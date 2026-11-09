using System.IO;
using System.Runtime.InteropServices;

namespace NotAirplay;

internal sealed class NativeVideoDecoder : IDisposable
{
    private nint handle;
    private NativeInfo previous;
    private readonly VideoStatistics statistics;
    private readonly Action<bool> started;
    private readonly Action<string> report;

    public NativeVideoDecoder(nint target, VideoStatistics statistics, Action<bool> started, Action<string> report, bool hardware = true)
    {
        this.statistics = statistics;
        this.started = started;
        this.report = report;
        int result = Create(target, hardware ? 1 : 0, out handle);
        if (result < 0)
            result = Create(target, 0, out handle);
        Check(result);
    }

    public unsafe void Write(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* pointer = bytes)
        {
            Check(Push(handle, pointer, (uint)bytes.Length, out var info));
            Update(info);
        }
    }

    public void Finish() { Check(Drain(handle, out var info)); Update(info); }

    private void Update(NativeInfo info)
    {
        statistics.Present((int)(info.Presented - previous.Presented));
        statistics.Skip((int)(info.Skipped - previous.Skipped));
        if (info.Frames != previous.Frames)
        {
            statistics.Decoder = info.Hardware != 0 ? "Direct3D 11 · hardware decode" : "Direct3D 11 · software decode";
            statistics.Decode((int)(info.Frames - previous.Frames), (int)info.Width, (int)info.Height);
            if (previous.Frames == 0)
            {
                report($"First video frame decoded ({info.Width} x {info.Height}). {statistics.Decoder}.");
                started(true);
            }
        }
        previous = info;
    }

    private static void Check(int result)
    {
        if (result < 0) throw new InvalidOperationException($"Windows video backend returned 0x{result:X8}.");
    }

    public void Dispose()
    {
        if (handle != 0) { Destroy(handle); handle = 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInfo { public uint Width, Height, Frames, Hardware, Presented, Skipped; }
    [DllImport("notairplay-video.dll", EntryPoint = "na_video_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Create(nint target, int hardware, out nint result);
    [DllImport("notairplay-video.dll", EntryPoint = "na_video_push", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int Push(nint handle, byte* bytes, uint length, out NativeInfo info);
    [DllImport("notairplay-video.dll", EntryPoint = "na_video_finish", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Drain(nint handle, out NativeInfo info);
    [DllImport("notairplay-video.dll", EntryPoint = "na_video_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Destroy(nint handle);
}
