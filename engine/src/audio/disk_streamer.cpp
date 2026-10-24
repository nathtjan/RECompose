// SPDX-License-Identifier: AGPL-3.0-or-later
#include "audio/disk_streamer.h"

#include <CDSPResampler.h>

#include <algorithm>
#include <cmath>

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
        }

        if (written == 0)
            wait (5);
    }
}

int DiskStreamer::fillTrack (Song& song, Track& track, int maxBlocks)
{
    if (track.missing || track.reader == nullptr)
        return 0;

    const auto request = song.currentRequest();
    const bool resampling = track.resamplers[0] != nullptr;
    const int channels = track.channels;

    if (request.generation != track.diskGeneration)
    {
        track.diskGeneration = request.generation;
        track.diskNextFrame = request.frame;
        track.diskSourcePos = resampling
                                ? static_cast<std::int64_t> (std::floor (static_cast<double> (request.frame) * track.sourceRate / song.sampleRate))
                                : request.frame;
        for (int c = 0; c < channels; ++c)
        {
            if (resampling)
                track.resamplers[c]->clear();
            track.pending[c].clear();
        }
    }

    int written = 0;
    while (written < maxBlocks)
    {
        if (song.currentRequest().generation != track.diskGeneration)
            break; // a seek arrived; start over at the new position next time

        auto* block = track.queue.writable();
        if (block == nullptr)
            break; // full

        float* dest[kMaxTrackChannels] { track.readBuffer[0].data(), channels > 1 ? track.readBuffer[1].data() : nullptr };

        if (! resampling)
        {
            // Past the end the reader returns silence, which is what the song should do too.
            track.reader->read (dest, channels, track.diskSourcePos, kBlockFrames);
            track.diskSourcePos += kBlockFrames;
            for (int i = 0; i < kBlockFrames; ++i)
                for (int c = 0; c < channels; ++c)
                    block->data[i * channels + c] = dest[c][i];
        }
        else
        {
            while (static_cast<int> (track.pending[0].size()) < kBlockFrames)
            {
                track.reader->read (dest, channels, track.diskSourcePos, kBlockFrames);
                track.diskSourcePos += kBlockFrames;
                for (int c = 0; c < channels; ++c)
                {
                    std::copy (dest[c], dest[c] + kBlockFrames, track.resampleInput.begin());
                    double* out = nullptr;
                    const int produced = track.resamplers[c]->process (track.resampleInput.data(), kBlockFrames, out);
                    track.pending[c].insert (track.pending[c].end(), out, out + produced);
                }
            }

            for (int i = 0; i < kBlockFrames; ++i)
                for (int c = 0; c < channels; ++c)
                    block->data[i * channels + c] = static_cast<float> (track.pending[c][static_cast<std::size_t> (i)]);
            for (int c = 0; c < channels; ++c)
                track.pending[c].erase (track.pending[c].begin(), track.pending[c].begin() + kBlockFrames);
        }

        block->generation = track.diskGeneration;
        block->startFrame = track.diskNextFrame;
        block->frames = kBlockFrames;

        if (song.currentRequest().generation != track.diskGeneration)
            break; // stale before it was published; drop it

        track.queue.publish();
        track.diskNextFrame += kBlockFrames;
        track.bufferedGeneration.store (track.diskGeneration);
        ++written;
    }

    return written;
}
} // namespace rec::audio
