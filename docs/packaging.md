# Packaging and releases

Installers and updates use [Velopack](https://velopack.io) (PLAT-70). The release workflow (`.github/workflows/release.yml`) runs when a tag such as `v0.2.0` is pushed:

```
git tag v0.2.0
git push origin v0.2.0
```

It builds and tests the engine, publishes the app for `win-x64`, `osx-arm64` and `osx-x64` with the speech model, packs each with `vpk`, and publishes a GitHub release with:

| File | What it is |
|---|---|
| `RECompose-win-x64-Setup.exe` | Windows installer, per user (`%LOCALAPPDATA%\RECompose`), with a Start menu shortcut |
| `RECompose-osx-arm64.dmg`, `RECompose-osx-x64.dmg` | macOS app for Apple Silicon and Intel; drag it to Applications |
| `RECompose-osx-*-Setup.pkg` | the same app as an installer package |
| `*-Portable.zip`, `*.nupkg`, `releases.*.json` | the portable app and the update feed the installed apps read |

## Updates

An installed app checks the latest GitHub release 30 s after it starts, downloads a newer one in the background and installs it after you quit. The top bar in Prepare says "Update 0.2.0 ready. It installs when you quit." Nothing is shown or installed in Perform or while playing (AT-63): quitting from Perform or while a song plays leaves the update for the next quit, and a downloaded update is not applied at start-up because the engine may still be playing (REL-34).

Settings, the library, the cache and the logs are in the data folder (`%APPDATA%\RECompose`, `~/Library/Application Support/RECompose`) and the Anthropic key is in the OS credential store, so updates and reinstalls keep them. Builds that were not installed (the CI artifact zips, `dotnet run`) never update.

## Signing

The workflow signs when these repository secrets are set, and otherwise builds unsigned packages with a warning.

| Secret | Value |
|---|---|
| `WINDOWS_CERTIFICATE_PFX` | the Authenticode code-signing certificate as a base64 `.pfx` |
| `WINDOWS_CERTIFICATE_PASSWORD` | its password |
| `MACOS_CERTIFICATE_P12` | a base64 `.p12` holding both the *Developer ID Application* and *Developer ID Installer* certificates |
| `MACOS_CERTIFICATE_PASSWORD` | its password |
| `MACOS_APP_IDENTITY` | e.g. `Developer ID Application: Name (TEAMID)` |
| `MACOS_INSTALL_IDENTITY` | e.g. `Developer ID Installer: Name (TEAMID)` |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` | the Apple ID, team and an app-specific password for notarisation |

macOS apps are signed with the hardened runtime and `packaging/macos/RECompose.entitlements` (JIT for .NET, audio input for the talkback mic), notarised, and the DMG is stapled. `packaging/macos/Info.plist` carries the bundle id `io.github.nathtjan.recompose`, macOS 13 as the minimum and the microphone text (PLAT-35).

Without signing, Windows SmartScreen warns on first run ("More info › Run anyway") and macOS needs right-click › Open the first time.
