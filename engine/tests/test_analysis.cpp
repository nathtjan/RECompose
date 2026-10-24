// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The analysis tool modes on synthetic audio: click onsets with accents (SEC-1) and the
// speech stretches the transcriber listens to (SEC-11).
#include "check.h"

#include "analysis.h"

#include <cmath>
#include <vector>

using namespace rec::analysis;

namespace
{
constexpr double pi = 3.14159265358979323846;

// A click: 72 BPM 4/4, first beat at 0.0007 s; beat 1 is a 2 kHz blip at −6 dBFS, the
// others 1 kHz at −12 dBFS, each 25 ms with an exponential decay.
std::vector<float> click (double rate, int beats)
{
    const double period = 60.0 / 72;
    std::vector<float> x (static_cast<size_t> ((0.0007 + beats * period + 1) * rate), 0.0f);
    for (int k = 0; k < beats; ++k)
    {
        const bool accent = k % 4 == 0;
        const double f = accent ? 2000 : 1000;
        const double a = accent ? 0.5 : 0.25;
        const auto start = static_cast<size_t> (std::llround ((0.0007 + k * period) * rate));
        for (int i = 0; i < static_cast<int> (0.025 * rate); ++i)
            x[start + static_cast<size_t> (i)] = static_cast<float> (a * std::sin (2 * pi * f * i / rate) * std::exp (-i / (0.006 * rate)));
    }
    return x;
}
} // namespace

TEST ("analysis: click onsets land within a fraction of a millisecond")
{
    const double rate = 48000;
    const auto x = click (rate, 40);
    const auto onsets = findOnsets (x.data(), static_cast<int> (x.size()), rate);

    CHECK (onsets.size() == 40);
    double worst = 0;
    for (size_t k = 0; k < onsets.size(); ++k)
        worst = std::max (worst, std::abs (onsets[k].time - (0.0007 + static_cast<double> (k) * 60.0 / 72)));
    CHECK (worst < 0.0005);
}

TEST ("analysis: accents are louder and brighter")
{
    const double rate = 44100;
    const auto x = click (rate, 16);
    const auto onsets = findOnsets (x.data(), static_cast<int> (x.size()), rate);
    CHECK (onsets.size() == 16);
    if (onsets.size() != 16)
        return;
    CHECK (onsets[0].peakDb > onsets[1].peakDb + 4);
    CHECK (onsets[4].centroidHz > onsets[5].centroidHz * 1.4);
    CHECK (std::abs (onsets[0].peakDb - 20 * std::log10 (0.5)) < 1.0);
}

TEST ("analysis: silence has no onsets")
{
    std::vector<float> x (48000, 0.0f);
    CHECK (findOnsets (x.data(), static_cast<int> (x.size()), 48000).empty());
}

TEST ("analysis: speech stretches join short gaps and are padded")
{
    // Noise bursts at 1.0–1.5 s and 1.8–2.2 s (a 0.3 s gap: one stretch) and 5.0–5.4 s.
    std::vector<float> x (16000 * 7, 0.0f);
    unsigned seed = 1;
    auto burst = [&] (double from, double to)
    {
        for (auto i = static_cast<size_t> (from * 16000); i < static_cast<size_t> (to * 16000); ++i)
        {
            seed = seed * 1664525u + 1013904223u;
            x[i] = 0.2f * (static_cast<float> (seed >> 8) / 16777216.0f - 0.5f);
        }
    };
    burst (1.0, 1.5);
    burst (1.8, 2.2);
    burst (5.0, 5.4);

    const auto spans = findSpeech (x.data(), static_cast<int> (x.size()));
    CHECK (spans.size() == 2);
    if (spans.size() == 2)
    {
        CHECK (std::abs (spans[0].start - 0.8) < 0.02);
        CHECK (std::abs (spans[0].end - 2.4) < 0.02);
        CHECK (std::abs (spans[1].start - 4.8) < 0.02);
        CHECK (std::abs (spans[1].end - 5.6) < 0.02);
    }
}
