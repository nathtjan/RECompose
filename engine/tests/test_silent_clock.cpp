// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The silent clock (REL-21, AT-43): whole blocks at real-time pace, counted from a backdated start, and nothing after stop.
#include "check.h"

#include "audio/silent_clock.h"

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <thread>

using rec::audio::SilentClock;

namespace
{
constexpr double kRate = 48000.0;

std::int64_t framesIn (double seconds) { return static_cast<std::int64_t> (seconds * kRate); }
} // namespace

TEST ("silent clock renders in real time")
{
    std::atomic<std::int64_t> frames { 0 };
    std::atomic<int> widest { 0 };
    SilentClock clock ([&] (float* const* outputs, int numOutputs, int numSamples) {
        outputs[0][0] = 1.0f; // the buffers are writable and nobody hears them
        widest.store (numOutputs);
        frames.fetch_add (numSamples);
    });

    const auto started = std::chrono::steady_clock::now();
    clock.start (kRate, 4);
    CHECK (clock.running());
    std::this_thread::sleep_for (std::chrono::milliseconds (600));
    const auto elapsed = std::chrono::duration<double> (std::chrono::steady_clock::now() - started).count();
    clock.stop();
    CHECK (! clock.running());

    // Within two blocks and the scheduler's slack (40 ms) of the stopwatch, in whole blocks.
    const auto rendered = frames.load();
    CHECK (rendered % SilentClock::kBlock == 0);
    CHECK (std::llabs (rendered - framesIn (elapsed)) < framesIn (0.04) + 2 * SilentClock::kBlock);
    CHECK (widest.load() == 4);

    const auto after = frames.load();
    std::this_thread::sleep_for (std::chrono::milliseconds (50));
    CHECK (frames.load() == after); // stopped means stopped
}

TEST ("silent clock counts from the last block a device played")
{
    std::atomic<std::int64_t> frames { 0 };
    SilentClock clock ([&] (float* const*, int, int numSamples) { frames.fetch_add (numSamples); });

    // The device went quiet 300 ms ago: the clock renders that time at once, then goes on in real time. The sleep is
    // measured, not assumed: a busy machine (a CI runner) oversleeps, and the clock rightly renders that time too.
    const auto started = std::chrono::steady_clock::now();
    clock.start (kRate, 2, SilentClock::steadyNowNs() - 300'000'000LL);
    std::this_thread::sleep_for (std::chrono::milliseconds (100));
    const auto elapsed = std::chrono::duration<double> (std::chrono::steady_clock::now() - started).count();
    clock.stop();
    CHECK (frames.load() >= framesIn (0.35));
    CHECK (frames.load() <= framesIn (0.3 + elapsed + 0.04) + 2 * SilentClock::kBlock);

    // A backdate beyond a second is cut to one.
    frames.store (0);
    clock.start (kRate, 2, SilentClock::steadyNowNs() - 10'000'000'000LL);
    std::this_thread::sleep_for (std::chrono::milliseconds (100));
    clock.stop();
    CHECK (frames.load() <= framesIn (1.2));
}
