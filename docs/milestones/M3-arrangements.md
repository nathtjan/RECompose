# Milestone 3: Arrangements

**Goal:** the MD shapes a song for this Sunday and hears it. A song gets named variants ("Short", "Christmas"); each variant is a list of entries (sections with options, click bars, pad bars, holds); the MD builds one by clicking the palette, dragging in the timeline, or typing `V1 C V2 C B x2 C+2 E`. The header counts the arrangement (`NOW CHORUS 1 · pass 1 of 2 · bar 6 / 14`, `NEXT · ARRANGEMENT · CHORUS 1 (pass 2) · in 8 bars, 2 beats`), and Play runs it with sample-accurate splices.

**Starting point:** milestone 2. Songs have a grid and sections; the only plan is the song as written.

## Decisions for this milestone

1. **No services yet.** ARR-1 puts the arrangement choice on a service entry (variant, custom, as written). Services arrive in M4, so in M3 the song itself carries `arrangements` and `defaultArrangementId` (DM-10), and the header's Variant select picks the one that plays. The plan builder takes `Arrangement?` (null = as written), so M4 only adds the layer that resolves an entry to one. "This service only" (ARR-12, AT-15) is therefore M4.
2. **Plan time starts at the first playable item.** PB-41: a lead-in shorter than half a beat is dropped, so the plan starts at S0. This changes one thing from M2: the engine's frame 0 is no longer source second 0. The app keeps two clocks, *plan time* (the engine's position, the as-heard timeline) and *source time* (sections, the as-written timeline, M, review), and converts with the plan.
3. **The engine plays a plan.** Protocol 3 adds `setPlan`: contiguous segments in plan seconds, each with a source start, optional fades, and the tracks muted in it. The engine converts to frames at its own rate, so a device rate change needs no resend. Contiguous segments stream without a seam; a jump splices (incoming starts on the boundary, outgoing audio continues for 10 ms with a fade-out, PB-55). Without a plan the engine plays the file as before.
4. **What is audible in M3.** Sections in any order, partial ranges, repeats, per-entry mutes, fades, the ring-out tail, and the playing order of everything. Not audible yet, but stored, shown and counted: key lift (needs the render cache, M6), click bars (click generator, M4), pad bars (pad player, M4), holds and open repeats (live control, M5; in M3 an open repeat plays once and a hold is passed over). The ENTRY tab says so next to each of those options.
5. **Editing while playing** (ARR-90) rebuilds the plan and keeps the playhead on the same entry and pass; the engine re-streams from there, so there can be a short gap. The gap-free swap comes with live plan changes in M5.
6. **Options live in the ENTRY tab.** Double-clicking a block in the timeline selects it and shows its options there; there is no separate popover.

## Definition of done: the demo

On the product owner's PC with a song that has sections (milestone 2):

1. Open the **ENTRY** tab. Variant shows **As written**. The summary line lists the sections as written with total bars and time.
2. **New variant…** from the current arrangement, name it "Short". Click palette chips `Verse 1`, `Chorus 1`, `Verse 2`, `Chorus 1`, `Ending`; the list and the summary line follow. Play: the header says `NEXT · ARRANGEMENT`, and the audio jumps without a click at each splice.
3. Type `V1 C V2 C O E` in Quick order. The preview shows the resolved labels (`Chorus 2` is the chorus after Verse 2). **Apply**; Ctrl+Z undoes it.
4. Select the second chorus: set **Repeat** ×2, **Range** bars 1–4, **Drums out**, **Fade out** 2 bars, a note. The row shows the badges; the header shows `pass 1 of 2` and the note; the drums go silent in that entry only.
5. Switch the timeline to **As heard**. Drag a block to reorder it, drag its edge to trim the range (tooltip `bars 1–4 of 8`), right-click for Repeat, Insert, Remove. Switch to **As written**: the sections lane is back and the song plays from where the playhead was.
6. Add a Hold and a Click bars entry; they appear in the list and the timeline, and the summary line counts them. Delete a section: its entries show a warning `Section removed — choose a replacement`.
7. Re-detect sections: the arrangement keeps pointing at the same Chorus 2 (SEC-17). Close and reopen the app: the variants and the selection are there.

CI repeats the numbers on generated audio: splice accuracy (AT-3), the plan of fixture A's Demo variant (AT-1, AT-2), the shorthand of fixture B (AT-10, AT-11), mutes (AT-12).

## Scope

**In**

| Area | Requirements | Notes |
|---|---|---|
| Model | DM-30, DM-10 `arrangements`, `defaultArrangementId` | Variants on the song; entries as in DM-30; old song files open |
| Plan builder | PB-40, PB-41 (steps 1–5), PB-42 | Pure, in Core. Lead-in, entries, repeats, ranges, tail, cumulative plan times |
| Readouts | PB-30, PB-31, PB-32, PB-33, PB-35, LIVE-20 rule 4, LIVE-21 | Pass info, MD note, remaining time including the tail, `+` for open ends, NEXT tag `ARRANGEMENT` or `AS WRITTEN` |
| Editor | ARR-10, ARR-11, ARR-13, ARR-30…40 (store and show), ARR-50 | Palette, list, options, variant controls, summary line, validation, undo |
| Shorthand | ARR-60 | Parser with preview, suggestions for unknown tokens |
| Timeline | UI-15 As heard / As written, ARR-20 | Arrangement lane, drag reorder, Alt-drag duplicate, edge trim, context menu, badges, re-sequenced waveforms |
| Sections and arrangements | SEC-17 (second half), SEC-54 | Boundary moves change lengths, delete flags, merge remaps, re-detect remaps by type, number and occurrence |
| Engine | PB-55, PB-57, PB-59, PB-26 in plan time | `setPlan`, splices, per-segment mutes with 10 ms ramps, linear-in-dB fades, protocol 3 |
| Persistence | DM-90 | Autosave on every edit; one undo step per edit |

**Out:** services and per-entry variants, key, tempo (M4); click and pad generation (M4); holds, open repeats, vamp, jumps (M5); rendered key lifts (M6); guide-call replacement (M8); the assistant's arrangement tools (M9); rehearsal loops and practice tempo (M5); A–B loop, audition, "Play from here" (M5).

## Architecture

```
RECompose.Core (pure, tested)                           app                         recompose-engine
────────────────────────────────────────────            ──────────────────────      ────────────────────────────
Arrangement, ArrangementEntry (DM-30)                   MainViewModel               Plan: segments (plan frames,
ArrangementEdit  add/move/remove/options/remap          EntryViewModel   ENTRY tab   source frames, fades, mutes)
QuickOrder       parse + resolve + preview              TimelineView     As heard    DiskStreamer follows the plan
PlanBuilder      Arrangement? + sections → SongPlan     setPlan ───────────────────► Player: segment mutes, fades,
SongPlan         items, plan↔source time, readouts                                    end at plan length
ArrangementValidator, ArrangementSummary
```

**Plan time and source time.** `SongPlan.SourceAt(planSec)` and `SongPlan.PlanAt(sourceSec)` convert; `PlanAt` returns the first item that contains the source time, or null when the arrangement doesn't play it. Section editing (M, split, set start, the Playhead buttons) uses source time. Review and the ▶ call button need the song as written and are disabled with the reason while a variant is selected.

**The engine's plan.** Segments are contiguous and in plan seconds:

```json
{"type":"setPlan","songId":"…","segments":[
  {"start":0,"end":25,"src":0,"fadeIn":0,"fadeOut":0,"mute":[]},
  {"start":25,"end":45,"src":100,"fadeOut":5,"mute":["drums-1"]},
  {"start":45,"end":55,"src":null}]}
```
`src: null` is silence (click and pad bars until M4). The engine rounds each start to a frame once, so neighbouring segments never overlap or leave a hole, and splits a 1024-frame block at a segment boundary. A segment whose source start equals the end of the one before is contiguous: no splice, no resampler reset.

## Rules as implemented (plan building, PB-41)

1. Resolve the arrangement; null plays the written sections in order with no options.
2. Lead-in: source `[0, first section start)` when it is at least half a beat, otherwise dropped.
3. Each entry: a section item for `srcStart = start + (fromBar − 1)·barLen` to `start + toBar·barLen` (or the section end), expanded into `repeat.count` passes; `untilCued` is one item that counts once and is marked open. Click and pad entries are silent items of `lengthBars`; a hold is a zero-length item. Entries whose section is gone are skipped and flagged.
4. Fades: `fadeIn` on the first pass, `fadeOut` on the last.
5. Tail: unless the last section item has Ending = Cut, `tailBars` (2) of source audio after it with a fade-out over its whole length, shortened to the audio left in the file and dropped when under half a beat (so an Ending that reaches the end of the file has none).
6. Plan times accumulate; song bars count from the first section item (S0).

## Work packages

| # | Package | Size | Exit |
|---|---|---|---|
| WP1 | Core model and edits: DM-30 types, `ArrangementEdit`, variant operations, SEC-54 remap, persistence of old files | M | Unit tests: edits, undo values, remap on delete/merge/re-detect, a song file without arrangements opens |
| WP2 | Core plan: `PlanBuilder`, `SongPlan`, readouts, time conversion, validation, summary | L | Fixture A numbers: AT-1 (`bar 1.1 / 38`, `0:00 / −1:40`, NEXT in 10 bars), AT-2 (`pass 1 of 2 · bar 6 / 14`, `bar 16.2 / 38`, `−1:01`) |
| WP3 | Core shorthand: `QuickOrder` | S | AT-10, AT-11 on fixture B; unknown tokens with suggestions |
| WP4 | Engine: protocol 3, `setPlan`, plan-aware streaming and player, splices, mutes, fades | L | C++ tests with offline renders: AT-3 splice to ±1 sample and no pop, AT-12 mute ramps, fade shape, end of plan |
| WP5 | App: plan in the view models, sending the plan, plan/source time, header readouts, sections tab and M on source time | M | Presentation tests with a fake engine |
| WP6 | App: ENTRY tab, palette, list, options, variant controls, quick order | L | Headless window tests; demo steps 1–4, 6–7 |
| WP7 | App: As heard timeline: arrangement lane, drag, trim, menu, re-sequenced waveforms | L | Demo step 5 |
| WP8 | CI, test guide, handoff | S | Green on all three platforms |

## Risks

| Risk | Mitigation |
|---|---|
| Splices clicking or leaving a gap at block edges | The engine rounds starts once, splits blocks at boundaries, and tests the rendered samples around every kind of boundary |
| Waveform drawing re-sequenced per item is slow at high zoom | Draw each item from the peaks buckets it covers; cache the geometry per zoom level |
| Two clocks confuse the sections code | Every use of the playhead is named `PlanSec` or `SourceSec`; tests mark and split while a variant plays |
| Shorthand ambiguity | Rules from ARR-60 are tested on fixtures A and B; the preview shows the result before Apply |
