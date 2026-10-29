// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Spoken clips (protocol 12, VC-50): a short mono clip, decoded and resampled on the message thread, played once on
// the MD confirm output. One at a time: a new clip replaces the one sounding. The audio thread only mixes.
#pragma once

#include <juce_audio_formats/juce_audio_formats.h>

#include <atomic>
#include <memory>
#include <vector>

namespace rec::audio
{
struct Clip
{
    std::vector<float> samples; // mono, at the engine rate
    int channel = 0, width = 2;
    float gain = 1.0f;
    std::atomic<bool> done { false }; // the audio thread has let go of it
};

// Reads a file into a clip at `rate`; null with `problem` set when it cannot be read.
std::unique_ptr<Clip> loadClip (const juce::File& file, double rate, juce::AudioFormatManager& formats, juce::String& problem);

class ClipPlayer
{
public:
    // Message thread.
    void start (Clip* clip) noexcept
    {
        if (auto* old = next_.exchange (clip))
            old->done.store (true); // replaced before it began
    }

    // Audio thread: mixes into the outputs (real-time safe).
    void process (float* const* outputs, int numOutputs, int numSamples) noexcept
    {
        if (auto* clip = next_.exchange (nullptr))
        {
            if (playing_ != nullptr)
                playing_->done.store (true);
            playing_ = clip;
            position_ = 0;
        }
        if (playing_ == nullptr)
            return;

        const auto& x = playing_->samples;
        const int n = static_cast<int> (std::min<std::size_t> (static_cast<std::size_t> (numSamples), x.size() - position_));
        for (int ch = playing_->channel; ch < std::min (numOutputs, playing_->channel + playing_->width); ++ch)
            if (outputs[ch] != nullptr)
                for (int i = 0; i < n; ++i)
                    outputs[ch][i] += playing_->gain * x[position_ + static_cast<std::size_t> (i)];
        position_ += static_cast<std::size_t> (n);
        if (position_ >= x.size())
        {
            playing_->done.store (true);
            playing_ = nullptr;
        }
    }

private:
    std::atomic<Clip*> next_ { nullptr };
    Clip* playing_ = nullptr;
    std::size_t position_ = 0;
};
} // namespace rec::audio
