/* @ds-bundle: {"format":4,"namespace":"RECompose","components":[{"name":"Button"},{"name":"IconButton"},{"name":"HoldButton"},{"name":"KeyGroup"},{"name":"SegmentedControl"},{"name":"Toggle"},{"name":"Checkbox"},{"name":"Select"},{"name":"TextField"},{"name":"NumberField"},{"name":"SearchField"},{"name":"Slider"},{"name":"Fader"},{"name":"Meter"},{"name":"Waveform"},{"name":"StatusPill"},{"name":"StatusIndicator"},{"name":"Chip"},{"name":"Progress"},{"name":"SectionBadge"},{"name":"Tile"},{"name":"NowBlock"},{"name":"NextCard"},{"name":"Lane"},{"name":"Ruler"},{"name":"Toast"},{"name":"Banner"},{"name":"Popover"},{"name":"Dialog"},{"name":"TabBar"},{"name":"MessageBubble"},{"name":"Icon"},{"name":"Led"}]} */
(function () {
"use strict";
var ICONS = {"play":"<path d=\"M7 4.5v15l12-7.5z\" fill=\"currentColor\"/>","pause":"<rect x=\"6\" y=\"4.5\" width=\"4\" height=\"15\" rx=\"1\" fill=\"currentColor\"/><rect x=\"14\" y=\"4.5\" width=\"4\" height=\"15\" rx=\"1\" fill=\"currentColor\"/>","stop":"<rect x=\"5.5\" y=\"5.5\" width=\"13\" height=\"13\" rx=\"1.5\" fill=\"currentColor\"/>","restart":"<path d=\"M5 5v14\"/><path d=\"M19 5.5v13L9 12z\" fill=\"currentColor\"/>","next":"<path d=\"M5 5.5v13L15 12z\" fill=\"currentColor\"/><path d=\"M19 5v14\"/>","go":"<path d=\"M5 12h13\"/><path d=\"M13 6l6 6-6 6\"/>","plus":"<path d=\"M12 5v14M5 12h14\"/>","minus":"<path d=\"M5 12h14\"/>","close":"<path d=\"M6 6l12 12M18 6L6 18\"/>","check":"<path d=\"M5 12.5l4.5 4.5L19 7.5\"/>","chevron-down":"<path d=\"M6 9l6 6 6-6\"/>","chevron-right":"<path d=\"M9 6l6 6-6 6\"/>","more":"<circle cx=\"5.5\" cy=\"12\" r=\"1.6\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"12\" cy=\"12\" r=\"1.6\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"18.5\" cy=\"12\" r=\"1.6\" fill=\"currentColor\" stroke=\"none\"/>","drag":"<circle cx=\"9\" cy=\"6\" r=\"1.5\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"15\" cy=\"6\" r=\"1.5\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"9\" cy=\"12\" r=\"1.5\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"15\" cy=\"12\" r=\"1.5\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"9\" cy=\"18\" r=\"1.5\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"15\" cy=\"18\" r=\"1.5\" fill=\"currentColor\" stroke=\"none\"/>","search":"<circle cx=\"10.5\" cy=\"10.5\" r=\"6\"/><path d=\"M15 15l5 5\"/>","undo":"<path d=\"M9 7L4.5 11.5 9 16\"/><path d=\"M5 11.5h9a5 5 0 0 1 0 10h-2\"/>","settings":"<circle cx=\"12\" cy=\"12\" r=\"3\"/><path d=\"M12 2.5v3M12 18.5v3M2.5 12h3M18.5 12h3M5.3 5.3l2.1 2.1M16.6 16.6l2.1 2.1M5.3 18.7l2.1-2.1M16.6 7.4l2.1-2.1\"/>","folder":"<path d=\"M3.5 6.5a1 1 0 0 1 1-1h5l2 2.5h8a1 1 0 0 1 1 1v9.5a1 1 0 0 1-1 1h-15a1 1 0 0 1-1-1z\"/>","loop":"<path d=\"M17 3.5l3 3-3 3\"/><path d=\"M4 11V9.5a3 3 0 0 1 3-3h13\"/><path d=\"M7 20.5l-3-3 3-3\"/><path d=\"M20 13v1.5a3 3 0 0 1-3 3H4\"/>","repeat":"<path d=\"M4 12a8 8 0 1 0 2.4-5.7\"/><path d=\"M4 4v4.5h4.5\"/>","vamp":"<path d=\"M7 7.5a6.5 6.5 0 1 0 10 0\"/><path d=\"M12 3v7\"/>","end":"<path d=\"M4 12h11\"/><path d=\"M11 7l5 5-5 5\"/><rect x=\"18\" y=\"5\" width=\"2.5\" height=\"14\" rx=\"1\" fill=\"currentColor\" stroke=\"none\"/>","lock":"<rect x=\"5\" y=\"10.5\" width=\"14\" height=\"10\" rx=\"1.5\"/><path d=\"M8 10.5V7.5a4 4 0 0 1 8 0v3\"/>","unlock":"<rect x=\"5\" y=\"10.5\" width=\"14\" height=\"10\" rx=\"1.5\"/><path d=\"M8 10.5V7.5a4 4 0 0 1 7.6-1.8\"/>","panic":"<path d=\"M8.5 3h7L21 8.5v7L15.5 21h-7L3 15.5v-7z\"/><path d=\"M12 7.5v5.5M12 16.5v.5\"/>","mic":"<rect x=\"9\" y=\"3\" width=\"6\" height=\"11\" rx=\"3\"/><path d=\"M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21\"/>","pedal":"<path d=\"M4 18.5h16\"/><path d=\"M6 18.5l2-6h8l2 6\"/><path d=\"M9 12.5l1-4.5h4l1 4.5\"/>","keyboard":"<rect x=\"2.5\" y=\"6.5\" width=\"19\" height=\"11\" rx=\"1.5\"/><path d=\"M6.5 10h1M10 10h1M13.5 10h1M17 10h.5M7 14h10\"/>","midi":"<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><circle cx=\"7.5\" cy=\"12\" r=\".8\"/><circle cx=\"16.5\" cy=\"12\" r=\".8\"/><circle cx=\"9\" cy=\"8.5\" r=\".8\"/><circle cx=\"15\" cy=\"8.5\" r=\".8\"/><circle cx=\"12\" cy=\"7.2\" r=\".8\"/><path d=\"M10.5 18h3\"/>","speaker":"<path d=\"M4 9.5v5h3.5L13 19V5L7.5 9.5z\"/><path d=\"M16.5 9a4 4 0 0 1 0 6\"/>","mute":"<path d=\"M4 9.5v5h3.5L13 19V5L7.5 9.5z\"/><path d=\"M16.5 9.5l5 5M21.5 9.5l-5 5\"/>","headphones":"<path d=\"M4 15v-3a8 8 0 0 1 16 0v3\"/><rect x=\"3.5\" y=\"14\" width=\"4\" height=\"6\" rx=\"1\"/><rect x=\"16.5\" y=\"14\" width=\"4\" height=\"6\" rx=\"1\"/>","cpu":"<rect x=\"6.5\" y=\"6.5\" width=\"11\" height=\"11\" rx=\"1\"/><path d=\"M10 3v3.5M14 3v3.5M10 17.5V21M14 17.5V21M3 10h3.5M3 14h3.5M17.5 10H21M17.5 14H21\"/>","assistant":"<path d=\"M4.5 5.5h15a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H11l-4.5 3.5v-3.5h-2a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1z\"/><path d=\"M8.5 11h7\"/>","metronome":"<path d=\"M7 20.5h10L14 3.5h-4z\"/><path d=\"M12 15l5.5-7.5\"/>","wave":"<path d=\"M2.5 12c2.4-5 4.8-5 7.2 0s4.8 5 7.2 0 3.4-4.2 4.6-2\"/>","hold":"<rect x=\"6\" y=\"5\" width=\"4\" height=\"14\" rx=\"1\" fill=\"currentColor\"/><rect x=\"14\" y=\"5\" width=\"4\" height=\"14\" rx=\"1\" fill=\"currentColor\"/>","diamond":"<path d=\"M12 3.5l8.5 8.5-8.5 8.5L3.5 12z\" fill=\"currentColor\"/>","warning":"<path d=\"M12 4l9 16H3z\"/><path d=\"M12 10v4.5M12 17.5v.3\"/>","error":"<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M9 9l6 6M15 9l-6 6\"/>","info":"<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M12 11v6M12 7.5v.3\"/>","rendering":"<path d=\"M20 12a8 8 0 1 1-2.3-5.6\"/><path d=\"M20 4.5v4h-4\"/>","transition":"<path d=\"M3 12h7\"/><path d=\"M14 12h7\"/><path d=\"M17 8l4 4-4 4\"/><circle cx=\"12\" cy=\"12\" r=\"1.2\"/>"};
var React = window.React;
var h = React.createElement;
var useState = React.useState, useRef = React.useRef, useEffect = React.useEffect;

function cx() {
  var out = [];
  for (var i = 0; i < arguments.length; i++) { if (arguments[i]) out.push(arguments[i]); }
  return out.join(" ");
}
function omit(obj, keys) {
  var o = {};
  for (var k in obj) { if (Object.prototype.hasOwnProperty.call(obj, k) && keys.indexOf(k) < 0) o[k] = obj[k]; }
  return o;
}
function reducedMotion() {
  try { return window.matchMedia("(prefers-reduced-motion: reduce)").matches; } catch (e) { return false; }
}

/* ---------- Icon ---------- */
function Icon(p) {
  var size = p.size || 16;
  var a11y = p.label ? { role: "img", "aria-label": p.label } : { "aria-hidden": "true" };
  return h("svg", Object.assign({
    className: cx("rc-icon", p.className), width: size, height: size, viewBox: "0 0 24 24",
    fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round",
    focusable: "false", dangerouslySetInnerHTML: { __html: ICONS[p.name] || "" }
  }, a11y));
}

/* ---------- Led ---------- */
function Led(p) {
  return h("span", { className: cx("rc-led", p.tone && p.tone !== "off" && "rc-led--" + p.tone, p.className), "aria-hidden": "true" });
}

/* ---------- Sections ---------- */
var SECTION = {
  intro: { token: "intro", letters: "I", name: "Intro" },
  verse: { token: "verse", letters: "V", name: "Verse" },
  preChorus: { token: "pre-chorus", letters: "PC", name: "Pre-chorus" },
  chorus: { token: "chorus", letters: "C", name: "Chorus" },
  postChorus: { token: "post-chorus", letters: "PoC", name: "Post-chorus" },
  bridge: { token: "bridge", letters: "B", name: "Bridge" },
  interlude: { token: "interlude", letters: "IL", name: "Interlude" },
  instrumental: { token: "interlude", letters: "IN", name: "Instrumental" },
  turnaround: { token: "interlude", letters: "TU", name: "Turnaround" },
  vamp: { token: "vamp", letters: "VA", name: "Vamp" },
  breakdown: { token: "vamp", letters: "BR", name: "Breakdown" },
  build: { token: "vamp", letters: "BU", name: "Build" },
  tag: { token: "tag", letters: "T", name: "Tag" },
  outro: { token: "outro", letters: "O", name: "Outro" },
  ending: { token: "ending", letters: "E", name: "Ending" },
  other: { token: "other", icon: "diamond", name: "Other" },
  click: { token: "click", icon: "metronome", name: "Click bars" },
  pad: { token: "pad", icon: "wave", name: "Pad bars" },
  hold: { token: "hold", icon: "hold", name: "Hold" }
};
var BADGE_ICON = { s: 11, m: 13, l: 20, xl: 24 };
function SectionBadge(p) {
  var def = SECTION[p.type] || SECTION.other;
  var size = p.size || "m";
  var text = p.letters || (def.letters ? def.letters + (p.number != null ? p.number : "") : null);
  var style = p.muted ? null : { background: "var(--section-" + def.token + ")" };
  return h("span", {
    className: cx("rc-badge", "rc-badge--" + size, p.muted && "rc-badge--muted", p.outline && "rc-badge--outline", p.className),
    style: p.outline ? { borderColor: "var(--section-" + def.token + ")", color: "var(--section-" + def.token + ")" } : style,
    title: p.title === false ? undefined : (p.title || def.name + (p.number != null ? " " + p.number : ""))
  }, text ? text : h(Icon, { name: def.icon, size: BADGE_ICON[size] }));
}

/* ---------- Button ---------- */
function Button(p) {
  var variant = p.variant || "secondary";
  var size = p.size || "compact";
  var stacked = size === "perform" && (p.led || p.hint);
  var rest = omit(p, ["variant", "size", "led", "hint", "icon", "pressed", "active", "className", "children", "width"]);
  var top = stacked ? h("span", { className: "rc-btn__top" },
    p.led ? h(Led, { tone: p.led }) : h("span"),
    p.hint ? h("span", { className: "rc-btn__hint" }, p.hint) : null) : null;
  return h("button", Object.assign({
    type: "button",
    className: cx("rc-btn", "rc-btn--" + variant, "rc-btn--" + size, stacked && "rc-btn--stack", p.active && "is-active", p.className),
    "aria-pressed": p.pressed == null ? undefined : !!p.pressed,
    style: p.width ? { width: p.width } : undefined
  }, rest),
    top,
    h("span", { className: "rc-btn__label" }, p.icon ? h(Icon, { name: p.icon, size: size === "perform" ? 18 : 14 }) : null, p.children));
}

function IconButton(p) {
  var size = p.size || "compact";
  var rest = omit(p, ["icon", "label", "size", "variant", "className", "active", "iconSize"]);
  return h("button", Object.assign({
    type: "button", "aria-label": p.label, title: p.label,
    className: cx("rc-btn", "rc-btn--icon", "rc-btn--" + (p.variant || "secondary"), "rc-btn--" + size, p.active && "is-active", p.className)
  }, rest), h(Icon, { name: p.icon, size: p.iconSize || (size === "perform" ? 18 : 16) }));
}

/* ---------- HoldButton (hold-to-confirm) ---------- */
function HoldButton(p) {
  var holdMs = p.holdMs || 600;
  var st = useState(0), progress = st[0], setProgress = st[1];
  var raf = useRef(0), start = useRef(0), done = useRef(false);
  function stop() { cancelAnimationFrame(raf.current); if (!done.current) setProgress(0); }
  function tick(t) {
    var v = Math.min(1, (t - start.current) / holdMs);
    setProgress(v);
    if (v >= 1) { done.current = true; if (p.onConfirm) p.onConfirm(); setTimeout(function () { done.current = false; setProgress(0); }, 300); return; }
    raf.current = requestAnimationFrame(tick);
  }
  function begin() { if (p.disabled) return; done.current = false; start.current = performance.now(); raf.current = requestAnimationFrame(tick); }
  useEffect(function () { return function () { cancelAnimationFrame(raf.current); }; }, []);
  var size = p.size || "perform";
  return h("button", {
    type: "button", disabled: p.disabled,
    className: cx("rc-btn", "rc-btn--secondary", "rc-btn--" + size, "rc-btn--hold", p.iconOnly && "rc-btn--icon", progress > 0 && "is-active", p.className),
    "aria-label": (p.label || "") + ", press and hold", title: (p.label || "") + " (press and hold)",
    style: p.width ? { width: p.width } : undefined,
    onPointerDown: function (e) { e.currentTarget.setPointerCapture && e.currentTarget.setPointerCapture(e.pointerId); begin(); },
    onPointerUp: stop, onPointerCancel: stop, onLostPointerCapture: stop,
    onKeyDown: function (e) { if ((e.key === " " || e.key === "Enter") && !e.repeat) { e.preventDefault(); begin(); } },
    onKeyUp: function (e) { if (e.key === " " || e.key === "Enter") stop(); },
    onBlur: stop
  },
    h("span", { className: "rc-btn__label" }, p.icon ? h(Icon, { name: p.icon, size: 18 }) : null, p.iconOnly ? null : p.label),
    h("span", { className: "rc-hold", "aria-hidden": "true" }, h("span", { className: "rc-hold__fill", style: { transform: "scaleX(" + progress + ")" } })));
}

/* ---------- KeyGroup ---------- */
function KeyGroup(p) {
  return h("div", { role: "group", "aria-label": p.ariaLabel || p.label, className: cx("rc-keygroup", p.className) },
    p.label ? h("span", { className: "rc-label" }, p.label) : null,
    h("div", { className: "rc-keygroup__keys" }, p.children));
}

/* ---------- SegmentedControl ---------- */
function SegmentedControl(p) {
  var size = p.size || "compact";
  var opts = p.options || [];
  function onKey(e, i) {
    var d = e.key === "ArrowRight" || e.key === "ArrowDown" ? 1 : e.key === "ArrowLeft" || e.key === "ArrowUp" ? -1 : 0;
    if (!d) return;
    e.preventDefault();
    var n = (i + d + opts.length) % opts.length;
    if (p.onChange) p.onChange(opts[n].value);
    var btns = e.currentTarget.parentNode.querySelectorAll("button");
    if (btns[n]) btns[n].focus();
  }
  return h("div", { role: "radiogroup", "aria-label": p.label, className: cx("rc-seg", "rc-seg--" + size, p.className) },
    opts.map(function (o, i) {
      var on = o.value === p.value;
      return h("button", {
        key: o.value, type: "button", role: "radio", "aria-checked": on, tabIndex: on ? 0 : -1,
        className: cx("rc-seg__opt", on && "is-on"),
        onClick: function () { if (p.onChange) p.onChange(o.value); },
        onKeyDown: function (e) { onKey(e, i); }
      }, on && o.led ? h(Led, { tone: o.led }) : null, o.icon ? h(Icon, { name: o.icon, size: 14 }) : null, o.label);
    }));
}

/* ---------- Toggle ---------- */
function Toggle(p) {
  return h("label", { className: cx("rc-toggle", p.disabled && "is-disabled", p.className) },
    h("button", {
      type: "button", role: "switch", "aria-checked": !!p.checked, disabled: p.disabled, id: p.id,
      className: "rc-toggle__track", onClick: function () { if (p.onChange) p.onChange(!p.checked); }
    }, h("span", { className: "rc-toggle__thumb" })),
    h("span", { className: "rc-toggle__label" }, p.label));
}

/* ---------- Checkbox ---------- */
function Checkbox(p) {
  return h("label", { className: cx("rc-check", p.disabled && "is-disabled", p.className) },
    h("input", { type: "checkbox", id: p.id, checked: !!p.checked, disabled: p.disabled, onChange: function (e) { if (p.onChange) p.onChange(e.target.checked); } }),
    h("span", { className: "rc-check__box", "aria-hidden": "true" }, h(Icon, { name: "check", size: 12 })),
    h("span", { className: "rc-check__label" }, p.label));
}

/* ---------- Field wrapper ---------- */
function Field(p, control) {
  return h("div", { className: cx("rc-field", p.error && "has-error", p.className) },
    p.label ? h("label", { className: "rc-field__label", htmlFor: p.id }, p.label) : null,
    control,
    p.error ? h("span", { className: "rc-field__msg rc-field__msg--error", id: p.id + "-msg" }, h(Icon, { name: "error", size: 14 }), p.error)
      : p.hint ? h("span", { className: "rc-field__msg", id: p.id + "-msg" }, p.hint) : null);
}

function TextField(p) {
  return Field(p, h("input", {
    id: p.id, type: "text", className: "rc-input", value: p.value, placeholder: p.placeholder, disabled: p.disabled,
    "aria-invalid": p.error ? true : undefined, "aria-describedby": (p.error || p.hint) ? p.id + "-msg" : undefined,
    onChange: function (e) { if (p.onChange) p.onChange(e.target.value); }, readOnly: !p.onChange
  }));
}

function SearchField(p) {
  return h("div", { className: cx("rc-search", p.className) },
    h(Icon, { name: "search", size: 16 }),
    h("input", {
      id: p.id, type: "search", className: "rc-input rc-input--search", value: p.value, placeholder: p.placeholder || "Search",
      "aria-label": p.label || "Search", onChange: function (e) { if (p.onChange) p.onChange(e.target.value); }, readOnly: !p.onChange
    }));
}

function Select(p) {
  return Field(p, h("span", { className: cx("rc-select", p.size === "perform" && "rc-select--perform") },
    h("select", {
      id: p.id, value: p.value, disabled: p.disabled,
      onChange: function (e) { if (p.onChange) p.onChange(e.target.value); }
    }, (p.options || []).map(function (o) { return h("option", { key: o.value, value: o.value }, o.label); })),
    h(Icon, { name: "chevron-down", size: 14 })));
}

function NumberField(p) {
  var size = p.size || "compact";
  var step = p.step || 1;
  function set(v) {
    if (p.min != null) v = Math.max(p.min, v);
    if (p.max != null) v = Math.min(p.max, v);
    if (p.onChange) p.onChange(v);
  }
  var shown = p.format ? p.format(p.value) : String(p.value);
  return h("div", { role: "group", "aria-label": p.label, className: cx("rc-number", "rc-number--" + size, p.className) },
    p.showLabel === false ? null : h("span", { className: "rc-label" }, p.label),
    h("div", { className: "rc-number__row" },
      h(IconButton, { icon: "minus", label: p.label + " down", size: size, onClick: function () { set(p.value - step); }, disabled: p.min != null && p.value <= p.min }),
      h("output", { className: cx("rc-number__value", p.warn && "is-warn"), "aria-live": "polite" }, shown),
      h(IconButton, { icon: "plus", label: p.label + " up", size: size, onClick: function () { set(p.value + step); }, disabled: p.max != null && p.value >= p.max })));
}

/* ---------- Slider ---------- */
function Slider(p) {
  var min = p.min != null ? p.min : 0, max = p.max != null ? p.max : 100;
  var pct = ((p.value - min) / (max - min)) * 100;
  return h("div", { className: cx("rc-slider", p.className) },
    h("div", { className: "rc-slider__head" },
      h("label", { className: "rc-field__label", htmlFor: p.id }, p.label),
      h("output", { className: "rc-slider__value", htmlFor: p.id }, p.format ? p.format(p.value) : p.value)),
    h("input", {
      id: p.id, type: "range", min: min, max: max, step: p.step || 1, value: p.value,
      style: { "--v": pct + "%" }, onChange: function (e) { if (p.onChange) p.onChange(Number(e.target.value)); }
    }));
}

/* ---------- Meter ---------- */
function Meter(p) {
  var segs = p.segments || 24;
  var db = p.db != null ? p.db : -60;
  var lit = Math.round(Math.max(0, Math.min(1, (db + 60) / 60)) * segs);
  var peak = p.peakDb != null ? Math.round(Math.max(0, Math.min(1, (p.peakDb + 60) / 60)) * segs) : null;
  var cells = [];
  for (var i = 0; i < segs; i++) {
    var segDb = -60 + ((i + 1) / segs) * 60;
    var zone = segDb > -6 ? "high" : segDb > -18 ? "mid" : "low";
    var on = i < lit || (peak != null && i === peak - 1);
    cells.push(h("span", { key: i, className: cx("rc-meter__seg", on && "is-on rc-meter__seg--" + zone) }));
  }
  return h("div", {
    className: cx("rc-meter", p.orientation === "horizontal" ? "rc-meter--h" : "rc-meter--v", p.className),
    role: "meter", "aria-label": p.label || "Level", "aria-valuemin": -60, "aria-valuemax": 0, "aria-valuenow": Math.round(db),
    "aria-valuetext": Math.round(db) + " dBFS"
  }, cells);
}

/* ---------- Fader ---------- */
function formatDb(v) { return v <= -60 ? "−∞" : (v > 0 ? "+" : v < 0 ? "−" : "") + Math.abs(v).toFixed(1); }
function Fader(p) {
  var min = -60, max = 6;
  return h("div", { className: cx("rc-fader", p.className) },
    h("span", { className: "rc-fader__name", id: p.id + "-name" }, p.label),
    p.group ? h("span", { className: "rc-fader__group" }, p.group) : null,
    h("div", { className: "rc-fader__body" },
      h("div", { className: "rc-fader__scale", "aria-hidden": "true" },
        ["+6", "0", "−6", "−12", "−24", "−48"].map(function (t) { return h("span", { key: t }, t); })),
      h("input", {
        id: p.id, type: "range", min: min, max: max, step: 0.5, value: p.value, "aria-labelledby": p.id + "-name",
        "aria-valuetext": formatDb(p.value) + " dB",
        onChange: function (e) { if (p.onChange) p.onChange(Number(e.target.value)); },
        onDoubleClick: function () { if (p.onChange) p.onChange(0); }
      }),
      p.meterDb != null ? h(Meter, { db: p.meterDb, peakDb: p.peakDb, segments: 20, label: p.label + " level" }) : null),
    h("output", { className: "rc-fader__value", htmlFor: p.id }, formatDb(p.value)),
    p.children ? h("div", { className: "rc-fader__keys" }, p.children) : null);
}

/* ---------- StatusPill ---------- */
var PILL = {
  ready: { text: "READY", mark: "dot" },
  playing: { text: "PLAYING", mark: "dot" },
  hold: { text: "HOLD", icon: "hold" },
  transition: { text: "TRANSITION", icon: "transition" },
  loading: { text: "LOADING", icon: "rendering" }
};
function StatusPill(p) {
  var d = PILL[p.state] || PILL.ready;
  return h("span", { className: cx("rc-pill", "rc-pill--" + (p.state || "ready"), p.className), role: "status" },
    d.icon ? h(Icon, { name: d.icon, size: 12 }) : h("span", { className: "rc-pill__dot", "aria-hidden": "true" }),
    p.children || d.text);
}

/* ---------- StatusIndicator ---------- */
function StatusIndicator(p) {
  var state = p.state || "ok";
  var mark = state === "warning" ? h(Icon, { name: "warning", size: 12 })
    : state === "error" ? h("span", { className: "rc-ind__sq", "aria-hidden": "true" })
      : h("span", { className: "rc-ind__dot", "aria-hidden": "true" });
  var level = p.level != null ? h("span", { className: "rc-ind__level", "aria-hidden": "true" },
    [0.25, 0.5, 0.75, 1].map(function (t, i) { return h("span", { key: i, className: p.level >= t ? "is-on" : null, style: { height: 4 + i * 3 + "px" } }); })) : null;
  var Tag = p.onClick ? "button" : "span";
  return h(Tag, { type: p.onClick ? "button" : undefined, onClick: p.onClick, className: cx("rc-ind", "rc-ind--" + state, p.onClick && "rc-ind--button", p.className) },
    mark, h("span", null, p.label), p.detail ? h("span", { className: "rc-ind__detail" }, p.detail) : null, level);
}

/* ---------- Chip ---------- */
function Chip(p) {
  var Tag = p.onClick ? "button" : "span";
  return h(Tag, {
    type: p.onClick ? "button" : undefined, onClick: p.onClick,
    className: cx("rc-chip", "rc-chip--" + (p.kind || "tag"), p.selected && "is-selected", p.onClick && "rc-chip--button", p.className)
  }, p.icon ? h(Icon, { name: p.icon, size: 14 }) : null, p.children);
}

/* ---------- Tile ---------- */
function Tile(p) {
  var state = p.state || "upcoming";
  var def = SECTION[p.type] || SECTION.other;
  var played = state === "played", skipped = state === "skipped";
  var corner = null;
  if (played) corner = h(Icon, { name: "check", size: 14 });
  else if (state === "current") corner = h("span", { className: "rc-tile__now" }, h(Led, { tone: "accent" }), "NOW");
  else if (state === "next") corner = h("span", { className: "rc-tile__tag" }, "NEXT");
  else if (skipped) corner = h("span", { className: "rc-tile__tag" }, "Skipped");
  else if (state === "armed") corner = h("span", { className: "rc-tile__tag rc-tile__tag--armed" }, "Tap again");
  else if (p.meta) corner = h("span", { className: cx("rc-tile__meta", state === "pending" && "is-pending") }, p.meta);
  var stateWord = { played: "played", current: "now playing", next: "next", pending: "pending jump target", skipped: "will be skipped", armed: "armed, tap again to jump", selected: "selected" }[state];
  var label = p.label || def.name;
  var passes = p.passes ? h("span", { className: "rc-tile__passes", "aria-hidden": "true" },
    Array.from({ length: p.passes.total }, function (_, i) { return h("span", { key: i, className: i < p.passes.done ? "is-on" : null }); })) : null;
  return h("button", {
    type: "button", onClick: p.onClick,
    className: cx("rc-tile", "rc-tile--" + state, p.kind === "section" && "rc-tile--section", p.className),
    "aria-current": state === "current" ? "true" : undefined,
    "aria-label": label + (p.meta ? ", " + p.meta : "") + (stateWord ? ", " + stateWord : ""),
    style: p.flex ? { flex: p.flex } : undefined
  },
    h("span", { className: "rc-tile__top" },
      h(SectionBadge, { type: p.type, number: p.number, size: "s", muted: played, outline: skipped, title: false }),
      corner),
    h("span", { className: "rc-tile__label" }, label),
    passes,
    state === "current" && p.progress != null ? h("span", { className: "rc-tile__progress", "aria-hidden": "true", style: { width: Math.round(p.progress * 100) + "%" } }) : null);
}

/* ---------- Progress ---------- */
function Progress(p) {
  var v = Math.max(0, Math.min(1, p.value || 0));
  var tone = p.tone || "neutral";
  if (p.variant === "ring") {
    var size = p.size || 20, r = (size - 4) / 2, c = 2 * Math.PI * r;
    return h("span", { className: cx("rc-ring", "rc-ring--" + tone, p.className), role: "progressbar", "aria-label": p.label, "aria-valuemin": 0, "aria-valuemax": 100, "aria-valuenow": Math.round(v * 100) },
      h("svg", { width: size, height: size, viewBox: "0 0 " + size + " " + size, "aria-hidden": "true" },
        h("circle", { cx: size / 2, cy: size / 2, r: r, className: "rc-ring__track" }),
        h("circle", { cx: size / 2, cy: size / 2, r: r, className: "rc-ring__fill", strokeDasharray: c, strokeDashoffset: c * (1 - v), transform: "rotate(-90 " + size / 2 + " " + size / 2 + ")" })),
      p.showValue ? h("span", { className: "rc-ring__value" }, Math.round(v * 100) + "%") : null);
  }
  return h("div", { className: cx("rc-bar", "rc-bar--" + tone, p.thin && "rc-bar--thin", p.className), role: "progressbar", "aria-label": p.label, "aria-valuemin": 0, "aria-valuemax": 100, "aria-valuenow": Math.round(v * 100) },
    h("span", { className: "rc-bar__fill", style: { width: v * 100 + "%" } }));
}

/* ---------- NowBlock ---------- */
function NowBlock(p) {
  var def = SECTION[p.type] || SECTION.other;
  var label = p.label || def.name + (p.number != null ? " " + p.number : "");
  return h("section", { className: cx("rc-now", p.xl && "rc-now--xl", p.className), "aria-label": "Now playing" },
    h("div", { className: "rc-now__head" },
      h("span", { className: "rc-label" }, "NOW"),
      h(StatusPill, { state: p.state || "playing" }),
      p.pass ? h("span", { className: "rc-now__pass" }, p.pass) : null),
    h("div", { className: "rc-now__title" },
      h(SectionBadge, { type: p.type, number: p.number, size: "xl" }),
      h("span", { className: "now" }, label)),
    p.bar != null ? h("div", { className: "rc-now__bar" },
      h("span", { className: "readout" }, "bar " + p.bar, h("span", { className: "rc-muted" }, " / " + p.bars)),
      h(Progress, { value: p.bar / p.bars, label: "Bar in section", tone: "text" })) : null,
    p.note ? h("p", { className: "rc-now__note body-perform" }, "MD note · " + p.note) : null);
}

/* ---------- NextCard ---------- */
var SOURCE_ICON = { VOICE: "mic", "VOICE?": "mic", PEDAL: "pedal", KEYS: "keyboard", MANUAL: "go" };
function NextCard(p) {
  var mode = p.mode || "normal";
  var pending = mode === "pending";
  var head;
  if (mode === "hold") head = h("span", { className: "rc-next__tag rc-next__tag--hold" }, h(Icon, { name: "hold", size: 13 }), "HOLD");
  else if (mode === "repeating") head = h("span", { className: "rc-next__tag" }, h(Icon, { name: "repeat", size: 13 }), "REPEATING");
  else if (mode === "nextSong") head = h("span", { className: "rc-next__tag" }, "NEXT SONG");
  else head = h("span", { className: cx("rc-next__tag", pending && "rc-next__tag--pending") },
    SOURCE_ICON[p.source] ? h(Icon, { name: SOURCE_ICON[p.source], size: 13 }) : null,
    (p.source || "ARRANGEMENT") + (pending ? " · PENDING" : ""));
  var target = mode === "hold" ? h("span", { className: "next" }, "Press GO")
    : mode === "nextSong" ? h("span", { className: "next" }, p.target)
      : h(React.Fragment, null, h(SectionBadge, { type: p.type, number: p.number, size: "l" }), h("span", { className: "next" }, p.target));
  return h("section", { className: cx("rc-next", "rc-next--" + mode, p.className), "aria-label": "Next", "aria-live": "polite" },
    h("div", { className: "rc-next__head" },
      h("span", { className: "rc-label" }, mode === "hold" || mode === "repeating" ? "STATUS" : "NEXT"),
      head,
      p.heard ? h("span", { className: "rc-next__heard" }, "heard “" + p.heard + "”") : null,
      h("span", { className: "rc-spacer" }),
      pending && p.onCancel !== false ? h(Button, { size: "touch", icon: "close", onClick: p.onCancel }, "Cancel jump") : null),
    h("div", { className: "rc-next__body" },
      target,
      p.keyShift ? h("span", { className: "rc-next__key" }, "KEY ", h("span", { className: "mono-small" }, p.keyShift)) : null,
      h("span", { className: "rc-spacer" }),
      p.countdown ? h("span", { className: cx("readout", pending && "rc-pending-text") }, p.countdown) : null,
      p.holdTime ? h("span", { className: "readout" }, "hold " + p.holdTime) : null),
    pending && p.progress != null ? h(Progress, { value: p.progress, tone: "pending", thin: true, label: "Time until the jump" }) : null,
    p.hint ? h("p", { className: "rc-next__hint" }, p.hint) : null);
}

/* ---------- Ruler ---------- */
function Ruler(p) {
  var bars = p.bars || 32, step = p.step || 4;
  var marks = [];
  for (var b = 1; b <= bars; b++) {
    var major = (b - 1) % step === 0;
    marks.push(h("span", { key: b, className: cx("rc-ruler__bar", major && "is-major", p.current === b && "is-current") }, major ? b : null));
  }
  return h("div", { className: cx("rc-ruler", p.className), "aria-hidden": "true", style: { gridTemplateColumns: "repeat(" + bars + ", minmax(0, 1fr))" } }, marks);
}

/* ---------- Lane ---------- */
function Lane(p) {
  var items = p.items || [];
  var total = items.reduce(function (s, it) { return s + (it.bars || 1); }, 0);
  /* Markers are placed in bar time, inside the block they fall in, so they stay
     true to the readouts even though blocks have gaps and minimum widths.
     playhead / pendingAt / loop are fractions of the total bars. */
  var starts = [], acc = 0;
  items.forEach(function (it) { starts.push(acc); acc += it.bars || 1; });
  var pct = function (v) { return (Math.max(0, Math.min(1, v)) * 100).toFixed(2) + "%"; };
  function blockOf(f) {
    var bar = f * total;
    for (var i = 0; i < items.length; i++) if (bar <= starts[i] + (items[i].bars || 1)) return i;
    return items.length - 1;
  }
  var playI = p.playhead != null ? blockOf(p.playhead) : -1;
  var pendI = p.pendingAt != null ? blockOf(p.pendingAt) : -1;
  var step = p.rulerStep || 4;
  function ticks(i) {
    if (!p.ruler || items[i].kind === "hold") return null;
    var len = items[i].bars || 1, out = [];
    for (var b = 0; b < len; b++) {
      var n = starts[i] + b + 1, major = (n - 1) % step === 0;
      out.push(h("span", { key: b, className: cx("rc-lane__tick", major && "is-major", p.currentBar === n && "is-current"), style: { left: pct(b / len) } }, major ? n : null));
    }
    return h("span", { className: "rc-lane__ticks", "aria-hidden": "true" }, out);
  }
  return h("div", { className: cx("rc-lane", p.ruler && "rc-lane--ruler", p.className) },
    h("div", { className: "rc-lane__blocks", role: "list", "aria-label": p.label || "Arrangement" },
      items.map(function (it, i) {
        var def = SECTION[it.type] || SECTION.other;
        var len = it.bars || 1;
        var hold = it.kind === "hold";
        var small = hold;
        var loopSeg = null;
        if (p.loop) {
          var a = Math.max(p.loop[0] * total, starts[i]), b = Math.min(p.loop[1] * total, starts[i] + len);
          if (b > a) loopSeg = h("span", { className: "rc-lane__loop", "aria-hidden": "true", style: { left: pct((a - starts[i]) / len), width: pct((b - a) / len) } });
        }
        return h("div", { key: i, role: "listitem", className: cx("rc-lane__slot", hold && "is-hold"), style: { flex: len + " " + len + " 0px" } }, h("button", {
          type: "button",
          className: cx("rc-lane__block", p.selected === i && "is-selected", small && "is-small", hold && "is-hold"),
          "aria-label": (it.label || def.name) + (it.meta ? ", " + it.meta : "") + (it.muted ? ", " + it.muted + " muted" : "") + (hold ? ", waits for GO" : ""),
          onClick: it.onClick
        },
          h(SectionBadge, { type: it.type, number: it.number, size: "s", title: false }),
          small ? null : h("span", { className: "rc-lane__name" }, it.label || def.name),
          small || !it.meta ? null : h("span", { className: "rc-lane__meta" }, it.meta),
          small || !it.muted ? null : h(Icon, { name: "mute", size: 14, label: it.muted + " muted" })),
          ticks(i),
          loopSeg,
          i === pendI ? h("span", { className: "rc-lane__pending", "aria-hidden": "true", style: { left: pct((p.pendingAt * total - starts[i]) / len) } }) : null,
          i === playI ? h("span", { className: "rc-lane__playhead", "aria-hidden": "true", style: { left: pct((p.playhead * total - starts[i]) / len) } }) : null);
      })));
}

/* ---------- Waveform ---------- */
function Waveform(p) {
  var n = p.bars || 120, w = p.width || 480, ht = p.height || 48;
  var seed = p.seed || 7;
  function rnd() { seed = (seed * 9301 + 49297) % 233280; return seed / 233280; }
  var bw = w / n, rects = [];
  for (var i = 0; i < n; i++) {
    var env = p.envelope ? p.envelope(i / n) : 0.55 + 0.35 * Math.sin(i / 9);
    var a = Math.max(0.06, Math.min(1, env * (0.6 + 0.4 * rnd())));
    var bh = a * (ht - 4);
    var f = i / n, muted = (p.muted || []).some(function (m) { return f >= m[0] && f < m[1]; });
    rects.push(h("rect", { key: i, x: (i * bw + bw * 0.15).toFixed(2), y: ((ht - bh) / 2).toFixed(2), width: (bw * 0.7).toFixed(2), height: bh.toFixed(2), rx: 0.5, className: muted ? "rc-wave__muted" : "rc-wave__bar" }));
  }
  return h("svg", { className: cx("rc-wave", p.className), width: "100%", height: ht, viewBox: "0 0 " + w + " " + ht, preserveAspectRatio: "none", role: "img", "aria-label": p.label || "Waveform" }, rects);
}

/* ---------- Popover ---------- */
function Popover(p) {
  return h("div", { className: cx("rc-popover", p.className), role: "dialog", "aria-modal": "false", "aria-label": p.title, style: p.width ? { width: p.width } : undefined },
    h("div", { className: "rc-popover__head" },
      h("div", null, h("h3", { className: "heading" }, p.title), p.subtitle ? h("p", { className: "rc-popover__sub" }, p.subtitle) : null),
      p.onClose ? h(IconButton, { icon: "close", label: "Close", variant: "ghost", onClick: p.onClose }) : null),
    h("div", { className: "rc-popover__body" }, p.children),
    p.footer ? h("div", { className: "rc-popover__foot" }, p.footer) : null);
}

/* ---------- Dialog ---------- */
function Dialog(p) {
  var panel = h("div", { className: "rc-dialog", role: "dialog", "aria-modal": "true", "aria-labelledby": "rc-dialog-title" },
    h("h2", { className: "title", id: "rc-dialog-title" }, p.title),
    h("div", { className: "rc-dialog__body" }, p.children),
    p.actions ? h("div", { className: "rc-dialog__actions" }, p.actions) : null);
  return p.inline ? panel : h("div", { className: "rc-scrim" }, panel);
}

/* ---------- Toast ---------- */
function Toast(p) {
  return h("div", { className: cx("rc-toast", p.tone && "rc-toast--" + p.tone, p.className), role: "status", "aria-live": "polite" },
    p.icon ? h(Icon, { name: p.icon, size: 16 }) : null,
    h("span", { className: "rc-toast__msg" }, p.children),
    p.actionLabel ? h(Button, { variant: "ghost", onClick: p.onAction, icon: p.actionIcon }, p.actionLabel) : null);
}

/* ---------- Banner ---------- */
var BANNER_ICON = { warning: "warning", error: "error", info: "info" };
function Banner(p) {
  var tone = p.tone || "info";
  return h("div", { className: cx("rc-banner", "rc-banner--" + tone, p.className), role: tone === "error" ? "alert" : "status" },
    h(Icon, { name: BANNER_ICON[tone], size: 18 }),
    h("div", { className: "rc-banner__text" }, h("strong", null, p.title), p.children ? h("span", null, p.children) : null),
    p.actions ? h("div", { className: "rc-banner__actions" }, p.actions) : null);
}

/* ---------- TabBar ---------- */
function TabBar(p) {
  var tabs = p.tabs || [];
  return h("div", { role: "tablist", "aria-label": p.label, className: cx("rc-tabs", p.className) },
    tabs.map(function (t, i) {
      var on = t.value === p.value;
      return h("button", {
        key: t.value, type: "button", role: "tab", "aria-selected": on, tabIndex: on ? 0 : -1, className: cx("rc-tab", on && "is-on"),
        onClick: function () { if (p.onChange) p.onChange(t.value); },
        onKeyDown: function (e) {
          var d = e.key === "ArrowRight" ? 1 : e.key === "ArrowLeft" ? -1 : 0; if (!d) return;
          var n = (i + d + tabs.length) % tabs.length; if (p.onChange) p.onChange(tabs[n].value);
          var b = e.currentTarget.parentNode.querySelectorAll("button")[n]; if (b) b.focus();
        }
      }, t.label, t.badge ? h("span", { className: "rc-tab__badge" }, t.badge) : null);
    }));
}

/* ---------- MessageBubble ---------- */
function MessageBubble(p) {
  var role = p.role || "assistant";
  if (role === "tool") return h("div", { className: "rc-msg rc-msg--tool" }, h(Icon, { name: "check", size: 14 }), h("span", null, p.children));
  if (role === "error") return h("div", { className: "rc-msg rc-msg--error", role: "alert" }, h(Icon, { name: "error", size: 14 }), h("span", null, p.children));
  if (role === "confirmation") return h("div", { className: "rc-msg rc-msg--confirm" },
    h("span", { className: "rc-label" }, "CONFIRM CHANGE"),
    h("p", null, p.children),
    h("div", { className: "rc-msg__actions" },
      h(Button, { variant: "primary", onClick: p.onApply }, "Apply"),
      h(Button, { onClick: p.onDiscard }, "Discard")));
  return h("div", { className: "rc-msg rc-msg--" + role }, p.children);
}

var api = { Icon: Icon, Led: Led, SectionBadge: SectionBadge, Button: Button, IconButton: IconButton, HoldButton: HoldButton, KeyGroup: KeyGroup,
  SegmentedControl: SegmentedControl, Toggle: Toggle, Checkbox: Checkbox, Select: Select, TextField: TextField, NumberField: NumberField,
  SearchField: SearchField, Slider: Slider, Fader: Fader, Meter: Meter, StatusPill: StatusPill, StatusIndicator: StatusIndicator, Chip: Chip,
  Tile: Tile, NowBlock: NowBlock, NextCard: NextCard, Lane: Lane, Ruler: Ruler, Popover: Popover, Dialog: Dialog, Toast: Toast, Banner: Banner,
  TabBar: TabBar, Progress: Progress, Waveform: Waveform, MessageBubble: MessageBubble, SECTIONS: SECTION, ICON_NAMES: Object.keys(ICONS) };
window.RECompose = Object.assign(window.RECompose || {}, api);
})();
