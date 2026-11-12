---
name: NotAirplay
description: Fluent Acoustic Deck — native Windows 11 WPF-UI receiver for AirPlay screen mirroring and lossless wireless audio.
colors:
  primary-deep: "#047857"
  acoustic-live: "#10B981"
  acoustic-live-text: "#34D399"
  acoustic-wave-cyan: "#38BDF8"
  warning: "#F59E0B"
  warning-text: "#FBBF24"
  danger: "#B91C1C"
  danger-hover: "#991B1B"
  danger-border: "#DC2626"
  surface-base: "#202020"
  surface-card: "#323232"
  surface-drawer: "#383838"
  text-primary: "#FFFFFF"
  neutral-muted: "#A1A1AA"
  neutral-offline: "#71717A"
typography:
  headline:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "24px"
    fontWeight: 600
    lineHeight: 1.25
  title:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "22px"
    fontWeight: 600
    lineHeight: 1.25
  card-heading:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "14px"
    fontWeight: 600
    lineHeight: 1.35
  body:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.45
  label:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "12px"
    fontWeight: 500
    lineHeight: 1.35
  mono:
    fontFamily: "Cascadia Code, Consolas, Segoe UI, monospace"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.58
    fontFeature: "tnum"
rounded:
  sm: "6px"
  md: "8px"
  pill: "10px"
  lg: "12px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "20px"
  xl: "24px"
  2xl: "28px"
components:
  button-primary:
    backgroundColor: "{colors.acoustic-live}"
    textColor: "#09090B"
    typography: "{typography.label}"
    rounded: "{rounded.sm}"
    padding: "9px 18px"
  button-secondary:
    backgroundColor: "{colors.surface-drawer}"
    textColor: "{colors.text-primary}"
    typography: "{typography.label}"
    rounded: "{rounded.sm}"
    padding: "8px 14px"
  button-danger:
    backgroundColor: "{colors.danger}"
    textColor: "{colors.text-primary}"
    typography: "{typography.label}"
    rounded: "{rounded.sm}"
    padding: "5px 12px"
  button-danger-hover:
    backgroundColor: "{colors.danger-hover}"
    textColor: "{colors.text-primary}"
  card-surface:
    backgroundColor: "{colors.surface-card}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.md}"
    padding: "20px"
  status-badge:
    backgroundColor: "#1A10B981"
    textColor: "{colors.acoustic-live-text}"
    typography: "{typography.label}"
    rounded: "{rounded.pill}"
    padding: "3px 8px"
  nav-pane-item:
    backgroundColor: "transparent"
    textColor: "{colors.text-primary}"
    typography: "{typography.body}"
    rounded: "{rounded.sm}"
    padding: "8px 12px"
---

# Design System: NotAirplay

## Overview

**Creative North Star: "Fluent Acoustic Deck"**

NotAirplay is built as an always-ready Windows desktop instrument in `Operate` mode rather than a modal media wrapper or web-like splash card. Built on an opaque WPF `Window` (`WindowStyle="None"`, `WindowChrome` `GlassFrameThickness="0"`, `#202020` dark graphite base) paired with `WPF-UI` (`v4.3.0`) dark Fluent controls, it treats desktop screen mirroring and lossless AirPlay audio equally: a calm layered workspace at standby, a dedicated Hi-Fi audio console during background speaker playback, and a chromeless Direct3D 11 viewport when mirroring video.

Density favors fast peripheral scanning at desk distance: concise technical labels, real-time capability tables, hairline separators, and restrained signal color that illuminates only when wireless transmission or session state changes. Dark theme is preserved across standby, audio playback, and docked diagnostics to avoid bright monitor glare during video viewing or background work.

**Key Characteristics:**
- Native Windows 11 Fluent geometry (custom opaque 48px title bar over `WindowChrome`, adaptive `ui:NavigationView`, `DynamicScrollViewer`, crisp `SymbolIcon` vectors, zero emojis).
- Opaque dark tonal separation (`#202020` ground, `#323232` elevated cards, `#383838` telemetry drawer) with zero artificial drop shadows or DWM glass contention behind the Direct3D 11 child `HWND`.
- Signal-driven Emerald (`#10B981` / `#34D399`) and Ice Cyan (`#38BDF8`) highlights reserved strictly for live connectivity, audio meters, and hardware state.
- Continuous `VideoSurface` `HwndHost` layer kept permanently in the visual tree for instant zero-recreation fullscreen transitions.

## Colors

A dark graphite neutral foundation lets wireless session beacons and high-contrast typography carry every status cue without visual noise.

### Primary
- **Acoustic Emerald** (`#10B981`): Active readiness dots, active navigation selection bar, audio visualizer bars, WASAPI volume fill bar, and primary CTA surface fill (paired with `#09090B` dark foreground in WPF-UI Dark primary buttons).
- **Emerald Text Mint** (`#34D399`): Accessible foreground text for live percentages (`Device volume`), `"Streaming audio"` pills, active Screen Mirroring icon, and pinned icon accent on dark card backgrounds.
- **Deep Emerald Accent** (`#047857`): Windows accent theme base passed to `ApplicationAccentColorManager.Apply` alongside secondary (`#34D399`) and dark (`#065F46`) tones.

### Secondary
- **Ice Wave Cyan** (`#38BDF8`): Secondary acoustic accent reserved for AirPlay Audio connection glyphs, visualizer bars, and `"Connecting"` / `"Starting"` handshake indicators.

### Neutral & Semantic Feedback
- **Desktop Obsidian Ground** (`#202020`): Base opaque window surface (`AcousticStageFallbackBrush` with `WindowBackdropType.None`) and left `NavigationView` navigation rail.
- **Elevated Card Surface** (`#323232`): Rendered composite fill of Fluent `CardBackgroundFillColorDefaultBrush` across `ConnectInstructions`, `HomeCapabilitiesCard`, Quick Desk Controls strip, and Audio Deck cards.
- **Telemetry Drawer Surface** (`#383838`): Side-docked `DiagnosticsPanel` elevation and secondary button surface.
- **Primary Ink** (`#FFFFFF`): Headlines, device titles, card headers, capability values, and active control text.
- **Muted Control Gray** (`#A1A1AA`) & **Offline Zinc** (`#71717A`): Unpinned toolbar icons, idle standby badges, and inactive dots.
- **Retry Amber** (`#F59E0B` / `#FBBF24`) & **Accessible Danger Crimson** (`#B91C1C`, hover `#991B1B`, border `#DC2626`): Used exclusively for retry/error beacons and destructive `Stop mirroring` / `Disconnect` actions (`6.47:1` contrast with `#FFFFFF` label).

### Named Rules
**The Signal-Only Beacon Rule.** Emerald (`#10B981`), Cyan (`#38BDF8`), and Amber (`#F59E0B`) may tint status dots, live icons, and volume telemetry, never decorative card backgrounds or marketing banners.
**The Dark-Ground Contrast Rule.** Every button and readout on `#202020`/`#323232` surfaces must maintain >= 4.5:1 text contrast (using `#34D399` for colored text, `#B91C1C` for red buttons with white text, and `#09090B` for bright `#10B981` primary fills).

## Typography

**Display & Body Font:** `Segoe UI Variable` (`Segoe UI` fallback)  
**Telemetry & Log Font:** `Cascadia Code` (`Consolas`, `Segoe UI` fallback)

**Character:** Crisp native Windows geometric grotesque for UI controls paired with tabular-figure monospace in hardware telemetry wells, letting FPS, megabyte counts, and timestamps scan cleanly without horizontal jitter.

### Hierarchy
- **Headline** (SemiBold `600`, `24px`, `1.25`): Receiver home status headline (`Ready for your iPhone.`, `Connecting.`, `Mirroring stopped.`).
- **Title** (SemiBold `600`, `22px` / `15px` drawer, `1.25`): Connected iOS device name in the Audio Deck and `Session telemetry` drawer header.
- **Card Heading** (SemiBold `600`, `14px`, `1.35`): Card titles (`Connect from iPhone or iPad`, `Receiver pipeline`, `Signal pipeline`, `Device volume`).
- **Body** (Regular `400`, `13px`–`14px`, `1.45`): Track titles, onboarding instructions, and helper copy.
- **Label** (Medium/Regular `400`–`500`, `11px`–`12px`, `1.35`): Status pills, footer statusbar, form/toggle labels, and key-value field captions.
- **Mono Telemetry** (Regular `400`, `11px`–`12px`, `1.58`, `Tabular` numerals): `StatsDetail` frame counters and `DiagnosticLog` stream lines.

### Named Rules
**The Tabular Telemetry Rule.** Every numeric counter, percentage (`AudioVolumeText`), footer FPS indicator (`StatusDetail`), and log entry (`StatsDetail`, `DiagnosticLog`) must apply `Typography.NumeralAlignment="Tabular"` (`font-variant-numeric: tabular-nums`).

## Layout

NotAirplay frames a three-row shell (`48px` `TitleBar`, flexible workspace row, `44px` `Footer`) at a default `960x720` window size (`MinWidth 620`, `MinHeight 520`; expanding `MinWidth` to `900` whenever `DiagnosticsPanel` opens). Inside the middle row, a left `NavigationView` (`188px` expanded / `48px` compact icon rail, `150ms` pane transition) hosts `Receiver` (`receiver`) and `Audio deck` (`audio`) above the fold and anchors `Diagnostics` (`diagnostics`) in the footer rail, while a collapsible `312px` right `DiagnosticsPanel` docks telemetry alongside the stage without modal interruption.

Stage spacing runs strictly on a 4px/8px Fluent rhythm: `28px` horizontal / `24px` vertical stage padding (`24px` on `AudioOnlyPanel` top card), `20px` card interior padding (`20px,14px` on Quick Desk Controls), `16px` gutter between side-by-side workspace cards, and `10px` vertical gaps across pipeline rows. Responsive breakpoints adapt automatically:
- **Stage width `< 580px`** (or whenever `ConnectInstructions` hides during handshake): `HomeCapabilitiesCard` and `AudioVolumeCard` restack vertically onto Row 2 with `HomeCardCol2` collapsed so zero dead column gap appears.
- **Window width `< 780px`** (or `< 1120px` while `DiagnosticsPanel` is open): `RootNav` collapses to `LeftMinimal` (`48px` icon rail) and widens back above those thresholds.
- **Video Streaming / `F11` Fullscreen**: `VideoLayer` renders outside `RootNav` while collapsing `RootNav` (`Visibility="Collapsed"`) so the navigation rail never shrinks video bounds; toggling fullscreen collapses `TitleBar` (`48px` -> `0px`) and `Footer` (`44px` -> `0px`) for 100% pixel dedication to Direct3D 11 output.

## Elevation & Depth

Depth is conveyed purely through disciplined dark tonal surface stepping separated by 1px hairline strokes (`CardStrokeColorDefaultBrush`) on an opaque `WindowChrome` (`GlassFrameThickness="0"`, `WindowBackdropType.None`) host, rather than cast drop shadows or translucent DWM glass sheets. Interactive surfaces sit at `#323232` on `#202020` ground, while the right-docked telemetry drawer steps subtly forward at `#383838` with inner sunken monospace wells at `ControlFillColorSecondaryBrush`. During active video playback, `SetVideoPresentationSurface` switches window `Background` to `#000000` (`ClipToBounds="True"` on `VideoLayer`), restoring `#202020` when returning to standby or Audio deck; `ToggleFullscreen` clears `WindowChrome` (`null`) so D3D11 video spans full monitor bounds unobstructed.

### Named Rules
**The Tonal-Layer Depth Rule.** Convey hierarchy through opaque background surface stepping (`#202020` -> `#323232` -> `#383838`) and 1px hairline borders; never add blur halos, DWM glass sheets, or offset drop shadows over the `HwndHost` video host.

## Shapes

All corners belong to a restrained rounded Fluent scale: `6px` for inner monospace wells (`DiagnosticLog` container), controls, and compact buttons; `8px` across workspace cards (`ConnectInstructions`, `HomeCapabilitiesCard`, `AudioVolumeCard`, Quick Desk Controls strip); `10px` on status badges (`AudioStateBadge`); and `12px` on the `TitleBar` live status pill (`StatusLabel`) and `72x72` Audio Deck visualizer emblem. Horizontal and vertical panel boundaries use hairline `1px` borders (`0,1,0,0` Footer divider, `1,0,0,0` drawer edge) with crisp pixel alignment (`UseLayoutRounding="True"`, `SnapsToDevicePixels="True"`).

## Components

Every component inherits native Fluent hover, focus, and high-contrast dynamic resource bindings while synchronizing live session states cleanly.

### Buttons
- **Shape:** Gently curved control corners (`6px` default Fluent corner radius).
- **Primary (`DiscoveryButton` — `"Start receiver"`):** Solid Emerald fill (`#10B981`) with dark `#09090B` label text (`12px` SemiBold, padding `9px 18px`), revealed only when discovery is paused or unavailable.
- **Danger (`StopVideoButton` — `"Stop mirroring"` / `"Disconnect"`):** Deep Crimson fill (`#B91C1C`, hover `#991B1B`, pressed `#7F1D1D`, border `#DC2626`) with crisp `#FFFFFF` label (`12px` Medium, padding `5px 12px`), maintaining `6.47:1` contrast on the footer bar.
- **Secondary & Transparent (`HomeDetailsButton`, `Copy diagnostics`, `CompatibilityButton`, `PinButton`, `FullscreenButton`, `DetailsButton`):** Neutral elevated dark fill (`#383838`) or transparent chrome ghost pill (`DetailsButton` elevates to `Secondary` whenever `DiagnosticsPanel` is open, while `HomeDetailsButton` flips between `"Open telemetry & log"` and `"Hide telemetry & log"`).

### Chips & Status Pills
- **TitleBar Status Pill:** `#202020`/`ControlFillColorDefaultBrush` rounded `12px` capsule housing a `7x7` state dot (`#10B981` Ready/Mirroring/Audio, `#38BDF8` Starting/Connecting, `#F59E0B` Retry, `#71717A` Offline) and `12px` status label.
- **Audio State Chip (`AudioStateBadge`):** `10px` pill toggling between `"Streaming audio"` (`#1A10B981` tint, `#4D10B981` border, `#34D399` label) and `"Connected · Standby"` / `"Idle standby"` (subtle white alpha tint `#18FFFFFF` with `#A1A1AA` label).

### Cards / Containers
- **Workspace Cards:** `8px` radius, `#323232` dark Fluent card fill, `1px` `CardStrokeColorDefaultBrush` stroke, `20px` padding (`24px` on the Audio identity banner), containing aligned icon-prefixed headers (`14px` SemiBold) and structured lists or tabular key-value rows (`80px` label column).
- **Quick Desk Controls Strip:** Full-width `8px` strip below home cards containing synchronized `ToggleSwitch` controls (`Keep window on top`, `FFmpeg compatibility fallback`) and keyboard hint (`F11 / Double-click: Full screen`).

### Navigation
- **Adaptive `NavigationView`:** `188px` left pane switching cleanly to `48px` `LeftMinimal` rail below `780px` (or `< 1120px` when telemetry drawer opens), with `150ms` transition, emerald active selection indicator, and pinned `Diagnostics` footer toggle that opens the side drawer without discarding the active stage view.

### Audio Deck Console (`AudioOnlyPanel`)
- Combines a `72x72` rounded (`12px`) equalizer bar emblem (paired `#10B981` / `#38BDF8` static bars), `AudioStateBadge`, `22px` SemiBold device header, `14px` track line, tabular `Signal pipeline` card, and `Device volume` WASAPI meter (`ProgressBar` + `16px` SemiBold tabular readout showing `"68%"` in Emerald when live or `"—"` at `0%` when idle).

## Do's and Don'ts

### Do:
- **Do** drive colors and typography via `{DynamicResource}` Fluent tokens (`Acoustic*Brush`, `TextFillColor*Brush`, `CardBackgroundFillColor*Brush`) and `Typography.NumeralAlignment="Tabular"` for all telemetry.
- **Do** keep `NativeSurface` (`VideoSurface` `HwndHost`) continuously attached inside `VideoLayer` (`Visibility="Hidden"`, never `Collapsed`) so Direct3D initialization succeeds before video arrives.
- **Do** synchronize mirrored UI controls (`PinButton` <-> `HomePinToggle`, `CompatibilityButton` <-> `HomeCompatToggle`, `DetailsButton` <-> `HomeDetailsButton`) whenever session or drawer state changes.
- **Do** reset `AudioVolumeText` to `"—"` and `AudioVolumeBar` to `0` on idle/stop states rather than displaying stale percentages.

### Don't:
- **Don't** center-float a small standby card over void `#000000` canvas or add numbered `1-2-3` tutorial badges or marketing kickers.
- **Don't** use WPF-UI's default `#F44336` Material red with white text (`3.68:1` contrast) on dark surfaces; use `#B91C1C` (`AcousticDangerBrush`).
- **Don't** trigger layout animations or allocations on the per-frame `CompositionTarget.Rendering` / D3D11 video path.
- **Don't** place un-aliased `Unicode` glyphs or emojis in labels where `SymbolIcon` (`Cast24`, `DesktopSpeaker24`, `Pulse24`, `Wifi124`, `Pin24`) belongs.
