using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace NotAirplay;

internal sealed class BinaryPropertyListReader
{
    private readonly ReadOnlyMemory<byte> input;
    private readonly int[] offsets;
    private readonly int referenceSize;
    private readonly int tableOffset;

    public int Root { get; }

    public BinaryPropertyListReader(ReadOnlyMemory<byte> input)
    {
        this.input = input;
        var bytes = input.Span;
        if (bytes.Length < 40 || !bytes[..8].SequenceEqual("bplist00"u8))
            throw new InvalidDataException("Expected a binary property list.");

        var trailer = bytes[^32..];
        int offsetSize = trailer[6];
        referenceSize = trailer[7];
        ulong count = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        ulong root = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        ulong table = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
        if (offsetSize is < 1 or > 8 || referenceSize is < 1 or > 8 || count is < 1 or > 4096 ||
            root >= count || table < 8 || table > (ulong)(bytes.Length - 32) ||
            count * (ulong)offsetSize > (ulong)(bytes.Length - 32) - table)
            throw new InvalidDataException("Invalid property list trailer.");

        Root = (int)root;
        tableOffset = (int)table;
        offsets = new int[(int)count];
        for (int index = 0; index < offsets.Length; index++)
        {
            ulong offset = ReadUnsigned(bytes.Slice(tableOffset + index * offsetSize, offsetSize));
            if (offset < 8 || offset >= table)
                throw new InvalidDataException("Invalid property list object offset.");
            offsets[index] = (int)offset;
        }
    }

    public int? Find(int dictionary, string key)
    {
        var references = GetCollection(dictionary, 0xd0, 2);
        int half = references.Length / 2;
        for (int offset = 0; offset < half; offset += referenceSize)
        {
            if (ReadString(ReadReference(references.Slice(offset, referenceSize))) == key)
                return ReadReference(references.Slice(half + offset, referenceSize));
        }
        return null;
    }

    public int[] ReadArray(int index)
    {
        var references = GetCollection(index, 0xa0, 1);
        var result = new int[references.Length / referenceSize];
        for (int item = 0; item < result.Length; item++)
            result[item] = ReadReference(references.Slice(item * referenceSize, referenceSize));
        return result;
    }

    public string ReadString(int index)
    {
        var value = GetObject(index);
        int kind = value[0] & 0xf0;
        if (kind is not (0x50 or 0x60 or 0x70))
            throw new InvalidDataException("Expected a property list string.");
        var contents = ReadSized(value, kind == 0x60 ? 2 : 1);
        return (kind == 0x60 ? Encoding.BigEndianUnicode : Encoding.UTF8).GetString(contents);
    }

    public ReadOnlySpan<byte> ReadData(int index)
    {
        var value = GetObject(index);
        if ((value[0] & 0xf0) != 0x40)
            throw new InvalidDataException("Expected a property list data field.");
        return ReadSized(value, 1);
    }

    public ulong ReadInteger(int index) => ReadIntegerValue(GetObject(index), out _);

    private ReadOnlySpan<byte> GetCollection(int index, int kind, int groups)
    {
        var value = GetObject(index);
        if ((value[0] & 0xf0) != kind)
            throw new InvalidDataException("Unexpected property list collection type.");
        return ReadSized(value, groups * referenceSize);
    }

    private ReadOnlySpan<byte> GetObject(int index) => input.Span[offsets[index]..tableOffset];

    private int ReadReference(ReadOnlySpan<byte> bytes)
    {
        ulong reference = ReadUnsigned(bytes);
        if (reference >= (ulong)offsets.Length)
            throw new InvalidDataException("Invalid property list reference.");
        return (int)reference;
    }

    private static ReadOnlySpan<byte> ReadSized(ReadOnlySpan<byte> value, int width)
    {
        ulong count = (uint)(value[0] & 15);
        int headerSize = 1;
        if (count == 15)
        {
            count = ReadIntegerValue(value[1..], out int integerSize);
            headerSize += integerSize;
        }
        if (count > (ulong)(value.Length - headerSize) / (ulong)width)
            throw new InvalidDataException("Truncated property list value.");
        return value.Slice(headerSize, (int)count * width);
    }

    private static ulong ReadIntegerValue(ReadOnlySpan<byte> value, out int totalSize)
    {
        if (value.IsEmpty || value[0] is < 0x10 or > 0x14)
            throw new InvalidDataException("Invalid property list integer.");
        int size = 1 << (value[0] & 15);
        totalSize = size + 1;
        if (value.Length < totalSize || (size == 16 && ReadUnsigned(value.Slice(1, 8)) != 0))
            throw new InvalidDataException("Property list integer exceeds 64 bits.");
        int outputSize = Math.Min(size, 8);
        return ReadUnsigned(value.Slice(totalSize - outputSize, outputSize));
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> value)
    {
        ulong result = 0;
        foreach (byte part in value)
            result = (result << 8) | part;
        return result;
    }
}
