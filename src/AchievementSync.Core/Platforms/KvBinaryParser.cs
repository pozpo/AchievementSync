using System.Buffers.Binary;
using System.Text;

namespace AchievementSync.Core.Platforms;

/// <summary>Base for a parsed Valve KeyValues (KV1) value.</summary>
public abstract class KvValue(string name)
{
    public string Name { get; } = name;
}

public sealed class KvString(string name, string value) : KvValue(name)
{
    public string Value { get; } = value;
}

public sealed class KvInt32(string name, int value) : KvValue(name)
{
    public int Value { get; } = value;
}

public sealed class KvFloat(string name, float value) : KvValue(name)
{
    public float Value { get; } = value;
}

public sealed class KvUInt64(string name, ulong value) : KvValue(name)
{
    public ulong Value { get; } = value;
}

public sealed class KvInt64(string name, long value) : KvValue(name)
{
    public long Value { get; } = value;
}

public sealed class KvWideString(string name, string value) : KvValue(name)
{
    public string Value { get; } = value;
}

/// <summary>An unordered KeyValues object whose children keep their on-disk order.</summary>
public sealed class KvObject(string name) : KvValue(name)
{
    private readonly List<KvValue> _items = [];

    public IReadOnlyList<KvValue> Items => _items;

    public void Add(KvValue value) => _items.Add(value);

    public bool TryGet(string childName, out KvValue? value)
    {
        foreach (var item in _items)
        {
            if (string.Equals(item.Name, childName, StringComparison.OrdinalIgnoreCase))
            {
                value = item;
                return true;
            }
        }

        value = null;
        return false;
    }

    public KvObject? Child(string childName) => TryGet(childName, out var value) ? value as KvObject : null;

    public string? String(string childName) => TryGet(childName, out var value)
        ? value switch { KvString text => text.Value, KvWideString wide => wide.Value, _ => null }
        : null;

    public long? Long(string childName)
    {
        if (!TryGet(childName, out var value)) return null;
        return value switch
        {
            KvInt32 number => number.Value,
            KvUInt64 number => unchecked((long)number.Value),
            KvInt64 number => number.Value,
            KvFloat number => (long)number.Value,
            KvString text when long.TryParse(text.Value, out var parsed) => parsed,
            _ => null
        };
    }
}

/// <summary>Robust little-endian Valve KeyValues binary parser used by Steam appcache and
/// achievement files. Malformed input throws <see cref="KvFormatException"/>.</summary>
public static class KvBinaryParser
{
    private const int MaxDepth = 64;

    public static KvObject ParseFile(string path) => Parse(File.ReadAllBytes(path));

    public static KvObject Parse(ReadOnlySpan<byte> data)
    {
        var index = 0;
        var root = new KvObject(string.Empty);
        ParseObjectContents(data, ref index, root, 0);
        return root;
    }

    private static void ParseObjectContents(ReadOnlySpan<byte> data, ref int index, KvObject target, int depth)
    {
        if (depth > MaxDepth) throw new KvFormatException("KeyValues nesting is too deep.");
        while (true)
        {
            if (index >= data.Length) return;
            var type = data[index++];
            if (type == 0x08) return;
            var name = ReadCString(data, ref index);

            switch (type)
            {
                case 0x00:
                {
                    var child = new KvObject(name);
                    ParseObjectContents(data, ref index, child, depth + 1);
                    target.Add(child);
                    break;
                }
                case 0x01:
                {
                    var value = ReadCString(data, ref index);
                    target.Add(new KvString(name, value));
                    break;
                }
                case 0x02:
                {
                    target.Add(new KvInt32(name, ReadInt32(data, ref index)));
                    break;
                }
                case 0x03:
                {
                    var bits = ReadInt32(data, ref index);
                    target.Add(new KvFloat(name, BitConverter.Int32BitsToSingle(bits)));
                    break;
                }
                case 0x04:
                {
                    target.Add(new KvInt32(name, ReadInt32(data, ref index)));
                    break;
                }
                case 0x05:
                {
                    target.Add(new KvWideString(name, ReadWideString(data, ref index)));
                    break;
                }
                case 0x06:
                {
                    target.Add(new KvInt32(name, ReadInt32(data, ref index)));
                    break;
                }
                case 0x07:
                case 0x09:
                {
                    Ensure(data, index, 8);
                    target.Add(new KvUInt64(name, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(index, 8))));
                    index += 8;
                    break;
                }
                case 0x0A:
                {
                    Ensure(data, index, 8);
                    target.Add(new KvInt64(name, BinaryPrimitives.ReadInt64LittleEndian(data.Slice(index, 8))));
                    index += 8;
                    break;
                }
                default:
                    throw new KvFormatException($"Unknown KeyValues type 0x{type:X2} at offset {index - 1}.");
            }
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int index)
    {
        var start = index;
        while (index < data.Length && data[index] != 0) index++;
        if (index >= data.Length) throw new KvFormatException("Unterminated KeyValues string.");
        var text = Encoding.UTF8.GetString(data.Slice(start, index - start));
        index++;
        return text;
    }

    private static string ReadWideString(ReadOnlySpan<byte> data, ref int index)
    {
        var start = index;
        while (index + 1 < data.Length && (data[index] != 0 || data[index + 1] != 0)) index += 2;
        var text = Encoding.Unicode.GetString(data.Slice(start, index - start));
        index = Math.Min(index + 2, data.Length);
        return text;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int index)
    {
        Ensure(data, index, 4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(index, 4));
        index += 4;
        return value;
    }

    private static void Ensure(ReadOnlySpan<byte> data, int index, int count)
    {
        if (index + count > data.Length) throw new KvFormatException("Truncated KeyValues value.");
    }
}

public sealed class KvFormatException(string message) : Exception(message);
