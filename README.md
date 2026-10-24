# RECompose

Live multitrack stem playback for a church music director, on Windows and macOS. The MD shapes each song's arrangement for this week's service, rehearses it with the band, and runs it live, hands-free, by voice or foot pedal.

**Status:** milestone 2 (sections and analysis) is ready to test: after import the click track sets the tempo grid and the guide track is transcribed offline, Detect from guide finds the sections, and the MD reviews and edits them; the header shows NOW and NEXT. Milestone 1 brought the engine, import, mixer, routing, transport and timeline. How to try it: [docs/testing/M2-test-guide.md](docs/testing/M2-test-guide.md) (and [M1](docs/testing/M1-test-guide.md)). Plans: [M1](docs/milestones/M1-engine-and-import.md), [M2](docs/milestones/M2-sections-and-analysis.md).

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

CI builds and tests every push on Windows, macOS and Linux, and attaches a runnable app for Windows (`RECompose-win-x64`) and macOS (`RECompose-osx-arm64`) to each run.

## Licence

RECompose is free software under the [GNU Affero General Public License v3.0 or later](LICENSE). It uses JUCE under its AGPLv3 option. Bundled fonts and other third-party parts are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
