# Preview 6 validation

Release build: zero warnings and errors. Protocol, encrypted video, lifecycle, fallback, native resolution changes, WPF hosting and discovery checks passed.

The final paced 60 fps test sent 360 frames in two-frame bursts over six seconds. Hardware decoding submitted 360 with zero replacements; software decoding submitted 359 with one replacement. These are local submission counters on a hidden test target, not measured physical-display cadence or iPhone-to-PC latency. Both modes decoded all 450 portrait/landscape frames in the complete test.

Audio tests passed AES-CBC payload/tail recovery, RFC3640 packet layout and encrypted AAC-LC UDP input to FFplay without reported decoder errors. Audible output, iPhone AAC-ELD interoperability, sender volume and A/V synchronization are not verified. ALAC and buffered AirPlay 2 audio are unsupported.

Preview 5's CPU throughput results did not establish smooth playback and are superseded by this validation approach. Preview 6 requires a real-device test before claiming smoothness improvements.
