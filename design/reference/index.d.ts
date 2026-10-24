import type * as React from 'react';

export type IconName = 'play' | 'pause' | 'stop' | 'restart' | 'next' | 'go' | 'plus' | 'minus' | 'close' | 'check' | 'chevron-down' | 'chevron-right' | 'more' | 'drag' | 'search' | 'undo' | 'settings' | 'folder' | 'loop' | 'repeat' | 'vamp' | 'end' | 'lock' | 'unlock' | 'panic' | 'mic' | 'pedal' | 'keyboard' | 'midi' | 'speaker' | 'mute' | 'headphones' | 'cpu' | 'assistant' | 'metronome' | 'wave' | 'hold' | 'diamond' | 'warning' | 'error' | 'info' | 'rendering' | 'transition';
export interface IconProps { name: IconName; size?: number;
  /** Give a label only when the icon stands alone and carries meaning */
  label?: string; className?: string }
export declare function Icon(props: IconProps): React.ReactElement;

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  /** secondary (default) · primary = REC yellow, one per view · ghost · danger = PANIC only */
  variant?: 'secondary' | 'primary' | 'ghost' | 'danger';
  /** perform = 56 px key · touch = 44 px (Perform, outside the deck) · compact = 32 px (Prepare only) */
  size?: 'perform' | 'touch' | 'compact';
  /** LED on a Perform key; 'off' shows the unlit LED */
  led?: 'off' | 'accent' | 'pending' | 'playing' | 'warning' | 'error';
  /** Keyboard shortcut printed on the key: 'V', 'Esc' */
  hint?: string;
  icon?: IconName;
  /** Sets aria-pressed for toggling keys (Vamp) */
  pressed?: boolean;
  /** Forces the pressed look (e.g. while a pedal holds it) */
  active?: boolean;
  width?: number | string;
}
export declare function Button(props: ButtonProps): React.ReactElement;

export interface IconButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  icon: IconName;
  /** Accessible name and tooltip. Required. */
  label: string;
  size?: 'perform' | 'touch' | 'compact';
  variant?: 'secondary' | 'primary' | 'ghost' | 'danger';
  active?: boolean;
  iconSize?: number;
}
export declare function IconButton(props: IconButtonProps): React.ReactElement;

export interface HoldButtonProps {
  label: string;
  onConfirm?: () => void;
  icon?: IconName;
  /** Show only the icon (label stays the accessible name) */
  iconOnly?: boolean;
  /** Default 600 (motion-hold) */
  holdMs?: number;
  size?: 'perform' | 'touch' | 'compact';
  disabled?: boolean;
  width?: number | string;
}
export declare function HoldButton(props: HoldButtonProps): React.ReactElement;

export interface KeyGroupProps { label: string; ariaLabel?: string; children?: React.ReactNode; className?: string }
export declare function KeyGroup(props: KeyGroupProps): React.ReactElement;

export interface SegmentedOption { value: string; label: string; icon?: IconName; led?: 'accent' | 'pending' | 'playing' }
export interface SegmentedControlProps { label: string; options: SegmentedOption[]; value: string; onChange?: (value: string) => void; size?: 'perform' | 'compact' }
export declare function SegmentedControl(props: SegmentedControlProps): React.ReactElement;

export interface ToggleProps { id?: string; label: string; checked: boolean; onChange?: (checked: boolean) => void; disabled?: boolean }
export declare function Toggle(props: ToggleProps): React.ReactElement;

export interface CheckboxProps { id?: string; label: string; checked: boolean; onChange?: (checked: boolean) => void; disabled?: boolean }
export declare function Checkbox(props: CheckboxProps): React.ReactElement;

export interface SelectOption { value: string; label: string }
export interface SelectProps { id: string; label?: string; options: SelectOption[]; value: string; onChange?: (value: string) => void; size?: 'perform' | 'compact'; disabled?: boolean; hint?: string; error?: string }
export declare function Select(props: SelectProps): React.ReactElement;

export interface TextFieldProps { id: string; label?: string; value?: string; onChange?: (value: string) => void; placeholder?: string; hint?: string; error?: string; disabled?: boolean }
export declare function TextField(props: TextFieldProps): React.ReactElement;

export interface NumberFieldProps { label: string; value: number; onChange?: (value: number) => void; step?: number; min?: number; max?: number;
  /** Text shown in the window, e.g. v => 'A −1' */
  format?: (value: number) => string; size?: 'perform' | 'compact'; warn?: boolean; showLabel?: boolean }
export declare function NumberField(props: NumberFieldProps): React.ReactElement;

export interface SearchFieldProps { id: string; label?: string; value?: string; onChange?: (value: string) => void; placeholder?: string }
export declare function SearchField(props: SearchFieldProps): React.ReactElement;

export interface SliderProps { id: string; label: string; value: number; onChange?: (value: number) => void; min?: number; max?: number; step?: number; format?: (value: number) => string }
export declare function Slider(props: SliderProps): React.ReactElement;

export interface FaderProps { id: string; label: string; group?: string;
  /** dB, −60 (−∞) to +6 */
  value: number; onChange?: (db: number) => void; meterDb?: number; peakDb?: number; children?: React.ReactNode }
export declare function Fader(props: FaderProps): React.ReactElement;

export interface MeterProps { db: number; peakDb?: number; segments?: number; orientation?: 'vertical' | 'horizontal'; label?: string }
export declare function Meter(props: MeterProps): React.ReactElement;

export interface WaveformProps { bars?: number; width?: number; height?: number; seed?: number; envelope?: (x: number) => number;
  /** Muted ranges as fractions [from, to] */
  muted?: [number, number][]; label?: string }
export declare function Waveform(props: WaveformProps): React.ReactElement;

export interface StatusPillProps { state: 'ready' | 'playing' | 'hold' | 'transition' | 'loading'; children?: React.ReactNode }
export declare function StatusPill(props: StatusPillProps): React.ReactElement;

export interface StatusIndicatorProps { label: string; state?: 'ok' | 'warning' | 'error' | 'off'; detail?: string;
  /** 0–1 input level (Voice) */
  level?: number; onClick?: () => void }
export declare function StatusIndicator(props: StatusIndicatorProps): React.ReactElement;

export interface ChipProps { kind?: 'tag' | 'transition'; icon?: IconName; selected?: boolean; onClick?: () => void; children?: React.ReactNode }
export declare function Chip(props: ChipProps): React.ReactElement;

export interface ProgressProps { value: number; variant?: 'bar' | 'ring'; tone?: 'neutral' | 'text' | 'pending' | 'accent' | 'warning'; thin?: boolean; size?: number; showValue?: boolean; label?: string }
export declare function Progress(props: ProgressProps): React.ReactElement;

export type SectionType = 'intro' | 'verse' | 'preChorus' | 'chorus' | 'postChorus' | 'bridge' | 'interlude' | 'instrumental' | 'turnaround' | 'vamp' | 'breakdown' | 'build' | 'tag' | 'outro' | 'ending' | 'other' | 'click' | 'pad' | 'hold';
export interface SectionBadgeProps { type: SectionType; number?: number; size?: 's' | 'm' | 'l' | 'xl';
  /** Override the letters, e.g. a custom-named section */
  letters?: string; muted?: boolean; outline?: boolean; title?: string | false }
export declare function SectionBadge(props: SectionBadgeProps): React.ReactElement;

export interface TileProps { type: SectionType; number?: number; label?: string;
  state?: 'played' | 'current' | 'next' | 'pending' | 'upcoming' | 'skipped' | 'armed' | 'selected';
  /** ×2, +2, 1–8, or bar count in the drawer */
  meta?: string; progress?: number; passes?: { done: number; total: number }; kind?: 'plan' | 'section'; flex?: number; onClick?: () => void }
export declare function Tile(props: TileProps): React.ReactElement;

export interface NowBlockProps { type: SectionType; number?: number; label?: string; state?: 'playing' | 'ready' | 'hold' | 'transition' | 'loading';
  bar?: number; bars?: number; pass?: string; note?: string; xl?: boolean }
export declare function NowBlock(props: NowBlockProps): React.ReactElement;

export interface NextCardProps { mode?: 'normal' | 'pending' | 'hold' | 'repeating' | 'nextSong';
  source?: 'ARRANGEMENT' | 'AS WRITTEN' | 'VOICE' | 'VOICE?' | 'PEDAL' | 'MANUAL' | 'KEYS';
  type?: SectionType; number?: number; target?: string; keyShift?: string; countdown?: string; holdTime?: string;
  heard?: string; hint?: string; progress?: number;
  /** Shown in pending mode; pass false to hide the key */
  onCancel?: (() => void) | false }
export declare function NextCard(props: NextCardProps): React.ReactElement;

export interface LaneItem { type: SectionType; number?: number; label?: string; bars?: number; meta?: string; muted?: string; kind?: 'section' | 'hold' | 'click' | 'pad'; onClick?: () => void }
export interface LaneProps { items: LaneItem[]; selected?: number;
  /** Fractions 0–1 of the total bars (not of the pixel width): drawn inside the block they fall in */
  playhead?: number; pendingAt?: number; loop?: [number, number]; label?: string;
  /** Bar ticks inside the blocks, numbered every rulerStep bars (default 4) */
  ruler?: boolean; rulerStep?: number; currentBar?: number }
export declare function Lane(props: LaneProps): React.ReactElement;

export interface RulerProps { bars: number; step?: number; current?: number }
export declare function Ruler(props: RulerProps): React.ReactElement;

export interface ToastProps { children?: React.ReactNode; icon?: IconName; tone?: 'pedal' | 'voice'; actionLabel?: string; actionIcon?: IconName; onAction?: () => void }
export declare function Toast(props: ToastProps): React.ReactElement;

export interface BannerProps { tone?: 'info' | 'warning' | 'error'; title: string; children?: React.ReactNode; actions?: React.ReactNode }
export declare function Banner(props: BannerProps): React.ReactElement;

export interface PopoverProps { title: string; subtitle?: string; children?: React.ReactNode; footer?: React.ReactNode; onClose?: () => void; width?: number }
export declare function Popover(props: PopoverProps): React.ReactElement;

export interface DialogProps { title: string; children?: React.ReactNode; actions?: React.ReactNode; inline?: boolean }
export declare function Dialog(props: DialogProps): React.ReactElement;

export interface Tab { value: string; label: string; badge?: string }
export interface TabBarProps { label: string; tabs: Tab[]; value: string; onChange?: (value: string) => void }
export declare function TabBar(props: TabBarProps): React.ReactElement;

export interface MessageBubbleProps { role: 'user' | 'assistant' | 'tool' | 'error' | 'confirmation'; children?: React.ReactNode; onApply?: () => void; onDiscard?: () => void }
export declare function MessageBubble(props: MessageBubbleProps): React.ReactElement;

export interface LedProps { tone?: 'off' | 'accent' | 'pending' | 'playing' | 'warning' | 'error'; className?: string }
export declare function Led(props: LedProps): React.ReactElement;

declare global { interface Window { RECompose: {
  Button: typeof Button;
  IconButton: typeof IconButton;
  HoldButton: typeof HoldButton;
  KeyGroup: typeof KeyGroup;
  SegmentedControl: typeof SegmentedControl;
  Toggle: typeof Toggle;
  Checkbox: typeof Checkbox;
  Select: typeof Select;
  TextField: typeof TextField;
  NumberField: typeof NumberField;
  SearchField: typeof SearchField;
  Slider: typeof Slider;
  Fader: typeof Fader;
  Meter: typeof Meter;
  Waveform: typeof Waveform;
  StatusPill: typeof StatusPill;
  StatusIndicator: typeof StatusIndicator;
  Chip: typeof Chip;
  Progress: typeof Progress;
  SectionBadge: typeof SectionBadge;
  Tile: typeof Tile;
  NowBlock: typeof NowBlock;
  NextCard: typeof NextCard;
  Lane: typeof Lane;
  Ruler: typeof Ruler;
  Toast: typeof Toast;
  Banner: typeof Banner;
  Popover: typeof Popover;
  Dialog: typeof Dialog;
  TabBar: typeof TabBar;
  MessageBubble: typeof MessageBubble;
  Icon: typeof Icon;
  Led: typeof Led;
  SECTIONS: Record<SectionType, { token: string; letters?: string; icon?: IconName; name: string }>;
  ICON_NAMES: IconName[];
} } }
