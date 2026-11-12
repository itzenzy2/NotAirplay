using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NotAirplay;

// RAOP UDP receiver feeding an in-process Windows WASAPI audio engine.
// ALAC uses native C decode; AAC-LC uses Windows Media Foundation MFT; AAC-ELD pipes headless FFmpeg PCM.
internal sealed class MirrorAudio : IAsyncDisposable
{
    private readonly UdpClient data, control;
    private readonly UdpClient? output;
    private readonly CancellationTokenSource stop = new();
    private readonly byte[] key, iv;
    private readonly Aes aesCipher = Aes.Create();
    private readonly IPAddress peer;
    private readonly IPEndPoint? sink;
    private readonly IPEndPoint? resendPeer;
    private readonly Process? player;
    private readonly string description = "";
    private readonly Action<string> report;
    private readonly Task receiving, controls, pumping, errors;
    private readonly NativeAlacDecoder? alacDecoder;
    private readonly bool useNativeAacLc;
    private readonly byte[] pcmBuffer = new byte[4096 * 4];
    private byte[] decryptBuffer = new byte[4096];
    private nint nativeAudio;
    private ushort? expected;
    private ushort resendSequence;
    private ushort lastSeq;
    private bool hasSeq;
    private ulong seqMask;
    private readonly object packetLock = new();
    private long packets;
    private volatile bool muted;
    private float currentVolume = 1.0f;

    public int DataPort => ((IPEndPoint)data.Client.LocalEndPoint!).Port;
    public int ControlPort => ((IPEndPoint)control.Client.LocalEndPoint!).Port;
    public ulong Codec { get; }
    public int PlayerId => Environment.ProcessId;
    public float Volume => currentVolume;

    public bool Muted
    {
        get => muted;
        set
        {
            muted = value;
            nint h = nativeAudio;
            if (h != 0) NativeSetMute(h, value ? 1 : 0);
        }
    }

    public void SetVolume(float volume)
    {
        currentVolume = Math.Clamp(volume, 0f, 1f);
        nint h = nativeAudio;
        if (h != 0) NativeSetVolume(h, currentVolume);
    }

    public void SetDevice(string? deviceId)
    {
        nint h = nativeAudio;
        if (h != 0) NativeSetDevice(h, string.IsNullOrWhiteSpace(deviceId) ? null : deviceId);
    }

    public void Flush()
    {
        nint h = nativeAudio;
        if (h != 0) NativeFlush(h);
    }

    public MirrorAudio(IPAddress local, IPAddress peer, byte[] key, byte[] iv, ulong codec, int remoteControl,
        Action<string> report, string? outputDeviceId = null)
    {
        if (codec is not (2 or 4 or 8))
            throw new InvalidDataException($"Audio codec {codec} is not supported yet (ALAC/AAC-LC/ELD required).");
        Codec = codec;
        if (iv.Length != 16)
            throw new InvalidDataException("Audio requires a 16-byte session IV.");
        this.peer = peer;
        this.key = key.ToArray();
        this.iv = iv.ToArray();
        this.report = report;
        aesCipher.Key = this.key;
        resendPeer = remoteControl > 0 ? new IPEndPoint(peer, remoteControl) : null;

        int hr = NativeCreate(string.IsNullOrWhiteSpace(outputDeviceId) ? null : outputDeviceId,
            currentVolume, muted ? 1 : 0, out nativeAudio);
        if (hr < 0 || nativeAudio == 0)
            throw new InvalidOperationException($"Could not initialize Windows audio playback (0x{hr:X8}).");

        data = new UdpClient(new IPEndPoint(local, 0));
        control = new UdpClient(new IPEndPoint(local, 0));
        pumping = Task.CompletedTask;
        errors = Task.CompletedTask;

        try
        {
            if (codec == 2)
            {
                alacDecoder = new NativeAlacDecoder(16, 2, 352);
            }
            else if (codec == 4 && NativeSupportsAacLc() != 0)
            {
                useNativeAacLc = true;
            }
            else
            {
                string executable = VideoDecoder.FindExecutable();
                output = new UdpClient(AddressFamily.InterNetwork);
                sink = AllocateSink();
                description = Path.Combine(Path.GetTempPath(), $"notairplay-audio-{Guid.NewGuid():N}.sdp");
                string config = codec == 8 ? "f8e85000" : "1210";
                File.WriteAllText(description,
                    $"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=NotAirplay\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {sink.Port} RTP/AVP 97\r\na=rtpmap:97 MPEG4-GENERIC/44100/2\r\na=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;config={config};sizeLength=13;indexLength=3;indexDeltaLength=3\r\n");

                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                string[] args = [
                    "-hide_banner", "-loglevel", "warning", "-nostdin",
                    "-localaddr", "127.0.0.1", "-probesize", "32", "-analyzeduration", "0",
                    "-fflags", "nobuffer", "-flags", "low_delay", "-max_delay", "80000",
                    "-protocol_whitelist", "file,udp,rtp", "-i", description,
                    "-f", "s16le", "-ac", "2", "-ar", "44100", "pipe:1"
                ];
                foreach (string arg in args) start.ArgumentList.Add(arg);
                player = Process.Start(start) ?? throw new InvalidOperationException("Could not start AAC-ELD decoder.");
                pumping = PumpPipeAsync(player.StandardOutput.BaseStream);
                errors = ReadErrorsAsync(player.StandardError);
            }

            receiving = ReceiveAsync(data, false);
            controls = ReceiveAsync(control, true);
        }
        catch
        {
            data.Dispose();
            control.Dispose();
            output?.Dispose();
            stop.Dispose();
            aesCipher.Dispose();
            alacDecoder?.Dispose();
            if (nativeAudio != 0) { NativeDestroy(nativeAudio); nativeAudio = 0; }
            CryptographicOperations.ZeroMemory(this.key);
            CryptographicOperations.ZeroMemory(this.iv);
            if (!string.IsNullOrEmpty(description) && File.Exists(description)) File.Delete(description);
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
        packet[..12].CopyTo(result);
        result[1] = (byte)((result[1] & 128) | 97);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(14), (ushort)(length << 3));
        if (encrypted > 0)
        {
            if (cipher is not null)
                cipher.DecryptCbc(packet.Slice(12, encrypted), iv, result.AsSpan(16, encrypted), PaddingMode.None);
            else
            {
                using var aes = Aes.Create();
                aes.Key = key;
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

    private ReadOnlySpan<byte> DecryptPayload(ReadOnlySpan<byte> rtpPacket)
    {
        int len = rtpPacket.Length - 12;
        if (len <= 0) return ReadOnlySpan<byte>.Empty;
        int enc = len & ~15;
        if (decryptBuffer.Length < len)
            decryptBuffer = new byte[len + 256];
        if (enc > 0)
            aesCipher.DecryptCbc(rtpPacket.Slice(12, enc), iv, decryptBuffer.AsSpan(0, enc), PaddingMode.None);
        rtpPacket[(12 + enc)..].CopyTo(decryptBuffer.AsSpan(enc));
        return decryptBuffer.AsSpan(0, len);
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
                if (ProcessAudioPacket(bytes, sequence, isControl) && Interlocked.Increment(ref packets) == 1)
                {
                    report("First audio packet decrypted and sent to the playback device.");
                    SetVolume(currentVolume);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or InvalidDataException or CryptographicException)
        {
            if (!stop.IsCancellationRequested) report($"Audio stream error: {ex.Message}");
        }
    }

    private bool ProcessAudioPacket(byte[] bytes, ushort sequence, bool isControl)
    {
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
                            return false;
                        seqMask |= (1UL << bit);
                    }
                    else
                    {
                        return false;
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

            if (Codec == 2 && alacDecoder is not null)
            {
                var payload = DecryptPayload(bytes);
                if (!payload.IsEmpty)
                {
                    int decoded = alacDecoder.Decode(payload, pcmBuffer);
                    if (decoded > 0) PushPcm(pcmBuffer.AsSpan(0, decoded));
                }
            }
            else if (Codec == 4 && useNativeAacLc)
            {
                var payload = DecryptPayload(bytes);
                if (!payload.IsEmpty) PushAacLc(payload);
            }
            else if (output is not null && sink is not null)
            {
                byte[] decoded = DecodePacket(bytes, key, iv, aesCipher);
                output.Send(decoded, sink);
            }
            return true;
        }
    }

    private async Task PumpPipeAsync(Stream stdout)
    {
        byte[] buffer = new byte[4096];
        int carry = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int read = await stdout.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), stop.Token).ConfigureAwait(false);
                if (read == 0) break;
                int total = carry + read;
                int aligned = total & ~3; // 4 bytes per stereo S16 frame
                if (aligned > 0) PushPcm(buffer.AsSpan(0, aligned));
                carry = total - aligned;
                if (carry > 0) Buffer.BlockCopy(buffer, aligned, buffer, 0, carry);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private async Task ReadErrorsAsync(StreamReader stderr)
    {
        try
        {
            int lines = 0;
            while (await stderr.ReadLineAsync(stop.Token).ConfigureAwait(false) is { } line)
            {
                string clean = StripAnsi(line);
                if (!string.IsNullOrWhiteSpace(clean) && lines++ < 6)
                    report("Audio: " + clean[..Math.Min(180, clean.Length)]);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string StripAnsi(string input)
    {
        if (!input.Contains('\x1b')) return input.Trim();
        var sb = new StringBuilder(input.Length);
        bool inEscape = false;
        foreach (char c in input)
        {
            if (c == '\x1b') { inEscape = true; continue; }
            if (inEscape)
            {
                if (c is >= '@' and <= '~') inEscape = false;
                continue;
            }
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private unsafe void PushPcm(ReadOnlySpan<byte> pcm)
    {
        nint h = nativeAudio;
        if (h == 0 || pcm.IsEmpty) return;
        fixed (byte* ptr = pcm) NativePushPcm(h, ptr, (uint)pcm.Length);
    }

    private unsafe void PushAacLc(ReadOnlySpan<byte> frame)
    {
        nint h = nativeAudio;
        if (h == 0 || frame.IsEmpty) return;
        fixed (byte* ptr = frame) NativePushAacLc(h, ptr, (uint)frame.Length);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            if (player is not null)
            {
                try { if (!player.HasExited) player.Kill(); } catch (InvalidOperationException) { }
            }
            await Task.WhenAll(receiving, controls, pumping, errors).ConfigureAwait(false);
            if (player is not null)
                await player.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            alacDecoder?.Dispose();
            nint h = Interlocked.Exchange(ref nativeAudio, 0);
            if (h != 0) NativeDestroy(h);
            player?.Dispose();
            data.Dispose();
            control.Dispose();
            output?.Dispose();
            stop.Dispose();
            aesCipher.Dispose();
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(iv);
            try { if (!string.IsNullOrEmpty(description) && File.Exists(description)) File.Delete(description); } catch { }
            report($"Audio stopped. {Interlocked.Read(ref packets)} packets received.");
        }
    }

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_create", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int NativeCreate(string? deviceId, float initialVolume, int initialMute, out nint result);

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_push_pcm", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int NativePushPcm(nint handle, byte* pcmBytes, uint byteLength);

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_push_aac_lc", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int NativePushAacLc(nint handle, byte* frameBytes, uint frameLength);

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_flush", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeFlush(nint handle);

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_set_volume", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeSetVolume(nint handle, float volume);

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_set_mute", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeSetMute(nint handle, int mute);

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_set_device", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void NativeSetDevice(nint handle, string? deviceId);

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeDestroy(nint handle);

    [DllImport("notairplay-video.dll", EntryPoint = "na_audio_supports_aac_lc", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeSupportsAacLc();
}
