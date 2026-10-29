// SPDX-License-Identifier: AGPL-3.0-or-later
#include "audio/disk_streamer.h"

#include "audio/click.h"
#include "audio/pads.h"

#include <CDSPResampler.h>

#include <algorithm>
#include <cmath>
#include <cstdlib>

namespace rec::audio
{
DiskStreamer::DiskStreamer() : juce::Thread ("RECompose disk")
{
    startThread (juce::Thread::Priority::high);
}

DiskStreamer::~DiskStreamer()
{
    stopThread (2000);
}

void DiskStreamer::addSong (Song* song)
{
    {
        const std::lock_guard lock (mutex_);
        songs_.push_back (song);
    }
    notify();
}

void DiskStreamer::removeSong (Song* song)
{
    const std::lock_guard lock (mutex_); // waits for a fill pass in progress
    songs_.erase (std::remove (songs_.begin(), songs_.end(), song), songs_.end());
}

void DiskStreamer::addPads (PadBank* bank)
{
    {
        const std::lock_guard lock (mutex_);
        pads_.push_back (bank);
    }
    notify();
}

void DiskStreamer::removePads (PadBank* bank)
{
    const std::lock_guard lock (mutex_);
    pads_.erase (std::remove (pads_.begin(), pads_.end(), bank), pads_.end());
}

void DiskStreamer::run()
{
    while (! threadShouldExit())
    {
        int written = 0;
        {
            const std::lock_guard lock (mutex_);
            for (auto* song : songs_)
                for (auto& track : song->tracks)
                    written += fillTrack (*song, *track, 8); // round-robin, 8 blocks at a time
            for (auto* bank : pads_)
                written += fillPads (*bank, 8);
        }

        if (written == 0)
            wait (5);
    }
}

// Reads `frames` engine-rate frames from a stream into `out`, resampling when the file's
// rate differs (PB-2). Past the end of the file the reader returns silence.
void readStream (Track& track, SourceStream& stream, float* const* out, int frames, double engineRate)
{
    const int channels = track.channels;
    auto* reader = stream.render >= 0 ? track.renders[static_cast<std::size_t> (stream.render)].reader.get() : track.reader.get();

    if (! track.resampling)
    {
        float* dest[kMaxTrackChannels] { out[0], channels > 1 ? out[1] : nullptr };
        reader->read (dest, channels, stream.pos, frames);
        stream.pos += frames;
        stream.nextEngine += frames;
        return;
    }

    (void) engineRate;
    while (static_cast<int> (stream.pending[0].size()) < frames)
    {
        float* dest[kMaxTrackChannels] { track.readBuffer[0].data(), channels > 1 ? track.readBuffer[1].data() : nullptr };
        reader->read (dest, channels, stream.pos, kBlockFrames);
        stream.pos += kBlockFrames;
        for (int c = 0; c < channels; ++c)
        {
            std::copy (dest[c], dest[c] + kBlockFrames, track.resampleInput.begin());
            double* resampled = nullptr;
            const int produced = stream.resamplers[c]->process (track.resampleInput.data(), kBlockFrames, resampled);
            stream.pending[c].insert (stream.pending[c].end(), resampled, resampled + produced);
        }
    }

    for (int c = 0; c < channels; ++c)
    {
        for (int i = 0; i < frames; ++i)
            out[c][i] = static_cast<float> (stream.pending[c][static_cast<std::size_t> (i)]);
        stream.pending[c].erase (stream.pending[c].begin(), stream.pending[c].begin() + frames);
    }
    stream.nextEngine += frames;
}

void startStream (Track& track, SourceStream& stream, std::int64_t engineFrame, double ratio)
{
    stream.clear();
    stream.nextEngine = engineFrame;
    stream.pos = track.resampling ? static_cast<std::int64_t> (std::floor (static_cast<double> (engineFrame) * ratio)) : engineFrame;
    stream.valid = true;
}

namespace
{

    // Pointers to the track's stretch input, grown on the disk thread when a block needs more.
    void stretchInput (Track& track, int frames, float* (&in)[kMaxTrackChannels])
    {
        for (int c = 0; c < track.channels; ++c)
        {
            if (static_cast<int> (track.stretchInput[c].size()) < frames)
                track.stretchInput[c].resize (static_cast<std::size_t> (frames));
            in[c] = track.stretchInput[c].data();
        }
        if (track.channels < kMaxTrackChannels)
            in[1] = nullptr;
    }

    // How a track plays a segment: straight from the stem, from a render made for this key and tempo
    // (protocol 11, PB-5), or through the live stretcher when there is none (REL-14).
    struct Source
    {
        int render = -1;
        bool stretched = false;
        int semitones = 0;
    };

    Source chooseSource (const Track& track, int key, double tempo, bool transposes)
    {
        Source source;
        source.semitones = transposes ? key : 0;
        if (std::abs (tempo - 1.0) <= 1.0e-9 && source.semitones == 0)
            return source;
        source.render = track.findRender (source.semitones, tempo);
        source.stretched = source.render < 0;
        return source;
    }

    // Starts a stream at engine-rate source frame `want`. A stretched one is primed so its first
    // output frame is `want` exactly (PB-58: shifted and unshifted tracks stay on one grid); a render
    // starts at the file frame that holds it (want / tempo).
    void openStream (Track& track, SourceStream& stream, double want, double ratio, double rate,
                     Source source, double tempo)
    {
        stream.render = source.render;
        startStream (track, stream, static_cast<std::int64_t> (std::floor (source.render >= 0 ? want / tempo : want)), ratio);
        stream.render = source.render;
        stream.nextSource = want;
        stream.stretched = source.stretched;
        stream.semitones = source.semitones;
        stream.inputOwed = 0.0;
        if (! source.stretched)
            return;
        const int semitones = source.semitones;

        if (stream.stretcher == nullptr)
            stream.stretcher = std::make_unique<Stretcher> (track.channels, rate);
        const int length = stream.stretcher->startLength (tempo);
        float* in[kMaxTrackChannels] {};
        stretchInput (track, length, in);
        readStream (track, stream, in, length, rate);
        stream.stretcher->start (in, length, semitones);
    }

    // `frames` output frames from a stream: straight from the file, or through its stretcher, which
    // takes `tempo` source frames per output frame (PB-60).
    void produce (Track& track, SourceStream& stream, float* const* out, int frames, double rate, double tempo)
    {
        if (! stream.stretched)
        {
            readStream (track, stream, out, frames, rate);
            stream.nextSource += frames * tempo; // a render holds `tempo` source frames per frame; the stem itself plays at 1
            return;
        }

        stream.inputOwed += frames * tempo;
        const int inputFrames = static_cast<int> (std::floor (stream.inputOwed));
        stream.inputOwed -= inputFrames;
        float* in[kMaxTrackChannels] {};
        stretchInput (track, inputFrames, in);
        readStream (track, stream, in, inputFrames, rate);
        stream.stretcher->process (in, inputFrames, out, frames);
        stream.nextSource += frames * tempo;
    }

    // A swap (protocol 8): the blocks written from the swap frame on were read for the old plan. Start again
    // there, with the streams where the old plan had them at that frame; if the new plan continues from the
    // same source it plays on with no seam, and if it jumps the usual splice blends the two (PB-55).
    void swapTrack (Track& track, const Plan& old, const Plan& next, double ratio, double rate, bool transposes)
    {
        const auto frame = next.swapFrame;
        if (track.diskNextFrame <= frame)
            return; // not written that far yet: everything queued so far is before the swap

        track.diskNextFrame = frame;
        track.diskSegment = 0;
        track.tailRemaining = 0;
        track.fadeInRemaining = 0;
        track.diskLastEnd = frame;
        track.streams[track.mainStream ^ 1].valid = false;

        const auto it = std::find_if (old.segments.begin(), old.segments.end(),
                                      [frame] (const PlanSegment& s) { return frame >= s.start && frame < s.end; });
        auto& stream = track.streams[track.mainStream];
        if (it == old.segments.end() || it->src < 0)
        {
            stream.valid = false;
            return;
        }

        const double tempo = old.tempo > 0.0 ? old.tempo : 1.0;
        const double want = static_cast<double> (it->src) + static_cast<double> (frame - it->start) * tempo;
        openStream (track, stream, want, ratio, rate, chooseSource (track, it->key, tempo, transposes), tempo);
    }
} // namespace

// The generated click track: silence with a click on every beat that starts in a click segment.
int DiskStreamer::fillClick (Song& song, Track& track, int maxBlocks)
{
    const auto request = song.currentRequest();
    const auto* plan = song.plan.load (std::memory_order_acquire);
    if (request.generation != track.diskGeneration)
    {
        track.diskGeneration = request.generation;
        track.diskNextFrame = request.frame;
        track.diskVersion = plan->version;
        track.diskPlan = plan;
    }
    else if (plan->version != track.diskVersion)
    {
        // A swap (protocol 8): what was written from the swap frame on belongs to the old plan.
        if (plan->swapFrame >= 0 && track.diskNextFrame > plan->swapFrame)
            track.diskNextFrame = plan->swapFrame;
        track.diskVersion = plan->version;
        track.diskPlan = plan;
    }

    int written = 0;
    while (written < maxBlocks)
    {
        if (song.currentRequest().generation != track.diskGeneration)
            break;
        auto* block = track.queue.writable();
        if (block == nullptr)
            break;

        const auto position = track.diskNextFrame;
        auto& buffer = track.mainBuffer[0];
        std::fill (buffer.begin(), buffer.end(), 0.0f);
        renderClick (*plan, position, kBlockFrames, song.sampleRate, buffer.data());
        std::copy_n (buffer.begin(), kBlockFrames, block->data);
        block->generation = track.diskGeneration;
        block->version = plan->version;
        block->startFrame = position;
        block->frames = kBlockFrames;

        if (song.currentRequest().generation != track.diskGeneration)
            break;
        track.queue.publish();
        track.diskNextFrame = position + kBlockFrames;
        track.bufferedGeneration.store (track.diskGeneration);
        ++written;
    }

    return written;
}

int DiskStreamer::fillTrack (Song& song, Track& track, int maxBlocks)
{
    if (track.generatedClick)
        return fillClick (song, track, maxBlocks);
    if (track.missing || track.reader == nullptr)
        return 0;

    const auto request = song.currentRequest();
    const auto* plan = song.plan.load (std::memory_order_acquire); // after the request: see header
    const int channels = track.channels;
    const double ratio = track.sourceRate / song.sampleRate;
    const int spliceOut = static_cast<int> (std::lround (kSpliceOutMs * song.sampleRate / 1000.0));
    const int spliceIn = static_cast<int> (std::lround (kSpliceInMs * song.sampleRate / 1000.0));
    const int keyChange = static_cast<int> (std::lround (kKeyChangeMs * song.sampleRate / 1000.0));
    const double tempo = plan->tempo > 0.0 ? plan->tempo : 1.0;
    const bool transposes = track.transposes.load (std::memory_order_relaxed);

    if (request.generation != track.diskGeneration)
    {
        track.diskGeneration = request.generation;
        track.diskNextFrame = request.frame;
        track.diskLastEnd = -1;
        track.diskSegment = 0;
        track.tailRemaining = 0;
        track.fadeInRemaining = 0;
        for (auto& stream : track.streams)
            stream.clear();
        track.diskVersion = plan->version;
        track.diskPlan = plan;
    }
    else if (plan->version != track.diskVersion)
    {
        if (plan->swapFrame >= 0 && track.diskPlan != nullptr)
            swapTrack (track, *track.diskPlan, *plan, ratio, song.sampleRate, transposes);
        track.diskVersion = plan->version;
        track.diskPlan = plan;
    }

    int written = 0;
    while (written < maxBlocks)
    {
        if (song.currentRequest().generation != track.diskGeneration)
            break; // a seek arrived; start over at the new position next time

        auto* block = track.queue.writable();
        if (block == nullptr)
            break; // full

        const auto position = track.diskNextFrame;
        const auto& segments = plan->segments;
        while (track.diskSegment < segments.size() && position >= segments[track.diskSegment].end)
            ++track.diskSegment;

        int frames = kBlockFrames;
        const PlanSegment* segment = track.diskSegment < segments.size() ? &segments[track.diskSegment] : nullptr;
        if (segment != nullptr && position < segment->start)
            segment = nullptr; // a gap in the plan plays silence; the segments are contiguous in practice
        if (segment != nullptr)
            frames = static_cast<int> (std::min<std::int64_t> (kBlockFrames, segment->end - position));

        for (int c = 0; c < channels; ++c)
            std::fill_n (track.mainBuffer[c].begin(), frames, 0.0f);

        float* main[kMaxTrackChannels] { track.mainBuffer[0].data(), channels > 1 ? track.mainBuffer[1].data() : nullptr };
        auto& current = track.streams[track.mainStream];

        if (segment != nullptr && segment->src >= 0)
        {
            const double want = static_cast<double> (segment->src) + static_cast<double> (position - segment->start) * tempo;
            const auto source = chooseSource (track, segment->key, tempo, transposes);
            const bool stretched = source.stretched;
            // Segment starts are rounded once in output frames, so under a tempo a contiguous source may
            // land a frame further off than it does at 100 %.
            const bool contiguous = current.valid && std::abs (current.nextSource - want) <= (tempo == 1.0 ? 1.0 : 2.0);

            if (contiguous && current.stretched == stretched && current.semitones == source.semitones && current.render == source.render)
            {
                if (! stretched)
                    current.nextSource = want; // contiguous: no seam, no resampler reset
            }
            else if (contiguous)
            {
                // Same audio, another key (a lift, the "#" switch) or tempo: the two crossfade over 50 ms
                // on the same output frames (PB-58).
                track.mainStream ^= 1;
                track.tailRemaining = track.tailLength = keyChange;
                track.fadeInRemaining = track.fadeInLength = keyChange;
                openStream (track, track.streams[track.mainStream], want, ratio, song.sampleRate, source, tempo);
            }
            else
            {
                // The source jumps. The outgoing audio keeps playing for 10 ms with a fade-out and
                // the incoming one fades in over 2 ms, so the seam is smooth and the incoming
                // material starts on the boundary (PB-55).
                const bool atBoundary = track.diskLastEnd == position;
                if (atBoundary && current.valid)
                {
                    track.mainStream ^= 1;
                    track.tailRemaining = track.tailLength = spliceOut;
                }
                if (atBoundary)
                    track.fadeInRemaining = track.fadeInLength = spliceIn;
                openStream (track, track.streams[track.mainStream], want, ratio, song.sampleRate, source, tempo);
            }

            auto& stream = track.streams[track.mainStream];
            produce (track, stream, main, frames, song.sampleRate, tempo);

            if (track.fadeInRemaining > 0)
            {
                const int k = std::min (frames, track.fadeInRemaining);
                for (int i = 0; i < k; ++i)
                {
                    const float gain = 1.0f - static_cast<float> (track.fadeInRemaining - i) / static_cast<float> (track.fadeInLength);
                    for (int c = 0; c < channels; ++c)
                        main[c][i] *= gain;
                }
                track.fadeInRemaining -= k;
            }
        }
        else if (segment != nullptr)
        {
            // Silence (click and pad bars in M4): the outgoing stream rings out, then nothing.
            if (current.valid && track.tailRemaining == 0 && track.diskLastEnd == position)
            {
                track.mainStream ^= 1;
                track.tailRemaining = track.tailLength = spliceOut;
            }
            track.streams[track.mainStream].valid = false;
        }
        else
        {
            track.streams[track.mainStream].valid = false;
        }

        if (track.tailRemaining > 0)
        {
            auto& tail = track.streams[track.mainStream ^ 1];
            if (! tail.valid)
            {
                track.tailRemaining = 0;
            }
            else
            {
                const int k = std::min (frames, track.tailRemaining);
                float* out[kMaxTrackChannels] { track.tailBuffer[0].data(), channels > 1 ? track.tailBuffer[1].data() : nullptr };
                produce (track, tail, out, k, song.sampleRate, tempo);
                for (int i = 0; i < k; ++i)
                {
                    const float gain = static_cast<float> (track.tailRemaining - i) / static_cast<float> (track.tailLength);
                    for (int c = 0; c < channels; ++c)
                        main[c][i] += out[c][i] * gain;
                }
                track.tailRemaining -= k;
                if (track.tailRemaining == 0)
                    tail.valid = false;
            }
        }

        for (int i = 0; i < frames; ++i)
            for (int c = 0; c < channels; ++c)
                block->data[i * channels + c] = main[c][i];

        block->generation = track.diskGeneration;
        block->version = plan->version;
        block->startFrame = position;
        block->frames = frames;

        if (song.currentRequest().generation != track.diskGeneration)
            break; // stale before it was published; drop it

        track.queue.publish();
        track.diskLastEnd = position + frames;
        track.diskNextFrame = position + frames;
        track.bufferedGeneration.store (track.diskGeneration);
        ++written;
    }

    return written;
}
} // namespace rec::audio
