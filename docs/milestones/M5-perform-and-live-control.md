# Milestone 5: Perform view and live control

**Goal:** the MD runs the service. Prepare and Perform are two modes of the same window. In Perform the screen is sparse and large: what is playing and where in it, what comes next and when, a plan strip in play order, the setlist, and a key deck of big keys. During a song the MD redirects it by keys, a foot pedal or a tapped tile: jump to a section, vamp, repeat once, move on, end, cancel. Holds wait for GO, open repeats wait for MOVE ON, the lock keeps stray touches from stopping the music, and Rehearsal mode loops a section with a count-in.

**Status:** in progress (25 Oct 2026). WP1–WP5 and the view-model half of WP6 (live plan, swaps, GO, hold state) are on `main`; the Perform screen is next.

**Starting point:** milestone 4. A service plays from song to song; the plan is built once per entry and changes only when the MD edits it. Holds and until-cued repeats are stored but passed over; there is no Perform view, no lock, no foot control.

## Decisions for this milestone

1. **A live change is a plan rewrite.** The engine still only executes timed segments (ADR 0001). A jump, a vamp pass, a move on or a repeat-once builds a new `SongPlan` in `RECompose.Core` (pure, tested, PB-47) and sends it with a **swap frame**: the engine keeps playing the old plan up to that frame and the new one from it. The prefix of the two plans is identical (past items never change), so the swap needs no seek. This is the gap-free swap that M3 left for M5 (ARR-90).
2. **Gap-free swap in the engine (protocol 8).** `setPlan` gains `swapAt` (plan seconds). The disk thread tags every block with the plan version, rewinds to the swap frame when it sees a newer plan, and primes each stream at the source position the old plan had at that frame; the audio thread drops old-version blocks from the swap frame on. A swap that arrives after the playhead passed its frame falls back to a seek (a short gap), as M3's re-stream does. Streams that resample or stretch are primed with a pre-roll so the restart is inaudible.
3. **Holds are markers in the plan.** A hold is a point where the engine freezes the plan clock (PB-52): the previous item's tail has already played, the pad sounds if the hold says so, and a free-running click runs if it says so. `go` releases it. The count-in on GO is part of the plan after the marker (click items), so the engine needs nothing special for it. The hold timer, the frozen song bar and `Holding for 0:23` come from the snapshot.
4. **Vamp and until-cued repeats are expanded to their passes.** An open item is built as up to `maxPasses` (16) instances of the same pass, marked open; MOVE ON swaps in a plan that ends the repeat at the end of the current pass (PB-50, PB-51). Vamp is the same thing on any item, started live. The NEXT card reads `REPEATING · cue to move on` while it lasts.
5. **One pending action at a time** (PB-45), executed at the execute point of the chosen timing (PB-44). The default is **end of section** for every input; the Perform view has a timing select. The app computes the execute point from the engine's clock, the plan and the song grid, and re-sends the plan at that point when it changes. A pending action shows on the NEXT card (tag `MANUAL`, `KEYS` or `PEDAL`), on the plan strip and in a dashed marker on the mini timeline; Cancel clears it.
6. **Perform lives in the main window.** The mode switch swaps the layout; the loaded song, the transport and the engine are the same, so switching never interrupts playback (WF-10). The Perform layout follows `design/perform-view.md` at 1280 × 760.
7. **Locks (WF-11).** *Rehearsal* allows everything. *Service* locks song edits, ruler seeking and deleting; Stop, Previous/Next song and loading another song need press-and-hold (0.6 s); a plan tile needs a second tap within 2 s; KEY and TEMPO show a 2 s undo toast. Panic is always one press.
8. **Foot control without speech.** The default profile is the page-turner (PageDown and → = GO / next plan item, PageUp and ← = Cancel, or Restart section in Rehearsal lock). MIDI input comes from the engine (JUCE), with MIDI Learn in Settings › Controls, 250 ms debounce and long-press bindings (LIVE-50…55). Voice is milestone 7; the `VOICE` tag exists in the model but nothing sends it yet.
9. **Panic is basic.** The key is in the status row (UI-40, REL-40): it stops the transport with a 100 ms fade, stops the pads and the transition click, and clears pending actions. MIDI All Notes Off and the resume prompt come with reliability (M6) and automation (M8).
10. **Rehearsal loops are plan rewrites too.** Loop section (ARR-70) repeats the current item with a 1-bar count-in between passes by building those passes into the plan; Stop or L clears it. Practice tempo (PB-64) changes the plan tempo and re-streams (a short gap is acceptable in rehearsal).

## Definition of done: the demo

On the product owner's PC with the service of milestone 4 (four songs, one with a hold and an until-cued repeat in its variant):

1. **Ctrl+P** (or the mode switch) enters Perform. Playback never stops. The status row shows the service name and the clock, the lock switch (Rehearsal), AUDIO and CPU, and PANIC. The rail lists the four songs with the current one marked. NOW names the section and the bar in it; the readouts column shows song bar, elapsed and remaining; the plan strip has one tile per plan item with the current one filled.
2. Press Play. During a verse click the tile of a later chorus: the NEXT card turns pending (`NEXT · MANUAL · PENDING`), the target tile gets a dashed outline, the skipped tiles are struck through, a dashed marker shows on the mini timeline, and Cancel is lit. At the end of the section the song jumps there without a click or a gap; the plan strip and the bar numbers follow.
3. Choose **Next bar** in the timing select and jump again: it executes at the next bar line. Press **Vamp** in a chorus: it repeats until **Move on**; the NEXT card reads `REPEATING · cue to move on`. **Repeat** plays the current item once more. **End** goes to the ending.
4. In the song with a hold: at the end of Verse 1 the tail rings out, the pad fades in, the status reads `HOLD`, the NOW block says Hold, `Holding for 0:23` counts, and the GO key is the yellow one. **GO** (or Space, or the pedal) gives a 1-bar count-in, then Chorus 1; the pad fades out; the elapsed time excludes the hold. In the until-cued chorus the NEXT card reads `REPEATING`; **Move on** continues; after 16 passes it continues by itself.
5. Switch to **Service lock**. Click the mini timeline: nothing seeks. Tap a tile: it arms (`tap again to jump`); a second tap within 2 s jumps. **Stop** needs a 0.6 s hold with a filling ring. **KEY +** shows a 2 s undo toast. **PANIC** stops everything at once.
6. Page-turner keys: **PageDown** is GO or the next plan item, **PageUp** cancels. In Settings › Controls press Learn on GO and a MIDI foot controller pedal: the binding shows `CC 64 ch 1 ≥ 64`; the pedal acts and shows `Pedal: GO`; two presses within 250 ms count once.
7. Back in Rehearsal lock: **L** loops the current section with a 1-bar count-in between passes; set practice tempo 85 % (badge `Practice 85%`); switching to Service lock clears both. Right-click a tile → **Play from here**.
8. A song with an arrangement edit made while playing keeps playing: the new order takes effect after the current item with no gap.

CI repeats the numbers on generated audio: the swap is sample-exact and gap-free (AT-3 style), holds freeze and release (AT-13), open repeats continue after 16 passes (AT-14), jump timing (AT-27), the NEXT card's states (LIVE-20), the lock's guards (AT-26 minus the window), the pedal debounce (AT-25), the rehearsal loop (AT-16).

## Scope

**In**

| Area | Requirements | Notes |
|---|---|---|
| Live plan | PB-44, PB-45, PB-47, PB-50, PB-51, PB-52, PB-42 | `LivePlan` in Core: pending actions, execute points, jump rules (replace the next item, or continue as written), vamp, repeat once, end, move on, cancel, holds, open repeats with `maxPasses` |
| Plan building | PB-40, PB-41 (holds, open repeats, tail before a hold) | Holds as markers with their count-in; open repeats as passes; ring-out before a hold |
| Readouts | LIVE-10, LIVE-20 (all rules but voice), LIVE-21, LIVE-22, LIVE-23, PB-33 (hold timer) | Pending, hold, repeating, NEXT SONG; hold timer; `AS WRITTEN` tag |
| Engine | PB-52, PB-55, ARR-90 | Gap-free plan swap, hold markers and GO, hold pad and click, protocol 8 |
| Perform view | UI-40, UI-14 (plan strip, All sections drawer), UI-11, LIVE-1, LIVE-30, LIVE-40, LIVE-60, WF-10 | Status row, rail, NOW and NEXT, strip, mini timeline, deck, mode switch, Ctrl+P |
| Locks | WF-11, LIVE-3, PB-12, PB-63, SET-20 (tap-twice, press-and-hold) | Two lock modes, guarded keys, tap-twice tiles, undo toasts |
| Controls | LIVE-50…55, SET-28, KEY-1 | Keyboard and page-turner profile, MIDI in, Learn, debounce, long press, toasts, log lines |
| Rehearsal | ARR-70, ARR-71, ARR-72, ARR-74, PB-64, PB-23 | Loop section with count-in, restart section, play from here, click and guide only, practice tempo |
| Panic | UI-40, REL-40 (basic) | Stop with a fade, pads and transition click off, pending cleared |
| Settings | SET-20 (jump timing, off-plan behaviour, tail, pad fade), SET-28 | A Playback page and a Controls page |

**Out:** voice control and its confirmations (M7: VC-*, AS-45 speech); the service check, render cache, journal and resume, device failover, MIDI All Notes Off (M6: REL-*); cues, section MIDI notes, guide-call replacement and the spoken guide (M8: AUTO-*); the service report and the log tab (M8); the assistant (M9); drag on the ruler for an A–B loop (the loop follows plan items in M5); auto-entering Perform near the service time with the 2-hour rule beyond a prompt (LIVE-1 prompt only); stage displays and the mobile companion (Later).

## Architecture

```
RECompose.Core (pure, tested)                       app                                recompose-engine
────────────────────────────────────────            ──────────────────────────         ───────────────────────────
LivePlan  items + cursor + pending                   PerformViewModel                   Plan swap: version tag,
  Jump / Vamp / Repeat / MoveOn / End / Cancel         status row, rail, strip,           stream restart at the swap
  ExecutePoint(timing)                                 NOW, NEXT, deck, locks             frame, old blocks dropped
PlanBuilder  holds, open passes, tail                ControlsService                    Holds: clock freezes at the
SongPlan(items)  readouts, HOLD timer                  keyboard, MIDI in, Learn           marker; pad and click run;
PerformReadout  NEXT card rules                      MainViewModel                      go releases
ControlMap  bindings, debounce, long press             live plan ↔ engine clock           MIDI input events
                                                     setPlan(swapAt) ─────────────────► snapshot: holding, hold
                                                     go, panic ───────────────────────► time, midiIn events
```

**The swap.** `setPlan {segments, swapAt}` (plan seconds). The engine converts to a frame `F`. The disk thread, on seeing a plan whose `swapFrame` is set, restarts every track's stream at the source frame the old plan reads at `F`, rewinds its write position to `F` and tags new blocks with the plan's version. The audio thread keeps playing blocks of the old version until `F` and drops those from `F` on, then plays the new version's. If the new plan's source at `F` continues the old one the stream carries on with no seam; otherwise the usual splice applies at `F` (PB-55). The click track, the pad bars and the transition trigger follow the new plan the same way.

**Holds.** The plan carries hold frames. When the playhead reaches one the player stops advancing, plays silence on the stems (the tail was in the plan before the marker), and the snapshot reports `holding` with the elapsed hold time. `go` resumes. With `hold.pad` the pad fades in at the marker and out after the next item starts; with `hold.click` a free-running click plays at the song tempo on the Cue output.

**Execute points (PB-44).** For `endOfSection` the end of the current plan item; `nextBar` and `nextBeat` the next line of the song grid; `immediately` now plus 50 ms. If the point is under 30 ms away the next point of the same kind is used.

## Rules as implemented (PB-45, PB-47)

1. One pending action; a new one replaces it.
2. `jumpToItem`: items between the current one and the target are dropped; no-op when the target is already next.
3. `jumpToSection(T)`: if a later item plays T it is a jump to that item; if it is already next, "already next" and nothing is queued; otherwise T **replaces the next item**, then the arrangement continues (or, with the setting, every written section after T, then the tail).
4. `moveOn`: an open repeat or vamp stops repeating at the end of the pass; a hold is released.
5. `repeatOnce`: a copy of the current item is inserted after it.
6. `end`: skip to the first later `ending` or `outro` item, else insert the song's Ending, else the tail.
7. After any of these the past stays as played and the plan, the readouts and the strip are rebuilt from the swap point.

## Work packages

| # | Package | Size | Exit |
|---|---|---|---|
| WP1 | Core plan building: holds with their count-in and pad/click flags, open repeats as passes, ring-out before a hold, segments with hold markers | M | Unit tests: AT-13 and AT-14 plans (`V1 HOLD C`, `V1 C x? E`) with times, bars and labels |
| WP2 | Core live plan: `LivePlan`, pending actions, execute points, the PB-47 rules, vamp, repeat once, end, move on, cancel, hold and repeat state | L | Unit tests: AT-20 (without speech), AT-21, AT-22, AT-23, AT-27 on fixture A and B |
| WP3 | Core readouts: NEXT card rules 0–4, pending and hold and repeating states, hold timer, plan strip model with states | M | Unit tests on the LIVE-20 table and PB-33 |
| WP4 | Engine: gap-free plan swap, protocol 8 | L | C++ offline renders: swap with no seam and no gap, swap onto a jump, late swap falls back, stretched and resampled streams |
| WP5 | Engine: holds, `go`, hold pad and click, hold snapshot fields, basic panic | M | C++ tests: clock freezes at the marker and resumes on `go`, pad and click during a hold |
| WP6 | App: live plan in `MainViewModel` (clock to execute point, swap sends, tags MANUAL and KEYS), mode switch, Perform shell, status row, rail | L | Presentation tests against the engine; demo steps 1, 8 |
| WP7 | App: NOW, NEXT card, plan strip, mini timeline, key deck, timing select, All sections drawer | L | Headless window tests at 1280 × 760 with screenshots; demo steps 2–4 |
| WP8 | App: locks and guards (hold buttons, tap-twice tiles, undo toasts), panic | M | Presentation and window tests; demo step 5 |
| WP9 | Controls: keyboard map and page-turner profile, MIDI in (engine and app), Learn, debounce, long press, Settings › Controls and › Playback | L | Unit tests for the map; engine test for MIDI events; demo step 6 |
| WP10 | Rehearsal: loop with count-in, restart section, play from here, click and guide only, practice tempo | M | Tests (AT-16); demo step 7 |
| WP11 | CI, test guide, handoff | S | Green on all three platforms |

Suggested order: WP1–3 (all pure), then WP4 and WP5 (the engine, with its tests), then WP6–8 (the screen), WP9 and WP10, WP11. After WP6 the app can already change the order of the playing song without a gap.

## Risks

| Risk | Mitigation |
|---|---|
| The gap-free swap is the hardest piece of audio code so far: blocks of two plans in one queue, streams restarted mid-song | Offline renders of every case before any UI depends on it (swap onto a contiguous plan, onto a jump, with resampling and stretching, a late swap); the version tag makes stale blocks cheap to drop; a late swap falls back to a seek |
| Jumps feel late or early against the grid | The execute point is computed from the song grid in Core and tested to the sample against the rendered output |
| The Perform layout does not fit at 1280 × 760 | The design notes give the sizes; the window test renders at the minimum and the deck tightens below 1400 px |
| Foot controller input is hard to test without hardware | The control map is pure and unit-tested; the engine's MIDI input sits behind a small interface with a test injection; the product owner's pedal is the real test |
| Locks that annoy in rehearsal | Rehearsal lock allows everything; Service lock is the default only within 2 hours of the service time (WF-11) |
| Size of the milestone | The order above leaves a working state after WP3, WP5 and WP8; Controls and Rehearsal can be split off if needed |
