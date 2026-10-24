RECompose is a live multitrack playback app for a church music director (MD), on Windows and macOS. This system, **Instrument**, makes it look and behave like a piece of stage gear: flat dark panels and thin rules like show-control software, keys you could press, and one REC yellow key for the action that matters most. Everything here serves two jobs: dense, calm editing at a desk (Prepare) and reading the screen from 2–3 m while playing keys (Perform).

## Principles

- **Reliable before expressive.** Nothing moves, glows or changes colour unless it tells the MD something. If a detail could distract during a prayer, remove it.
- **One yellow key.** `accent` (REC yellow, #FCD937) fills the primary key (Play, or GO in a hold) and marks the current position (NOW tag, current-tile line, playhead). Never use it for large areas, decoration or a second primary action in the same view.
- **Colour never works alone.** Every state also has a word, an icon or a shape: section badges carry letters, LEDs come with a changed label, indicators change shape (dot, triangle, square) and text, skipped tiles are struck through.
- **Hardware manners.** Keys are raised, flat-coloured and labelled like a mixer: a small uppercase group label above, the word on the key, the shortcut letter in the corner, an LED when the key can be on.
- **Same on both platforms.** Bundle the three fonts in `fonts/` and draw every icon from this set, so Windows and macOS render identically. Use the native title bar, menus and file pickers; everything inside the window comes from these tokens.

## Voice and copy

- Use the band's words: Verse 2, Chorus 3, Vamp, Move on, End, Ending, Count-in, Pad. Section names are occurrence labels (Chorus 1, Chorus 2), never a bare "Chorus" when there are several.
- Keys carry a verb or the musical word: Vamp, Repeat, Move on, End, Cancel, GO, PANIC. Prepare buttons are sentence case: "Detect from guide", "Save as variant…", "Render now".
- Group labels, status words and tags are short capitals in `label` style: TRANSPORT, NOW, NEXT · VOICE · PENDING, HOLD, READY.
- Name what happened, then what RECompose is doing: "Main audio device lost. Playing on MacBook Pro Speakers from the same position." Never "Error" alone, never apologise.
- Toasts are one line with an undo: "Key A → B♭ · Undo", "Pedal: GO", "Marked Chorus at chart bar 11 · Undo".
- Keys are written with real accidentals: D♭, F♯, B♭. The bundled `REC Accidentals` font draws ♭ ♮ ♯ in every text style.
- No emoji, no exclamation marks, no "Oops".

## Colour

Three themes: **Dark** (first; the default for Perform and for stage use), **Light** (Prepare at a desk, following the system appearance) and **High contrast** (follows the OS high-contrast setting: every text colour 7:1 or better, borders 3:1).

- Grounds, darkest to lightest in Dark: `bg-inset` (wells: key/tempo windows, fields, meter backs) → `bg-app` (window, key deck) → `bg-surface` (panels) → `bg-raised` (current row and tile, menus, popovers). Separate regions with 1 px `border` rules, not shadows.
- Text: `text-primary` for content and key labels, `text-secondary` for second lines and hints, `text-label` for uppercase group labels, `text-key-hint` for the shortcut letter on a key, `text-dim` for played and skipped items. Every pair passes 4.5:1 in all three themes on the grounds its token note names.
- Keys: `key` face, `key-edge` outline and lower edge, `key-hover`, `key-pressed`. In Dark the key face is REC charcoal (#333333).
- States: `state-playing` (green), `state-pending` (blue, a queued jump), `state-hold` (yellow; ochre as text in Light), `state-warning` (amber), `state-error` (red). Use the `-subtle` grounds only behind their own state (the pending NEXT card, banners).
- In Light, yellow is a fill only; as text or a thin mark use `accent-text` (ochre), and the playhead is charcoal (`playhead`).
- Focus: a solid 2 px `focus-ring` outline with a 2 px offset on every focusable control. Yellow in Dark and High contrast, charcoal in Light.

## Sections

Every section type has a fill **and** letters, and the letters always carry the meaning. Fills are the same in every theme; text on them is `text-on-section`.

| Type | Badge | Token |
|---|---|---|
| Intro | I | `section-intro` |
| Verse | V1, V2… | `section-verse` |
| Pre-chorus | PC | `section-pre-chorus` |
| Chorus | C1, C2… (occurrence) | `section-chorus` |
| Post-chorus | PoC | `section-post-chorus` |
| Bridge | B, B2 | `section-bridge` |
| Interlude · Instrumental · Turnaround | IL · IN · TU | `section-interlude` |
| Vamp · Breakdown · Build | VA · BR · BU | `section-vamp` |
| Tag | T | `section-tag` |
| Outro | O | `section-outro` |
| Ending | E | `section-ending` |
| Other | diamond icon | `section-other` |
| Click bars | metronome icon | `section-click` |
| Pad bars | wave icon | `section-pad` |
| Hold | pause icon | `section-hold` (= `accent`) |

The verse family is cool (blues, teal), the chorus family warm (orange, amber), the bridge violet. Similar types share a fill and differ by letters, matching the MIDI note map (AUTO-21). Use the `SectionBadge` component everywhere a section is named.

## Type

- **Hanken Grotesk** (variable, 100–900) for everything you read. **Martian Mono** (variable width and weight) for every number that changes while playing: bars, times, countdowns, key and tempo, CPU, log lines. Hanken has no tabular figures, so live numbers are always mono; that keeps layouts from shifting as digits change.
- Perform: `now` (96 px, NOW label), `next` (56 px, NEXT target), `readout-xl` (32 px, song bar and time), `readout` (26 px, bar in section and countdown), `body-perform` (16 px, notes and hints).
- Prepare: `title` (20 px), `heading` (16 px), `body` (14 px), `body-strong`, `meta` (12 px), `label` (12 px capitals, +0.08em), `value`, `mono` (13 px), `mono-small` (12 px).
- Nothing is smaller than `key-hint` (11 px), and that is only the shortcut letter on a key. Perform labels are 12 px or larger.
- In small mono text (lists, lanes, chips) set Martian Mono's width axis to 87.5 (`font-variation-settings: "wdth" 87.5`).
- Settings › Display › Extra-large text scales the Perform styles by 1.25 (NOW 120 px) and collapses the setlist rail.

## Space, shape and depth

- Spacing steps `space-1` (2 px) to `space-8` (32 px). Keys 8 px apart (`space-4`), key groups 16 px (`space-6`), group label to keys 6 px (`space-3`), Perform main area padding 24 px (`space-7`).
- Radii: `radius-s` (4 px) for badges, chips and lane blocks; `radius-m` (7 px) for keys and fields; `radius-l` (10 px) for panels, popovers and dialogs.
- Sizes: Perform keys `size-key` (56 px tall, at least 52 px wide); Perform keys outside the deck (Cancel jump, the Prepare key) use the `touch` size, `size-target` (44 px), and nothing pressable in Perform is smaller. Prepare buttons and fields are `size-key-compact` (32 px). The Perform deck tightens below 1400 px wide (see perform-view.md).
- Depth is physical, not decorative: keys use `shadow-key` (top highlight, 1 px dark lower edge, soft drop); a pressed key drops 1 px with `shadow-key-pressed`. Popovers and dialogs use `elevation-popover` and `elevation-dialog` with a border. No gradients, no glows, no coloured shadows.

## Motion

Three things move because they carry time: the pending-jump progress bar, the press-and-hold fill (`motion-hold`, 600 ms) and the section progress. Keys respond in `motion-fast` (120 ms); popovers and toasts enter in `motion-normal` (200 ms) with `ease-out`. When the OS asks for reduced motion, use `motion-reduced`: transitions become instant, the "already next" pulse is skipped, and the three time-carrying progress bars keep moving because they are information.

## Iconography

Use only the RECompose set (`Icon` component; SVG files in Assets › Icons): 24 px grid, 2 px stroke, round caps and joins, solid transport glyphs. 18 px on Perform keys, 16 px in Prepare, 12–14 px in pills, chips and tags. Icons sit beside words except on transport and stepper keys, which always have an accessible name. No emoji and no text characters standing in for icons (✓, ×, ⟳).

## Brand marks

There is no RECompose logo yet. Set the name in Hanken Grotesk: **REC** at weight 800 followed by **ompose** at 500, in `text-primary`. Don't use the REC church emblem inside the app unless the church provides it as a file.

## Accessibility

- Text 4.5:1, large text and meaningful graphics (borders of fields, focus ring, LEDs on yellow, meters) 3:1, checked in all three themes.
- Every Perform control is at least 44 × 44 pt; every live action also has a keyboard, pedal or voice path.
- Plan tiles, setlist rows and lane blocks are buttons with full accessible names ("Chorus 3, plus 2, pending jump target"). Toggling keys use `aria-pressed`, the lock and mode switches are radio groups, editor tabs are a tab list.
- Guarded actions (Stop, Next song, Load) use `HoldButton`; tile jumps in Service lock need a second tap.
