# Engine protocol

The app (C#) and the audio engine (`recompose-engine`, C++/JUCE) are separate processes (ADR 0001). This protocol is the only interface between them. Change it in one commit on both sides: `REC_PROTOCOL_VERSION` in `engine/src/protocol.h` and `EngineProtocol.Version` in `src/RECompose.Engine/EngineProtocol.cs` move together.

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
| App starts, engine already running | The app connects to it and says hello: it **reattaches** (for example after an app crash while a song kept playing). |
| A second connection arrives | It replaces the current one. |
| App disconnects or crashes | The engine keeps running and keeps playing. |
| No app connected for `idle-exit-seconds` and nothing playing | The engine exits. This stops orphaned engines. |
| App quits normally | It sends `shutdown`; the engine exits. |
| Engine speaks another protocol version | `hello` fails with `protocolMismatch`; the app sends `shutdown` (which every version accepts) and starts its own engine. |

The engine ignores `SIGPIPE`, so a vanished app can never kill it.

## Messages

Every message has a `type`. Requests from the app carry an integer `id`; the reply carries the same value in `re`. Messages from the engine without `re` are **events** (milestone 1 onwards: snapshots, device changes, load progress).

Errors: `{"type":"error","re":<id>,"code":"…","message":"…"}`. Codes: `badMessage`, `helloRequired`, `protocolMismatch`, `unknownType`.

### Protocol 1 (milestone 0)

| Request | Reply | Notes |
|---|---|---|
| `{"type":"hello","id":1,"protocol":1,"client":"RECompose 0.1.0"}` | `{"type":"hello","re":1,"protocol":1,"engine":"0.1.0","juce":"JUCE v9.0.4","pid":1234}` | Must come first. A mismatch replies `protocolMismatch` and closes the connection. |
| `{"type":"ping","id":2}` | `{"type":"pong","re":2}` | |
| `{"type":"shutdown","id":3}` | `{"type":"ok","re":3}` | Accepted before `hello`. The engine exits after replying. |

`hello` and `shutdown` keep this shape in every future version.

Milestone 1 adds the playback messages (device selection, loading segments, transport, mixer, snapshots, file info and peaks). They are sketched in `docs/milestones/M1-engine-and-import.md` and documented here as they land.
