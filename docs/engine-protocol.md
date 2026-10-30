# Engine protocol

The app (C#) and the audio engine (`recompose-engine`, C++/JUCE) are separate processes (ADR 0001). This protocol is the only interface between them. Change it in one commit on both sides: `REC_PROTOCOL_VERSION` in `engine/src/protocol.h` and `EngineProtocol.Version` in `src/RECompose.Engine/EngineProtocol.cs` move together. The C# side is `EngineClient`; the C++ side is `Engine::handle`.

## Transport

- A Unix domain socket (`AF_UNIX`, stream) on all three platforms (Windows 10 1803 and later support it natively). One engine per user, at:
  - Windows: `%LOCALAPPDATA%\RECompose\run\engine.sock`
  - macOS: `~/Library/Application Support/RECompose/run/engine.sock`
  - Linux (development only): `~/.local/share/RECompose/run/engine.sock`
- The engine **listens**; the app connects. Socket paths are limited to about 100 bytes.
- **Frames:** a 4-byte little-endian payload length, then the payload: one UTF-8 JSON object. Frames over 16 MiB are a protocol error and close the connection.

## Lifetime (REL-34)

| Situation | What happens |
|---|---|
| App starts, no engine running | The app starts `recompose-engine --endpoint <path> --idle-exit-seconds 10` (no console window, no redirected output) and connects as soon as the socket accepts. |
| App starts, engine already running | The app connects and says hello: it **reattaches**. `getState` tells it which song is loaded and where the transport is (for example after an app crash while a song kept playing). |
| A second connection arrives | It replaces the current one. |
| App disconnects or crashes | The engine keeps running and keeps playing. |
| No app connected for `idle-exit-seconds` and nothing playing | The engine exits. This stops orphaned engines. |
| App quits normally | It sends `shutdown`; the engine exits. |
| Engine crashes | The app sees the connection close, starts a new engine, opens the device and reloads the song at the last position, stopped. |
| Engine speaks another protocol version | `hello` fails with `protocolMismatch`; the app sends `shutdown` (which every version accepts) and starts its own engine. |

The engine ignores `SIGPIPE`, so a vanished app can never kill it.

## Messages

Every message has a `type`. Requests from the app carry an integer `id`; the reply carries the same value in `re`. Messages from the engine without `re` are **events**.

Errors: `{"type":"error","re":<id>,"code":"…","message":"…"}`. Codes: `badMessage`, `helloRequired`, `protocolMismatch`, `unknownType`, `deviceError`, `noSong`, `unknownTrack`.

Frames are at the **engine rate**: the open device's sample rate (48 000 when no device is open). Stems at other rates are resampled (PB-2). When the device rate changes, the engine reloads the song at the new rate and sends `songReloaded`.

### Session (every version)

| Request | Reply |
|---|---|
| `hello {protocol, client}` | `hello {protocol, engine, juce, pid}`. Must come first. A mismatch replies `protocolMismatch` and closes the connection. |
| `ping` | `pong` |
| `shutdown` | `ok`, then the engine exits. Accepted before `hello`. |

`hello` and `shutdown` keep this shape in every future version.

### Protocol 2 to 14 (milestones 1 to 8)

Protocol 3 adds `setPlan` and the `planFrames` snapshot field; everything else is as in 2. Protocol 4 adds the engine's click: a generated track in `loadSong`, the song grid in `setPlan` and a `click` flag per segment. Protocol 5 adds live key and tempo: `transposes` per track, `key` per segment and `tempo` per plan. Protocol 6 adds pads: `loadPadSet`, `pad`, `songKey` and `pad` per segment, and `pad` in the snapshot. Protocol 7 adds the second song slot and transitions: `slot` on `loadSong`, `unloadSong` and `setPlan`, `setTransition`, `startNext`, `promoteNext`, the `songChanged` and `songEnded` events, and `next` and `transition` in the snapshot. Protocol 8 adds live plan changes and holds: `swapAt` and `holds` on `setPlan`, the `go` and `panic` requests, and `holding` and `holdSec` in the snapshot. Protocol 9 adds MIDI input: `setMidiInputs`, the `midiInputs` list in the `listDevices` reply and the `midiIn` event. Protocol 10 adds device failover: `setFallbackDevice`, `switchToMain`, `injectDeviceLoss`, the `route`, `lostSec` and `mainBack` snapshot fields, the `deviceLost`, `deviceRestored`, `mainDeviceBack`, `silentClockStopped`, `xrun` and `midiInputsChanged` events, and `missing` in the `setMidiInputs` reply. Protocol 11 adds rendered keys and tempos: `renders` per track in `loadSong` and its reply. Protocol 12 adds the talkback mic and spoken clips: `setVoiceInput`, `injectVoice`, `playClip`, the `utterance` and `voiceError` events, `voice` in the snapshot, and `inputs` per driver type and `inputChannels` per device in `listDevices`. Protocol 13 adds automation: `midi` and `calls` on `setPlan`, the `speech` generated track, `setMidiOutput`, `sendMidi`, `midiOutputs` in `listDevices`, the `midiOut` event, and MIDI All Notes Off and All Sound Off on `panic`. Protocol 14 adds `offsetSec` per track in `loadSong`: the file starts that many seconds after the song start (negative: its first seconds are skipped), the song is as long as its last track ends, and renders follow the stem.

| Request | Fields | Reply |
|---|---|---|
| `listDevices` | | `devices {types: [{name, devices: [names]}], current: device \| null}` |
| `setDevice` | `deviceType?`, `name?`, `sampleRate?`, `bufferSize?` (absent = current or default) | `device {device}` or `deviceError` |
| `loadSong` | `songId`, `tracks: [{id, file, output: {channel, width}, gainDb, mute, solo, soloSafe, generated?, transposes?, renders?: [{file, semitones, tempo}] (11), offsetSec? (14)}]`, `masterGainDb`, `startFrame` | `song {songId, sampleRate, lengthFrames, masterGainDb, tracks: [{id, channels, sourceRate, sourceFrames, missing, problem, gainDb, mute, solo, output, renders (11)}]}` |
| `unloadSong` | | `ok` |
| `play`, `pause`, `stop` | | `ok`. Fades of 10, 20 and 50 ms (PB-20…22); stop returns to frame 0. |
| `seek` | `frame` | `ok`. While playing: a 10 ms fade-out, then playback resumes exactly at `frame` once every track has data there (PB-26, PB-55). |
| `setTrack` | `trackId`, any of `gainDb`, `mute`, `solo`, `output`, `transposes` (5) | `ok`. Changes ramp over one 256-frame block; `transposes` applies from the audio not yet read ahead (up to 4 s later). |
| `setMaster` | `gainDb` | `ok` |
| `setMidiInputs` (9) | `names?: [..]`, `all?` | `midiInputs {open: [names], missing: [names]}` (`missing` 10). Listens to the named MIDI inputs (or all); each message arrives as a `midiIn` event. An empty call closes them. Inputs that go away are reopened when they return (10). |
| `setFallbackDevice` (10) | `deviceType?`, `name?` (absent name = none) | `fallback {name, present}`. The device that takes over when the main one is lost (REL-20). |
| `switchToMain` (10) | | `device {device}` or `deviceError`. From the fallback back to the main device, with the clock running (REL-23). |
| `injectDeviceLoss` (10) | `device?` (`main` or `fallback`), `present` (default false), `silentLimitSec?` | `ok {route}`. Tests only: the engine sees that device as unplugged or back and takes the same paths as for a real one. With no device open at all, a loss starts the silent clock. |
| `setVoiceInput` (12) | `channel` (0-based, −1 = off), `device?` (another input device, where the driver allows it) | `voice {channel, device, on}` or `deviceError`. The device opens again with that input channel (VC-1). If the input will not open, the outputs open alone and `voiceError {message}` says why. |
| `playClip` (12) | `file`, `output: {channel, width}`, `gainDb` (default −12) | `clip {durationSec}`. Plays a short clip once (under 30 s, mixed to mono, resampled to the engine rate) on those channels, replacing one that sounds: the MD's spoken confirmations (VC-50). |
| `injectVoice` (12) | `samples` (16 kHz 16-bit PCM, base64) | `ok`. Tests only: the audio goes straight into the voice detector. |
| `setMidiOutput` (13) | `name?`, `virtual?` | `midiOutput {name, open}`. MIDI out to that port, or with `virtual` a new source of that name (macOS, Linux); no name closes it (SET-30). |
| `sendMidi` (13) | `messages: [{bytes: [status, data…], id?}]` | `ok`. Sends them now (a test note, a section note on Play) and reports them in `midiOut`. |
| `injectMidi` (9) | `kind`, `channel`, `number`, `value` | `ok`. Tests only: raises the `midiIn` event a device would. |
| `go` (8) | | `ok`. Releases the hold the playhead waits at (PB-52). |
| `panic` (8) | | `ok`. Stops the current and the next song with a 100 ms fade (at once during a hold), fades the pad out, cancels the transition and its click (UI-40); sends CC 123 and CC 120 on every MIDI channel used (13, REL-40). |
| `setPlan` (3–5) | `segments: [{start, end, src, fadeIn, fadeOut, mute: [trackIds], click?, key?}]`, `grid?: {origin, beat, beatsPerBar}` (4), `tempo?` (5), `position?`, `holds?: [{at, pad, padKey?, click}]` (8), `swapAt?` (8), `midi?: [{at, bytes, id?}]` (13), `calls?: [{at, file, from?, length?, gainDb?, normalize?}]` (13) | `plan {frames, swapped?}`. See below. |
| `loadPadSet` (6) | `id`, `samples: [{pitchClass, file}]` | `padSet {id, keys: [{pitchClass, missing, problem}]}`. Opens a pad set (SL-47); a sounding pad crossfades to the same key of the new set (SL-41). |
| `pad` (6) | any of `key` (pitch class 0–11, or `null` to fade out), `gainDb`, `fadeSec`, `follow`, `output: {channel, width}` | `ok`. Pads play independently of the song transport (SL-40). |
| `loadSong`, `unloadSong`, `setPlan` with `slot: "next"` (7) | as for the current song | The next song, loaded and planned while the current one plays (PB-11). It has its own player, its tracks and its plan. |
| `setTransition` (7) | `kind` (`none`, `stop`, `carryOver`, `segue`, `crossfade`, `pad`), `at` (plan seconds of the current plan), `gapSec`, `crossfadeSec`, `autoStartSec?`, `pad?: {key, fadeOut, click?: {bpm, beatsPerBar, channel, width}}` | `ok`. How the current song hands over to the next (SL-20). Resent whenever the plan changes. |
| `startNext` (7) | `retuneTo?` (pitch class) | `ok`. Play with a song cued: starts the next song, on the next click downbeat in a pad transition; `retuneTo` moves a sounding pad (Carry over). |
| `promoteNext` (7) | | `song`. Makes the cued next song the current one while the current one is stopped (Auto-advance, ⏭); also reported as `songChanged`. |
| `getState` | | `state {song, next, device, snapshot}` |
| `renderOffline` | `frames`, `channels`, `path?` | `rendered {frames, channels, peak: [..], rms: [..], snapshot}`. Tests only: pulls the audio through the graph on the message thread instead of the device, waiting for the disk, and optionally writes a 32-bit float WAV. |

**Plans (`setPlan`, PB-40).** The app turns the arrangement into contiguous timed segments in *plan seconds* (`RECompose.Core`, `PlanBuilder`); the engine only executes them. Segment `i` plays plan time `[start, end)`; `src` is the source second it reads from, or `null` for silence (count-ins, click and pad bars). A segment's `src` that continues where the previous one stopped plays on with no seam and no resampler reset. Where the source jumps, the incoming audio starts exactly on the boundary with a 2 ms fade-in while the outgoing audio keeps playing for 10 ms with a fade-out (PB-55), through a second resampler per track so the seam is as clean as the rest. `fadeIn` and `fadeOut` (seconds, inside the segment) are dB-linear from −60 dB to 0 and skip solo-safe tracks. `mute` lists track ids silenced in this segment, ramped over 10 ms. `position` is the plan second the playhead resumes at; absent keeps the current frame. Segments must be contiguous and start at 0, or the engine replies `badMessage`. The engine converts to frames once (rounding each start and source once) and keeps the seconds, so a device rate change derives the plan again. While playing, a new plan is applied like a seek (10 ms fade-out, then play on at `position`); with `swapAt` (protocol 8) the swap is gap-free, see below. A loaded song plays as written (one segment, source second 0) until a plan arrives; `loadSong` and `unloadSong` clear the plan. The song ends at the last segment: the transport stops and returns to the start. Every plan sent is kept until the song is unloaded (a few kilobytes each), so the audio thread never frees one.

**The click (protocol 4, PB-70).** A track with `generated: "click"` has no file: the engine synthesizes a click into it (accent on beat 1 at 1.6 kHz, other beats at 1 kHz, 30 ms, peaking at −6 dBFS), so it is routed, muted and metered like any track. The app loads one with the id `generated-click` on the Cue output. It sounds on every beat of the plan's `grid` that starts inside a segment with `click: true` (count-ins and click bars, SL-36, ARR-39). `grid.origin` is the plan second of bar 1 beat 1 (S0), `grid.beat` the beat length in seconds; beats before the origin count down to it, so a count-in ending on S0 lands on the grid. Each beat's frame is rounded once, as segment starts are. Without a grid there is no click.

**Key and tempo (protocol 5, PB-58, PB-60).** A segment's `key` (semitones, the entry key plus any lift) shifts the tracks loaded with `transposes: true`; `tempo` (0.5–1.5, default 1) plays the whole plan that much faster or slower without changing pitch, and applies to every track. A track at key 0 in a plan at tempo 1 bypasses the processing and plays bit for bit as before. Processing runs on the disk thread ahead of playback (Signalsmith Stretch), never on the audio thread; a shifted stream is primed so its first frame is the source frame it starts at, so shifted and unshifted tracks stay on one grid. Where the key changes on a contiguous boundary (a lift, or `transposes` switched), the old and the new key crossfade over 50 ms. The app's frames stay *plan frames* under a tempo: `seek.frame`, the `plan` reply, and the snapshot's `frame`, `planFrames` and `latency` are all in plan frames, and `position` is in plan seconds; the engine converts. A track's `renders` (protocol 11, PB-5, REL-10) are the stem rendered offline at a key and a tempo by the `render` tool: frame f of the file is source frame f × tempo. Where a segment's key (for a track that transposes; 0 otherwise) and the plan's tempo match a render, the disk thread reads the render straight at source ÷ tempo instead of running the stretcher; elsewhere it processes live as before (REL-14). Moving between a render and live processing, or between two renders (a lift), is the same 50 ms crossfade as a key change. A render whose file is missing, or whose rate or channel count differs from the stem's, is left out; the reply's `renders` is how many opened.

**Pads (protocol 6, SL-40…46, PB-72).** One file per pitch class. The disk thread reads only the keys that sound and loops each between the file's own loop points (WAV `smpl`) or, without them, 25–90 % of the file, with a 2 s equal-power crossfade (SL-46). A `key` fades that pad in over `fadeSec` (default 4 s) while any other fades out (SL-42); `null` fades out. With `follow`, a sounding pad takes each segment's `songKey` (the pitch class heard, key lifts included, SL-44). A segment with `pad` (pad bars, ARR-39) starts that pad and, when it ends, the pad fades out or goes to the song's key with `follow`. The volume defaults to −6 dB (SL-45) and the pads play on the output the app sends (Pad, default Main). The engine counts a sounding pad as busy, so it stays alive without an app (REL-34). The snapshot's `pad` is `{key, level}`.

**Transitions (protocol 7, SL-20, SL-30, AT-30, AT-31).** The engine keeps two songs, each with its own player, and a small state machine on the audio thread starts the next one on the exact sample, so the transition does not depend on the app's timing. `setTransition` describes it relative to the current plan: for a **segue** the next song starts at `at` plus `gapSec` while the current one's tail fades; for a **crossfade** it starts at `at` at full level and the current song fades out over `crossfadeSec` (the app sets `at` that long before the end); for a **pad** transition the pad fades in at `at`, the transition click runs at its own tempo on the click channel, and the next song starts on the next downbeat of that click after `startNext` (or after `autoStartSec`), with the pad fading out if `fadeOut`. `stop` and `carryOver` need nothing from the engine until `startNext`. If `at` is the very end of the plan (a song with no tail), the plan ending counts as reaching it. When the next song has taken over and the current one has stopped, the engine swaps their roles and sends `songChanged {songId}`; when the current song plays to its end with nothing handing over it sends `songEnded {songId}` (during a pad or segue transition it does not). `stop` also cancels a transition, stops the click and leaves the next song cued (SL-32). The snapshot's `next` is `{songId, buffered, state}` and `transition` is `none`, `armed`, `segue`, `waiting` (a pad transition waiting for Play) or `handing`.

**Live plan changes (protocol 8, PB-47, ARR-90).** A jump, a vamp, a repeat or a move on is a new plan built by the app. With `swapAt` (plan seconds) a plan sent while playing takes effect at that frame with no seek: the player keeps playing the old plan up to the frame and the new one from it, so the two plans must be identical before it. The disk thread tags every block with the version of the plan it was read for; at the swap frame it starts again with its streams where the old plan had them, so a new plan that carries on from the same source has no seam and one that jumps gets the usual splice (PB-55). Blocks of the old plan from the frame on are dropped as they come up. A swap whose frame the playhead has already passed takes effect where the playhead is (the disk catches up; a short gap). A second swap before the first has landed starts from the earlier frame. A swap needs the same `tempo`; otherwise, and when the song is not playing, `setPlan` applies as before and the reply has no `swapped`.

**Device loss (protocol 10, REL-20…24, AT-42, AT-43).** The device `setDevice` opened is the *main* device. The engine notices that it has gone (it is closed, JUCE opened another one in its place, or it stopped calling back for 500 ms), closes whatever is open and starts the **silent clock**: the same audio graph rendered in 512-frame blocks at real-time pace into a buffer nobody hears, counted from the last block the device played, so the plan moves on as if heard. If a fallback is set and connected it is opened at once (the silent clock covers the opening) and plays from the frame the clock reached; `deviceLost {name, route: "fallback", fallback, device}`. Otherwise `deviceLost {name, route: "silent"}`; the main device is looked for every second and reopened as soon as it is back, at the bar the clock reached (`deviceRestored {name, device}`). After `silentLimitSec` (default 60) the playing songs pause (`silentClockStopped {afterSec}`). On the fallback, a main device that returns is reported once with `mainDeviceBack {name}`; the app sends `switchToMain` (Switch back, or at the next section boundary). A device that starts always takes over from the silent clock before it renders, so only one thread renders at a time. The snapshot's `route` is `main`, `fallback`, `silent` or `none` (no device chosen), `lostSec` the seconds since the loss and `mainBack` whether the main device is back. Each new xrun is sent as `xrun {count, frame}` (plan frame, REL-24). MIDI inputs are checked every 2 s: one that went away is closed and opened again when it returns, with `midiInputsChanged {open, missing}`.

**Voice (protocol 12, VC-10).** The audio callback copies the voice channel into a lock-free ring and nothing else (PB-3). A thread of its own resamples it to 16 kHz and runs an energy detector in 20 ms frames: speech is 10 dB above the noise floor and above −50 dBFS; an utterance starts after 60 ms of speech with the 300 ms before it, ends after 500 ms without, and is cut at 6 s; under 160 ms of speech is dropped. Each utterance is sent as `utterance {rate: 16000, durationSec, trailingSec, samples}` (16-bit PCM, base64; the last `trailingSec` are silence). The engine does not recognise speech: the app gives each utterance to the `listen` worker (REL-50). The snapshot's `voice` is `{on, level (peak dBFS), hearing}` while a voice channel is set, and null otherwise.

**Automation (protocol 13, AUTO-5, AUTO-21, AUTO-30, AUTO-31, PB-71).** The app builds the automation with the plan (`RECompose.Core.Automation`) and sends it in `setPlan`, in plan seconds. `midi` messages (up to 3 bytes; an empty one sends nothing and is only reported) are picked up by the audio thread as the playhead plays past them, never when a seek or a swap jumps over them, copied into a lock-free ring and sent by a MIDI thread a millisecond or so later; each batch sent, by the plan, `sendMidi` or panic, comes back as `midiOut {messages: [{id, bytes}]}`, which the app logs by `id`. `calls` are clips the engine reads once per song (mono, resampled; from `from` seconds into the file for `length` seconds when given, peak-normalised to −6 dBFS with `normalize`) and renders into the track loaded with `generated: "speech"` (no file; the app puts it on the Cue output, solo-safe), on the disk thread as the click is, so seeks, swaps, mutes and meters work as for any track. The guide's written call is silenced with an ordinary `mute` of the guide track in a segment split around it.

**Holds (protocol 8, PB-52, ARR-38).** `holds` lists plan seconds where the plan clock waits; each lies on a segment boundary (the ring-out before it is in the plan). On reaching one the playhead stops and the stems are silent. With `pad` and a `padKey` (pitch class) the pad fades in; with `click` a free-running click plays at the song tempo on the click track's output (accent on the first beat counted from the hold). `go` releases it: the playhead moves on, the pad fades out, and the count-in that follows is ordinary click segments in the plan. The snapshot reports `holding` and `holdSec` (real seconds waited). A plan swapped in without the hold, or a seek, releases it. A hold at the very end of the plan waits too; the song ends at `go`.

`device` = `{type, name, sampleRate, bufferSize, outputChannels: [names], activeOutputs, sampleRates, bufferSizes, outputLatency}`.

`output.channel` is 0-based; `width` 1 = one channel, 2 = a pair. A mono track on a pair plays at the same level on both channels; a stereo track on one channel is summed at −3 dB (PB-85). `soloSafe` tracks (click, guide, spoken guide) keep playing when others are soloed (PB-82). The app resolves the logical outputs Main and Cue to channels before sending.

| Event | Fields |
|---|---|
| `snapshot` | `state` (`stopped`, `playing`, `paused`), `frame` (next frame to render, in plan frames), `t` (engine clock, ns), `latency` (output latency, in plan frames), `sampleRate`, `songId`, `lengthFrames` (the files), `planFrames` (the plan; the transport ends here), `buffered`, `waiting`, `starved`, `xruns`, `cpu`, `deviceOpen`, `meters: [{id, peak}]`, `master: [left, right]`, `holding`, `holdSec` (8), `route`, `lostSec`, `mainBack` (10), `voice` (12). Sent at 60 Hz while playing, otherwise on change and once a second. The heard frame is `frame − latency` while playing (PB-4). |
| `midiIn` (9) | `kind` (`note`, `cc`, `pc`), `channel` (1–16), `number`, `value`. Note on has its velocity as `value` and note off 0; a program change has 0. The app matches them against its bindings (LIVE-52); the engine does not interpret them. |
| `deviceChanged` | `device` (or null when it went away) |
| `deviceError` | `message` |
| `deviceLost` (10) | `name`, `route` (`fallback` or `silent`), `fallback?`, `device?` |
| `deviceRestored` (10) | `name`, `device` |
| `mainDeviceBack` (10) | `name` |
| `silentClockStopped` (10) | `afterSec` |
| `xrun` (10) | `count`, `frame` |
| `midiInputsChanged` (10) | `open: [names]`, `missing: [names]` |
| `utterance` (12) | `rate`, `durationSec`, `trailingSec`, `samples` |
| `voiceError` (12) | `message` |
| `midiOut` (13) | `messages: [{id, bytes}]` |
| `fileMissing` | `trackId`, `problem` (REL-50) |
| `songReloaded` | the `song` object, after a device rate change |

### Tool modes

The import and analysis jobs run the same executable as short-lived, low-priority processes (PB-92), never the playback engine. They print one JSON line on stdout and exit non-zero on failure with `{ok: false, problem}`:

- `recompose-engine probe <file>` prints `{ok, channels, sampleRate, frames, durationSec, bitsPerSample, format}` or `{ok: false, problem}`.
- `recompose-engine peaks <file> <out> [framesPerBucket]` writes a peaks file: `RECP`, u32 version 1, u32 framesPerBucket, u32 bucketCount, f64 sampleRate, then bucketCount × (f32 min, f32 max) over all channels; little-endian.
- `recompose-engine onsets <file>` prints `{ok, sampleRate, durationSec, onsets: [[timeSec, peakDb, centroidHz], …]}`: the attacks of a click track with their level in the first 30 ms and the spectral centroid of the first 1024 samples (SEC-1). The fit, accents and downbeat are computed in `RECompose.Core` (`ClickFit`).
- `recompose-engine transcribe <file> <model> [--prompt <text>] [--threads N]` prints `{ok, model, chunks, words: [[text, startSec, endSec, confidence], …]}`: the guide's words with whisper.cpp (SEC-11/12). Only the stretches above the level threshold are transcribed, packed into windows of up to 28 s a second apart; times are source seconds. Progress lines `progress 0.42` go to stderr. Section detection is in `RECompose.Core` (`SectionDetector`).
- `recompose-engine listen <model> [--threads N]` is the voice worker the app keeps running (VC-10): it prints `{ok: true}` once the model is loaded, then answers each stdin line `{id, prompt, samples}` (16 kHz 16-bit PCM in base64; `prompt` is the bias list, VC-11) with one stdout line `{id, text, confidence}` (the mean token probability). It exits when stdin closes.
