// SPDX-License-Identifier: AGPL-3.0-or-later
#include "tools.h"

#include "audio/stretcher.h"
#include "message_handler.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <cmath>
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
int render (const char* in, const char* out, int semitones, double tempo)
{
    auto fail = [] (const juce::String& problem)
    {
        auto* result = new juce::DynamicObject();
        juce::var answer (result);
        result->setProperty ("ok", false);
        result->setProperty ("problem", problem);
        print (answer);
        return 2;
    };

    juce::String problem;
    auto reader = open (in, problem);
    if (reader == nullptr)
        return fail (problem);
    if (! (tempo >= 0.5 && tempo <= 1.5) || std::abs (semitones) > 24)
        return fail ("Key or tempo out of range");

    const int channels = juce::jlimit (1, 2, static_cast<int> (reader->numChannels)); // the engine plays two at most
    const double rate = reader->sampleRate;
    const auto inFrames = reader->lengthInSamples;
    const auto outFrames = static_cast<juce::int64> (std::ceil (static_cast<double> (inFrames) / tempo));

    const juce::File target (juce::String::fromUTF8 (out));
    target.getParentDirectory().createDirectory();
    const auto temp = target.getSiblingFile (target.getFileName() + ".part");
    temp.deleteFile();

    std::unique_ptr<juce::AudioFormatWriter> writer;
    {
        std::unique_ptr<juce::OutputStream> stream = std::make_unique<juce::FileOutputStream> (temp);
        const auto options = juce::AudioFormatWriterOptions{}.withSampleRate (rate).withNumChannels (channels).withBitsPerSample (24);
        if (target.hasFileExtension ("wav"))
            writer = juce::WavAudioFormat().createWriterFor (stream, options);
        else
            writer = juce::FlacAudioFormat().createWriterFor (stream, options);
    }
    if (writer == nullptr)
        return fail ("Could not write " + target.getFullPathName());

    // The same stretcher and settings as live playback, primed so output frame 0 is source frame 0
    // (the engine's streams start the same way, so a render and the live path line up).
    audio::Stretcher stretcher (channels, rate);
    const int primeLength = stretcher.startLength (tempo);
    constexpr int kOut = 4096;
    const int maxIn = static_cast<int> (std::ceil (kOut * 1.5)) + 2;
    juce::AudioBuffer<float> input (channels, std::max (primeLength, maxIn));
    juce::AudioBuffer<float> output (channels, kOut);

    juce::int64 readPos = 0;
    auto readInput = [&] (int frames)
    {
        input.clear();
        reader->read (input.getArrayOfWritePointers(), channels, readPos, frames); // zeros past the end
        readPos += frames;
    };

    readInput (primeLength);
    stretcher.start (input.getArrayOfReadPointers(), primeLength, semitones);

    double owed = 0.0;
    int lastPercent = -1;
    for (juce::int64 done = 0; done < outFrames;)
    {
        owed += kOut * tempo;
        const int n = static_cast<int> (std::floor (owed));
        owed -= n;
        readInput (n);
        stretcher.process (input.getArrayOfReadPointers(), n, output.getArrayOfWritePointers(), kOut);

        const int keep = static_cast<int> (std::min<juce::int64> (kOut, outFrames - done));
        if (! writer->writeFromAudioSampleBuffer (output, 0, keep))
        {
            writer.reset();
            temp.deleteFile();
            return fail ("Could not write " + target.getFullPathName());
        }
        done += keep;

        if (const int percent = static_cast<int> (100 * done / std::max<juce::int64> (1, outFrames)); percent != lastPercent)
        {
            lastPercent = percent;
            std::fprintf (stderr, "progress %.2f\n", percent / 100.0); // stderr, as the transcribe tool does
            std::fflush (stderr);
        }
    }

    writer.reset(); // flushes and closes the file
    target.deleteFile();
    if (! temp.moveFileTo (target))
    {
        temp.deleteFile();
        return fail ("Could not write " + target.getFullPathName());
    }

    auto* result = new juce::DynamicObject();
    juce::var answer (result);
    result->setProperty ("ok", true);
    result->setProperty ("frames", outFrames);
    result->setProperty ("sampleRate", rate);
    result->setProperty ("channels", channels);
    print (answer);
    return 0;
}
} // namespace rec::tools
