# Testing milestone 3 on your PC

This build adds arrangements. A song can carry named variants ("Short", "Christmas"); each is a list of entries (sections with options, click bars, pad bars, holds) that you build from the palette, from the timeline, or by typing `V1 C V2 C B x2 C+2 E`. The header counts the arrangement (`NOW CHORUS 1 · pass 1 of 2 · bar 6 / 14`, `NEXT · ARRANGEMENT · CHORUS 1 (pass 2) · in 8 bars, 2 beats`) and Play runs it with sample-accurate splices. Everything from milestones 1 and 2 still works ([M1](M1-test-guide.md), [M2](M2-test-guide.md)).

## 1. Get the build

GitHub → **Actions** → the latest green **CI** run on `main` → **Artifacts** → `RECompose-win-x64`. Unzip it and run `RECompose.exe`, keeping `recompose-engine.exe` and the `models` folder next to it. This build changes the engine protocol (version 3), so use the new `recompose-engine.exe` with the new app; an old engine left running is replaced when the app asks it to shut down, or end it in Task Manager.

## 2. Try the demo

Use a song that already has sections (milestone 2).

| # | Do this | You should see |
|---|---|---|
| 1 | Load the song and open the **ENTRY** tab. | **Playing** in the header and **Variant** in the tab both show **As written**. The summary line lists the sections in order with total bars and time. |
| 2 | **New from As written**, type "Short" in the name box, **Rename**. Remove what you do not want, then click palette chips such as `Verse 1`, `Chorus 1`, `Verse 2`, `Chorus 1`, `Ending`. | The list and the summary line follow each click (a new entry goes after the selected one). Play: the header says `NEXT · ARRANGEMENT`, and the audio jumps at each splice without a click. |
| 3 | Type `V1 C V2 C O E` in **Quick order**. | The preview shows what each word resolved to (`Chorus 2` is the chorus after Verse 2) and flags words it does not know, with a suggestion. **Apply** replaces the list; **Ctrl+Z** undoes it. |
| 4 | Select the second chorus. Set **Repeat** ×2, **Range** bars 1–4, **Drums out**, **Fade out** 2 bars and a note. | The row shows the badges. In the header: `pass 1 of 2` and the note. The drums are silent in that entry only, with a short ramp, not a cut. |
| 5 | Switch the timeline to **As heard** (button over the lane names; it switches by itself when the variant changes). Drag a block to reorder it (**Alt**-drag copies it). Drag a block's edge to trim its range. Right-click a block. Then switch back to **As written**. | As heard: the lane shows one block per piece, `Chorus 2  2/2`, `(+2)` for a lift, `muted`. The waveforms follow the new order. While you trim, a label reads `bars 1–4 of 8`. The menu has Options, Duplicate, Move earlier/later and Remove. Double-click opens the entry's options. As written: the sections lane is back and the playhead keeps its place in the song. |
| 6 | Add a **Hold** and **+ Click** bars; then delete a section in the **SECTIONS** tab. | They appear in the list and the lane, and the summary counts them. Entries that used the deleted section show `Section removed — choose a replacement`. |
| 7 | Re-detect sections, then close and reopen the app. | The arrangement still points at the same Chorus 2. The variants and the selection are there. |

Keyboard in the ENTRY tab: **Ctrl+D** duplicates, **Delete** removes, **Alt+↑/↓** move the selected entry.

## 3. Please tell me

- Whether any splice clicks or leaves a gap, and which songs; the engine log notes plan changes.
- Whether editing during playback feels right: the playhead stays on the same entry and pass, with a short gap while the engine re-streams.
- Anything on the ENTRY tab or the As heard lane that was hard to find or reach.

## 4. Known gaps in this build

- **Key lift**, **Click** bars and **Pad** bars are stored, shown and counted but not heard yet (the render cache is milestone 6, the click and pad players milestone 4). The tab says so next to each option.
- **Hold** and **until cued** repeats are stored, but play once and are passed over until live control (milestone 5). An arrangement that ends on one warns that the song can only end by cue.
- The entry list reorders with **Move up/down** and **Alt+↑/↓**; there is no drag in the list itself (drag in the timeline works).
- Editing while playing re-streams, so there can be a short gap; the gap-free swap is milestone 5.
- The arrangement is chosen per song (the header's **Playing** select). Choosing it per service entry, and "this service only", arrive with services in milestone 4.

## 5. If something goes wrong

- The log is in `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log`. Lines starting "Plan" show each plan sent to the engine.
- If playback stops after an edit, press Play again and send me the log.
