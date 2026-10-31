# Changelog

## 0.2.0

### New

- Render audio: Song › Render audio writes the loaded song, as it plays now, to WAV or FLAC. Pick the band mix, the Main or Cue output as routed, every output in one file, any bus, or single tracks, and choose whether to follow the mixer's mutes, solos, track volumes, master volume and the arrangement's own mutes. A second engine does the work, so a running service is never touched.
- Output buses: each track group plays on its own bus, and Settings › Audio maps every bus to Main, Cue or device channels once for the whole rig. Custom buses can be added.
- Export a service with its audio as one `.recservice` file and import it on another computer. Songs are matched by their audio; when a song differs you choose to keep this computer's copy, use the file's, or keep both. Double-clicking a `.recservice` file opens RECompose and imports it.
- The assistant works with OpenAI as well as Anthropic, each with its own key. The key shows masked in Settings and can be deleted.
- A bottom bar shows the latest log line and live CPU, latency, dropout and disk statistics, with a Reset.
- The voice indicator shows yellow Ready until push to talk is held, then green Listening. The talkback mic can be on a different device than the music.
- A searchable service picker with All services and New service; a single Settings window with tabs, an updates switch and an About dialog.
- Tracks can be added, deleted, swapped, moved and offset in time, and a track's name, role, group and output can be edited from its lane header.
- Pad sets can be renamed and deleted; Play and Stop wait while a pad fades.
- The main window remembers its place, size and maximised state.
- The Sections tab folds the automatic cue options into an Auto-cues button.
- The app and the engine show the released version.

### Fixed

- Settings › Audio opens quickly: input devices are no longer queried one by one.
- Applying audio settings no longer crashes when a bus choice is cleared.
- Clicking the top-right corner of the screen closes the maximised window.
- Detect from guide looks disabled when there is no guide, and the CALL column says why a call can't play.
- A long audio device name no longer pushes the voice indicator out.
- A taskbar that hides itself can come up over the maximised window.
- Clicks pass through tooltips to the button underneath.
- A track swap right after loading a song no longer fails.
- Pad fades last the time set, at the device's sample rate.
- The deprecated Avalonia `Watermark` warning is gone.

### Other

- The release notes are taken from this file.
- The engine protocol is now version 17 (the new `bounce` request).
