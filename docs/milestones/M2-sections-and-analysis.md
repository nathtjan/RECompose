# Milestone 2: Sections and analysis

**Goal:** a newly imported song gets its tempo grid from the click track and its sections from the guide track, the MD reviews the one or two doubtful sections and fixes the rest by ear, and every part of the app names sections with their labels (Chorus 1, Chorus 2). The header says where the playhead is in the song: `NOW CHORUS 1 · bar 6 / 14` and `NEXT · AS WRITTEN · VERSE 2 · in 8 bars, 2 beats`.

**Starting point:** milestone 1. Songs import, play, mix and route; the grid comes from the file name or the SONG tab; there are no sections.

**Spec references:** 05 SEC-1…54, 03 DM-10/20/21, 04 PB-30/31/35 (as written), 02 UI-15 (sections lane), UI-16 (SECTIONS tab), UI-80 (no sections yet), 09 SL-53/54/60, 14 KEY-1 (M, Delete), 13 PLAT-40 (bundled recognition), 15 AT-35…38.

## Definition of done: the demo

On the product owner's PC with a purchased song that has a click and a guide track:

1. Import the folder. The library row shows `Analysing…` and then the meta line with the BPM and meter from the click track. If the file name said a different BPM, the click wins and the log says so (SL-53).
2. Load the song. The header shows the "No sections yet" card with **Detect from guide** and **press M** (UI-80).
3. Press **Detect from guide**. Within a second (the guide was transcribed in the background after import) the SECTIONS tab shows the result box (`{title}: 12 sections: Verse 1, Chorus 1, … Click: 72 BPM, 4 beats per bar. Guide: 12 sections from 19 spoken calls. 1 needs review.`), the table, and a sections lane above the tracks with coloured, lettered blocks (V1, C1, V2…).
4. Press **Review**. The app plays two bars either side of the flagged boundary and shows what it heard ("heard 'verse three' at 1:28.1"). Accept it, or move it a bar with ← and →.
5. Play. The header shows `NOW VERSE 1 · bar 3 / 8`, the song bar counts from the first section, and the NEXT card counts down to the next section.
6. Drag a boundary in the sections lane: it snaps to bars (Alt snaps to beats). Rename a section in the table, change a type, split at the playhead, merge with the next one, delete one. Each change is one undo step.
7. Delete all sections, play, and press **M** at each section. The new sections take their names from the nearest guide call; the toast says `Marked Chorus at chart bar 11 · Undo`.
8. Rename "Tag" to "Tag (big)" and run Detect again: it keeps the edit and says "Kept 1 edited section" (SEC-17).

CI runs the same flow end to end on generated audio: a synthetic click, a guide spoken by a TTS voice, and the real speech model.

## Scope

**In**

| Area | Requirements | Notes |
|---|---|---|
| Click analysis | SEC-1 steps 1–4, SL-53 (click wins) | Engine tool `onsets`; the fit, accents and downbeat in Core |
| Guide transcription | SEC-11, SEC-12, PLAT-40 | Engine tool `transcribe` with whisper.cpp; cached word timestamps; vocabulary prompt |
| Detection | SEC-13…17, SEC-30…32 | Pure rules in Core with confidence, the result text, re-detect that keeps edits, the review flow |
| Sections | DM-20, DM-21, SEC-41…43 | Labels, type keywords, type override |
| Editing | SEC-50…53 | Sections lane, SECTIONS table, M key, + At playhead; all undoable and autosaved |
| Readouts | PB-30, PB-31, PB-32 (S0 = first section), PB-35 | NOW label and bar in section, NEXT card for the as-written plan, LEAD-IN countdown |
| Background jobs | SL-54 | Peaks → click analysis → transcription, with progress on the library row and the top bar |
| SONG tab | SL-60 subset | Re-analyse click, Re-transcribe guide, Detect from click (first downbeat) |

**Out:** arrangements and their remapping on re-detect or delete (SEC-17 second half, SEC-54: M3; nothing uses sections yet besides the as-written plan); playing a single guide call through the recorded clip (AUTO-30, M8); tempo maps (SEC-7 stays an interface).

## Architecture

```
app (C#)                                              recompose-engine (C++)
──────────────────────────────────────────────        ───────────────────────────────────
AnalysisService  ─ job queue per song ──────────────► onsets <click>        JSON: onsets with
  peaks → click → guide (low priority)                                     level and brightness
                                                 └──► transcribe <guide> <model>
SpeechModel  ─ finds or downloads ggml-base.en                             JSON: words with times
                                                                           and confidence
RECompose.Core (pure, tested)
  ClickAnalysis.Fit(onsets) → bpm, meter, downbeat
  SectionDetector.Detect(words, grid, music peaks) → sections, calls, result text
  SectionList (labels, letters, types, edit operations, M marking)
  SongPlan.AsWritten(song) → NOW, bar in item, NEXT countdown
```

**Why the analysis runs in the engine executable.** It reuses JUCE's decoders (the same formats as playback) and r8brain for the 16 kHz conversion, and each job is its own short-lived process at low priority, so a decoder or model crash can't touch playback (PB-92), as with peaks in M1.

**Transcribing only where someone speaks.** A guide track is mostly silence with short calls. The tool finds the speech by level (10 ms frames, gaps under 0.6 s joined), packs those stretches into windows of up to 28 s with a second of silence between them, and maps each word back to its stretch. This avoids whisper's habit of inventing words in silence, gives the decoder more than one isolated word to work with, and is fast: a 5-minute guide with 20 calls needs two or three encoder passes (SEC-11 asks for ≤ 20 s).

**The model.** `ggml-base.en.bin` (142 MiB, MIT, SHA-1 `137c4040…390c`). The app looks next to its executable first (`models/`, where the CI build puts it), then in `<AppData>/RECompose/models/`, and otherwise downloads it once from the whisper.cpp model repository with a progress line and a checksum check. Without it, Detect explains how to get it and M marking still works. Bundling it in the installer is part of M10.

**Vocabulary bias (SEC-12).** The decoder gets an initial prompt made of the call vocabulary: section words (SEC-42), "one" to "ten", counts and directives ("all in", "break", "hits", "drums", "last time", "key change"). The normaliser maps "Verse two", "verse 2" and "Verse II" to the same tokens.

## Detection rules (SEC-13…15) as implemented

1. **Utterances:** words joined while the gap is under 0.6 s.
2. **Calls:** utterances containing a vocabulary term. The result text counts these ("from 19 spoken calls").
3. **Section calls:** calls with a section keyword (SEC-42, first match; "pre chorus" before "chorus"). A trailing number word or digit becomes the section number.
4. **Start:** if count words follow within 2 bars of the call, the first bar line after the last count word; otherwise the first bar line after the call ends. "After" allows a quarter of a beat of spill, so a call that runs onto the downbeat still lands on it.
5. **First-bar rule:** a call inside bar 1 while the music tracks already sound (> −40 dBFS in their peaks) starts the section at bar 1.
6. **Merge:** calls landing on the same bar merge, keeping the more specific name (numbered beats unnumbered). The same name again within 2 bars counts once.
7. **Naming:** spoken numbers are kept; unnumbered verses are numbered in order; other names stay as spoken. Labels follow SEC-41.
8. **Confidence:** the lowest word confidence of the call × 1.0 (count follows), 0.8 (no count) or 0.6 (first-bar rule). Below 0.7 → needs review.

## Work packages

| # | Package | Size | Exit |
|---|---|---|---|
| WP1 | Core: sections model, labels, letters, types (SEC-41…43), edit operations (move, rename, retype, split, merge, delete, mark), as-written plan readouts (PB-30/31/35) | M | Unit tests with fixture A and B numbers: labels Chorus 1/2/3, AT-38 M at 33.9 s → chart bar 11, NOW/NEXT text at AT-1 positions |
| WP2 | Core: click fit and accents (SEC-1), detection rules (SEC-13…17), result text | M | Fixture B words → the AT-35 result text and rows; re-detect keeps a renamed Tag; fits to 0.01 BPM with jitter and missing clicks |
| WP3 | Engine: `onsets` and `transcribe` tool modes, whisper.cpp 1.9.5 via FetchContent, portable CPU flags | M | C++ test for onsets on a synthetic click; CI transcribes a TTS guide with the real model |
| WP4 | App: analysis jobs after import, model lookup and download, SONG tab buttons | M | Presentation tests with a fake tool runner; jobs never block the UI |
| WP5 | App: SECTIONS tab, review flow, sections lane, NOW and NEXT in the header, "No sections yet" card, M key | L | Headless window tests; demo steps 2–8 |
| WP6 | CI end to end, test guide, handoff | S | Green on Windows, macOS and Linux |

## New dependencies

| Package | Use | Licence |
|---|---|---|
| whisper.cpp 1.9.5 (with ggml) | offline guide transcription (SEC-12) | MIT |
| ggml-base.en model | English speech model | MIT |

## Risks

| Risk | Mitigation |
|---|---|
| Vendor guides word calls differently | Vocabulary and keywords in one table in Core; the review flow catches the doubtful ones; M marking always works |
| whisper invents text in silence | Only speech stretches are transcribed; a no-speech threshold drops the rest |
| CPU features of the build machine leak into the binary | ggml built with GGML_NATIVE off, AVX2/FMA on x64 (2013+ CPUs), OpenMP off (no extra runtime DLL) |
| Model download blocked at the church | The CI build ships the model next to the app; the SONG tab says where to put a copy |
| Click tracks with count-in voice or a cowbell on 1 | Outlier rejection in the fit; accents need both level and brightness contrast, otherwise "meter assumed" |
