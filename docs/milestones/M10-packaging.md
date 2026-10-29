# Milestone 10: Platform polish and packaging

**Goal:** RECompose installs like any other app on Windows and macOS, updates itself without ever getting in the way of a service, asks for the microphone properly, and has the platforms' menus.

**Status:** done (29 Oct 2026). How to try it: [M10 test guide](../testing/M10-test-guide.md). Releases: [packaging.md](../packaging.md).

**Starting point:** milestone 9. CI builds and tests on Windows, macOS and Linux and uploads runnable zips; nothing is installed, signed or updated.

## Decisions for this milestone

1. **Velopack on both platforms** (MIT) for the installers and updates, instead of Velopack on Windows and Sparkle on macOS: one update path, one feed (GitHub releases), and it is a NuGet package the app already builds with. Windows gets a per-user `Setup.exe`; macOS a DMG (and a `.pkg`) of the same app.
2. **Updates download in the background and install after quitting** (PLAT-70, AT-63). Nothing appears in Perform; quitting while playing or in Perform leaves it for the next quit; nothing is applied at start-up, where the engine may still be playing from before (REL-34).
3. **Signing is configured, not hard-wired.** The release workflow signs (Authenticode; Developer ID with the hardened runtime, notarisation and stapling) when the certificates are in the repository secrets, and builds unsigned packages otherwise.
4. **Two macOS builds** (`osx-arm64`, `osx-x64`) instead of one universal app: a self-contained .NET app's runtime is per architecture, so a universal bundle would need two runtimes and a launcher. The engine is already universal. Each build updates on its own channel.
5. **Microphone permission is read, not requested** (PLAT-35, AT-61): macOS asks on its own the first time the engine opens the input, using the bundle's `NSMicrophoneUsageDescription`; the app reads the answer (AVCaptureDevice on macOS, the privacy switches in the registry on Windows) and turns the Voice indicator red with a link to the system page.
6. **The menu is built once** (PLAT-20) from the window's existing commands: the macOS menu bar, and the same groups behind a Menu button in the top bar on Windows.

## Definition of done: the demo

1. Push a tag `v0.2.0`: the Release run publishes `RECompose-win-x64-Setup.exe` and the two DMGs.
2. Run the Setup: RECompose installs for this user and opens; it is in the Start menu.
3. Publish `v0.2.1`: within a minute of starting, the Prepare top bar says "Update 0.2.1 ready. It installs when you quit." Perform shows nothing. Quit, start again: 0.2.1, with the library, settings, cache and API key intact.
4. Turn off microphone access for desktop apps and choose a voice channel: VOICE is red, its tooltip names the setting, a click opens it. Pedals still work.
5. The Menu button (Windows) or the menu bar (macOS) has File, Edit, Song, Service, Transport, View, Window and Help.

## Work packages

| # | Package | Exit |
|---|---|---|
| WP1 | App: Velopack hooks, update check, download, install on quit | Builds; `vpk pack` accepts the app |
| WP2 | Release workflow, macOS Info.plist and entitlements, signing secrets | Local Windows pack; tag run |
| WP3 | Microphone permission and link (AT-61) | Presentation test |
| WP4 | Menu (PLAT-20) | Window test |
| WP5 | Docs, test guide, soak procedure | |

## As built

- The update check runs once, 30 s after start-up; failures are logged and retried at the next start.
- Microphone permission is re-read at most every 2 s while the engine reports, so turning it on in the system settings clears the error without a restart.
- The menu items run the existing commands; the shortcuts stay with the window's key handling and are not shown in the menu.
- The 3-hour soak and the hardware tests of REL-60 are run by hand from the test guide.

## Known gaps

- No release has been built by the workflow yet, and the macOS packaging, signing and notarisation are untested until the first tag runs with the secrets.
- Windows on arm64 (PLAT-2: later).
- The menus show no shortcuts, and there is no About window (Help links to the repository).
