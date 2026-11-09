# NotAirplay

A Windows receiver for your iPhone's built-in Screen Mirroring. **Playback preview 6** repairs video presentation scheduling and adds initial AAC audio playback. No Bluetooth or iPhone app is needed.

## Run

Use Windows 10/11 x64 with the .NET 8 Desktop Runtime. Extract the ZIP, keep its files together, close any older NotAirplay preview, and run `NotAirplay.exe`.

1. Put the iPhone and PC on the same network. Ethernet is fine for the PC.
2. Choose **Start receiver**.
3. On the iPhone, open **Control Center → Screen Mirroring → NotAirplay**.
4. Stop on either device when finished. The picture clears, the app shows **Mirroring stopped**, and discovery stays ready for reconnection.

**Turn off receiver** stops advertising. **F11** or the full-screen button enters full screen; **Esc** leaves it. The pin keeps the window above other apps.

H.264 uses Windows Media Foundation and Direct3D 11 by default. With a supported graphics driver, decode, color conversion, scaling, and presentation stay on the GPU. The Windows software decoder can still use GPU presentation. `ffmpeg.exe` on PATH is needed for HEVC and compatibility fallback.

Open **Details** for frame counts, decoder choice, received data, local processing time, and **Copy diagnostics**. **Compatibility mode** applies to the next connection and forces the fallback path. Logs stay in memory until copied; screen pixels, media keys, and control-request bodies are not logged.

**Quality: 1440p** requests a larger display canvas on the next connection. Disconnect and reconnect after changing it. The iPhone chooses the encoded dimensions; portrait mirroring fits inside the advertised canvas, so this does not promise 1440 pixels across a portrait image.

Audio uses separately installed **ffplay.exe**, beside ffmpeg.exe on PATH, and the Windows default output device. AAC-LC and AAC-ELD RTP streams are supported; ALAC and buffered AirPlay 2 audio are not. Use the Windows volume mixer for playback volume. Sender volume commands, complete clock-based A/V synchronization, and audible AAC-ELD playback on a real iPhone have not been validated.

## What changed

- Replaced discard-on-busy presentation with a separate vsync presentation worker. At most two decoded frames wait for display; reception/decoding continues independently. Stale decoded frames can still be replaced when the display falls behind.
- Added AES-CBC audio decryption, AAC RTP repackaging, UDP gap retransmission requests, playback process cleanup, and audio-only teardown.
- Added a next-connection 1080p/1440p quality request.

- Replaced the default H.264 bitmap pipe with an in-process Windows decoder and a native D3D11 surface. The GPU path avoids full-size RGB transfers through a child process and WPF.
- Limited presentation queueing; skipped obsolete presentation work instead of allowing delay to accumulate.
- Made fallback WPF presentation follow compositor rendering, with one pending frame and a nonblocking bitmap update.
- Added typed connection/session events and session IDs. EOF, local stop, failures, and late callbacks now produce the correct UI state.
- Kept discovery available after mirroring stops, released sockets/decoders on teardown, and added a first-frame deadline and control timeout.
- Added a clean home screen, separate diagnostics panel, visible streaming state, full screen, always-on-top, and compatibility mode.
- Retained H.264/HEVC compatibility, the working handshake, and IPv4 discovery.

## Validation and limits

Local checks cover the protocol, encrypted TCP media, stop/reconnect behavior, decoder fallback, native hardware/software decode, resolution changes, and UI lifecycle. Reproducible tests and benchmark methodology are in [tests/README.md](tests/README.md).

Preview 5's unpaced CPU benchmark did not predict smooth playback and is not used as evidence for this release. Preview 6 tests paced and bursty input instead. Submission counters do not measure the physical display or end-to-end iPhone latency. Native HEVC, input control, and protected-content playback are not implemented.

## Build

Use the .NET 8 SDK and [LLVM-MinGW](https://github.com/mstorsjo/llvm-mingw/releases/tag/20260616):

```powershell
dotnet build NotAirplay.csproj -c Release
.\native\Build-Native.ps1 -Clang C:\path\to\llvm-mingw\bin\clang.exe
.\bin\Release\net8.0-windows\NotAirplay.exe
```

`native/video.cpp` is the original native video implementation. It uses the documented [Windows H.264 decoder](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-decoder), [low-latency property](https://learn.microsoft.com/en-us/windows/win32/medfound/codecapi-avlowlatencymode), and [D3D11 device-manager integration](https://learn.microsoft.com/en-us/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation).

The FairPlay wire handler uses protocol data from published [mode 0/2 traces](https://github.com/FDH2/UxPlay/issues/67), [mode 1 trace](https://github.com/FDH2/UxPlay/issues/535), and [mode 3 trace](https://github.com/FDH2/UxPlay/issues/176). [Wire-format notes](https://air-display.github.io/airplay-internal/pairing_process.html) describe the acknowledgement. The separate PlayFair helper recovers the media key once per session. Its complete source and license are included.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for component provenance.

