# Changelog

## 0.3.0

### New

- MIDI controls go beyond presses. A button can jump to the next Verse, Chorus or any section type, or to a tile of the Perform strip, optionally only while Perform is open.
- Pads from a keyboard: learn the lowest and highest note of a range and each key starts or stops the pad in its key, or sounds it while held. Single pad keys and Pads play or stop can be bound too.
- MIDI faders: a controller sets the master, the pads or every track of a group, keeping the group's balance. Soft takeover keeps a fader from jumping, a whole move is one undo, and in Service lock a fader can't take a track below −40 dB.
- Control profiles: each profile keeps its own bindings, and a button can switch to the next profile or to a named one. Panic and the profile buttons work in every profile.
- Controller feedback: choose the controller's output in Settings › MIDI and its buttons light for the section, tile, pad and profile in force, and motorised faders follow the mix.
- When a new binding shares an input with another one, Settings says which one acts. Less common rows are under More.
- Importing a folder of pads always opens a review: each key's file, the keys that are missing, and the files that name the same key, with Play for each key. A folder the app isn't sure about can be imported either way, and a folder of pad set folders is reviewed set by set.
- Pad names are read more widely: lower case, "F sharp", "B-flat", "Gb Major", "Ebm". A pad folder with a few keys missing imports as a pad set instead of a song.
- Edit a pad set from Library › Pads (double-click, or right-click › Edit): replace or clear a key's file, choose the loop points and crossfade with a preview of the loop seam, and give the set its own volume.
- A missing key can be made from the nearest key by transposing it, when you choose to, in the review or the editor. Keys without a file are off in the sidebar.
- File › Import pad set imports a folder as a pad set whatever its names say. A song can be turned into a pad set, and a pad set imported as a song, each in one undo.
- Library › Pads gets a search field from six pad sets.

## 0.2.1

### New

- An error the app did not expect opens a window with its details, which can be copied for a bug report, and the app keeps running. An error that closes the app is shown the next time it starts. While the band plays, the window opens without taking the keys.
- Dragging a song into the service, or a service row to a new place, shows the row under the pointer and a line where it will land.

### Fixed

- Dragging a song into the service, or a service row to a new place, no longer closes the app on macOS 26.

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
