// SPDX-License-Identifier: AGPL-3.0-or-later
#include "audio/transition.h"

#include "audio/click.h"

#include <algorithm>
#include <cmath>

namespace rec::audio
{
namespace
{
    constexpr int kMaxOutputs = 64;
} // namespace

bool Transitioner::post (const TransitionCommand& command) noexcept
{
    const int w = write_.load (std::memory_order_relaxed);
    const int next = (w + 1) % kFifoSize;
    if (next == read_.load (std::memory_order_acquire))
        return false;
    commands_[static_cast<std::size_t> (w)] = command;
    write_.store (next, std::memory_order_release);
    return true;
}

void Transitioner::drain (Player& current, Player& next, PadPlayer& pads) noexcept
{
    for (;;)
    {
        const int r = read_.load (std::memory_order_relaxed);
        if (r == write_.load (std::memory_order_acquire))
            return;
        const auto command = commands_[static_cast<std::size_t> (r)];
        read_.store ((r + 1) % kFifoSize, std::memory_order_release);
        handle (command, current, next, pads);
    }
}

void Transitioner::handle (const TransitionCommand& command, Player& current, Player& next, PadPlayer& pads) noexcept
{
    switch (command.type)
    {
        case TransitionCommand::Type::arm:
            spec_ = command;
            since_ = 0;
            clicking_ = false;
            startRequested_ = false;
            retune_ = -1;
            passed_ = current.audioPosition() >= command.trigger; // armed in the tail: wait for a seek back
            seenEnds_ = current.endCount();
            current_ = command.kind == TransitionCommand::Kind::none ? Phase::idle : Phase::armed;
            setPhase (current_);
            return;

        case TransitionCommand::Type::startNext:
            if (current_ == Phase::padWait)
            {
                startRequested_ = true;
                retune_ = command.retune;
            }
            else if (current_ == Phase::idle || current_ == Phase::armed)
            {
                // A cued song with nothing to wait for (a Stop or Carry over that ended): Play starts it.
                next.playNow();
                if (command.retune >= 0)
                    pads.playNow (command.retune);
                current_ = Phase::handing;
                setPhase (current_);
            }
            return;

        case TransitionCommand::Type::cancel:
            current_ = Phase::idle;
            clicking_ = false;
            startRequested_ = false;
            setPhase (current_);
            return;
    }
}

void Transitioner::fire (Player& current, Player& next, PadPlayer& pads) noexcept
{
    since_ = 0;
    if (spec_.kind == TransitionCommand::Kind::segue)
    {
        if (spec_.gap <= 0)
            startNext (next, pads);
        else
            setPhase (current_ = Phase::segueGap);
    }
    else if (spec_.kind == TransitionCommand::Kind::crossfade)
    {
        next.playNow();
        current.fadeStopNow (spec_.fade);
        setPhase (current_ = Phase::handing);
    }
    else
    {
        if (spec_.padKey >= 0)
            pads.playNow (spec_.padKey);
        clicking_ = spec_.click && spec_.clickPlan != nullptr;
        setPhase (current_ = Phase::padWait);
    }
}

void Transitioner::startNext (Player& next, PadPlayer& pads) noexcept
{
    next.playNow();
    clicking_ = false;
    if (spec_.kind == TransitionCommand::Kind::pad)
    {
        if (spec_.fadePadOut)
            pads.playNow (-1); // the pad fades out once the next song has started (SL-30)
        else if (retune_ >= 0)
            pads.playNow (retune_);
    }
    startRequested_ = false;
    current_ = Phase::handing;
    setPhase (current_);
}

void Transitioner::process (Player& current, Player& next, PadPlayer& pads, float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept
{
    drain (current, next, pads);
    numOutputs = std::min (numOutputs, kMaxOutputs);
    float* shifted[kMaxOutputs];

    int done = 0;
    while (done < numSamples)
    {
        int frames = std::min (numSamples - done, static_cast<int> (clickBuffer_.size()));

        // What happens at this frame, and how far the chunk may run before the next event.
        switch (current_)
        {
            case Phase::armed:
            {
                // A trigger at the very end of the plan (no tail) is crossed by the plan ending: the player
                // rewinds in the same chunk, so the playhead never shows it.
                const auto ends = current.endCount();
                const bool ended = ends != seenEnds_;
                seenEnds_ = ends;
                if (ended && ! passed_)
                {
                    fire (current, next, pads);
                    continue;
                }

                if (current.playingNow())
                {
                    const auto pos = current.audioPosition();
                    if (passed_ && pos < spec_.trigger)
                        passed_ = false;
                    if (! passed_)
                    {
                        if (pos >= spec_.trigger)
                        {
                            fire (current, next, pads);
                            continue;
                        }

                        frames = static_cast<int> (std::min<std::int64_t> (frames, spec_.trigger - pos));
                    }
                }
                break;
            }

            case Phase::segueGap:
                if (since_ >= spec_.gap)
                {
                    startNext (next, pads);
                    continue;
                }
                frames = static_cast<int> (std::min<std::int64_t> (frames, spec_.gap - since_));
                break;

            case Phase::padWait:
            {
                if (! startRequested_ && spec_.autoStart >= 0 && since_ >= spec_.autoStart)
                    startRequested_ = true;

                if (startRequested_)
                {
                    if (! clicking_)
                    {
                        startNext (next, pads);
                        continue;
                    }

                    // The next song starts on the next downbeat of the transition click.
                    const double bar = spec_.clickPlan->beatFrames * spec_.clickPlan->beatsPerBar;
                    const auto downbeat = static_cast<std::int64_t> (std::llround (std::ceil (static_cast<double> (since_) / bar - 1.0e-9) * bar));
                    if (since_ >= downbeat)
                    {
                        startNext (next, pads);
                        continue;
                    }
                    frames = static_cast<int> (std::min<std::int64_t> (frames, downbeat - since_));
                }
                else if (spec_.autoStart >= 0)
                {
                    frames = static_cast<int> (std::min<std::int64_t> (frames, spec_.autoStart - since_));
                }
                break;
            }

            case Phase::handing:
                if (! current.playingNow() && next.playingNow())
                {
                    handover_.store (true, std::memory_order_release);
                    setPhase (current_ = Phase::idle);
                    continue;
                }
                break;

            case Phase::idle:
                break;
        }

        frames = std::max (1, frames);
        for (int ch = 0; ch < numOutputs; ++ch)
            shifted[ch] = outputs[ch] != nullptr ? outputs[ch] + done : nullptr;

        current.process (shifted, numOutputs, frames, offline, true);
        next.process (shifted, numOutputs, frames, offline, true);

        if (clicking_)
        {
            std::fill_n (clickBuffer_.begin(), frames, 0.0f);
            renderClick (*spec_.clickPlan, since_, frames, sampleRate_, clickBuffer_.data());
            const int a = spec_.clickChannel;
            if (a >= 0 && a < numOutputs && shifted[a] != nullptr)
                for (int i = 0; i < frames; ++i)
                    shifted[a][i] += clickBuffer_[static_cast<std::size_t> (i)];
            if (spec_.clickWidth == 2 && a + 1 < numOutputs && shifted[a + 1] != nullptr)
                for (int i = 0; i < frames; ++i)
                    shifted[a + 1][i] += clickBuffer_[static_cast<std::size_t> (i)];
        }

        if (current_ == Phase::segueGap || current_ == Phase::padWait)
            since_ += frames;
        done += frames;
    }

    // The handover can complete in the last chunk (the current song just ended).
    if (current_ == Phase::handing && ! current.playingNow() && next.playingNow())
    {
        handover_.store (true, std::memory_order_release);
        setPhase (current_ = Phase::idle);
    }
}
} // namespace rec::audio
