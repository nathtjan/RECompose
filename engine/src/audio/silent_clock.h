// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The clock that keeps time when no audio device does (REL-21, M6 decision 4): it renders the same audio graph in whole
// blocks at real-time pace into a buffer nobody hears, so the plan moves on and a device that takes over later starts at
// the right bar. It is a fallback, not the playing path: the device callback stops it before it processes anything, so
// only one thread renders at a time.
#pragma once

#include <juce_core/juce_core.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <vector>

namespace rec::audio
{
class SilentClock : private juce::Thread
{
public:
    // Called on the clock's thread for each block; it must be as real-time safe as the device callback.
    using Render = std::function<void (float* const* outputs, int numOutputs, int numSamples)>;

    explicit SilentClock (Render render);
    ~SilentClock() override;

    // Message thread. backdateNs is a monotonic time (steadyNowNs) the clock counts from: the last block a device
    // played, so the time between the loss and noticing it is rendered too. 0 starts from now.
    void start (double sampleRate, int numOutputs, std::int64_t backdateNs = 0);
    void stop();

    // Any thread.
    bool running() const noexcept { return running_.load (std::memory_order_acquire); }
    std::int64_t framesRendered() const noexcept { return rendered_.load (std::memory_order_relaxed); }

    static std::int64_t steadyNowNs() noexcept;

    static constexpr int kBlock = 512;

private:
    void run() override;

    Render render_;
    double rate_ = 48000.0;
    std::int64_t startNs_ = 0;
    std::vector<std::vector<float>> buffers_;
    std::vector<float*> pointers_;
    std::atomic<bool> running_ { false };
    std::atomic<std::int64_t> rendered_ { 0 };
};
} // namespace rec::audio
