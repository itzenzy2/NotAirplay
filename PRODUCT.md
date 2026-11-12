# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

## Users

iPhone and iPad users on Windows 10 and Windows 11 PCs (desktop monitors, multi-monitor desks, or laptops) who want low-latency wireless screen mirroring and lossless wireless audio playback directly on their PC over local Wi-Fi or Ethernet without cables, Bluetooth audio compression, or companion iOS apps.

## Product Purpose

A lightweight, native Windows desktop AirPlay receiver balanced equally for full-viewport screen mirroring (videos, apps, games, demos) and docked background AirPlay audio standby/playback. Success means instant discoverability from iOS Control Center (under `"NotAirplay"` or an optional custom receiver name), hardware-accelerated Direct3D 11 mirroring with adaptive timestamp pacing and `30/60/120 fps` capability presets, persistent Bluetooth-speaker-like audio connectivity between tracks, and clean Windows 11 Fluent controls that feel genuinely native rather than web-ported boxes.

## Positioning

Local-only, zero-telemetry Windows AirPlay receiver combining Direct3D 11 waitable swap chain presentation (`DXVA2` hardware H.264/HEVC via Media Foundation + FFmpeg compatibility fallback), embedded native C Apple Lossless (`ALAC`) and Media Foundation `AAC-LC` decoding (`+` headless FFmpeg `AAC-ELD`), in-process WASAPI audio rendering with endpoint selection and hardware volume synchronization from iOS volume controls, display/system wake locking while mirroring, and FairPlay v3 / RTSP protocol handling with zero companion mobile apps.

## Operating Context

NotAirplay operates across three primary window states on Windows desktop:
1. **Ready / Standby**: Receiver auto-starts IPv4 mDNS advertising on launch (`AutoStartDiscovery`) using either `"NotAirplay"` or a saved custom name, exposes `30 / 60 / 120 fps` capability selection (default `60`), and waits passively for iOS Control Center -> Screen Mirroring or AirPlay Audio selection.
2. **Audio Playback & Persistent Standby**: Displays active streaming or paused standby (`audio` / `audio-paused`) as a docked desktop audio monitor/player showing connected device name, track title/artist metadata (oversized artwork is dropped on-socket without memory bloat), codec telemetry, output playback device selector (`AudioOutputCombo`), live WASAPI volume level, and quick disconnect.
3. **Video Mirroring & Fullscreen**: Displays low-latency H.264/HEVC mirroring via native D3D11 `HWND` (`NativeSurface`) or fallback `MirrorImage` while holding `SetThreadExecutionState` to prevent monitor/system sleep, supporting double-click / `F11` / `Esc` fullscreen toggling, always-on-top pinning (`Topmost`), and instant disconnect.

Secondary utilities live cleanly accessible without cluttering the main stage:
- Custom receiver name input + target frame rate pills (`30 / 60 / 120 fps`) and `%LocalAppData%\NotAirplay\settings.json` persistence
- Session telemetry & frame statistics (`StatsDetail`: decoder backend, resolution, fps, presented/skipped frames, MB received, ms/frame)
- Compatibility mode toggle (forces FFmpeg fallback on next connection)
- Live diagnostic protocol log & one-click Copy diagnostics

## Capabilities and Constraints

- **Framework & UI Library**: `.NET 8.0-windows` (`x64`) WPF (`System.Windows`) with an opaque `WindowChrome` (`GlassFrameThickness="0"`) host paired with the **`WPF-UI`** NuGet library (`v4.3.0`) for dark Fluent controls (`NavigationView`, `DynamicScrollViewer`, `ToggleSwitch`, `SymbolIcon`, `ThemesDictionary` Dark).
- **Theme Constraint**: Dark-first experience matching desktop monitoring and studio playback usage (user explicitly excluded bright/light default themes). Support semantic `{DynamicResource}` Fluent brushes so high-contrast and system accents remain clean.
- **Functional Continuity**: Auto-start discovery on launch (`DiscoveryButton` preserved for error/stopped fallback), `CompatibilityButton`, `PinButton`, `FullscreenButton`, `StopVideoButton`, and diagnostics export (`CopyDiagnostics_Click`) must remain fully functional.
- **UI Test & Code Contracts**: Automated tests (`tests/UiTests.cs`, `tests/Program.cs`) verify layout rendering at `960x720` and `620x520` and inspect named controls (`HeroTitle`, `HeroDetail`, `ReceiverHome`, `MirrorImage`, `AudioOnlyPanel`, `AudioDeviceText`, `AudioTrackText`, `AudioVolumeText`, `StatusDetail`, `StatusLabel`, `StatusDot`, `LiveDot`, `NativeSurface`, `TitleBar`, `DiagnosticsPanel`, `StatsDetail`, `CompatibilityButton`, `DiagnosticLog`, `DiscoveryButton`, `ConnectInstructions`, `StopVideoButton`, `FullscreenButton`, `PinButton`) plus exact expected status strings (`"Connecting."`, `"Mirroring stopped."`, `"Connection interrupted."`, `"Audio iPhone · Paused"`).
- **Performance**: Zero unnecessary allocations or heavy animations during active 60fps video mirroring or audio streaming; maintain frozen resources and clean UI thread dispatching.

## Brand Commitments

- **Product Name**: `NotAirplay`
- **Confirmed Logo & App Icons**: [`assets/logo.png`](assets/logo.png) (512x512 dark obsidian squircle with crisp platinum monitor frame and cyan-to-emerald AirPlay beacon triangle) and [`assets/app.ico`](assets/app.ico) (embedded into executable and window icon).
- **Editorial Voice**: Concise, factual, technical-clarity sentence case; zero exclamation marks, hype copy, marketing badges, or emojis.

## Evidence on Hand

- Visual logo and icon assets: `assets/logo.png`, `assets/app.ico`
- Desktop window capture and verification test suite: `Build.ps1 -Test` (`tests/Program.cs` renders `tests/fixtures/window-960.png` and `tests/fixtures/window-620.png`)
- Functional audio, video, protocol, discovery, and hosted D3D11 tests in `tests/`

## Product Principles

- **Immediate Readiness**: Launch straight into active network discovery without setup wizards or manual start toggles.
- **Media-First Restraint**: When video is streaming, window chrome stays secondary and drops out cleanly in fullscreen (`F11` / double-click).
- **First-Class Docked Audio**: Audio playback and persistent standby deserve purposeful desktop status, track clarity, and volume visibility—never a minimal stub.
- **Genuine Windows 11 Craft**: Native Fluent depth, crisp typography, semantic resource tokens, keyboard/UIA accessibility, and disciplined layout rhythm over generic centered web-like boxes.
- **Zero Telemetry & Local Trust**: All communication stays on the user's LAN; privacy and diagnostics control remain explicit and inspectable.

## Accessibility & Inclusion

- Complete Windows UI Automation metadata (`AutomationProperties.Name`, `AutomationProperties.LiveSetting="Polite"`).
- Full keyboard navigation (`Tab` order, `F11` / `Esc` shortcuts, visible focus visuals).
- Fractional-DPI sharpness (`UseLayoutRounding="True"`, `SnapsToDevicePixels="True"`) and `Typography.NumeralAlignment="Tabular"` for all telemetry counters and logs.
- Accessible text contrast (>= 4.5:1 for body text and telemetry labels, >= 3:1 for UI boundaries and status glyphs).
