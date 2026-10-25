// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The real-time part of the engine (PB-3): runs in the audio callback, or on the message
// thread for offline renders and while no device is running. No allocation, locks, I/O
// or logging in process(). Commands arrive through a lock-free FIFO from the message
// thread; replaced songs go back through another FIFO to be freed there.
//
// Transport (PB-20…22, PB-26): Play fades in over 10 ms, Pause fades out over 20 ms,
// Stop fades out over 50 ms and returns to the start, a seek while playing fades out
// over 10 ms and resumes at the target as soon as every track has data there.
#pragma once

#include "audio/song.h"

namespace rec::audio
{
class PadPlayer;
}

#include <array>
#include <atomic>
#include <cstdint>
#include <vector>

namespace rec::audio
{
enum class TransportState : int
{
    stopped = 0,
    playing = 1,
    paused = 2
};

struct Command
{
    enum class Type
    {
        play,
        pause,
        stop,
        seek,
        setSong,
        setPlan // like a seek to `frame`, and the plan takes effect there (PB-40)
    };

    Type type = Type::play;
    std::int64_t frame = 0;
    std::uint32_t generation = 0;
    Song* song = nullptr;
    const Plan* plan = nullptr; // setPlan: owned by the song
};

class Player
{
public:
    Player();

    // Message thread. Returns false if the FIFO is full.
    bool post (const Command& command) noexcept;

    // Message thread, before any process() at this configuration (allocates).
    void prepare (double sampleRate, int maxBlockSize, int numOutputChannels);

    // Audio thread (or message thread when no device runs). Writes numSamples frames to
    // every output channel. With offline = true it waits for missing data instead of
    // playing silence, so renders are deterministic.
    void process (float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept;

    // Message thread: applies queued commands without producing audio (no device running).
    void processCommandsOnly() noexcept;

    // Message thread: a song the audio side no longer uses, or nullptr.
    Song* takeRetiredSong() noexcept;

    // Message thread, before the device starts: the pads told about each segment the song enters
    // (Follow song, pad bars). They are mixed by their own player.
    void setPads (PadPlayer* pads) noexcept { pads_ = pads; }

    // Snapshot values, readable from any thread.
    TransportState state() const noexcept { return static_cast<TransportState> (state_.load()); }
    std::int64_t position() const noexcept { return position_.load(); }
    std::int64_t positionTimeNs() const noexcept { return positionTimeNs_.load(); }
    std::uint64_t starvedBlocks() const noexcept { return starved_.load(); }
    bool waitingForData() const noexcept { return waitingPublished_.load(); }

    static constexpr float playFadeMs = 10.0f;
    static constexpr float pauseFadeMs = 20.0f;
    static constexpr float stopFadeMs = 50.0f;
    static constexpr float seekFadeMs = 10.0f;

private:
    enum class AfterFade
    {
        none,
        pause,
        stopAndRewind,
        applySeek
    };

    void drainCommands() noexcept;
    void handle (const Command& command) noexcept;
    void startFade (float target, float milliseconds, AfterFade after) noexcept;
    void finishFade() noexcept;
    void rewindToStart() noexcept;
    void dropStaleBlocks() noexcept;
    bool allTracksReady() noexcept;
    void renderChunk (float* const* outputs, int numOutputs, int offset, int frames, bool offline) noexcept;
    void adoptPendingPlan() noexcept;
    std::int64_t planLength() const noexcept;
    const PlanSegment* locateSegment() noexcept;
    void seekTo (std::int64_t frame, std::uint32_t generation) noexcept;
    int pullFrames (Track& track, std::int64_t from, int frames, bool offline) noexcept;
    void publish() noexcept;

    // FIFOs (single producer, single consumer).
    static constexpr int kFifoSize = 256;
    std::array<Command, kFifoSize> commands_ {};
    std::atomic<int> commandRead_ { 0 }, commandWrite_ { 0 };
    std::array<Song*, 16> retired_ {};
    std::atomic<int> retiredRead_ { 0 }, retiredWrite_ { 0 };

    // Audio-side state.
    Song* song_ = nullptr;
    TransportState transport_ = TransportState::stopped;
    std::uint32_t readGeneration_ = 0;
    std::int64_t position_frames_ = 0;
    bool waiting_ = false;
    float fade_ = 0.0f;
    float fadeTarget_ = 0.0f;
    float fadeStep_ = 0.0f;
    int fadeRemaining_ = 0; // frames until fade_ reaches fadeTarget_
    AfterFade after_ = AfterFade::none;
    bool seekPending_ = false;
    std::uint32_t seekGeneration_ = 0;
    std::int64_t seekFrame_ = 0;
    double sampleRate_ = 48000.0;
    PadPlayer* pads_ = nullptr;
    const PlanSegment* lastSegment_ = nullptr; // the segment the pads were last told about
    const Plan* plan_ = nullptr;        // the plan in play
    const Plan* pendingPlan_ = nullptr; // takes effect when the pending seek lands
    std::size_t segmentIndex_ = 0;

    std::vector<float> scratch_[kMaxTrackChannels];
    std::vector<float> mixScratch_;

    // Published.
    std::atomic<int> state_ { 0 };
    std::atomic<std::int64_t> position_ { 0 };
    std::atomic<std::int64_t> positionTimeNs_ { 0 };
    std::atomic<std::uint64_t> starved_ { 0 };
    std::atomic<bool> waitingPublished_ { false };
};
} // namespace rec::audio
