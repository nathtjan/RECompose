# Testing milestone 5 on your PC

This build adds the Perform view and live control. Press **Ctrl+P** and the window becomes the screen you read from the keyboard stand: what is playing, what comes next and when, the plan in play order, the setlist and a deck of big keys. While a song plays you can jump to another section, vamp, repeat, move on, end or cancel, with the keys, a page-turner or a foot pedal, and the song carries on with no gap. A hold waits for GO. Service lock keeps stray touches from stopping the music, and Rehearsal mode loops a section with a count-in. Everything from milestones 1 to 4 still works ([M1](M1-test-guide.md), [M2](M2-test-guide.md), [M3](M3-test-guide.md), [M4](M4-test-guide.md)).

## 1. Get the build

GitHub → **Actions** → the latest green **CI** run on `main` → **Artifacts** → `RECompose-win-x64`. Unzip it and run `RECompose.exe`, keeping `recompose-engine.exe` and the `models` folder next to it. This build changes the engine protocol (version 9), so use the new `recompose-engine.exe` with the new app; an old engine left running is replaced when the app asks it to shut down, or end it in Task Manager.

Use the service of milestone 4: four songs with sections. Give one of them a variant with a hold and an until-cued repeat: in the ENTRY tab add **Verse 1 · Hold · Chorus** with *until cued* on the chorus (**Hold** and **Repeat → Until cued** are in the entry's settings). For the pedal, a MIDI foot controller (a sustain pedal on a keyboard works) plugged in before you start the app.

## 2. Try the demo

| # | Do this | You should see |
|---|---|---|
| 1 | Load entry 1 and press **Ctrl+P** (or **Perform** at the top). | The window changes; playback, if any, does not stop. The status row shows the service name and the clock, **Rehearsal** and **Service lock**, the state pill, **AUDIO** and **PANIC**. The rail lists the songs with the loaded one marked. NOW names the section and the bar in it; the column on the right has song bar, elapsed and remaining; the plan strip has one tile per plan item with the current one outlined in yellow; the mini timeline shows the same plan with the playhead. |
| 2 | Press Play. During a verse click the tile of a later chorus. | The NEXT card turns blue: `NEXT · MANUAL · PENDING`, the target tile has a dashed blue outline, the tiles between are struck through, a dashed marker shows on the mini timeline and the **Cancel** key's LED is lit. At the end of the section the song jumps there with no click and no gap; the strip and the bar numbers follow. **Cancel** (or **Esc**) before that clears the change. |
| 3 | Choose **Next bar** in **Jump timing** and jump again. Press **Vamp** in a chorus, then **Move on**. Press **Repeat**, then **End**. | The jump lands on the next bar line. Vamp repeats the section; NEXT reads `REPEATING` and `cue to move on`; Move on goes on after the pass. Repeat plays the section once more. End goes to the next Ending or Outro, or to the song's ending. |
| 4 | In the song with the hold: let Verse 1 end. | The verse rings out, the pad fades in if the hold has one, the status says `HOLD`, NOW says `HOLD`, the readouts column shows `HOLDING FOR` and a timer, the NEXT card names the chorus and says `on GO`, and the yellow key reads **GO**. **GO** (the key, **Space**, **PageDown** or the pedal) gives the count-in you set, then the chorus; the elapsed time does not count the wait. In the until-cued chorus the NEXT card reads `REPEATING`; **Move on** continues. After 16 passes it continues by itself. |
| 5 | Switch to **Service lock**. Click the mini timeline. Tap a tile. Hold **Stop**. Press **KEY +**. Press **PANIC**. | The timeline does not seek. The tile says `tap again to jump`; a second tap within 2 seconds jumps. **Stop**, **Next song**, **Prepare** and the rail's songs need a held press: a bar fills along the key's foot for 0.6 s. KEY + shows a toast with **Undo** for 2 s. PANIC stops everything at once, with a short fade. |
| 6 | Press **PageDown** and **PageUp** in Perform. Open **Controls** (top bar, or the pedal icon in the status row), press **Learn** on **GO**, then press the pedal. | PageDown is GO, or Move on in a repeat, or the next plan item; PageUp cancels. After Learn the row reads like `CC 64 ch 1 ≥ 64`; the pedal acts and the toast area logs `Pedal: GO`. Two presses within 250 ms count once. **Jump takes effect** and **After a jump** are in the same dialog. |
| 7 | Back in **Rehearsal**: press **L** in a chorus. Set practice tempo 85 % in the status row. Right-click a tile → **Play from here**. Press **Click + guide**. Then switch to **Service lock**. | L loops the section with a bar of click before each pass; press L again to end it after the pass. The badge reads `Practice 85%`, the song is slower and keeps its pitch. Play from here starts at that tile. Click + guide mutes the music only in the engine; your mix is untouched. Service lock puts the loop, the tempo and the click-only mix away. |
| 8 | Play an entry and, in the ENTRY tab, add a section to the end of its variant. | The song keeps playing; the longer plan takes effect after the current section, with no gap. |

## 3. Please tell me

- Whether a jump ever clicks, drops out or lands late (try **Immediately**; it asks for a swap only 50 ms ahead and may fall back to a short gap on a slow disk).
- Whether the Perform screen is readable from where you stand, at 1280 × 760 and larger, and whether the hold buttons feel right.
- Whether your pedal or controller binds and acts the first time, and which messages it sends if it does not.

## 4. Known gaps in this build

- **Controls and playback** is one dialog, not yet the pages of a full Settings window. There is still no **Pad output** selector in Settings › Audio.
- The status row has the state pill, AUDIO and PANIC; the MIDI, VOICE and RENDER indicators and CPU come with milestones 6 and 7. The `VOICE` tag exists but nothing sends it yet.
- Service lock is a switch you set; it does not turn itself on within two hours of the service time (WF-11), and **Panic** has no MIDI All Notes Off or resume prompt (milestone 6).
- A jump on a song that plays resampled or stretched audio restarts those streams at the swap frame; a click can show up on a very slow disk or with extreme tempo. The engine tests cover plain, jumping, late and double swaps.
- The hold's pad and click are in the engine; the pad is covered only by hand because the engine tests have no pad files.
- Old window tests from milestone 1 (`WindowSmokeTests`) stop at their first real wait without failing; they were written that way and are listed for repair.

## 5. If something goes wrong

- The log is in `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log`. Lines starting "Live" show each action and its execute point, "Controls" each key and pedal press, "Perform" the lock and panic.
- If the song stops at a jump, press Play again and send me the log.
