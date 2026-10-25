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

### Protocol 2, 3 and 4 (milestones 1, 3 and 4)

Protocol 3 adds `setPlan` and the `planFrames` snapshot field; everything else is as in 2. Protocol 4 adds the engine's click: a generated track in `loadSong`, the song grid in `setPlan` and a `click` flag per segment.

| Request | Fields | Reply |
|---|---|---|
| `listDevices` | | `devices {types: [{name, devices: [names]}], current: device \| null}` |
| `setDevice` | `deviceType?`, `name?`, `sampleRate?`, `bufferSize?` (absent = current or default) | `device {device}` or `deviceError` |
| `loadSong` | `songId`, `tracks: [{id, file, output: {channel, width}, gainDb, mute, solo, soloSafe, generated?}]`, `masterGainDb`, `startFrame` | `song {songId, sampleRate, lengthFrames, masterGainDb, tracks: [{id, channels, sourceRate, sourceFrames, missing, problem, gainDb, mute, solo, output}]}` |
| `unloadSong` | | `ok` |
| `play`, `pause`, `stop` | | `ok`. Fades of 10, 20 and 50 ms (PB-20…22); stop returns to frame 0. |
| `seek` | `frame` | `ok`. While playing: a 10 ms fade-out, then playback resumes exactly at `frame` once every track has data there (PB-26, PB-55). |
| `setTrack` | `trackId`, any of `gainDb`, `mute`, `solo`, `output` | `ok`. Changes ramp over one 256-frame block. |
| `setMaster` | `gainDb` | `ok` |
| `setPlan` (3, 4) | `segments: [{start, end, src, fadeIn, fadeOut, mute: [trackIds], click?}]`, `grid?: {origin, beat, beatsPerBar}` (4), `position?` | `plan {frames}`. See below. |
| `getState` | | `state {song, device, snapshot}` |
| `renderOffline` | `frames`, `channels`, `path?` | `rendered {frames, channels, peak: [..], rms: [..], snapshot}`. Tests only: pulls the audio through the graph on the message thread instead of the device, waiting for the disk, and optionally writes a 32-bit float WAV. |

**Plans (`setPlan`, PB-40).** The app turns the arrangement into contiguous timed segments in *plan seconds* (`RECompose.Core`, `PlanBuilder`); the engine only executes them. Segment `i` plays plan time `[start, end)`; `src` is the source second it reads from, or `null` for silence (count-ins, click and pad bars). A segment's `src` that continues where the previous one stopped plays on with no seam and no resampler reset. Where the source jumps, the incoming audio starts exactly on the boundary with a 2 ms fade-in while the outgoing audio keeps playing for 10 ms with a fade-out (PB-55), through a second resampler per track so the seam is as clean as the rest. `fadeIn` and `fadeOut` (seconds, inside the segment) are dB-linear from −60 dB to 0 and skip solo-safe tracks. `mute` lists track ids silenced in this segment, ramped over 10 ms. `position` is the plan second the playhead resumes at; absent keeps the current frame. Segments must be contiguous and start at 0, or the engine replies `badMessage`. The engine converts to frames once (rounding each start and source once) and keeps the seconds, so a device rate change derives the plan again. While playing, a new plan is applied like a seek (10 ms fade-out, then play on at `position`); a gap-free swap is milestone 5. A loaded song plays as written (one segment, source second 0) until a plan arrives; `loadSong` and `unloadSong` clear the plan. The song ends at the last segment: the transport stops and returns to the start. Every plan sent is kept until the song is unloaded (a few kilobytes each), so the audio thread never frees one.

**The click (protocol 4, PB-70).** A track with `generated: "click"` has no file: the engine synthesizes a click into it (accent on beat 1 at 1.6 kHz, other beats at 1 kHz, 30 ms, peaking at −6 dBFS), so it is routed, muted and metered like any track. The app loads one with the id `generated-click` on the Cue output. It sounds on every beat of the plan's `grid` that starts inside a segment with `click: true` (count-ins and click bars, SL-36, ARR-39). `grid.origin` is the plan second of bar 1 beat 1 (S0), `grid.beat` the beat length in seconds; beats before the origin count down to it, so a count-in ending on S0 lands on the grid. Each beat's frame is rounded once, as segment starts are. Without a grid there is no click.

`device` = `{type, name, sampleRate, bufferSize, outputChannels: [names], activeOutputs, sampleRates, bufferSizes, outputLatency}`.

`output.channel` is 0-based; `width` 1 = one channel, 2 = a pair. A mono track on a pair plays at the same level on both channels; a stereo track on one channel is summed at −3 dB (PB-85). `soloSafe` tracks (click, guide, spoken guide) keep playing when others are soloed (PB-82). The app resolves the logical outputs Main and Cue to channels before sending.

| Event | Fields |
|---|---|
| `snapshot` | `state` (`stopped`, `playing`, `paused`), `frame` (next frame to render), `t` (engine clock, ns), `latency` (output latency, frames), `sampleRate`, `songId`, `lengthFrames` (the files), `planFrames` (the plan; the transport ends here), `buffered`, `waiting`, `starved`, `xruns`, `cpu`, `deviceOpen`, `meters: [{id, peak}]`, `master: [left, right]`. Sent at 60 Hz while playing, otherwise on change and once a second. The heard frame is `frame − latency` while playing (PB-4). |
| `deviceChanged` | `device` (or null when it went away) |
| `deviceError` | `message` |
| `fileMissing` | `trackId`, `problem` (REL-50) |
| `songReloaded` | the `song` object, after a device rate change |

### Tool modes

The import and analysis jobs run the same executable as short-lived, low-priority processes (PB-92), never the playback engine. They print one JSON line on stdout and exit non-zero on failure with `{ok: false, problem}`:

- `recompose-engine probe <file>` prints `{ok, channels, sampleRate, frames, durationSec, bitsPerSample, format}` or `{ok: false, problem}`.
- `recompose-engine peaks <file> <out> [framesPerBucket]` writes a peaks file: `RECP`, u32 version 1, u32 framesPerBucket, u32 bucketCount, f64 sampleRate, then bucketCount × (f32 min, f32 max) over all channels; little-endian.
- `recompose-engine onsets <file>` prints `{ok, sampleRate, durationSec, onsets: [[timeSec, peakDb, centroidHz], …]}`: the attacks of a click track with their level in the first 30 ms and the spectral centroid of the first 1024 samples (SEC-1). The fit, accents and downbeat are computed in `RECompose.Core` (`ClickFit`).
- `recompose-engine transcribe <file> <model> [--prompt <text>] [--threads N]` prints `{ok, model, chunks, words: [[text, startSec, endSec, confidence], …]}`: the guide's words with whisper.cpp (SEC-11/12). Only the stretches above the level threshold are transcribed, packed into windows of up to 28 s a second apart; times are source seconds. Progress lines `progress 0.42` go to stderr. Section detection is in `RECompose.Core` (`SectionDetector`).
