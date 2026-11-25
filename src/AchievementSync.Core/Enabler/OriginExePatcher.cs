using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AchievementSync.Core.Enabler;

public sealed record EaExecutableInspection(
    bool IsValidPe,
    bool Is64Bit,
    bool ImportsAnadius,
    bool HasOoaSection,
    bool HasBak,
    bool CanPatch,
    string StatusMessage);

/// <summary>
/// Pure C# PE analyzer, Origin DRM (.ooa) unwrapper, and anadius DLL import-table patcher.
/// Mirrors origin_unwrapper.py (--add-dll) and dll_adder.py without requiring Python.
/// </summary>
public static class OriginExePatcher
{
    private const uint PeSignature = 0x00004550;
    private const ushort DosMagic = 0x5A4D;
    private const ushort MachineI386 = 0x014C;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;

    private const int DirExport = 0;
    private const int DirImport = 1;
    private const int DirSecurity = 4;
    private const int DirReloc = 5;
    private const int DirTls = 9;
    private const int DirBoundImport = 11;
    private const int DirIat = 12;

    private static readonly byte[] OriginLicenseAesKey = Convert.FromBase64String("QTJyLdCC77DcZFfFdmjKCQ==");
    private static readonly Version LastVersionWithParFiles = new(4, 9, 0, 202);
    private static readonly Version FirstVersionWithShorterGapAfterGarbage = new(5, 1, 3, 52);
    private static readonly Version FirstVersionWith0x100Bytes = new(5, 2, 15, 89);
    private static readonly Version FirstVersionWithoutRestoringTlsBytes = new(5, 2, 15, 103);
    private static readonly Version FirstVersionWithoutImageBaseFlag = new(5, 2, 20, 136);

    private sealed class SectionHeader
    {
        public int HeaderOffset { get; init; }
        public byte[] RawName { get; init; } = [];
        public string Name { get; init; } = string.Empty;
        public uint VirtualSize { get; set; }
        public uint VirtualAddress { get; set; }
        public uint SizeOfRawData { get; set; }
        public uint PointerToRawData { get; set; }
        public uint Characteristics { get; set; }
    }

    public static EaExecutableInspection InspectExecutable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return new EaExecutableInspection(false, true, false, false, false, false, "Executable not found.");

        var bakPath = exePath + ".BAK";
        var hasBak = File.Exists(bakPath);
        var inspectPath = hasBak ? bakPath : exePath;

        try
        {
            var bytes = File.ReadAllBytes(exePath);
            if (!TryParsePe(bytes, out var pe))
                return new EaExecutableInspection(false, true, false, false, hasBak, false, "Not a standard PE executable (patching skipped).");

            var importsAnadius = HasAnadiusImport(bytes, pe);
            var lastSection = pe.Sections.LastOrDefault();
            var hasOoaCurrent = lastSection is not null && lastSection.Name.StartsWith(".ooa", StringComparison.Ordinal);

            if (importsAnadius)
            {
                return new EaExecutableInspection(true, pe.Is64Bit, true, hasOoaCurrent, hasBak, false,
                    hasBak
                        ? "Executable already imports anadius DLL (original preserved in .BAK)."
                        : "Executable already imports anadius DLL.");
            }

            if (hasBak && File.Exists(bakPath))
            {
                var bakBytes = File.ReadAllBytes(bakPath);
                if (TryParsePe(bakBytes, out var bakPe))
                {
                    var bakHasOoa = bakPe.Sections.LastOrDefault()?.Name.StartsWith(".ooa", StringComparison.Ordinal) == true;
                    return new EaExecutableInspection(true, bakPe.Is64Bit, false, bakHasOoa, true, true,
                        bakHasOoa
                            ? "Original .BAK has .ooa Origin wrapper; ready to unwrap and add anadius DLL import."
                            : "Original .BAK ready to add anadius DLL import.");
                }
            }

            if (hasOoaCurrent)
            {
                return new EaExecutableInspection(true, pe.Is64Bit, false, true, hasBak, true,
                    "Origin DRM (.ooa) detected; will unwrap to .BAK backup and add anadius DLL import.");
            }

            var canAppend = CanAppendImportSection(bytes, pe, out var reason);
            return new EaExecutableInspection(true, pe.Is64Bit, false, false, hasBak, canAppend,
                canAppend
                    ? "PE executable can be patched with .anadius DLL import (original backed up to .BAK)."
                    : $"Executable does not import anadius DLL ({reason}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EaExecutableInspection(false, true, false, false, hasBak, false, $"Could not inspect executable: {ex.Message}");
        }
    }

    public static byte[] PatchExecutable(string sourceExePath, string gameRootDirectory)
    {
        var bytes = File.ReadAllBytes(sourceExePath);
        if (!TryParsePe(bytes, out var pe))
            throw new InvalidOperationException("Target file is not a valid PE executable.");

        if (HasAnadiusImport(bytes, pe))
            return bytes;

        var lastSection = pe.Sections.LastOrDefault()
            ?? throw new InvalidOperationException("PE executable has no sections.");

        if (lastSection.Name.StartsWith(".ooa", StringComparison.Ordinal))
        {
            return UnwrapOoaAndAddAnadiusImport(bytes, pe, sourceExePath, gameRootDirectory);
        }

        return AppendAnadiusImportSection(bytes, pe);
    }

    private sealed class PeInfo
    {
        public int PeOffset { get; init; }
        public bool Is64Bit { get; init; }
        public int NumberOfSections { get; init; }
        public int SizeOfOptionalHeader { get; init; }
        public int OptionalHeaderOffset { get; init; }
        public int DataDirectoryOffset { get; init; }
        public int NumberOfDataDirectories { get; init; }
        public int SectionTableOffset { get; init; }
        public uint SectionAlignment { get; init; }
        public uint FileAlignment { get; init; }
        public uint SizeOfImage { get; init; }
        public uint SizeOfHeaders { get; init; }
        public uint AddressOfEntryPoint { get; init; }
        public ulong ImageBase { get; init; }
        public List<SectionHeader> Sections { get; } = [];
    }

    private static bool TryParsePe(byte[] data, out PeInfo pe)
    {
        pe = null!;
        if (data.Length < 0x40 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2)) != DosMagic)
            return false;

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C, 4));
        if (peOffset < 0 || peOffset + 24 > data.Length)
            return false;

        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(peOffset, 4)) != PeSignature)
            return false;

        var fileHeaderOffset = peOffset + 4;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fileHeaderOffset, 2));
        if (machine is not (MachineI386 or MachineAmd64 or MachineArm64))
            return false;

        var numSections = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fileHeaderOffset + 2, 2));
        var sizeOfOptional = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fileHeaderOffset + 16, 2));
        var optOffset = fileHeaderOffset + 20;
        if (optOffset + sizeOfOptional > data.Length || sizeOfOptional < 96)
            return false;

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(optOffset, 2));
        var is64 = magic == 0x20B;
        if (magic is not (0x10B or 0x20B))
            return false;

        var numRvaOffset = optOffset + (is64 ? 108 : 92);
        var dataDirOffset = optOffset + (is64 ? 112 : 96);
        if (numRvaOffset + 4 > data.Length)
            return false;

        var numDirs = (int)Math.Min(16, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(numRvaOffset, 4)));
        var secTableOffset = optOffset + sizeOfOptional;
        if (secTableOffset + numSections * 40 > data.Length)
            return false;

        pe = new PeInfo
        {
            PeOffset = peOffset,
            Is64Bit = is64,
            NumberOfSections = numSections,
            SizeOfOptionalHeader = sizeOfOptional,
            OptionalHeaderOffset = optOffset,
            DataDirectoryOffset = dataDirOffset,
            NumberOfDataDirectories = numDirs,
            SectionTableOffset = secTableOffset,
            AddressOfEntryPoint = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optOffset + 16, 4)),
            ImageBase = is64
                ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(optOffset + 24, 8))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optOffset + 28, 4)),
            SectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optOffset + 32, 4)),
            FileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optOffset + 36, 4)),
            SizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optOffset + 56, 4)),
            SizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optOffset + 60, 4))
        };

        for (int i = 0; i < numSections; i++)
        {
            var off = secTableOffset + i * 40;
            var rawName = data.AsSpan(off, 8).ToArray();
            var name = Encoding.ASCII.GetString(rawName).TrimEnd('\0');
            pe.Sections.Add(new SectionHeader
            {
                HeaderOffset = off,
                RawName = rawName,
                Name = name,
                VirtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 8, 4)),
                VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 12, 4)),
                SizeOfRawData = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 16, 4)),
                PointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 20, 4)),
                Characteristics = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 36, 4))
            });
        }

        return true;
    }

    private static (uint Rva, uint Size) GetDataDirectory(byte[] data, PeInfo pe, int index)
    {
        if (index >= pe.NumberOfDataDirectories) return (0, 0);
        var off = pe.DataDirectoryOffset + index * 8;
        if (off + 8 > data.Length) return (0, 0);
        return (
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4, 4))
        );
    }

    private static void SetDataDirectory(byte[] data, PeInfo pe, int index, uint rva, uint size)
    {
        if (index >= pe.NumberOfDataDirectories) return;
        var off = pe.DataDirectoryOffset + index * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off, 4), rva);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 4, 4), size);
    }

    private static int RvaToFileOffset(PeInfo pe, uint rva)
    {
        foreach (var sec in pe.Sections)
        {
            var span = Math.Max(sec.VirtualSize, sec.SizeOfRawData);
            if (rva >= sec.VirtualAddress && rva < sec.VirtualAddress + span)
                return (int)(sec.PointerToRawData + (rva - sec.VirtualAddress));
        }
        return rva < pe.SizeOfHeaders ? (int)rva : -1;
    }

    private static bool HasAnadiusImport(byte[] data, PeInfo pe)
    {
        var (impRva, impSize) = GetDataDirectory(data, pe, DirImport);
        if (impRva == 0 || impSize < 20) return false;

        var offset = RvaToFileOffset(pe, impRva);
        if (offset < 0 || offset + 20 > data.Length) return false;

        for (int i = 0; offset + (i + 1) * 20 <= data.Length; i++)
        {
            var descOff = offset + i * 20;
            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(descOff + 12, 4));
            var firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(descOff + 16, 4));
            if (nameRva == 0 && firstThunk == 0) break;
            if (nameRva == 0) continue;

            var nameOff = RvaToFileOffset(pe, nameRva);
            if (nameOff < 0 || nameOff >= data.Length) continue;
            var dllName = ReadAsciiZ(data, nameOff);
            if (dllName.StartsWith("anadius32", StringComparison.OrdinalIgnoreCase) ||
                dllName.StartsWith("anadius64", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadAsciiZ(byte[] data, int offset, int maxLen = 256)
    {
        int end = offset;
        int limit = Math.Min(data.Length, offset + maxLen);
        while (end < limit && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static bool CanAppendImportSection(byte[] data, PeInfo pe, out string reason)
    {
        reason = string.Empty;
        var sectionTableEnd = pe.SectionTableOffset + pe.NumberOfSections * 40;
        var minSectionRaw = pe.Sections.Where(s => s.PointerToRawData > 0).Select(s => (int)s.PointerToRawData).DefaultIfEmpty(data.Length).Min();
        if (sectionTableEnd + 40 > minSectionRaw)
        {
            reason = "no PE header room for a new import section";
            return false;
        }

        var (secOff, secSize) = GetDataDirectory(data, pe, DirSecurity);
        var lastEnd = pe.Sections.Where(s => s.PointerToRawData > 0)
            .Select(s => (long)s.PointerToRawData + s.SizeOfRawData)
            .DefaultIfEmpty(pe.SizeOfHeaders)
            .Max();

        // Allow stripping trailing Authenticode signature at EOF, but refuse if there is non-signature overlay data.
        if (data.Length > lastEnd)
        {
            if (secSize > 0 && secOff == lastEnd && secOff + secSize == data.Length)
                return true;

            reason = "executable contains trailing overlay data";
            return false;
        }

        return true;
    }

    private static byte[] AppendAnadiusImportSection(byte[] input, PeInfo pe)
    {
        if (!CanAppendImportSection(input, pe, out var reason))
            throw new InvalidOperationException($"Cannot patch PE import table: {reason}.");

        var (secOff, secSize) = GetDataDirectory(input, pe, DirSecurity);
        var lastRawEnd = pe.Sections.Where(s => s.PointerToRawData > 0)
            .Select(s => (int)(s.PointerToRawData + s.SizeOfRawData))
            .DefaultIfEmpty((int)pe.SizeOfHeaders)
            .Max();

        // Trim trailing Authenticode certificate if present at EOF
        var baseLen = (secSize > 0 && secOff == lastRawEnd && secOff + secSize == input.Length)
            ? lastRawEnd
            : input.Length;

        var (origImpRva, origImpSize) = GetDataDirectory(input, pe, DirImport);
        byte[] origImpBytes = [];
        if (origImpRva != 0 && origImpSize >= 20)
        {
            var origOff = RvaToFileOffset(pe, origImpRva);
            if (origOff >= 0 && origOff + origImpSize <= baseLen)
                origImpBytes = input.AsSpan(origOff, (int)origImpSize).ToArray();
        }
        if (origImpBytes.Length == 0)
            origImpBytes = new byte[20]; // null terminator

        var lastSection = pe.Sections[^1];
        var secAlign = Math.Max(0x1000u, pe.SectionAlignment);
        var fileAlign = Math.Max(0x200u, pe.FileAlignment);
        var newRva = AlignUp(lastSection.VirtualAddress + Math.Max(lastSection.VirtualSize, lastSection.SizeOfRawData), secAlign);
        var newRawOffset = AlignUp((uint)baseLen, fileAlign);

        var (payload, newImpRva, newImpSize) = BuildAnadiusImportPayload(pe.Is64Bit, newRva, origImpBytes, addInFront: true);
        var newRawSize = AlignUp((uint)payload.Length, fileAlign);
        var newVirtualSize = (uint)payload.Length;

        var output = new byte[newRawOffset + newRawSize];
        Buffer.BlockCopy(input, 0, output, 0, baseLen);
        Buffer.BlockCopy(payload, 0, output, (int)newRawOffset, payload.Length);

        // Write new section header ".anadius"
        var newSecHdrOffset = pe.SectionTableOffset + pe.NumberOfSections * 40;
        var secNameBytes = new byte[8];
        Encoding.ASCII.GetBytes(".anadius").CopyTo(secNameBytes, 0);
        Buffer.BlockCopy(secNameBytes, 0, output, newSecHdrOffset, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(newSecHdrOffset + 8, 4), newVirtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(newSecHdrOffset + 12, 4), newRva);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(newSecHdrOffset + 16, 4), newRawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(newSecHdrOffset + 20, 4), newRawOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(newSecHdrOffset + 36, 4), 0xC0000040); // READ | WRITE | INITIALIZED_DATA

        // Update NumberOfSections, SizeOfImage, Import Dir, clear BoundImport & Security
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pe.PeOffset + 6, 2), (ushort)(pe.NumberOfSections + 1));
        var newSizeOfImage = AlignUp(newRva + newVirtualSize, secAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pe.OptionalHeaderOffset + 56, 4), newSizeOfImage);
        SetDataDirectory(output, pe, DirImport, newImpRva, newImpSize);
        SetDataDirectory(output, pe, DirBoundImport, 0, 0);
        SetDataDirectory(output, pe, DirSecurity, 0, 0);

        return output;
    }

    private static (byte[] Payload, uint ImportDirRva, uint ImportDirSize) BuildAnadiusImportPayload(
        bool is64Bit, uint sectionRva, byte[] existingImportDescriptors, bool addInFront)
    {
        var dllName = is64Bit ? "anadius64" : "anadius32";
        const string funcName = "anadius";

        static byte[] ToAlignedAscii(string s)
        {
            var raw = Encoding.ASCII.GetBytes(s);
            int pad = 2 - (raw.Length & 1);
            var buf = new byte[raw.Length + pad];
            Buffer.BlockCopy(raw, 0, buf, 0, raw.Length);
            return buf;
        }

        using var ms = new MemoryStream();
        uint cursor = sectionRva;

        var dllNameBytes = ToAlignedAscii(dllName);
        uint dllNameRva = cursor;
        ms.Write(dllNameBytes);
        cursor += (uint)dllNameBytes.Length;

        var funcHintNameBytes = ToAlignedAscii(funcName);
        uint hintNameRva = cursor;
        ms.Write([0, 0]); // Hint = 0
        ms.Write(funcHintNameBytes);
        cursor += (uint)(2 + funcHintNameBytes.Length);

        uint thunkTableRva = cursor;
        if (is64Bit)
        {
            Span<byte> thunks = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(thunks[..8], hintNameRva);
            BinaryPrimitives.WriteUInt64LittleEndian(thunks[8..16], 0);
            ms.Write(thunks);
            cursor += 16;
        }
        else
        {
            Span<byte> thunks = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(thunks[..4], hintNameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(thunks[4..8], 0);
            ms.Write(thunks);
            cursor += 8;
        }

        uint newImportDirRva = cursor;
        Span<byte> newDescriptor = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(newDescriptor[..4], 0); // OriginalFirstThunk = 0 (mirrors dll_adder.py)
        BinaryPrimitives.WriteUInt32LittleEndian(newDescriptor[4..8], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(newDescriptor[8..12], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(newDescriptor[12..16], dllNameRva);
        BinaryPrimitives.WriteUInt32LittleEndian(newDescriptor[16..20], thunkTableRva);

        byte[] combinedImports;
        if (addInFront || existingImportDescriptors.Length < 20)
        {
            combinedImports = new byte[20 + existingImportDescriptors.Length];
            newDescriptor.CopyTo(combinedImports.AsSpan(0, 20));
            Buffer.BlockCopy(existingImportDescriptors, 0, combinedImports, 20, existingImportDescriptors.Length);
        }
        else
        {
            combinedImports = new byte[existingImportDescriptors.Length + 20];
            int headLen = existingImportDescriptors.Length - 20;
            Buffer.BlockCopy(existingImportDescriptors, 0, combinedImports, 0, headLen);
            newDescriptor.CopyTo(combinedImports.AsSpan(headLen, 20));
            Buffer.BlockCopy(existingImportDescriptors, headLen, combinedImports, headLen + 20, 20);
        }

        ms.Write(combinedImports);
        return (ms.ToArray(), newImportDirRva, (uint)combinedImports.Length);
    }

    private static uint AlignUp(uint value, uint alignment) =>
        alignment == 0 ? value : ((value + alignment - 1) / alignment) * alignment;

    // -----------------------------------------------------------------------
    // .ooa Origin DRM Unwrapper (port of origin_unwrapper.py + dll_adder.py)
    // -----------------------------------------------------------------------

    private sealed record EncryptedSectionInfo(uint Rva, uint Size, uint DecryptedSize, byte[] XorKey);

    private static byte[] UnwrapOoaAndAddAnadiusImport(byte[] input, PeInfo pe, string sourceExePath, string gameRootDirectory)
    {
        var output = (byte[])input.Clone();
        var ooaSec = pe.Sections[^1];
        uint ooaRva = ooaSec.VirtualAddress;
        int ooaFileOffset = (int)ooaSec.PointerToRawData;
        int ooaSize = (int)ooaSec.SizeOfRawData;
        if (ooaFileOffset + ooaSize > output.Length)
            throw new InvalidOperationException(".ooa section extends past end of file.");

        if (pe.AddressOfEntryPoint != ooaRva)
            throw new InvalidOperationException($"OEP (0x{pe.AddressOfEntryPoint:X}) does not match .ooa RVA (0x{ooaRva:X}).");

        int cursor = ooaFileOffset;
        int OffsetInOoa() => cursor - ooaFileOffset;
        void CheckOffset(uint expected, string label)
        {
            uint current = (uint)OffsetInOoa() + ooaRva;
            if (current != expected)
                throw new InvalidOperationException($".ooa offset mismatch at {label}: 0x{current:X} != 0x{expected:X}.");
        }
        byte[] ReadBytes(int count)
        {
            if (cursor + count > ooaFileOffset + ooaSize)
                throw new InvalidOperationException("Unexpected end of .ooa section.");
            var slice = output.AsSpan(cursor, count).ToArray();
            cursor += count;
            return slice;
        }
        uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
        ulong ReadU64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));
        byte ReadU8() => ReadBytes(1)[0];
        string ReadCString()
        {
            int start = cursor;
            int end = start;
            while (end < ooaFileOffset + ooaSize && output[end] != 0) end++;
            var s = Encoding.ASCII.GetString(output, start, end - start);
            cursor = Math.Min(ooaFileOffset + ooaSize, end + 1);
            return s;
        }
        string ReadEncodedString(int length)
        {
            int charLen = 1;
            while (cursor + charLen < output.Length && output[cursor + charLen] == 0 && charLen < 4)
                charLen++;
            var raw = ReadBytes(charLen * length);
            var encoding = charLen switch
            {
                2 => Encoding.Unicode,
                4 => Encoding.UTF32,
                _ => Encoding.UTF8
            };
            var decoded = encoding.GetString(raw);
            int nul = decoded.IndexOf('\0');
            return nul >= 0 ? decoded[..nul] : decoded;
        }

        _ = ReadBytes(16); // ASM jump stub
        var activationDll = ReadCString();
        if (!pe.Is64Bit) _ = ReadBytes(2);

        var magic = Encoding.ASCII.GetString(ReadBytes(4));
        if (magic != "5AOO")
            throw new InvalidOperationException($"Unexpected .ooa magic '{magic}', expected '5AOO'.");

        var versionHash = ReadBytes(20);
        var (commonVersions, decryptionKeys) = LoadOriginUnwrapperData();
        var wrapperVersionStr = ResolveWrapperVersion(versionHash, commonVersions, Path.GetDirectoryName(sourceExePath), activationDll);
        var parsedVersion = ParseVersion(wrapperVersionStr);

        List<string> contentIds;
        if (parsedVersion <= LastVersionWithParFiles)
        {
            var parPath = Path.ChangeExtension(sourceExePath, ".par");
            if (!File.Exists(parPath))
                throw new InvalidOperationException($"Missing .par file for legacy Origin DRM ({wrapperVersionStr}): {Path.GetFileName(parPath)}.");
            contentIds = ReadParContentIds(parPath);
        }
        else
        {
            _ = ReadU32(); // flags
            var idsStr = ReadEncodedString(0x100);
            contentIds = idsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        if (contentIds.Count == 0)
            throw new InvalidOperationException("No Content IDs found inside .ooa section.");

        var (fakeImpRva, fakeImpSize) = GetDataDirectory(output, pe, DirImport);
        CheckOffset(fakeImpRva, "fake import dir");
        _ = ReadBytes((int)fakeImpSize);

        var (fakeIatRva, fakeIatSize) = GetDataDirectory(output, pe, DirIat);
        CheckOffset(fakeIatRva, "fake IAT");
        _ = ReadBytes((int)fakeIatSize);
        _ = ReadBytes((int)fakeIatSize); // fake OFT
        _ = ReadBytes(72); // gap

        var maxReloc = ReadU32();
        var relocSize = ReadU32();
        if (relocSize > 0)
        {
            var (fakeRelocRva, _) = GetDataDirectory(output, pe, DirReloc);
            CheckOffset(fakeRelocRva, "fake reloc");
        }
        _ = ReadBytes((int)relocSize);
        _ = ReadBytes((int)(maxReloc - relocSize));

        var hasTls = ReadU32() == 1;
        var callbackRva = ReadU32();
        var firstCallback = ReadBytes(8);

        if (parsedVersion >= FirstVersionWith0x100Bytes)
            _ = ReadBytes(256);

        var realOep = ReadU32();
        int encCount = ReadU8();
        var encryptedSections = new List<EncryptedSectionInfo>();
        for (int i = 0; i < encCount; i++)
        {
            var secRva = ReadU32();
            var secSize = ReadU32();
            var decSize = ReadU32();
            _ = ReadBytes(20); // unknown1, unknown2, gap1, unknown3, gap2
            var xorKey = ReadBytes(16);
            encryptedSections.Add(new EncryptedSectionInfo(secRva, secSize, decSize, xorKey));
        }

        _ = ReadBytes((10 - encCount) * 48); // garbage
        _ = ReadBytes(parsedVersion >= FirstVersionWithShorterGapAfterGarbage ? 8 : 9);

        var flags2 = ReadU8();
        bool hasImageBase = parsedVersion >= FirstVersionWithoutImageBaseFlag || (flags2 & 0x1) != 0;
        var imageBaseVal = ReadU64();
        if (hasImageBase && imageBaseVal != pe.ImageBase)
            throw new InvalidOperationException("ImageBase mismatch inside .ooa header.");

        var imageSize = ReadU32();
        if (imageSize != ooaRva)
            throw new InvalidOperationException("ImageSize mismatch inside .ooa header.");

        var realImpRva = ReadU32();
        var realImpSize = ReadU32();
        var realRelocRva = ReadU32();
        var realRelocSize = ReadU32();
        var realIatRva = ReadU32();
        var realIatSize = ReadU32();
        _ = ReadCString(); // denuvo dll name

        // Find decryption key (from embedded data.yaml or EA license .dlf)
        var aesKey = ResolveDecryptionKey(contentIds, decryptionKeys, gameRootDirectory);
        if (aesKey is null)
            throw new InvalidOperationException($"Origin DRM decryption key not found for ContentID(s): {string.Join(", ", contentIds)}. Run the original game once with EA App to cache its license file.");

        // Decrypt sections
        foreach (var sec in encryptedSections)
        {
            var fileOff = RvaToFileOffset(pe, sec.Rva);
            if (fileOff < 0 || fileOff + sec.Size > output.Length)
                throw new InvalidOperationException($"Encrypted section RVA 0x{sec.Rva:X} out of range.");

            var cipherText = output.AsSpan(fileOff, (int)sec.Size).ToArray();
            var decrypted = AesCbcDecryptPkcs7(cipherText, aesKey);
            if (decrypted.Length != sec.DecryptedSize)
                throw new InvalidOperationException($"Decrypted section size mismatch ({decrypted.Length} != {sec.DecryptedSize}).");

            var restored = new byte[sec.Size];
            Buffer.BlockCopy(decrypted, 0, restored, 0, decrypted.Length);
            for (int b = 0; b < 16; b++)
                restored[decrypted.Length + b] = (byte)(decrypted[decrypted.Length - 16 + b] ^ sec.XorKey[b]);

            Buffer.BlockCopy(restored, 0, output, fileOff, restored.Length);
        }

        // Restore OEP & real directories
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pe.OptionalHeaderOffset + 16, 4), realOep);
        SetDataDirectory(output, pe, DirImport, realImpRva, realImpSize);
        SetDataDirectory(output, pe, DirReloc, realRelocRva, realRelocSize);
        SetDataDirectory(output, pe, DirIat, realIatRva, realIatSize);
        SetDataDirectory(output, pe, DirBoundImport, 0, 0);
        SetDataDirectory(output, pe, DirSecurity, 0, 0);

        if (hasTls)
        {
            var (tlsRva, _) = GetDataDirectory(output, pe, DirTls);
            int tlsFileOff = RvaToFileOffset(pe, tlsRva);
            if (tlsFileOff >= 0)
            {
                if (parsedVersion < FirstVersionWithoutRestoringTlsBytes)
                {
                    int cbOff = RvaToFileOffset(pe, callbackRva);
                    if (cbOff >= 0 && cbOff + firstCallback.Length <= output.Length && output.AsSpan(cbOff, firstCallback.Length).SequenceEqual(new byte[firstCallback.Length]))
                        Buffer.BlockCopy(firstCallback, 0, output, cbOff, firstCallback.Length);
                }
                ulong cbVa = pe.ImageBase + callbackRva;
                if (pe.Is64Bit && tlsFileOff + 32 <= output.Length)
                    BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(tlsFileOff + 24, 8), cbVa);
                else if (!pe.Is64Bit && tlsFileOff + 16 <= output.Length)
                    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(tlsFileOff + 12, 4), (uint)cbVa);
            }
        }

        // Now perform dll_adder.py on the .ooa section -> rename to .anadius and prepend import descriptor!
        byte[] realImpBytes = [];
        int realImpOff = RvaToFileOffset(pe, realImpRva);
        if (realImpOff >= 0 && realImpOff + realImpSize <= output.Length)
            realImpBytes = output.AsSpan(realImpOff, (int)realImpSize).ToArray();
        else
            realImpBytes = new byte[20];

        var (payload, patchedImpRva, patchedImpSize) = BuildAnadiusImportPayload(pe.Is64Bit, ooaRva, realImpBytes, addInFront: true);
        if (payload.Length > ooaSize)
            throw new InvalidOperationException(".ooa section too small to hold .anadius import table.");

        // Rename section header & update characteristics
        var anadiusName = new byte[8];
        Encoding.ASCII.GetBytes(".anadius").CopyTo(anadiusName, 0);
        Buffer.BlockCopy(anadiusName, 0, output, ooaSec.HeaderOffset, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(ooaSec.HeaderOffset + 36, 4), 0xC0000040);

        Array.Clear(output, ooaFileOffset, ooaSize);
        Buffer.BlockCopy(payload, 0, output, ooaFileOffset, payload.Length);
        SetDataDirectory(output, pe, DirImport, patchedImpRva, patchedImpSize);

        return output;
    }

    private static byte[] AesCbcDecryptPkcs7(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static (Dictionary<string, string> VersionsBySha1Hex, Dictionary<string, byte[]> KeysByContentId) LoadOriginUnwrapperData()
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        string? yamlText = null;
        var diskPath = Path.Combine(EmbeddedEaOriginRuntime.RootPath, "data.yaml");
        if (File.Exists(diskPath))
        {
            yamlText = File.ReadAllText(diskPath);
        }
        else
        {
            using var stream = typeof(OriginExePatcher).Assembly.GetManifestResourceStream("AchievementSync.Core.EaOriginTemplate.data.yaml");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                yamlText = reader.ReadToEnd();
            }
        }

        if (string.IsNullOrWhiteSpace(yamlText))
            return (versions, keys);

        int docIndex = 0;
        foreach (var rawLine in yamlText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Split('#')[0].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line == "---") { docIndex++; continue; }
            if (line == "...") continue;

            if (docIndex == 1 && line.StartsWith("- "))
            {
                var ver = line[2..].Trim();
                var hashHex = Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(ver)));
                versions[hashHex] = ver;
            }
            else if (docIndex >= 2)
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var id = line[..colon].Trim();
                    var b64 = line[(colon + 1)..].Trim();
                    try
                    {
                        keys[id] = Convert.FromBase64String(b64);
                    }
                    catch { }
                }
            }
        }

        return (versions, keys);
    }

    private static string ResolveWrapperVersion(byte[] versionHash, Dictionary<string, string> commonVersions, string? exeDir, string activationDll)
    {
        var targetHex = Convert.ToHexString(versionHash);
        if (commonVersions.TryGetValue(targetHex, out var known))
            return known;

        if (!string.IsNullOrWhiteSpace(exeDir) && !string.IsNullOrWhiteSpace(activationDll))
        {
            var dllPath = Path.Combine(exeDir, activationDll);
            if (File.Exists(dllPath))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(dllPath);
                    foreach (var cand in new[]
                             {
                                 $"{vi.ProductMajorPart}.{vi.ProductMinorPart:D2}.{vi.ProductBuildPart:D2}.{vi.ProductPrivatePart:D2}",
                                 $"{vi.ProductMajorPart}.{vi.ProductMinorPart}.{vi.ProductBuildPart}.{vi.ProductPrivatePart}"
                             })
                    {
                        if (SHA1.HashData(Encoding.ASCII.GetBytes(cand)).AsSpan().SequenceEqual(versionHash))
                            return cand;
                    }
                }
                catch { }
            }
        }

        // Fast brute force for common 4.x/5.x Origin wrapper versions if not listed in data.yaml
        for (int a = 5; a >= 4; a--)
        {
            for (int b = 0; b < 30; b++)
            {
                for (int c = 0; c < 30; c++)
                {
                    for (int d = 0; d < 250; d++)
                    {
                        var v1 = $"{a}.{b:D2}.{c:D2}.{d:D2}";
                        if (SHA1.HashData(Encoding.ASCII.GetBytes(v1)).AsSpan().SequenceEqual(versionHash))
                            return v1;
                        var v2 = $"{a}.{b}.{c}.{d}";
                        if (SHA1.HashData(Encoding.ASCII.GetBytes(v2)).AsSpan().SequenceEqual(versionHash))
                            return v2;
                    }
                }
            }
        }

        throw new InvalidOperationException($"Unknown Origin DRM wrapper version (SHA-1: {targetHex}).");
    }

    private static Version ParseVersion(string version)
    {
        var parts = version.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
        while (parts.Count < 4) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    private static byte[]? ResolveDecryptionKey(IReadOnlyList<string> contentIds, Dictionary<string, byte[]> keys, string gameRootDirectory)
    {
        foreach (var cid in contentIds)
        {
            if (keys.TryGetValue(cid, out var key))
                return key;
        }

        var licenseRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Electronic Arts", "EA Services", "License");

        if (Directory.Exists(licenseRoot))
        {
            foreach (var cid in contentIds)
            {
                foreach (var suffix in new[] { "_cached.dlf", ".dlf" })
                {
                    var dlfPath = Path.Combine(licenseRoot, cid + suffix);
                    if (!File.Exists(dlfPath)) continue;
                    try
                    {
                        var raw = File.ReadAllBytes(dlfPath);
                        if (raw.Length <= 0x41) continue;
                        var xmlBytes = AesCbcDecryptPkcs7(raw.AsSpan(0x41).ToArray(), OriginLicenseAesKey);
                        var xml = XDocument.Parse(Encoding.UTF8.GetString(xmlBytes));
                        var cipherKey = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "CipherKey")?.Value?.Trim();
                        if (string.IsNullOrWhiteSpace(cipherKey)) continue;

                        if (cipherKey.Length > 22) cipherKey = cipherKey[..22];
                        cipherKey = cipherKey.TrimEnd('=');
                        if (cipherKey.Length % 4 == 1) cipherKey = cipherKey[..^1];
                        while (cipherKey.Length % 4 != 0) cipherKey += "=";
                        var keyBytes = Convert.FromBase64String(cipherKey);
                        if (keyBytes.Length < 16)
                        {
                            var padded = new byte[16];
                            Buffer.BlockCopy(keyBytes, 0, padded, 0, keyBytes.Length);
                            return padded;
                        }
                        return keyBytes[..16];
                    }
                    catch { }
                }
            }
        }

        // Also check installerdata.xml contentIDs in case a sibling key matches
        var xmlPath = Path.Combine(gameRootDirectory, "__Installer", "installerdata.xml");
        if (File.Exists(xmlPath))
        {
            try
            {
                var doc = XDocument.Load(xmlPath);
                foreach (var cid in doc.Descendants("contentID").Select(e => e.Value.Trim()))
                {
                    if (keys.TryGetValue(cid, out var key))
                        return key;
                }
            }
            catch { }
        }

        return null;
    }

    private static List<string> ReadParContentIds(string parPath)
    {
        var raw = File.ReadAllBytes(parPath);
        if (raw.Length < 0x10C) return [];
        // In legacy Origin .par files, contentIDs start at offset 0x08 (null-terminated ASCII)
        var end = Array.IndexOf(raw, (byte)0, 8, Math.Min(256, raw.Length - 8));
        var len = end >= 8 ? end - 8 : Math.Min(256, raw.Length - 8);
        var text = Encoding.ASCII.GetString(raw, 8, len);
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
