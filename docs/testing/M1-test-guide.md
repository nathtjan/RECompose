# Testing milestone 1 on your PC

This build plays a multitrack folder through your audio interface: import, mixer, Main and Cue routing, transport, timeline and readouts. The engine runs as its own process, so the music survives the app being closed.

## 1. Get the build

**Download (no tools needed).** On GitHub, open the repository → **Actions** → the latest green **CI** run on branch `m1/engine-and-import` → **Artifacts** → `RECompose-win-x64`. Unzip it anywhere (for example `C:\RECompose`) and run `RECompose.exe`. Keep `recompose-engine.exe` in the same folder.

Windows SmartScreen warns about unsigned apps ("Windows protected your PC"): choose **More info → Run anyway**. Signing arrives with packaging in milestone 10.

**Or build it yourself** (Visual Studio 2022/2026 with "Desktop development with C++", CMake, .NET 10 SDK): in the repository folder run `.\build.ps1`, then `dotnet run --project src/RECompose.App`. The first build downloads JUCE (about 130 MB) and the ASIO SDK.

## 2. Try the demo

Use a real stems folder from your library (WAV, AIFF, FLAC, Ogg or MP3).

| # | Do this | You should see |
|---|---|---|
| 1 | Start RECompose. | The Prepare screen in the dark theme with "Import a song to begin". The top bar shows **AUDIO** with your default device, e.g. "Speakers (Windows Audio) · 2 out · 48 kHz". |
| 2 | Drag a stems folder onto the window (or Ctrl+I). | **Import song** lists every file with its role (Click, Guide, Music), group (Drums, Bass, Keys…) and output (Cue for click and guide, Main for the rest), and the title, key and BPM read from the names. Correct anything, then **Import**. The song appears in the library as "B♭ · 72 BPM · 3/4 · 12 tracks". |
| 3 | Double-click the song. | The status pill goes **LOADING** → **READY**. Lanes appear in the order Click, Guide, Drums, Percussion, Bass, Keys…; waveforms fill in within a few seconds. |
| 4 | Press Space. | It plays; the playhead moves; **SONG BAR** (e.g. `bar 32.2 / 124`), **TIME** (`1:18 / −3:51`), the pill (**PLAYING**) and `chart bar` follow. If BPM or meter are wrong, fix them in the **SONG** tab and press **Save song details**. |
| 5 | **MIXER** tab: move a fader, press **M** on Drums, **S** on Keys. | Meters move. Drums go silent; with Keys soloed, click and guide still play (PB-82). Double-click a fader for 0.0 dB. **Ctrl+Z** undoes the last change with a toast "Undid: …". |
| 6 | **Settings** (top right, or click the AUDIO status): choose **ASIO** and your interface, Apply. Open Settings again, set **Cue** to **3–4**, Apply. | The status reads e.g. "Focusrite USB ASIO · 4 out · 48 kHz". Click and guide now come out of outputs 3–4; the band stays on 1–2. The lane headers show "Cue 3–4". |
| 7 | Click the ruler or a lane while playing; stop; use ← and →. | The playhead jumps with no click or pop. When stopped, ← and → move one bar. Stop returns to the start. F fits the song; + and − zoom; Ctrl+wheel zooms at the pointer. |
| 8 | While a song plays, end **RECompose** in Task Manager (the app, not "recompose-engine"). | **The music keeps playing.** Start RECompose again: it reconnects, the song is loaded, and a toast says it kept playing. |
| 9 | Quit normally (close the window) and start again. | The engine stops on quit. On restart the library, the last song, its mix and the audio settings are back. |
| 10 | Task Manager while stopped with a song loaded, then while playing 16 stems. | RECompose plus recompose-engine stay around 3 % CPU when stopped and well under 20 % playing; the AUDIO status shows "· N dropouts" if the interface ever underran. |

## 3. Known gaps in this build

- Sections, the NOW/NEXT labels, arrangements, services, pads and the Perform view come in milestones 2–5.
- The mix is one "Default" preset per song; presets arrive with services.
- The readouts treat the whole file as the plan, with BPM and meter from the file names or the SONG tab (no click analysis yet, milestone 2).
- Light and High contrast themes exist in the tokens but there is no switch yet; the window is dark.
- Bar labels count from the first downbeat, which is 0 s until you set it in the SONG tab.
- No installer, signing or auto-update (milestone 10).

## 4. If something goes wrong

- The app writes a log to `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log` (engine start, device, imports, loads, errors). Please send it with a description.
- Library, songs and settings live in `%APPDATA%\RECompose\`. Deleting that folder resets the app (your audio files are never touched).
- If audio doesn't start: open Settings and pick the device again; try a larger buffer (512) if you hear crackles.
- A stuck engine (rare): end `recompose-engine` in Task Manager; the app starts a new one.
