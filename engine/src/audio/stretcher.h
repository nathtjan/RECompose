// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Live key and tempo (PB-5 fallback, PB-58, PB-60): Signalsmith Stretch on one source stream.
// It runs on the disk thread, ahead of playback, never on the audio thread (PB-3). A stream is
// started aligned: the first output frame after start() is the first input frame given to it,
// so a shifted track stays on the same sample grid as an unshifted one.
#pragma once

#include <memory>

namespace rec::audio
{
class Stretcher
{
public:
    Stretcher (int channels, double sampleRate);
    ~Stretcher();

    // Input frames start() needs at `tempo` (source frames per output frame).
    int startLength (double tempo) const;

    // Clears the stretcher, sets the shift and primes it with `length` (= startLength) frames
    // from the start position, so the next process() output begins exactly there.
    void start (const float* const* input, int length, int semitones);

    // `inputFrames` in, `outputFrames` out; their ratio is the tempo.
    void process (const float* const* input, int inputFrames, float* const* output, int outputFrames);

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
} // namespace rec::audio
