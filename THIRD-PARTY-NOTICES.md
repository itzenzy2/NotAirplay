# Third-party components

The NotAirplay receiver, WPF interface, transport code, native Direct3D 11 video backend (`native/video.cpp`), and in-process WASAPI audio renderer (`native/audio.cpp`) are original project code calling Windows Media Foundation, Direct3D 11, DXGI, and WASAPI system APIs.

- **ALAC decoder**: `native/alac.c` and `native/alac.h` incorporate David Hammerton's MIT-licensed Apple Lossless (`alac`) decoder. License terms are preserved at the top of both source files.
- **PlayFair**: the separate `notairplay-key.exe` helper incorporates `native/playfair` from UxPlay v1.73.7, commit `df67c212a433cf6dda3676dd40c097900d24e645`. Complete included source, license, wrapper, and build script are supplied with this package. See `native/playfair/LICENSE.md`.
- **WPF-UI**: the Windows 11 Fluent UI controls (`WPF-UI` `v4.3.0`, MIT License) are restored via NuGet.
- **LLVM / libc++ runtime**: the native DLL was built with LLVM-MinGW and statically linked runtime components. See `native/licenses/LLVM.txt`.
- **MinGW-w64 runtime / winpthreads**: associated runtime notices are in `native/licenses`.
- **FFmpeg**: `ffmpeg.exe` is optionally invoked headless for compatibility/HEVC video decoding and `AAC-ELD` audio decoding (`ffplay.exe` is not required). It is installed separately by the user and is not distributed in this repository.

Build tools are not bundled. No pcairplay or UxPlay receiver implementation is incorporated into the NotAirplay receiver, video renderer, or audio engine.
