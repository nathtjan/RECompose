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

### Protocol 2 (milestone 1)

| Request | Fields | Reply |
|---|---|---|
| `listDevices` | | `devices {types: [{name, devices: [names]}], current: device \| null}` |
| `setDevice` | `deviceType?`, `name?`, `sampleRate?`, `bufferSize?` (absent = current or default) | `device {device}` or `deviceError` |
| `loadSong` | `songId`, `tracks: [{id, file, output: {channel, width}, gainDb, mute, solo, soloSafe}]`, `masterGainDb`, `startFrame` | `song {songId, sampleRate, lengthFrames, masterGainDb, tracks: [{id, channels, sourceRate, sourceFrames, missing, problem, gainDb, mute, solo, output}]}` |
| `unloadSong` | | `ok` |
| `play`, `pause`, `stop` | | `ok`. Fades of 10, 20 and 50 ms (PB-20…22); stop returns to frame 0. |
| `seek` | `frame` | `ok`. While playing: a 10 ms fade-out, then playback resumes exactly at `frame` once every track has data there (PB-26, PB-55). |
| `setTrack` | `trackId`, any of `gainDb`, `mute`, `solo`, `output` | `ok`. Changes ramp over one 256-frame block. |
| `setMaster` | `gainDb` | `ok` |
| `getState` | | `state {song, device, snapshot}` |
| `renderOffline` | `frames`, `channels`, `path?` | `rendered {frames, channels, peak: [..], rms: [..], snapshot}`. Tests only: pulls the audio through the graph on the message thread instead of the device, waiting for the disk, and optionally writes a 32-bit float WAV. |

`device` = `{type, name, sampleRate, bufferSize, outputChannels: [names], activeOutputs, sampleRates, bufferSizes, outputLatency}`.

`output.channel` is 0-based; `width` 1 = one channel, 2 = a pair. A mono track on a pair plays at the same level on both channels; a stereo track on one channel is summed at −3 dB (PB-85). `soloSafe` tracks (click, guide, spoken guide) keep playing when others are soloed (PB-82). The app resolves the logical outputs Main and Cue to channels before sending.

| Event | Fields |
|---|---|
| `snapshot` | `state` (`stopped`, `playing`, `paused`), `frame` (next frame to render), `t` (engine clock, ns), `latency` (output latency, frames), `sampleRate`, `songId`, `lengthFrames`, `buffered`, `waiting`, `starved`, `xruns`, `cpu`, `deviceOpen`, `meters: [{id, peak}]`, `master: [left, right]`. Sent at 60 Hz while playing, otherwise on change and once a second. The heard frame is `frame − latency` while playing (PB-4). |
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
