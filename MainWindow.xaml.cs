using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace NotAirplay;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush BrushReady = FreezeBrush("#10B981");
    private static readonly SolidColorBrush BrushReadyText = FreezeBrush("#34D399");
    private static readonly SolidColorBrush BrushCyan = FreezeBrush("#38BDF8");
    private static readonly SolidColorBrush BrushRetry = FreezeBrush("#F59E0B");
    private static readonly SolidColorBrush BrushOffline = FreezeBrush("#71717A");
    private static readonly SolidColorBrush BrushMuted = FreezeBrush("#A1A1AA");
    private static readonly SolidColorBrush BrushAudioLiveBg = FreezeBrush("#1A10B981");
    private static readonly SolidColorBrush BrushAudioLiveBorder = FreezeBrush("#4D10B981");
    private static readonly SolidColorBrush BrushAudioPausedBg = FreezeBrush("#18FFFFFF");
    private static readonly SolidColorBrush BrushAudioPausedBorder = FreezeBrush("#26FFFFFF");
    private static readonly SolidColorBrush BrushPinnedBg = FreezeBrush("#2610B981");
    private static readonly SolidColorBrush BrushTransparent = FreezeBrush("#00000000");

    private static ResourceDictionary? fallbackThemeDictionary;

    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsContinuous = 0x80000000;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private readonly object historyLock = new();
    private readonly Queue<string> history = new();
    private readonly DispatcherTimer statusTimer;
    private ReceiverProbe? receiver;
    private Task? discoveryTransition;
    private bool closeRequested, closingComplete, showingVideo, showingAudio, drawingFrames, fullscreen;
    private bool changingReceiver, syncingToggle, syncingAudioDevices, videoSurfaceOpaque, pendingNameRestart;
    private uint executionState = EsContinuous;
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
    private AppSettings settings = new();
    private string customName = "";
    private int targetFps = 60;
    private string? selectedAudioDeviceId;

    public static bool AutoStartDiscovery { get; set; } = true;
    public static bool UseSavedSettings { get; set; } = true;

    private string ActiveReceiverName => receiver?.AdvertisedName ?? ReceiverIdentity.SanitizeName(customName);

    private sealed record AudioDeviceChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    public MainWindow()
    {
        EnsureThemeResources();
        InitializeComponent();
        StateChanged += (_, _) => UpdateWindowChromeBounds();

        if (UseSavedSettings)
        {
            settings = AppSettings.Load();
            customName = settings.CustomName;
            targetFps = settings.TargetFps;
            selectedAudioDeviceId = settings.AudioDeviceId;
        }

        statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) => UpdateDetails();

        Loaded += async (_, _) =>
        {
            UpdateResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
            RefreshAudioOutputDevices();
            statusTimer.Start();
            if (AutoStartDiscovery && receiver is null && !closeRequested)
            {
                discoveryTransition = ToggleDiscoveryAsync();
                await discoveryTransition;
            }
        };

        Stage.SizeChanged += (_, e) => UpdateStageCardsLayout(e.NewSize.Width);
        SetNavSelected(NavReceiver);
        SyncPreferenceControls();
        RefreshAudioOutputDevices();
        Record("NotAirplay receiver opened.");
    }

    private void EnsureThemeResources()
    {
        if (Application.Current is null)
        {
            var testApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            testApp.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
            testApp.Resources.MergedDictionaries.Add(new ControlsDictionary());
            ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, false);
            ApplicationAccentColorManager.Apply(
                Color.FromRgb(0x04, 0x78, 0x57),
                Color.FromRgb(0x34, 0xD3, 0x99),
                Color.FromRgb(0x10, 0xB9, 0x81),
                Color.FromRgb(0x06, 0x5F, 0x46));
        }

        if (Application.Current?.Resources.Contains("AcousticLiveBrush") == true)
            return;

        fallbackThemeDictionary ??= BuildFallbackThemeDictionary();
        if (Application.Current is not null)
            Application.Current.Resources.MergedDictionaries.Add(fallbackThemeDictionary);
        else
            Resources.MergedDictionaries.Add(fallbackThemeDictionary);
    }

    private static ResourceDictionary BuildFallbackThemeDictionary()
    {
        var container = new ResourceDictionary();
        container["AcousticLiveBrush"] = BrushReady;
        container["AcousticLiveTextBrush"] = BrushReadyText;
        container["AcousticLiveSubtleBrush"] = BrushAudioLiveBg;
        container["AcousticLiveBorderBrush"] = BrushAudioLiveBorder;
        container["AcousticWaveCyanBrush"] = BrushCyan;
        container["AcousticWarningBrush"] = BrushRetry;
        container["AcousticWarningTextBrush"] = FreezeBrush("#FBBF24");
        container["AcousticDangerBrush"] = FreezeBrush("#B91C1C");
        container["AcousticDangerHoverBrush"] = FreezeBrush("#991B1B");
        container["AcousticDangerPressedBrush"] = FreezeBrush("#7F1D1D");
        container["AcousticDangerBorderBrush"] = FreezeBrush("#DC2626");
        container["AcousticOfflineBrush"] = BrushOffline;
        container["AcousticStageFallbackBrush"] = FreezeBrush("#202020");
        return container;
    }

    private void SetVideoPresentationSurface(bool videoActive)
    {
        if (videoSurfaceOpaque == videoActive) return;
        videoSurfaceOpaque = videoActive;
        if (videoActive)
            Background = Brushes.Black;
        else
            SetResourceReference(BackgroundProperty, "AcousticStageFallbackBrush");
    }

    private void UpdateWindowChromeBounds()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(maximized
            ? "M 3,0 H 10 V 7 M 0,3 H 7 V 10 H 0 Z" : "M 0,0 H 10 V 10 H 0 Z");
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
        WindowOuterBorder.BorderThickness = new Thickness(fullscreen ? 0 : 1);
        WindowOuterBorder.Padding = maximized && !fullscreen ? new Thickness(7) : new Thickness(0);
    }

    private static SolidColorBrush FreezeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
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
                SetStatus("Starting", BrushCyan);
                HeroTitle.Text = "Getting ready.";
                HeroDetail.Text = "Making your PC discoverable.";
                long current = ++generation;
                videoTarget = new VideoTarget(NativeSurface.Handle, !compatibility, targetFps);
                receiver = await ReceiverProbe.StartAsync(
                    Record,
                    QueueVideoFrame,
                    videoTarget,
                    update => Dispatcher.BeginInvoke(() =>
                    {
                        if (current == generation && !closeRequested) ApplySession(update);
                    }),
                    () => Volatile.Read(ref targetFps),
                    customName,
                    () => selectedAudioDeviceId);
                receiver.VolumeChanged += volume => Dispatcher.BeginInvoke(() => ApplyVolume(volume));
                receiver.TrackChanged += track => Dispatcher.BeginInvoke(() => ApplyTrack(track));
                UpdateBrandLabels();
                Record($"Receiver started ({ActiveReceiverName}). TCP port {receiver.Port}. {receiver.DiscoverySummary}");
                if (activeSession == Guid.Empty)
                    ShowHome("Ready for your iPhone.", $"Select {ActiveReceiverName} in Screen Mirroring or Audio on your iPhone.");
            }
            else
            {
                var active = receiver;
                receiver = null;
                ++generation;
                ClearVideo();
                await active.DisposeAsync();
                Record($"Receiver stopped. {active.DiscoverySummary}");
                ShowHome("Receiver paused.", "Select Start receiver to advertise on your local network.");
            }
        }
        catch (Exception ex) when (ex is SocketException or Win32Exception or InvalidOperationException)
        {
            Record($"Receiver error: {ex.Message}");
            ++generation;
            receiver = null;
            ShowHome("Couldn't start the receiver.", ex.Message);
            SetStatus("Unavailable", BrushRetry);
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
            UpdateStageCardsLayout(Stage.ActualWidth > 0 ? Stage.ActualWidth : Width);
            StopVideoButton.Content = "Disconnect";
            StopVideoButton.Visibility = Visibility.Visible;
            SetStatus("Connecting", BrushCyan);
            LiveDot.Visibility = Visibility.Visible;
            LiveDot.Fill = BrushCyan;
            StatusDetail.Text = "Establishing a connection";
        }
        else if (update.SessionId == activeSession)
        {
            statistics = update.Statistics;
            switch (update.State)
            {
                case MirrorState.Streaming:
                    bool isAudioOnly = update.Detail is "audio" or "audio-paused";
                    if (isAudioOnly)
                    {
                        showingVideo = false;
                        showingAudio = true;
                        SetVideoPresentationSurface(false);
                        RootNav.Visibility = Visibility.Visible;
                        RootNav.IsPaneVisible = !fullscreen;
                        VideoLayer.Visibility = Visibility.Hidden;
                        NativeSurface.Visibility = Visibility.Hidden;
                        MirrorImage.Visibility = Visibility.Collapsed;
                        ReceiverHome.Visibility = Visibility.Collapsed;
                        AudioOnlyPanel.Visibility = Visibility.Visible;
                        AudioDeviceText.Text = device;
                        StopVideoButton.Content = "Disconnect";
                        StopVideoButton.Visibility = Visibility.Visible;
                        FullscreenButton.Visibility = Visibility.Collapsed;
                        SetNavSelected(NavAudio);

                        bool paused = update.Detail == "audio-paused";
                        UpdateAudioBadge(paused);
                        UpdateExecutionState(ResolveExecutionState(streamingVideo: false, streamingAudio: true, audioPaused: paused));
                        RefreshAudioOutputDevices();
                        if (paused)
                        {
                            SetStatus("Connected", BrushReady);
                            LiveDot.Visibility = Visibility.Visible;
                            LiveDot.Fill = BrushOffline;
                            StatusDetail.Text = $"{device} · Paused";
                            AudioTrackText.Text = currentTrack ?? "Connected · Ready to play";
                            Title = FormatWindowTitle(currentTrack is not null
                                ? $"{currentTrack} (Paused) — {device}"
                                : $"{device} (Connected)");
                        }
                        else
                        {
                            SetStatus("Audio", BrushReady);
                            LiveDot.Visibility = Visibility.Visible;
                            LiveDot.Fill = BrushReady;
                            StatusDetail.Text = currentTrack is not null ? $"{device} · {currentTrack}" : $"{device} · Audio streaming";
                            AudioTrackText.Text = currentTrack ?? "Streaming audio to your PC";
                            Title = FormatWindowTitle(currentTrack is not null
                                ? $"{currentTrack} — {device}"
                                : $"{device} (Audio)");
                        }
                    }
                    else
                    {
                        showingAudio = false;
                        showingVideo = true;
                        AudioOnlyPanel.Visibility = Visibility.Collapsed;
                        ReceiverHome.Visibility = Visibility.Collapsed;
                        RootNav.Visibility = Visibility.Collapsed;
                        RootNav.IsPaneVisible = false;
                        SetVideoPresentationSurface(true);
                        StopVideoButton.Content = "Stop mirroring";
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
                        SetStatus("Mirroring", BrushReady);
                        LiveDot.Visibility = Visibility.Visible;
                        LiveDot.Fill = BrushReady;
                        StatusDetail.Text = device;
                        Title = FormatWindowTitle(device);
                        UpdateExecutionState(ResolveExecutionState(streamingVideo: true, streamingAudio: false, audioPaused: false));
                    }
                    break;
                case MirrorState.Stopped:
                    Record("Mirroring stopped. Ready for another connection.");
                    ShowHome("Mirroring stopped.", $"Select {ActiveReceiverName} on your iPhone to connect again.");
                    _ = RestartReceiverIfPendingRenameAsync();
                    break;
                case MirrorState.Failed:
                    Record($"Session ended: {update.Detail}");
                    ShowHome("Connection interrupted.", "Try connecting again. Session details are available below.");
                    SetStatus("Ready to retry", BrushRetry);
                    LiveDot.Visibility = Visibility.Visible;
                    LiveDot.Fill = BrushRetry;
                    _ = RestartReceiverIfPendingRenameAsync();
                    break;
            }
        }
    }

    private void UpdateAudioBadge(bool paused)
    {
        if (paused)
        {
            AudioStateBadge.Background = BrushAudioPausedBg;
            AudioStateBadge.BorderBrush = BrushAudioPausedBorder;
            AudioStateDot.Fill = BrushOffline;
            AudioStateBadgeText.Foreground = BrushMuted;
            AudioStateBadgeText.Text = "Connected · Standby";
        }
        else
        {
            AudioStateBadge.Background = BrushAudioLiveBg;
            AudioStateBadge.BorderBrush = BrushAudioLiveBorder;
            AudioStateDot.Fill = BrushReady;
            AudioStateBadgeText.Foreground = BrushReadyText;
            AudioStateBadgeText.Text = "Streaming audio";
        }
    }

    private void ClearVideo()
    {
        showingVideo = false;
        showingAudio = false;
        activeSession = Guid.Empty;
        UpdateExecutionState(EsContinuous);
        StopDrawing();
        Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        NativeSurface.Visibility = Visibility.Hidden;
        MirrorImage.Source = null;
        MirrorImage.Visibility = Visibility.Collapsed;
        VideoLayer.Visibility = Visibility.Hidden;
        AudioOnlyPanel.Visibility = Visibility.Collapsed;
        AudioDeviceText.Text = "iPhone";
        AudioTrackText.Text = "AirPlay Audio";
        AudioVolumeText.Text = "—";
        AudioVolumeText.Foreground = BrushMuted;
        AudioVolumeBar.Value = 0;
        AudioVolumeIcon.Symbol = SymbolRegular.Speaker224;
        currentTrack = null;
        StopVideoButton.Content = "Stop mirroring";
        ReceiverHome.Visibility = Visibility.Visible;
        StopVideoButton.Visibility = FullscreenButton.Visibility = Visibility.Collapsed;
        SetVideoPresentationSurface(false);
        RootNav.Visibility = Visibility.Visible;
        if (!fullscreen) RootNav.IsPaneVisible = true;
        SetNavSelected(NavReceiver);
        Title = FormatWindowTitle(null);
        if (fullscreen) ToggleFullscreen();
    }

    private void ApplyVolume(float volume)
    {
        double pct = Math.Clamp(volume * 100.0, 0.0, 100.0);
        AudioVolumeText.Foreground = BrushReadyText;
        AudioVolumeText.Text = $"{pct:F0}%";
        AudioVolumeBar.Value = pct;
        AudioVolumeIcon.Symbol = pct switch
        {
            <= 0.5 => SymbolRegular.SpeakerMute24,
            < 35.0 => SymbolRegular.Speaker024,
            < 75.0 => SymbolRegular.Speaker124,
            _ => SymbolRegular.Speaker224,
        };
    }

    private void ApplyTrack(string track)
    {
        if (string.IsNullOrWhiteSpace(track))
        {
            currentTrack = null;
            AudioTrackText.Text = "Connected · Ready to play";
            if (AudioOnlyPanel.Visibility == Visibility.Visible)
            {
                UpdateAudioBadge(paused: true);
                UpdateExecutionState(ResolveExecutionState(streamingVideo: false, streamingAudio: true, audioPaused: true));
                StatusDetail.Text = $"{device} · Paused";
                Title = FormatWindowTitle($"{device} (Connected)");
            }
            return;
        }

        currentTrack = track;
        AudioTrackText.Text = track;
        if (AudioOnlyPanel.Visibility == Visibility.Visible)
        {
            UpdateAudioBadge(paused: false);
            UpdateExecutionState(ResolveExecutionState(streamingVideo: false, streamingAudio: true, audioPaused: false));
            StatusDetail.Text = $"{device} · {track}";
            Title = FormatWindowTitle($"{track} — {device}");
        }
        else if (showingVideo)
        {
            StatusDetail.Text = $"{device} · {track}";
            Title = FormatWindowTitle($"{track} — {device}");
        }
    }

    private void ShowHome(string title, string detail)
    {
        ClearVideo();
        HeroTitle.Text = title;
        HeroDetail.Text = detail;
        DiscoveryButton.Visibility = receiver is null ? Visibility.Visible : Visibility.Collapsed;
        ConnectInstructions.Visibility = receiver is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateStageCardsLayout(Stage.ActualWidth > 0 ? Stage.ActualWidth : Width);
        bool online = receiver is not null;
        SetStatus(online ? "Ready" : "Offline", online ? BrushReady : BrushOffline);
        LiveDot.Visibility = Visibility.Collapsed;
        LiveDot.Fill = online ? BrushReady : BrushOffline;
        StatusDetail.Text = online ? "Waiting for your iPhone" : "Same network. No cables.";
    }

    private void SetNavSelected(NavigationViewItem item)
    {
        if (item == NavReceiver)
        {
            NavAudio.Deactivate(RootNav);
            NavTelemetry.Deactivate(RootNav);
            NavReceiver.Activate(RootNav);
        }
        else if (item == NavAudio)
        {
            NavReceiver.Deactivate(RootNav);
            NavTelemetry.Deactivate(RootNav);
            NavAudio.Activate(RootNav);
        }
    }

    private void NavReceiver_Click(object sender, RoutedEventArgs e)
    {
        if (showingVideo) return;
        AudioOnlyPanel.Visibility = Visibility.Collapsed;
        ReceiverHome.Visibility = Visibility.Visible;
        SetNavSelected(NavReceiver);
    }

    private void NavAudio_Click(object sender, RoutedEventArgs e)
    {
        if (showingVideo) return;
        ReceiverHome.Visibility = Visibility.Collapsed;
        AudioOnlyPanel.Visibility = Visibility.Visible;
        RefreshAudioOutputDevices();
        if (!showingAudio)
        {
            AudioDeviceText.Text = "No active stream";
            AudioTrackText.Text = $"Select {ActiveReceiverName} from iPhone Control Center audio picker to stream lossless ALAC or AAC.";
            AudioStateBadgeText.Text = "Idle standby";
            AudioStateBadge.Background = BrushAudioPausedBg;
            AudioStateBadge.BorderBrush = BrushAudioPausedBorder;
            AudioStateDot.Fill = BrushOffline;
            AudioStateBadgeText.Foreground = BrushMuted;
            AudioVolumeText.Text = "—";
            AudioVolumeText.Foreground = BrushMuted;
            AudioVolumeBar.Value = 0;
            AudioVolumeIcon.Symbol = SymbolRegular.Speaker224;
        }
        SetNavSelected(NavAudio);
    }

    private void NavTelemetry_Click(object sender, RoutedEventArgs e)
    {
        Details_Click(sender, e);
        SetNavSelected(AudioOnlyPanel.Visibility == Visibility.Visible ? NavAudio : NavReceiver);
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
            if (showingVideo && !fullscreen)
            {
                string trackPart = !string.IsNullOrWhiteSpace(currentTrack) ? $"  ·  {currentTrack}" : "";
                StatusDetail.Text = statistics.SecondsSincePacket > 3 ? "Connected · waiting for frames"
                    : $"{statistics.Width} × {statistics.Height}  ·  {fps:F0} fps{trackPart}";
            }
            else if (showingAudio && !string.IsNullOrWhiteSpace(currentTrack))
            {
                StatusDetail.Text = $"{device} · {currentTrack}";
            }
            if (DiagnosticsPanel.Visibility == Visibility.Visible)
                StatsDetail.Text = $"{statistics.Decoder}\n{statistics.Width} × {statistics.Height} · {(decoded - lastDecoded) / seconds:F0} decoded fps ({targetFps} fps max)\n" +
                    $"Presented: {presented}   Skipped: {statistics.Skipped}\n" +
                    $"Received: {statistics.Bytes / 1048576.0:F1} MB\n" +
                    $"Local processing: {statistics.ProcessingMilliseconds / Math.Max(1, statistics.Packets):F2} ms/frame\nIn-process WASAPI audio enabled" +
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

    private void SetStatus(string title, Brush color)
    {
        StatusLabel.Text = title;
        StatusDot.Fill = color;
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string diagnostic;
        lock (historyLock) diagnostic = "NotAirplay\r\n" + string.Join("\r\n", history);
        if (receiver is not null) diagnostic += "\r\n" + receiver.DiscoverySummary;
        if (statistics is not null) diagnostic += "\r\n" + statistics.Summary;
        try { Clipboard.SetText(diagnostic); StatusDetail.Text = "Diagnostics copied"; }
        catch (ExternalException) { StatusDetail.Text = "Clipboard busy. Try again."; }
    }

    private void StopVideo_Click(object sender, RoutedEventArgs e)
    {
        receiver?.StopMirroring();
        ShowHome("Mirroring stopped.", $"Select {ActiveReceiverName} on your iPhone to connect again.");
        _ = RestartReceiverIfPendingRenameAsync();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        bool open = DiagnosticsPanel.Visibility != Visibility.Visible;
        if (open && ActualWidth > 0 && ActualWidth < 900) Width = 960;
        MinWidth = open ? 900 : 620;
        DiagnosticsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SyncDiagnosticsButtons(open);
        UpdateResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
        UpdateDetails();
    }

    private void SyncDiagnosticsButtons(bool open)
    {
        HomeDetailsButton.Content = open ? "Hide telemetry & log" : "Open telemetry & log";
        DetailsButton.Appearance = open ? ControlAppearance.Secondary : ControlAppearance.Transparent;
    }

    private void Compatibility_Click(object sender, RoutedEventArgs e)
    {
        SetCompatibility(!compatibility);
    }

    private void HomeCompatToggle_Click(object sender, RoutedEventArgs e)
    {
        if (syncingToggle) return;
        SetCompatibility(HomeCompatToggle.IsChecked == true);
    }

    private void SetCompatibility(bool value)
    {
        compatibility = value;
        if (videoTarget is not null) videoTarget.PreferNative = !compatibility;
        CompatibilityButton.Content = compatibility ? "Compatibility mode: on" : "Compatibility mode: off";
        CompatibilityButton.Appearance = compatibility ? ControlAppearance.Info : ControlAppearance.Secondary;
        UpdateBrandLabels();
        syncingToggle = true;
        try { HomeCompatToggle.IsChecked = compatibility; }
        finally { syncingToggle = false; }
        StatusDetail.Text = "Playback mode applies to the next connection";
    }

    internal static uint ResolveExecutionState(bool streamingVideo, bool streamingAudio, bool audioPaused)
    {
        if (streamingVideo) return EsContinuous | EsDisplayRequired | EsSystemRequired;
        if (streamingAudio && !audioPaused) return EsContinuous | EsSystemRequired;
        return EsContinuous;
    }

    private void UpdateExecutionState(uint desired)
    {
        if (executionState == desired) return;
        if (SetThreadExecutionState(desired) != 0 || desired == EsContinuous)
            executionState = desired;
    }

    private string FormatWindowTitle(string? prefix)
    {
        string name = ActiveReceiverName;
        string appLabel = name == ReceiverIdentity.ServiceName ? name : $"{name} · {ReceiverIdentity.ServiceName}";
        return string.IsNullOrWhiteSpace(prefix) ? appLabel : $"{prefix} — {appLabel}";
    }

    private void UpdateBrandLabels()
    {
        string name = ActiveReceiverName;
        TitleBarBrandText.Text = name == ReceiverIdentity.ServiceName
            ? ReceiverIdentity.ServiceName
            : $"{ReceiverIdentity.ServiceName} · {name}";
        ConnectMirrorHint.Text = $"Swipe into Control Center, tap Screen Mirroring, and choose {name}.";
        HomeVideoModeText.Text = compatibility
            ? $"FFmpeg compatibility fallback · {targetFps} fps"
            : $"Direct3D 11 · H.264 / HEVC · {targetFps} fps";
        if (activeSession == Guid.Empty && receiver is not null)
        {
            HeroDetail.Text = $"Select {name} in Screen Mirroring or Audio on your iPhone.";
            Title = FormatWindowTitle(null);
        }
    }

    private void SyncPreferenceControls()
    {
        syncingToggle = true;
        try
        {
            ReceiverNameBox.Text = customName;
            ApplyNameButton.IsEnabled = false;
            Fps30Button.IsChecked = targetFps == 30;
            Fps60Button.IsChecked = targetFps == 60;
            Fps120Button.IsChecked = targetFps == 120;
            UpdateBrandLabels();
        }
        finally { syncingToggle = false; }
    }

    private void SaveCurrentSettings()
    {
        if (!UseSavedSettings) return;
        settings.CustomName = customName;
        settings.TargetFps = targetFps;
        settings.AudioDeviceId = selectedAudioDeviceId;
        settings.Save();
    }

    private void ReceiverNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (syncingToggle || ApplyNameButton is null) return;
        string candidate = string.IsNullOrWhiteSpace(ReceiverNameBox.Text) ? "" : ReceiverIdentity.SanitizeName(ReceiverNameBox.Text);
        string current = string.IsNullOrWhiteSpace(customName) ? "" : ReceiverIdentity.SanitizeName(customName);
        ApplyNameButton.IsEnabled = !string.Equals(candidate, current, StringComparison.Ordinal);
    }

    private void ReceiverNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ApplyNameButton.IsEnabled)
        {
            e.Handled = true;
            _ = ApplyReceiverNameAsync();
        }
    }

    private void ApplyName_Click(object sender, RoutedEventArgs e) => _ = ApplyReceiverNameAsync();

    private async Task ApplyReceiverNameAsync()
    {
        if (changingReceiver || closeRequested) return;
        string cleaned = string.IsNullOrWhiteSpace(ReceiverNameBox.Text) ? "" : ReceiverIdentity.SanitizeName(ReceiverNameBox.Text);
        customName = cleaned == ReceiverIdentity.ServiceName ? "" : cleaned;
        syncingToggle = true;
        try
        {
            ReceiverNameBox.Text = customName;
            ApplyNameButton.IsEnabled = false;
        }
        finally { syncingToggle = false; }

        SaveCurrentSettings();
        UpdateBrandLabels();
        Record($"Receiver name set to \"{ReceiverIdentity.SanitizeName(customName)}\".");

        if (receiver is null) return;
        if (activeSession != Guid.Empty)
        {
            pendingNameRestart = true;
            StatusDetail.Text = "Name saved · updates after disconnect";
            return;
        }

        await RestartDiscoveryAsync();
    }

    private async Task RestartReceiverIfPendingRenameAsync()
    {
        if (!pendingNameRestart || changingReceiver || closeRequested || receiver is null || activeSession != Guid.Empty)
            return;
        pendingNameRestart = false;
        await RestartDiscoveryAsync();
    }

    private async Task RestartDiscoveryAsync()
    {
        if (receiver is null || changingReceiver || closeRequested) return;
        discoveryTransition = RestartCoreAsync();
        await discoveryTransition;
    }

    private async Task RestartCoreAsync()
    {
        await ToggleDiscoveryAsync();
        if (receiver is null && !closeRequested)
            await ToggleDiscoveryAsync();
    }

    private void FpsOption_Checked(object sender, RoutedEventArgs e)
    {
        if (syncingToggle || !IsLoaded) return;
        if (sender is RadioButton { Tag: string raw } && int.TryParse(raw, out int chosen))
        {
            int normalized = AppSettings.NormalizeFps(chosen);
            if (normalized == targetFps) return;
            targetFps = normalized;
            if (videoTarget is not null) videoTarget.TargetFps = normalized;
            SaveCurrentSettings();
            UpdateBrandLabels();
            StatusDetail.Text = $"Frame rate set to {normalized} fps · applies next connection";
            Record($"Target frame rate set to {normalized} fps (applies on next connection).");
        }
    }

    private void RefreshAudioOutputDevices()
    {
        if (AudioOutputCombo is null) return;
        syncingAudioDevices = true;
        try
        {
            var items = new List<AudioDeviceChoice> { new("", "System default playback device") };
            foreach (var dev in WasapiVolume.ListOutputDevices())
                items.Add(new AudioDeviceChoice(dev.Id, dev.Name));

            AudioOutputCombo.ItemsSource = items;
            int selectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(selectedAudioDeviceId))
            {
                for (int i = 1; i < items.Count; i++)
                {
                    if (string.Equals(items[i].Id, selectedAudioDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }
            AudioOutputCombo.SelectedIndex = selectedIndex;
        }
        finally { syncingAudioDevices = false; }
    }

    private void AudioOutputCombo_DropDownOpened(object? sender, EventArgs e) => RefreshAudioOutputDevices();

    private void AudioOutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingAudioDevices || AudioOutputCombo.SelectedItem is not AudioDeviceChoice choice) return;
        string? newId = string.IsNullOrWhiteSpace(choice.Id) ? null : choice.Id;
        if (string.Equals(newId, selectedAudioDeviceId, StringComparison.OrdinalIgnoreCase)) return;
        selectedAudioDeviceId = newId;
        SaveCurrentSettings();
        receiver?.SetAudioDevice(selectedAudioDeviceId);
        Record($"Audio output changed to {choice.Label}.");
        if (showingAudio) StatusDetail.Text = $"Audio output: {choice.Label}";
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        SetPinned(!Topmost);
    }

    private void HomePinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (syncingToggle) return;
        SetPinned(HomePinToggle.IsChecked == true);
    }

    private void SetPinned(bool pinned)
    {
        Topmost = pinned;
        PinButton.Background = Topmost ? BrushPinnedBg : BrushTransparent;
        PinIcon.Foreground = Topmost ? BrushReadyText : BrushMuted;
        PinButton.ToolTip = Topmost ? "Unpin window" : "Keep on top";
        AutomationProperties.SetName(PinButton, Topmost ? "Unpin window" : "Keep on top");
        syncingToggle = true;
        try { HomePinToggle.IsChecked = Topmost; }
        finally { syncingToggle = false; }
    }

    private void RootShellGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double windowWidth)
    {
        bool drawerOpen = DiagnosticsPanel.Visibility == Visibility.Visible;
        bool shouldOpenPane = drawerOpen ? windowWidth >= 1120 : windowWidth >= 780;
        var targetMode = shouldOpenPane ? NavigationViewPaneDisplayMode.Left : NavigationViewPaneDisplayMode.LeftMinimal;
        bool modeChanged = RootNav.PaneDisplayMode != targetMode;
        RootNav.OpenPaneLength = 188;
        if (modeChanged)
            RootNav.PaneDisplayMode = targetMode;
        if (RootNav.IsPaneOpen != shouldOpenPane)
            RootNav.IsPaneOpen = shouldOpenPane;
        if (modeChanged && !IsLoaded)
            RootNav.UpdateLayout();
    }

    private void UpdateStageCardsLayout(double stageWidth)
    {
        bool showConnect = ConnectInstructions.Visibility == Visibility.Visible;
        bool stackVertically = !showConnect || (stageWidth > 0 && stageWidth < 580);
        if (stackVertically)
        {
            HomeCardSpacer.Width = new GridLength(0);
            HomeCardCol2.Width = new GridLength(0);
            HomeCardRowSpacer.Height = new GridLength(showConnect ? 16 : 0);
            Grid.SetColumn(HomeCapabilitiesCard, 0);
            Grid.SetRow(HomeCapabilitiesCard, showConnect ? 2 : 0);
        }
        else
        {
            HomeCardSpacer.Width = new GridLength(16);
            HomeCardCol2.Width = new GridLength(1, GridUnitType.Star);
            HomeCardRowSpacer.Height = new GridLength(0);
            Grid.SetColumn(HomeCapabilitiesCard, 2);
            Grid.SetRow(HomeCapabilitiesCard, 0);
        }

        bool stackAudio = stageWidth > 0 && stageWidth < 580;
        if (stackAudio)
        {
            AudioCardSpacer.Width = new GridLength(0);
            AudioCardCol2.Width = new GridLength(0);
            AudioCardRowSpacer.Height = new GridLength(16);
            Grid.SetColumn(AudioVolumeCard, 0);
            Grid.SetRow(AudioVolumeCard, 2);
        }
        else
        {
            AudioCardSpacer.Width = new GridLength(16);
            AudioCardCol2.Width = new GridLength(1, GridUnitType.Star);
            AudioCardRowSpacer.Height = new GridLength(0);
            Grid.SetColumn(AudioVolumeCard, 2);
            Grid.SetRow(AudioVolumeCard, 0);
        }
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
            SyncDiagnosticsButtons(false);
            MinWidth = 620;
            WindowChrome.SetWindowChrome(this, null);
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 48,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12)
            });
            WindowState = WindowState.Normal;
            WindowState = beforeFullscreen;
        }
        UpdateWindowChromeBounds();
        TitleBar.Visibility = Footer.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        HeaderRow.Height = new GridLength(fullscreen ? 0 : 48);
        FooterRow.Height = new GridLength(fullscreen ? 0 : 44);
        RootNav.Visibility = !fullscreen && !showingVideo ? Visibility.Visible : Visibility.Collapsed;
        RootNav.IsPaneVisible = !fullscreen && !showingVideo;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closingComplete || e.Cancel) return;
        e.Cancel = true;
        if (closeRequested) return;
        closeRequested = true;
        UpdateExecutionState(EsContinuous);
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
}
