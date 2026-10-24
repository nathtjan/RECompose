# Milestone 4: Services

**Goal:** the MD builds this Sunday's service and runs it from song to song. A service ("Sunday 26 Oct · 9:00") is an ordered list of entries; each entry plays a song with its own arrangement (a variant, a custom arrangement for this service only, or as written), key, tempo, mix preset, count-in and transition into the next song. Play runs the service: the music is in the entry's key and tempo, a generated count-in clicks the band in, a pad fades in under the transition, and the next song starts by itself or on Play.

**Status:** planned (25 Oct 2026), not started.

**Starting point:** milestone 3. Songs carry variants and the song itself picks the one that plays (`DefaultArrangementId`); there are no services; key lift, click bars and pad bars are stored but silent; the engine holds one song and plays only its stems.

## Decisions for this milestone

1. **The service entry chooses the arrangement.** A `ServiceEntry` (DM-41) resolves to `Arrangement?` with one function, and `PlanBuilder` keeps taking `Arrangement?` (M3 decision 1). `Song.DefaultArrangementId` goes back to its DM-10 meaning: the variant new entries start with. A library song opened outside a service plays its default variant at the original key and 100 %, so the M3 workflow keeps working, and the ENTRY tab edits the song's variants directly there.
2. **Selected entry and loaded entry are different things** (DM-80, UI-16). The sidebar selection drives the editor tabs; the loaded entry is what the engine plays. The editor header reads `Editing: {song} · service entry {n}`, with a "not loaded" badge and a **Load** button when they differ. As with the two clocks in M3, every use is named `SelectedEntry` or `LoadedEntry`, never "the song".
3. **"This service only" copies the variant** (ARR-12, AT-15). The first edit of a variant from a service asks once per variant: this service only, or all services; the choice can be remembered. A custom arrangement remembers the variant it came from (`copiedFromId`, an addition to DM-30's `origin: copy`), so the ENTRY tab can offer **Revert to "Short"** and **Save as variant…**.
4. **Key and tempo are audible now, through real-time processing.** The render cache (REL-10, PB-5's normal path) is M6; M4 builds the real-time path, which PB-5 and REL-14 require as the fallback anyway, so it is needed regardless. The engine runs Signalsmith Stretch (MIT) per track. A track at key 0 and 100 % bypasses it, so click, guide and untransposed songs stay bit-identical to M3. With the same shifter, **key lifts become audible in M4** (PB-58, 50 ms retune crossfade). The header shows the badge `Live key` whenever a shifted entry plays.
5. **Plan time stays in source-second units under tempo changes** (PB-30). The engine plays the plan at `tf = tempo% / 100` and reports plan frames as before; song bars, the ruler, sections and the two clocks do not change. Only elapsed and remaining time, durations and the summary line divide by `tf` (PB-33, ARR-13, ARR-80).
6. **Generated sources live in the engine** (PB-6, PB-70, PB-72): a synthesized click on the Cue output and a pad player on the Pad output. Plan segments say where the click runs and which pad key sounds, and the engine places both on its own clock, so count-ins and click bars land on the song grid to the sample.
7. **Spoken counts wait for the bundled voice.** A generated count-in and the "spoken count" option on click bars need the TTS voice (PB-71, PLAT-40), which first arrives with voice control (M7) for MD confirmations. In M4 a generated count-in is click only, and the ENTRY tab marks the spoken count as not audible yet, the way M3 marked its stored-only options. AT-32 is checked for the click now and for the count in M7.
8. **The engine gets a second song slot for the next entry.** Segue and crossfade overlap two songs (SL-30), and only the engine can start the next one on the right sample. While an entry plays, the app loads the next entry into the engine's `next` slot (files open, the first 10 s buffered, plan sent), and the engine runs the transition and promotes `next` to `current`. This is the PB-11 preload mechanism, in both modes. M6 adds its reliability checks (render status on the rail, the service check).
9. **What is audible in M4:** entry key and tempo, key lifts, generated count-ins (click), click bars, pad bars, pads from the sidebar with Follow song, all five transition types, auto-advance and per-entry mix presets. Still stored only: holds and open repeats (M5), spoken counts (M7), rendered keys (M6).

## Definition of done: the demo

On the product owner's PC with four imported songs and a pad folder:

1. The sidebar says **Create a service for this Sunday**. **New service…**: name "Sunday 26 Oct", date 26 Oct 2026, 9:00. The empty service says **Add songs from the library (drag them here)**. Drag four songs in, one of them twice. Each row shows index, title, key, variant; the header shows `4 songs · 18:40`.
2. Select entry 1. The editor header reads `Editing: Champion · service entry 1 · not loaded`; **Load**. Set KEY −1: the song header reads `A (−1) · 72 BPM · 3/4 · Variant: Demo`. Play: the music is a step lower, the click and guide are unchanged, the badge reads `Live key`. TEMPO 95 %: the remaining time grows; the song bars stay the same.
3. Edit the Demo variant from entry 1. The app asks whether the change is for this service only or all services; **This service only**. The row and the header show `Custom for this service`; the library song's Demo variant is unchanged. **Revert to "Demo"** undoes it.
4. Entry 3: count-in **Generated, 2 bars**. Play: two bars of click on the Cue output, NOW `COUNT-IN` with the beat countdown, then Verse 1 on the downbeat.
5. Entry 1's transition chip: **Pad**, key D♭, click on, 72 BPM. Play to the end: the pad fades in, the click runs, the status pill reads `TRANSITION`, NEXT reads `NEXT SONG` with entry 2's title. Play starts entry 2 on a click downbeat, the click stops, the pad fades out over 4 s, and the header now shows entry 2.
6. Entry 2 → 3 **Segue**, gap 0: entry 3 starts as entry 2's tail begins. Entry 3 → 4 **Crossfade 6 s**. Entry 4 **Stop** with Auto-advance on: entry 1 of the next row is cued; with it off, the playhead returns to the start of the same entry.
7. Import the pad folder: it appears under **Library › Pads**, not as a song. Press E♭ in the sidebar pads: it fades in; press G: it crossfades. **Follow song** with a `C+2` key lift in the arrangement: the pad moves up two semitones at that boundary. Add **Click bars 4** and **Pad bars 8** entries: both sound.
8. MIXER: **Save as new…** "Rehearsal – band live", mute Keys 1. Entry 2 uses that preset; entry 3 keeps Default. The `#` toggle stops a track from transposing.
9. **Duplicate service…** as "Sunday 2 Nov": same entries, custom arrangements, keys, tempos, count-ins and transitions. Close and reopen the app: the service, the selection and the loaded entry are back.

CI repeats the numbers on generated audio: variant versus service (AT-15), count-in (AT-32, click part), pad transition (AT-30), segue (AT-31), duplicate (AT-33), pad set import (AT-34), key lift with pads following and click and guide bit-identical (AT-11), real-time CPU (PB-91, AT-48 real-time part).

## Scope

**In**

| Area | Requirements | Notes |
|---|---|---|
| Model | DM-40, DM-41, DM-42, DM-60, DM-12 `transposes`, DM-13 | `services/<id>.json`, `padsets.json`; `lastCheck` stored but unused until M6 |
| Services | SL-1, SL-2, SL-3, SL-4, SL-5 (drag, library menu), SL-6, SL-7, SL-12 | New, Duplicate, Rename, Delete; reorder keeps the transition with the entry before the gap; the same song twice |
| Arrangement per entry | ARR-1, ARR-11 (delete with replacement), ARR-12, AT-15, ARR-80 | Variant, custom or as written; Save as variant, Revert; entry and service durations |
| Plan | PB-40, PB-41 steps 1–3 (count-in, `keyTotal`), PB-42 | Count-in item, per-item key, transition point; PB-31 beat countdown before S0 |
| Readouts | PB-30 `COUNT-IN`, PB-33 with `tf`, LIVE-20 rule 0, ARR-13 at the entry tempo | `NEXT SONG` during a transition; status pill `TRANSITION` |
| Key and tempo | PB-1, PB-58, PB-60, PB-62, UI-13, KEY-1 (Ctrl+arrows) | Real-time path; `Live key` badge; range warnings; Shift = 5 % |
| Generated sources | PB-6, PB-70, PB-72, ARR-39, SL-36 | Click on the Cue output; count-ins and click bars; pad bars |
| Pads | SL-40…47, SL-51 (pad-set notice), PB-85 Pad output, SET-10, SET-20 pad fade time | Library › Pads; sidebar pads with Follow song including key lifts |
| Transitions | SL-20, SL-21, SL-30, SL-31, SL-32, PB-25 | Stop, Segue, Crossfade, Pad, Carry over; chip and popover |
| Transport between entries | PB-11 (mechanism), PB-12 (Prepare confirmation), PB-23, PB-24 | ⏮ at the plan start selects the previous entry, ⏭ loads the next |
| Mixer | PB-80 `#`, PB-81 | Presets per entry; fader moves change the preset in use |
| UI | UI-11 service picker, UI-12 (service block, library Songs/Pads, compact pads), UI-16 header, UI-80 empty states, SL-56 (services warning on delete) | Status icons ⚠ ● ▶ on rows (⟳ is M6) |
| Engine | protocol 4 | Segments with key, click and pad; tempo; second slot; transitions; pad player |

**Out:** holds, open repeats, vamp, jumps, the Perform view and setlist rail, locks and their guards (M5: PB-50…52, LIVE-*, PB-63, WF-11); practice tempo and audition (M5: PB-64, ARR-73, SL-22); spoken counts and clips (M7: PB-71); render cache and background renders (M6: REL-10…16, PB-61); service check (M6: REL-1); export, import and version restore of services (M6: REL-45); service report (M8: AUTO-60); the assistant's service tools (M9); auto-entering Perform near the service time (M5: LIVE-1).

## Architecture

```
RECompose.Core (pure, tested)                         app                              recompose-engine
──────────────────────────────────────────            ───────────────────────────      ─────────────────────────────────
Service, ServiceEntry, Transition (DM-40…42)          ServicesViewModel  sidebar       slots: current, next
ServiceEdit   add/move/remove/duplicate               MainViewModel                    Player: plan at tempo tf
EntryArrangement.Resolve(entry, song) → Arrangement?    SelectedEntry / LoadedEntry    Stretcher per track (Signalsmith),
PlanBuilder  + count-in, keyTotal, transition point   EntryViewModel   ENTRY tab        bypassed at key 0 and 100 %
SongPlan     + tf readouts, NEXT SONG                 TransitionPopover                ClickGenerator  ─► Cue output
ServiceDurations (ARR-80)                             PadsViewModel                    PadPlayer       ─► Pad output
PadSet, PadSetDetector (DM-60, SL-47)                 setPlan / setNext / setTransition / pad ─────────►  Transition runner:
                                                                                         segue, crossfade, pad, carry, stop
                                                      ◄──────── snapshot, songChanged ──
```

**Protocol 4.** Changes on both sides in one commit; `hello` and `shutdown` keep their shape.

```json
{"type":"setPlan","slot":"current","songId":"…","tempo":0.95,
 "grid":{"origin":5.0,"beat":0.8333,"beatsPerBar":3},
 "segments":[
  {"start":0,"end":5,"src":null,"click":true},
  {"start":5,"end":30,"src":0.02,"key":-1,"songKey":9},
  {"start":30,"end":50,"src":65.02,"key":1,"songKey":11,"mute":["drums-1"]},
  {"start":50,"end":70,"src":null,"pad":1}]}
```

- `key`: semitones for tracks with `transposes` on (the entry key plus the lift). `songKey`: the pitch class heard in that segment, for pads with Follow song. `click`: the click generator runs on the plan's grid. `pad`: the pitch class of a pad-bars item. `tempo`: `tf`; segment times stay plan seconds.
- `loadSong` and `setPlan` take `slot: "next"`. `setTransition {type, at, gapSec, crossfadeSec, pad: {key, click: {bpm, beatsPerBar}, autoStartBars, fadeOutOnStart}, retuneTo}` describes how `current` hands over to `next`; `at` is the transition point in the current plan.
- `loadPadSet {id, samples: [{pitchClass, file, loop?}]}` and `pad {key | null, fadeSec, gainDb, follow}` drive the pad player, which does not depend on the transport (SL-40).
- Event `songChanged {songId}` when `next` becomes `current`. The snapshot gains `next {songId, buffered}`, `transition` (`none`, `running`, `waiting` for Play) and `pad {key, level}`.

**Real-time key and tempo.** Each transposing or stretched track feeds its resampled stream into a stretcher. The engine reads ahead by the stretcher's latency so that output stays on the plan clock. A splice happens on the stretcher's input side, so the input keeps one continuous signal. A key change at a boundary crossfades over 50 ms (PB-58). Bypassed tracks take the M3 path untouched.

**Two slots.** The app fills `next` as soon as the current entry starts playing, or immediately for Segue and Crossfade. At the transition point the engine starts `next` on the same clock (segue: tail start plus the gap; crossfade: plan end, the outgoing song fading over `crossfadeSec`; pad: the next click downbeat after Play or after `autoStartBars`). Then it swaps the slots and tells the app, which rebinds the header to the new loaded entry and loads the entry after it.

## Rules (plan building per entry)

1. Resolve the entry: a variant by id; a custom arrangement as stored; as written. A variant that no longer exists plays as written and flags the entry (ARR-50 style warning on the row and in the ENTRY tab).
2. Count-in (SL-36): `track` keeps the M3 lead-in rule; `generated` replaces it with a count-in item of 1 or 2 bars ending at S0, with the click on; `none` starts at S0.
3. Each section item gets `keyTotal = keyOffset + transpose`. Segments carry it for tracks whose `transposes` is on (default off for click, guide and spoken guide, DM-12; the mix preset can override it, DM-13).
4. Click bars are silent segments with the click on; pad bars carry their pad key (the entry key or the chosen one), and the pad fades in and out over the pad fade time inside the item.
5. The transition point is the end of the last section item, where the tail begins, or the plan end when there is no tail (SL-30).
6. Durations: an entry's length is its plan length divided by `tf`. The service length adds the entries and the segue gaps, counts a crossfade's overlap once, and counts pad and carry-over transitions as 0 with a `+` (ARR-80).

## Work packages

| # | Package | Size | Exit |
|---|---|---|---|
| WP1 | Core services: DM-40…42 types, `ServiceEdit`, resolution, custom copy, revert and save as variant, variant delete with replacement, persistence, durations | M | Unit tests: AT-15, AT-33, reorder keeps transitions, resolution of a missing variant, durations with tempo and transitions |
| WP2 | Core plan: count-in, `keyTotal`, transposes, tempo readouts, transition point, NEXT SONG, segments with key, click and pad | M | Fixture A at −1 and 95 %: readouts and remaining time; fixture B with a generated 2-bar count-in (AT-32 plan); AT-11 segment keys |
| WP3 | Core pads: `PadSet`, detection of key-named folders, lenient key names | S | AT-34 on a generated pad folder; enharmonic names; a 13-file folder with a click is still a song |
| WP4 | Engine: protocol 4, click generator, pad player (loops, key crossfades, fades, Pad output) | L | C++ offline renders: clicks on the grid to ±1 sample with accents, count-in into S0, pad loop crossfade without a gap, key crossfade |
| WP5 | Engine: real-time key and tempo with Signalsmith Stretch, latency compensation, bypass, 50 ms retune | L | AT-11 (music +2, click and guide bit-identical), tempo 95 % keeps the plan clock, CPU of 16 stems at −1 and 95 % within PB-91 |
| WP6 | Engine: second slot, transitions, `songChanged` | L | Offline renders of AT-30 and AT-31: next song starts on the expected sample; crossfade shape; Stop leaves `next` cued |
| WP7 | App: services in the sidebar and top bar, selected and loaded entry, editor header, empty states, drag from the library, reorder, context menu | L | Headless window tests; demo steps 1, 2 (selection and Load), 9 |
| WP8 | App: ENTRY tab per entry (arrangement choice with the ARR-12 prompt, key, tempo, count-in, mix preset, notes), transition chip and popover, song header line, KEY and TEMPO steppers and shortcuts, MIXER presets and `#` | L | Presentation tests with a fake engine; demo steps 2–4, 8 |
| WP9 | App: pads (Library › Pads, sidebar pads, Follow song, Settings › Audio Pad output) | M | Demo step 7 |
| WP10 | App: running the service (transition status, NEXT SONG, loaded entry following the engine, auto-advance, ⏮ and ⏭ between entries, load-while-playing confirmation) | M | Presentation tests driving a fake engine's `songChanged`; demo steps 5–6 |
| WP11 | CI, test guide, handoff | S | Green on all three platforms |

Suggested order: WP1–3, then WP7–8 with key and tempo stored, then WP4, WP5, WP6 with WP10, WP9 and WP11. Each step leaves `main` working.

## Risks

| Risk | Mitigation |
|---|---|
| Real-time shifting of 16 stems over the CPU target | Bypass at key 0 and 100 %; measure on CI and the product owner's PC in WP5 before the UI depends on it; the render cache in M6 takes it off the live path |
| Stretcher latency breaking sample-accurate splices and the plan clock | Read ahead by the latency, splice on the input side, test the rendered output of a shifted and an unshifted track against each other at every kind of boundary |
| Two songs streaming during a segue or crossfade | `next` buffers 10 s until it is needed; offline test with two 16-stem songs, starved-block counter at 0 |
| Selected versus loaded entry confusing the view models | Separate named properties, the editor header with the not-loaded badge, tests that edit one entry while another plays |
| Services pointing at changed songs (variant deleted, song deleted, sections removed) | Resolution never throws: it falls back to as written and flags the entry; deleting a used variant or song asks for a replacement or warns |
| Large pad files | Stream only the sounding keys; keep the first 2 s of all twelve in memory so a key press starts at once |
| Milestone size (eleven packages, three of them large in the engine) | The order above keeps a demoable state after WP8 (services with stored keys) and after WP6; the scope can be split there if needed |
