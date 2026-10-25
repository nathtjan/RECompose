// SPDX-License-Identifier: AGPL-3.0-or-later
//
// A single-producer, single-consumer queue of fixed-size audio blocks, preallocated so
// neither side allocates. The disk thread produces; the audio thread consumes (PB-3).
// Each block is tagged with the request generation it was read for, so the audio thread
// can drop blocks from before a seek without the producer ever touching the read side.
#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace rec::audio
{
inline constexpr int kBlockFrames = 1024;
inline constexpr int kMaxTrackChannels = 2;

struct Block
{
    std::uint32_t generation = 0;
    std::uint32_t version = 0;   // the plan the block was read for (gap-free swap, protocol 8)
    std::int64_t startFrame = 0; // song frame (engine rate) of the first frame
    int frames = 0;
    float data[kBlockFrames * kMaxTrackChannels] {}; // interleaved
};

#if defined(_MSC_VER)
  #pragma warning(push)
  #pragma warning(disable : 4324) // padded on purpose: head and tail on separate cache lines
#endif

class BlockQueue
{
public:
    explicit BlockQueue (std::size_t capacity) : slots_ (capacity + 1) {}

    // Producer side.
    Block* writable() noexcept
    {
        const auto t = tail_.load (std::memory_order_relaxed);
        const auto next = (t + 1) % slots_.size();
        if (next == head_.load (std::memory_order_acquire))
            return nullptr;
        return &slots_[t];
    }

    void publish() noexcept
    {
        const auto t = tail_.load (std::memory_order_relaxed);
        tail_.store ((t + 1) % slots_.size(), std::memory_order_release);
    }

    // Consumer side.
    Block* front() noexcept
    {
        const auto h = head_.load (std::memory_order_relaxed);
        if (h == tail_.load (std::memory_order_acquire))
            return nullptr;
        return &slots_[h];
    }

    void pop() noexcept
    {
        const auto h = head_.load (std::memory_order_relaxed);
        head_.store ((h + 1) % slots_.size(), std::memory_order_release);
    }

    // Either side; approximate while the other side runs.
    std::size_t size() const noexcept
    {
        const auto h = head_.load (std::memory_order_acquire);
        const auto t = tail_.load (std::memory_order_acquire);
        return (t + slots_.size() - h) % slots_.size();
    }

    std::size_t capacity() const noexcept { return slots_.size() - 1; }

private:
    std::vector<Block> slots_;
    alignas (64) std::atomic<std::size_t> head_ { 0 };
    alignas (64) std::atomic<std::size_t> tail_ { 0 };
};

#if defined(_MSC_VER)
  #pragma warning(pop)
#endif
} // namespace rec::audio
