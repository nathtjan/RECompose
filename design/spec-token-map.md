# Spec token map

The UI spec (02-ui-structure, UI-90) names tokens like `color.state.live`. This is where each one lives in this system. CSS custom properties are the token name with `--` (`--state-pending`); type styles are classes of the same name.

| Spec name | This system |
|---|---|
| `color.bg.app` | `bg-app` |
| `color.bg.surface` | `bg-surface` |
| `color.bg.raised` | `bg-raised` |
| `color.bg.inset` | `bg-inset` |
| `color.border` | `border` |
| `color.divider` | `divider` |
| `color.text.primary` | `text-primary` |
| `color.text.secondary` | `text-secondary` |
| `color.text.disabled` | `text-disabled` |
| `color.text.onAccent` | `text-on-accent` |
| `color.accent` | `accent` |
| `color.accent.subtle` | `accent-subtle` |
| `color.state.playing` | `state-playing` |
| `color.state.ready` | `state-ready` |
| `color.state.hold` | `state-hold` |
| `color.state.pending` | `state-pending` |
| `color.state.warning` | `state-warning` |
| `color.state.error` | `state-error` |
| `color.state.success` | `state-success` |
| `color.section.intro` | `section-intro` |
| `color.section.verse` | `section-verse` |
| `color.section.preChorus` | `section-pre-chorus` |
| `color.section.chorus` | `section-chorus` |
| `color.section.postChorus` | `section-post-chorus` |
| `color.section.bridge` | `section-bridge` |
| `color.section.interlude` | `section-interlude` |
| `color.section.vamp` | `section-vamp` |
| `color.section.tag` | `section-tag` |
| `color.section.outro` | `section-outro` |
| `color.section.ending` | `section-ending` |
| `color.section.other` | `section-other` |
| `color.section.click` | `section-click` |
| `color.section.pad` | `section-pad` |
| `color.section.hold` | `section-hold` |
| `color.waveform` | `waveform` |
| `color.waveform.muted` | `waveform-muted` |
| `color.meter.low` | `meter-low` |
| `color.meter.mid` | `meter-mid` |
| `color.meter.high` | `meter-high` |
| `color.playhead` | `playhead` |
| `color.loop` | `loop` |
| `color.pendingMarker` | `pending-marker` |
| `elevation.popover` | `elevation-popover` |
| `elevation.dialog` | `elevation-dialog` |
| `motion.fast` | `motion-fast` |
| `motion.normal` | `motion-normal` |
| `motion.reduced` | `motion-reduced` |
| `type.display` | `.now (type style)` |
| `type.title` | `.title (type style)` |
| `type.body` | `.body (type style)` |
| `type.meta` | `.meta (type style)` |
| `type.label` | `.label (type style)` |
| `type.mono` | `.mono (type style)` |
| `space.1…8` | `space-1` … `space-8` |
| `radius.s/m/l` | `radius-s`, `radius-m`, `radius-l` |
| `motion.fast/normal` | `motion-fast`, `motion-normal` (and `motion-hold`) |

Added by this system (not in UI-90): `bg-*` already listed, plus `control-border`, `key`, `key-hover`, `key-pressed`, `key-edge`, `text-label`, `text-key-hint`, `text-dim`, `text-on-section`, `accent-edge`, `accent-hover`, `accent-text`, `state-pending-subtle`, `state-pending-track`, `state-warning-subtle`, `state-error-subtle`, `scrim`, `led-off`, `focus-ring`, `loop-edge`, `size-*`, `ease-*`.

## Component inventory

| UI-90 component | Component here |
|---|---|
| Button (primary, secondary, ghost, danger, hold-to-confirm) | `Button` (variants), `HoldButton` |
| Icon button | `IconButton` |
| Segmented control | `SegmentedControl` |
| Toggle | `Toggle` |
| Checkbox | `Checkbox` |
| Select | `Select` |
| Text field | `TextField` |
| Number field | `NumberField` |
| Search field | `SearchField` |
| Slider | `Slider` |
| Fader | `Fader` |
| Meter | `Meter` |
| Status pill | `StatusPill` |
| Status indicator (dot + label) | `StatusIndicator` |
| Chip (transition, tag) | `Chip` |
| Tile (plan item: played/current/next/pending/upcoming; section: normal/selected) | `Tile` (plus skipped and armed) |
| Card (NEXT: normal/pending/hold) | `NextCard` (plus repeating and next song) |
| Lane | `Lane` |
| Ruler | `Ruler` |
| Popover | `Popover` |
| Dialog | `Dialog` |
| Toast | `Toast` |
| Banner | `Banner` |
| Tab bar | `TabBar` |
| Progress bar / ring | `Progress` |
| Waveform | `Waveform` |
| Message bubble (user/assistant/tool/error/confirmation) | `MessageBubble` |

Intentional additions: `SectionBadge` (the letters-plus-colour mark every section needs, UI-1.5), `NowBlock` (the Perform NOW readout, LIVE-10), `KeyGroup` (the labelled key rows of the deck), `Icon` (the drawn icon set, for parity across platforms) and `Led` (the key and indicator light).
