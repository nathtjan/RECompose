# Milestone 8: Automation and log

**Goal:** the plan drives the room. Lights follow the sections over MIDI, cues fire where they were placed, the guide calls the section that will really play next, count-ins are spoken, and after the service there is a log and a report of what happened.

**Status:** done (29 Oct 2026). How to try it: [M8 test guide](../testing/M8-test-guide.md).

**Starting point:** milestone 7. The engine plays plans, a generated click track and one-off clips (`playClip`, protocol 12); TTS clips are cached in `cache/tts` (`SpeechClips`). The guide's words and calls are stored with each section's written call (`Section.CallId`). MIDI comes in (protocol 9) but nothing goes out. The log is a daily text file.

## Decisions for this milestone

1. **Automation is part of the plan.** `RECompose.Core.Automation` turns a plan and its song into timed events in plan seconds: MIDI messages (section notes AUTO-21, cue actions AUTO-5) and spoken calls (spoken guide AUTO-31, guide-call replacements AUTO-30, spoken counts PB-71, Speak cues), plus the stretches where the guide track is silenced (a `mute` of the guide in split plan segments, so the engine needs nothing new for it). It is pure and unit-tested; the app sends it with every plan.
2. **The engine fires them on the audio clock.** `setPlan` carries `midi` and `calls` (protocol 13). Calls are rendered by the disk thread into a generated speech track (`generated: "speech"`, the "Guide (spoken)" lane, on the Cue output) the way the click is, so seeks, swaps and mutes work unchanged. MIDI messages are picked up by the audio thread as the playhead passes them during playback (never when seeking over them), copied into a lock-free ring and sent by a MIDI thread; each batch sent comes back as a `midiOut` event, which the log uses. Panic sends All Notes Off and All Sound Off on every channel used.
3. **MIDI out is one port.** Windows: a chosen output (for example loopMIDI); macOS: a virtual source "RECompose" by default (SET-30). Settings › MIDI out has the port, channel, velocity, note length, middle C naming, the note map and a test note.
4. **Cues live on the song.** A cue is a source time, a name and an action (note, CC, program change, or speak); C adds one at the playhead with the first free note from C4 (AUTO-2). The CUES tab lists them; the timeline's Cues lane shows ◆ at every as-heard occurrence.
5. **The log is the app's.** Every log line becomes an entry with a category; the LOG tab shows them newest first with filter chips, search, Clear view and Export. Lines also go to `logs/service-YYYY-MM-DD.log`. The service report (AUTO-60) is Markdown built in Core from what the app recorded while performing.

## Definition of done: the demo

1. Settings › MIDI out: choose a port, Send test note: the note arrives in the lighting app (or a MIDI monitor).
2. Turn on section notes for a song and play it: every section start sends its type's note; the log reads `Section Chorus 2: MIDI note E3`.
3. Press C during playback: `Cue 1` appears in the Cues lane and the CUES tab with `MIDI note C4`; on the next pass the log reads `Cue 1: MIDI note C4`.
4. Turn on the spoken guide: one bar before each section the Cue output says its name. A voice jump to Verse 2 replaces the guide's own "Chorus" call with "Verse two"; the log reads `Guide: "Verse 2" replaces the written call`.
5. A generated 2-bar count-in counts "one, two, one two three four".
6. The LOG tab filters by category; leaving Perform after a song writes a report to `reports/`.

## Work packages

| # | Package | Exit |
|---|---|---|
| WP1 | Core: cues, note map, automation events, guide-call replacement, spoken guide and counts, log categories, service report | Unit tests per rule |
| WP2 | Engine: MIDI out port, `sendMidi`, plan `midi` and `calls`, the speech track, `midiOut`, panic CCs (protocol 13) | C++ tests; C# round trip |
| WP3 | App: settings, automation card, C key, CUES tab, Cues lane, plan sending with clips, log lines | Presentation tests |
| WP4 | LOG tab, service log file, service report | Presentation tests |
| WP5 | Docs, test guide | |

## As built

- MIDI out and Replace guide calls are in **Settings › Controls and playback**, not pages of their own; velocity and note length use the defaults (127, 100 ms) and the note map is the default one (no editor yet).
- The automatic section cues card is under the SECTIONS tab; the CUES and LOG tabs sit next to it in Prepare.
- Cue markers (◆ and the name) are drawn at the top of the sections lane rather than in a lane of their own.
- Spoken calls the platform voice has not rendered yet are left out of the plan sent first; once rendered the plan is sent again, taking effect at the end of the current item.
- A guide call inside a segment that fades in or out is not silenced (splitting the segment would restart the fade).
- The report is written when leaving Perform after a song played (and with Service ⋯ › Export report) to `reports/` in the data folder; it lists live changes, ignored voice commands and errors from the log.

## Known gaps

- No editor for the section-type note map, velocity or note length (SET-30); the defaults are used.
- The guide-call replacement uses the guide's own recording of the incoming section's call when it has one, and the platform voice otherwise; the as-heard Guide lane does not show silenced or replaced calls (AUTO-30 step 6).
- A song started by a segue or crossfade is not added to the report as a song of its own.
- `LiveControlTests.AT23` and `AT43` sometimes fail when the whole solution's tests run at once and pass alone (timing under load).

## Out

ProPresenter (later); OSC; Speak cues on an output of their own (they use the speech track on the Cue output).
