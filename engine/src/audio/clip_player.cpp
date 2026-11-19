// SPDX-License-Identifier: AGPL-3.0-or-later
#include "clip_player.h"

#include "pads.h"

#include <algorithm>
#include <cmath>

namespace rec::audio
{
namespace
{
    constexpr double kHalfPi = 1.5707963267948966;

    // `length` frames from `start`, mixed to mono (message thread).
    std::vector<float> readMono (juce::AudioFormatReader& reader, std::int64_t start, std::int64_t length)
    {
        const int frames = static_cast<int> (std::max<std::int64_t> (0, std::min<std::int64_t> (length, reader.lengthInSamples - start)));
        const int channels = static_cast<int> (reader.numChannels);
        std::vector<float> mono (static_cast<std::size_t> (frames), 0.0f);
        if (frames == 0)
            return mono;
        juce::AudioBuffer<float> buffer (channels, frames);
        reader.read (&buffer, 0, frames, start, true, true);
        for (int ch = 0; ch < channels; ++ch)
            for (int i = 0; i < frames; ++i)
                mono[static_cast<std::size_t> (i)] += buffer.getSample (ch, i) / static_cast<float> (channels);
        return mono;
    }

    std::unique_ptr<Clip> toClip (std::vector<float> mono, double sourceRate, double rate)
    {
        auto clip = std::make_unique<Clip>();
        const double ratio = sourceRate / rate;
        const int frames = static_cast<int> (mono.size());
        clip->samples.resize (static_cast<std::size_t> (frames / ratio));
        juce::LagrangeInterpolator interpolator;
        interpolator.process (ratio, mono.data(), clip->samples.data(), static_cast<int> (clip->samples.size()), frames, 0);
        return clip;
    }
} // namespace

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

    return toClip (readMono (*reader, start, length), reader->sampleRate, rate);
}

std::unique_ptr<Clip> loadSeamClip (const juce::File& file, double rate, juce::AudioFormatManager& formats, juce::String& problem,
                                    const PadLoopSpec& loop, double contextSec)
{
    std::unique_ptr<juce::AudioFormatReader> reader (formats.createReaderFor (file));
    if (reader == nullptr)
    {
        problem = file.existsAsFile() ? "The pad could not be read" : "File missing";
        return nullptr;
    }

    const auto points = padLoopPoints (*reader, loop);
    const auto context = static_cast<std::int64_t> (contextSec * reader->sampleRate);
    const auto fadeStart = points.end - points.crossfade;
    const auto beforeFrom = std::max<std::int64_t> (0, fadeStart - context);
    if (points.end <= points.start || static_cast<double> (fadeStart - beforeFrom + points.crossfade + context) > reader->sampleRate * 30)
    {
        problem = "The loop is too long to preview";
        return nullptr;
    }

    // As the pad plays it (SL-46): the end fades out while the loop start fades in, at equal power.
    auto before = readMono (*reader, beforeFrom, fadeStart - beforeFrom);
    const auto outgoing = readMono (*reader, fadeStart, points.crossfade);
    const auto incoming = readMono (*reader, points.start, points.crossfade);
    const auto after = readMono (*reader, points.start + points.crossfade, context);
    for (std::size_t i = 0; i < outgoing.size(); ++i)
    {
        const double t = static_cast<double> (i) / static_cast<double> (outgoing.size());
        before.push_back (static_cast<float> (outgoing[i] * std::cos (t * kHalfPi) + incoming[i] * std::sin (t * kHalfPi)));
    }
    before.insert (before.end(), after.begin(), after.end());
    return toClip (std::move (before), reader->sampleRate, rate);
}
} // namespace rec::audio
