using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NotAirplay;

// The legacy wire exchange does not authenticate the peer. The challenge payloads
// below are from the packet traces in README.md.
internal sealed class FairPlayProbe : IDisposable
{
    private const int HeaderSize = 12;
    public const int MaxRequestSize = 164;
    private const int ChallengeTagSize = 20;
    private int? pendingMode;
    public byte[]? KeyMessage { get; private set; }

    private static readonly byte[][] Challenges =
    [
        Convert.FromHexString(
            "0f9f3f9e0a2521dbdf312ab2bfb29e8d232b6376a8c818701d22ae93d82737fe" +
            "af9db4fdf41c2dba9d1f49caaabf6591ac1f7bc6f7e0663d21afe01565953eab" +
            "81f418ceed095adb7c3d0e254909a79831d49c3982973434facb42c63a1cd911" +
            "a6fe941a8a6d4a743b46c3a7649e44c78955e49d8155009549c4e2f7a3f6d5ba"),
        Convert.FromHexString(
            "cf32a25714b2524f8aa0ad7af164e37bcf4424e200047efc0ad67afcd95ded1c" +
            "2730bb591b962ed63a9c4ded88ba8fc78de64d91ccfd5c7b56da88e31f5cceaf" +
            "c7431995a01665a54e1939d25b94db64b9e45d8d063e1e6af07e9656162b0efa" +
            "404275ea5a44d9591c7256b9fbe6513898b80227721988571650942ad946688a"),
        Convert.FromHexString(
            "c169a352eeed35b18cdd9c58d64f16c1519a89eb5317bd0d4336cd68f638ff9d" +
            "016a5b52b7fa9216b2b65482c78444118121a2c7fed83db7119e9182aad7d18c" +
            "7063e2a457555910af9e0efc76347d164043807f581ee4fbe42ca9dedc1b5eb2" +
            "a3aa3d2ecd59e7eee70b3629f22afd161d877353ddb99adc8e07006e56f850ce"),
        Convert.FromHexString(
            "9001e1727e0f57f9f5880db104a6257a23f5cfff1abbe1e93045251afb97eb9f" +
            "c0011ebe0f3a81df5b691d76acb2f7a5c708e3d328f56bb39dbde5f29c8a17f4" +
            "81487e3ae863c678325422e6f78e166d18aa7fd636258bce28726f661f738893" +
            "ce44311e4be6c0535193e5ef72e8686233729c227d820c999445d89246c8c359"),
    ];

    public (string Status, byte[]? Payload, string Diagnostic) Reply(ReadOnlySpan<byte> request, int length)
    {
        if (request.Length < HeaderSize || !request[..4].SequenceEqual("FPLY"u8))
            return ("400 Bad Request", null, $"FairPlay frame missing or invalid ({length} bytes).");

        if (request[4] != 3 || request[5] != 1)
            return ("501 Not Implemented", null, $"FairPlay version {request[4]}.{request[5]} is unsupported.");

        if (request[7] != 0 || BinaryPrimitives.ReadUInt32BigEndian(request[8..]) != request.Length - HeaderSize)
            return ("400 Bad Request", null, "FairPlay frame length or reserved field is invalid.");

        if (request[6] == 1 && request.Length == 16 && request[12] == 2 && request[13] == 0 && request[15] == 0xbb)
        {
            int mode = request[14];
            if (mode >= Challenges.Length)
                return ("501 Not Implemented", null, $"FairPlay mode {mode} is unsupported.");

            var response = Frame(type: 2, 2 + Challenges[mode].Length);
            response[12] = 2;
            response[13] = (byte)mode;
            Challenges[mode].CopyTo(response, 14);
            Dispose();
            pendingMode = mode;
            return ("200 OK", response, $"FairPlay v3.1 mode {mode} challenge sent.");
        }

        if (request[6] == 3 && request.Length == MaxRequestSize && request[12] == pendingMode)
        {
            var response = Frame(type: 4, ChallengeTagSize);
            request[^ChallengeTagSize..].CopyTo(response.AsSpan(HeaderSize));
            pendingMode = null;
            KeyMessage = request.ToArray();
            return ("200 OK", response, "FairPlay wire acknowledgement sent. Waiting for stream SETUP.");
        }

        return ("400 Bad Request", null, $"Unexpected FairPlay message {request[6]} ({length} bytes).");
    }

    public void Dispose()
    {
        if (KeyMessage is not null)
        {
            CryptographicOperations.ZeroMemory(KeyMessage);
            KeyMessage = null;
        }
    }

    private static byte[] Frame(byte type, int payloadLength)
    {
        var frame = new byte[HeaderSize + payloadLength];
        "FPLY"u8.CopyTo(frame);
        frame[4] = 3;
        frame[5] = 1;
        frame[6] = type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(8), payloadLength);
        return frame;
    }
}
