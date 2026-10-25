// SPDX-License-Identifier: AGPL-3.0-or-later
#include "audio/click.h"

#include <algorithm>
#include <cmath>

namespace rec::audio
{
namespace
{
    constexpr double kTwoPi = 6.283185307179586;
    constexpr double kDecayMs = 6.0;

    std::int64_t floorMod (std::int64_t a, std::int64_t b) noexcept { return ((a % b) + b) % b; }

    // True when the beat starting at `frame` falls in a segment with the click on.
    bool clickAt (const Plan& plan, std::int64_t frame) noexcept
    {
        const auto& segments = plan.segments;
        auto it = std::upper_bound (segments.begin(), segments.end(), frame,
                                    [] (std::int64_t f, const PlanSegment& s) { return f < s.start; });
        if (it == segments.begin())
            return false;
        --it;
        return frame < it->end && it->click;
    }
} // namespace

std::int64_t beatFrame (const Plan& plan, std::int64_t k) noexcept
{
    return static_cast<std::int64_t> (std::llround (plan.gridOrigin + static_cast<double> (k) * plan.beatFrames));
}

void renderClick (const Plan& plan, std::int64_t position, int frames, double sampleRate, float* out) noexcept
{
    if (plan.beatFrames <= 1.0 || frames <= 0)
        return;

    const auto length = static_cast<std::int64_t> (std::lround (kClickMs * sampleRate / 1000.0));
    const double decay = 1.0 / (kDecayMs * sampleRate / 1000.0);
    const auto bpb = static_cast<std::int64_t> (std::max (1, plan.beatsPerBar));
    const auto end = position + frames;

    // Every beat whose sound reaches into [position, end), one either side for the rounding of each start.
    const auto first = static_cast<std::int64_t> (std::ceil ((static_cast<double> (position - length) - plan.gridOrigin) / plan.beatFrames)) - 1;
    const auto last = static_cast<std::int64_t> (std::floor ((static_cast<double> (end - 1) - plan.gridOrigin) / plan.beatFrames)) + 1;

    for (auto k = first; k <= last; ++k)
    {
        const auto start = beatFrame (plan, k);
        if (start + length <= position || start >= end || ! clickAt (plan, start))
            continue;

        const double hz = floorMod (k, bpb) == 0 ? kAccentHz : kClickHz;
        const double w = kTwoPi * hz / sampleRate;
        const auto from = std::max (start, position);
        const auto to = std::min (start + length, end);
        for (auto n = from; n < to; ++n)
        {
            const auto i = static_cast<double> (n - start);
            out[n - position] += kClickPeak * static_cast<float> (std::exp (-i * decay) * std::cos (w * i));
        }
    }
}
} // namespace rec::audio
