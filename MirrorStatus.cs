using System.Diagnostics;

namespace NotAirplay;

internal enum MirrorState { Connecting, Streaming, Stopped, Failed }
internal sealed record MirrorUpdate(Guid SessionId, MirrorState State, string Detail, VideoStatistics Statistics);
internal sealed class VideoTarget(nint handle, bool preferNative = true)
{
    public nint Handle { get; } = handle;
    public bool SharperVideo { get; set; }
    private int preferNativeValue = preferNative ? 1 : 0;
    public bool PreferNative { get => Volatile.Read(ref preferNativeValue) != 0; set => Volatile.Write(ref preferNativeValue, value ? 1 : 0); }
}

internal sealed class VideoStatistics
{
    private long packets, bytes, decoded, presented, skipped, processingTicks, lastPacket;
    public string Decoder { get; set; } = "Waiting";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Packets => Interlocked.Read(ref packets);
    public long Bytes => Interlocked.Read(ref bytes);
    public long Decoded => Interlocked.Read(ref decoded);
    public long Presented => Interlocked.Read(ref presented);
    public long Skipped => Interlocked.Read(ref skipped);
    public double ProcessingMilliseconds => Interlocked.Read(ref processingTicks) * 1000.0 / Stopwatch.Frequency;
    public double SecondsSincePacket => Interlocked.Read(ref lastPacket) is long value && value != 0
        ? Stopwatch.GetElapsedTime(value).TotalSeconds : 0;
    public void Packet(int length) { Interlocked.Increment(ref packets); Interlocked.Add(ref bytes, length); Interlocked.Exchange(ref lastPacket, Stopwatch.GetTimestamp()); }
    public void Decode(int count, int width, int height) { Interlocked.Add(ref decoded, count); Width = width; Height = height; }
    public void Present(int count = 1) => Interlocked.Add(ref presented, count);
    public void Skip(int count = 1) => Interlocked.Add(ref skipped, count);
    public void Processing(long ticks) => Interlocked.Add(ref processingTicks, ticks);
    public string Summary => $"{Decoder}; {Width} x {Height}; received {Packets}, decoded {Decoded}, presented {Presented}, skipped {Skipped}; local processing {ProcessingMilliseconds / Math.Max(1, Packets):F2} ms/frame.";
}
