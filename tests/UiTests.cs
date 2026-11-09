using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Net;
using System.Net.Sockets;
using NotAirplay;

internal static class UiTests
{
    public static void Run(string path)
    {
        var high = new ReceiverIdentity(() => 1440);
        var metadata = new BinaryPropertyListReader(high.Info);
        int display = metadata.ReadArray(metadata.Find(metadata.Root, "displays")!.Value).Single();
        Check(metadata.ReadInteger(metadata.Find(display, "height")!.Value) == 1440, "higher-resolution advertisement");
        var window = new MainWindow();
        var apply = typeof(MainWindow).GetMethod("ApplySession", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var id = Guid.NewGuid(); var stats = new VideoStatistics();
        apply.Invoke(window, [new MirrorUpdate(id, MirrorState.Connecting, "Test iPhone", stats)]);
        Check(((TextBlock)window.FindName("HeroTitle")).Text == "Connecting.", "connecting state");
        apply.Invoke(window, [new MirrorUpdate(id, MirrorState.Streaming, "compatibility", stats)]);
        Check(((FrameworkElement)window.FindName("ReceiverHome")).Visibility == Visibility.Collapsed, "live state");
        var image = (Image)window.FindName("MirrorImage");
        image.Source = new WriteableBitmap(96, 64, 96, 96, PixelFormats.Bgr24, null);
        apply.Invoke(window, [new MirrorUpdate(id, MirrorState.Stopped, "Stopped", stats)]);
        Check(image.Source is null && image.Visibility == Visibility.Collapsed, "clear stale frame");
        Check(((TextBlock)window.FindName("HeroTitle")).Text == "Mirroring stopped.", "stopped message");
        apply.Invoke(window, [new MirrorUpdate(id, MirrorState.Streaming, "native", stats)]);
        Check(((FrameworkElement)window.FindName("ReceiverHome")).Visibility == Visibility.Visible, "ignore late frame/session callback");
        var second = Guid.NewGuid();
        apply.Invoke(window, [new MirrorUpdate(second, MirrorState.Connecting, "iPhone", stats)]);
        apply.Invoke(window, [new MirrorUpdate(second, MirrorState.Failed, "Test failure", stats)]);
        Check(((TextBlock)window.FindName("HeroTitle")).Text == "Connection interrupted.", "failure state");
        var audioId = Guid.NewGuid();
        apply.Invoke(window, [new MirrorUpdate(audioId, MirrorState.Connecting, "Audio iPhone", stats)]);
        apply.Invoke(window, [new MirrorUpdate(audioId, MirrorState.Streaming, "audio", stats)]);
        Check(((FrameworkElement)window.FindName("AudioOnlyPanel")).Visibility == Visibility.Visible, "audio panel shown");
        Check(((FrameworkElement)window.FindName("ReceiverHome")).Visibility == Visibility.Collapsed, "home hidden during audio");
        apply.Invoke(window, [new MirrorUpdate(audioId, MirrorState.Streaming, "audio-paused", stats)]);
        Check(((FrameworkElement)window.FindName("AudioOnlyPanel")).Visibility == Visibility.Visible, "audio panel remains visible while paused");
        Check(((TextBlock)window.FindName("StatusDetail")).Text == "Audio iPhone · Paused", "status shows paused");
        apply.Invoke(window, [new MirrorUpdate(audioId, MirrorState.Stopped, "Stopped", stats)]);
        Check(((FrameworkElement)window.FindName("AudioOnlyPanel")).Visibility == Visibility.Collapsed, "audio panel hidden on stop");
        Console.WriteLine("PASS UI state: connecting, streaming, cleared image on stop, ignored stale event, retry after failure, audio only");
    }

    public static void RunHosted(string path)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var pending = dispatcher.InvokeAsync(async () =>
        {
            var window = new MainWindow {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000
            };
            window.Show();
            var target = (VideoSurface)window.FindName("NativeSurface");
            Check(target.Handle != 0, "native HWND exists while home is visible");
            var apply = typeof(MainWindow).GetMethod("ApplySession", BindingFlags.NonPublic | BindingFlags.Instance)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var gate = new SemaphoreSlim(1);
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new MirrorSession(IPAddress.Loopback, IPAddress.Loopback, gate,
                f => { f.Dispose(); throw new Exception("Native-host test unexpectedly used FFmpeg"); },
                Console.WriteLine, new VideoTarget(target.Handle), update => dispatcher.BeginInvoke(() => {
                    apply.Invoke(window, [update]);
                    if (update.State == MirrorState.Streaming) first.TrySetResult();
                    if (update.State is MirrorState.Stopped or MirrorState.Failed) ended.TrySetResult();
                }));
            try
            {
                var identity = new ReceiverIdentity();
                byte[] message = File.ReadAllBytes(Path.Combine(path, "m3.bin"));
                await session.SetupAsync(File.ReadAllBytes(Path.Combine(path, "setup.bplist")), message, identity, timeout.Token);
                var answer = new BinaryPropertyListReader(await session.SetupAsync(File.ReadAllBytes(Path.Combine(path, "stream-setup.bplist")), message, identity, timeout.Token));
                int entry = answer.ReadArray(answer.Find(answer.Root, "streams")!.Value).Single();
                int port = (int)answer.ReadInteger(answer.Find(entry, "dataPort")!.Value);
                using var sender = new TcpClient();
                await sender.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                await sender.GetStream().WriteAsync(File.ReadAllBytes(Path.Combine(path, "h264.mirror")), timeout.Token);
                await first.Task.WaitAsync(timeout.Token);
                Check(target.Visibility == Visibility.Visible, "native surface shown after first decoded frame");
                Check(((FrameworkElement)window.FindName("ReceiverHome")).Visibility == Visibility.Collapsed, "home hidden in live host");
                window.Width = 720; window.Height = 560;
                window.WindowState = WindowState.Minimized;
                window.WindowState = WindowState.Normal;
                window.UpdateLayout();
                // Validate full screen restores chrome even if the session ends inside it.
                typeof(MainWindow).GetMethod("ToggleFullscreen", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                session.RequestStop();
            }
            finally { await session.DisposeAsync(); }
            await ended.Task.WaitAsync(timeout.Token);
            Check(target.Visibility != Visibility.Visible, "native surface hidden on disconnect");
            Check(((TextBlock)window.FindName("HeroTitle")).Text == "Mirroring stopped.", "real session stopped state");
            Check(((FrameworkElement)window.FindName("TitleBar")).Visibility == Visibility.Visible, "full screen restored on stop");
            window.Close();
            Console.WriteLine("PASS hosted WPF + encrypted TCP + native GPU: handle initialization, first frame, resize, minimize/restore, full screen, disconnect clears video");
        }).Task.Unwrap();
        _ = pending.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        pending.GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("UI check: " + name); }
}
