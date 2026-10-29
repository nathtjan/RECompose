# Milestone 6: Reliability

**Goal:** nothing optional may stop the music (REL principle). Before Sunday the MD runs a **service check** and **renders** the keys and tempos so the live path has no real-time pitch shifting. During the service a lost audio device, a crash, a UI freeze or a lost controller costs as little as possible: the engine keeps its clock, a fallback device takes over, the app resumes at the right bar, and PANIC stops everything without unloading the song.

**Status:** in progress (25 Oct 2026). Done: WP1–WP2 (journal, resume, panic resume), WP3 (service check) with its dialog, and WP7 (device failover, protocol 10: silent clock, fallback device, switch back, xrun log, MIDI inputs reopened when they return, with the MIDI indicator). Done too: WP5–6 (render tool, protocol 11 renders, cache index with LRU eviction protecting the next 7 days, background queue, check item 4, live-key badge). Not started: versions and export (WP8), the rest of WP9 (sleep prevention wired to Perform, journal-failure banner check, Library & cache settings), soak (WP10). The fallback and switch-back paths need a real device and are checked by hand (demo step 3); CI covers the silent clock (AT-43) and its limit.
**Starting point:** milestone 5. Key and tempo are processed live on the disk thread; there is no service check, no render cache, no journal; a lost device stops the clock; PANIC fades the transport and the pads but sends no MIDI; autosave is atomic but keeps no history.

## Decisions for this milestone

1. **The service check is a pure function** (`ServiceCheck` in Core) over a `CheckContext` the app fills in (files, device, cache, disk, power, controllers). Items that need hardware the app cannot see yet (MIDI out ports, voice level test, the performance probe) report `info` with the reason rather than a false ✓. Its result is stored on the service as `lastCheck` with a hash of what it looked at, so the badge can say *Changed since check* (REL-2).
2. **Renders belong to the stem they were made from.** A rendered stem is a new file at the entry's key and tempo (frame f is source frame f × tempo). `loadSong` gives each track the renders the cache holds for it (protocol 11); the disk thread plays the one that matches a segment's key and the plan's tempo, straight, at source ÷ tempo, and processes live where none matches (REL-14). Changing between them is the existing 50 ms key-change crossfade, so mixer, meters and mute lists are untouched. (This replaces the first plan of extra engine tracks chosen by mute lists with a `RenderedMix` mapping in the app: same result, far less to keep in step.) When the loaded entry's renders finish while it is stopped, it loads again with them; one that finishes during a song applies the next time the entry loads (the mid-song switch of REL-14 is a known gap).
3. **Rendering is an engine tool** (`recompose-engine render <in> <out> --semitones N --tempo T`): the same Signalsmith stretcher in an offline pass at its highest quality, FLAC 24-bit out. Jobs run one at a time in a low-priority background queue (PB-92), never in the playback engine process (REL-50). The cache index (`cache/render/index.json`) holds source hash, parameters, bytes and last use; a mismatch is a miss (REL-16); eviction is LRU under the budget and never evicts renders for services in the next 7 days (REL-15).
4. **The engine keeps the clock when the device goes.** On device loss the engine opens the **fallback device** if one is set (within 1 s, at the same position, outputs remapped), otherwise a software clock drives the same audio graph at real-time pace into a silent buffer for up to 60 s, then pauses. A returning main device takes over again; the app asks (button) or switches at the next section boundary (REL-21…23). The test hook `injectDeviceLoss` makes the paths testable without unplugging anything. Xruns are counted with their position (REL-24).
5. **Recovery is a journal plus the plan itself.** The journal (`journal.json`, every second while playing and on each plan change) holds the service, entry, position, transport, pad and the **plan snapshot**: every plan item as plain data (`PlanItemData`), so live changes (jumps, vamps, loops) survive a crash without replaying actions. `state.json` records a clean shutdown. A launch after an unclean one during playback offers *Resume at {section}*, *Resume at the next section*, *Open the service*, *Don't resume* (REL-31); resume loads the entry, restores the plan and starts with a 1-bar count-in at the chosen item (REL-32). PANIC writes the same journal line, so the Perform view can offer *Resume at {section}* (REL-41).
6. **Data safety.** Every document write may leave a version in `versions/<id>/<timestamp>.json`, at most one per 5 minutes per document, 20 kept (DM-90); the service menu lists them with *Restore earlier version…* (REL-45). **Export service…** writes a `.recompose-service` zip (service, song metadata, no audio); **Import service…** relinks by title and hash and lists missing stems.
7. **Interruptions.** In Perform the app prevents system and display sleep (Windows `SetThreadExecutionState`, macOS `caffeinate`), and warns on battery below 20 % once per service (REL-25).
8. **Degradation is visible and quiet.** The status row gets MIDI, RENDER and CPU indicators next to AUDIO; each opens the service check. A lost MIDI input is retried every 2 s by the engine and shown red; journal write failures show a banner and nothing else; failed jobs mark the check ⚠ (REL-50).

## Definition of done: the demo

On the product owner's PC with the service of milestone 5:

1. Open the service check from the badge in the top bar. Items show ✓ ⚠ ✗ with Fix buttons; the result reads `Ready with warnings`. Rename one stem file: item 1 turns ✗ with **Relink…**; put it back and **Run check**: ✓.
2. Change entry 3's key. The row shows ⟳ with a percentage; the check shows item 4 ⚠ with progress, then ✓. Play it: the header's `Live key` tag is gone; CPU in the status row is lower than with live processing. Play it before the render finishes: `Live key — rendering…`.
3. Unplug the audio interface mid-chorus with a fallback device chosen: sound continues on the fallback within a second at the same position, a red banner names it; plug the interface back: **Switch back** appears (and switches by itself at the next section boundary). With no fallback: the clock keeps running silently; plug back within 60 s and the sound returns at the right bar.
4. Kill the app mid-chorus. Relaunch: the dialog reads `RECompose closed unexpectedly during Sunday 26 Oct while playing Champion — Chorus 2, bar 5` with the four choices. **Resume at Chorus 2**: the song loads and plays within 15 s with a 1-bar count-in, with your jump or vamp from before the crash still in the plan.
5. Press **PANIC** (or Ctrl+.): everything stops within 100 ms, pads and the transition click end, and the Perform view says `Stopped by PANIC · Resume at Chorus 2`. The song is still loaded.
6. In Perform the computer does not sleep. Unplug a MIDI controller: the MIDI indicator turns red, playback goes on, and it turns green again when you plug it back in.
7. **Restore earlier version…** on the service lists versions; restoring one brings the entries back. **Export service…** and **Import service…** on a fresh data folder bring the entries back and list the stems that are missing.

CI repeats the numbers: the check's rules (AT-40), render fallback and parameters (AT-41, REL-16), failover and the silent clock (AT-42, AT-43), journal and resume (AT-44), panic (AT-45), degradation (AT-46), and a random-jump soak in offline renders (AT-47 reduced: 200 jumps with no starved blocks).

## Scope

**In:** REL-1…3 (service check, items 1–6, 8, 11, 12 and 13; 7, 9, 10 as info), REL-10…16 (render cache), REL-20…24 (failover, silent clock, xruns), REL-25 (sleep, battery), REL-30…34 (journal, resume), REL-40 and REL-41 (panic, resume after it; MIDI All Notes Off waits for MIDI out, M8), REL-45 (versions, export, import), REL-50 (degradation), DM-70 and DM-80, DM-90 (versions), UI-70 (banners), LIVE-70 (status row indicators), SET-60 (Library & cache: budget, clean), SET-10 (fallback device, auto switch back).

**Out:** the 3-hour soak and the hardware tests of REL-60 (run by hand on the product owner's machines); the mid-song switch to a finished render; voice level test (M7); MIDI out ports and CC 123/120 (M8); the assistant key check (M9); packaging and updates (M10).

## Work packages

| # | Package | Size | Exit |
|---|---|---|---|
| WP1 | Core: `PlanItemData`, `Journal`, `AppState`, resume rules (the item to resume at, the next section) | M | Unit tests: plan round trip with live items, resume targets (AT-44) |
| WP2 | App: journal writer, clean-shutdown flag, resume dialog, PANIC journal line and Perform resume bar | M | Presentation tests against the engine; kill-and-resume test |
| WP3 | Core: `ServiceCheck`, `CheckContext`, `lastCheck`, result hash | M | Unit tests on every rule (AT-40) |
| WP4 | App: check dialog, top-bar badge, Fix actions, Perform run-check, status row indicators | M | Window tests with screenshots |
| WP5 | Engine: `render` tool, `srcScale`, FLAC; tests | M | C++ tool test (length, pitch, tempo); engine test for srcScale |
| WP6 | Core: render cache index, keys, eviction; app: queue, triggers, `RenderedMix`, progress, fallback | L | Unit tests (REL-15, REL-16); Presentation test: rendered entry plays the rendered file (AT-41) |
| WP7 | Engine: device loss paths, silent clock, fallback device, xrun log, `injectDeviceLoss`; app: banners, settings, switch back | L | C++ and Presentation tests (AT-42, AT-43) |
| WP8 | Core/App: version history, Restore earlier version, export and import service | M | Unit and Presentation tests |
| WP9 | App: sleep and battery, MIDI loss and retry, journal-failure banner, Library & cache settings | M | Tests where possible |
| WP10 | Soak test (200 jumps), docs, test guide | S | Green on all three platforms |

Suggested order: WP1–2 (recovery matters most on Sunday), WP3–4, WP7, WP5–6, WP8, WP9, WP10.

## Risks

| Risk | Mitigation |
|---|---|
| The journal's plan snapshot drifts from the plan | Plans are rebuilt from the snapshot only after a crash; a round-trip test over every kind of plan item; a snapshot that fails to load falls back to resuming the written arrangement at the same section |
| Rendered variants and the mixer fall out of step | One mapping object (`RenderedMix`) owns the variants; the mixer sends through it; tests change gain, mute and solo on a rendered entry and read the engine's state |
| A silent clock that is not real time | It runs on the message thread's timer from a monotonic clock and renders in whole blocks; tests check position against a stopwatch with a tolerance; it is a fallback, not the playing path |
| A render hides a quality or length bug | The tool's output is checked in tests for length (within a frame), pitch (by autocorrelation) and tempo |
| The size of the milestone | The order above leaves a working state after WP2, WP4 and WP7; versions, export and the sleep prevention can slip |
