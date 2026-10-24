# ADR 0001: Stack, licence and engine placement

Date: 24 Oct 2026 · Status: accepted

## Context

The spec (section 18) offered three stacks for a Windows + macOS live playback app with hard reliability targets (no dropouts at 256 frames / 48 kHz, ≤ 3 % idle CPU, sample-accurate splices, ASIO / WASAPI / CoreAudio):

| Option | UI | Engine |
|---|---|---|
| A | JUCE (C++) | JUCE |
| B | Avalonia (C#) | native engine via P/Invoke or IPC |
| C | Tauri, web UI | Rust (`cpal`) |

The product owner is learning .NET (cross-platform desktop), has some C++ and no Rust, and develops on Windows.

## Decision

1. **Option B: Avalonia 12 on .NET 10 for UI and app logic, a C++20 engine on headless JUCE 9.**
   - About 80 % of the code (plan builder, command bus, persistence, import, readouts, all screens) is C#.
   - JUCE supplies the platform audio work: WASAPI / ASIO / CoreAudio, MMCSS and audio workgroups, device-change detection, MIDI, file decoding.
   - Rust (B-Rust, C) was rejected: a second new language, and thinner ASIO and pro-MIDI support.
2. **The engine runs as a separate process from the start** (`recompose-engine`), the arrangement spec 18 §1 recommends, so a UI crash or freeze can't stop the music (REL-34).
   - Transport: a Unix domain socket on all three platforms (native on Windows 10 1803+), carrying length-prefixed JSON messages: commands go down, events and state snapshots come up (`docs/engine-protocol.md`). Snapshots run at display rate only while something moves (PB-36, PB-90).
   - Lifetime: the engine listens; the app starts it or reattaches to one that is already running. It keeps playing when the app goes away, and exits on `shutdown` or after a short idle period with no app connected.
   - JUCE runs as an ordinary JUCE process with its own message thread, so it never shares a run loop with Avalonia (this removes the in-process hosting risk on macOS).
   - Rejected: in-process with an IPC-shaped C ABI (simpler to debug, but a managed crash, a GC stall or a native fault in the UI would take the audio with it, and moving out later would mean rewriting the boundary).
3. **The plan is built in C# and the engine only executes it** (spec 18 §1). The engine receives timed segments ("at song frame N play track T from source frame S with these ramps") and reports the clock, meters and device state.
4. **Licence: AGPL-3.0-or-later, public repository** `nathtjan/RECompose`. This matches JUCE's open-source licence, and the ASIO SDK's GPLv3 option (Steinberg, Oct 2025) is compatible with it.
5. **Design tokens are generated** from `design/tokens.json` into Avalonia resources. The React components in `design/reference/` are the behavioural reference; Avalonia controls are written to match them.

## Consequences

- Two toolchains (CMake + dotnet) and one process boundary. `build.ps1` / `build.sh` and CI hide this; the protocol stays small, documented and versioned (`REC_PROTOCOL_VERSION`).
- No managed code in the engine process at all, so .NET garbage collection can't cause dropouts.
- Extra work up front: process supervision (start, reattach, restart after an engine crash), the protocol, and moving meter and clock data at 60 Hz across the socket. If the socket proves too slow for meters, a shared-memory snapshot can be added behind the same protocol.
- Packaging (M10) ships two executables: on macOS the engine sits in the app bundle and is signed and notarised with it; on Windows it sits next to `RECompose.exe`.
- macOS correctness is checked by CI builds and tests. Real-device audio checks on macOS need a Mac.
- Risks to retire first in milestone 1: the AF_UNIX transport on all three platforms (CI from milestone 0), and the round-trip latency of commands and snapshots between the processes.
