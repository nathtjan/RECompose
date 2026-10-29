// SPDX-License-Identifier: AGPL-3.0-or-later
//
// MIDI out (protocol 13, AUTO-5, AUTO-21): the audio thread copies each plan message the playhead passes into this
// lock-free ring (PB-3: no allocation, no locks, no I/O); the engine's MIDI thread takes them and sends them.
#pragma once

#include "audio/song.h"

#include <array>
#include <atomic>

namespace rec::audio
{
class MidiOutQueue
{
public:
    // Audio thread (one producer at a time). A full ring drops the message.
    void push (const PlanMidi& message) noexcept
    {
        const auto w = write_.load (std::memory_order_relaxed);
        const auto next = (w + 1) % kSize;
        if (next == read_.load (std::memory_order_acquire))
            return;
        ring_[w] = message;
        write_.store (next, std::memory_order_release);
    }

    // The MIDI thread.
    bool pop (PlanMidi& out) noexcept
    {
        const auto r = read_.load (std::memory_order_relaxed);
        if (r == write_.load (std::memory_order_acquire))
            return false;
        out = ring_[r];
        read_.store ((r + 1) % kSize, std::memory_order_release);
        return true;
    }

private:
    static constexpr std::size_t kSize = 1024;
    std::array<PlanMidi, kSize> ring_ {};
    std::atomic<std::size_t> read_ { 0 }, write_ { 0 };
};
} // namespace rec::audio
