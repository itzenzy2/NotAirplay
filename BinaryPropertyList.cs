using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace NotAirplay;

internal static class BinaryPropertyList
{
    public static byte[] Write(IReadOnlyDictionary<string, object> fields)
    {
        using var output = new MemoryStream();
        var objects = new List<byte[]>();
        byte root = AddObject(fields);
        var offsets = new List<ushort>();
        output.Write("bplist00"u8);
        foreach (var value in objects)
        {
            offsets.Add(checked((ushort)output.Position));
            output.Write(value);
        }

        ulong offsetTable = (ulong)output.Position;
        Span<byte> offsetBytes = stackalloc byte[2];
        foreach (ushort offset in offsets)
        {
            BinaryPrimitives.WriteUInt16BigEndian(offsetBytes, offset);
            output.Write(offsetBytes);
        }

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = 2; // Bytes per offset.
        trailer[7] = 1; // Bytes per object reference.
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)offsets.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], root);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], offsetTable);
        output.Write(trailer);
        return output.ToArray();

        byte AddObject(object value)
        {
            using var bytes = new MemoryStream();
            switch (value)
            {
                case string text when text.All(char.IsAscii):
                    WriteMarker(bytes, 0x50, text.Length);
                    bytes.Write(Encoding.ASCII.GetBytes(text));
                    break;
                case byte[] data:
                    WriteMarker(bytes, 0x40, data.Length);
                    bytes.Write(data);
                    break;
                case ulong number:
                    WriteInteger(bytes, number);
                    break;
                case bool flag:
                    bytes.WriteByte(flag ? (byte)9 : (byte)8);
                    break;
                case IReadOnlyDictionary<string, object> dictionary:
                    var entries = dictionary.ToArray();
                    WriteMarker(bytes, 0xd0, entries.Length);
                    foreach (var entry in entries)
                        bytes.WriteByte(AddObject(entry.Key));
                    foreach (var entry in entries)
                        bytes.WriteByte(AddObject(entry.Value));
                    break;
                case object[] array:
                    WriteMarker(bytes, 0xa0, array.Length);
                    foreach (var item in array)
                        bytes.WriteByte(AddObject(item));
                    break;
                default:
                    throw new ArgumentException("Unsupported receiver metadata value.", nameof(fields));
            }
            byte index = checked((byte)objects.Count);
            objects.Add(bytes.ToArray());
            return index;
        }
    }

    private static void WriteMarker(Stream output, byte type, int length)
    {
        output.WriteByte((byte)(type | Math.Min(length, 15)));
        if (length >= 15)
            WriteInteger(output, (ulong)length);
    }

    private static void WriteInteger(Stream output, ulong value)
    {
        bool wide = value > long.MaxValue;
        output.WriteByte(wide ? (byte)0x14 : (byte)0x13);
        if (wide)
            output.Write("\0\0\0\0\0\0\0\0"u8);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        output.Write(bytes);
    }
}
