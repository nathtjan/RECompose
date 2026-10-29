# RECompose

Live multitrack stem playback for a church music director, on Windows and macOS. The MD shapes each song's arrangement for this week's service, rehearses it with the band, and runs it live, hands-free, by voice or foot pedal.

**Status:** milestone 10 (platform polish and packaging) is ready to test: a per-user Windows installer and macOS DMGs from tagged releases, updates that download in the background and install after quitting (never in Perform or while playing), the microphone permission with a link to the system page, and the menus. Releases: [docs/packaging.md](docs/packaging.md). Milestone 9 brought the assistant: with an Anthropic API key the MD can type or dictate a change to the service, a song or its cues ("make song 3 short: V1, C, V2, C, end") and the assistant makes it with the editor's own edits, one undo step per message, asking for confirmation in Perform; without a key nothing changes. Milestone 8 brought automation and the log: section notes and cues go out over MIDI for the lights, a spoken guide calls each section a bar ahead, the guide's own call is replaced when a change makes it wrong, count-ins are spoken, and a LOG tab and a service report record what happened. Milestone 7 brought voice control: the MD talks into a talkback mic and RECompose acts on band directions as it does on a key or a pedal. Milestone 6 brought a service check before the service, rendered keys and tempos, a fallback audio device and a silent clock when the interface is lost, resume after a crash or PANIC, version history, and service export and import. Milestone 5 brought the Perform view and live control: Ctrl+P turns the window into the Perform screen with NOW, NEXT, the plan strip, the mini timeline and a key deck; jumps, vamps, repeats, move on, end and cancel change the song while it plays with no gap; holds wait for GO; Service lock guards the keys; a page-turner, keys or a MIDI foot pedal drive it, with Learn; and Rehearsal loops a section with a count-in. Milestone 4 brought services, key and tempo, count-ins, pads and transitions, milestone 3 arrangements, milestone 2 sections and analysis, milestone 1 the engine, import, mixer, routing, transport and timeline. How to try it: [docs/testing/M10-test-guide.md](docs/testing/M10-test-guide.md) (and [M9](docs/testing/M9-test-guide.md), [M8](docs/testing/M8-test-guide.md), [M7](docs/testing/M7-test-guide.md), [M6](docs/testing/M6-test-guide.md), [M5](docs/testing/M5-test-guide.md), [M4](docs/testing/M4-test-guide.md), [M3](docs/testing/M3-test-guide.md), [M2](docs/testing/M2-test-guide.md), [M1](docs/testing/M1-test-guide.md)). Plans: [M1](docs/milestones/M1-engine-and-import.md), [M2](docs/milestones/M2-sections-and-analysis.md), [M3](docs/milestones/M3-arrangements.md), [M4](docs/milestones/M4-services.md), [M5](docs/milestones/M5-perform-and-live-control.md), [M6](docs/milestones/M6-reliability.md), [M7](docs/milestones/M7-voice-control.md), [M8](docs/milestones/M8-automation-and-log.md), [M9](docs/milestones/M9-assistant.md), [M10](docs/milestones/M10-packaging.md).

## Stack

| Part | Technology | Where |
|---|---|---|
| UI and app logic | C# / .NET 10, [Avalonia](https://avaloniaui.net) 12 | `src/` |
| Audio engine | C++20 on [JUCE](https://juce.com) 9, a separate process (`recompose-engine`) the app talks to over a local socket | `engine/` |
| Design system "Instrument" | tokens, fonts, icons (source of truth for the UI) | `design/` |

Why this split, and what is still open, is recorded in [docs/adr/0001-stack-and-architecture.md](docs/adr/0001-stack-and-architecture.md).

```
engine/            the engine process (CMake); docs/engine-protocol.md is the only interface
src/RECompose.Core     domain model, plan building, readouts, import rules (pure, unit-tested)
src/RECompose.Engine   engine client: starts or reattaches to the engine, speaks the protocol
src/RECompose.Presentation  view models and app services (no UI framework, tested against the engine)
src/RECompose.App      Avalonia desktop app
tests/             xUnit v3 tests, including headless window tests (RECompose.App.Tests)
tools/             generators for design resources (tokens, icons) and CI helpers
design/            tokens.json, fonts, icons, design-system notes
docs/              decisions, the engine protocol, milestone plans
```

## Build

### Prerequisites

| | Windows 11 | macOS 13+ |
|---|---|---|
| C++ | Visual Studio 2022 or 2026 (Community or Build Tools) with **Desktop development with C++** | Xcode command line tools (`xcode-select --install`) |
| CMake | 3.22 or later (Visual Studio includes one) | `brew install cmake` |
| .NET | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | same |
| Git | Git for Windows | included with the command line tools |

The first engine build downloads JUCE 9.0.4 (about 130 MB) and whisper.cpp 1.9.5 and takes a few minutes.

Guide transcription needs the speech model `ggml-base.en.bin` (142 MiB, MIT). The CI builds ship it in `models/` next to the app. A development build downloads it on first use into `<data folder>/models/`, or set `RECOMPOSE_SPEECH_MODEL` to a copy.

### Build, test and run

```powershell
# Windows (PowerShell)
.\build.ps1
dotnet run --project src/RECompose.App
```

```bash
# macOS / Linux
./build.sh
dotnet run --project src/RECompose.App
```

The script builds the engine with CMake, runs its tests, copies `recompose-engine` to `artifacts/native/bin/`, then builds and tests the .NET solution (the build copies the engine next to the app). After changing only C# code, `dotnet build` / `dotnet test` / `dotnet run` are enough.

CI builds and tests every push on Windows, macOS and Linux, and attaches a runnable app for Windows (`RECompose-win-x64`) and macOS (`RECompose-osx-arm64`) to each run. A tag such as `v0.2.0` builds the installers and publishes a release ([docs/packaging.md](docs/packaging.md)).

## Licence

RECompose is free software under the [GNU Affero General Public License v3.0 or later](LICENSE). It uses JUCE under its AGPLv3 option. Bundled fonts and other third-party parts are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
