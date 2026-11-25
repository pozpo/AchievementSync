using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AchievementSync.Core.Enabler;

public sealed record EaCatalogCacheEntry(
    string ContentId,
    string? OfrId,
    string? DisplayName,
    string? AchievementsSetId,
    string? SteamAppId);

public sealed record GogCompatRow(string Release, IReadOnlyList<string> SdkVersions);

/// <summary>
/// Native C# online metadata & release helpers for Epic (egdata.app / epicgames.dev),
/// GOG (UniverseLAN compatibility table & releases), and EA/Origin (ITAD + EA GraphQL).
/// </summary>
public static class AdapterOnlineMetadataService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex Hex32Regex = new(@"^[0-9a-fA-F]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] EpicLocales =
    [
        "en", "fr", "de", "it", "ja", "ko", "pl", "pt-BR", "ru", "es-ES", "es-MX", "zh-CN", "zh-TW"
    ];

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AchievementSync/1.2");
        return client;
    }

    // -----------------------------------------------------------------------
    // Epic Games (egdata.app namespace lookup + epicgames.dev achievements)
    // -----------------------------------------------------------------------

    public static string EpicOutputRoot =>
        Path.Combine(EmbeddedEpicRuntime.RootPath, "epic_output");

    public static string GetEpicNamespaceOutputDir(string epicNamespace) =>
        Path.Combine(EpicOutputRoot, SanitizePathSegment(epicNamespace));

    public static string? FindLocalEpicNamespace(string gameDirectory)
    {
        var candidates = new List<string> { gameDirectory };
        if (Directory.GetParent(gameDirectory)?.FullName is { } parent)
            candidates.Add(parent);

        foreach (var root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var egStore = Path.Combine(root, ".egstore");
            if (!Directory.Exists(egStore)) continue;
            try
            {
                foreach (var mancpn in Directory.EnumerateFiles(egStore, "*.mancpn", SearchOption.TopDirectoryOnly))
                {
                    if (JsonNode.Parse(File.ReadAllText(mancpn)) is JsonObject obj &&
                        obj.TryGetPropertyValue("CatalogNamespace", out var nsNode) &&
                        nsNode?.ToString() is { Length: > 0 } ns)
                    {
                        return ns.Trim();
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public static async Task<(string? Namespace, string? Title)> LookupEpicNamespaceAsync(
        string gameName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return (null, null);

        try
        {
            var payload = new JsonObject
            {
                ["title"] = gameName.Trim(),
                ["offerType"] = "BASE_GAME",
                ["price"] = new JsonObject { ["min"] = 100, ["max"] = 100000 },
                ["page"] = 1,
                ["limit"] = 28
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.egdata.app/search/v2/search?country=US")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, null);

            var raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(raw) is not JsonObject root ||
                root["offers"] is not JsonArray offers)
                return (null, null);

            foreach (var item in offers)
            {
                if (item is not JsonObject offer) continue;
                if (offer["prePurchase"]?.GetValue<bool>() == true) continue;
                if (offer["isCodeRedemptionOnly"]?.GetValue<bool>() == true) continue;
                var discountPrice = offer["price"]?["price"]?["discountPrice"]?.GetValue<decimal?>() ?? 0m;
                if (discountPrice <= 0m) continue;

                var ns = offer["namespace"]?.ToString()?.Trim();
                var title = offer["title"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(ns))
                    return (ns, title);
            }
        }
        catch { }

        return (null, null);
    }

    public static async Task<EnablerGeneratorRefreshResult> RefreshEpicAchievementsAsync(
        string epicNamespace, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(epicNamespace))
            return new EnablerGeneratorRefreshResult(false, false, "An Epic namespace / sandboxId is required to fetch Epic achievements.");

        var ns = epicNamespace.Trim();
        var localeMaps = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.OrdinalIgnoreCase);
        var localeLists = new Dictionary<string, List<JsonObject>>(StringComparer.OrdinalIgnoreCase);

        foreach (var locale in EpicLocales)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"https://api.epicgames.dev/epic/achievements/v1/public/achievements/product/{Uri.EscapeDataString(ns)}/locale/{Uri.EscapeDataString(locale)}?includeAchievements=true";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (JsonNode.Parse(body) is not JsonObject payload ||
                    payload["achievements"] is not JsonArray achArr)
                    continue;

                var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
                var list = new List<JsonObject>();
                foreach (var entry in achArr)
                {
                    if (entry?["achievement"] is not JsonObject ach) continue;
                    var name = ach["name"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    map[name] = ach;
                    list.Add(ach);
                }

                if (list.Count > 0)
                {
                    localeMaps[locale] = map;
                    localeLists[locale] = list;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (!localeLists.TryGetValue("en", out var baseList) || baseList.Count == 0)
        {
            baseList = localeLists.Values.FirstOrDefault(l => l.Count > 0);
        }

        if (baseList is null || baseList.Count == 0)
        {
            return new EnablerGeneratorRefreshResult(true, false,
                $"No public Epic achievements were returned for namespace '{ns}'.");
        }

        EmbeddedEpicRuntime.EnsureReady();
        var outDir = GetEpicNamespaceOutputDir(ns);
        var imagesDir = Path.Combine(outDir, "achievements_images");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(imagesDir);

        static JsonObject BuildTransMap(string defaultVal, string field, string achId, Dictionary<string, Dictionary<string, JsonObject>> maps)
        {
            string GetLoc(string loc) =>
                maps.TryGetValue(loc, out var m) && m.TryGetValue(achId, out var o) ? o[field]?.ToString() ?? string.Empty : string.Empty;

            return new JsonObject
            {
                ["de"] = GetLoc("de"),
                ["default"] = defaultVal,
                ["en"] = GetLoc("en"),
                ["es-ES"] = GetLoc("es-ES"),
                ["fr"] = GetLoc("fr"),
                ["it"] = GetLoc("it"),
                ["ja"] = GetLoc("ja"),
                ["pt-BR"] = GetLoc("pt-BR"),
                ["ru"] = GetLoc("ru"),
                ["zh"] = GetLoc("zh-CN")
            };
        }

        var outputArray = new JsonArray();
        var imageTasks = new List<(string Url, string BaseName)>();

        foreach (var achBase in baseList)
        {
            var id = achBase["name"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(id)) continue;

            var statsThresholds = new JsonArray();
            if (achBase["statThresholds"] is JsonObject stObj)
            {
                foreach (var prop in stObj)
                {
                    statsThresholds.Add(new JsonObject
                    {
                        ["Name"] = prop.Key,
                        ["Threshold"] = prop.Value?.DeepClone()
                    });
                }
            }

            var entry = new JsonObject
            {
                ["AchievementId"] = id,
                ["UnlockedDisplayName"] = BuildTransMap(achBase["unlockedDisplayName"]?.ToString() ?? string.Empty, "unlockedDisplayName", id, localeMaps),
                ["UnlockedDescription"] = BuildTransMap(achBase["unlockedDescription"]?.ToString() ?? string.Empty, "unlockedDescription", id, localeMaps),
                ["LockedDisplayName"] = BuildTransMap(achBase["lockedDisplayName"]?.ToString() ?? string.Empty, "lockedDisplayName", id, localeMaps),
                ["LockedDescription"] = BuildTransMap(achBase["lockedDescription"]?.ToString() ?? string.Empty, "lockedDescription", id, localeMaps),
                ["FlavorText"] = new JsonObject { ["default"] = achBase["flavorText"]?.ToString() ?? string.Empty },
                ["UnlockedIconUrl"] = id,
                ["LockedIconUrl"] = id + "_locked",
                ["IsHidden"] = achBase["hidden"]?.GetValue<bool>() ?? false,
                ["StatsThresholds"] = statsThresholds
            };
            outputArray.Add(entry);

            if (achBase["unlockedIconLink"]?.ToString() is { Length: > 0 } uUrl)
                imageTasks.Add((uUrl, id));
            if (achBase["lockedIconLink"]?.ToString() is { Length: > 0 } lUrl)
                imageTasks.Add((lUrl, id + "_locked"));
        }

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "achievements_db.json"),
            outputArray.ToJsonString(JsonOptions),
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);

        // Download missing icon images (up to 4 concurrent)
        using var sem = new SemaphoreSlim(4);
        var downloads = imageTasks.Select(async t =>
        {
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var ext = ".png";
                try
                {
                    var uriExt = Path.GetExtension(new Uri(t.Url).AbsolutePath);
                    if (!string.IsNullOrWhiteSpace(uriExt)) ext = uriExt;
                }
                catch { }
                var dest = Path.Combine(imagesDir, SanitizePathSegment(t.BaseName) + ext);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) return;
                var bytes = await Http.GetByteArrayAsync(t.Url, cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 0)
                    await File.WriteAllBytesAsync(dest, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally { sem.Release(); }
        });
        await Task.WhenAll(downloads).ConfigureAwait(false);

        return new EnablerGeneratorRefreshResult(true, true,
            $"Fetched {outputArray.Count} Epic achievements and icons for namespace '{ns}'.");
    }

    public static bool LooksLikeEpicNamespace(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Hex32Regex.IsMatch(value.Trim());

    // -----------------------------------------------------------------------
    // GOG UniverseLAN (compat table + matching release resolution)
    // -----------------------------------------------------------------------

    public static string UniverseLanReleasesRoot =>
        Path.Combine(EmbeddedUniverseLanRuntime.RootPath, "releases");

    public static string UniverseLanCompatJsonPath =>
        Path.Combine(EmbeddedUniverseLanRuntime.RootPath, "galaxy_sdk_compat.json");

    public static async Task<EnablerGeneratorRefreshResult> RefreshGogUniverseLanAsync(
        string? requiredSdkVersion, bool is64Bit, CancellationToken cancellationToken = default)
    {
        EmbeddedUniverseLanRuntime.EnsureReady();
        var table = await FetchAndCacheCompatTableAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(requiredSdkVersion))
        {
            return new EnablerGeneratorRefreshResult(true, table.Count > 0,
                table.Count > 0
                    ? $"Updated Galaxy SDK compatibility table ({table.Count} rows)."
                    : "Could not fetch Galaxy SDK compatibility table; using cached/bundled UniverseLAN files.");
        }

        // Check if already matched on disk (cached releases or bundled 1.99.0.0)
        var existing = FindMatchingUniverseLanFolder(requiredSdkVersion, is64Bit, out _);
        if (existing is not null)
        {
            return new EnablerGeneratorRefreshResult(true, true,
                $"Compatible UniverseLAN build already available for Galaxy SDK {requiredSdkVersion}.");
        }

        // Try downloading the compatible release from GitHub
        var targetReleaseTag = FindReleaseForSdkVersion(table, requiredSdkVersion);
        try
        {
            var releasesJson = await Http.GetStringAsync(
                "https://api.github.com/repos/grasmanek94/UniverseLAN/releases?per_page=20",
                cancellationToken).ConfigureAwait(false);

            if (JsonNode.Parse(releasesJson) is JsonArray releasesArr)
            {
                JsonObject? chosenRelease = null;
                if (!string.IsNullOrWhiteSpace(targetReleaseTag))
                {
                    chosenRelease = releasesArr.OfType<JsonObject>().FirstOrDefault(r =>
                    {
                        var tag = r["tag_name"]?.ToString() ?? r["name"]?.ToString() ?? string.Empty;
                        return tag.Contains(targetReleaseTag, StringComparison.OrdinalIgnoreCase);
                    });
                }
                chosenRelease ??= releasesArr.OfType<JsonObject>().FirstOrDefault();

                if (chosenRelease?["assets"] is JsonArray assets)
                {
                    var tagName = chosenRelease["tag_name"]?.ToString() ?? chosenRelease["name"]?.ToString() ?? "release";
                    var tagDir = Path.Combine(UniverseLanReleasesRoot, SanitizePathSegment(tagName));
                    Directory.CreateDirectory(tagDir);

                    foreach (var asset in assets.OfType<JsonObject>())
                    {
                        var assetName = asset["name"]?.ToString();
                        var dlUrl = asset["browser_download_url"]?.ToString();
                        if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(dlUrl) ||
                            !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var extractDir = Path.Combine(tagDir, Path.GetFileNameWithoutExtension(assetName));
                        if (Directory.Exists(extractDir) && Directory.EnumerateFiles(extractDir, "Galaxy*.dll", SearchOption.AllDirectories).Any())
                            continue;

                        var zipBytes = await Http.GetByteArrayAsync(dlUrl, cancellationToken).ConfigureAwait(false);
                        var zipTmp = Path.Combine(tagDir, assetName + ".tmp");
                        await File.WriteAllBytesAsync(zipTmp, zipBytes, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
                            ZipFile.ExtractToDirectory(zipTmp, extractDir);
                        }
                        finally
                        {
                            if (File.Exists(zipTmp)) File.Delete(zipTmp);
                        }
                    }

                    var matchedAfter = FindMatchingUniverseLanFolder(requiredSdkVersion, is64Bit, out _);
                    if (matchedAfter is not null)
                    {
                        return new EnablerGeneratorRefreshResult(true, true,
                            $"Downloaded matching UniverseLAN release ({tagName}) for Galaxy SDK {requiredSdkVersion}.");
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return new EnablerGeneratorRefreshResult(true, false,
            $"No matching UniverseLAN release was found for Galaxy SDK {requiredSdkVersion}.");
    }

    private static async Task<List<GogCompatRow>> FetchAndCacheCompatTableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var readme = await Http.GetStringAsync(
                "https://raw.githubusercontent.com/grasmanek94/UniverseLAN/master/README.MD",
                cancellationToken).ConfigureAwait(false);

            var rows = new List<GogCompatRow>();
            bool inTable = false;
            foreach (var line in readme.Split(['\r', '\n']))
            {
                if (!inTable)
                {
                    if (Regex.IsMatch(line, @"^\s*\|\s*UniverseLAN Release\s*\|", RegexOptions.IgnoreCase))
                        inTable = true;
                    continue;
                }
                if (Regex.IsMatch(line, @"^\s*\|\s*:-+:?\s*\|")) continue;
                if (!line.TrimStart().StartsWith('|')) break;

                var cells = line.Trim().Trim('|').Split('|');
                if (cells.Length < 2) continue;
                var rel = cells[0].Trim();
                var sdks = Regex.Split(cells[1], @"<br\s*/?>", RegexOptions.IgnoreCase)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                if (!string.IsNullOrWhiteSpace(rel) && sdks.Length > 0)
                    rows.Add(new GogCompatRow(rel, sdks));
            }

            if (rows.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UniverseLanCompatJsonPath)!);
                var json = JsonSerializer.Serialize(rows, JsonOptions);
                await File.WriteAllTextAsync(UniverseLanCompatJsonPath, json, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
                return rows;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return ReadCachedCompatTable();
    }

    public static List<GogCompatRow> ReadCachedCompatTable()
    {
        var paths = new List<string> { UniverseLanCompatJsonPath };
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        paths.Add(Path.Combine(systemDrive + Path.DirectorySeparatorChar, "steamcmd", "_GOG", "UniverseLAN", "galaxy_sdk_compat.json"));

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<List<GogCompatRow>>(File.ReadAllText(path));
                if (parsed is { Count: > 0 }) return parsed;
            }
            catch { }
        }
        return [];
    }

    public static string? FindMatchingUniverseLanFolder(string? sdkVersion, bool is64Bit, out string? matchMethod)
    {
        matchMethod = null;
        var dllName = is64Bit ? "Galaxy64.dll" : "Galaxy.dll";

        var searchRoots = new List<string> { UniverseLanReleasesRoot, EmbeddedUniverseLanRuntime.RootPath };
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var aeCache = Path.Combine(systemDrive + Path.DirectorySeparatorChar, "steamcmd", "_GOG", "UniverseLAN");
        if (Directory.Exists(aeCache)) searchRoots.Add(aeCache);

        var candidates = new List<string>();
        foreach (var root in searchRoots.Where(Directory.Exists))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(root, dllName, SearchOption.AllDirectories));
            }
            catch { }
        }

        if (candidates.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(sdkVersion))
        {
            matchMethod = "default bundled runtime";
            return EmbeddedUniverseLanRuntime.RootPath;
        }

        var compat = ReadCachedCompatTable();
        var relFromTable = FindReleaseForSdkVersion(compat, sdkVersion);

        // 1a. Compatibility table match
        if (!string.IsNullOrWhiteSpace(relFromTable))
        {
            foreach (var cand in candidates)
            {
                var folder = Path.GetDirectoryName(cand)!;
                var folderVer = ExtractUniverseLanBuildVersion(Path.GetFileName(folder));
                if (!string.IsNullOrWhiteSpace(folderVer) && VersionsMatch(folderVer, relFromTable))
                {
                    matchMethod = $"Galaxy SDK compatibility table ({sdkVersion} -> UniverseLAN {relFromTable})";
                    return folder;
                }
            }
        }

        // 1b. Direct DLL FileVersion / ProductVersion comparison
        foreach (var cand in candidates)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(cand);
                if (VersionsMatch(vi.FileVersion, sdkVersion) || VersionsMatch(vi.ProductVersion, sdkVersion))
                {
                    matchMethod = $"direct DLL version match ({sdkVersion})";
                    return Path.GetDirectoryName(cand)!;
                }
            }
            catch { }
        }

        // 1c. Build folder name version comparison
        foreach (var cand in candidates)
        {
            var folder = Path.GetDirectoryName(cand)!;
            var folderVer = ExtractUniverseLanBuildVersion(Path.GetFileName(folder));
            if (!string.IsNullOrWhiteSpace(folderVer) && VersionsMatch(folderVer, sdkVersion))
            {
                matchMethod = $"build folder version match ({folderVer})";
                return folder;
            }
        }

        return null;
    }

    private static string? FindReleaseForSdkVersion(IReadOnlyList<GogCompatRow> table, string sdkVersion)
    {
        foreach (var row in table)
        {
            if (string.IsNullOrWhiteSpace(row.Release) || row.Release == "-") continue;
            foreach (var v in row.SdkVersions)
            {
                var clean = v.TrimEnd('?').Trim();
                if (VersionsMatch(clean, sdkVersion)) return row.Release;
            }
        }
        return null;
    }

    private static string? ExtractUniverseLanBuildVersion(string folderName)
    {
        var m = Regex.Match(folderName, @"^UniverseLAN-(?<ver>[\d.]+)-Build-", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["ver"].Value : null;
    }

    public static int[]? ParseVersionParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var matches = Regex.Matches(version, @"\d+");
        if (matches.Count == 0) return null;
        var list = matches.Select(m => int.TryParse(m.Value, out var n) ? n : 0).ToList();
        while (list.Count < 4) list.Add(0);
        return list.ToArray();
    }

    public static bool VersionsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var pa = ParseVersionParts(a);
        var pb = ParseVersionParts(b);
        if (pa is null || pb is null) return false;
        var len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return false;
        }
        return true;
    }

    // -----------------------------------------------------------------------
    // EA / Origin (isthereanydeal.com + EA GraphQL legacyOffers)
    // -----------------------------------------------------------------------

    public static string EaCacheDirectory =>
        Path.Combine(EmbeddedEaOriginRuntime.RootPath, "cache");

    public static EaCatalogCacheEntry? ReadCachedEaCatalog(string? contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId)) return null;
        var path = Path.Combine(EaCacheDirectory, SanitizePathSegment(contentId) + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<EaCatalogCacheEntry>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static async Task<EaCatalogCacheEntry?> LookupEaCatalogAsync(
        string? gameTitle, string? contentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameTitle))
            return ReadCachedEaCatalog(contentId);

        var slug = SlugifyItadName(gameTitle);
        string? productsHtml = await TryFetchItadProductsHtmlAsync(slug, cancellationToken).ConfigureAwait(false);
        if (productsHtml is null || !productsHtml.Contains("Origin.OFR", StringComparison.OrdinalIgnoreCase))
        {
            var searchedSlug = await SearchItadSlugAsync(gameTitle, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(searchedSlug))
            {
                slug = searchedSlug;
                productsHtml = await TryFetchItadProductsHtmlAsync(slug, cancellationToken).ConfigureAwait(false);
            }
        }

        string? ofrId = null;
        string? steamAppId = null;

        if (!string.IsNullOrWhiteSpace(productsHtml))
        {
            var eaLinks = ExtractStoreProducts(productsHtml, "EA Store");
            var ofrMatches = new List<(string OfrId, string Text)>();
            foreach (var txt in eaLinks)
            {
                var m = Regex.Match(txt, @"\b(Origin\.OFR\.[A-Za-z0-9._-]+)\b");
                if (m.Success) ofrMatches.Add((m.Groups[1].Value, txt));
            }
            if (ofrMatches.Count > 0)
            {
                var usPool = ofrMatches.Where(x => x.Text.Contains("US", StringComparison.OrdinalIgnoreCase)).ToList();
                var pool = usPool.Count > 0 ? usPool : ofrMatches;
                ofrId = pool.OrderByDescending(x => x.Text.Split(',').Length).First().OfrId;
            }

            var steamLinks = ExtractStoreProducts(productsHtml, "Steam");
            foreach (var txt in steamLinks)
            {
                var m = Regex.Match(txt, @"\bapp/(\d+)\b");
                if (m.Success)
                {
                    steamAppId = m.Groups[1].Value;
                    break;
                }
            }
        }

        string? displayName = null;
        string? achievementsId = null;

        if (!string.IsNullOrWhiteSpace(ofrId))
        {
            var (gName, gAchSet, _) = await QueryEaOfferGraphQlAsync(ofrId, contentId, cancellationToken).ConfigureAwait(false);
            displayName = gName;
            achievementsId = gAchSet;
        }

        if (ofrId is null && achievementsId is null && steamAppId is null)
            return ReadCachedEaCatalog(contentId);

        var entry = new EaCatalogCacheEntry(contentId ?? string.Empty, ofrId, displayName, achievementsId, steamAppId);
        if (!string.IsNullOrWhiteSpace(contentId))
        {
            try
            {
                Directory.CreateDirectory(EaCacheDirectory);
                var cachePath = Path.Combine(EaCacheDirectory, SanitizePathSegment(contentId) + ".json");
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(entry, JsonOptions), Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }
        return entry;
    }

    private static async Task<string?> TryFetchItadProductsHtmlAsync(string slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        try
        {
            var url = $"https://isthereanydeal.com/game/{Uri.EscapeDataString(slug)}/products/";
            using var resp = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<string?> SearchItadSlugAsync(string gameTitle, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://isthereanydeal.com/search/?q={Uri.EscapeDataString(gameTitle.Trim())}";
            var html = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var m = Regex.Match(html, @"href=""/game/([^/""]+)/""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static List<string> ExtractStoreProducts(string html, string storeName)
    {
        var results = new List<string>();
        var sections = Regex.Matches(html, @"<section\b[^>]*>(.*?)</section>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match sec in sections)
        {
            var body = sec.Groups[1].Value;
            var headingMatch = Regex.Match(body, @"<h[23][^>]*>(.*?)</h[23]>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!headingMatch.Success) continue;

            var headingPlain = Regex.Replace(headingMatch.Groups[1].Value, "<.*?>", " ").Trim();
            if (!headingPlain.StartsWith(storeName, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (Match link in Regex.Matches(body, @"<a\b[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var plain = WebUtility.HtmlDecode(Regex.Replace(link.Groups[1].Value, "<.*?>", " "));
                plain = Regex.Replace(plain, @"\s+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(plain)) results.Add(plain);
            }
        }
        return results;
    }

    private static async Task<(string? DisplayName, string? AchievementsSetId, string? ContentId)> QueryEaOfferGraphQlAsync(
        string ofrId, string? expectedContentId, CancellationToken cancellationToken)
    {
        try
        {
            const string query = """
                query getLegacyCatalogDefs($offerIds: [String!]!, $locale: Locale) {
                  legacyOffers(offerIds: $offerIds, locale: $locale) {
                    offerId: id
                    contentId
                    achievementSetOverride
                    displayName
                  }
                }
                """;
            var payload = new JsonObject
            {
                ["query"] = query,
                ["variables"] = new JsonObject
                {
                    ["locale"] = "en",
                    ["offerIds"] = new JsonArray { ofrId }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://service-aggregation-layer.juno.ea.com/graphql")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd("EAApp/PC/13.128.0.5641");
            req.Headers.Add("x-client-id", "EAX-JUNO-CLIENT");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, null, null);

            var raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(raw)?["data"]?["legacyOffers"] is not JsonArray offers)
                return (null, null, null);

            JsonObject? match = null;
            if (!string.IsNullOrWhiteSpace(expectedContentId))
            {
                match = offers.OfType<JsonObject>().FirstOrDefault(o =>
                    string.Equals(o["contentId"]?.ToString(), expectedContentId, StringComparison.OrdinalIgnoreCase));
            }
            match ??= offers.OfType<JsonObject>().FirstOrDefault();

            return match is null
                ? (null, null, null)
                : (match["displayName"]?.ToString(), match["achievementSetOverride"]?.ToString(), match["contentId"]?.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, null, null); }
    }

    private static string SlugifyItadName(string name)
    {
        var s = name.ToLowerInvariant().Replace("™", "").Replace("®", "");
        s = Regex.Replace(s, @"[^a-z0-9]+", "-");
        return s.Trim('-');
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
