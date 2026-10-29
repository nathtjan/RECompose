// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Rendered keys and tempos (REL-10, REL-14, PB-5, AT-41): the render tool's length and pitch, and a track that
// plays its render instead of processing the stem live, at the right file frame.
#include "check.h"

#include "audio/disk_streamer.h"
#include "audio/player.h"
#include "audio/song.h"
#include "tools.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <cmath>
#include <functional>
#include <vector>

using namespace rec::audio;

namespace
{
constexpr double kRate = 48000.0;

juce::File dir()
{
    static const auto d = []
    {
        auto f = juce::File::getSpecialLocation (juce::File::tempDirectory)
                     .getChildFile ("rec-render-tests-" + juce::String (juce::Random::getSystemRandom().nextInt (1000000)));
        f.createDirectory();
        return f;
    }();
    return d;
}

juce::File writeWav (const char* name, int frames, const std::function<float (int)>& sample)
{
    const auto file = dir().getChildFile (name);
    juce::AudioBuffer<float> buffer (1, frames);
    for (int i = 0; i < frames; ++i)
        buffer.setSample (0, i, sample (i));
    std::unique_ptr<juce::OutputStream> stream = std::make_unique<juce::FileOutputStream> (file);
    auto writer = juce::WavAudioFormat().createWriterFor (stream, juce::AudioFormatWriterOptions{}
                                                                       .withSampleRate (kRate)
                                                                       .withNumChannels (1)
                                                                       .withBitsPerSample (32)
                                                                       .withSampleFormat (juce::AudioFormatWriterOptions::SampleFormat::floatingPoint));
    writer->writeFromAudioSampleBuffer (buffer, 0, frames);
    return file;
}

std::vector<float> readAll (const juce::File& file, double& rate)
{
    juce::AudioFormatManager formats;
    formats.registerBasicFormats();
    std::unique_ptr<juce::AudioFormatReader> reader (formats.createReaderFor (file));
    if (reader == nullptr)
        return {};
    rate = reader->sampleRate;
    juce::AudioBuffer<float> buffer (1, static_cast<int> (reader->lengthInSamples));
    reader->read (&buffer, 0, buffer.getNumSamples(), 0, true, false);
    return { buffer.getReadPointer (0), buffer.getReadPointer (0) + buffer.getNumSamples() };
}

// Frequency from upward zero crossings over the middle of the signal.
double frequency (const std::vector<float>& x, double rate)
{
    const auto from = x.size() / 4, to = x.size() * 3 / 4;
    int crossings = 0;
    std::size_t first = 0, last = 0;
    for (auto i = from + 1; i < to; ++i)
    {
        if (x[i - 1] < 0.0f && x[i] >= 0.0f)
        {
            if (crossings == 0)
                first = i;
            last = i;
            ++crossings;
        }
    }
    return crossings > 1 ? (crossings - 1) * rate / static_cast<double> (last - first) : 0.0;
}

std::vector<float> play (std::unique_ptr<Song> song, std::unique_ptr<Plan> plan, int frames)
{
    DiskStreamer disk;
    Player player;
    disk.addSong (song.get());
    player.prepare (kRate, 512, 2);
    Command set;
    set.type = Command::Type::setSong;
    set.song = song.get();
    player.post (set);
    player.processCommandsOnly();
    Command c;
    c.type = Command::Type::setPlan;
    c.plan = song->publishPlan (std::move (plan));
    c.frame = 0;
    c.generation = song->requestFrame (0).generation;
    player.post (c);
    player.processCommandsOnly();
    disk.wake();
    Command start;
    start.type = Command::Type::play;
    player.post (start);

    std::vector<float> left;
    for (int done = 0; done < frames; done += 512)
    {
        std::vector<float> a (512), b (512);
        float* out[2] { a.data(), b.data() };
        player.process (out, 2, 512, true);
        left.insert (left.end(), a.begin(), a.end());
    }

    Command unload;
    unload.type = Command::Type::setSong;
    player.post (unload);
    player.processCommandsOnly();
    disk.removeSong (song.get());
    return left;
}
} // namespace

TEST ("render: a key render keeps the length and moves the pitch (REL-10)")
{
    const auto in = writeWav ("sine.wav", 96000, [] (int i) { return 0.5f * static_cast<float> (std::sin (2.0 * juce::MathConstants<double>::pi * 440.0 * i / kRate)); });
    const auto out = dir().getChildFile ("sine+2.wav");
    CHECK (rec::tools::render (in.getFullPathName().toRawUTF8(), out.getFullPathName().toRawUTF8(), 2, 1.0) == 0);

    double rate = 0;
    const auto x = readAll (out, rate);
    CHECK (x.size() == 96000u);
    CHECK (rate == kRate);
    const auto f = frequency (x, rate);
    CHECK (std::abs (f - 440.0 * std::pow (2.0, 2.0 / 12.0)) < 4.0); // 493.9 Hz
    CHECK (! dir().getChildFile ("sine+2.wav.part").exists());
}

TEST ("render: a tempo render is longer by the tempo and keeps the pitch, in FLAC by default (REL-10)")
{
    const auto in = dir().getChildFile ("sine.wav");
    const auto out = dir().getChildFile ("sine-80.flac");
    CHECK (rec::tools::render (in.getFullPathName().toRawUTF8(), out.getFullPathName().toRawUTF8(), 0, 0.8) == 0);

    double rate = 0;
    const auto x = readAll (out, rate);
    CHECK (x.size() == 120000u); // 96000 / 0.8
    CHECK (std::abs (frequency (x, rate) - 440.0) < 4.0);
}

TEST ("render: a missing input and an out-of-range tempo fail without a file")
{
    const auto out = dir().getChildFile ("nothing.flac");
    CHECK (rec::tools::render (dir().getChildFile ("no-such.wav").getFullPathName().toRawUTF8(), out.getFullPathName().toRawUTF8(), 1, 1.0) != 0);
    CHECK (rec::tools::render (dir().getChildFile ("sine.wav").getFullPathName().toRawUTF8(), out.getFullPathName().toRawUTF8(), 0, 3.0) != 0);
    CHECK (! out.exists());
}

TEST ("render: a track plays its render where key and tempo match, and the stem as written elsewhere (REL-14, AT-41)")
{
    juce::AudioFormatManager formats;
    formats.registerBasicFormats();
    const auto stem = writeWav ("stem.wav", 96000, [] (int) { return 0.2f; });
    const auto rendered = writeWav ("stem+2.wav", 96000, [] (int) { return 0.5f; });

    TrackSpec spec;
    spec.id = "t";
    spec.file = stem.getFullPathName().toStdString();
    spec.outputChannel = 0;
    spec.outputWidth = 1;
    spec.transposes = true;
    spec.renders.push_back ({ rendered.getFullPathName().toStdString(), 2, 1.0 });

    auto song = loadSong ("s", { spec }, kRate, 0, formats);
    CHECK (song->tracks[0]->renders.size() == 1u);

    // Key 0 for one second, then key +2 (the render) for one second.
    auto plan = std::make_unique<Plan>();
    PlanSegment a;
    a.start = 0;
    a.end = 48000;
    a.src = 0;
    PlanSegment b = a;
    b.start = 48000;
    b.end = 96000;
    b.src = 48000;
    b.key = 2;
    plan->segments = { a, b };
    plan->lengthFrames = 96000;

    const auto out = play (std::move (song), std::move (plan), 96000);
    CHECK (std::abs (out[24000] - 0.2f) < 1.0e-6f);
    CHECK (std::abs (out[90000] - 0.5f) < 1.0e-6f); // straight from the render: no live shifting
}

TEST ("render: a tempo render is read at source / tempo (AT-41)")
{
    juce::AudioFormatManager formats;
    formats.registerBasicFormats();
    const auto stem = writeWav ("stem2.wav", 96000, [] (int) { return 0.2f; });
    // A ramp: the value says which file frame played.
    const auto rendered = writeWav ("ramp-80.wav", 120000, [] (int i) { return 0.01f + static_cast<float> (i) * 1.0e-6f; });

    TrackSpec spec;
    spec.id = "t";
    spec.file = stem.getFullPathName().toStdString();
    spec.outputChannel = 0;
    spec.outputWidth = 1;
    spec.renders.push_back ({ rendered.getFullPathName().toStdString(), 0, 0.8 });
    auto song = loadSong ("s", { spec }, kRate, 0, formats);

    // Plays from source second 1 at 80 %: file frame 60000 first.
    auto plan = std::make_unique<Plan>();
    PlanSegment a;
    a.start = 0;
    a.end = 48000;
    a.src = 48000;
    plan->segments = { a };
    plan->lengthFrames = 48000;
    plan->tempo = 0.8;

    const auto out = play (std::move (song), std::move (plan), 24576);
    CHECK (std::abs (out[1000] - (0.01f + 61000 * 1.0e-6f)) < 2.0e-6f); // after the 10 ms play fade (PB-20)
    CHECK (std::abs (out[20000] - (0.01f + 80000 * 1.0e-6f)) < 2.0e-6f);
}
