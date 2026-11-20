using System.Buffers.Binary;
using System.Text;

namespace AchievementSync.Core.Platforms;

/// <summary>Small read-only SQLite table reader used for local launcher databases
/// (for example GOG Galaxy's per-game <c>gameplay.db</c>). It reads committed leaf pages
/// only and never opens the file for writing.</summary>
public sealed class SqliteReadOnly
{
    private readonly byte[] _data;
    private readonly int _pageSize;
    private readonly int _usableSize;

    public SqliteReadOnly(string path) : this(File.ReadAllBytes(path)) { }

    public SqliteReadOnly(byte[] data)
    {
        _data = data;
        if (data.Length < 100 || !data.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("Not a SQLite 3 database.");
        var raw = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(16, 2));
        _pageSize = raw == 1 ? 65536 : raw;
        var reserved = data[20];
        _usableSize = _pageSize - reserved;
        if (_pageSize < 512 || _pageSize > 65536) throw new InvalidDataException("Unsupported SQLite page size.");
    }

    public IReadOnlyList<object?[]> ReadTable(string tableName)
    {
        var master = ReadPageRows(1);
        int? rootPage = null;
        foreach (var row in master)
        {
            if (row.Length >= 4
                && string.Equals(row[0] as string, "table", StringComparison.OrdinalIgnoreCase)
                && string.Equals(row[1] as string, tableName, StringComparison.OrdinalIgnoreCase)
                && row[3] is long page)
            {
                rootPage = (int)page;
                break;
            }
        }

        return rootPage is null ? [] : ReadPageRows(rootPage.Value);
    }

    private List<object?[]> ReadPageRows(int rootPage)
    {
        var rows = new List<object?[]>();
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(rootPage);
        while (pending.Count > 0)
        {
            var pageNumber = pending.Pop();
            if (!visited.Add(pageNumber) || visited.Count > 200_000) continue;
            var pageOffset = (pageNumber - 1) * _pageSize;
            if (pageOffset < 0 || pageOffset + _pageSize > _data.Length) continue;
            var page = _data.AsSpan(pageOffset, _pageSize);
            var type = page[0];
            var cellCount = BinaryPrimitives.ReadUInt16BigEndian(page[3..5]);
            if (cellCount > 20_000) continue;

            if (type == 0x05)
            {
                for (var index = 0; index < cellCount; index++)
                {
                    var pointer = BinaryPrimitives.ReadUInt16BigEndian(page[(12 + index * 2)..]);
                    if (pointer + 4 > _pageSize) continue;
                    pending.Push((int)BinaryPrimitives.ReadUInt32BigEndian(page[pointer..]));
                }
                var right = BinaryPrimitives.ReadUInt32BigEndian(page[8..12]);
                if (right > 0) pending.Push((int)right);
                continue;
            }

            if (type != 0x0D) continue;
            for (var index = 0; index < cellCount; index++)
            {
                var pointer = BinaryPrimitives.ReadUInt16BigEndian(page[(8 + index * 2)..]);
                if (pointer <= 0 || pointer >= _pageSize) continue;
                var record = ReadLeafCell(page, pointer);
                if (record is not null) rows.Add(record);
            }
        }

        return rows;
    }

    private object?[]? ReadLeafCell(ReadOnlySpan<byte> page, int pointer)
    {
        var cursor = pointer;
        if (!TryReadVarint(page, ref cursor, out var payloadLength)) return null;
        if (!TryReadVarint(page, ref cursor, out _)) return null;
        if (payloadLength < 0 || payloadLength > 64 * 1024 * 1024) return null;

        var maxLocal = _usableSize - 35;
        var minLocal = (_usableSize - 12) * 32 / 255 - 23;
        int local;
        if (payloadLength <= maxLocal) local = (int)payloadLength;
        else
        {
            local = minLocal + (int)((payloadLength - minLocal) % (_usableSize - 4));
            if (local > maxLocal) local = minLocal;
        }

        if (cursor + local > page.Length) return null;
        var payload = new byte[payloadLength];
        page.Slice(cursor, local).CopyTo(payload);
        if (local < payloadLength)
        {
            var overflowPage = cursor + local + 4 <= page.Length
                ? BinaryPrimitives.ReadUInt32BigEndian(page[(cursor + local)..])
                : 0u;
            ReadOverflow(payload, local, overflowPage);
        }

        return ReadRecord(payload);
    }

    private void ReadOverflow(byte[] payload, int local, uint firstPage)
    {
        var pageNumber = (int)firstPage;
        var written = local;
        var guard = 0;
        while (pageNumber > 0 && written < payload.Length && guard++ < 100_000)
        {
            var offset = (pageNumber - 1) * _pageSize;
            if (offset < 0 || offset + 4 > _data.Length) return;
            var next = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
            var chunk = Math.Min(_usableSize - 4, payload.Length - written);
            _data.AsSpan(offset + 4, chunk).CopyTo(payload.AsSpan(written));
            written += chunk;
            pageNumber = (int)next;
        }
    }

    private static object?[]? ReadRecord(byte[] payload)
    {
        var cursor = 0;
        if (!TryReadVarint(payload, ref cursor, out var headerLength) || headerLength <= 0 || headerLength > payload.Length)
            return null;
        var headerEnd = (int)headerLength;
        var serialTypes = new List<long>();
        while (cursor < headerEnd)
        {
            if (!TryReadVarint(payload, ref cursor, out var serial)) break;
            serialTypes.Add(serial);
        }

        var values = new object?[serialTypes.Count];
        var body = headerEnd;
        for (var index = 0; index < serialTypes.Count; index++)
        {
            var (value, size) = ReadValue(payload, body, serialTypes[index]);
            values[index] = value;
            body += size;
        }
        return values;
    }

    private static (object? Value, int Size) ReadValue(byte[] payload, int offset, long serial)
    {
        if (serial == 0) return (null, 0);
        if (serial is >= 1 and <= 6)
        {
            var size = (int)serial;
            if (offset + size > payload.Length) return (null, 0);
            long value = 0;
            for (var index = 0; index < size; index++) value = (value << 8) | payload[offset + index];
            var shift = 64 - size * 8;
            return (shift > 0 ? value << shift >> shift : value, size);
        }
        if (serial == 7) return (offset + 8 <= payload.Length ? BitConverter.ToDouble(payload, offset) : null, 8);
        if (serial == 8) return (0L, 0);
        if (serial == 9) return (1L, 0);
        if (serial < 12) return (null, 0);

        var length = (int)((serial - 12) / 2);
        if (offset + length > payload.Length) return (null, 0);
        if (serial % 2 == 0) return (payload.AsSpan(offset, length).ToArray(), length);
        return (Encoding.UTF8.GetString(payload, offset, length), length);
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int cursor, out long value)
    {
        value = 0;
        for (var index = 0; index < 9; index++)
        {
            if (cursor >= data.Length) return false;
            var current = data[cursor++];
            if (index == 8) value = (value << 8) | current;
            else value = (value << 7) | (long)(current & 0x7F);
            if ((current & 0x80) == 0) return true;
        }
        return true;
    }
}
