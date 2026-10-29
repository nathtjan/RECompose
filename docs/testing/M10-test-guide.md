# Testing milestone 10 on your PC

This build makes RECompose an installed app: a Windows installer and macOS DMGs, updates that install after you quit, the microphone permission, and the menus. Everything from milestones 1 to 9 still works ([M9](M9-test-guide.md) and earlier).

## 1. Get the build

GitHub → **Releases** → the latest release → `RECompose-win-x64-Setup.exe` (Windows) or `RECompose-osx-arm64.dmg` / `RECompose-osx-x64.dmg` (Apple Silicon / Intel Mac). If the release is not signed yet, Windows shows SmartScreen ("More info › Run anyway") and macOS needs right-click › Open the first time.

The CI zips still work as before, but they never update.

## 2. Try the demo

| # | Do this | You should see |
|---|---|---|
| 1 | Run the Setup (or open the DMG and drag RECompose to Applications). | RECompose installs for you only, opens, and is in the Start menu (Launchpad). Your library, services and settings from earlier builds are there. |
| 2 | When a newer release exists, start RECompose and wait a minute in Prepare. | The top bar says "Update 0.2.1 ready. It installs when you quit." |
| 3 | Go to Perform. | No update note anywhere. |
| 4 | Quit from Prepare, wait a few seconds, start again. | The new version (the log's first line names it). Library, settings, render cache and the assistant key are unchanged. |
| 5 | Windows: Settings › Privacy & security › Microphone › turn off "Let desktop apps access your microphone". macOS: System Settings › Privacy & Security › Microphone › turn off RECompose. Then choose a voice channel in Settings › Audio. | VOICE is red with "Error"; the tooltip says where the switch is; clicking VOICE opens that page. Pedals and keys still work. |
| 6 | Turn the microphone back on. | Within a few seconds VOICE goes back to Listening. |
| 7 | macOS: first time you choose a voice channel. | The system asks "RECompose would like to access the microphone" with "RECompose listens to your talkback mic for voice commands." |
| 8 | **Menu** in the top bar (Windows) or the menu bar (macOS). | File, Edit, Song, Service, Transport, View, Window, Help, running the same actions as the buttons. |
| 9 | macOS with Lightkey: Settings › MIDI out. | The virtual source "RECompose" is listed in the lighting app. |

## 3. The soak (REL-60, by hand)

Once before the first real service:

1. Build a service of 6 to 8 songs with transitions, a pedal and voice control on, and Auto-advance on.
2. Play it in Perform in a loop for 3 hours. Every 10 minutes or so: a jump by pedal, one by voice, a vamp, PANIC and resume.
3. Once each: unplug and replug the audio interface mid-song, unplug the MIDI controller, turn off the network while using the assistant, and end the app from Task Manager / Activity Monitor during playback (it should keep playing and offer to resume).
4. Afterwards: the LOG tab and `logs/app-*.log` should show no xruns or errors you did not cause.

## 4. Please tell me

- Whether the installer and the first update went through without questions.
- Anything from earlier builds that was missing after installing.
- How the soak went (the log helps).

## 5. Known gaps in this build

- The app uses the default icon.
- Menus do not show shortcuts; Help links to the GitHub page.

## 6. If something goes wrong

Send me `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log` (macOS: `~/Library/Application Support/RECompose/logs`). For an installer problem, a screenshot of the message is enough.
