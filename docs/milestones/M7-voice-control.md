# Milestone 7: Voice control

**Goal:** the MD talks into a talkback mic and RECompose acts on band directions ("let's go to the bridge", "stay here", "never mind, keep going") as it acts on a key or a pedal, without being fooled by the guide track, and confirms in the MD's ear.

**Status:** done (29 Oct 2026). How to try it: [M7 test guide](../testing/M7-test-guide.md).

**Starting point:** milestone 6. Live actions are plan rewrites with a pending action and a NEXT card that names its source (`NEXT · VOICE` exists, nothing sends it). whisper.cpp is linked into the engine for offline guide transcription (`ggml-base.en`, M2). The engine opens output channels only.

## Decisions for this milestone

1. **Understanding is pure Core.** `VoiceText` normalises (VC-12), `VoiceGrammar` turns a transcript into commands left to right (VC-20, fillers, timing phrases VC-35), `VoiceResolver` resolves a section reference against the song and the remaining plan (VC-30) and makes the live action, `VoiceGate` drops commands the guide spoke itself (VC-40), and `VoiceSafeguards` applies confidence and the 2 s de-duplication (VC-45). The worked example (VC-70) is a unit test.
2. **The engine captures, a worker recognises.** The engine opens the voice input channel on the device it plays through (one driver, one process: ASIO allows nothing else), runs a cheap energy VAD on its own thread fed by a lock-free ring from the audio callback, and sends each utterance (16 kHz mono, at most 6 s) as an `utterance` event (protocol 12). Recognition never runs in the playback process (REL-50): the app keeps a `recompose-engine listen <model>` worker process alive, writes utterances to it and reads `{text, confidence}` lines back. The worker biases whisper with the loaded song's section names and the command words as its prompt (VC-11). `injectVoice` feeds audio to the VAD for tests.
3. **Voice acts like a key.** A resolved command becomes the same `LiveAction` with `ActionSource.Voice`; the NEXT card says `NEXT · VOICE`, a caption under it shows what was heard and what it did (VC-55), the log has the VC-60 lines. Voice is active while playing, paused or holding (VC-2). Confirm mode (VC-46) proposes for 4 s; `yes`, GO or a pedal confirms. Push-to-talk is a control binding (hold to listen).
4. **Confirmations are clips.** MD confirmations (VC-50) are short TTS phrases rendered once per phrase into `cache/tts` (Windows: System.Speech; macOS: `say`) and played by the engine on the MD confirm output with `playClip` (protocol 12); they are also the gate's own speech. Spoken counts (PB-71) use the same clips in M8.

## Definition of done: the demo

1. **Settings › Voice**: choose the input channel; the test panel's meter moves; say "let's go to the bridge": the panel shows the transcript and `would have done: Bridge 1 next`.
2. Play the Champion fixture; during the first chorus say "let's go to verse": NEXT shows `NEXT · VOICE` / `VERSE 2` with `heard "let s go to verse"`; the MD hears "Verse two next"; at the boundary Verse 2 replaces the next item (VC-70).
3. "Never mind, let's stay in this section": the pending jump goes and the chorus vamps; "move on" ends it.
4. With the guide calling "Chorus" into the mic, nothing happens and the log reads `Voice ignored: Chorus (the guide track was talking)`; "bridge" over the same moment is accepted.
5. Confirm mode: "skip the bridge" shows `VOICE?` for 4 s; "yes" confirms.

## Work packages

| # | Package | Exit |
|---|---|---|
| WP1 | Core: normalise, grammar, timing, resolver, gate, safeguards | Unit tests per intent, VC-30 rules, VC-36, VC-40, VC-45, VC-70 |
| WP2 | Engine: input channel, ring, VAD thread, `utterance`, `injectVoice`, voice level; `listen` worker | C++ VAD test; worker round trip on a TTS clip |
| WP3 | App: voice service, settings and test panel, VOICE indicator, captions, confirm mode, push-to-talk, log | Presentation tests with injected transcripts |
| WP4 | TTS clips, `playClip`, MD confirm output, gate windows from guide words and clips | Tests where the platform allows |
| WP5 | Docs, test guide | |

## As built

- Voice settings are in **Settings › Audio** (the mic is a channel of the audio device): VOICE input, push to talk, ask before acting, the guide gate, bare section names, the test panel and the MD CONFIRM output. Voice stays off until an input is chosen; the spec's default is on, but opening a mic nobody chose is worse.
- The engine stamps nothing: the app places an utterance in plan time from the playhead when it arrives, which is tens of milliseconds late; the gate allows 300 ms.
- Confirm mode shows `VOICE? … · say "yes" or press GO` under the NEXT card rather than as the card's tag.
- STOP uses the transport's stop fade (50 ms), not the 1 s fade of VC-20.
- MD confirmations are spoken for every live action (keys and pedals too, LIVE-54) and for cancel; the optional "Didn't catch that" is not.
- The resampler to 16 kHz is linear interpolation without an anti-alias filter.

## Known gaps

- No microphone permission prompt (VC-3): Windows asks on first use of the input; on macOS the engine needs the entitlement that packaging (M10) adds.
- The VAD is energy-based; a loud stage through the talkback mic needs push to talk or a gated mic.
- Echo cancellation (VC-14) and the latency measurement of VC-13 are not done.
- `LiveControlTests.AT23` sometimes fails when the whole solution's tests run at once and passes alone; it predates this milestone.

## Out

Echo cancellation with the Cue output as reference (VC-14), other languages, spoken counts (M8 with the other generated clips).
