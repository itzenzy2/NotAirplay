using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;

namespace NotAirplay;

internal sealed class MdnsRecords
{
    private const ushort Address = 1, Ptr = 12, Txt = 16, Srv = 33, Any = 255;
    private const string Services = "_services._dns-sd._udp.local";
    private const int HeaderSize = 12;
    private const uint Lifetime = 120;
    private readonly Record[] published;
    private readonly Record[] serviceTypes;

    public MdnsRecords(ReceiverIdentity identity, IPAddress address, ushort port)
    {
        Record[] ForService(DiscoveryService service)
        {
            using var target = new MemoryStream();
            Write16(target, 0);
            Write16(target, 0);
            Write16(target, port);
            WriteName(target, identity.HostName);
            return [
                new(service.Type, Ptr, NameBytes(service.FullName)),
                new(service.FullName, Srv, target.ToArray()),
                new(service.FullName, Txt, service.EncodeTxt()),
            ];
        }

        published = [.. ForService(identity.AirPlay), .. ForService(identity.Raop),
            new(identity.HostName, Address, address.GetAddressBytes())];
        serviceTypes = [new(Services, Ptr, NameBytes(identity.AirPlay.Type)),
            new(Services, Ptr, NameBytes(identity.Raop.Type))];
    }

    public byte[] Announcement(bool goodbye = false) => Build(published, [], [], 0, goodbye ? 0 : Lifetime, true);

    public byte[]? Answer(ReadOnlySpan<byte> packet, bool legacy, out bool unicast)
    {
        unicast = false;
        if (packet.Length < HeaderSize || (Read16(packet, 2) & 0xf800) != 0 || Read16(packet, 4) is 0 or > 16)
            return null;

        int offset = HeaderSize;
        var questions = new List<Question>();
        var answers = new HashSet<Record>();
        var additional = new HashSet<Record>();
        bool multicastRequested = false;
        for (int index = 0; index < Read16(packet, 4); index++)
        {
            string? name = ReadName(packet, ref offset);
            if (name is null || offset + 4 > packet.Length)
                return null;

            var question = new Question(name, Read16(packet, offset), Read16(packet, offset + 2));
            offset += 4;
            questions.Add(question);
            if ((question.Class & 0x7fff) is not (1 or Any))
                continue;

            var candidates = name.Equals(Services, StringComparison.OrdinalIgnoreCase) ? serviceTypes : published;
            foreach (var record in candidates)
            {
                if (!name.Equals(record.Name, StringComparison.OrdinalIgnoreCase) ||
                    (question.Type != Any && question.Type != record.Type))
                    continue;
                answers.Add(record);
                multicastRequested |= (question.Class & 0x8000) == 0;
                if (candidates == published && record.Type == Ptr)
                {
                    // A service browse needs the endpoint and its host address as well as the instance name.
                    int start = record == published[0] ? 1 : 4;
                    additional.Add(published[start]);
                    additional.Add(published[start + 1]);
                    additional.Add(published[^1]);
                }
                else if (record.Type == Srv)
                {
                    additional.Add(published[^1]);
                }
            }
        }

        if (answers.Count == 0)
            return null;

        additional.ExceptWith(answers);
        unicast = legacy || !multicastRequested;
        return Build(answers, additional, legacy ? questions : [], legacy ? Read16(packet, 0) : (ushort)0,
            legacy ? 10 : Lifetime, !legacy);
    }

    private static byte[] Build(IReadOnlyCollection<Record> answers, IReadOnlyCollection<Record> additional,
        IReadOnlyCollection<Question> questions, ushort id, uint lifetime, bool cacheFlush)
    {
        using var output = new MemoryStream();
        Write16(output, id);
        Write16(output, 0x8400); // Authoritative response.
        Write16(output, (ushort)questions.Count);
        Write16(output, (ushort)answers.Count);
        Write16(output, 0);
        Write16(output, (ushort)additional.Count);
        foreach (var question in questions)
        {
            WriteName(output, question.Name);
            Write16(output, question.Type);
            Write16(output, (ushort)(question.Class & 0x7fff));
        }
        Span<byte> ttl = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ttl, lifetime);
        foreach (var record in answers.Concat(additional))
        {
            WriteName(output, record.Name);
            Write16(output, record.Type);
            Write16(output, (ushort)(cacheFlush && record.Type != Ptr ? 0x8001 : 1));
            output.Write(ttl);
            Write16(output, (ushort)record.Data.Length);
            output.Write(record.Data);
        }
        return output.ToArray();
    }

    private static string? ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var name = new StringBuilder();
        int cursor = offset;
        int nameBytes = 0;
        bool jumped = false;
        for (int step = 0; step < 128 && cursor < packet.Length; step++)
        {
            int size = packet[cursor++];
            if (size == 0)
            {
                if (!jumped)
                    offset = cursor;
                return name.ToString();
            }
            if ((size & 0xc0) == 0xc0 && cursor < packet.Length)
            {
                int target = ((size & 0x3f) << 8) | packet[cursor++];
                if (target < HeaderSize || target >= cursor - 2)
                    return null;
                if (!jumped)
                    offset = cursor;
                jumped = true;
                cursor = target;
                continue;
            }
            if (size > 63 || cursor + size > packet.Length || (nameBytes += size + 1) >= 255)
                return null;
            if (name.Length > 0)
                name.Append('.');
            var label = packet.Slice(cursor, size);
            if (label.Contains((byte)'.') || label.Contains((byte)0))
                return null;
            name.Append(Encoding.UTF8.GetString(label));
            cursor += size;
        }
        return null;
    }

    private static byte[] NameBytes(string name)
    {
        using var bytes = new MemoryStream();
        WriteName(bytes, name);
        return bytes.ToArray();
    }

    private static void WriteName(Stream output, string name)
    {
        foreach (string label in name.Split('.'))
        {
            if (label.Length == 0)
                break;
            var bytes = Encoding.UTF8.GetBytes(label);
            output.WriteByte((byte)bytes.Length);
            output.Write(bytes);
        }
        output.WriteByte(0);
    }

    private static ushort Read16(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);

    private static void Write16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        output.Write(bytes);
    }

    private sealed record Record(string Name, ushort Type, byte[] Data);
    private sealed record Question(string Name, ushort Type, ushort Class);
}
