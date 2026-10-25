// SPDX-License-Identifier: AGPL-3.0-or-later
#include "audio/pads.h"

#include "audio/disk_streamer.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <thread>

namespace rec::audio
{
namespace
{
    constexpr double kHalfPi = 1.5707963267948966;
    constexpr double kLoopCrossfadeSec = 2.0; // SL-46
    constexpr int kChunk = 1024;
    constexpr float kMinusThreeDb = 0.70710678f;

    // Loop points in engine frames: the file's own (WAV `smpl`), else 25 % to 90 % of it (SL-46).
    void setLoop (PadKey& key, std::int64_t length, double engineRate)
    {
        auto& track = *key.track;
        std::int64_t start = static_cast<std::int64_t> (static_cast<double> (length) * 0.25);
        std::int64_t end = static_cast<std::int64_t> (static_cast<double> (length) * 0.90);

        if (track.reader != nullptr)
        {
            const auto& meta = track.reader->metadataValues;
            if (meta.getValue ("NumSampleLoops", "0").getIntValue() > 0)
            {
                const auto scale = engineRate / track.sourceRate;
                const auto s = static_cast<std::int64_t> (meta.getValue ("Loop0Start", "0").getLargeIntValue() * scale);
                const auto e = static_cast<std::int64_t> (meta.getValue ("Loop0End", "0").getLargeIntValue() * scale);
                if (e > s && e <= length)
                {
                    start = s;
                    end = e;
                }
            }
        }

        key.loopStart = start;
        key.loopEnd = end;
        key.crossfade = std::min<std::int64_t> (static_cast<std::int64_t> (kLoopCrossfadeSec * engineRate), (end - start) / 2);
    }
} // namespace

std::unique_ptr<PadBank> loadPadBank (std::string id, const std::array<std::string, kPadKeys>& files, double engineRate,
                                      juce::AudioFormatManager& formats)
{
    auto bank = std::make_unique<PadBank>();
    bank->id = std::move (id);
    bank->sampleRate = engineRate;
    const auto capacity = static_cast<std::size_t> (std::ceil (engineRate * 2.0 / kBlockFrames)) + 2; // 2 s read-ahead

    for (int k = 0; k < kPadKeys; ++k)
    {
        TrackSpec spec;
        spec.id = "pad-" + std::to_string (k);
        spec.file = files[static_cast<std::size_t> (k)];
        auto& key = bank->keys[static_cast<std::size_t> (k)];
        key.track = std::make_unique<Track> (spec, capacity);
        const auto length = spec.file.empty() ? 0 : openTrackFile (*key.track, engineRate, formats);
        if (spec.file.empty())
            key.track->missing = true;
        setLoop (key, length, engineRate);
    }

    return bank;
}

int fillPads (PadBank& bank, int maxBlocks)
{
    int written = 0;
    for (auto& key : bank.keys)
    {
        auto& track = *key.track;
        if (track.missing || track.reader == nullptr || ! key.wanted.load (std::memory_order_acquire))
            continue;

        const double ratio = track.sourceRate / bank.sampleRate;
        const auto generation = key.generation.load (std::memory_order_acquire);
        if (generation != key.diskGeneration)
        {
            key.diskGeneration = generation;
            key.diskPos = 0;
            key.inCrossfade = false;
            track.mainStream = 0;
            for (auto& stream : track.streams)
                stream.clear();
            startStream (track, track.streams[0], 0, ratio);
        }

        const int channels = track.channels;
        const bool loops = key.crossfade > 0 && key.loopEnd > key.loopStart;
        for (int n = 0; n < maxBlocks; ++n)
        {
            if (key.generation.load (std::memory_order_acquire) != key.diskGeneration)
                break;
            auto* block = track.queue.writable();
            if (block == nullptr)
                break;

            const auto p = key.diskPos;
            const auto fadeStart = key.loopEnd - key.crossfade;
            int frames = kBlockFrames;
            if (loops)
                frames = static_cast<int> (std::min<std::int64_t> (kBlockFrames, p < fadeStart ? fadeStart - p : key.loopEnd - p));
            frames = std::max (1, frames);

            float* main[kMaxTrackChannels] { track.mainBuffer[0].data(), channels > 1 ? track.mainBuffer[1].data() : nullptr };
            readStream (track, track.streams[track.mainStream], main, frames, bank.sampleRate);

            if (loops && p >= fadeStart)
            {
                // The loop seam: the end fades out while the loop start fades in, at equal power (SL-46).
                auto& incoming = track.streams[track.mainStream ^ 1];
                if (! key.inCrossfade)
                {
                    startStream (track, incoming, key.loopStart, ratio);
                    key.inCrossfade = true;
                }
                float* tail[kMaxTrackChannels] { track.tailBuffer[0].data(), channels > 1 ? track.tailBuffer[1].data() : nullptr };
                readStream (track, incoming, tail, frames, bank.sampleRate);
                for (int i = 0; i < frames; ++i)
                {
                    const double t = static_cast<double> (p + i - fadeStart) / static_cast<double> (key.crossfade);
                    const auto out = static_cast<float> (std::cos (t * kHalfPi));
                    const auto in = static_cast<float> (std::sin (t * kHalfPi));
                    for (int c = 0; c < channels; ++c)
                        main[c][i] = main[c][i] * out + tail[c][i] * in;
                }
            }

            for (int i = 0; i < frames; ++i)
                for (int c = 0; c < channels; ++c)
                    block->data[i * channels + c] = main[c][i];
            block->generation = key.diskGeneration;
            block->startFrame = p;
            block->frames = frames;
            track.queue.publish();
            ++written;

            key.diskPos = p + frames;
            if (loops && key.diskPos >= key.loopEnd)
            {
                // The incoming stream carries on; it has played the crossfade already.
                track.streams[track.mainStream].valid = false;
                track.mainStream ^= 1;
                key.diskPos = key.loopStart + key.crossfade;
                key.inCrossfade = false;
            }
        }
    }

    return written;
}

//==============================================================================
PadPlayer::PadPlayer()
{
    prepare (48000.0);
}

void PadPlayer::prepare (double sampleRate)
{
    sampleRate_ = sampleRate > 0.0 ? sampleRate : 48000.0;
    left_.assign (kChunk, 0.0f);
    right_.assign (kChunk, 0.0f);
}

bool PadPlayer::post (const PadCommand& command) noexcept
{
    const int w = write_.load (std::memory_order_relaxed);
    const int next = (w + 1) % kFifoSize;
    if (next == read_.load (std::memory_order_acquire))
        return false;
    commands_[static_cast<std::size_t> (w)] = command;
    write_.store (next, std::memory_order_release);
    return true;
}

PadBank* PadPlayer::takeRetiredBank() noexcept
{
    const int r = retiredRead_.load (std::memory_order_relaxed);
    if (r == retiredWrite_.load (std::memory_order_acquire))
        return nullptr;
    auto* bank = retired_[static_cast<std::size_t> (r)];
    retiredRead_.store ((r + 1) % static_cast<int> (retired_.size()), std::memory_order_release);
    return bank;
}

void PadPlayer::retire (PadBank* bank) noexcept
{
    if (bank == nullptr)
        return;
    for (auto& key : bank->keys)
        key.wanted.store (false, std::memory_order_release);
    const int w = retiredWrite_.load (std::memory_order_relaxed);
    const int next = (w + 1) % static_cast<int> (retired_.size());
    if (next != retiredRead_.load (std::memory_order_acquire))
    {
        retired_[static_cast<std::size_t> (w)] = bank;
        retiredWrite_.store (next, std::memory_order_release);
    }
}

void PadPlayer::drain() noexcept
{
    for (;;)
    {
        const int r = read_.load (std::memory_order_relaxed);
        if (r == write_.load (std::memory_order_acquire))
            return;
        const auto command = commands_[static_cast<std::size_t> (r)];
        read_.store ((r + 1) % kFifoSize, std::memory_order_release);
        handle (command);
    }
}

void PadPlayer::fadeTo (Voice& voice, float target) noexcept
{
    voice.target = target;
    voice.step = (target - voice.gain) / static_cast<float> (std::max<std::int64_t> (1, fadeFrames_));
}

void PadPlayer::start (int bankIndex, int key) noexcept
{
    auto* bank = banks_[static_cast<std::size_t> (bankIndex)];
    if (bank == nullptr || key < 0 || key >= kPadKeys)
        return;
    auto& pad = bank->keys[static_cast<std::size_t> (key)];
    auto& voice = voices_[static_cast<std::size_t> (bankIndex)][static_cast<std::size_t> (key)];
    if (voice.gain <= 0.0f || ! pad.wanted.load (std::memory_order_relaxed))
    {
        // Silent: start again from the top of the file.
        voice.gain = 0.0f;
        voice.readGeneration = pad.generation.load (std::memory_order_relaxed) + 1;
        pad.generation.store (voice.readGeneration, std::memory_order_release);
        pad.wanted.store (true, std::memory_order_release);
    }
    fadeTo (voice, 1.0f);
}

void PadPlayer::handle (const PadCommand& command) noexcept
{
    switch (command.type)
    {
        case PadCommand::Type::setBank:
            retire (banks_[1]);
            banks_[1] = banks_[0];
            voices_[1] = voices_[0];
            for (auto& voice : voices_[1])
                fadeTo (voice, 0.0f);
            banks_[0] = command.bank;
            voices_[0] = {};
            if (playing_ >= 0)
                start (0, playing_); // SL-41: the same key in the new set
            return;

        case PadCommand::Type::play:
            playing_ = command.key >= 0 && command.key < kPadKeys ? command.key : -1;
            fromPadBars_ = false;
            for (int k = 0; k < kPadKeys; ++k)
                if (k != playing_)
                    fadeTo (voices_[0][static_cast<std::size_t> (k)], 0.0f);
            for (auto& voice : voices_[1])
                fadeTo (voice, 0.0f);
            if (playing_ >= 0)
                start (0, playing_);
            return;

        case PadCommand::Type::setGain:
            gain_ = command.value;
            return;

        case PadCommand::Type::setFade:
            fadeFrames_ = std::max<std::int64_t> (1, command.frames);
            return;

        case PadCommand::Type::setFollow:
            follow_ = command.key != 0;
            return;

        case PadCommand::Type::setOutput:
            channel_ = command.channel;
            width_ = command.width == 1 ? 1 : 2;
            return;
    }
}

void PadPlayer::playNow (int key) noexcept
{
    PadCommand command;
    command.key = key;
    handle (command);
}

void PadPlayer::onSegment (const PlanSegment& segment) noexcept
{
    drain();
    if (segment.pad >= 0)
    {
        // Pad bars (ARR-39): the pad sounds through them.
        if (playing_ != segment.pad)
        {
            PadCommand play;
            play.key = segment.pad;
            handle (play);
        }
        fromPadBars_ = true;
        return;
    }

    if (fromPadBars_)
    {
        // The pad bars are over: their pad fades out, unless the pad follows the song.
        PadCommand play;
        play.key = follow_ && segment.songKey >= 0 ? segment.songKey : -1;
        handle (play);
        return;
    }

    if (follow_ && playing_ >= 0 && segment.songKey >= 0 && segment.songKey != playing_)
    {
        // SL-44: a sounding pad follows the key heard, key lifts included.
        PadCommand play;
        play.key = segment.songKey;
        handle (play);
    }
}

int PadPlayer::pull (PadKey& key, Voice& voice, float* left, float* right, int frames, bool offline) noexcept
{
    auto& track = *key.track;
    const int channels = track.channels;
    int i = 0;
    int waited = 0;
    while (i < frames)
    {
        auto* block = track.queue.front();
        if (block == nullptr)
        {
            if (offline && waited < 5000)
            {
                std::this_thread::sleep_for (std::chrono::milliseconds (1));
                ++waited;
                continue;
            }
            break;
        }
        if (block->generation != voice.readGeneration)
        {
            track.queue.pop();
            continue;
        }

        const int take = std::min (frames - i, block->frames - voice.offset);
        const float* src = block->data + voice.offset * channels;
        for (int k = 0; k < take; ++k)
        {
            left[i + k] = src[k * channels];
            right[i + k] = channels > 1 ? src[k * channels + 1] : src[k * channels];
        }
        i += take;
        voice.offset += take;
        if (voice.offset >= block->frames)
        {
            track.queue.pop();
            voice.offset = 0;
        }
    }

    std::fill (left + i, left + frames, 0.0f);
    std::fill (right + i, right + frames, 0.0f);
    return i;
}

void PadPlayer::process (float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept
{
    drain();

    float level = 0.0f;
    for (int done = 0; done < numSamples;)
    {
        const int frames = std::min (kChunk, numSamples - done);
        for (std::size_t b = 0; b < banks_.size(); ++b)
        {
            auto* bank = banks_[b];
            if (bank == nullptr)
                continue;
            for (int k = 0; k < kPadKeys; ++k)
            {
                auto& voice = voices_[b][static_cast<std::size_t> (k)];
                auto& key = bank->keys[static_cast<std::size_t> (k)];
                if (voice.gain <= 0.0f && voice.target <= 0.0f)
                    continue;
                if (key.track->missing)
                    continue;

                pull (key, voice, left_.data(), right_.data(), frames, offline);
                float* outA = channel_ >= 0 && channel_ < numOutputs ? outputs[channel_] : nullptr;
                float* outB = width_ == 2 && channel_ + 1 < numOutputs ? outputs[channel_ + 1] : nullptr;
                for (int i = 0; i < frames; ++i)
                {
                    if (voice.gain != voice.target)
                    {
                        voice.gain += voice.step;
                        if ((voice.step > 0.0f && voice.gain >= voice.target) || (voice.step < 0.0f && voice.gain <= voice.target) || voice.step == 0.0f)
                            voice.gain = voice.target;
                    }
                    const float g = voice.gain * gain_;
                    const float l = left_[static_cast<std::size_t> (i)] * g;
                    const float r = right_[static_cast<std::size_t> (i)] * g;
                    if (width_ == 2)
                    {
                        if (outA != nullptr) outA[done + i] += l;
                        if (outB != nullptr) outB[done + i] += r;
                    }
                    else if (outA != nullptr)
                    {
                        outA[done + i] += (l + r) * kMinusThreeDb; // a stereo pad on one channel (PB-85)
                    }
                }

                if (voice.gain <= 0.0f && voice.target <= 0.0f)
                    key.wanted.store (false, std::memory_order_release); // faded out: the disk stops reading it
                if (b == 0)
                    level = std::max (level, voice.gain);
            }
        }

        // The previous set is retired once it has faded out (SL-41).
        if (banks_[1] != nullptr && std::all_of (voices_[1].begin(), voices_[1].end(), [] (const Voice& v) { return v.gain <= 0.0f && v.target <= 0.0f; }))
        {
            retire (banks_[1]);
            banks_[1] = nullptr;
            voices_[1] = {};
        }
        done += frames;
    }

    key_.store (playing_, std::memory_order_relaxed);
    level_.store (level, std::memory_order_relaxed);
}
} // namespace rec::audio
