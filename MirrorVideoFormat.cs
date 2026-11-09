using System.Buffers.Binary;
using System.IO;

namespace NotAirplay;

internal sealed record MirrorVideoFormat(string Decoder, int LengthWidth, byte[] ParameterSets)
{
    private static ReadOnlySpan<byte> StartCode => "\0\0\0\x01"u8;

    public static MirrorVideoFormat Read(ReadOnlySpan<byte> payload)
    {
        using var annexB = new MemoryStream();
        bool hevc = payload.Length >= 8 && payload.Slice(4, 4).SequenceEqual("hvc1"u8);
        int offset;
        int lengthWidth;
        if (hevc)
        {
            int box = payload.IndexOf("hvcC"u8);
            int start = box >= 0 ? box + 4 : 0x75 - 23;
            if (start + 23 > payload.Length)
                throw new InvalidDataException("Truncated HEVC configuration.");
            lengthWidth = (payload[start + 21] & 3) + 1;
            int arrays = payload[start + 22];
            offset = start + 23;
            for (int index = 0; index < arrays; index++)
            {
                if (offset + 3 > payload.Length)
                    throw new InvalidDataException("Truncated HEVC parameter array.");
                offset++;
                int count = ReadLength(payload, ref offset);
                CopyParameters(payload, ref offset, count, annexB);
            }
        }
        else
        {
            if (payload.Length < 7 || payload[0] != 1)
                throw new InvalidDataException("Invalid AVC configuration.");
            lengthWidth = (payload[4] & 3) + 1;
            offset = 6;
            CopyParameters(payload, ref offset, payload[5] & 31, annexB);
            if (offset >= payload.Length)
                throw new InvalidDataException("Missing AVC picture parameters.");
            int pictureCount = payload[offset++];
            CopyParameters(payload, ref offset, pictureCount, annexB);
        }
        if (annexB.Length == 0)
            throw new InvalidDataException("No video parameters were sent.");
        return new(hevc ? "hevc" : "h264", lengthWidth, annexB.ToArray());
    }

    public ReadOnlyMemory<byte> ToAnnexB(Memory<byte> data)
    {
        int offset = 0;
        using var converted = LengthWidth == 4 ? null : new MemoryStream(data.Length);
        while (offset < data.Length)
        {
            var remaining = data.Span[offset..];
            if (remaining.Length < LengthWidth)
                throw new InvalidDataException("Truncated video unit length.");
            uint length = 0;
            for (int index = 0; index < LengthWidth; index++)
                length = (length << 8) | remaining[index];
            if (length == 0 || length > remaining.Length - LengthWidth)
                throw new InvalidDataException("Invalid video unit length.");
            if (converted is null)
                StartCode.CopyTo(remaining);
            else
            {
                converted.Write(StartCode);
                converted.Write(remaining.Slice(LengthWidth, (int)length));
            }
            offset += LengthWidth + (int)length;
        }
        return converted is null ? data : converted.ToArray();
    }

    private static void CopyParameters(ReadOnlySpan<byte> payload, ref int offset, int count, Stream destination)
    {
        for (int index = 0; index < count; index++)
        {
            int length = ReadLength(payload, ref offset);
            if (length == 0 || length > payload.Length - offset)
                throw new InvalidDataException("Invalid video parameter length.");
            destination.Write(StartCode);
            destination.Write(payload.Slice(offset, length));
            offset += length;
        }
    }

    private static int ReadLength(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (payload.Length - offset < 2)
            throw new InvalidDataException("Truncated video parameter length.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;
        return length;
    }
}
