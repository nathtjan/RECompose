// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Analysis tool modes for milestone 2, run by the app in short-lived low-priority
// processes like probe and peaks (PB-92):
//
//   recompose-engine onsets <file>
//       JSON on stdout: {"ok":true,"sampleRate":..,"durationSec":..,
//                        "onsets":[[timeSec, peakDb, centroidHz], ...]}
//       Onsets of a click track (SEC-1): time of the attack, peak level in the first
//       30 ms, and the spectral centroid of the first 1024 samples (accent brightness).
//
//   recompose-engine transcribe <file> <model> [--prompt <text>] [--threads N]
//       JSON on stdout: {"ok":true,"model":..,"chunks":n,"words":[[text, start, end, p], ...]}
//       Word timestamps of a guide track (SEC-11/12) with whisper.cpp. Only the stretches
//       where someone speaks are transcribed, each on its own; times are in source seconds.
//       Progress goes to stderr as lines "progress 0.42".
//
// On failure both print {"ok":false,"problem":"..."} and exit with a non-zero code.
#pragma once

#include <vector>

namespace rec::analysis
{
struct Onset
{
    double time = 0;
    double peakDb = -120;
    double centroidHz = 0;
};

/// Onsets of a mono signal (the click track's channels averaged). Exposed for the tests.
std::vector<Onset> findOnsets (const float* samples, int count, double sampleRate);

/// Speech stretches [start, end) in seconds of a 16 kHz mono signal: 10 ms frames above
/// the level threshold, joined across gaps shorter than joinGapSec, padded by padSec.
struct Span
{
    double start = 0;
    double end = 0;
};
std::vector<Span> findSpeech (const float* samples16k, int count, double joinGapSec = 0.5, double padSec = 0.2);

int onsets (const char* file);
int transcribe (int argc, char* argv[]);
} // namespace rec::analysis
