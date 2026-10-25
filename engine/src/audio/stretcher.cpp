// SPDX-License-Identifier: AGPL-3.0-or-later
#include "audio/stretcher.h"

#if defined(_MSC_VER)
  #pragma warning(push, 0)
#endif
#include <signalsmith-stretch/signalsmith-stretch.h>
#if defined(_MSC_VER)
  #pragma warning(pop)
#endif

#include <algorithm>

namespace rec::audio
{
struct Stretcher::Impl
{
    // A fixed seed keeps renders deterministic, so offline tests compare like with like.
    signalsmith::stretch::SignalsmithStretch<float> stretch { 0x5245431L };
    int channels = 1;
};

Stretcher::Stretcher (int channels, double sampleRate) : impl_ (std::make_unique<Impl>())
{
    impl_->channels = std::max (1, channels);
    impl_->stretch.presetDefault (impl_->channels, static_cast<float> (sampleRate));
}

Stretcher::~Stretcher() = default;

int Stretcher::startLength (double tempo) const
{
    return impl_->stretch.outputSeekLength (static_cast<float> (tempo));
}

void Stretcher::start (const float* const* input, int length, int semitones)
{
    impl_->stretch.setTransposeSemitones (static_cast<float> (semitones));
    impl_->stretch.outputSeek (input, length);
}

void Stretcher::process (const float* const* input, int inputFrames, float* const* output, int outputFrames)
{
    impl_->stretch.process (input, inputFrames, output, outputFrames);
}
} // namespace rec::audio
