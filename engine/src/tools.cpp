// SPDX-License-Identifier: AGPL-3.0-or-later
#include "tools.h"

#include "message_handler.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <cstdio>
#include <limits>
#include <memory>

namespace rec::tools
{
namespace
{
    std::unique_ptr<juce::AudioFormatReader> open (const char* path, juce::String& problem)
    {
        static juce::AudioFormatManager formats;
        if (formats.getNumKnownFormats() == 0)
            formats.registerBasicFormats();

        const juce::File file (juce::String::fromUTF8 (path));
        if (! file.existsAsFile())
        {
            problem = "File missing";
            return {};
        }

        std::unique_ptr<juce::AudioFormatReader> reader (formats.createReaderFor (file));
        if (reader == nullptr)
            problem = "Can't read this file";
        return reader;
    }

    void print (const juce::var& value) { std::printf ("%s\n", toJson (value).c_str()); }
} // namespace

int probe (const char* file)
{
    juce::String problem;
    auto reader = open (file, problem);

    auto* result = new juce::DynamicObject();
    juce::var answer (result);

    if (reader == nullptr)
    {
        result->setProperty ("ok", false);
        result->setProperty ("problem", problem);
        print (answer);
        return 2;
    }

    result->setProperty ("ok", true);
    result->setProperty ("channels", static_cast<int> (reader->numChannels));
    result->setProperty ("sampleRate", reader->sampleRate);
    result->setProperty ("frames", static_cast<juce::int64> (reader->lengthInSamples));
    result->setProperty ("durationSec", reader->sampleRate > 0 ? static_cast<double> (reader->lengthInSamples) / reader->sampleRate : 0.0);
    result->setProperty ("bitsPerSample", static_cast<int> (reader->bitsPerSample));
    result->setProperty ("format", reader->getFormatName());
    print (answer);
    return 0;
}

int peaks (const char* file, const char* out, int framesPerBucket)
{
    juce::String problem;
    auto reader = open (file, problem);
    if (reader == nullptr)
    {
        std::fprintf (stderr, "%s\n", problem.toRawUTF8());
        return 2;
    }

    framesPerBucket = juce::jmax (16, framesPerBucket);
    const int channels = juce::jmax (1, static_cast<int> (reader->numChannels));
    const auto total = reader->lengthInSamples;
    const auto bucketCount = static_cast<juce::uint32> ((total + framesPerBucket - 1) / framesPerBucket);

    juce::MemoryOutputStream data;
    data.write ("RECP", 4);
    data.writeInt (1);
    data.writeInt (framesPerBucket);
    data.writeInt (static_cast<int> (bucketCount));
    data.writeDouble (reader->sampleRate);

    const int chunkBuckets = 256;
    juce::AudioBuffer<float> buffer (channels, framesPerBucket * chunkBuckets);

    for (juce::int64 start = 0; start < total; start += buffer.getNumSamples())
    {
        const int n = static_cast<int> (juce::jmin<juce::int64> (buffer.getNumSamples(), total - start));
        reader->read (buffer.getArrayOfWritePointers(), channels, start, n);

        for (int b = 0; b * framesPerBucket < n; ++b)
        {
            const int from = b * framesPerBucket;
            const int len = juce::jmin (framesPerBucket, n - from);
            float lo = std::numeric_limits<float>::max(), hi = std::numeric_limits<float>::lowest();
            for (int c = 0; c < channels; ++c)
            {
                const auto range = juce::FloatVectorOperations::findMinAndMax (buffer.getReadPointer (c, from), len);
                lo = juce::jmin (lo, range.getStart());
                hi = juce::jmax (hi, range.getEnd());
            }
            data.writeFloat (lo);
            data.writeFloat (hi);
        }
    }

    const juce::File target (juce::String::fromUTF8 (out));
    target.getParentDirectory().createDirectory();
    const auto temp = target.getSiblingFile (target.getFileName() + ".tmp");
    if (! temp.replaceWithData (data.getData(), data.getDataSize()) || ! temp.moveFileTo (target))
    {
        std::fprintf (stderr, "Could not write %s\n", out);
        return 3;
    }

    return 0;
}
} // namespace rec::tools
