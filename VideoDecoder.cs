using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace NotAirplay;

internal sealed class VideoFrame(byte[] pixels, int width, int height, int stride, bool bottomUp) : IDisposable
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public bool BottomUp { get; } = bottomUp;
    public Guid SessionId { get; set; }
    public VideoStatistics? Statistics { get; set; }

    public void Dispose() => ArrayPool<byte>.Shared.Return(Pixels);
}

internal sealed class VideoDecoder : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource stopping;
    private readonly Task reading;
    private readonly Task errors;

    public VideoDecoder(string codec, Action<VideoFrame> present, Action<string> report, CancellationToken token)
    {
        var start = new ProcessStartInfo(FindExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-probesize", "32", "-analyzeduration", "0",
            "-flags", "low_delay", "-threads", "2", "-thread_type", "slice", "-f", codec, "-i", "pipe:0", "-an", "-fps_mode", "passthrough",
            "-threads", "1", "-filter_threads", "1", "-c:v", "bmp", "-pix_fmt", "bgr24", "-f", "image2pipe",
            "-flush_packets", "1", "pipe:1"];
        foreach (string argument in args)
            start.ArgumentList.Add(argument);

        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        reading = ReadFramesAsync(process.StandardOutput.BaseStream, present, report, stopping.Token);
        errors = ReadErrorsAsync(process.StandardError, report, stopping.Token);
        _ = reading.ContinueWith(_ =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public static string FindExecutable()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string root = directory.Trim('"');
            if (!Path.IsPathFullyQualified(root))
                continue;
            string candidate = Path.Combine(root, "ffmpeg.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("FFmpeg is required for video. Install it and make ffmpeg.exe available on PATH.");
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) =>
        process.StandardInput.BaseStream.WriteAsync(bytes, token);

    public async Task FinishAsync(CancellationToken token)
    {
        process.StandardInput.Close();
        await reading.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
    }

    private static async Task ReadFramesAsync(Stream source, Action<VideoFrame> present, Action<string> report,
        CancellationToken token)
    {
        try
        {
            var header = new byte[54];
            bool first = true;
            while (await source.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
            {
                await source.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
                int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18));
                int height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22));
                int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
                if (!header.AsSpan(0, 2).SequenceEqual("BM"u8) ||
                    BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(10)) != header.Length ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)) != 24 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(30)) != 0 ||
                    width is < 1 or > 4096 || height is 0 or < -4096 or > 4096 ||
                    width * Math.Abs(height) > 9_000_000)
                    throw new InvalidDataException("Unexpected FFmpeg frame format.");

                int stride = (width * 3 + 3) & ~3;
                int length = stride * Math.Abs(height);
                if (size != length + header.Length)
                    throw new InvalidDataException("Unexpected FFmpeg frame length.");

                var frame = new VideoFrame(ArrayPool<byte>.Shared.Rent(length), width, Math.Abs(height), stride, height > 0);
                try
                {
                    await source.ReadExactlyAsync(frame.Pixels.AsMemory(0, length), token).ConfigureAwait(false);
                    if (first)
                    {
                        report($"First video frame decoded ({width} × {frame.Height}).");
                        first = false;
                    }
                    present(frame);
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) when (token.IsCancellationRequested) { }
    }

    private static async Task ReadErrorsAsync(StreamReader source, Action<string> report, CancellationToken token)
    {
        int lines = 0;
        try
        {
            while (await source.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (lines++ < 4)
                    report("FFmpeg: " + line[..Math.Min(line.Length, 180)]);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        try
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
            }
            try { await Task.WhenAll(reading, errors).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        }
        finally
        {
            process.Dispose();
            stopping.Dispose();
        }
    }
}
