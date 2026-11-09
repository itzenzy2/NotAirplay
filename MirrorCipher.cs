using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NotAirplay;

internal sealed class MirrorCipher : IDisposable
{
    private readonly Aes aes = Aes.Create();
    private readonly ICryptoTransform encryptor;
    private readonly byte[] counter;
    private readonly byte[] counters = new byte[4096];
    private readonly byte[] keystream = new byte[4096];
    private int position, available;

    public MirrorCipher(byte[] mediaKey, ulong streamId)
    {
        string suffix = streamId.ToString(CultureInfo.InvariantCulture);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        var key = Derive("AirPlayStreamKey" + suffix, mediaKey);
        try
        {
            aes.Key = key;
            encryptor = aes.CreateEncryptor();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        counter = Derive("AirPlayStreamIV" + suffix, mediaKey);
    }

    public void Transform(Span<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (position == available)
            {
                available = Math.Min(counters.Length, (data.Length + 15) & ~15);
                for (int offset = 0; offset < available; offset += 16)
                {
                    counter.CopyTo(counters, offset);
                    for (int index = 15; index >= 0; index--)
                    {
                        if (++counter[index] != 0)
                            break;
                    }
                }
                encryptor.TransformBlock(counters, 0, available, keystream, 0);
                position = 0;
            }
            int count = Math.Min(data.Length, available - position);
            for (int index = 0; index < count; index++)
                data[index] ^= keystream[position + index];
            position += count;
            data = data[count..];
        }
    }

    private static byte[] Derive(string label, byte[] key)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(Encoding.ASCII.GetBytes(label));
        hash.AppendData(key);
        var digest = hash.GetHashAndReset();
        var result = digest[..16];
        CryptographicOperations.ZeroMemory(digest);
        return result;
    }

    public void Dispose()
    {
        encryptor.Dispose();
        aes.Dispose();
        CryptographicOperations.ZeroMemory(counter);
        CryptographicOperations.ZeroMemory(counters);
        CryptographicOperations.ZeroMemory(keystream);
    }
}
