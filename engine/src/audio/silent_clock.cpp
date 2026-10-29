// SPDX-License-Identifier: AGPL-3.0-or-later
#include "silent_clock.h"

#include <algorithm>
#include <chrono>

namespace rec::audio
{
SilentClock::SilentClock (Render render)
    : juce::Thread ("RECompose silent clock"), render_ (std::move (render))
{
}

SilentClock::~SilentClock()
{
    stop();
}

std::int64_t SilentClock::steadyNowNs() noexcept
{
    return std::chrono::duration_cast<std::chrono::nanoseconds> (std::chrono::steady_clock::now().time_since_epoch()).count();
}

void SilentClock::start (double sampleRate, int numOutputs, std::int64_t backdateNs)
{
    stop();
    rate_ = sampleRate > 0.0 ? sampleRate : 48000.0;
    const auto channels = static_cast<std::size_t> (std::clamp (numOutputs, 2, 64));
    buffers_.assign (channels, std::vector<float> (kBlock, 0.0f));
    pointers_.assign (channels, nullptr);
    for (std::size_t c = 0; c < channels; ++c)
        pointers_[c] = buffers_[c].data();

    const auto now = steadyNowNs();
    // Backdating by more than a second would race through audio the disk thread has not read; nobody hears it anyway.
    startNs_ = backdateNs > 0 ? std::max<std::int64_t> (backdateNs, now - 1'000'000'000LL) : now;
    rendered_.store (0, std::memory_order_relaxed);
    running_.store (true, std::memory_order_release);
    startThread (juce::Thread::Priority::highest);
}

void SilentClock::stop()
{
    if (isThreadRunning())
    {
        signalThreadShouldExit();
        notify();
        stopThread (2000);
    }
    running_.store (false, std::memory_order_release);
}

void SilentClock::run()
{
    std::int64_t done = 0;
    auto startNs = startNs_;
    const auto channels = static_cast<int> (pointers_.size());

    while (! threadShouldExit())
    {
        const auto due = static_cast<std::int64_t> (static_cast<double> (steadyNowNs() - startNs) * rate_ / 1.0e9);

        // Whole blocks only, and a few at a time, so catching up never starves the disk thread for long.
        for (int n = 0; n < 8 && done + kBlock <= due && ! threadShouldExit(); ++n)
        {
            render_ (pointers_.data(), channels, kBlock);
            done += kBlock;
        }
        rendered_.store (done, std::memory_order_relaxed);

        // A machine that stalled for seconds does not race to catch up: the clock starts counting again from now.
        if (due - done > static_cast<std::int64_t> (rate_ * 2.0))
            startNs = steadyNowNs() - static_cast<std::int64_t> (static_cast<double> (done) * 1.0e9 / rate_);

        wait (2);
    }
}
} // namespace rec::audio
