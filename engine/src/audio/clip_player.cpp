// SPDX-License-Identifier: AGPL-3.0-or-later
#include "clip_player.h"

namespace rec::audio
{
std::unique_ptr<Clip> loadClip (const juce::File& file, double rate, juce::AudioFormatManager& formats, juce::String& problem,
                                double fromSec, double lengthSec)
{
    std::unique_ptr<juce::AudioFormatReader> reader (formats.createReaderFor (file));
    if (reader == nullptr)
    {
        problem = file.existsAsFile() ? "The clip could not be read" : "File missing";
        return nullptr;
    }

    const auto start = fromSec >= 0.0 ? std::min (reader->lengthInSamples, static_cast<juce::int64> (fromSec * reader->sampleRate)) : juce::int64 { 0 };
    auto length = reader->lengthInSamples - start;
    if (lengthSec > 0.0)
        length = std::min (length, static_cast<juce::int64> (lengthSec * reader->sampleRate));
    if (length > static_cast<juce::int64> (reader->sampleRate * 30) || length <= 0)
    {
        problem = length <= 0 ? "The clip is empty" : "The clip is longer than 30 s";
        return nullptr;
    }

    const int frames = static_cast<int> (length);
    const int channels = static_cast<int> (reader->numChannels);
    juce::AudioBuffer<float> buffer (channels, frames);
    reader->read (&buffer, 0, frames, start, true, true);
    std::vector<float> mono (static_cast<std::size_t> (frames), 0.0f);
    for (int ch = 0; ch < channels; ++ch)
        for (int i = 0; i < frames; ++i)
            mono[static_cast<std::size_t> (i)] += buffer.getSample (ch, i) / static_cast<float> (channels);

    auto clip = std::make_unique<Clip>();
    const double ratio = reader->sampleRate / rate;
    clip->samples.resize (static_cast<std::size_t> (frames / ratio));
    juce::LagrangeInterpolator interpolator;
    interpolator.process (ratio, mono.data(), clip->samples.data(), static_cast<int> (clip->samples.size()), frames, 0);
    return clip;
}
} // namespace rec::audio
