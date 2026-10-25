# Testing milestone 4 on your PC

This build adds services. You build Sunday's service from the library, give each entry its own arrangement, key, tempo, count-in and transition into the next song, and Play runs it from song to song. Key and tempo are audible (processed live), count-ins and click bars click, pads play, and the next song starts by itself, or on Play, with the transition you chose. Everything from milestones 1 to 3 still works ([M1](M1-test-guide.md), [M2](M2-test-guide.md), [M3](M3-test-guide.md)).

## 1. Get the build

GitHub → **Actions** → the latest green **CI** run on `main` → **Artifacts** → `RECompose-win-x64`. Unzip it and run `RECompose.exe`, keeping `recompose-engine.exe` and the `models` folder next to it. This build changes the engine protocol (version 7), so use the new `recompose-engine.exe` with the new app; an old engine left running is replaced when the app asks it to shut down, or end it in Task Manager.

Use four imported songs that have sections (milestone 2), one of them with variants (milestone 3), and a folder of twelve pad files named by key (`OB Pad C.wav`, `OB Pad Db.wav` … `OB Pad B.wav`).

## 2. Try the demo

| # | Do this | You should see |
|---|---|---|
| 1 | Open the app with no service. | The sidebar says **Create a service for this Sunday**. **New service…**: name "Sunday 26 Oct", a date and 9:00. The empty service says **Add songs from the library (drag them here)**. Drag four songs in (one of them twice), or right-click a library song → **Add to Sunday 26 Oct**. Each row shows the number, title, key, variant and length; the block header shows `4 songs · 18:40`. |
| 2 | Click entry 1, then **Load** (or double-click it). Press **KEY −** in the header (or Ctrl+↓). Press Play. | The ENTRY tab starts with `Editing: … · service entry 1`, with a **not loaded** tag and a **Load** button while another entry is loaded. The header reads `A (−1) · 72 BPM · 3/4 · Demo` and a **Live key** tag shows. The music is a step lower; the click and guide are unchanged. Press **TEMPO −** a few times: the song gets slower, the pitch stays, the remaining time grows, and the song bars stay the same. |
| 3 | Change something in the variant's order (add a section chip) while the entry plays a variant. | A question: **this service only** or **all services**. Choose **This service only**: the row says `Custom for this service`, the library song's variant is unchanged, and **Revert to "Demo"** and **Save as variant…** appear. Revert brings the variant back. |
| 4 | Entry 3: **Count-in** → **Click, 2 bars**. Press Play. | NOW reads `COUNT-IN` with a beat countdown; two bars of click on the Cue output, then the first section on its downbeat. |
| 5 | Click the chip between entries 1 and 2 → **Pad**, key D♭, click on, 72 BPM → **Save**. Play entry 1 to its end. | The chip reads `Pad in D♭ · click 72`. At the tail the pad fades in with the click, the pill reads `TRANSITION`, and NEXT reads `NEXT SONG` with entry 2's title and `Press Play`. **Play** starts entry 2 on a click downbeat; the click stops, the pad fades out over 4 s, and the header and the setlist now show entry 2. |
| 6 | Entry 2 → 3: **Segue**, gap 0. Entry 3 → 4: **Crossfade**, 6 s. Entry 4: **Stop**, with **Auto-advance** on in the service menu. | Entry 3 starts by itself as entry 2's tail begins, and the tail fades under it. Entry 4 starts while entry 3 fades out over its last 6 s. When entry 4 ends the next entry is cued; with Auto-advance off the playhead returns to the start of the same entry. **⏭** loads the next entry (asking first while playing); **⏮** at the start of an entry selects the previous one. |
| 7 | **Library › Pads**: import the pad folder (drag it in, or **Import**). Press E♭ on the sidebar pads, then G. Turn **Follow song** on and play a song whose arrangement has a key lift (`C+2`). Add **Click bars 4** and **Pad bars 8** to an arrangement. | The folder appears under **Pads**, not as a song. E♭ fades in; G crossfades. With Follow song the pad moves up two semitones at the lift. The click bars click and the pad bars sound the pad. |
| 8 | **MIXER**: **Save as new…** "Rehearsal – band live", mute Keys 1. Give entry 2 that preset in the ENTRY tab. | Entry 2 plays with Keys 1 muted; entry 3 keeps **Default**. The **#** button on a strip stops that track from following the key. |
| 9 | **⋯ → Duplicate service…** as "Sunday 2 Nov". Close and reopen the app. | The copy has the same entries, custom arrangements, keys, tempos, count-ins and transitions. The services, the selection and the loaded entry come back. |

## 3. Please tell me

- Whether any transition clicks, leaves a gap or starts late, and which kind (the engine counts the start on the sample, but real devices and the pad files are the things I cannot hear).
- Whether live key and tempo sound good enough on your stems, and whether the CPU meter in your system stays comfortable with all tracks shifted (milestone 6 pre-renders them).
- Anything in the sidebar, the ENTRY tab's entry settings or the transition popover that was hard to find or reach.

## 4. Known gaps in this build

- There is no **Pad output** selector in Settings › Audio yet: pads play on Main.
- **Crossfade** fades the outgoing song over its own last seconds; it does not play past the song's end.
- **Hold** and **until cued** repeats are still stored only (milestone 5), and the **spoken count** of a count-in or click bars needs the voice that arrives with voice control (milestone 7): the click is audible, the "one, two, three, four" is not.
- Key and tempo are processed live and are not pre-rendered (milestone 6), so the **Live key** tag always shows for a shifted entry.
- There is no Perform view, no locks and no service check yet.

## 5. If something goes wrong

- The log is in `%APPDATA%\RECompose\logs\app-YYYY-MM-DD.log`. Lines starting "Transport" show each load and each transition; "Services" shows each edit.
- If playback stops at a transition, press Play again and send me the log.
