using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchievementSync.Core.Enabler;

/// <summary>Decodes raw achievement bitmask values into named achievements using bit-map JSON files.</summary>
public static class BitmaskAchievementDecoder
{
    /// <summary>Decodes a bitmask value into achievement entries using the provided bit-map dictionary.</summary>
    /// <param name="appId">The Steam AppID (used to select the correct bit-map file).</param>
    /// <param name="bitmaskValue">The raw bitmask value from a local achievement file.</param>
    /// <param name="bitMaps">Dictionary mapping AppID string → bit-map JSON (position → achievement name).</param>
    /// <returns>A list of achievement key/earned pairs decoded from the bitmask.</returns>
    public static IReadOnlyList<(string Key, bool Earned)> DecodeBitmask(
        string appId,
        long bitmaskValue,
        IReadOnlyDictionary<string, JsonObject> bitMaps)
    {
        var result = new List<(string Key, bool Earned)>();

        if (string.IsNullOrWhiteSpace(appId) || bitMaps.Count == 0) return result;

        // Try exact AppID first, then fall back to any matching bit-map.
        JsonObject? bitMap = null;
        if (bitMaps.TryGetValue(appId, out var exact)) bitMap = exact;

        if (bitMap is null)
        {
            foreach (var kvp in bitMaps)
            {
                if (kvp.Key.StartsWith(appId + "_", StringComparison.OrdinalIgnoreCase) ||
                    appId.StartsWith(kvp.Key + "_", StringComparison.OrdinalIgnoreCase))
                {
                    bitMap = kvp.Value;
                    break;
                }
            }
        }

        if (bitMap is null) return result;

        var position = 0;
        var remaining = bitmaskValue;
        while (remaining != 0)
        {
            if ((remaining & 1) == 1)
            {
                var key = bitMap.TryGetPropertyValue(position.ToString(), out var nameNode)
                    ? nameNode?.ToString()
                    : null;
                result.Add((key ?? $"ACH_{position:D3}", true));
            }
            remaining >>= 1;
            position++;
        }

        return result;
    }

    /// <summary>Decodes a raw byte buffer (e.g. 8-byte achievement.dat MSB-first) into achievement entries.</summary>
    public static IReadOnlyList<(string Key, bool Earned)> DecodeBitmaskBuffer(
        string appId,
        byte[] buffer,
        IReadOnlyDictionary<string, JsonObject> bitMaps)
    {
        var result = new List<(string Key, bool Earned)>();
        if (string.IsNullOrWhiteSpace(appId) || bitMaps.Count == 0 || buffer.Length == 0) return result;

        JsonObject? bitMap = null;
        if (bitMaps.TryGetValue(appId, out var exact)) bitMap = exact;
        if (bitMap is null)
        {
            foreach (var kvp in bitMaps)
            {
                if (kvp.Key.StartsWith(appId + "_", StringComparison.OrdinalIgnoreCase) ||
                    appId.StartsWith(kvp.Key + "_", StringComparison.OrdinalIgnoreCase))
                {
                    bitMap = kvp.Value;
                    break;
                }
            }
        }
        if (bitMap is null) return result;

        for (var bit = 0; bit < buffer.Length * 8; bit++)
        {
            var bitKey = bit.ToString();
            if (bitMap.TryGetPropertyValue(bitKey, out var nameNode) && nameNode is not null)
            {
                var earned = (buffer[bit / 8] & (1 << (7 - (bit % 8)))) != 0;
                result.Add((nameNode.ToString(), earned));
            }
        }

        return result;
    }

    /// <summary>Loads all embedded bit-map JSON files into a dictionary keyed by AppID.</summary>
    public static IReadOnlyDictionary<string, JsonObject> LoadEmbeddedBitMaps()
    {
        var maps = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(BitmaskAchievementDecoder).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        foreach (var name in resourceNames)
        {
            if (!name.Contains("BitMap", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("bitmap", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("bit-map", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                if (JsonNode.Parse(json) is not JsonObject obj) continue;

                // Extract AppID from resource name like "...achievement-bit-maps.steam.39140.json"
                var parts = name.Split('.');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Equals("steam", StringComparison.OrdinalIgnoreCase) &&
                        i + 1 < parts.Length &&
                        long.TryParse(parts[i + 1], out _))
                    {
                        maps[parts[i + 1]] = obj;
                        break;
                    }
                }
            }
            catch { }
        }

        return maps;
    }
}

/// <summary>Represents a single achievement in the Roschach96 viewer format.</summary>
public sealed record ViewerAchievementEntry(
    [property: JsonPropertyName("earned")] bool Earned,
    [property: JsonPropertyName("earned_time")] long EarnedTime);
