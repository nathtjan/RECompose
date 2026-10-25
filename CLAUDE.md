# RECompose: notes for coding agents

Read `README.md` for the layout and build commands, `docs/adr/` for decisions, and the current milestone plan in `docs/milestones/`. The binding product spec (requirement IDs such as PB-30, LIVE-10) is in `specs/RECompose-spec.md` (local only, git-ignored; `specs/README.md` indexes the rest: handoff, design guide, screenshots, artifacts) and the design system guide is kept in the product owner's Claude project; `design/` holds the design system's source files.

## Rules

- **Commits:** author `nathtjan <74716863+nathtjan@users.noreply.github.com>`. Never add AI attribution of any kind: no `Co-Authored-By` or other trailers naming Claude or Anthropic, no "Generated with" lines, no signing with an agent's key. This applies to commit messages, tags and pull request descriptions.
- **Never commit `specs/`.** It holds the private product spec (`specs/RECompose-spec.md`) and is git-ignored. Don't `git add -f` it, copy it elsewhere in the repo, or quote it in commits, docs, issues or pull requests; cite requirement IDs only.
- Reference requirement IDs in tests and in commit messages where they apply (`PB-32: song bar readout`).
- The audio thread is real-time safe (PB-3): no allocation, locks, I/O or logging in the engine's audio callback. Managed code never runs on the audio thread.
- The engine is a separate process (`recompose-engine`). The engine protocol (`docs/engine-protocol.md`) is the only boundary between C# and C++: length-prefixed JSON over a local socket. Change both sides in one commit, update the doc, and bump `REC_PROTOCOL_VERSION` (`engine/src/protocol.h`) together with `EngineProtocol.Version`. `hello` and `shutdown` never change shape.
- The engine must outlive the app (REL-34): it keeps playing when the app disconnects, is started without redirected output (a broken pipe must not kill it), and exits only on `shutdown` or when idle with no app connected.
- Plan building, readouts and import rules live in `RECompose.Core`, are pure, and are unit-tested. The engine only executes timed segments.
- UI styling comes from `design/tokens.json` through generated resources. Don't hand-code colours, sizes or fonts; use the design system's components as the behavioural reference (`design/reference/`).
- Copy follows the design system's voice: band words (Verse 2, Chorus 3), real accidentals (B♭), no emoji, no exclamation marks.

## Where things stand

Milestones 0–4 are on `main`; the next one is M5 (Perform view and live control). Start from `docs/milestones/M4-services.md` (status, known gaps) and the handoff doc in the Claude project.

- Two clocks: *plan time* is the engine position and the as-heard timeline; *source time* is stems, sections, M and review. Convert only through `SongPlan` (`SourceAt`, `PlanAt`, `Remap`).
- `MainViewModel` owns the playhead (`PlanPositionSec`, `PositionSec`, `TimelinePosition`); never mix them.
- The engine plays a plan sent with `setPlan` (protocol 7 now, see `docs/engine-protocol.md`); plan building lives in `RECompose.Core.Planning`.
- Services (M4): a service entry picks a variant, a custom arrangement or as written, plus key, tempo, count-in, mix preset and the transition after it. `EntryPlan`/`PlanOptions.For(entry, song)` build its plan; the engine plays key and tempo live (protocol 5), click and count-ins (4), pads (6), and a second song slot with segue, crossfade and pad transitions (7). Holds and until-cued repeats are still stored but not audible until M5; rendered keys come in M6; spoken counts in M7.

## Commands

- Full build and tests: `./build.sh` (macOS/Linux) or `.\build.ps1` (Windows)
- .NET only: `dotnet build RECompose.slnx`, `dotnet test --solution RECompose.slnx`
- Engine only: `cmake -S engine -B build/engine && cmake --build build/engine --config Release && ctest --test-dir build/engine -C Release`
