# Milestone 9: Assistant

**Goal:** the MD can type or dictate a change ("make Come Ye Sinners short: V1, C, V2, C, end") and the assistant makes it with the same edits as the editor, one undo step per message. It is optional: without an API key or internet RECompose works as before, and playback never calls the API.

**Status:** done (29 Oct 2026). How to try it: [M9 test guide](../testing/M9-test-guide.md).

**Starting point:** milestone 8. Every manual edit goes through `MainViewModel.Commit` (one undo step, autosave, the live plan follows per ARR-90). The quick order (`QuickOrder`, ARR-60) resolves section shorthand. The LOG tab already has an Assistant chip.

## Decisions for this milestone

1. **The tools are Core.** `RECompose.Core.Assistant` holds the system prompt (AS-11), the state snapshot (AS-12), the tool schemas and the tools themselves (AS-30). A tool reads an `AssistantWorld` (services, songs, library) and returns data or an `AssistantChange` built with the existing Core edits (`ServiceEdit`, `ArrangementEdit`, `SectionList`, `QuickOrder`). It is pure and unit-tested; the app applies a change through `Commit` like a manual edit.
2. **The official Anthropic C# SDK** (`Anthropic` on NuGet) sends the requests, with a manual tool loop of at most 10 rounds (AS-10): 60 s per call, 2 retries for 429/5xx, the workspace id as an extra header. The model is the one in Settings (default `claude-sonnet-5-5`). Tests give the client a fake HTTP handler, so they run offline.
3. **Undo groups.** Every change of one user message folds into one undo step (`UndoStack.Group`, AS-40). The panel's Undo reverts the latest assistant group while it is the latest step.
4. **Perform asks first.** In Perform a mutating tool call becomes a confirmation card (Apply, Discard) and the tool returns `pending_confirmation`; the result goes to the model with the next message (AS-45). "Not allowed" returns an error instead.
5. **The key lives in the OS credential store** (Windows Credential Manager, macOS Keychain), never in settings.json (AS-1).

## Definition of done: the demo

1. Settings › Assistant: paste a key, Test connection shows ✓ Connected. Without a key the panel shows the setup card.
2. Ctrl+J opens the panel. "he change the key of song 2 to F" gives a ✓ row `Sunday 26 Oct: Champion to F.` and a short reply.
3. "On Champion can we do verse 1, chorus, verse 2, chorus and end" sets a custom arrangement for this service and names the chorus it used.
4. Undo in the panel reverts the whole message; the log shows `Assistant: …` lines.
5. In Perform the same request shows a card; Apply makes the change, Discard does not.
6. A rejected key, no network and other API errors show the AS-50 bubbles.

## Work packages

| # | Package | Exit |
|---|---|---|
| WP1 | Core: prompt, snapshot, tools, undo groups | Unit tests per tool rule |
| WP2 | App: client and tool loop, credential store, settings, panel, Ctrl+J, Perform cards, undo, log | Presentation tests with a fake API |
| WP3 | Docs, test guide | |

## As built

- The system prompt is the same for the whole conversation; the mode line and the state snapshot go with every user message and every round of tool results. Editing the system prompt or earlier messages mid-conversation would invalidate the model's earlier thinking blocks.
- AS-20's last 40 messages: older messages are dropped from a message the user typed onwards, and the thinking blocks of the turns kept are removed at that point (text and tool calls stay), so the history stays append-only afterwards. A failed request (an error bubble) leaves the history as it was before the message.
- Settings › Assistant is the gear in the panel: key (Save stores it, an empty field keeps the stored one), workspace id, model, "Allow changes during Perform, with confirmation", Test connection (it fetches the model's details) and the privacy note.
- The panel is a 400 px drawer over the right edge in both Prepare and Perform, opened with Ctrl+J or the top-bar Assistant button.
- A Perform card's Apply runs the tool again against the state at that moment, so edits made meanwhile are kept; the card's outcome (applied, discarded, undone) is told to the model with the next message.
- The panel's Undo reverts the latest assistant group while it is still the latest undo step; otherwise it asks to undo the later edits first.
- Each change keeps a version of the song or service it replaces first (`DataStore.KeepVersionNow`, DM-90), and logs an `Assistant: …` line.
- Not built: `add_section` and `delete_section` (sections carry arrangement references and guide-call mapping that the editor's own flows handle).

## Known gaps

- The conversation lives in memory; New conversation archives its rows to `assistant/<id>.json`, which is not reopened.
- `add_section` and `delete_section` tools (AS-30).
- Perform has no Assistant button (Ctrl+J only).
- No streaming: the reply appears when the whole tool loop is done, with "Working on it" meanwhile.
