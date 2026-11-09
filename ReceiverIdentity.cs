using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace NotAirplay;

internal sealed class ReceiverIdentity
{
    public const string ServiceName = "NotAirplay";
    private const string Model = "AppleTV3,2";
    private const string ProtocolVersion = "1.0";
    public const string SourceVersion = "220.68";
    // Legacy discovery profile with screen multicodec negotiation and audio enabled.
    private const uint LegacyFeatures = 0x527FFFF7;
    private const uint ExtendedFeatures = 0x400;
    private const uint StatusFlags = 0x4;

    public string Name => ServiceName;
    public string HostName { get; }
    public DiscoveryService AirPlay { get; }
    public DiscoveryService Raop { get; }
    public byte[] Info => BinaryPropertyList.Write(CreateInfoFields());
    private readonly Func<int> displayHeight;
    public byte[] ServerInfo { get; }

    public ReceiverIdentity(Func<int>? displayHeight = null)
    {
        this.displayHeight = displayHeight ?? (() => 1080);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"NotAirplay/{Environment.MachineName}"));
        hash[0] = (byte)((hash[0] & 0xfc) | 0x02);
        var deviceIdBytes = hash.AsSpan(0, 6);
        string deviceId = Convert.ToHexString(deviceIdBytes);
        string deviceAddress = string.Join(":", deviceId.Chunk(2).Select(pair => new string(pair)));
        string pairingId = new Guid(hash.AsSpan(6, 16)).ToString();
        string features = $"0x{LegacyFeatures:X},0x{ExtendedFeatures:X}";
        string flags = $"0x{StatusFlags:X}";
        ulong featureMask = ((ulong)ExtendedFeatures << 32) | LegacyFeatures;
        HostName = $"notairplay-{deviceId.ToLowerInvariant()}.local";
        AirPlay = new DiscoveryService(Name, "_airplay._tcp.local", new Dictionary<string, string>
        {
            ["deviceid"] = deviceAddress,
            ["features"] = features,
            ["model"] = Model,
            ["protovers"] = ProtocolVersion,
            ["srcvers"] = SourceVersion,
            ["flags"] = flags,
            ["pi"] = pairingId,
            ["vv"] = "2",
        });
        Raop = new DiscoveryService($"{deviceId}@{Name}", "_raop._tcp.local", new Dictionary<string, string>
        {
            ["txtvers"] = "1",
            ["ch"] = "2",
            ["cn"] = "0,1,2,3",
            ["et"] = "0,3,5",
            ["ft"] = features,
            ["am"] = Model,
            ["sf"] = flags,
            ["pi"] = pairingId,
            ["da"] = "true",
            ["tp"] = "UDP",
            ["vn"] = "65537",
            ["vs"] = SourceVersion,
            ["vv"] = "2",
        });

        var plist = new XElement("plist", new XAttribute("version", "1.0"),
            new XElement("dict", new XElement("key", "deviceid"), new XElement("string", deviceAddress),
                new XElement("key", "model"), new XElement("string", Model),
                new XElement("key", "protovers"), new XElement("string", ProtocolVersion),
                new XElement("key", "srcvers"), new XElement("string", SourceVersion),
                new XElement("key", "features"), new XElement("integer", featureMask)));
        ServerInfo = Encoding.UTF8.GetBytes(plist.ToString(SaveOptions.DisableFormatting));
    }

    public Dictionary<string, object> CreateInfoFields() => new()
    {
        ["deviceID"] = AirPlay.Properties["deviceid"],
        ["macAddress"] = AirPlay.Properties["deviceid"],
        ["features"] = ((ulong)ExtendedFeatures << 32) | LegacyFeatures,
        ["model"] = Model,
        ["name"] = Name,
        ["protocolVersion"] = ProtocolVersion,
        ["sourceVersion"] = SourceVersion,
        ["statusFlags"] = (ulong)StatusFlags,
        ["pi"] = AirPlay.Properties["pi"],
        ["vv"] = 2UL,
        ["keepAliveLowPower"] = true,
        ["txtAirPlay"] = AirPlay.EncodeTxt(),
        ["displays"] = new object[]
        {
            new Dictionary<string, object>
            {
                ["uuid"] = AirPlay.Properties["pi"],
                ["widthPhysical"] = 0UL,
                ["heightPhysical"] = 0UL,
                ["width"] = (ulong)(displayHeight() * 16 / 9),
                ["height"] = (ulong)displayHeight(),
                ["widthPixels"] = (ulong)(displayHeight() * 16 / 9),
                ["heightPixels"] = (ulong)displayHeight(),
                ["rotation"] = false,
                ["overscanned"] = false,
                ["maxFPS"] = 60UL,
                ["features"] = 14UL,
            },
        },
    };
}

internal sealed record DiscoveryService(string Name, string Type, IReadOnlyDictionary<string, string> Properties)
{
    public string FullName => $"{Name}.{Type}";

    public byte[] EncodeTxt()
    {
        using var output = new MemoryStream();
        foreach (var (key, value) in Properties)
        {
            byte[] field = Encoding.ASCII.GetBytes($"{key}={value}");
            output.WriteByte(checked((byte)field.Length));
            output.Write(field);
        }
        return output.ToArray();
    }
}
