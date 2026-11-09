# Third-party components

The NotAirplay receiver, WPF interface, transport code, and native video backend are original project code. The video backend calls Windows Media Foundation and Direct3D 11 system APIs.

- **PlayFair**: the separate `notairplay-key.exe` helper incorporates `native/playfair` from UxPlay v1.73.7, commit `df67c212a433cf6dda3676dd40c097900d24e645`. Complete included source, license, wrapper, and build script are supplied with this package. See `native/playfair/LICENSE.md`.
- **LLVM / libc++ runtime**: the native DLL was built with LLVM-MinGW 20260616 and statically linked runtime components. See `native/licenses/LLVM.txt`.
- **MinGW-w64 runtime / winpthreads**: associated runtime notices are in `native/licenses`.
- **FFmpeg / FFplay**: used for HEVC/compatibility video decoding and AAC audio playback. They are installed separately and are not distributed in this package.

Build tools are not bundled. No pcairplay or UxPlay receiver implementation is incorporated into the NotAirplay receiver or video renderer.
