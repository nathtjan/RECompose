// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Pads offline (SL-40…46, PB-72): fades and key changes, the loop seam, Follow song and pad bars.
// Each key of the generated set is a sine at its own frequency, so the output says which key sounds.
#include "check.h"

#include "audio/disk_streamer.h"
#include "audio/pads.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <cmath>
#include <vector>

using namespace rec::audio;

namespace
{
constexpr double kRate = 48000.0;
constexpr double kPi = 3.141592653589793;

double keyHz (int key) { return 200.0 + 50.0 * key; }

juce::File padFolder (int seconds)
{
    auto dir = juce::File::getSpecialLocation (juce::File::tempDirectory)
                   .getChildFile ("rec-pads-" + juce::String (juce::Random::getSystemRandom().nextInt (1000000)));
    dir.createDirectory();
    for (int k = 0; k < kPadKeys; ++k)
    {
        const auto file = dir.getChildFile ("Pad " + juce::String (k) + ".wav");
        const int frames = static_cast<int> (kRate) * seconds;
        juce::AudioBuffer<float> buffer (2, frames);
        for (int i = 0; i < frames; ++i)
            for (int c = 0; c < 2; ++c)
                buffer.setSample (c, i, 0.5f * static_cast<float> (std::sin (2.0 * kPi * keyHz (k) * i / kRate)));
        std::unique_ptr<juce::OutputStream> stream = std::make_unique<juce::FileOutputStream> (file);
        juce::WavAudioFormat wav;
        auto writer = wav.createWriterFor (stream, juce::AudioFormatWriterOptions{}
                                                       .withSampleRate (kRate)
                                                       .withNumChannels (2)
                                                       .withBitsPerSample (32)
                                                       .withSampleFormat (juce::AudioFormatWriterOptions::SampleFormat::floatingPoint));
        writer->writeFromAudioSampleBuffer (buffer, 0, frames);
    }
    return dir;
}

struct PadRig
{
    juce::AudioFormatManager formats;
    DiskStreamer disk;
    PadPlayer pads;
    std::unique_ptr<PadBank> bank;

    explicit PadRig (int seconds = 6)
    {
        formats.registerBasicFormats();
        const auto dir = padFolder (seconds);
        std::array<std::string, kPadKeys> files;
        for (int k = 0; k < kPadKeys; ++k)
            files[static_cast<std::size_t> (k)] = dir.getChildFile ("Pad " + juce::String (k) + ".wav").getFullPathName().toStdString();
        bank = loadPadBank ("set", files, kRate, formats);
        disk.addPads (bank.get());
        pads.prepare (kRate);
        PadCommand set;
        set.type = PadCommand::Type::setBank;
        set.bank = bank.get();
        pads.post (set);
        PadCommand gain;
        gain.type = PadCommand::Type::setGain;
        gain.value = 1.0f;
        pads.post (gain);
        fade (0.1);
    }

    ~PadRig() { disk.removePads (bank.get()); }

    void fade (double seconds)
    {
        PadCommand c;
        c.type = PadCommand::Type::setFade;
        c.frames = static_cast<std::int64_t> (seconds * kRate);
        pads.post (c);
    }

    void play (int key)
    {
        PadCommand c;
        c.type = PadCommand::Type::play;
        c.key = key;
        pads.post (c);
        disk.wake();
    }

    std::vector<float> render (int frames)
    {
        std::vector<float> left (static_cast<std::size_t> (frames)), right (static_cast<std::size_t> (frames));
        for (int done = 0; done < frames;)
        {
            const int n = std::min (512, frames - done);
            float* out[2] { left.data() + done, right.data() + done };
            pads.process (out, 2, n, true);
            done += n;
        }
        return left;
    }
};

double frequency (const std::vector<float>& x, std::size_t from, std::size_t count)
{
    int crossings = 0;
    for (auto i = from + 1; i < from + count; ++i)
        if (x[i - 1] < 0.0f && x[i] >= 0.0f)
            ++crossings;
    return crossings * kRate / static_cast<double> (count);
}

float rms (const std::vector<float>& x, std::size_t from, std::size_t count)
{
    double sum = 0.0;
    for (auto i = from; i < from + count; ++i)
        sum += static_cast<double> (x[i]) * x[i];
    return static_cast<float> (std::sqrt (sum / static_cast<double> (count)));
}

bool near (double a, double b, double tol) { return std::abs (a - b) <= tol; }
} // namespace

TEST ("pads: a key fades in, another key takes over, stop fades out (SL-42, SL-45)")
{
    PadRig rig;
    rig.play (3);
    const auto first = rig.render (24000);
    CHECK (rms (first, 0, 960) < rms (first, 9600, 960));               // fading in over 0.1 s
    CHECK (near (frequency (first, 9600, 14400), keyHz (3), 4.0));
    CHECK (near (rms (first, 9600, 9600), 0.354f, 0.02f));             // full level after the fade
    CHECK (rig.pads.key() == 3);

    rig.play (7);
    const auto second = rig.render (24000);
    CHECK (near (frequency (second, 9600, 14400), keyHz (7), 4.0));    // key 3 has faded out
    CHECK (rig.pads.key() == 7);

    rig.play (-1);
    const auto stopped = rig.render (9600);
    CHECK (rms (stopped, 7200, 2400) < 1.0e-4f);
    CHECK (rig.pads.key() == -1);
    CHECK (! rig.bank->keys[7].wanted.load()); // the disk no longer reads it
}

TEST ("pads: a short file loops from 90 % back to 25 % with no gap (SL-46)")
{
    PadRig rig (1); // 1 s files: the loop is 0.25–0.9 s with a 0.33 s crossfade
    CHECK (rig.bank->keys[0].loopStart == 12000);
    CHECK (rig.bank->keys[0].loopEnd == 43200);
    rig.play (0);
    const auto out = rig.render (48000 * 4);

    bool noGap = true;
    for (std::size_t at = 9600; at + 960 < out.size(); at += 480)
        noGap = noGap && rms (out, at, 960) > 0.25f;
    CHECK (noGap);
    CHECK (near (frequency (out, 96000, 48000), keyHz (0), 4.0));
}

TEST ("pads: Follow song takes the key heard; pad bars start and end a pad (SL-44, ARR-39)")
{
    PadRig rig;
    PadCommand follow;
    follow.type = PadCommand::Type::setFollow;
    follow.key = 1;
    rig.pads.post (follow);

    // Not sounding: following changes nothing.
    PlanSegment verse;
    verse.songKey = 2;
    rig.pads.onSegment (verse);
    rig.render (512);
    CHECK (rig.pads.key() == -1);

    // Sounding: a lift to 4 moves the pad there.
    rig.play (2);
    rig.render (9600);
    PlanSegment lifted;
    lifted.songKey = 4;
    rig.pads.onSegment (lifted);
    const auto out = rig.render (24000);
    CHECK (rig.pads.key() == 4);
    CHECK (near (frequency (out, 9600, 14400), keyHz (4), 4.0));

    // Pad bars in another key, then a section: the pad goes back to the song's key.
    PlanSegment padBars;
    padBars.pad = 9;
    rig.pads.onSegment (padBars);
    rig.render (9600);
    CHECK (rig.pads.key() == 9);
    rig.pads.onSegment (lifted);
    rig.render (512);
    CHECK (rig.pads.key() == 4);

    // Without Follow song, pad bars end with silence.
    follow.key = 0;
    rig.pads.post (follow);
    rig.pads.onSegment (padBars);
    rig.render (512);
    CHECK (rig.pads.key() == 9);
    rig.pads.onSegment (verse);
    rig.render (512);
    CHECK (rig.pads.key() == -1);
}
