// SPDX-License-Identifier: AGPL-3.0-or-later
#include "audio/player.h"

#include "audio/pads.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <thread>

namespace rec::audio
{
namespace
{
    constexpr int kChunk = 256;
    constexpr float kMinusThreeDb = 0.70710678f;
    constexpr int kOfflineWaitLimitMs = 5000;
    constexpr float kPlanMuteRampMs = 10.0f; // AT-12

    // dB-linear fades from −60 dB to 0 (PB-40): t in [0, 1] is the progress through the fade.
    float fadeGain (float t) noexcept { return std::pow (10.0f, -3.0f * (1.0f - t)); }

    // The fade factor of a segment at a plan frame (1 outside the fades).
    float segmentFade (const PlanSegment& s, std::int64_t frame) noexcept
    {
        float g = 1.0f;
        if (s.fadeIn > 0 && frame < s.start + s.fadeIn)
            g *= fadeGain (static_cast<float> (frame - s.start) / static_cast<float> (s.fadeIn));
        if (s.fadeOut > 0 && frame > s.end - s.fadeOut)
            g *= fadeGain (static_cast<float> (s.end - frame) / static_cast<float> (s.fadeOut));
        return g;
    }

    std::int64_t nowNs() noexcept
    {
        return std::chrono::duration_cast<std::chrono::nanoseconds> (std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    void storeMax (std::atomic<float>& meter, float value) noexcept
    {
        if (value > meter.load (std::memory_order_relaxed))
            meter.store (value, std::memory_order_relaxed);
    }
} // namespace

Player::Player()
{
    prepare (48000.0, kChunk, 2);
}

bool Player::post (const Command& command) noexcept
{
    const int w = commandWrite_.load (std::memory_order_relaxed);
    const int next = (w + 1) % kFifoSize;
    if (next == commandRead_.load (std::memory_order_acquire))
        return false;
    commands_[static_cast<std::size_t> (w)] = command;
    commandWrite_.store (next, std::memory_order_release);
    return true;
}

Song* Player::takeRetiredSong() noexcept
{
    const int r = retiredRead_.load (std::memory_order_relaxed);
    if (r == retiredWrite_.load (std::memory_order_acquire))
        return nullptr;
    auto* song = retired_[static_cast<std::size_t> (r)];
    retiredRead_.store ((r + 1) % static_cast<int> (retired_.size()), std::memory_order_release);
    return song;
}

void Player::prepare (double sampleRate, int /*maxBlockSize*/, int /*numOutputChannels*/)
{
    sampleRate_ = sampleRate > 0.0 ? sampleRate : 48000.0;
    for (auto& s : scratch_)
        s.assign (kChunk, 0.0f);
}

void Player::drainCommands() noexcept
{
    for (;;)
    {
        const int r = commandRead_.load (std::memory_order_relaxed);
        if (r == commandWrite_.load (std::memory_order_acquire))
            return;
        const auto command = commands_[static_cast<std::size_t> (r)];
        commandRead_.store ((r + 1) % kFifoSize, std::memory_order_release);
        handle (command);
    }
}

void Player::handle (const Command& command) noexcept
{
    switch (command.type)
    {
        case Command::Type::setSong:
        {
            if (song_ != nullptr)
            {
                const int w = retiredWrite_.load (std::memory_order_relaxed);
                const int next = (w + 1) % static_cast<int> (retired_.size());
                if (next != retiredRead_.load (std::memory_order_acquire))
                {
                    retired_[static_cast<std::size_t> (w)] = song_;
                    retiredWrite_.store (next, std::memory_order_release);
                }
            }

            song_ = command.song;
            lastSegment_ = nullptr;
            plan_ = song_ != nullptr ? song_->plan.load (std::memory_order_acquire) : nullptr;
            pendingPlan_ = nullptr;
            segmentIndex_ = 0;
            transport_ = TransportState::stopped;
            fade_ = 0.0f;
            fadeTarget_ = 0.0f;
            fadeRemaining_ = 0;
            after_ = AfterFade::none;
            seekPending_ = false;
            waiting_ = false;

            if (song_ != nullptr)
            {
                const auto request = song_->currentRequest();
                readGeneration_ = request.generation;
                position_frames_ = request.frame;
            }
            else
            {
                position_frames_ = 0;
            }
            return;
        }

        case Command::Type::play:
            if (song_ == nullptr)
                return;
            if (transport_ == TransportState::playing)
            {
                if (after_ == AfterFade::pause || after_ == AfterFade::stopAndRewind)
                    startFade (1.0f, playFadeMs, AfterFade::none); // changed their mind
                return;
            }
            transport_ = TransportState::playing;
            fade_ = 0.0f;
            waiting_ = true; // starts as soon as every track has data at the position
            return;

        case Command::Type::pause:
            if (transport_ != TransportState::playing || after_ == AfterFade::stopAndRewind)
                return;
            if (waiting_)
            {
                transport_ = TransportState::paused;
                waiting_ = false;
                return;
            }
            startFade (0.0f, pauseFadeMs, AfterFade::pause);
            return;

        case Command::Type::stop:
            if (song_ == nullptr)
                return;
            if (transport_ == TransportState::playing && ! waiting_)
            {
                startFade (0.0f, stopFadeMs, AfterFade::stopAndRewind);
                return;
            }
            transport_ = TransportState::stopped;
            waiting_ = false;
            after_ = AfterFade::none;
            seekPending_ = false;
            rewindToStart();
            return;

        case Command::Type::setPlan:
            if (song_ == nullptr || command.plan == nullptr)
                return;
            pendingPlan_ = command.plan;
            seekTo (std::clamp<std::int64_t> (command.frame, 0, command.plan->lengthFrames), command.generation);
            return;

        case Command::Type::seek:
            if (song_ == nullptr)
                return;
            seekTo (std::clamp<std::int64_t> (command.frame, 0, planLength()), command.generation);
            return;
    }
}

void Player::seekTo (std::int64_t frame, std::uint32_t generation) noexcept
{
    if (transport_ == TransportState::playing && ! waiting_)
    {
        seekPending_ = true;
        seekGeneration_ = generation;
        seekFrame_ = frame;
        if (after_ == AfterFade::none || after_ == AfterFade::applySeek)
            startFade (0.0f, seekFadeMs, AfterFade::applySeek);
        return;
    }

    adoptPendingPlan();
    const auto current = song_->currentRequest();
    readGeneration_ = current.generation == generation ? generation : song_->requestFrame (frame).generation;
    position_frames_ = frame;
    dropStaleBlocks();
}

void Player::adoptPendingPlan() noexcept
{
    lastSegment_ = nullptr; // after a seek or a new plan the pads hear about the segment again
    if (pendingPlan_ != nullptr)
    {
        plan_ = pendingPlan_;
        pendingPlan_ = nullptr;
        segmentIndex_ = 0;
    }
}

std::int64_t Player::planLength() const noexcept
{
    return plan_ != nullptr ? plan_->lengthFrames : (song_ != nullptr ? song_->lengthFrames : 0);
}

// The segment that contains the playhead, or nullptr past the end of the plan.
const PlanSegment* Player::locateSegment() noexcept
{
    if (plan_ == nullptr)
        return nullptr;
    const auto& segments = plan_->segments;
    if (segmentIndex_ >= segments.size() || position_frames_ < segments[segmentIndex_].start)
        segmentIndex_ = 0;
    while (segmentIndex_ < segments.size() && position_frames_ >= segments[segmentIndex_].end)
        ++segmentIndex_;
    return segmentIndex_ < segments.size() && position_frames_ >= segments[segmentIndex_].start ? &segments[segmentIndex_] : nullptr;
}

void Player::startFade (float target, float milliseconds, AfterFade after) noexcept
{
    fadeTarget_ = target;
    fadeRemaining_ = std::max (1, static_cast<int> (std::lround (milliseconds * sampleRate_ / 1000.0)));
    fadeStep_ = (target - fade_) / static_cast<float> (fadeRemaining_);
    after_ = after;
}

void Player::rewindToStart() noexcept
{
    if (song_ == nullptr)
        return;
    adoptPendingPlan();
    readGeneration_ = song_->requestFrame (0).generation;
    position_frames_ = 0;
    dropStaleBlocks();
}

void Player::finishFade() noexcept
{
    const auto after = after_;
    after_ = AfterFade::none;

    auto reposition = [this]
    {
        adoptPendingPlan();
        const auto current = song_->currentRequest();
        readGeneration_ = current.generation == seekGeneration_ ? seekGeneration_
                                                                 : song_->requestFrame (seekFrame_).generation;
        position_frames_ = seekFrame_;
        seekPending_ = false;
        dropStaleBlocks();
    };

    switch (after)
    {
        case AfterFade::none:
            break;
        case AfterFade::pause:
            transport_ = TransportState::paused;
            if (seekPending_)
                reposition();
            break;
        case AfterFade::stopAndRewind:
            transport_ = TransportState::stopped;
            seekPending_ = false;
            rewindToStart();
            break;
        case AfterFade::applySeek:
            reposition();
            waiting_ = true; // resumes with a fade-in once the target is buffered
            break;
    }
}

void Player::dropStaleBlocks() noexcept
{
    if (song_ == nullptr)
        return;
    for (auto& track : song_->tracks)
    {
        while (auto* block = track->queue.front())
        {
            if (block->generation == readGeneration_)
                break;
            track->queue.pop();
        }
    }
}

bool Player::allTracksReady() noexcept
{
    for (auto& track : song_->tracks)
    {
        if (track->missing)
            continue;
        auto* block = track->queue.front();
        if (block == nullptr || block->generation != readGeneration_)
            return false;
        if (position_frames_ < block->startFrame || position_frames_ >= block->startFrame + block->frames)
            return false;
    }
    return true;
}

void Player::processCommandsOnly() noexcept
{
    drainCommands();
    if (song_ != nullptr && transport_ != TransportState::playing)
        dropStaleBlocks();
    publish();
}

void Player::process (float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept
{
    drainCommands();

    for (int ch = 0; ch < numOutputs; ++ch)
        if (outputs[ch] != nullptr)
            std::memset (outputs[ch], 0, sizeof (float) * static_cast<std::size_t> (numSamples));

    if (song_ == nullptr || transport_ != TransportState::playing)
    {
        // Idle: keep the queues moving so a seek or a load can buffer, and nothing else (PB-90).
        if (song_ != nullptr)
            dropStaleBlocks();
        publish();
        return;
    }

    int done = 0;
    int waitedMs = 0;
    while (done < numSamples && transport_ == TransportState::playing)
    {
        if (waiting_)
        {
            dropStaleBlocks();
            if (! allTracksReady())
            {
                if (! offline || waitedMs >= kOfflineWaitLimitMs)
                    break; // silence until the disk catches up
                std::this_thread::sleep_for (std::chrono::milliseconds (1));
                ++waitedMs;
                continue;
            }
            waiting_ = false;
            fade_ = 0.0f;
            startFade (1.0f, playFadeMs, AfterFade::none);
        }

        int frames = std::min (kChunk, numSamples - done);
        if (const auto* segment = locateSegment())
        {
            frames = static_cast<int> (std::min<std::int64_t> (frames, segment->end - position_frames_)); // mutes and fades change at the seams
            if (segment != lastSegment_)
            {
                lastSegment_ = segment;
                if (pads_ != nullptr)
                    pads_->onSegment (*segment); // Follow song and pad bars (SL-44, ARR-39)
            }
        }
        renderChunk (outputs, numOutputs, done, frames, offline);
        done += frames;
    }

    publish();
}

int Player::pullFrames (Track& track, std::int64_t from, int frames, bool offline) noexcept
{
    const int channels = track.channels;
    int i = 0;
    int waitedMs = 0;

    while (i < frames)
    {
        auto* block = track.queue.front();
        if (block == nullptr)
        {
            if (offline && waitedMs < kOfflineWaitLimitMs)
            {
                std::this_thread::sleep_for (std::chrono::milliseconds (1));
                ++waitedMs;
                continue;
            }
            break;
        }

        if (block->generation != readGeneration_)
        {
            track.queue.pop();
            continue;
        }

        const auto offset = from + i - block->startFrame;
        if (offset >= block->frames)
        {
            track.queue.pop();
            continue;
        }

        if (offset < 0)
        {
            const int gap = static_cast<int> (std::min<std::int64_t> (frames - i, -offset));
            for (int c = 0; c < channels; ++c)
                std::fill_n (scratch_[c].data() + i, gap, 0.0f);
            i += gap;
            continue;
        }

        const int take = static_cast<int> (std::min<std::int64_t> (frames - i, block->frames - offset));
        const float* src = block->data + offset * channels;
        for (int k = 0; k < take; ++k)
            for (int c = 0; c < channels; ++c)
                scratch_[c][static_cast<std::size_t> (i + k)] = src[k * channels + c];

        i += take;
        if (offset + take == block->frames)
            track.queue.pop();
    }

    if (i < frames)
    {
        for (int c = 0; c < channels; ++c)
            std::fill_n (scratch_[c].data() + i, frames - i, 0.0f);
        starved_.fetch_add (1, std::memory_order_relaxed);
    }

    return i;
}

void Player::renderChunk (float* const* outputs, int numOutputs, int offset, int frames, bool offline) noexcept
{
    bool anySolo = false;
    for (auto& track : song_->tracks)
        if (! track->missing && ! track->spec.soloSafe && track->solo.load (std::memory_order_relaxed))
            anySolo = true;

    const auto* segment = locateSegment();
    const float planRamp = 1.0f / std::max (1.0f, kPlanMuteRampMs * static_cast<float> (sampleRate_) / 1000.0f);

    for (std::size_t trackIndex = 0; trackIndex < song_->tracks.size(); ++trackIndex)
    {
        auto& track = *song_->tracks[trackIndex];
        if (track.missing || track.channels == 0)
            continue;

        pullFrames (track, position_frames_, frames, offline);

        // The arrangement's mutes and fades for this segment (PB-40). Click and guide keep playing
        // through fades; a mute listed for them is honoured.
        const float planTarget = segment != nullptr && trackIndex < segment->muted.size() && segment->muted[trackIndex] != 0 ? 0.0f : 1.0f;
        float fadeStart = 1.0f, fadeEnd = 1.0f;
        if (segment != nullptr && ! track.spec.soloSafe && (segment->fadeIn > 0 || segment->fadeOut > 0))
        {
            fadeStart = segmentFade (*segment, position_frames_);
            fadeEnd = segmentFade (*segment, position_frames_ + frames);
        }
        float planGain = track.planGain;

        float target = track.gain.load (std::memory_order_relaxed);
        if (track.mute.load (std::memory_order_relaxed))
            target = 0.0f;
        else if (anySolo && ! track.spec.soloSafe && ! track.solo.load (std::memory_order_relaxed))
            target = 0.0f;

        const float start = track.appliedGain;
        const float step = (target - start) / static_cast<float> (frames);
        const int out = track.outputChannel.load (std::memory_order_relaxed);
        const int width = track.outputWidth.load (std::memory_order_relaxed);
        const bool stereo = track.channels == 2;
        const float* left = scratch_[0].data();
        const float* right = stereo ? scratch_[1].data() : left;

        float* outA = out >= 0 && out < numOutputs ? outputs[out] : nullptr;
        float* outB = width == 2 && out + 1 >= 0 && out + 1 < numOutputs ? outputs[out + 1] : nullptr;

        float peak = 0.0f;
        for (int i = 0; i < frames; ++i)
        {
            planGain += std::clamp (planTarget - planGain, -planRamp, planRamp);
            const float fade = fadeStart + (fadeEnd - fadeStart) * static_cast<float> (i + 1) / static_cast<float> (frames);
            const float g = (start + step * static_cast<float> (i + 1)) * planGain * fade;
            const float l = left[i] * g;
            const float r = right[i] * g;
            peak = std::max (peak, std::max (std::abs (l), std::abs (r)));

            if (width == 2)
            {
                // Mono to a pair: the same level on both channels; stereo: left and right.
                if (outA != nullptr)
                    outA[offset + i] += l;
                if (outB != nullptr)
                    outB[offset + i] += r;
            }
            else if (outA != nullptr)
            {
                // Stereo to one channel: summed at −3 dB (PB-85).
                outA[offset + i] += stereo ? (l + r) * kMinusThreeDb : l;
            }
        }

        track.appliedGain = target;
        track.planGain = planGain;
        storeMax (track.peak, peak);
    }

    // Master gain and the transport fade, then the master meters.
    const float master = song_->masterGain.load (std::memory_order_relaxed);
    float masterPeak[2] { 0.0f, 0.0f };
    for (int i = 0; i < frames; ++i)
    {
        if (fadeRemaining_ > 0)
        {
            fade_ += fadeStep_;
            if (--fadeRemaining_ == 0)
                fade_ = fadeTarget_;
        }

        const float g = fade_ * master;
        for (int ch = 0; ch < numOutputs; ++ch)
        {
            if (outputs[ch] == nullptr)
                continue;
            auto& sample = outputs[ch][offset + i];
            sample *= g;
            if (ch < 2)
                masterPeak[ch] = std::max (masterPeak[ch], std::abs (sample));
        }
    }
    storeMax (song_->masterPeak[0], masterPeak[0]);
    storeMax (song_->masterPeak[1], masterPeak[1]);

    position_frames_ += frames;

    if (after_ != AfterFade::none && fadeRemaining_ == 0)
        finishFade();

    // End of the song: return to the start, stopped (PB-25 without a transition).
    if (transport_ == TransportState::playing && ! waiting_ && position_frames_ >= planLength())
    {
        transport_ = TransportState::stopped;
        after_ = AfterFade::none;
        rewindToStart();
    }
}

void Player::publish() noexcept
{
    state_.store (static_cast<int> (transport_), std::memory_order_relaxed);
    position_.store (position_frames_, std::memory_order_relaxed);
    positionTimeNs_.store (nowNs(), std::memory_order_relaxed);
    waitingPublished_.store (waiting_ && transport_ == TransportState::playing, std::memory_order_relaxed);
}
} // namespace rec::audio
