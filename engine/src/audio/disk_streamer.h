// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The disk thread: keeps every track's block queue filled from the current stream
// request (PB-3: at least 2 s of read-ahead), resampling to the engine rate with
// r8brain when the file's rate differs (PB-2). Never touches the audio thread's state.
#pragma once

#include "audio/song.h"

#include <juce_core/juce_core.h>

#include <mutex>
#include <vector>

namespace rec::audio
{
struct PadBank;

// Disk thread: reads `frames` engine-rate frames of a stream (resampled when needed, PB-2), and
// starts a stream at an engine-rate frame. Shared by the songs and the pads.
void readStream (Track& track, SourceStream& stream, float* const* out, int frames, double engineRate);
void startStream (Track& track, SourceStream& stream, std::int64_t engineFrame, double ratio);

class DiskStreamer : private juce::Thread
{
public:
    DiskStreamer();
    ~DiskStreamer() override;

    // Message thread.
    void addSong (Song* song);
    void removeSong (Song* song); // returns once the disk thread no longer uses it
    void addPads (PadBank* bank);
    void removePads (PadBank* bank); // returns once the disk thread no longer uses it

    // Any thread: a request changed, fill now rather than at the next poll.
    void wake() { notify(); }

    // Fills one track until its queue is full or the request changes. Returns the
    // number of blocks written. Public for tests.
    static int fillTrack (Song& song, Track& track, int maxBlocks);

private:
    static int fillClick (Song& song, Track& track, int maxBlocks);

    void run() override;

    std::mutex mutex_;
    std::vector<Song*> songs_;
    std::vector<PadBank*> pads_;
};
} // namespace rec::audio
