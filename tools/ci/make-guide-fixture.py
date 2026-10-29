#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Writes an end-to-end fixture for click analysis and Detect from guide (SEC-1, SEC-10…15).

Usage: python3 tools/ci/make-guide-fixture.py <out folder>

The folder gets Click.wav (72 BPM 4/4, first downbeat 0.5 s, beat 1 brighter and louder),
Guide.wav (section calls spoken by the platform's text-to-speech voice on beat 2 of the bar
before each section), Voice.wav ("Let us go to the bridge", for the voice worker) and
expected.json (the labels and chart bars Detect should find).
Text-to-speech: espeak-ng on Linux, `say` on macOS, System.Speech on Windows.
"""
import json
import math
import os
import platform
import struct
import subprocess
import sys
import tempfile
import wave

RATE = 44100
BPM = 72.0
BEATS = 4
DOWNBEAT = 0.5
BAR = BEATS * 60.0 / BPM
BARS = 62

# (section start bar, spoken call, expected label)
CALLS = [
    (2, "Verse one", "Verse 1"),
    (10, "Chorus", "Chorus 1"),
    (18, "Verse two", "Verse 2"),
    (26, "Pre chorus", "Pre-Chorus"),
    (30, "Chorus", "Chorus 2"),
    (38, "Bridge", "Bridge"),
    (46, "Chorus", "Chorus 3"),
    (54, "Tag", "Tag"),
    (58, "Ending", "Ending"),
]


def bar_time(bar: int) -> float:
    return DOWNBEAT + (bar - 1) * BAR


def speak(text: str, path: str) -> None:
    system = platform.system()
    if system == "Linux":
        subprocess.run(["espeak-ng", "-v", "en-us", "-s", "150", "-w", path, text], check=True)
    elif system == "Darwin":
        subprocess.run(["say", "-o", path, "--file-format=WAVE", "--data-format=LEI16@22050", text], check=True)
    elif system == "Windows":
        script = (
            "Add-Type -AssemblyName System.Speech; "
            "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
            f"$s.SetOutputToWaveFile('{path}'); $s.Speak('{text}'); $s.Dispose()"
        )
        subprocess.run(["powershell", "-NoProfile", "-Command", script], check=True)
    else:
        raise SystemExit(f"No text-to-speech for {system}")


def read_wav(path: str) -> list:
    with wave.open(path, "rb") as w:
        channels, width, rate, frames = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(frames)
    if width != 2:
        raise SystemExit(f"{path}: expected 16-bit PCM, got {8 * width}-bit")
    values = struct.unpack("<%dh" % (len(raw) // 2), raw)
    mono = [sum(values[i:i + channels]) / channels / 32768.0 for i in range(0, len(values), channels)]
    if rate == RATE:
        return mono
    out = []
    step = rate / RATE
    position = 0.0
    while position < len(mono) - 1:
        i = int(position)
        f = position - i
        out.append(mono[i] * (1 - f) + mono[i + 1] * f)
        position += step
    return out


def write_wav(path: str, samples: list) -> None:
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1.0, min(1.0, s)) * 32767)) for s in samples))


def main() -> int:
    out = sys.argv[1] if len(sys.argv) > 1 else "guide-fixture"
    os.makedirs(out, exist_ok=True)
    length = int((bar_time(BARS + 1) + 1) * RATE)

    # Click: a 25 ms decaying blip per beat; beat 1 at 2 kHz and -6 dBFS, the others 1 kHz and -12 dBFS.
    click = [0.0] * length
    beat = 60.0 / BPM
    k = 0
    while DOWNBEAT + k * beat < bar_time(BARS + 1):
        accent = k % BEATS == 0
        start = int(round((DOWNBEAT + k * beat) * RATE))
        freq, amp = (2000.0, 0.5) if accent else (1000.0, 0.25)
        for i in range(int(0.025 * RATE)):
            if start + i < length:
                click[start + i] = amp * math.sin(2 * math.pi * freq * i / RATE) * math.exp(-i / (0.006 * RATE))
        k += 1
    write_wav(os.path.join(out, "Click.wav"), click)

    guide = [0.0] * length
    with tempfile.TemporaryDirectory() as temp:
        for n, (bar, text, _) in enumerate(CALLS):
            path = os.path.join(temp, f"call{n}.wav")
            speak(text, path)
            voice = read_wav(path)
            peak = max(abs(v) for v in voice) or 1.0
            start = int((bar_time(bar - 1) + beat) * RATE)
            for i, v in enumerate(voice):
                if start + i < length:
                    guide[start + i] += 0.5 * v / peak
    write_wav(os.path.join(out, "Guide.wav"), guide)

    # The MD on the talkback mic (VC-70): half a second of silence either side, for the voice worker's round trip.
    with tempfile.TemporaryDirectory() as temp:
        path = os.path.join(temp, "voice.wav")
        speak("Let us go to the bridge", path)  # no apostrophe: the Windows voice is quoted in PowerShell
        voice = read_wav(path)
        peak = max(abs(v) for v in voice) or 1.0
        pad = [0.0] * (RATE // 2)
        write_wav(os.path.join(out, "Voice.wav"), pad + [0.5 * v / peak for v in voice] + pad)

    expected = {
        "bpm": BPM,
        "beatsPerBar": BEATS,
        "firstDownbeatSec": DOWNBEAT,
        "sections": [{"label": label, "chartBar": bar} for bar, _, label in CALLS],
    }
    with open(os.path.join(out, "expected.json"), "w", encoding="utf-8") as f:
        json.dump(expected, f, indent=2)
    print(f"Wrote {out}: Click.wav, Guide.wav, Voice.wav, expected.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
