using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AchievementSync.Core.Platforms;

/// <summary>Reads PS4 TRP trophy metadata using the public retail key or a local override.</summary>
public static class Ps4TrophyArchiveReader
{
    private const string PublicRetailTrophyKey = "21F41A6BAD8A1D3ECA7AD586C101B7A9";
    private const int TrpHeaderLength = 96;
    private const int MaximumArchiveBytes = 128 * 1024 * 1024;
    private const int MaximumEntryBytes = 4 * 1024 * 1024;
    private static readonly Regex MetadataEntryName = new(
        @"^TROP(?:_\d{2})?\.(?:XML|SFM|ESFM)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrophySetName = new(
        @"^trophy(?<slot>\d{2})(?:\.trp)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommunicationId = new(
        @"^NPWR\d{5}_\d{2}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record TrophyArchive(string? Title, string? NpCommunicationId, IReadOnlyList<PlatformAchievement> Achievements);

    public static TrophyArchive Read(string archivePath, string? bindingFile = null, string? keyFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var archiveBytes = ReadBoundedFile(archivePath, MaximumArchiveBytes);
        if (archiveBytes.Length < TrpHeaderLength || ReadUInt32(archiveBytes, 0) != 0xdca24d00)
            throw new InvalidDataException("Invalid PS4 TRP header.");

        var entryCount = ReadUInt32(archiveBytes, 16);
        var entryStride = ReadUInt32(archiveBytes, 20);
        if (entryCount > 4096 || entryStride < 64 || entryStride > 4096
            || (long)TrpHeaderLength + (long)entryCount * entryStride > archiveBytes.Length)
            throw new InvalidDataException("Invalid PS4 TRP directory.");

        var trophySlot = ResolveTrophySlot(archivePath);
        var bindings = string.IsNullOrWhiteSpace(bindingFile)
            ? new Dictionary<int, string>()
            : ReadNpBindings(bindingFile);
        var npcommid = bindings.GetValueOrDefault(trophySlot);
        var key = ResolveTrophyKey(keyFile);
        var documents = new List<(string Name, string Xml)>();

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = checked(TrpHeaderLength + (int)(index * entryStride));
            var name = Encoding.ASCII.GetString(archiveBytes, entryOffset, 32).Split('\0', 2)[0];
            if (!MetadataEntryName.IsMatch(name)) continue;

            var dataOffset = ReadUInt64(archiveBytes, entryOffset + 32);
            var dataSize = ReadUInt64(archiveBytes, entryOffset + 40);
            if (dataOffset > int.MaxValue || dataSize > MaximumEntryBytes
                || dataOffset + dataSize > (ulong)archiveBytes.Length)
                throw new InvalidDataException("Invalid PS4 TRP entry bounds.");

            var payload = archiveBytes.AsSpan((int)dataOffset, (int)dataSize).ToArray();
            if (name.EndsWith(".ESFM", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(npcommid))
                    throw new InvalidDataException("The PS4 trophy set has no matching NP communication ID in npbind.dat.");
                payload = DecryptEsfm(payload, npcommid, key);
            }

            var xml = Encoding.UTF8.GetString(payload);
            var lastTagEnd = xml.LastIndexOf('>');
            if (lastTagEnd >= 0) xml = xml[..(lastTagEnd + 1)];
            if (!xml.Contains("<trophyconf", StringComparison.OrdinalIgnoreCase)
                || !xml.Contains("</trophyconf", StringComparison.OrdinalIgnoreCase))
            {
                if (name.EndsWith(".ESFM", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unable to decrypt PS4 trophy metadata; check npbind.dat and trophy.key.");
                continue;
            }
            documents.Add((name, xml));
        }

        if (documents.Count == 0) throw new InvalidDataException("The PS4 TRP archive contains no readable trophy metadata.");
        IReadOnlyList<PlatformAchievement> achievements;
        try
        {
            achievements = PlayStationDataAdapter.ReadPs4TrophyXmlDocuments(documents);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new InvalidDataException("The PS4 TRP archive contains malformed trophy XML.", exception);
        }
        if (achievements.Count == 0) throw new InvalidDataException("The PS4 TRP archive contains no trophy entries.");
        var title = documents.Select(document => ReadTitle(document.Xml)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new TrophyArchive(title, npcommid, achievements);
    }

    public static IReadOnlyDictionary<int, string> ReadNpBindings(string bindingFile)
    {
        var bytes = ReadBoundedFile(bindingFile, 4 * 1024 * 1024);
        if (bytes.Length < 128 || ReadUInt32(bytes, 0) != 0xd294a018)
            throw new InvalidDataException("Invalid PS4 npbind.dat header.");

        var count = ReadUInt64(bytes, 24);
        if (count > 100) throw new InvalidDataException("Invalid PS4 npbind.dat entry count.");
        var position = 128;
        var bindings = new Dictionary<int, string>();
        for (var index = 0; (ulong)index < count; index++)
        {
            string? id = null;
            string? label = null;
            for (var field = 0; field < 4; field++)
            {
                EnsureAvailable(bytes, position, 4);
                var type = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position, 2));
                var size = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 2, 2));
                position += 4;
                EnsureAvailable(bytes, position, size);
                var value = Encoding.ASCII.GetString(bytes, position, size).Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
                if (type == 0x10) id = value;
                if (type == 0x11) label = value;
                position += size;
            }

            if (id is null || !CommunicationId.IsMatch(id) || !int.TryParse(label, out var slot) || slot is < 0 or > 99)
                throw new InvalidDataException("Invalid PS4 trophy identity in npbind.dat.");
            if (!bindings.TryAdd(slot, id.ToUpperInvariant()))
                throw new InvalidDataException("Duplicate PS4 trophy slot in npbind.dat.");
            EnsureAvailable(bytes, position, 152);
            position += 152;
        }
        return bindings;
    }

    private static byte[] DecryptEsfm(byte[] encrypted, string npcommid, byte[] key)
    {
        if (encrypted.Length < 32 || encrypted.Length % 16 != 0)
            throw new InvalidDataException("Invalid encrypted PS4 trophy metadata length.");
        if (!CommunicationId.IsMatch(npcommid))
            throw new InvalidDataException("Invalid NP communication ID for PS4 trophy metadata.");

        var identity = new byte[16];
        Encoding.ASCII.GetBytes(npcommid, identity);
        using var keyGenerator = Aes.Create();
        keyGenerator.Key = key;
        keyGenerator.IV = new byte[16];
        keyGenerator.Mode = CipherMode.CBC;
        keyGenerator.Padding = PaddingMode.None;
        using var encryptor = keyGenerator.CreateEncryptor();
        var derivedKey = encryptor.TransformFinalBlock(identity, 0, identity.Length);

        using var aes = Aes.Create();
        aes.Key = derivedKey;
        aes.IV = encrypted.AsSpan(0, 16).ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 16, encrypted.Length - 16);
    }

    private static byte[] ResolveTrophyKey(string? keyFile)
    {
        var keyText = string.IsNullOrWhiteSpace(keyFile) || !File.Exists(keyFile)
            ? PublicRetailTrophyKey
            : Encoding.UTF8.GetString(ReadBoundedFile(keyFile, 256)).Trim();
        if (keyText.Length != 32 || !keyText.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid trophy.key; expected 32 hexadecimal characters.");
        return Convert.FromHexString(keyText);
    }

    private static int ResolveTrophySlot(string archivePath)
    {
        var match = TrophySetName.Match(Path.GetFileName(archivePath));
        return match.Success && int.TryParse(match.Groups["slot"].Value, out var slot) ? slot : 0;
    }

    private static byte[] ReadBoundedFile(string path, int maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0
            || info.Length is <= 0 || info.Length > maxBytes)
            throw new InvalidDataException("Invalid or oversized PS4 trophy data file.");
        return File.ReadAllBytes(path);
    }

    private static string? ReadTitle(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            return document.Descendants().FirstOrDefault(element => element.Name.LocalName == "title-name")?.Value.Trim();
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        EnsureAvailable(bytes, offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        EnsureAvailable(bytes, offset, 8);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset, 8));
    }

    private static void EnsureAvailable(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > bytes.Length - count)
            throw new InvalidDataException("Truncated PS4 trophy data.");
    }
}
