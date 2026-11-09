# Verification

Run from the source root after building the application and native components:

```powershell
dotnet run --project tests/NotAirplay.Tests.csproj -c Release -- tests/fixtures --native
```

The test runner needs Windows, .NET 8 Desktop, FFmpeg on PATH, and a working D3D11 adapter for `--native`. Omit that flag on machines without graphics access. Tests use local TCP/UDP and briefly announce the receiver for discovery lifecycle checks. The hosted-window test creates an offscreen test window and closes it automatically.

Coverage includes fragmented/pipelined RTSP, bounded framing, metadata, private-field filtering, FairPlay handshake fixtures, encrypted H.264/HEVC over TCP, timing, media EOF, local stop, malformed streams, decoder fallback, admission release/reconnect, UI stale-frame clearing, and two discovery start/stop cycles.

Native checks decode 360 synthetic portrait frames and 90 landscape frames, change resolution on the same decoder, resize the target, verify first output arrives before input ends, and repeat with forced software decoding. The hosted WPF test verifies that the native child window exists, encrypted input reaches it, and stopping clears the picture and exits full screen.

Preview 6 feeds 360 native frames at 60 fps in two-frame bursts and checks submission/replacement counts. The native target is hidden; this verifies bounded scheduling, not physical-display smoothness. The old Preview 4 unpaced command is retained only as a reference and is not directly comparable to the paced run. Audio checks verify CBC decryption including unencrypted packet tails, RFC3640 AU headers, and encrypted AAC-LC UDP input to FFplay. They do not prove audible output, AAC-ELD interoperability, or A/V synchronization on an iPhone.

## Fixtures

`h264.mirror` and `hevc.mirror` contain 12 independently encrypted synthetic frames each, originally generated with FFmpeg and Node/OpenSSL for Preview 4. The setup and FairPlay fixtures are protocol test data, not captured user screen content. Published handshake packet fixtures are cited in the main README.

The larger elementary streams are reproducible with:

```powershell
ffmpeg -f lavfi -i "testsrc2=size=500x1080:rate=60" -frames:v 360 -c:v libx264 -preset veryfast -tune zerolatency -x264-params "aud=1:keyint=60:scenecut=0" -pix_fmt yuv420p -y tests/fixtures/benchmark.h264
ffmpeg -f lavfi -i "testsrc2=size=1080x500:rate=60" -frames:v 90 -c:v libx264 -preset veryfast -tune zerolatency -x264-params "aud=1:keyint=60:scenecut=0" -pix_fmt yuv420p -y tests/fixtures/landscape.h264
```

The runner also writes layout PNGs and protocol serialization samples into the fixture directory for inspection.
