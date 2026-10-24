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
#pragma once

namespace rec::tools
{
int probe (const char* file);
int peaks (const char* file, const char* out, int framesPerBucket);
} // namespace rec::tools
