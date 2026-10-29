# Testing milestone 6 on your PC

This build is about Sunday morning going wrong. Before the service you run a **service check** and the app **renders** every key and tempo change so nothing is pitch-shifted live. During the service a lost audio interface, a crash or a lost pedal costs as little as possible: the song keeps time, a fallback device takes over, the app offers to resume at the right bar, and PANIC stops everything without unloading the song. Everything from milestones 1 to 5 still works ([M1](M1-test-guide.md), [M2](M2-test-guide.md), [M3](M3-test-guide.md), [M4](M4-test-guide.md), [M5](M5-test-guide.md)).

## 1. Get the build

GitHub → **Actions** → the latest green **CI** run on `main` → **Artifacts** → `RECompose-win-x64`. Unzip it and run `RECompose.exe`, keeping `recompose-engine.exe` and the `models` folder next to it. This build changes the engine protocol (version 11), so use the new `recompose-engine.exe` with the new app; an old engine left running is replaced when the app asks it to shut down, or end it in Task Manager.

Use the service of milestone 5. For step 3 you need your audio interface and a second output (the computer's speakers or headphones work); in **Settings › Audio** choose the interface as the device and the speakers as **FALLBACK**. For step 6, a MIDI foot controller plugged in before you start the app.

## 2. Try the demo

| # | Do this | You should see |
|---|---|---|
| 1 | Click the check badge in the top bar (`Not checked` the first time). | The service check lists twelve items with ✓, ⚠, ✗ or ℹ and a Fix button where there is one, and a verdict such as `Ready with warnings`. Rename one stem file in Explorer and press **Run check**: item 1 turns ✗ with **Relink…**. Put the name back and run it again: ✓. Edit the service afterwards: the badge adds `changed since check`. |
| 2 | Change entry 3's key with **KEY +**. Watch the top bar. | Within a few seconds the activity line reads `Rendering {song} · {stem} · 42% · 3 more`. Load the entry while it renders: the header tag reads `Live key — rendering 42%`. When the renders finish with the entry stopped, it loads again by itself and the tag is gone: the stems play rendered, not shifted live. The check's item 4 counts the renders done. |
| 3 | Play entry 2 and pull the interface's USB cable in the chorus. Plug it back in. Then choose **FALLBACK: None** and do it again. | Within a second the music carries on through the speakers at the same place, and a red banner reads `{interface} was lost. Playing on {speakers}.` When the interface is back the banner offers **Switch back**; with **Switch back to the main device by itself** on, it also switches at the next section boundary. With no fallback the song keeps time silently (`No audio device · keeping time`); plug back within a minute and the sound returns at the right bar. After a minute it pauses and says so. |
| 4 | Play a song, add a vamp in the chorus, then end `RECompose.exe` in Task Manager. Start it again. | The dialog reads `RECompose closed unexpectedly during {service} while playing {song} — Chorus, bar 5` with **Resume at Chorus**, **Resume at the next section**, **Open the service** and **Don't resume**. Resume: the song loads, a bar of click counts in and the chorus plays, with the vamp from before still in the plan. |
| 5 | Press **PANIC** (or **Ctrl+Shift+Space**) while playing. | Everything stops within a tenth of a second, the pad and any transition click end, and the Perform view says `Stopped by PANIC · Resume at {section}`. The song is still loaded; **Resume** plays on from that section. |
| 6 | Go to Perform and leave the computer alone longer than its sleep time. Unplug the MIDI controller, then plug it back in. | The screen stays on and the computer does not sleep; back in Prepare it may sleep again. On battery below 20 % a banner says so once per service. The status row's **MIDI** light turns red with `{controller} lost` while it is out, playback goes on, and within a couple of seconds of plugging it back it turns green with a toast. |
| 7 | Remove an entry from the service, then open the service menu (⋯) → **Restore earlier version…**. Then **Export service…**, delete the service, and **Import service…** the file. | The list shows the kept versions with the time, name and number of songs; restoring one brings the entries back (Ctrl+Z undoes it). Export writes a `.recompose-service` file with no audio. Import adds the service back with its keys and transitions and lists any stem that is not on this computer. |
| 8 | Open **Settings › Audio**. | **RENDER CACHE** sets how much the renders may take (default 30 GB) and shows how much they take now; **Clean cache** frees the renders the next seven days do not need. |

## 3. Please tell me

- Whether the switch to the fallback is ever audible as more than a short dropout, and how long it takes on your interface.
- Whether rendered keys sound as good as or better than the live key of milestone 4, and how long a whole service takes to render.
- Whether anything in the check is wrong for your setup (an ⚠ that should be ✓ or the other way round).

## 4. Known gaps in this build

- A render that finishes while the song is playing is used the next time the entry loads, not mid-song.
- The status row has AUDIO, MIDI and PANIC; render progress is in the top bar's activity line, and there is no CPU meter yet.
- PANIC sends no MIDI All Notes Off (MIDI out comes with milestone 8). The check's items 7, 9 and 10 (MIDI out, voice level, performance probe) report ℹ until those exist.
- The fallback and switch-back paths are tested by hand; the automated tests cover the silent clock, renders, the journal, versions, export and import, and a 200-jump soak in offline renders. The 3-hour soak is a hand test.
- Version history keeps one version per five minutes of editing, 20 per service or song; there is no restore for songs in the UI yet.

## 5. If something goes wrong

The log is in `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log`: lines starting "Audio" show device loss and xruns, "Render" each render, "Recovery" the journal and resume, "Check" each service check; the render cache is in `%APPDATA%\RECompose\cache\render` and can be deleted at any time (renders are made again). Send me the log with the time it happened.
