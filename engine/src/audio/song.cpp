// SPDX-License-Identifier: AGPL-3.0-or-later
#include "audio/song.h"

#include <CDSPResampler.h>

#include <cmath>

namespace rec::audio
{
namespace
{
    // Four seconds of read-ahead at the engine rate (PB-3 asks for at least two).
    std::size_t queueCapacity (double engineRate)
    {
        return static_cast<std::size_t> (std::ceil (engineRate * 4.0 / kBlockFrames)) + 2;
    }
} // namespace

float decibelsToGain (float db) noexcept
{
    return db <= -100.0f ? 0.0f : std::pow (10.0f, db / 20.0f);
}

SourceStream::SourceStream() = default;
SourceStream::~SourceStream() = default;

void SourceStream::clear()
{
    for (auto& r : resamplers)
        if (r != nullptr)
            r->clear();
    for (auto& p : pending)
        p.clear();
    valid = false;
}

Track::Track (TrackSpec s, std::size_t queueCapacity) : spec (std::move (s)), queue (queueCapacity) {}
Track::~Track() = default;

StreamRequest Song::requestFrame (std::int64_t frame) noexcept
{
    auto current = request.load();
    StreamRequest next;
    do
    {
        next = StreamRequest::unpack (current);
        next.generation = (next.generation + 1) & 0xffffu;
        next.frame = frame < 0 ? 0 : frame;
    } while (! request.compare_exchange_weak (current, StreamRequest::pack (next)));
    return next;
}

const Plan* Song::publishPlan (std::unique_ptr<Plan> next)
{
    const auto* raw = next.get();
    plans.push_back (std::move (next));
    plan.store (raw, std::memory_order_release);
    return raw;
}

std::unique_ptr<Plan> identityPlan (std::int64_t lengthFrames)
{
    auto plan = std::make_unique<Plan>();
    plan->lengthFrames = lengthFrames;
    PlanSegment all;
    all.start = 0;
    all.end = lengthFrames;
    all.src = 0;
    plan->segments.push_back (all);
    return plan;
}

bool Song::isBuffered() const noexcept
{
    const auto generation = currentRequest().generation;
    for (const auto& track : tracks)
        if (! track->missing && track->bufferedGeneration.load() != generation)
            return false;
    return true;
}

std::unique_ptr<Song> loadSong (std::string id, std::vector<TrackSpec> specs, double engineRate,
                                std::int64_t startFrame, juce::AudioFormatManager& formats)
{
    auto song = std::make_unique<Song>();
    song->id = std::move (id);
    song->sampleRate = engineRate;

    const auto capacity = queueCapacity (engineRate);

    for (auto& spec : specs)
    {
        auto track = std::make_unique<Track> (spec, capacity);
        track->gain.store (decibelsToGain (spec.gainDb));
        track->mute.store (spec.mute);
        track->solo.store (spec.solo);
        track->outputChannel.store (spec.outputChannel);
        track->outputWidth.store (spec.outputWidth == 1 ? 1 : 2);

        const juce::File file (juce::String::fromUTF8 (spec.file.c_str()));
        if (! file.existsAsFile())
        {
            track->missing = true;
            track->problem = "File missing";
        }
        else if (auto* reader = formats.createReaderFor (file))
        {
            track->reader.reset (reader);
            track->channels = juce::jlimit (1, kMaxTrackChannels, static_cast<int> (reader->numChannels));
            track->sourceRate = reader->sampleRate;
            track->sourceFrames = reader->lengthInSamples;

            const auto songFrames = static_cast<std::int64_t> (
                std::ceil (static_cast<double> (reader->lengthInSamples) * engineRate / reader->sampleRate));
            song->lengthFrames = std::max (song->lengthFrames, songFrames);

            track->resampling = std::abs (reader->sampleRate - engineRate) > 0.5;
            for (int c = 0; c < track->channels; ++c)
            {
                track->readBuffer[c].resize (kBlockFrames);
                track->mainBuffer[c].resize (kBlockFrames);
                track->tailBuffer[c].resize (kBlockFrames);
                if (track->resampling)
                    for (auto& stream : track->streams)
                        stream.resamplers[c] = std::make_unique<r8b::CDSPResampler24> (reader->sampleRate, engineRate, kBlockFrames);
            }
            track->resampleInput.resize (kBlockFrames);
        }
        else
        {
            track->missing = true;
            track->problem = "Can't read this file";
        }

        song->tracks.push_back (std::move (track));
    }

    song->publishPlan (identityPlan (song->lengthFrames));
    song->request.store (StreamRequest::pack ({ 1, std::clamp<std::int64_t> (startFrame, 0, song->lengthFrames) }));
    return song;
}
} // namespace rec::audio
