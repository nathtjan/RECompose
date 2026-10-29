// SPDX-License-Identifier: AGPL-3.0-or-later
//
// One-shot tool modes used by the app's import jobs. They run in their own short-lived
// process, so a file that crashes a decoder can't touch playback (PB-92).
//
//   recompose-engine probe <file>          JSON on stdout: channels, rate, frames, format
//   recompose-engine peaks <file> <out> [framesPerBucket]
//       writes a peaks file: "RECP", u32 version 1, u32 framesPerBucket, u32 bucketCount,
//       f64 sampleRate, then bucketCount × (f32 min, f32 max) over all channels; all
//       little-endian.
//   recompose-engine render <in> <out> --semitones N --tempo T
//       renders a stem at a key and a tempo (REL-10, PB-5): the same stretcher as live playback at its
//       highest quality, one offline pass, 24-bit FLAC (or WAV when <out> ends in .wav) at the input's rate.
//       Frame f of the output is source frame f × T. Prints "progress p" lines (0…1) on stderr and then one JSON
//       line {ok, frames, sampleRate, channels} or {ok: false, problem}. The output is written to a
//       temporary file and moved into place, so a render that stops half way leaves nothing behind.
#pragma once

namespace rec::tools
{
int probe (const char* file);
int peaks (const char* file, const char* out, int framesPerBucket);
int render (const char* in, const char* out, int semitones, double tempo);
} // namespace rec::tools
