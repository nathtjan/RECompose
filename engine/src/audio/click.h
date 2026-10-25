// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The click generator (PB-70): a synthesized click on the plan's beat grid, accented on beat 1,
// peaking at −6 dBFS. It sounds on every beat that starts inside a click segment (count-ins and
// click bars, SL-36, ARR-39). The disk thread renders it into the generated click track, so it
// is mixed, routed (Cue output) and muted like any other track and lands on the grid to the sample.
#pragma once

#include "audio/song.h"

#include <cstdint>

namespace rec::audio
{
inline constexpr float kClickPeak = 0.5011872f; // −6 dBFS
inline constexpr double kClickMs = 30.0;
inline constexpr double kClickHz = 1000.0;
inline constexpr double kAccentHz = 1600.0;

// Adds `frames` mono frames of click, starting at plan frame `position`, to `out`.
void renderClick (const Plan& plan, std::int64_t position, int frames, double sampleRate, float* out) noexcept;

// The frame a beat starts on: beat k of the plan's grid, rounded once.
std::int64_t beatFrame (const Plan& plan, std::int64_t k) noexcept;
} // namespace rec::audio
