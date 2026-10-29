// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The voice detector (VC-10): an utterance per burst of speech over a noise floor, with its lead-in, cut at 6 s,
// and nothing for a knock or for steady noise; the input path resamples a 48 kHz ring to 16 kHz.
#include "check.h"

#include "audio/voice_input.h"

#include <chrono>
#include <cmath>
#include <mutex>
#include <random>
#include <thread>

using rec::audio::VoiceActivity;
using rec::audio::VoiceInput;

namespace
{
constexpr int kRate = VoiceActivity::kRate;

// Noise at −60 dBFS with bursts of a 300 Hz "voice" at −20 dBFS.
std::vector<float> signal (std::initializer_list<std::pair<double, bool>> parts, double rate = kRate)
{
    std::mt19937 random (7);
    std::normal_distribution<float> noise (0.0f, 0.001f);
    std::vector<float> out;
    double t = 0;
    for (const auto& [seconds, voice] : parts)
    {
        const auto n = static_cast<int> (seconds * rate);
        for (int i = 0; i < n; ++i, t += 1.0 / rate)
            out.push_back (noise (random) + (voice ? 0.14f * static_cast<float> (std::sin (2 * 3.14159265 * 300 * t)) : 0.0f));
    }
    return out;
}

struct Heard
{
    std::vector<std::size_t> lengths;
    std::vector<int> trailing;
};

Heard run (const std::vector<float>& x)
{
    Heard heard;
    VoiceActivity vad ([&] (std::vector<float>&& samples, int trailing) {
        heard.lengths.push_back (samples.size());
        heard.trailing.push_back (trailing);
    });
    for (std::size_t i = 0; i < x.size(); i += 512)
        vad.feed (x.data() + i, static_cast<int> (std::min<std::size_t> (512, x.size() - i)));
    return heard;
}
} // namespace

TEST ("voice: one utterance per burst, with 300 ms before it and the 500 ms after")
{
    const auto heard = run (signal ({ { 1.0, false }, { 1.0, true }, { 1.0, false }, { 0.5, true }, { 1.0, false } }));
    CHECK (heard.lengths.size() == 2);
    if (heard.lengths.size() == 2)
    {
        const auto first = heard.lengths[0] / static_cast<double> (kRate);
        CHECK (first > 1.6 && first < 1.9); // 0.3 s lead-in + 1 s + 0.5 s of silence
        CHECK (heard.trailing[0] == 25 * VoiceActivity::kFrame);
        const auto second = heard.lengths[1] / static_cast<double> (kRate);
        CHECK (second > 1.1 && second < 1.4);
    }
}

TEST ("voice: a knock and steady noise are not speech, a long speech is cut at 6 s")
{
    CHECK (run (signal ({ { 1.0, false }, { 0.08, true }, { 1.0, false } })).lengths.empty());
    CHECK (run (signal ({ { 3.0, false } })).lengths.empty());

    const auto long_ = run (signal ({ { 0.5, false }, { 8.0, true }, { 1.0, false } }));
    CHECK (! long_.lengths.empty());
    CHECK (! long_.lengths.empty() && long_.lengths[0] == static_cast<std::size_t> (VoiceActivity::kMaxSamples));
}

TEST ("voice: the input resamples the device rate to 16 kHz and injected audio reaches the detector")
{
    std::mutex mutex;
    std::vector<double> durations;
    {
        VoiceInput input ([&] (std::vector<float>&&, double durationSec, double) {
            const std::lock_guard lock (mutex);
            durations.push_back (durationSec);
        });
        input.prepare (48000.0);
        std::this_thread::sleep_for (std::chrono::milliseconds (30));

        const auto x = signal ({ { 0.5, false }, { 1.0, true }, { 0.8, false } }, 48000.0);
        for (std::size_t i = 0; i < x.size(); i += 480)
            input.write (x.data() + i, static_cast<int> (std::min<std::size_t> (480, x.size() - i)));
        CHECK (input.takeLevelDb() > -20.0f);

        input.inject (signal ({ { 0.5, false }, { 0.6, true }, { 0.8, false } }));
        for (int i = 0; i < 100; ++i)
        {
            std::this_thread::sleep_for (std::chrono::milliseconds (20));
            const std::lock_guard lock (mutex);
            if (durations.size() >= 2)
                break;
        }
    }
    CHECK (durations.size() == 2);
    CHECK (durations.size() == 2 && durations[0] > 1.6 && durations[0] < 1.9);
    CHECK (durations.size() == 2 && durations[1] > 1.2 && durations[1] < 1.5);
}
