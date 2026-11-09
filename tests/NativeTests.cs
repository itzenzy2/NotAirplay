using System.Diagnostics;
using System.Runtime.InteropServices;
using NotAirplay;

internal static class NativeTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, int flags);

    public static void Run(string path)
    {
        var portrait = AccessUnits(File.ReadAllBytes(Path.Combine(path, "benchmark.h264")));
        var landscape = AccessUnits(File.ReadAllBytes(Path.Combine(path, "landscape.h264")));
        nint hwnd = CreateWindowEx(0, "static", "", unchecked((int)0x80000000), 0, 0, 500, 1080, 0, 0, 0, 0);
        try
        {
            foreach (bool hardware in new[] { true, false })
            {
                var stats = new VideoStatistics();
                using var decoder = new NativeVideoDecoder(hwnd, stats, _ => { }, Console.WriteLine, hardware);
                var process = Process.GetCurrentProcess(); var before = process.TotalProcessorTime;
                var watch = Stopwatch.StartNew();
                int firstAt = 0;
                for (int i = 0; i < portrait.Count; i++)
                {
                    // Real-time cadence, including two-frame network bursts.
                    if (i % 2 == 0) {
                        double remaining = i * 1000.0 / 60 - watch.Elapsed.TotalMilliseconds;
                        if (remaining > 0) Thread.Sleep(TimeSpan.FromMilliseconds(remaining));
                    }
                    decoder.Write(portrait[i]);
                    if (stats.Decoded > 0 && firstAt == 0) firstAt = i + 1;
                }
                if (firstAt is 0 or > 3) throw new Exception($"Decoder buffered {firstAt} inputs before first output");
                decoder.Finish();
                Console.WriteLine($"PACED native hardware={hardware}: {portrait.Count} inputs, {watch.Elapsed.TotalMilliseconds:F1} ms elapsed, presented={stats.Presented}, skipped={stats.Skipped}; first output at input {firstAt}");
                if (stats.Skipped > portrait.Count / 10) throw new Exception("Too many presentation skips in paced playback.");
                // New SPS, cropped frame sizes and a resized target on the same decoder.
                SetWindowPos(hwnd, 0, 0, 0, 960, 600, 0x14);
                foreach (var bytes in landscape) decoder.Write(bytes);
                decoder.Finish();
                if (stats.Decoded != portrait.Count + landscape.Count || stats.Width != 1080 || stats.Height != 500)
                    throw new Exception($"Native orientation/count mismatch: {stats.Summary}");
                Console.WriteLine($"PASS native {(hardware ? "automatic" : "software")} decode, portrait → landscape, target resize: {stats.Decoded} frames");
            }
        }
        finally { DestroyWindow(hwnd); }
        BenchmarkPreview4(portrait).GetAwaiter().GetResult();
    }

    private static List<byte[]> AccessUnits(byte[] source)
    {
        var indices = new List<int>();
        for (int i = 0; i + 5 < source.Length; i++)
            if (source[i] == 0 && source[i + 1] == 0 && source[i + 2] == 0 && source[i + 3] == 1 && (source[i + 4] & 31) == 9)
                indices.Add(i);
        indices.Add(source.Length);
        if (indices.Count < 3) throw new Exception("Expected AUD-delimited synthetic fixture.");
        return Enumerable.Range(0, indices.Count - 1).Select(i => source[indices[i]..indices[i + 1]]).ToList();
    }

    private static async Task BenchmarkPreview4(List<byte[]> frames)
    {
        var start = new ProcessStartInfo(VideoDecoder.FindExecutable()) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        // Exact Preview 4 FFmpeg pipeline; output is drained without WPF drawing,
        // so this is a lower bound for that preview's processing cost.
        string[] args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-probesize", "32", "-analyzeduration", "0",
            "-flags", "low_delay", "-threads", "2", "-f", "h264", "-i", "pipe:0", "-an", "-fps_mode", "passthrough",
            "-threads", "1", "-filter_threads", "1", "-c:v", "bmp", "-pix_fmt", "bgr24", "-f", "image2pipe",
            "-flush_packets", "1", "pipe:1"];
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var parent = Process.GetCurrentProcess(); var before = parent.TotalProcessorTime; var watch = Stopwatch.StartNew();
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        foreach (var frame in frames) await process.StandardInput.BaseStream.WriteAsync(frame, timeout.Token);
        process.StandardInput.Close();
        await Task.WhenAll(output, process.WaitForExitAsync(timeout.Token));
        if (process.ExitCode != 0) throw new Exception(await errors);
        Console.WriteLine($"BENCH Preview 4 BMP pipe: {frames.Count} inputs, {watch.Elapsed.TotalMilliseconds:F1} ms elapsed, {((parent.TotalProcessorTime-before)+process.TotalProcessorTime).TotalMilliseconds:F1} ms combined CPU (excludes WPF rendering)");
    }
}
