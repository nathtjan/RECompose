# Testing milestone 9 on your PC

This build adds the assistant: type or dictate a change to the service, a song or its cues, and it makes it with the same edits as the editor. It is optional; without a key everything works as before, and playback never calls it. Everything from milestones 1 to 8 still works ([M1](M1-test-guide.md), [M2](M2-test-guide.md), [M3](M3-test-guide.md), [M4](M4-test-guide.md), [M5](M5-test-guide.md), [M6](M6-test-guide.md), [M7](M7-test-guide.md), [M8](M8-test-guide.md)).

## 1. Get the build

GitHub → **Actions** → the latest green **CI** run on `main` → **Artifacts** → `RECompose-win-x64`. Unzip it and run `RECompose.exe`, keeping `recompose-engine.exe` and the `models` folder next to it. The engine protocol is unchanged (version 13).

You need an Anthropic API key from [console.anthropic.com](https://console.anthropic.com). Requests are billed to that account; a short request costs a fraction of a cent.

## 2. Try the demo

| # | Do this | You should see |
|---|---|---|
| 1 | Press **Ctrl+J** (or **Assistant** in the top bar). | A panel on the right with "Add your Anthropic API key to use the assistant". |
| 2 | **Open settings**, paste the key, **Save**, then **Test connection**. | "✓ Connected". The key is in Windows Credential Manager (generic credential `RECompose/anthropic-api-key`), not in the settings file. If your key belongs to a workspace, fill **Workspace ID** first. |
| 3 | Type "he change the key of song 2 to F" and press Enter. | "Working on it", then `✓ Sunday 26 Oct: … to F.` and a short reply naming the key it came from. The sidebar shows the new key. |
| 4 | "On song 1 can we just do verse 1, chorus, verse 2, chorus and then end?" | `✓ … arrangement: 1. Verse 1 · 2. Chorus 1 · 3. Verse 2 · 4. Chorus 2 · 5. Ending.` The entry now plays a custom arrangement for this service; the song's variants are unchanged. The reply says which chorus it used. |
| 5 | "Make the last chorus go up a step and have the drums drop out in verse 1." | The arrangement gets a +2 key lift and a Drums mute. |
| 6 | Press **Undo** in the panel. | Everything the last message changed is undone at once, and `↶ Undid: …` appears. Ctrl+Z treats it as one step too. |
| 7 | "Find Gratitude and add it after the opener." / "Are we ready for Sunday?" | It searches the library and adds the song as song 2. / It runs the service check and summarises it. |
| 8 | Go to **Perform** (Ctrl+P), open the panel with Ctrl+J and ask for a change. | A **CONFIRM CHANGE** card with **Apply** and **Discard**. Nothing changes until you press Apply; a change to the playing song takes effect after the current section. |
| 9 | Untick **Allow changes during Perform** in the panel's settings and ask again in Perform. | The reply says changes are not allowed in Perform. |
| 10 | Paste a wrong key, or unplug the network, and send a message. | "The Anthropic API key was rejected. Update it in Settings > Assistant." / "Assistant error: Couldn't reach the Anthropic API (…)." |
| 11 | **LOG** tab, **Assistant** chip. | A line per change the assistant made. |

## 3. Please tell me

- Whether its answers pick the sections you meant (which chorus, which verse).
- What you asked for that it could not do.
- Whether the confirmation cards in Perform are clear enough to use mid-song.

## 4. Known gaps in this build

- The conversation is kept until you press **New conversation** or close the app; earlier conversations are archived to `%APPDATA%\RECompose\assistant` as text but not reopened.
- It cannot add or delete sections yet (renaming and changing their type work).
- Settings › Assistant is in the panel (the gear), not in the Settings window.
- Perform has no Assistant button: use Ctrl+J.

## 5. If something goes wrong

Send me `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log` and the text of the conversation.
