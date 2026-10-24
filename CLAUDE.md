# RECompose: notes for coding agents

Read `README.md` for the layout and build commands, `docs/adr/` for decisions, and the current milestone plan in `docs/milestones/`. The binding product spec (requirement IDs such as PB-30, LIVE-10) and the design system guide are kept in the product owner's Claude project; `design/` holds the design system's source files.

## Rules

- **Commits:** author `nathtjan <74716863+nathtjan@users.noreply.github.com>`. Never add AI attribution of any kind: no `Co-Authored-By` or other trailers naming Claude or Anthropic, no "Generated with" lines, no signing with an agent's key. This applies to commit messages, tags and pull request descriptions.
- Reference requirement IDs in tests and in commit messages where they apply (`PB-32: song bar readout`).
- The audio thread is real-time safe (PB-3): no allocation, locks, I/O or logging in the engine's audio callback. Managed code never runs on the audio thread.
- The engine is a separate process (`recompose-engine`). The engine protocol (`docs/engine-protocol.md`) is the only boundary between C# and C++: length-prefixed JSON over a local socket. Change both sides in one commit, update the doc, and bump `REC_PROTOCOL_VERSION` (`engine/src/protocol.h`) together with `EngineProtocol.Version`. `hello` and `shutdown` never change shape.
- The engine must outlive the app (REL-34): it keeps playing when the app disconnects, is started without redirected output (a broken pipe must not kill it), and exits only on `shutdown` or when idle with no app connected.
- Plan building, readouts and import rules live in `RECompose.Core`, are pure, and are unit-tested. The engine only executes timed segments.
- UI styling comes from `design/tokens.json` through generated resources. Don't hand-code colours, sizes or fonts; use the design system's components as the behavioural reference (`design/reference/`).
- Copy follows the design system's voice: band words (Verse 2, Chorus 3), real accidentals (B♭), no emoji, no exclamation marks.

## Where things stand

Milestones 0–3 are on `main`; the next one is M4 (services). Start from `docs/milestones/M3-arrangements.md` (status, known gaps) and the handoff doc in the Claude project.

- Two clocks: *plan time* is the engine position and the as-heard timeline; *source time* is stems, sections, M and review. Convert only through `SongPlan` (`SourceAt`, `PlanAt`, `Remap`).
- `MainViewModel` owns the playhead (`PlanPositionSec`, `PositionSec`, `TimelinePosition`); never mix them.
- The engine plays a plan sent with `setPlan` (protocol 3); plan building lives in `RECompose.Core.Planning`.
- Key lift, click/pad bars, holds and until-cued repeats are stored but not audible until M4–M6.

## Commands

- Full build and tests: `./build.sh` (macOS/Linux) or `.\build.ps1` (Windows)
- .NET only: `dotnet build RECompose.slnx`, `dotnet test --solution RECompose.slnx`
- Engine only: `cmake -S engine -B build/engine && cmake --build build/engine --config Release && ctest --test-dir build/engine -C Release`
