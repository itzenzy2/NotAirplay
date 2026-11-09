using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NotAirplay;

// Original RAOP UDP adapter. FFplay supplies AAC decoding and Windows audio output.
internal sealed class MirrorAudio : IAsyncDisposable
{
    private readonly UdpClient data, control, output = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource stop = new();
    private readonly byte[] key, iv;
    private readonly Aes aesCipher = Aes.Create();
    private readonly IPAddress peer;
    private readonly IPEndPoint sink;
    private readonly IPEndPoint? resendPeer;
    private readonly Process player;
    private readonly string description;
    private readonly Action<string> report;
    private readonly Task receiving, controls, errors;
    private readonly NativeAlacDecoder? alacDecoder;
    private readonly byte[] pcmBuffer = new byte[4096 * 4];
    private byte[] decryptBuffer = new byte[4096];
    private ushort? expected;
    private ushort resendSequence;
    private ushort lastSeq;
    private bool hasSeq;
    private ulong seqMask;
    private readonly object packetLock = new();
    private long packets;
    public int DataPort => ((IPEndPoint)data.Client.LocalEndPoint!).Port;
    public int ControlPort => ((IPEndPoint)control.Client.LocalEndPoint!).Port;
    public ulong Codec { get; }
    public bool Muted { get; set; }
    private float currentVolume = 1.0f;
    public float Volume => currentVolume;
    public int PlayerId => player.Id;

    public void SetVolume(float volume)
    {
        currentVolume = Math.Clamp(volume, 0f, 1f);
        try
        {
            if (!player.HasExited)
            {
                bool ok = WasapiVolume.SetProcessVolume(player.Id, Muted ? 0f : currentVolume);
                if (!ok)
                {
                    Task.Delay(150).ContinueWith(_ =>
                    {
                        try { if (!player.HasExited) WasapiVolume.SetProcessVolume(player.Id, Muted ? 0f : currentVolume); }
                        catch { }
                    });
                }
            }
        }
        catch { }
    }

    public MirrorAudio(IPAddress local, IPAddress peer, byte[] key, byte[] iv, ulong codec, int remoteControl, Action<string> report)
    {
        if (codec is not (2 or 4 or 8)) throw new InvalidDataException($"Audio codec {codec} is not supported yet (ALAC/AAC-LC/ELD required).");
        Codec = codec;
        if (iv.Length != 16) throw new InvalidDataException("Audio requires a 16-byte session IV.");
        this.peer = peer; this.key = key.ToArray(); this.iv = iv.ToArray(); this.report = report;
        aesCipher.Key = this.key;
        resendPeer = remoteControl > 0 ? new IPEndPoint(peer, remoteControl) : null;
        string executable = Path.Combine(Path.GetDirectoryName(VideoDecoder.FindExecutable())!, "ffplay.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("Audio needs ffplay.exe beside ffmpeg.exe.");
        data = new UdpClient(new IPEndPoint(local, 0));
        control = new UdpClient(new IPEndPoint(local, 0));
        sink = AllocateSink();
        description = codec == 2 ? "" : Path.Combine(Path.GetTempPath(), $"notairplay-audio-{Guid.NewGuid():N}.sdp");
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            if (codec == 2)
            {
                alacDecoder = new NativeAlacDecoder(16, 2, 352);
                string[] args = ["-nodisp", "-loglevel", "warning", "-f", "s16le", "-ch_layout", "stereo", "-sample_rate", "44100",
                    "-i", $"udp://127.0.0.1:{sink.Port}?listen=1"];
                foreach (string arg in args) start.ArgumentList.Add(arg);
            }
            else
            {
                string config = codec == 8 ? "f8e85000" : "1210";
                File.WriteAllText(description, $"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=NotAirplay\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {sink.Port} RTP/AVP 97\r\na=rtpmap:97 MPEG4-GENERIC/44100/2\r\na=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;config={config};sizeLength=13;indexLength=3;indexDeltaLength=3\r\n");
                string[] args = ["-nodisp", "-loglevel", "warning", "-localaddr", "127.0.0.1", "-probesize", "32", "-analyzeduration", "0",
                    "-max_delay", "200000", "-protocol_whitelist", "file,udp,rtp", "-i", description];
                foreach (string arg in args) start.ArgumentList.Add(arg);
            }
            player = Process.Start(start) ?? throw new InvalidOperationException("Could not start audio output.");
            receiving = ReceiveAsync(data, false);
            controls = ReceiveAsync(control, true);
            errors = ReadErrorsAsync();
        }
        catch
        {
            data.Dispose(); control.Dispose(); output.Dispose(); stop.Dispose(); aesCipher.Dispose();
            alacDecoder?.Dispose();
            CryptographicOperations.ZeroMemory(this.key); CryptographicOperations.ZeroMemory(this.iv);
            if (!string.IsNullOrEmpty(description)) File.Delete(description);
            throw;
        }
        string codecName = codec switch { 2 => "ALAC (lossless)", 8 => "AAC-ELD", _ => "AAC-LC" };
        report($"Audio ready: {codecName}, 44.1 kHz stereo.");
    }

    internal static byte[] DecodePacket(ReadOnlySpan<byte> packet, byte[] key, byte[] iv, Aes? cipher = null)
    {
        if (packet.Length < 13 || packet.Length > 8191 || packet[0] != 0x80)
            throw new InvalidDataException("Unsupported audio RTP header.");
        int length = packet.Length - 12, encrypted = length & ~15;
        byte[] result = new byte[packet.Length + 4];
        packet[..12].CopyTo(result); result[1] = (byte)((result[1] & 128) | 97);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(14), (ushort)(length << 3));
        if (encrypted > 0)
        {
            if (cipher is not null)
                cipher.DecryptCbc(packet.Slice(12, encrypted), iv, result.AsSpan(16, encrypted), PaddingMode.None);
            else
            {
                using var aes = Aes.Create(); aes.Key = key;
                aes.DecryptCbc(packet.Slice(12, encrypted), iv, result.AsSpan(16, encrypted), PaddingMode.None);
            }
        }
        packet[(12 + encrypted)..].CopyTo(result.AsSpan(16 + encrypted));
        return result;
    }

    private static int nextSinkPort = 42100;
    private static IPEndPoint AllocateSink()
    {
        for (int i = 0; i < 200; i++)
        {
            int p = Interlocked.Add(ref nextSinkPort, 2);
            if (p > 50000) Interlocked.Exchange(ref nextSinkPort, 42100);
            try
            {
                using var test1 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                test1.ExclusiveAddressUse = false;
                test1.Bind(new IPEndPoint(IPAddress.Loopback, p));

                using var test2 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                test2.ExclusiveAddressUse = false;
                test2.Bind(new IPEndPoint(IPAddress.Loopback, p + 1));

                return new IPEndPoint(IPAddress.Loopback, p);
            }
            catch { }
        }
        return new IPEndPoint(IPAddress.Loopback, 42100);
    }

    private async Task ReceiveAsync(UdpClient socket, bool isControl)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(stop.Token).ConfigureAwait(false);
                if (!received.RemoteEndPoint.Address.Equals(peer)) continue;
                byte[] bytes = received.Buffer;
                if (isControl)
                {
                    if (bytes.Length < 17 || (bytes[1] & 127) != 86) continue;
                    bytes = bytes[4..]; // Retransmitted RTP packet.
                }
                if (bytes.Length < 13 || (bytes[1] & 127) != 96) continue;
                if (bytes.Length == 16 && bytes[12] == 0x00 && bytes[13] == 0x68 && bytes[14] == 0x34 && bytes[15] == 0x00)
                    continue; // Skip stream startup dummy marker
                ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2));
                lock (packetLock)
                {
                    if (!isControl)
                    {
                        if (hasSeq)
                        {
                            short diff = (short)(sequence - lastSeq);
                            if (diff > 0)
                            {
                                if (diff < 64) seqMask = (seqMask << diff) | 1UL;
                                else seqMask = 1UL;
                                lastSeq = sequence;
                            }
                            else if (diff > -64)
                            {
                                int bit = -diff;
                                if ((seqMask & (1UL << bit)) != 0)
                                    continue; // Duplicate packet from iOS redundant transmission; ignore!
                                seqMask |= (1UL << bit);
                            }
                            else
                            {
                                continue; // Stale packet; ignore!
                            }
                        }
                        else
                        {
                            hasSeq = true;
                            lastSeq = sequence;
                            seqMask = 1UL;
                        }

                        if (expected is ushort next && resendPeer is not null)
                        {
                            int gap = (ushort)(sequence - next);
                            if (gap is > 0 and <= 32)
                            {
                                byte[] request = [0x80, 0xd5, 0, 0, 0, 0, 0, 0];
                                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), resendSequence++);
                                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), next);
                                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)gap);
                                control.Send(request, resendPeer);
                            }
                        }
                        if (expected is null || (short)(sequence + 1 - expected.Value) > 0)
                            expected = (ushort)(sequence + 1);
                    }
                    if (!Muted)
                    {
                        if (Codec == 2 && alacDecoder is not null)
                        {
                            int len = bytes.Length - 12;
                            if (len > 0)
                            {
                                int enc = len & ~15;
                                if (decryptBuffer.Length < len)
                                    decryptBuffer = new byte[len + 256];
                                if (enc > 0)
                                    aesCipher.DecryptCbc(bytes.AsSpan(12, enc), iv, decryptBuffer.AsSpan(0, enc), PaddingMode.None);
                                bytes.AsSpan(12 + enc).CopyTo(decryptBuffer.AsSpan(enc));
                                int decoded = alacDecoder.Decode(decryptBuffer.AsSpan(0, len), pcmBuffer);
                                if (decoded > 0)
                                    output.Send(pcmBuffer.AsSpan(0, decoded), sink);
                            }
                        }
                        else
                        {
                            byte[] decoded = DecodePacket(bytes, key, iv, aesCipher);
                            output.Send(decoded, sink);
                        }
                    }
                }
                if (Interlocked.Increment(ref packets) == 1)
                {
                    report("First audio packet decrypted and sent to the playback device.");
                    SetVolume(currentVolume);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or InvalidDataException or CryptographicException)
        { if (!stop.IsCancellationRequested) report($"Audio stream error: {ex.Message}"); }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            int lines = 0;
            while (await player.StandardError.ReadLineAsync(stop.Token).ConfigureAwait(false) is { } line)
                if (lines++ < 6) report("Audio: " + line[..Math.Min(180, line.Length)]);
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            if (!player.HasExited) player.Kill();
            await Task.WhenAll(receiving, controls, errors).ConfigureAwait(false);
            await player.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            alacDecoder?.Dispose();
            player.Dispose(); data.Dispose(); control.Dispose(); output.Dispose(); stop.Dispose(); aesCipher.Dispose();
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(iv);
            try { if (!string.IsNullOrEmpty(description) && File.Exists(description)) File.Delete(description); } catch { }
            report($"Audio stopped. {Interlocked.Read(ref packets)} packets received.");
        }
    }
}
