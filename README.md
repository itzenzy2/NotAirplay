# NotAirplay

A lightweight, low-latency AirPlay screen mirroring and audio receiver for Windows 10 and 11. Connects directly to your iPhone or iPad over your local Wi-Fi or Ethernet network — no companion iOS apps, Bluetooth pairing, or cloud relays required.

---

## Features

- **Hardware-accelerated video**: Zero-copy Direct3D 11 swapchain presentation with hardware H.264 and HEVC decoding via Windows Media Foundation (DXVA2).
- **Lossless audio**: Bit-perfect Apple Lossless Audio Codec (ALAC) decoding using a native C decoder, alongside low-latency AAC-ELD and AAC-LC playback.
- **Hardware volume synchronization**: Native Windows Audio Session API (WASAPI) volume integration driven directly by your device's volume rocker and slider.
- **Persistent standby**: Maintains connection continuously during playback pauses and between track changes without dropping the session.
- **Zero iOS configuration**: Pairs natively through iOS Control Center. No jailbreak, mobile profiles, or third-party apps needed.
- **Automatic discovery**: Broadcasts over multi-adapter IPv4 mDNS for instant pairing on startup.
- **Zero telemetry**: Operates strictly within your local network. No external network requests, account requirements, or data collection.

---

## Requirements

- **Operating System**: Windows 10 or Windows 11 (64-bit)
- **Runtime**: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Network**: iPhone/iPad and Windows PC connected to the same local network (Wi-Fi or Ethernet)
- **Fallback / HEVC decoding** *(optional)*: `ffmpeg.exe` and `ffplay.exe` on your system `PATH` for compatibility mode and HEVC stream fallback.

---

## Getting Started

1. Download the latest release from the [Releases](https://github.com/itzenzy2/NotAirplay/releases) page and extract the archive.
2. Launch `NotAirplay.exe`. The receiver starts automatically and announces itself on your local network.
3. On your iPhone or iPad:
   - Swipe down to open **Control Center**.
   - Tap **Screen Mirroring** (for video) or tap the AirPlay audio icon in the media player.
   - Select **NotAirplay**.
4. To disconnect, tap **Stop Mirroring** on your device or click **Disconnect** in NotAirplay.

---

## Keyboard Shortcuts & Controls

| Action | Shortcut |
| :--- | :--- |
| **Toggle Full Screen** | `F11` or double-click the video surface |
| **Exit Full Screen** | `Esc` |
| **Keep on Top** | Pin button in the footer bar |
| **Session Diagnostics** | Details button in the footer bar |

---

## Architecture

NotAirplay implements the AirPlay protocol natively on Windows:

- **Pairing & Handshake**: Implements the RTSP control channel and FairPlay v3 authentication (modes 0, 1, 2, and 3), recovering session media keys with zero external network calls.
- **Video Pipeline**: Decrypts AES-CBC video packets, parses NAL units, feeds samples into the Windows Media Foundation H.264/HEVC decoder MFT, and presents directly onto a Direct3D 11 swapchain with vsync-aligned frame delivery.
- **Audio Pipeline**: Receives RTP audio over UDP, decrypts payloads, and decodes ALAC frames with an embedded native C decoder (`native/alac.c`), routing audio via WASAPI with real-time process volume synchronization.
- **Memory & Throughput**: Employs zero-allocation packet stream processing and instant memory-safe stream discarding for oversized metadata to maintain smooth playback without GC pauses.

---

## Building from Source

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [LLVM-MinGW](https://github.com/mstorsjo/llvm-mingw/releases) (or Clang on Windows)

### Build Steps

Clone the repository and run the build script:

```powershell
git clone https://github.com/itzenzy2/NotAirplay.git
cd NotAirplay

# Build native components, compile WPF application, and run test suite:
powershell -ExecutionPolicy Bypass -File .\Build.ps1 -Clang C:\path\to\llvm-mingw\bin\clang.exe -Test
```

The compiled binaries will be placed in `bin/Release/net8.0-windows/`.

---

## Troubleshooting

- **Device cannot find NotAirplay**: Verify that both devices are connected to the same subnet and that your router does not have "AP Isolation" or "Client Isolation" enabled. Ensure Windows Firewall permits incoming TCP and UDP connections for `NotAirplay.exe`.
- **Black screen or stuttering**: Open **Details** in the bottom right corner and toggle **Compatibility mode: on** to switch to the fallback FFmpeg decoder on your next connection.
- **Diagnostics**: Click **Copy diagnostics** inside the Details panel to copy the active session parameters, frame counters, and protocol activity log.

---

## License & Acknowledgments

NotAirplay is open-source software. Third-party components and cryptographic reference notices are documented in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
