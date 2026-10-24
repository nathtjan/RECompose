# Milestone 1: Engine and import

**Goal:** import a multitrack folder and play it through the real audio interface with a working mixer, output routing, transport, a basic timeline and correct readouts. Every later milestone plays through this, so the engine is built to its final shape now (segment scheduler, splices, routing, the engine process and its protocol), even though the UI only plays songs as written.

**Starting point:** milestone 0 in this repository. `recompose-engine` runs as its own process, the app starts it or reattaches to it, and both sides speak protocol 1 (`hello`, `ping`, `shutdown`) over a local socket. CI builds and tests on Windows, macOS and Linux.

**Spec references:** 04-playback-engine (PB), 09 SL-50…56 (import, library), SL-60 (SONG tab), 03 DM-10/12/13/90, 02 UI-10…16, 12 REL-24/34/50, 13 PLAT-10, 14 SET-10 and KEY-1.

## Status (24 Oct 2026)

Ready for the demo on the product owner's PC (see `docs/testing/M1-test-guide.md`). CI is green on Windows, macOS and Linux, including headless window tests.

| Package | Done | Still open |
|---|---|---|
| WP1 Devices | JUCE message loop in the engine (CFRunLoop on macOS), device list and selection, device-change events, ASIO from the GPLv3 SDK, MMCSS via JUCE's WASAPI | Hands-on check with the real interface; null-device CI smoke test |
| WP2 Engine core | Disk streaming with 4 s read-ahead, r8brain resampling (−150 dB THD+N), transport fades, sample-exact seeks, gain/mute/solo with solo-safe, routing rules, meters, clock, starvation and xrun counts, offline render | General segment scheduler (M1 plays the file as written; seeks splice), debug allocation checker, 20× offline speed test |
| WP3 Protocol 2 | All messages, reattach with `getState`, engine-crash restart | Round-trip latency measurement |
| WP4 Core | Model, persistence, import rules, readouts, undo with merged fader drags | PlanCompiler as a separate type (arrives with M3), the log tab's categories |
| WP5 Import | Probe and peaks as separate jobs, review dialog, duplicates, library with search | Import timing check on the reference machine |
| WP6 Design system | Token and icon generators with CI checks, Fluent mapped onto tokens, type styles | Component gallery window, Martian Mono width axis, theme switch |
| WP7 Prepare shell | Top bar, library, header, transport, readouts, timeline, MIXER and SONG tabs, Settings › Audio, banners, toasts, shortcuts, single instance | Splitter sizes persisted, zoom buttons |
| WP8 Hardening | App log, headless smoke tests | Performance pass, 30-minute soak, macOS hands-on |

## Definition of done: the demo

Run it on the product owner's Windows PC with the audio interface, and from the CI build on a Mac if one is available. Every step must work in one sitting.

1. Launch RECompose. It opens in Prepare (dark theme, Instrument styling) with an empty library and the prompt to import a song. The top bar's audio indicator shows the device and driver ("Focusrite USB ASIO · 4 out · 48 kHz").
2. Drag a purchased stems folder onto the window (or press Ctrl+I). The **import review** (SL-51) lists every file with its detected role, group and output, plus the title, key and BPM parsed from the file names. Confirm. The song appears in the library with `{key} · {BPM} BPM · {meter} · {n} tracks`; waveforms are computed in the background.
3. Double-click the song. The status pill reads `LOADING` with progress, then `READY`. The timeline shows the ruler in bars and one lane per track in spec order (Click, Guide, Drums, Percussion, Bass, Keys…), with waveforms.
4. Press Space. The music plays, the playhead moves and the readouts follow. On a fixture-A-like song at source 78.52 s: `bar 32.2 / 124`, `1:18 / −3:51`, `PLAYING`, `chart bar 32.2`.
5. Open the MIXER tab. Move a fader, mute Drums and solo Keys: click and guide keep playing (PB-82). The meters move. Ctrl+Z undoes the fader move, with a toast.
6. In Settings › Audio, choose the interface (ASIO on Windows) and set **Cue** to outputs 3–4. Click and guide move to the in-ear outputs; the band stays on 1–2.
7. Click the ruler while playing: the playhead jumps with no click or pop. When stopped, ← and → step one bar. Stop returns to the start.
8. **End the RECompose task in Task Manager while the song plays. The music keeps playing.** Launch RECompose again: it reattaches to the engine, the song is loaded, and the playhead is where the music is (REL-34).
9. Quit and relaunch. The library, the mix and the audio settings are still there.
10. Task Manager / Activity Monitor: idle with a song loaded, app and engine together ≤ 3 % CPU (PB-90); 16 stems playing well under 20 % (PB-91); the xrun counter stays at 0 at 256 frames / 48 kHz (REL-24).

The automated part of the definition of done: every work package's exit tests pass on CI on all three OSes.

## Scope

**In**

| Area | Requirements | Notes |
|---|---|---|
| Engine playback | PB-1 (without key/tempo), PB-2, PB-3, PB-4, PB-55, PB-56, PB-93 | Streaming, high-quality resampling, segment scheduler with splice fades, one audio clock |
| Engine process | REL-34, REL-50 (missing file) | Survives app crashes; the app reattaches; the app restarts a crashed engine |
| Transport | PB-10, PB-20, PB-21, PB-22, PB-26, PB-36 | Play/Pause/Stop with 10/20/50 ms fades, seek on the ruler; ⏮ goes to the plan start; ⏭ waits for services (M4) |
| Readouts | PB-31…34 subset, PB-36 | Song bar, elapsed/remaining, status pill, chart bar. NOW/NEXT labels need sections (M2) |
| Grid | SEC-1 step 4, SEC-7 interface | BPM and meter from the file name or typed in the SONG tab, downbeat at 0. Click analysis is M2 |
| Mixer | PB-80, PB-81 (Default preset only), PB-82, PB-84 | Fader −∞…+6 dB, double-click = 0.0, M, S, post-fader meters, MASTER |
| Routing | PB-85 (Main, Cue) | Logical outputs → device channels; role-based defaults; per-track override; mono/stereo rules |
| Audio devices | PLAT-10, SET-10 subset, REL-24 | WASAPI shared/exclusive, ASIO, CoreAudio; rate and buffer; driver in the status label; xrun count |
| Import & library | SL-50 (folders), SL-51, SL-52, SL-53, SL-54 (peaks only), SL-55, SL-56 subset | Review dialog, role/group detection and lane order, BPM/key parsing, duplicates by audio hash |
| Data | DM-10/11/12/13, DM-90 subset | `library.json`, `songs/<id>.json`, `settings.json`, atomic autosave within 500 ms, `cache/peaks/` |
| UI | UI-10 shell, UI-11 subset, UI-12 library block, UI-13, UI-15 (as heard = whole file), UI-16 MIXER and SONG tabs, UI-70 | The Perform switch is shown but disabled until M5 |
| Design system | tokens, fonts, icons, base components | Generated from `design/tokens.json`; Dark checked visually, Light and High contrast generated |
| Commands & log | CommandBus (spec 18 §1), AUTO-50 subset | Undo/redo, autosave, `Loaded`, `Play at bar`, `Pause at bar`, `Stop`, `File missing` |
| Shortcuts | KEY-1 subset | Space, ←/→ (stopped), F + − ., Ctrl+I, Ctrl+Z / Ctrl+Shift+Z, Ctrl+, |

**Out** (later milestones): sections and analysis (M2); arrangements and the plan builder beyond "as written" (M3); services, keys, tempo, pads and transitions (M4); the Perform view (M5); the render cache, device failover, the journal, crash resume at a section, and panic (M6); voice (M7); cues, MIDI and the LOG tab (M8); the assistant (M9); signing and installers (M10). Zip import (SL-50) waits for M4.

## Architecture

```
┌──────────────────────── RECompose (C#, Avalonia) ─────────────────────────┐
│ Prepare shell · timeline · mixer · settings          (view models, MVVM)  │
│ CommandBus ── undo/redo · autosave · log                                  │
│ RECompose.Core (pure, unit-tested)          RECompose.Engine (client)     │
│   Library · Song · Track · MixPreset          EngineHost: start/reattach/  │
│   ImportRules · TempoGrid · Readouts            restart · EngineConnection │
│   PlanCompiler (as written → segments) ───►   commands ↓ · events and      │
│                                               snapshots ↑ (60 Hz playing)  │
│ Job runner ── recompose-engine probe/peaks (short-lived, one per file)    │
└───────────────────────────────┬───────────────────────────────────────────┘
                                │ engine protocol 2: length-prefixed JSON
                                │ over a Unix domain socket (docs/engine-protocol.md)
┌───────────────────────────────▼──── recompose-engine (C++/JUCE 9) ────────┐
│ main thread: JUCE message loop · AudioDeviceManager · device-change events │
│ control thread: socket, parse commands → lock-free queue → audio thread    │
│ disk thread: JUCE readers → per-track ring buffers (≥ 2 s), prefetch       │
│ audio thread: segment scheduler → r8brain resampler → gain/mute/solo ramps │
│   → routing matrix → device channels · meters · clock (frames rendered)    │
│ publisher: snapshot to the app at 60 Hz while playing, on change otherwise │
└────────────────────────────────────────────────────────────────────────────┘
```

**Why a separate process.** ADR 0001: a UI crash, freeze or GC pause can't stop the music (REL-34), and JUCE gets an ordinary process with its own message loop instead of sharing Avalonia's run loop.

**Segments, not files.** The C# `PlanCompiler` turns plan items into segments (`songFrameStart, trackId, sourceFrame, frameCount, fadeIn, fadeOut`). In M1 the plan is "the whole file, as written", but the engine accepts any segment list, so the AT-3 splice test runs now and milestone 3 only has to build bigger plans.

**Clock and playhead.** Each snapshot carries the song frame at the output (latency-compensated, PB-4) and the engine's monotonic timestamp. The UI extrapolates between snapshots, so the playhead moves smoothly at display rate and agrees with the readouts.

**Decoding jobs run out of process.** File info, audio hashes and peaks come from short-lived `recompose-engine probe <file>` / `peaks <file> <out>` runs started by the app at low priority (JSON on stdout). One decoder (JUCE) serves both platforms, a corrupt file can't crash playback, and background work stays off the audio process (PB-92).

### Protocol 2 (sketch)

Requests get replies; the engine also sends events. Field details are fixed in WP3 and documented in `docs/engine-protocol.md`.

| Message | Direction | Purpose |
|---|---|---|
| `hello`, `ping`, `shutdown` | app → engine | as in protocol 1 (shapes never change) |
| `listDevices` | app → engine | driver types; devices with channel names, rates and buffer sizes |
| `setDevice` | app → engine | driver, device, rate, buffer, logical output map |
| `loadSong` | app → engine | song id, tracks (file, channels, output, gain/mute/solo), segments |
| `play`, `pause`, `stop`, `seek` | app → engine | transport (with the PB-20…22 fades); `seek` splices while playing (PB-26) |
| `setStrip`, `setMaster`, `setRouting` | app → engine | mixer and routing changes with 10 ms ramps |
| `getState` | app → engine | everything needed to rebuild the UI after reattaching: loaded song id, transport, mix, device |
| `renderOffline` | app → engine | tests only: run the graph on a null device and write a WAV |
| `snapshot` | engine → app | transport state, song frame, timestamp, sample rate, load %, xruns, per-strip peak/RMS |
| `deviceChanged`, `deviceLost`, `loadProgress`, `fileMissing`, `error` | engine → app | events |

## Work packages

The order retires risk first. Each package ends green on CI on all three OSes. Sizes: S ≈ a few evenings, M ≈ one to two weeks, L ≈ two to four weeks at the product owner's pace.

### WP1: Audio devices in the engine process (risk first) · M
JUCE's message loop on the engine's main thread, the control loop on its own thread; `AudioDeviceManager` opens a real device and plays a test tone; device-change notifications become events; the audio thread joins MMCSS "Pro Audio" (Windows) and the device's audio workgroup (macOS) (PLAT-10).
- Enable **ASIO**: the Steinberg ASIO SDK (GPLv3 option) via `-DRECOMPOSE_ASIO_SDK_DIR`. Confirm where Steinberg publishes it and whether CI may download it; otherwise CI builds without ASIO and ASIO is checked by hand.
- Check that the message loop exits cleanly on macOS when `shutdown` arrives (JUCE posts the quit message from the control thread).
- **Exit:** a tone plays on the product owner's interface via WASAPI and ASIO; unplugging and replugging produces events; a CI smoke test opens a null device on all three OSes.

### WP2: Engine core · L
Streaming (JUCE readers, disk thread, ring buffers ≥ 2 s); resampling (**r8brain-free**, MIT); the segment scheduler with splice fades (10 ms); transport fades; gain/mute/solo ramps; the routing matrix (mono → pair at equal level, stereo → mono −3 dB); post-fader meters; the clock with latency compensation; xrun and load counters; offline render. In debug builds the audio callback is checked for allocations and locks (PB-3).
- **Exit (C++ tests on synthetic audio):** a splice lands within ±1 sample with no doubled click (AT-3 on fixture A data); fades have the right lengths; routing sums match the rules; 44.1 → 48 kHz resampling of a 1 kHz sine keeps THD+N below −90 dB (PB-2); 16 tracks render offline faster than 20× real time.

### WP3: Protocol 2 and the engine client · M
The messages above on both sides; `EngineClient` posts commands and publishes snapshots on the UI thread at display rate only while something moves (idle = no timers, PB-90); device listing and selection; reattach (`getState`) and engine-crash recovery (the app notices the disconnect, shows the banner "Audio engine stopped. Restarting it.", starts a new engine and reloads the song, stopped).
- **Exit:** C# integration tests start the real engine, load a generated multitrack folder, render offline, seek, and read back meters and the clock; a test kills the app side mid-play and reattaches; a test kills the engine and checks the restart. Command round trip under 5 ms on loopback (measured and logged).

### WP4: Core domain · M (good first tasks for learning C#)
`Song`, `Track`, `MixPreset`, `Library`, `Settings` (DM-10/12/13, SET-10); JSON persistence with `System.Text.Json` source generation, `schemaVersion: 2`, atomic autosave under `%APPDATA%\RECompose` and `~/Library/Application Support/RECompose` (DM-90); `ImportRules` (SL-52 role/group keywords and lane order, SL-53 BPM/key regexes, the `Key` type from M0); `TempoGrid` behind the SEC-7 interface; `Readouts` (PB-32 song bar, PB-33 times, PB-34 chart bar); `PlanCompiler` (as written → segments); `CommandBus` with undo groups and the log.
- **Exit:** unit tests, including fixture A numbers: at source 78.52 s → `32.2`; at 72 BPM in 3/4 a bar is 2.5 s; vendor-style file names map to the right role and group (`Click Track.wav` → click, `Synth Bass.wav` → Bass, `Keys 3.wav` → Keys, `BGV.wav` → Vocals).

### WP5: Import pipeline · M
The `probe` and `peaks` modes of `recompose-engine` and a job runner in the app (parallel, low priority, cancellable); folder picker, drag and drop and Ctrl+I; the **import review** dialog (SL-51) with editable role, group and output; duplicate detection by audio hash (SL-55); the peaks cache in `cache/peaks/`; the library list with search and meta line (SL-56).
- **Exit:** a 16-stem folder reaches the review dialog in under 10 s and has peaks within 30 s on the reference machine; a corrupt file is reported in the review ("Can't read this file") without stopping the import.

### WP6: Design system port · M
A generator (`tools/tokens`) turns `design/tokens.json` into Avalonia resources: colours per theme variant (Dark, Light, and a custom High contrast variant), type styles, spacing, radii, sizes and durations. CI checks that the generated files are up to date. Also: fonts (Hanken Grotesk and Martian Mono variable weights, Martian Mono's width axis at 87.5 for small mono text, the REC Accidentals fallback for ♭ ♮ ♯), icons as tintable path geometry, and base control styles: Button and key, IconButton, SegmentedControl, Select, TextField, NumberField, SearchField, TabBar, StatusPill, StatusIndicator, Fader, Meter, Toast, Banner.
- **Exit:** a hidden gallery window shows every M1 component in all states, compared side by side with the design-system previews.

### WP7: Prepare shell · L
Top bar (Perform switch disabled until M5, audio status indicator); library sidebar; song header (title, `{key}  {BPM} BPM  {meter}`, compact transport, readouts, status pill); timeline (ruler in bars with labels at least 55 px apart, lanes with header, M/S and output tag, cached waveform bitmaps, a playhead layer that redraws only while playing, click-to-seek, scrub, zoom/fit/follow); editor tabs MIXER and SONG; Settings › Audio; toasts and banners (file missing, engine restarted).
- **Exit:** demo steps 1–9 work against real stems.

### WP8: Hardening and demo · S
A performance pass against PB-90/91 on the product owner's PC (app and engine processes together); a 30-minute playback soak with random seeks at 256 frames and 0 xruns; a missing-file run (REL-50: the rest plays and the lane shows "File missing"); log lines; CI artifacts for `win-x64` and `osx-arm64` containing both executables; README run instructions; a rehearsed demo.

**Order:** WP1 → WP2 and WP4 in parallel (C++ and C# don't depend on each other) → WP3 → WP5 and WP6 in parallel → WP7 → WP8.

## Test fixtures

Synthetic only; no purchased stems in the repository. A fixture generator writes WAV folders on the fly: a fixture-A-like song (72 BPM, 3/4, 124 bars, downbeat 0.02 s, a click with an accented beat 1, and per-track tones and noise so splices and routing are measurable), at 44.1 kHz and 48 kHz, in mono and stereo. Real stems stay on the product owner's machine for the manual demo.

## New dependencies

| Package | Use | Licence |
|---|---|---|
| r8brain-free-src 6.5 | resampling (engine) | MIT |
| Steinberg ASIO SDK | ASIO on Windows | GPLv3 (option) |
| CommunityToolkit.Mvvm 8.4 | view models | MIT |

## Risks

| Risk | Mitigation |
|---|---|
| Meter and clock traffic across the process boundary | Snapshots only while something moves; the UI extrapolates the clock. If JSON at 60 Hz costs too much, add a shared-memory snapshot behind the same protocol (measured in WP3) |
| Orphaned or duplicate engine processes | One endpoint per user, reattach on start, idle exit, `shutdown` accepted by every version (M0 already tests these) |
| JUCE's message loop in a helper process on macOS (no app bundle in development) | WP1 first; fall back to driving `AudioIODeviceType` directly if `AudioDeviceManager` misbehaves |
| Variable fonts in Avalonia 12 (weights, Martian Mono's width axis, family-name lookup) | Checked in WP6; fall back to static instances generated from the variable fonts |
| Timeline cost with 16 lanes | Waveforms drawn from cached peak bitmaps; only the playhead layer redraws while playing |
| ASIO SDK availability for CI | Build ASIO locally from the SDK path; CI builds without ASIO if the SDK can't be fetched |
| No Mac for hands-on checks | CI builds and tests macOS; real CoreAudio playback checked when a Mac is available |
