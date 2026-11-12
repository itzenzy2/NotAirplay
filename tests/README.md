# Verification

Run from the source root after building the application and native components:

```powershell
dotnet run --project tests/NotAirplay.Tests.csproj -c Release -- tests/fixtures --native
```

The test runner requires Windows, .NET 8 Desktop, `ffmpeg.exe` on `PATH`, a working Direct3D 11 adapter (for `--native`), and an active Windows audio render endpoint. Omit `--native` on headless machines without graphics access. Tests use local loopback TCP/UDP and briefly announce the receiver for discovery lifecycle checks; the hosted WPF test opens an offscreen window and closes it automatically.

Coverage includes:
- Fragmented and pipelined RTSP framing, HTTP `/server-info`, private field redaction, and FairPlay v3 mode 0–3 handshake fixtures.
- Compressed paired-service mDNS queries (`QU`/`QM`, legacy unicast, malformed packets) and custom sanitized AirPlay/RAOP instance names (`<= 48` UTF-8 bytes) plus `/info` `maxFPS` (`30`, `60`, `120`) advertisement.
- Encrypted H.264/HEVC over TCP, UDP timing, media EOF, local disconnect, malformed streams, decoder fallback, admission release/reconnect, and two discovery start/stop cycles.
- AirPlay 128-byte header NTP timestamp extraction and paced 360-frame hardware/software D3D11 native decode + `benchmark.h264` portrait-to-`landscape.h264` target resize (`450` frames).
- Audio AES-CBC packet tail decryption, RFC3640 AU headers, DMAP/bplist metadata, volume/pause/teardown handling, and in-process WASAPI audio output across ALAC (`codec=2`), Media Foundation AAC-LC (`codec=4` encrypted RTP + WASAPI session volume sync), and AAC-ELD (`codec=8`) plus device enumeration.
- WPF UI state transitions (connecting, streaming, audio-only/paused, cleared video frame, stale callback rejection), `SetThreadExecutionState` wake-lock state mapping, Fluent UI layout captures at `960x720` and `620x520`, and hosted offscreen D3D11 fullscreen/resize/minimize lifecycle.

## Fixtures

`h264.mirror` and `hevc.mirror` contain 12 independently encrypted synthetic frames each, originally generated with FFmpeg and Node/OpenSSL. The setup and FairPlay fixtures are protocol test data, not captured user screen content.

The larger elementary streams are reproducible with:

```powershell
ffmpeg -f lavfi -i "testsrc2=size=500x1080:rate=60" -frames:v 360 -c:v libx264 -preset veryfast -tune zerolatency -x264-params "aud=1:keyint=60:scenecut=0" -pix_fmt yuv420p -y tests/fixtures/benchmark.h264
ffmpeg -f lavfi -i "testsrc2=size=1080x500:rate=60" -frames:v 90 -c:v libx264 -preset veryfast -tune zerolatency -x264-params "aud=1:keyint=60:scenecut=0" -pix_fmt yuv420p -y tests/fixtures/landscape.h264
```

The runner also writes layout PNGs (`tests/fixtures/window-*.png`, gitignored) and protocol serialization samples into the fixture directory for visual inspection.
