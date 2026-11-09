using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NotAirplay;

internal sealed class MirrorSession(IPAddress local, IPAddress remote, SemaphoreSlim activeMirror,
    Action<VideoFrame> present, Action<string> report, VideoTarget? target = null,
    Action<MirrorUpdate>? changed = null) : IAsyncDisposable
{
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly VideoStatistics statistics = new();
    private string? failure;
    private bool streaming;
    private string senderName = "iPhone";
    private readonly CancellationTokenSource stopping = new();
    private MirrorTiming? timing;
    private byte[]? mediaKey;
    private byte[]? audioIv;
    private MirrorAudio? audio;
    private TcpListener? videoListener;
    private Task video = Task.CompletedTask;
    private bool ownsMirror;

    public bool IsSetUp => mediaKey is not null;
    public CancellationToken Ended => stopping.Token;
    public void Fail(string reason) => failure = reason;
    private float volumeDb;
    public float VolumeDb => volumeDb;
    public event Action<float>? VolumeChanged;
    public event Action<string>? TrackChanged;

    private bool isMuted;
    public void SetMute(bool mute)
    {
        isMuted = mute;
        if (audio is not null) audio.Muted = mute;
    }

    public void SetVolume(float volume)
    {
        audio?.SetVolume(volume);
        volumeDb = volume <= 0.0001f ? -144.0f : (volume * 30.0f) - 30.0f;
    }

    public void HandleSetParameter(byte[] body)
    {
        int volIdx = body.AsSpan().IndexOf("volume:"u8);
        if (volIdx >= 0)
        {
            string text = Encoding.ASCII.GetString(body, volIdx, Math.Min(64, body.Length - volIdx));
            string line = text.Split("\r\n", 2)[0];
            string val = line["volume:".Length..].Trim();
            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float db))
            {
                volumeDb = db;
                float linear = db <= -144.0f || db <= -30.0f ? 0f : Math.Clamp((db + 30.0f) / 30.0f, 0f, 1f);
                audio?.SetVolume(linear);
                VolumeChanged?.Invoke(linear);
                report($"Volume: {linear * 100:F0}% ({db:F1} dB)");
            }
        }
        ExtractMetadata(body);
        NotifyStandbyIfIdle();
    }

    public void HandlePost(byte[] body, string route) => ExtractMetadata(body);

    public void HandleRecord()
    {
        // When client sends RECORD, if no media stream is active (audio only / paused),
        // transition promptly to standby so the UI shows Connected rather than "Connecting...".
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            NotifyStandbyIfIdle();
        });
    }

    private int standbyReported;
    public void NotifyStandbyIfIdle()
    {
        if (IsSetUp && !streaming && videoListener is null && !stopping.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref standbyReported, 1) == 0)
            {
                report("AirPlay audio session ready. Standing by.");
            }
            changed?.Invoke(new(sessionId, MirrorState.Streaming, "audio-paused", statistics));
        }
    }

    private void ExtractMetadata(byte[] body)
    {
        if (body.Length < 8) return;
        if (body[0] == 0xFF && body[1] == 0xD8) return; // JPEG image (album artwork)
        if (body[0] == 0x89 && body[1] == 0x50 && body[2] == 0x4E && body[3] == 0x47) return; // PNG image (album artwork)
        try
        {
            string? title = null, artist = null, album = null;
            if (body[0] == (byte)'b' && body[1] == (byte)'p' && body[2] == (byte)'l' && body[3] == (byte)'i' && body[4] == (byte)'s' && body[5] == (byte)'t')
            {
                var reader = new BinaryPropertyListReader(body);
                if (reader.Find(reader.Root, "title") is int t) title = reader.ReadString(t);
                if (reader.Find(reader.Root, "artist") is int a) artist = reader.ReadString(a);
                if (reader.Find(reader.Root, "name") is int n && title is null) title = reader.ReadString(n);
                if (reader.Find(reader.Root, "album") is int al) album = reader.ReadString(al);
            }
            else
            {
                ParseDmap(body, ref title, ref artist, ref album);
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                {
                    for (int i = 0; i + 8 <= body.Length; i++)
                    {
                        uint tag = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i, 4));
                        int len = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(i + 4, 4));
                        if (len is > 0 and <= 256 && i + 8 + len <= body.Length)
                        {
                            if (tag == 0x6D696E6D && title is null)
                            {
                                title = Encoding.UTF8.GetString(body.AsSpan(i + 8, len));
                                i += 7 + len;
                            }
                            else if (tag == 0x61736172 && artist is null)
                            {
                                artist = Encoding.UTF8.GetString(body.AsSpan(i + 8, len));
                                i += 7 + len;
                            }
                            else if (tag == 0x6173616C && album is null)
                            {
                                album = Encoding.UTF8.GetString(body.AsSpan(i + 8, len));
                                i += 7 + len;
                            }
                        }
                    }
                }
            }

            title = title?.Trim();
            artist = artist?.Trim();
            album = album?.Trim();
            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist))
            {
                string info = !string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title)
                    ? $"{title} — {artist}" : (title ?? artist)!;
                TrackChanged?.Invoke(info);
                report($"Now playing: {info}");
            }
        }
        catch { }
    }

    private static void ParseDmap(ReadOnlySpan<byte> data, ref string? title, ref string? artist, ref string? album)
    {
        int offset = 0;
        while (offset + 8 <= data.Length)
        {
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            int len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 4, 4));
            offset += 8;
            if (len < 0 || offset + len > data.Length) break;
            switch (tag)
            {
                case 0x6D696E6D: // minm (dmap.itemname)
                    title = Encoding.UTF8.GetString(data.Slice(offset, len));
                    break;
                case 0x61736172: // asar (daap.songartist)
                    artist = Encoding.UTF8.GetString(data.Slice(offset, len));
                    break;
                case 0x6173616C: // asal (daap.songalbum)
                    album = Encoding.UTF8.GetString(data.Slice(offset, len));
                    break;
                case 0x6D6C6974: // mlit
                case 0x6D64636C: // mdcl
                    ParseDmap(data.Slice(offset, len), ref title, ref artist, ref album);
                    break;
            }
            offset += len;
        }
    }

    public async Task<bool> TeardownAsync(byte[]? body)
    {
        if (body is not { Length: > 0 }) return true;
        var plist = new BinaryPropertyListReader(body);
        if (plist.Find(plist.Root, "streams") is not int streams) return true;
        foreach (int stream in plist.ReadArray(streams))
        {
            if (plist.Find(stream, "type") is not int type) continue;
            ulong value = plist.ReadInteger(type);
            if (value == 110) return true;
            if (value == 96)
            {
                if (audio is not null)
                {
                    var previous = audio; audio = null;
                    await previous.DisposeAsync().ConfigureAwait(false);
                }
                if (videoListener is null)
                {
                    // Audio-only session stays connected in standby when playback pauses,
                    // preserving the connection on the phone just like a Bluetooth speaker.
                    streaming = false;
                    Interlocked.Exchange(ref standbyReported, 1);
                    TrackChanged?.Invoke(string.Empty);
                    report("AirPlay audio playback paused. Standing by.");
                    changed?.Invoke(new(sessionId, MirrorState.Streaming, "audio-paused", statistics));
                    return false;
                }
            }
        }
        return false;
    }

    public void RequestStop()
    {
        try { stopping.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task<byte[]> SetupAsync(byte[] request, byte[]? keyMessage, ReceiverIdentity identity,
        CancellationToken token)
    {
        var plist = new BinaryPropertyListReader(request);
        if (plist.Find(plist.Root, "streams") is int streams && plist.Find(plist.Root, "ekey") is null)
            return StartVideo(plist, streams);

        if (keyMessage is null || plist.Find(plist.Root, "ekey") is not int ekey)
            throw new InvalidDataException("Stream SETUP requires a FairPlay exchange and wrapped key.");
        if (IsSetUp)
            throw new InvalidDataException("Stream control has already been set up.");

        ulong port = plist.Find(plist.Root, "timingPort") is int timingPort ? plist.ReadInteger(timingPort) : 0;
        string protocol = plist.Find(plist.Root, "timingProtocol") is int timingProtocol
            ? plist.ReadString(timingProtocol) : "NTP";
        if (port > ushort.MaxValue || protocol is not ("NTP" or "None"))
            throw new InvalidDataException("Unsupported AirPlay timing protocol or port.");

        if (target is not { PreferNative: true, Handle: not 0 })
            _ = VideoDecoder.FindExecutable();
        if (!activeMirror.Wait(0))
            throw new InvalidOperationException("Another iPhone is already setting up a mirror session.");
        ownsMirror = true;
        if (plist.Find(plist.Root, "name") is int name)
            senderName = new string(plist.ReadString(name).Where(c => !char.IsControl(c)).Take(64).ToArray());
        if (string.IsNullOrWhiteSpace(senderName)) senderName = "iPhone";
        changed?.Invoke(new(sessionId, MirrorState.Connecting, senderName, statistics));
        var wrapped = plist.ReadData(ekey).ToArray();
        try { mediaKey = await FairPlayKeyRecovery.RecoverAsync(keyMessage, wrapped, token).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(wrapped); }
        if (plist.Find(plist.Root, "eiv") is int eiv) audioIv = plist.ReadData(eiv).ToArray();
        if (protocol == "NTP")
            timing = new MirrorTiming(local, port != 0 ? new IPEndPoint(remote, (int)port) : null, report);

        var fields = plist.Find(plist.Root, "combinedGetInfoWithControlSetup") is not null
            ? identity.CreateInfoFields() : new Dictionary<string, object>();
        fields["eventPort"] = 0UL;
        fields["timingPort"] = (ulong)(timing?.Port ?? 0);
        if (plist.Find(plist.Root, "streams") is int sameRequestStreams)
        {
            fields["streams"] = OpenVideoStream(plist, sameRequestStreams);
        }
        report("Recovered the media key. Stream control is ready.");
        return BinaryPropertyList.Write(fields);
    }

    private byte[] StartVideo(BinaryPropertyListReader plist, int streams)
    {
        if (plist.Find(plist.Root, "timingPort") is int tp)
        {
            ulong p = plist.ReadInteger(tp);
            if (p is > 0 and <= ushort.MaxValue)
                timing?.SetPeer(new IPEndPoint(remote, (int)p));
        }
        if (plist.Find(plist.Root, "eiv") is int rootEiv)
            audioIv = plist.ReadData(rootEiv).ToArray();
        return BinaryPropertyList.Write(new Dictionary<string, object> { ["streams"] = OpenVideoStream(plist, streams) });
    }

    private object[] OpenVideoStream(BinaryPropertyListReader plist, int streams)
    {
        if (mediaKey is null)
            throw new InvalidDataException("Video SETUP arrived before the media key.");
        var results = new List<object>();
        bool hasVideo = false;
        foreach (int item in plist.ReadArray(streams))
        {
            if (plist.Find(item, "type") is not int type) continue;
            ulong streamType = plist.ReadInteger(type);
            if (streamType == 96)
            {
                ulong codec = plist.Find(item, "ct") is int ct ? plist.ReadInteger(ct) : 8;
                ulong remotePort = plist.Find(item, "controlPort") is int cp ? plist.ReadInteger(cp) : 0;
                if (plist.Find(item, "eiv") is int streamEiv)
                    audioIv = plist.ReadData(streamEiv).ToArray();
                report($"Audio SETUP: type {streamType}, codec {codec}.");
                if (remotePort > 65535) throw new InvalidDataException("Invalid audio control port.");
                if (audio is not null && audio.Codec != codec)
                    throw new InvalidDataException("Audio codec changed; stop audio before changing its format.");
                if (audio is null)
                {
                    audio = new MirrorAudio(local, remote, mediaKey, audioIv ?? [], codec, (int)remotePort, report);
                    if (isMuted) audio.Muted = true;
                    float linear = volumeDb <= -144.0f || volumeDb <= -30.0f ? 0f : Math.Clamp((volumeDb + 30.0f) / 30.0f, 0f, 1f);
                    audio.SetVolume(linear);
                }
                results.Add(new Dictionary<string, object> { ["type"] = 96UL, ["dataPort"] = (ulong)audio.DataPort, ["controlPort"] = (ulong)audio.ControlPort });
                continue;
            }
            if (streamType != 110) throw new InvalidDataException($"Unsupported stream type {streamType}.");
            if (plist.Find(item, "streamConnectionID") is not int streamId)
                throw new InvalidDataException("Video SETUP did not include a stream ID.");
            if (videoListener is not null)
                throw new InvalidDataException("A video stream is already set up on this session.");

            hasVideo = true;
            ulong id = plist.ReadInteger(streamId);
            videoListener = new TcpListener(local, 0);
            videoListener.Start(2);
            int port = ((IPEndPoint)videoListener.LocalEndpoint).Port;
            video = ReceiveVideoAsync(videoListener, id, stopping.Token);
            report($"Video SETUP accepted. Waiting on TCP port {port}.");
            results.Add(new Dictionary<string, object> { ["type"] = 110UL, ["dataPort"] = (ulong)port });
        }
        if (!hasVideo && videoListener is null && audio is not null)
        {
            streaming = true;
            Interlocked.Exchange(ref standbyReported, 0);
            report("AirPlay audio playback active.");
            changed?.Invoke(new(sessionId, MirrorState.Streaming, "audio", statistics));
        }
        return results.ToArray();
    }

    private async Task ReceiveVideoAsync(TcpListener listener, ulong id, CancellationToken token)
    {
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(25));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(wait.Token).ConfigureAwait(false);
                var peer = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (!peer.Equals(remote))
                    continue;
                listener.Stop();
                client.NoDelay = true;
                client.Client.ReceiveBufferSize = 128 * 1024;
                report("iPhone opened the video stream.");
                await using var firstFrameDeadline = new Timer(_ =>
                {
                    if (statistics.Decoded == 0)
                    {
                        failure = "No video could be decoded. Try compatibility mode and reconnect.";
                        RequestStop();
                    }
                }, null, TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
                await DecodeStreamAsync(client.GetStream(), id, token).ConfigureAwait(false);
                report("Video stream ended.");
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException) { failure = "Your iPhone did not open the video connection. Try mirroring again."; report(failure); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or Win32Exception or InvalidOperationException or TimeoutException)
        { if (!token.IsCancellationRequested) { failure = ex.Message; report($"Video stream stopped. {ex.Message}"); } }
        finally { stopping.Cancel(); }
    }

    internal async Task DecodeStreamAsync(Stream stream, ulong id, CancellationToken token)
    {
        const int maxPacketSize = 8 * 1024 * 1024;
        using var cipher = new MirrorCipher(mediaKey!, id);
        var header = new byte[128];
        MirrorVideoFormat? format = null;
        VideoDecoder? decoder = null;
        NativeVideoDecoder? native = null;
        bool nativeUnavailable = target is not { PreferNative: true, Handle: not 0 };
        void Started(bool gpu)
        {
            changed?.Invoke(new(sessionId, MirrorState.Streaming, gpu ? "native" : "compatibility", statistics));
            streaming = true;
        }
        void Present(VideoFrame frame)
        {
            frame.SessionId = sessionId;
            frame.Statistics = statistics;
            statistics.Decode(1, frame.Width, frame.Height);
            if (!streaming) Started(false);
            present(frame);
        }
        async Task CompatibilityAsync()
        {
            statistics.Decoder = "FFmpeg · compatibility";
            decoder = new VideoDecoder(format!.Decoder, Present, report, token);
            await decoder.WriteAsync(format.ParameterSets, token).ConfigureAwait(false);
        }
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(header.AsMemory(0, 128), token).ConfigureAwait(false);
                if (read == 0) break;
                int headerOffset = read;
                while (headerOffset < 128)
                {
                    int next = await stream.ReadAsync(header.AsMemory(headerOffset, 128 - headerOffset), token).ConfigureAwait(false);
                    if (next == 0) throw new EndOfStreamException();
                    headerOffset += next;
                }
                int size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size is < 0 or > maxPacketSize)
                    throw new InvalidDataException("Invalid mirror packet size.");
                if (size == 0)
                    continue;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
                var payload = buffer.AsMemory(0, size);
                try
                {
                    await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
                    if (header[4] == 1)
                    {
                        var announced = MirrorVideoFormat.Read(payload.Span);
                        if (format is null || format.Decoder != announced.Decoder ||
                            !format.ParameterSets.AsSpan().SequenceEqual(announced.ParameterSets))
                        {
                            if (decoder is not null)
                            {
                                var previous = decoder;
                                decoder = null;
                                await previous.DisposeAsync().ConfigureAwait(false);
                            }
                            native?.Dispose(); native = null;
                            streaming = false;
                            format = announced;
                            if (!nativeUnavailable && announced.Decoder == "h264")
                            {
                                try
                                {
                                    native = new NativeVideoDecoder(target!.Handle, statistics, Started, report);
                                    native.Write(announced.ParameterSets);
                                }
                                catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                                {
                                    native?.Dispose(); native = null; nativeUnavailable = true;
                                    report($"Using compatibility decoder. {ex.Message}");
                                }
                            }
                            if (native is null) await CompatibilityAsync().ConfigureAwait(false);
                            report($"Video configuration received ({announced.Decoder}).");
                        }
                        format = announced;
                    }
                    else if (header[4] == 0)
                    {
                        statistics.Packet(size);
                        long started = Stopwatch.GetTimestamp();
                        cipher.Transform(payload.Span);
                        if (format is not null)
                        {
                            var packet = format.ToAnnexB(payload);
                            if (native is not null)
                            {
                                try { native.Write(packet.Span); }
                                catch (InvalidOperationException ex)
                                {
                                    native.Dispose(); native = null; nativeUnavailable = true; streaming = false;
                                    report($"Windows video path unavailable. Switching to compatibility. {ex.Message}");
                                    await CompatibilityAsync().ConfigureAwait(false);
                                }
                            }
                            if (decoder is not null) await decoder.WriteAsync(packet, token).ConfigureAwait(false);
                        }
                        statistics.Processing(Stopwatch.GetTimestamp() - started);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            if (decoder is not null)
                await decoder.FinishAsync(token).ConfigureAwait(false);
            native?.Finish();
        }
        finally
        {
            native?.Dispose();
            if (decoder is not null)
                await decoder.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        try
        {
            await Task.WhenAny(video, Task.Delay(1000)).ConfigureAwait(false);
            if (audio is not null) await audio.DisposeAsync().ConfigureAwait(false);
            if (timing is not null)
                await timing.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            videoListener?.Stop();
            if (mediaKey is not null)
                CryptographicOperations.ZeroMemory(mediaKey);
            if (audioIv is not null) CryptographicOperations.ZeroMemory(audioIv);
            if (ownsMirror)
            {
                report(statistics.Summary);
                changed?.Invoke(new(sessionId, failure is null ? MirrorState.Stopped : MirrorState.Failed,
                    failure ?? "Mirroring stopped", statistics));
                activeMirror.Release();
            }
            stopping.Dispose();
        }
    }
}
