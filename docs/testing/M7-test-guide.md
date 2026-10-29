# Testing milestone 7 on your PC

This build is about calling the song by voice. The MD talks into a talkback mic, RECompose understands band directions such as "let's go to the bridge", "stay here" and "never mind, keep going", and acts on them exactly as it does on a key or a pedal. It ignores the guide track calling the same word, can ask before acting, and confirms in the MD's ear. Everything from milestones 1 to 6 still works ([M1](M1-test-guide.md), [M2](M2-test-guide.md), [M3](M3-test-guide.md), [M4](M4-test-guide.md), [M5](M5-test-guide.md), [M6](M6-test-guide.md)).

## 1. Get the build

GitHub → **Actions** → the latest green **CI** run on `main` → **Artifacts** → `RECompose-win-x64`. Unzip it and run `RECompose.exe`, keeping `recompose-engine.exe` and the `models` folder next to it. This build changes the engine protocol (version 12), so use the new `recompose-engine.exe` with the new app; an old engine left running is replaced when the app asks it to shut down, or end it in Task Manager.

You need a microphone on an input of your audio interface (a talkback mic, or any mic for trying it) and headphones on a spare output for the MD confirmations. The speech model is the one Detect from guide uses; if it is not downloaded yet, the first utterance downloads it (about 140 MB).

## 2. Try the demo

| # | Do this | You should see |
|---|---|---|
| 1 | **Settings › Audio**: choose the mic's input under **VOICE** and your headphones under **MD CONFIRM**, then **Apply**. Open **Settings › Audio** again and say "let's go to the bridge". | The status row shows **VOICE** with a green light and a level that moves. In the test panel the meter moves; after you stop talking it reads `heard "let s go to the bridge"` and `would have done: Bridge next` (or `didn't catch a section` if the loaded song has no bridge). Nothing changes in playback while the window is open. |
| 2 | Load the Champion fixture with the Demo variant (Intro, Chorus, Chorus) and play it. During the first chorus say "let's go to verse". | The NEXT card reads `NEXT · VOICE · PENDING` / `VERSE 2` with `heard "let s go to verse"` in its detail, and the caption under it reads `heard "let s go to verse" → Verse 2 next`. In the headphones you hear "Verse two next". At the end of the chorus Verse 2 plays instead of the second chorus pass, then the song ends. |
| 3 | Play again; in the chorus say "go to the bridge", then "never mind, let's stay in this section". Then say "move on". | The pending jump goes ("Cancelled"), and the chorus is set to repeat ("Vamping"). "Move on" ends the vamp after this pass ("Moving on"). |
| 4 | Play a song with a spoken guide on the Cue output where the mic can hear it. When the guide calls "Chorus", say "chorus" at the same moment; then, at another call, say "bridge" over it. | The first is ignored: the caption reads `ignored — guide talking` and the log has `Voice ignored: Chorus (the guide track was talking)`. The second is accepted. |
| 5 | Tick **Ask before acting**, apply, play, and say "skip the bridge" when the bridge is next (or "skip next"). Then say "yes". Try again and press GO or a pedal instead, and once more saying nothing. | `VOICE? … · say "yes" or press GO` appears under the NEXT card for 4 seconds. "yes", GO or a pedal makes it pending; saying nothing lets it expire, and the log reads `Voice: … not confirmed`. |
| 6 | Tick **Push to talk**, bind **Push to talk** to a pedal or key in **Settings › Controls**, and talk with and without holding it. | The VOICE indicator reads `PTT` until you hold it; only what you say while holding it is acted on. |
| 7 | Say a command while the song is stopped, then mumble something quietly. | Stopped: nothing happens, the log reads `Voice ignored: … (not playing)`. A low-confidence guess is ignored with `low confidence`. Saying the same command twice within 2 seconds acts once. |

## 3. Please tell me

- How long it takes from the end of a phrase to the NEXT card changing (the target is under 0.7 s), on your computer.
- Which phrases it gets wrong, and what it heard instead (the caption shows it).
- Whether the confirmations are loud enough at −12 dB in your in-ears, and whether the voice is clear.

## 4. Known gaps in this build

- Voice settings are under **Settings › Audio**, not a page of their own. Voice is off until you choose an input.
- There is no echo cancellation: if the band or the click is loud in the mic, use push to talk or a mic that is only open while you talk.
- "Stop the tracks" stops with the normal short fade, not a one-second one.
- macOS: the microphone permission comes with the packaged app (milestone 10).

## 5. If something goes wrong

The log is in `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log`: lines starting "Voice" show every command heard, what it did or why it was ignored, and "Voice: Input not opened" or "Recogniser" when the mic or the speech model could not start. The spoken confirmations are in `%APPDATA%\RECompose\cache\tts` and can be deleted at any time. Send me the log with the time it happened.
