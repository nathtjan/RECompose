# Milestone 1: Engine and import

**Goal:** import a multitrack folder and play it through the real audio interface with a working mixer, output routing, transport, a basic timeline and correct readouts. This is the foundation every later milestone plays through, so the engine is built to its final shape now (segment scheduler, splices, routing), even though the UI only plays songs as written.

Spec references: 04-playback-engine (PB), 09 §SL-50…60 (import, library, SONG tab), 03 (DM-10/12/13/90), 02 (UI-10…16), 13 (PLAT-10), 14 (SET-10, KEY-1). Starting point: milestone 0 (this repository's skeleton: engine C ABI, Avalonia shell, CI on three OSes).

## Definition of done: the demo

Run on the product owner's Windows PC with the audio interface, and from the CI build on a Mac if one is available.

1. Launch RECompose. It opens in Prepare (dark theme, Instrument styling) with an empty library and the prompt to import a song.
2. Drag a purchased stems folder onto the window (or press Ctrl+I). The **import review** lists every file with its detected role, group and output, plus the title, key and BPM parsed from the file names. Confirm the import. The song appears in the library with `{key} · {BPM} BPM · {meter} · {n} tracks`, and waveforms are computed in the background.
3. Double-click the song. The status pill reads `LOADING` with progress, then `READY`. The timeline shows the ruler in bars and one lane per track, in spec order: Click, Guide, Drums, Percussion, Bass, Keys and so on, with waveforms.
4. Press Space. The music plays, the playhead moves and the readouts follow (on a fixture-A-like song: `bar 32.2 / 124`, `1:18 / −3:51`, `PLAYING`, `chart bar 32.2`).
5. Open the MIXER tab. Move a fader, mute Drums, and solo Keys (click and guide keep playing, PB-82). The meters move. Ctrl+Z undoes the fader move and a toast confirms it.
6. In Settings › Audio, choose the interface (ASIO on Windows) and set **Cue** to outputs 3–4. Click and guide now go to the in-ear outputs while the band stays on 1–2.
7. Click the ruler while playing. The playhead jumps with no click or pop. When stopped, ← and → step one bar. Stop returns to the start.
8. Quit and relaunch. The library, the mix and the audio settings are still there.
9. Task Manager or Activity Monitor: idle with a song loaded ≤ 3 % CPU; 16 stems playing well under 20 %; the xrun counter stays at 0 at 256 frames / 48 kHz.

## Scope

**In**

| Area | Requirements | Notes |
|---|---|---|
| Engine playback | PB-1 (without key/tempo), PB-2, PB-3, PB-4, PB-55, PB-56, PB-93 | Streaming, high-quality resampling, segment scheduler with splice fades, one audio clock |
| Transport | PB-10, PB-20, PB-21, PB-22, PB-26, PB-36 | Play/Pause/Stop with 10/20/50 ms fades, seek by ruler; ⏮ goes to the plan start; ⏭ waits for services (M4) |
| Readouts | PB-31…34 subset, PB-36 | Song bar, elapsed/remaining, status pill, chart bar. NOW/NEXT labels need sections (M2) |
| Grid | SEC-7 interface; SEC-1 step 4 | BPM and meter from the file name or typed in the SONG tab, downbeat at 0. Click analysis is M2 |
| Mixer | PB-80, PB-81 (Default preset only), PB-82, PB-84 | Fader −∞…+6 dB, double-click = 0.0, M, S, post-fader meters, MASTER |
| Routing | PB-85 (Main, Cue) | Logical outputs → device channels; role-based defaults; per-track override; mono/stereo rules |
| Audio devices | PLAT-10, SET-10 subset | WASAPI shared/exclusive, ASIO, CoreAudio; rate and buffer; driver type in the status label; xrun count (REL-24) |
| Import & library | SL-50 (folders), SL-51, SL-52, SL-53, SL-54 (peaks only), SL-55, SL-56 subset | Review dialog, role/group detection and lane order, BPM/key parsing, duplicate detection by hash |
| Data | DM-10/11/12/13, DM-90 subset | `library.json`, `songs/<id>.json`, `settings.json`, autosave within 500 ms (atomic), `cache/peaks/` |
| UI | UI-10 shell, UI-11 subset, UI-12 library block, UI-13, UI-15 (as heard = whole file), UI-16 MIXER and SONG tabs | Perform switch shown but disabled until M5 |
| Design system | tokens, fonts, icons, base components | Generated from `design/tokens.json`; Dark is checked visually, Light and High contrast are generated |
| Commands & log | CommandBus (spec 18 §1), AUTO-50 subset | Undo/redo, autosave, `Loaded`, `Play at bar`, `Pause at bar`, `Stop`, `File missing` |
| Shortcuts | KEY-1 subset | Space, ←/→ (stopped), F + − ., Ctrl+I, Ctrl+Z / Ctrl+Shift+Z, Ctrl+, |

**Out** (later milestones): sections and analysis (M2), arrangements and the plan builder beyond as written (M3), services, keys, tempo, pads and transitions (M4), the Perform view (M5), the render cache, failover, journal and panic (M6), voice (M7), cues, MIDI and the log tab (M8), the assistant (M9), signing and installers (M10). Zip import (SL-50) waits for M4.

## Architecture for this milestone

```
Avalonia UI (Prepare shell, timeline, mixer, settings)
   │  view models (CommunityToolkit.Mvvm)
   ▼
CommandBus ── undo/redo, autosave, log
   │
RECompose.Core (pure)                         RECompose.Engine (managed client)
  Library · Song · Track · MixPreset            EngineClient: commands ↓, snapshots ↑
  ImportRules (SL-52/53) · TempoGrid (SEC-7)    SafeHandle, ABI check, 60 Hz poll while playing
  Readouts (PB-31…34) · PlanCompiler ───────►   P/Invoke
                                                   │  C ABI (recompose_engine.h, ABI v2)
                                                   ▼
Native engine (C++/JUCE)
  control thread: parse commands → lock-free queue
  disk thread: JUCE readers → per-track ring buffers (≥ 2 s), prefetch at splice targets
  audio thread: segment scheduler → r8brain resampler → gain/mute/solo ramps → routing matrix
                → device channels; meters; clock (frames rendered, latency-compensated)
  JUCE AudioDeviceManager on its own message thread (see WP1)
```

**Engine ABI v2 (sketch).** The hot path is a fixed C struct; everything else is UTF-8 JSON, so the same messages can later cross a process boundary.

| Function | Purpose |
|---|---|
| `rec_engine_create(config_json)` / `destroy` | lifecycle; config = sample-rate preference, buffer, logical output map |
| `rec_engine_submit(engine, command_json)` | `setDevice`, `loadSong` (tracks + segments), `play`, `pause`, `stop`, `seek`, `setTrackGain/Mute/Solo`, `setRouting`, … |
| `rec_engine_read_snapshot(engine, rec_snapshot*)` | transport state, song frame (latency-compensated), sample rate, load %, xruns, per-strip peak/RMS meters |
| `rec_engine_poll_events(engine, buf, cap)` | device list changed, device lost, file missing, load progress, errors |
| `rec_engine_list_devices(engine, buf, cap)` | driver types and devices with channel names, rates, buffer sizes |
| `rec_audio_file_info(path, buf, cap)` / `rec_audio_compute_peaks(…)` | used by import and the waveform cache, so one decoder (JUCE) serves both platforms |
| `rec_engine_render_offline(engine, frames, out)` | tests only: pulls audio through a null device, deterministic |

**Segments, not files.** The C# `PlanCompiler` turns plan items into segments (`songFrameStart, trackId, sourceFrame, frameCount, fadeIn, fadeOut`). In M1 the plan is "the whole file, as written", but the engine already supports any segment list, so the AT-3 splice test runs now and milestone 3 only has to build bigger plans.

## Work packages

The order retires risk first. Each package ends green on CI on all three OSes.

### WP1: Engine spike (risk first) · M
Open a real device from inside the Avalonia process and play a test tone; receive device-change notifications.
- JUCE's `AudioDeviceManager` expects a JUCE message thread. Plan: on Windows and Linux, run JUCE's message loop on a dedicated engine thread; on macOS, initialise JUCE from the main thread so it attaches to the run loop Avalonia already owns. Fallback if that misbehaves: drive `AudioIODeviceType` directly and poll for device changes.
- Enable **ASIO**: the Steinberg ASIO SDK (GPLv3 option) is supplied through `-DRECOMPOSE_ASIO_SDK_DIR`. Confirm where Steinberg publishes it and whether CI may download it.
- **Exit:** tone plays on the product owner's interface via WASAPI and ASIO; unplugging and replugging produces events; the CI smoke test opens the null device on all three OSes.

### WP2: Engine core · L
Streaming (JUCE readers, disk thread, ring buffers ≥ 2 s), resampling (**r8brain-free**, MIT), segment scheduler with splice fades (10 ms), transport fades, gain/mute/solo ramps, the routing matrix (mono → pair equal, stereo → mono −3 dB), post-fader meters, the clock and latency compensation, xrun and load counters, offline render. The audio callback is checked for allocations in debug builds.
- **Exit (C++ tests on synthetic audio):** splice lands within ±1 sample with no doubled click (AT-3 on fixture A data); fades have the right lengths; routing sums match the rules; 44.1 → 48 kHz resampling of a 1 kHz sine keeps THD+N below −90 dB (PB-2); 16 tracks render offline faster than 20× real time.

### WP3: ABI v2 and the managed engine client · M
The functions above, a `SafeHandle`, an `EngineClient` that posts commands and publishes snapshots on the UI thread at display rate only while playing (idle = no timers, PB-90), and device listing and selection.
- **Exit:** integration tests load a generated multitrack folder, play offline, seek, and read back meters and the clock through the C ABI.

### WP4: Core domain · M (good first tasks for learning C#)
`Song`, `Track`, `MixPreset`, `Library`, `Settings` (DM-10/12/13, SET-10); JSON persistence with `System.Text.Json` source generation, `schemaVersion: 2`, and atomic autosave under `%APPDATA%\RECompose` and `~/Library/Application Support/RECompose` (DM-90); `ImportRules` (SL-52 role/group keywords and lane order, SL-53 BPM/key regexes, the `Key` type from M0); `TempoGrid` behind the SEC-7 interface; `Readouts` (PB-32 song bar, PB-33 times, PB-34 chart bar); `PlanCompiler` (as written → segments); `CommandBus` with undo groups and the log.
- **Exit:** unit tests, including fixture A numbers: at source 78.52 s → `32.2`; at 72 BPM 3/4 a bar is 2.5 s; vendor-style file names map to the right role and group (e.g. `Click Track.wav` → click, `Synth Bass.wav` → Bass, `Keys 3.wav` → Keys, `BGV.wav` → Vocals).

### WP5: Import pipeline · M
Folder picker, drag and drop and Ctrl+I; scan audio files through the engine's file-info call; the **import review** dialog (SL-51) with editable role, group and output; duplicate detection by audio hash (SL-55); the background peaks job writing `cache/peaks/`; the library list with search and meta line (SL-56).
- **Exit:** importing a 16-stem folder takes under 10 s to the review dialog, and peaks are ready within 30 s on the reference machine.

### WP6: Design system port · M
A generator (`tools/tokens`) turns `design/tokens.json` into Avalonia resources: colours per theme variant (Dark, Light and a custom High contrast variant), type styles, spacing, radii, sizes and durations. CI checks the generated files are up to date. Also: the font setup (Hanken Grotesk and Martian Mono variable weights, Martian Mono's width axis at 87.5 for small mono text, the REC Accidentals fallback for ♭ ♮ ♯), icons as tintable path geometry, and base control styles: Button and key, IconButton, SegmentedControl, Select, TextField, NumberField, SearchField, TabBar, StatusPill, StatusIndicator, Fader, Meter, Toast.
- **Exit:** a hidden gallery window shows every M1 component in all states, compared side by side with the design-system previews.

### WP7: Prepare shell · L
Top bar (the Perform switch is disabled until M5, audio status indicator), library sidebar, song header (title, `{key}  {BPM} BPM  {meter}`, compact transport, readouts, status pill), timeline (ruler in bars at ≥ 55 px label spacing, lanes with header, M/S and output tag, cached waveform bitmaps, playhead drawn per frame only while playing, click-to-seek, scrub, zoom/fit/follow), editor tabs MIXER and SONG, Settings › Audio, toasts and banners (file missing).
- **Exit:** the demo steps 1–8 work against real stems.

### WP8: Hardening and demo · S
Performance pass against PB-90/91 on the product owner's PC; a 30-minute playback soak with random seeks at 256 frames and 0 xruns; a missing-file run (REL-50: the rest plays and the lane shows "File missing"); log lines; CI artifacts for `win-x64` and `osx-arm64`; README run instructions; a rehearsed demo.

## Test fixtures

Synthetic only; no purchased stems in the repository. A small fixture generator writes WAV folders on the fly: a fixture-A-like song (72 BPM, 3/4, 124 bars, downbeat 0.02 s, a click with accented beat 1, and per-track tones and noise so splices and routing are measurable), at 44.1 kHz and 48 kHz, in mono and stereo. Real stems stay on the product owner's machine for the manual demo.

## New dependencies

| Package | Use | Licence |
|---|---|---|
| r8brain-free-src 6.5 | resampling (engine) | MIT |
| Steinberg ASIO SDK | ASIO on Windows | GPLv3 (option) |
| CommunityToolkit.Mvvm 8.4 | view models | MIT |

## Risks

| Risk | Mitigation |
|---|---|
| JUCE device manager hosted in a .NET process (message thread, macOS main run loop) | WP1 first, with a fallback that bypasses `AudioDeviceManager` |
| Variable fonts in Avalonia 12 (weights, Martian Mono's width axis, family-name lookup) | Checked in WP6; fall back to static instances generated from the variable fonts if needed |
| Timeline cost with 16 lanes | Waveforms drawn from cached peak bitmaps; only the playhead layer redraws while playing |
| ASIO SDK availability for CI | Build ASIO locally from the SDK path; CI builds without ASIO if the SDK can't be fetched |
| No Mac for hands-on checks | CI builds and tests macOS; real CoreAudio playback verified when a Mac is available |
