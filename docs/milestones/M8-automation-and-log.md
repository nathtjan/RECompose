# Milestone 8: Automation and log

**Goal:** the plan drives the room. Lights follow the sections over MIDI, cues fire where they were placed, the guide calls the section that will really play next, count-ins are spoken, and after the service there is a log and a report of what happened.

**Status:** in progress (29 Oct 2026).

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

## Out

ProPresenter (later); OSC; Speak cues on an output of their own (they use the speech track on the Cue output).
