// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Transitions between two songs (SL-20, SL-30), run on the audio thread so the next song starts on
// the exact sample. The engine keeps the current song and the next one loaded, each with its own
// player; this watches the current one and starts the next:
//
//   segue      when the tail begins, plus a gap, the next starts and the tail fades over it;
//   crossfade  when the fade begins, the next starts at full level and the current fades out;
//   pad        when the tail begins, the pad fades in and the transition click runs; Play (or a
//              number of bars) starts the next song on the next click downbeat.
//
// Commands come from the message thread through a FIFO; the audio thread owns the state.
#pragma once

#include "audio/pads.h"
#include "audio/player.h"

#include <array>
#include <atomic>
#include <cstdint>

namespace rec::audio
{
struct TransitionCommand
{
    enum class Type
    {
        arm,       // describe the transition out of the current song
        startNext, // Play: start the next song (on the next click downbeat in a pad transition)
        cancel     // forget it (Stop, a new song)
    };

    enum class Kind
    {
        none,
        segue,
        crossfade,
        pad
    };

    Type type = Type::cancel;
    Kind kind = Kind::none;
    std::int64_t trigger = 0;   // output frames of the current plan where the transition starts
    std::int64_t gap = 0;       // segue: frames after the trigger
    std::int64_t fade = 0;      // crossfade: frames the current song fades over
    int padKey = -1;            // pad: the pitch class to fade in, -1 for none
    bool click = false;         // pad: run the transition click
    int clickChannel = 0;
    int clickWidth = 2;
    std::int64_t autoStart = -1; // pad: frames after the trigger when the next song starts by itself, -1 never
    bool fadePadOut = true;      // pad: fade the pad out when the next song starts
    int retune = -1;             // startNext: the pad key the song's key moves it to, -1 none
    const Plan* clickPlan = nullptr; // pad: one click segment on the transition's grid, owned by the engine
};

class Transitioner
{
public:
    enum class Phase : int
    {
        idle = 0,
        armed,    // waiting for the current song to reach the trigger
        segueGap, // the trigger passed; the next song starts after the gap
        padWait,  // the pad sounds; waiting for Play or the automatic start
        handing   // the next song plays; the current one is ending
    };

    bool post (const TransitionCommand& command) noexcept; // message thread
    void prepare (double sampleRate) noexcept { sampleRate_ = sampleRate; }

    // Audio thread: adds the current and next players and the transition click to `outputs`.
    void process (Player& current, Player& next, PadPlayer& pads, float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept;

    Phase phase() const noexcept { return static_cast<Phase> (phase_.load (std::memory_order_relaxed)); }

    // True once when the next song has taken over and the current one has stopped.
    bool takeHandover() noexcept { return handover_.exchange (false); }

private:
    void drain (Player& current, Player& next, PadPlayer& pads) noexcept;
    void handle (const TransitionCommand& command, Player& current, Player& next, PadPlayer& pads) noexcept;
    void startNext (Player& next, PadPlayer& pads) noexcept;
    void fire (Player& current, Player& next, PadPlayer& pads) noexcept;
    void setPhase (Phase p) noexcept { phase_.store (static_cast<int> (p), std::memory_order_relaxed); }

    static constexpr int kFifoSize = 16;
    std::array<TransitionCommand, kFifoSize> commands_ {};
    std::atomic<int> read_ { 0 }, write_ { 0 };

    // Audio thread only.
    TransitionCommand spec_;
    Phase current_ = Phase::idle;
    bool passed_ = false;          // the playhead was already past the trigger when armed
    std::uint32_t seenEnds_ = 0;   // the current song's end count when last looked at
    std::int64_t since_ = 0;       // frames since the trigger
    bool clicking_ = false;
    bool startRequested_ = false;
    int retune_ = -1;
    double sampleRate_ = 48000.0;
    std::array<float, 1024> clickBuffer_ {};

    std::atomic<int> phase_ { 0 };
    std::atomic<bool> handover_ { false };
};
} // namespace rec::audio
