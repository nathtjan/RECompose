# Testing milestone 2 on your PC

This build adds sections. After import, the click track sets the tempo grid and the guide track is transcribed offline. **Detect from guide** then finds the sections, you review the doubtful ones and fix the rest by ear, and the header shows where you are: `NOW CHORUS 1 · bar 6 / 14`, `NEXT · AS WRITTEN · VERSE 2 · in 8 bars, 2 beats`. Everything from milestone 1 still works ([M1 test guide](M1-test-guide.md)).

## 1. Get the build

As in milestone 1: GitHub → **Actions** → the latest green **CI** run on `main` → **Artifacts** → `RECompose-win-x64`. Unzip it and run `RECompose.exe`. Keep `recompose-engine.exe` and the `models` folder (the 142 MB speech model) next to it; without the folder the app downloads the model the first time it needs it.

Building it yourself works as before (`.\build.ps1`); the first build also downloads whisper.cpp, and the speech model is downloaded on first use.

## 2. Try the demo

Use a purchased song that has a **click** and a **guide** track (the guide is the voice that says "Verse one", "Chorus", "1, 2, 3, 4").

| # | Do this | You should see |
|---|---|---|
| 1 | Import the folder (drag it in or Ctrl+I) and confirm the review. | The library row shows "Analysing the click…" and then "Listening to the guide 40 %", and the top bar shows the same while it works. When it's done the meta line has the BPM and meter from the click track. If the file name said another BPM, the click wins (the log says so). |
| 2 | Load the song. | Under the title: **No sections yet** with **Detect from guide** and **Mark at playhead (M)**. The timeline has an empty **SECTIONS** lane under the ruler. |
| 3 | Press **Detect from guide**. | In about a second the **SECTIONS** tab shows the result box, e.g. "…: 12 sections: Verse 1, Chorus 1, Verse 2, … Click: 72 BPM, 4 beats per bar. Guide: 12 sections from 19 spoken calls. 1 needs review." The table lists each section with its badge (V1, C1…), type, start, chart bar and length; the lane shows coloured, lettered blocks. Repeated names are numbered: Chorus 1, Chorus 2. |
| 4 | Press **Review (1)**. | The app plays two bars either side of the flagged start and shows what it heard: "heard 'verse three' at 1:24.2 · chart bar 27". **Enter** accepts, **←**/**→** move it a bar and play again, **R** renames, **T** changes the type, **Delete** removes it, **N** skips, **Esc** stops. |
| 5 | Play from the start. | **NOW** reads LEAD-IN with a beat countdown, then the section and its bar (`bar 3 / 8`) with a progress bar; **NEXT · AS WRITTEN** counts down to the next section. **SONG BAR** now counts from the first section; `chart bar` still counts from the first downbeat. |
| 6 | In the lane, drag a block's left edge. | It snaps to bars (hold **Alt** for beats, for pickups). Double-click a block to rename it; right-click for Change type, Split at playhead, Merge with next, Delete. In the table, type a new name or start and press Enter; **Playhead** moves a start to the playhead; ▶ plays the guide call. **Ctrl+Z** undoes each change. |
| 7 | Delete all sections (or use a song without a guide), play, and press **M** at each new section. | Each press adds a section on the nearest bar, named from the nearest guide call ("Chorus") or "Section 3". The toast says "Marked Chorus at chart bar 11" with **Undo**. Pressing M where a section already starts makes its row flash. |
| 8 | Rename "Tag" to "Tag (big)", then **Detect from guide** again. | It asks first, keeps your edit, and the result box ends with "Kept 1 edited section." |
| 9 | **SONG** tab. | **Re-analyse click** sets BPM, meter and first downbeat from the click again; **Detect from click** only the first downbeat; **Re-transcribe guide** listens again. Values you type and save are kept until you re-analyse. |

## 3. Please tell me

- Which songs (vendor) detected well and which didn't: the result box text and the log lines starting with "Sections" and "Analysis" help most.
- Calls the guide says that were missed or misread (for example "Turn", "Instrumental", "Down chorus", "Refrain").
- How long "Listening to the guide" takes after an import on your PC.

## 4. Known gaps in this build

- Songs still play **as written**; arrangements (reordering, repeats, key lifts) are milestone 3. NEXT is always **AS WRITTEN**.
- ▶ in the table plays the original guide call at its place in the song; replacing calls after arrangement changes is milestone 8.
- Speech recognition is English only.
- The rest of milestone 1's gaps still apply (no themes switch, no installer).

## 5. If something goes wrong

- The log is in `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log`; "Analysis" and "Sections" lines describe what the click analysis and Detect found.
- If transcription fails with a download message, copy `ggml-base.en.bin` (from the CI download's `models` folder) into `%APPDATA%\RECompose\models\`.
- Detect needs a track whose role is **Guide** (set it in the import review). Without one, mark sections with M.
