# Perform view

The screen the MD reads from the keyboard stand (UI-40). Design it at the minimum window, 1280 × 760; larger windows only add space.

## Layout at 1280 × 760

| Region | Height | Contents |
|---|---|---|
| Status row | 60 px (`size-status-bar`) | Prepare key (`HoldButton`, `touch` size: hold to leave Perform) · service name over "Perform · clock" · LOCK switch (`SegmentedControl`, perform size, open and closed padlocks: Rehearsal / Service lock) · `StatusIndicator`s as buttons that open their detail: AUDIO, MIDI, VOICE with level, RENDER, CPU · divider · PANIC (`Button` danger) isolated at the right edge |
| Body | the rest | setlist rail 248 px wide on the left; main column with `NowBlock` and a 280 px readouts column (Song bar, Elapsed, Remaining stacked, one value each, never wrapped), `NextCard`, plan strip of `Tile`s (76 px), mini timeline (`Lane`) at its natural height |
| Key deck | 108 px, `bg-app` | `KeyGroup`s: TRANSPORT (restart, Stop hold, Play/GO yellow, Next song hold) · LIVE (Vamp, Repeat, Move on, End, Cancel) · JUMP TIMING (`Select`, perform size) · KEY and TEMPO (`NumberField`, perform size) |

The window height is the screen height: the body takes what is left after the status row, any banner and the deck. Extra height goes to the NOW row (the block centres in it), never to an empty band between the strip and the timeline.

The deck tightens below 1400 px wide so it fits at 1280: live keys 84 → 76 px, key and tempo steppers 52 → 44 px, 16 → 12 px between groups, timing select 160 → 140 px. At 1280 that is transport 280, live 412, timing 140, key and tempo 176 each, total 1264 px including the 16 px margins. Pads open from the deck in a popover rather than taking a permanent row (the hold screen shows them in place of jump timing, key and tempo).

## What the MD sees in one second

1. **What is playing**: section badge and name in `now`, bar in section in `readout`, the yellow NOW marker on the current tile.
2. **What comes next and when**: the `NextCard` target in `next` and the countdown in `readout`.
3. **Whether anything is pending**: the NEXT card ground turns `state-pending-subtle`, the tag reads NEXT · VOICE · PENDING (or PEDAL, MANUAL, KEYS), the target tile has a dashed blue outline, the skipped tiles are struck through, the Cancel key's LED is lit.

## Rules

- One yellow key on screen: Play (Pause while playing; GO in a hold).
- In a hold the NOW block reads Hold, the readouts column shows "Holding for" with the timer (once), and the NEXT card names what GO starts with the countdown "on GO". The GO key is the yellow key.
- Live keys keep their positions in every state; a disabled key goes flat rather than disappearing.
- Locked actions in Service lock use `HoldButton` (Stop, Next song, loading a song from the rail) or the tap-twice armed tile (plan jumps).
- Voice: while a jump is pending, the NEXT card carries what was heard (heard “skip to the last chorus”); it is not repeated elsewhere. Ignored phrases show for 3 s above the mini timeline: ignored: “let us pray” · not a command.
- Every number on screen agrees: the song bar readout, the bar in section, the countdown, the tile progress and the playhead in the mini timeline describe the same moment (the `Lane` places markers in bar time for this reason).
- Banners (device lost, rendering, voice input missing) push the body down; they never cover the NOW block.
- NOW grows to 120 px when the window is at least 860 px tall.
- Extra-large text: NOW grows to 120 px, readouts to 32 px, and the setlist rail collapses to a 56 px strip with song numbers.
