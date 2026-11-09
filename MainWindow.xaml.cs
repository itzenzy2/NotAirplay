using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace NotAirplay;

public partial class MainWindow : Window
{
    private readonly object historyLock = new();
    private readonly Queue<string> history = new();
    private readonly DispatcherTimer statusTimer;
    private ReceiverProbe? receiver;
    private Task? discoveryTransition;
    private bool closeRequested, closingComplete, showingVideo, drawingFrames, fullscreen;
    private bool changingReceiver;
    private WindowState beforeFullscreen;
    private VideoFrame? latestFrame;
    private Guid activeSession;
    private VideoStatistics? statistics;
    private long lastPresented, lastDecoded;
    private long lastStats = Stopwatch.GetTimestamp();
    private string device = "iPhone";
    private string? currentTrack;
    private long generation;
    private int historyRevision, displayedRevision = -1;
    private VideoTarget? videoTarget;
    private bool compatibility;

    public static bool AutoStartDiscovery { get; set; } = true;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) =>
        {
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.Data = Geometry.Parse(maximized
                ? "M 3,0 H 10 V 7 M 0,3 H 7 V 10 H 0 Z" : "M 0,0 H 10 V 10 H 0 Z");
            AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
        };
        statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) => UpdateDetails();
        Loaded += async (_, _) =>
        {
            statusTimer.Start();
            if (AutoStartDiscovery && receiver is null && !closeRequested)
            {
                await ToggleDiscoveryAsync();
            }
        };
        Record("NotAirplay feature preview 12 opened.");
    }

    private async void Discovery_Click(object sender, RoutedEventArgs e)
    {
        if (changingReceiver || closeRequested) return;
        discoveryTransition = ToggleDiscoveryAsync();
        await discoveryTransition;
    }

    private async Task ToggleDiscoveryAsync()
    {
        changingReceiver = true;
        DiscoveryButton.IsEnabled = false;
        try
        {
            if (receiver is null)
            {
                SetStatus("Starting", "#9EAFCA");
                HeroTitle.Text = "Getting ready.";
                HeroDetail.Text = "Making your PC discoverable.";
                long current = ++generation;
                videoTarget = new VideoTarget(NativeSurface.Handle, !compatibility);
                receiver = await ReceiverProbe.StartAsync(Record, QueueVideoFrame, videoTarget,
                    update => Dispatcher.BeginInvoke(() =>
                    {
                        if (current == generation && !closeRequested) ApplySession(update);
                    }));
                receiver.VolumeChanged += volume => Dispatcher.BeginInvoke(() => ApplyVolume(volume));
                receiver.TrackChanged += track => Dispatcher.BeginInvoke(() => ApplyTrack(track));
                Record($"Receiver started. TCP port {receiver.Port}. {receiver.DiscoverySummary}");
                if (activeSession == Guid.Empty) ShowHome("Ready when you are.", "Select NotAirplay in Screen Mirroring or Audio on your iPhone.");
            }
            else
            {
                var active = receiver;
                receiver = null;
                ++generation;
                ClearVideo();
                await active.DisposeAsync();
                Record($"Receiver stopped. {active.DiscoverySummary}");
                ShowHome("A little more screen.", "Your iPhone. On your PC. Just like that.");
            }
        }
        catch (Exception ex) when (ex is SocketException or Win32Exception or InvalidOperationException)
        {
            Record($"Receiver error: {ex.Message}");
            ++generation;
            receiver = null;
            ShowHome("Couldn't start the receiver.", ex.Message);
            SetStatus("Unavailable", "#D68D8D");
        }
        finally
        {
            changingReceiver = false;
            DiscoveryButton.IsEnabled = !closeRequested;
        }
    }

    private void ApplySession(MirrorUpdate update)
    {
        if (update.State == MirrorState.Connecting)
        {
            ClearVideo();
            activeSession = update.SessionId;
            statistics = update.Statistics;
            device = update.Detail;
            lastPresented = lastDecoded = 0;
            lastStats = Stopwatch.GetTimestamp();
            HeroTitle.Text = "Connecting.";
            HeroDetail.Text = "Your iPhone will appear here in a moment.";
            DiscoveryButton.Visibility = Visibility.Collapsed;
            ConnectInstructions.Visibility = Visibility.Collapsed;
            StopVideoButton.Visibility = Visibility.Visible;
            SetStatus("Connecting", "#A6B6CF");
            StatusDetail.Text = "Establishing a connection";
        }
        else if (update.SessionId == activeSession)
        {
            statistics = update.Statistics;
            switch (update.State)
            {
                case MirrorState.Streaming:
                    ReceiverHome.Visibility = Visibility.Collapsed;
                    bool isAudioOnly = update.Detail is "audio" or "audio-paused";
                    if (isAudioOnly)
                    {
                        showingVideo = false;
                        VideoLayer.Visibility = Visibility.Hidden;
                        NativeSurface.Visibility = Visibility.Hidden;
                        MirrorImage.Visibility = Visibility.Collapsed;
                        AudioOnlyPanel.Visibility = Visibility.Visible;
                        AudioDeviceText.Text = device;
                        StopVideoButton.Content = "Disconnect";
                        StopVideoButton.Visibility = Visibility.Visible;
                        FullscreenButton.Visibility = Visibility.Collapsed;
                        if (update.Detail == "audio-paused")
                        {
                            SetStatus("Connected", "#81C7A0");
                            LiveDot.Fill = Brush("#63636C");
                            StatusDetail.Text = $"{device} · Paused";
                            AudioTrackText.Text = currentTrack ?? "Connected · Ready to play";
                            Title = currentTrack is not null
                                ? $"{currentTrack} (Paused) — {device} — NotAirplay"
                                : $"{device} (Connected) — NotAirplay";
                        }
                        else
                        {
                            SetStatus("Audio", "#81C7A0");
                            LiveDot.Fill = StatusDot.Fill;
                            StatusDetail.Text = currentTrack is not null ? $"{device} · {currentTrack}" : $"{device} · Audio streaming";
                            AudioTrackText.Text = currentTrack ?? "Streaming audio to your PC";
                            Title = currentTrack is not null
                                ? $"{currentTrack} — {device} — NotAirplay"
                                : $"{device} (Audio) — NotAirplay";
                        }
                    }
                    else
                    {
                        AudioOnlyPanel.Visibility = Visibility.Collapsed;
                        StopVideoButton.Content = "Stop mirroring";
                        showingVideo = true;
                        VideoLayer.Visibility = Visibility.Visible;
                        bool native = update.Detail == "native";
                        NativeSurface.Visibility = native ? Visibility.Visible : Visibility.Hidden;
                        MirrorImage.Visibility = native ? Visibility.Collapsed : Visibility.Visible;
                        if (!native && !drawingFrames)
                        {
                            CompositionTarget.Rendering += DrawLatestFrame;
                            drawingFrames = true;
                        }
                        else if (native)
                        {
                            StopDrawing();
                            Interlocked.Exchange(ref latestFrame, null)?.Dispose();
                            MirrorImage.Source = null;
                        }
                        StopVideoButton.Visibility = FullscreenButton.Visibility = Visibility.Visible;
                        SetStatus("Mirroring", "#81C7A0");
                        LiveDot.Fill = StatusDot.Fill;
                        StatusDetail.Text = device;
                        Title = $"{device} — NotAirplay";
                    }
                    break;
                case MirrorState.Stopped:
                    Record("Mirroring stopped. Ready for another connection.");
                    ShowHome("Mirroring stopped.", "Select NotAirplay on your iPhone to connect again.");
                    break;
                case MirrorState.Failed:
                    Record($"Session ended: {update.Detail}");
                    ShowHome("Connection interrupted.", "Try connecting again. Session details are available below.");
                    SetStatus("Ready to retry", "#D8B37D");
                    break;
            }
        }
    }

    private void ClearVideo()
    {
        showingVideo = false;
        activeSession = Guid.Empty;
        StopDrawing();
        Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        NativeSurface.Visibility = Visibility.Hidden;
        MirrorImage.Source = null;
        MirrorImage.Visibility = Visibility.Collapsed;
        VideoLayer.Visibility = Visibility.Hidden;
        AudioOnlyPanel.Visibility = Visibility.Collapsed;
        AudioDeviceText.Text = "iPhone";
        AudioTrackText.Text = "AirPlay Audio";
        AudioVolumeText.Text = "";
        currentTrack = null;
        StopVideoButton.Content = "Stop mirroring";
        ReceiverHome.Visibility = Visibility.Visible;
        StopVideoButton.Visibility = FullscreenButton.Visibility = Visibility.Collapsed;
        Title = "NotAirplay";
        if (fullscreen) ToggleFullscreen();
    }

    private void ApplyVolume(float volume)
    {
        AudioVolumeText.Text = $"Volume {volume * 100:F0}%";
    }

    private void ApplyTrack(string track)
    {
        if (string.IsNullOrWhiteSpace(track))
        {
            currentTrack = null;
            AudioTrackText.Text = "Connected · Ready to play";
            if (AudioOnlyPanel.Visibility == Visibility.Visible)
            {
                StatusDetail.Text = $"{device} · Paused";
                Title = $"{device} (Connected) — NotAirplay";
            }
            return;
        }

        currentTrack = track;
        AudioTrackText.Text = track;
        if (AudioOnlyPanel.Visibility == Visibility.Visible)
        {
            StatusDetail.Text = $"{device} · {track}";
            Title = $"{track} — {device} — NotAirplay";
        }
        else if (showingVideo)
        {
            StatusDetail.Text = $"{device} · {track}";
            Title = $"{track} — {device} — NotAirplay";
        }
    }

    private void ShowHome(string title, string detail)
    {
        ClearVideo();
        HeroTitle.Text = title;
        HeroDetail.Text = detail;
        DiscoveryButton.Visibility = receiver is null ? Visibility.Visible : Visibility.Collapsed;
        DiscoveryButton.Content = "Start receiver";
        DiscoveryButton.Background = Brush("#E8E8ED");
        DiscoveryButton.Foreground = Brush("#171719");
        DiscoveryButton.BorderBrush = Brush("#E8E8ED");
        ConnectInstructions.Visibility = receiver is null ? Visibility.Collapsed : Visibility.Visible;
        SetStatus(receiver is null ? "Offline" : "Ready", receiver is null ? "#717178" : "#81C7A0");
        LiveDot.Fill = Brush("#63636C");
        StatusDetail.Text = receiver is null ? "Same network. No cables." : "Waiting for your iPhone";
    }

    private void QueueVideoFrame(VideoFrame frame)
    {
        var previous = Interlocked.Exchange(ref latestFrame, frame);
        if (previous is not null) { previous.Statistics?.Skip(); previous.Dispose(); }
    }

    private void DrawLatestFrame(object? sender, EventArgs e)
    {
        using var frame = Interlocked.Exchange(ref latestFrame, null);
        if (frame is null) return;
        if (!showingVideo || frame.SessionId != activeSession || closeRequested || WindowState == WindowState.Minimized)
        {
            frame.Statistics?.Skip();
            return;
        }
        var bitmap = MirrorImage.Source as WriteableBitmap;
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            MirrorImage.Source = bitmap;
        }
        MirrorImage.RenderTransform = frame.BottomUp ? FlipVideo : Transform.Identity;
        // Never block the UI waiting for the compositor's copy of the previous frame.
        if (!bitmap.TryLock(new Duration(TimeSpan.Zero)))
        {
            frame.Statistics?.Skip();
            return;
        }
        try
        {
            unsafe
            {
                fixed (byte* source = frame.Pixels)
                {
                    byte* destination = (byte*)bitmap.BackBuffer;
                    if (bitmap.BackBufferStride == frame.Stride)
                        Buffer.MemoryCopy(source, destination, (long)bitmap.BackBufferStride * frame.Height, (long)frame.Stride * frame.Height);
                    else
                        for (int y = 0; y < frame.Height; y++)
                            Buffer.MemoryCopy(source + y * frame.Stride, destination + y * bitmap.BackBufferStride, bitmap.BackBufferStride, frame.Width * 3);
                }
            }
            bitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
            frame.Statistics?.Present();
        }
        finally { bitmap.Unlock(); }
    }

    private static readonly ScaleTransform FlipVideo = MakeFlip();
    private static ScaleTransform MakeFlip() { var flip = new ScaleTransform(1, -1); flip.Freeze(); return flip; }

    private void StopDrawing()
    {
        if (!drawingFrames) return;
        CompositionTarget.Rendering -= DrawLatestFrame;
        drawingFrames = false;
    }

    private void Record(string message)
    {
        lock (historyLock)
        {
            if (history.Count == 160) history.Dequeue();
            history.Enqueue($"{DateTimeOffset.Now:HH:mm:ss}  {message}");
            historyRevision++;
        }
    }

    private void UpdateDetails()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(lastStats, now).TotalSeconds;
        if (statistics is not null && seconds > 0)
        {
            long presented = statistics.Presented, decoded = statistics.Decoded;
            double fps = (presented - lastPresented) / seconds;
            if (showingVideo)
            {
                string trackPart = !string.IsNullOrWhiteSpace(currentTrack) ? $"  ·  {currentTrack}" : "";
                StatusDetail.Text = statistics.SecondsSincePacket > 3 ? "Connected · waiting for frames"
                    : $"{statistics.Width} × {statistics.Height}  ·  {fps:F0} fps{trackPart}";
            }
            else if (AudioOnlyPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(currentTrack))
            {
                StatusDetail.Text = $"{device} · {currentTrack}";
            }
            if (DiagnosticsPanel.Visibility == Visibility.Visible)
                StatsDetail.Text = $"{statistics.Decoder}\n{statistics.Width} × {statistics.Height} · {(decoded - lastDecoded) / seconds:F0} decoded fps\n" +
                    $"Presented: {presented}   Skipped: {statistics.Skipped}\n" +
                    $"Received: {statistics.Bytes / 1048576.0:F1} MB\n" +
                    $"Local processing: {statistics.ProcessingMilliseconds / Math.Max(1, statistics.Packets):F2} ms/frame\nAAC audio enabled" +
                    (!string.IsNullOrWhiteSpace(currentTrack) ? $"\nTrack: {currentTrack}" : "");
            lastPresented = presented; lastDecoded = decoded;
        }
        lastStats = now;
        if (DiagnosticsPanel.Visibility == Visibility.Visible)
        {
            lock (historyLock)
            {
                if (displayedRevision != historyRevision)
                {
                    DiagnosticLog.Text = string.Join("\n", history);
                    DiagnosticLog.ScrollToEnd();
                    displayedRevision = historyRevision;
                }
            }
        }
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private void SetStatus(string title, string color) { StatusLabel.Text = title; StatusDot.Fill = Brush(color); }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string diagnostic;
        lock (historyLock) diagnostic = "NotAirplay feature preview 11\r\n" + string.Join("\r\n", history);
        if (receiver is not null) diagnostic += "\r\n" + receiver.DiscoverySummary;
        if (statistics is not null) diagnostic += "\r\n" + statistics.Summary;
        try { Clipboard.SetText(diagnostic); StatusDetail.Text = "Diagnostics copied"; }
        catch (ExternalException) { StatusDetail.Text = "Clipboard busy. Try again."; }
    }

    private void StopVideo_Click(object sender, RoutedEventArgs e)
    {
        receiver?.StopMirroring();
        ShowHome("Mirroring stopped.", "Select NotAirplay on your iPhone to connect again.");
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        bool open = DiagnosticsPanel.Visibility != Visibility.Visible;
        // Keep the main content usable at the minimum size.
        if (open && ActualWidth < 900) Width = 960;
        MinWidth = open ? 900 : 620;
        DiagnosticsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetails();
    }

    private void Compatibility_Click(object sender, RoutedEventArgs e)
    {
        compatibility = !compatibility;
        if (videoTarget is not null) videoTarget.PreferNative = !compatibility;
        CompatibilityButton.Content = compatibility ? "Compatibility mode: on" : "Compatibility mode: off";
        CompatibilityButton.BorderBrush = Brush(compatibility ? "#9BBDF6" : "#35353A");
        StatusDetail.Text = "Playback mode applies to the next connection";
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Background = Brush(Topmost ? "#303039" : "#00000000");
        PinButton.ToolTip = Topmost ? "Unpin window" : "Keep on top";
        AutomationProperties.SetName(PinButton, Topmost ? "Unpin window" : "Keep on top");
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void Stage_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2 && showingVideo) ToggleFullscreen(); }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && showingVideo || e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; }
    }

    private void ToggleFullscreen()
    {
        fullscreen = !fullscreen;
        if (fullscreen)
        {
            beforeFullscreen = WindowState;
            DiagnosticsPanel.Visibility = Visibility.Collapsed;
            MinWidth = 620;
            WindowChrome.SetWindowChrome(this, null);
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 62, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0) });
            WindowState = WindowState.Normal;
            WindowState = beforeFullscreen;
        }
        TitleBar.Visibility = Footer.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        HeaderRow.Height = new GridLength(fullscreen ? 0 : 62);
        FooterRow.Height = new GridLength(fullscreen ? 0 : 58);
        Stage.Margin = fullscreen ? new Thickness(0) : new Thickness(18, 0, 18, 0);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closingComplete || e.Cancel) return;
        e.Cancel = true;
        if (closeRequested) return;
        closeRequested = true;
        statusTimer.Stop();
        DiscoveryButton.IsEnabled = false;
        StopVideoButton.IsEnabled = false;
        _ = StopAndCloseAsync();
    }

    private async Task StopAndCloseAsync()
    {
        await Task.Yield();
        if (discoveryTransition is not null) await discoveryTransition;
        ++generation;
        ClearVideo();
        if (receiver is not null)
        {
            var active = receiver; receiver = null;
            await active.DisposeAsync();
        }
        Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        closingComplete = true;
        Close();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

