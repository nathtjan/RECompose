# Testing milestone 8 on your PC

This build makes the plan drive the room: lights follow the sections over MIDI, cues fire where you placed them, the guide calls the section that really plays next, count-ins are spoken, and there is a log and a report of the service. Everything from milestones 1 to 7 still works ([M1](M1-test-guide.md), [M2](M2-test-guide.md), [M3](M3-test-guide.md), [M4](M4-test-guide.md), [M5](M5-test-guide.md), [M6](M6-test-guide.md), [M7](M7-test-guide.md)).

## 1. Get the build

GitHub → **Actions** → the latest green **CI** run on `main` → **Artifacts** → `RECompose-win-x64`. Unzip it and run `RECompose.exe`, keeping `recompose-engine.exe` and the `models` folder next to it. This build changes the engine protocol (version 13), so use the new `recompose-engine.exe` with the new app; an old engine left running is replaced when the app asks it to shut down, or end it in Task Manager.

For MIDI out on Windows install [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) and create a port, then watch it with a MIDI monitor (for example MIDI-OX) or your lighting software (QLC+).

## 2. Try the demo

| # | Do this | You should see |
|---|---|---|
| 1 | **Settings › Controls and playback**: choose the loopMIDI port under **MIDI out**, press **Send test note**. | The monitor shows note 60 on channel 1, velocity 127, then its note off. |
| 2 | Load the Champion fixture. In **SECTIONS**, tick **Send a MIDI note per section type** and play. | At each section start the monitor shows its note (Intro C3 = 60, Verse D3 = 62, Chorus E3 = 64…). **LOG** shows `Section Chorus 1: MIDI note E3`. Start in the middle of a section: its note is sent at once. |
| 3 | During playback press **C**. Open **CUES**. | A row `Cue 1 · chart bar … · MIDI note C4`, and ◆ Cue 1 at the top of the timeline. The next time the playhead passes it the monitor shows note 72 and the log reads `Cue 1: MIDI note C4`. Change its action to **Speak** and type "Big chorus": it is spoken on the Cue output there instead. |
| 4 | Change a cue's note to E3 with section notes on. | A warning under the row: `Same note as Chorus sections`. |
| 5 | Tick **Spoken guide** and play with the Cue output in your headphones. | One bar before each section you hear its name ("Chorus one"), and "Ending" one bar before the tail. |
| 6 | Play a song with a spoken guide (Detect from guide done). During a verse whose guide calls "Chorus", make a live jump to the bridge. | The guide's "Chorus" is silenced and "Bridge" plays at the same moment, in the guide's own voice if it ever calls "Bridge", otherwise the computer's. The log reads `Guide: "Bridge" replaces the written call`. Untick **Replace guide calls** in settings to hear the written call instead. |
| 7 | Give a service entry a generated 2-bar count-in and play it. | The click counts with a voice: "one, two, one, two, three, four". |
| 8 | Open **LOG** and try the chips, the search box, **Clear view** and **Export…** (.txt or .csv). | Newest lines on top; the chips filter by kind. |
| 9 | Go to **Perform**, play a song of a service, then go back to Prepare. | A toast: `Service report saved: …`. The report is in `%APPDATA%\RECompose\reports` and lists the planned order, what was played, live changes and ignored voice commands. **Service ⋯ › Export report** writes it at any time. |
| 10 | Press **PANIC** while notes are sounding in the lighting app. | All Notes Off and All Sound Off arrive on the channels used. |

## 3. Please tell me

- Whether the section notes arrive on time in your lighting software (they are sent on the audio clock, a millisecond or two after the downbeat).
- Whether the replaced guide calls sound natural next to the guide.
- What the report should say that it does not.

## 4. Known gaps in this build

- No editor yet for the note map, velocity or note length: the defaults are used.
- The timeline does not show silenced or replaced guide calls.
- macOS: the virtual "RECompose" MIDI source appears with the packaged app (milestone 10).

## 5. If something goes wrong

The log is in `%APPDATA%\RECompose\logs`: `service-YYYY-MM-DD.log` has the lines the LOG tab shows, `app-YYYY-MM-DD.log` the details of errors. Spoken calls are cached in `%APPDATA%\RECompose\cache\tts` and can be deleted at any time. Send me the log with the time it happened.
