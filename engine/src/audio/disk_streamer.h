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
class DiskStreamer : private juce::Thread
{
public:
    DiskStreamer();
    ~DiskStreamer() override;

    // Message thread.
    void addSong (Song* song);
    void removeSong (Song* song); // returns once the disk thread no longer uses it

    // Any thread: a request changed, fill now rather than at the next poll.
    void wake() { notify(); }

    // Fills one track until its queue is full or the request changes. Returns the
    // number of blocks written. Public for tests.
    static int fillTrack (Song& song, Track& track, int maxBlocks);

private:
    void run() override;

    std::mutex mutex_;
    std::vector<Song*> songs_;
};
} // namespace rec::audio
