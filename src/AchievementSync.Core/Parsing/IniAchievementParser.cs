using System.Globalization;
using AchievementSync.Core.Models;

namespace AchievementSync.Core.Parsing;

/// <summary>
/// Reads section-based achievement progress INI files without assuming that
/// an emulator writes a complete achievement catalog.
/// </summary>
public sealed class IniAchievementParser : IAchievementParser
{
    private static readonly HashSet<string> IniNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "achievements.ini",
        "achiev.ini"
    };

    public string FormatName => "INI achievement profile";

    public bool SupportsFile(string path) => IniNames.Contains(Path.GetFileName(path));

    public ParseResult Parse(string path, string appId)
    {
        try
        {
            var achievements = new List<AchievementRecord>();
            string? section = null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Flush()
            {
                if (string.IsNullOrWhiteSpace(section)) return;
                var state = Find(values, "earned", "achieved", "unlocked", "state");
                var unlocked = ParseState(state);
                if (!unlocked.HasValue) return;

                var time = Find(values, "earned_time", "unlocktime", "unlock_time", "timestamp");
                long? unixTime = long.TryParse(time, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

                achievements.Add(new AchievementRecord(section, null, null, unlocked, unixTime));
            }

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

                if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
                {
                    Flush();
                    section = line[1..^1].Trim();
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            Flush();
            if (achievements.Count == 0)
            {
                return ParseResult.Unsupported("No achievement unlock-state entries were found in this INI file.");
            }

            var fileTime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            return ParseResult.Parsed(new ParsedProfile(
                appId,
                FormatName,
                path,
                fileTime,
                achievements,
                HasCompleteCatalog: false));
        }
        catch (IOException)
        {
            return ParseResult.Unsupported("The file could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return ParseResult.Unsupported("Access to the file was denied.");
        }
    }

    private static string? Find(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value)) return value;
        }

        return null;
    }

    private static bool? ParseState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (value == "1") return true;
        if (value == "0") return false;
        return null;
    }
}
