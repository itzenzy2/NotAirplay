using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace NotAirplay;

internal static class FairPlayKeyRecovery
{
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "notairplay-key.exe");

    public static async Task<byte[]> RecoverAsync(byte[] handshake, byte[] wrapped, CancellationToken token)
    {
        if (handshake.Length != 164 || wrapped.Length != 72)
            throw new InvalidDataException("Invalid FairPlay key input size.");
        if (!File.Exists(HelperPath))
            throw new InvalidOperationException("FairPlay key helper is missing. Keep notairplay-key.exe beside NotAirplay.exe.");

        using var process = Process.Start(new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("Could not start the FairPlay key helper.");

        byte[] key = new byte[16];
        bool recovered = false;
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(handshake, token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(wrapped, token).ConfigureAwait(false);
            process.StandardInput.Close();
            int bytesRead = await process.StandardOutput.BaseStream.ReadAtLeastAsync(key, key.Length, false, token)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode == 3)
                throw new InvalidDataException("Unsupported FairPlay key frame header.");
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FairPlay key helper exited with code {process.ExitCode}.");
            if (bytesRead != key.Length)
                throw new IOException("FairPlay key helper returned an incomplete key.");
            recovered = true;
            return key;
        }
        finally
        {
            if (!recovered)
                CryptographicOperations.ZeroMemory(key);
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
            }
        }
    }
}
