// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Transitions offline (SL-20, SL-30, AT-30, AT-31): the next song starts on the exact sample.
// Song A is a constant 0.5, song B a constant 0.25, so the sum says which of them sounds.
#include "check.h"

#include "audio/click.h"
#include "audio/disk_streamer.h"
#include "audio/pads.h"
#include "audio/player.h"
#include "audio/song.h"
#include "audio/transition.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <cmath>
#include <vector>

using namespace rec::audio;

namespace
{
constexpr double kRate = 48000.0;

juce::File constantWav (const char* name, float value, int frames)
{
    static const auto dir = []
    {
        auto d = juce::File::getSpecialLocation (juce::File::tempDirectory)
                     .getChildFile ("rec-transition-tests-" + juce::String (juce::Random::getSystemRandom().nextInt (1000000)));
        d.createDirectory();
        return d;
    }();
    const auto file = dir.getChildFile (name);
    juce::AudioBuffer<float> buffer (1, frames);
    for (int i = 0; i < frames; ++i)
        buffer.setSample (0, i, value);
    std::unique_ptr<juce::OutputStream> stream = std::make_unique<juce::FileOutputStream> (file);
    juce::WavAudioFormat wav;
    auto writer = wav.createWriterFor (stream, juce::AudioFormatWriterOptions{}
                                                   .withSampleRate (kRate)
                                                   .withNumChannels (1)
                                                   .withBitsPerSample (32)
                                                   .withSampleFormat (juce::AudioFormatWriterOptions::SampleFormat::floatingPoint));
    writer->writeFromAudioSampleBuffer (buffer, 0, frames);
    return file;
}

struct Pair
{
    juce::AudioFormatManager formats;
    DiskStreamer disk;
    Player a, b;
    PadPlayer pads;
    Transitioner transitions;
    std::unique_ptr<Song> songA, songB;

    Pair (std::int64_t lengthA)
    {
        formats.registerBasicFormats();
        auto make = [this] (const juce::File& file)
        {
            TrackSpec spec;
            spec.id = "t";
            spec.file = file.getFullPathName().toStdString();
            spec.outputWidth = 1;
            auto song = loadSong ("song", { spec }, kRate, 0, formats);
            disk.addSong (song.get());
            return song;
        };
        songA = make (constantWav ("a.wav", 0.5f, 300000));
        songB = make (constantWav ("b.wav", 0.25f, 300000));
        for (auto* player : { &a, &b })
            player->prepare (kRate, 512, 2);
        pads.prepare (kRate);
        transitions.prepare (kRate);

        Command setA, setB;
        setA.type = setB.type = Command::Type::setSong;
        setA.song = songA.get();
        setB.song = songB.get();
        a.post (setA);
        b.post (setB);
        a.processCommandsOnly();
        b.processCommandsOnly();

        for (auto [song, player, length] : { std::tuple { songA.get(), &a, lengthA }, std::tuple { songB.get(), &b, std::int64_t (96000) } })
        {
            auto plan = std::make_unique<Plan>();
            plan->lengthFrames = length;
            PlanSegment segment;
            segment.start = 0;
            segment.end = length;
            segment.src = 0;
            plan->segments.push_back (segment);
            Command c;
            c.type = Command::Type::setPlan;
            c.plan = song->publishPlan (std::move (plan));
            c.frame = 0;
            c.generation = song->requestFrame (0).generation;
            player->post (c);
            player->processCommandsOnly();
        }
        disk.wake();
    }

    ~Pair()
    {
        for (auto* player : { &a, &b })
        {
            Command unload;
            unload.type = Command::Type::setSong;
            player->post (unload);
            player->processCommandsOnly();
        }
        disk.removeSong (songA.get());
        disk.removeSong (songB.get());
    }

    std::vector<float> render (int frames)
    {
        std::vector<float> left (static_cast<std::size_t> (frames)), right (static_cast<std::size_t> (frames));
        for (int done = 0; done < frames;)
        {
            const int n = std::min (512, frames - done);
            float* out[2] { left.data() + done, right.data() + done };
            std::fill_n (out[0], n, 0.0f);
            std::fill_n (out[1], n, 0.0f);
            transitions.process (a, b, pads, out, 2, n, true);
            done += n;
        }
        return left;
    }
};

bool near (float a, float b, float tol = 2.0e-3f) { return std::abs (a - b) <= tol; }


// The first frame at or after `from` where the output leaves `level`: the next song fades in from its first frame.
std::size_t firstChange (const std::vector<float>& x, std::size_t from, float level)
{
    for (auto i = from; i < x.size(); ++i)
        if (std::abs (x[i] - level) > 5.0e-7f)
            return i;
    return x.size();
}
} // namespace

TEST ("transition: a segue starts the next song on the trigger frame and the tail carries on (SL-30, AT-31)")
{
    Pair pair (96000);
    TransitionCommand arm;
    arm.type = TransitionCommand::Type::arm;
    arm.kind = TransitionCommand::Kind::segue;
    arm.trigger = 60000;
    arm.gap = 0;
    pair.transitions.post (arm);

    pair.a.post ([] { Command c; c.type = Command::Type::play; return c; }());
    const auto out = pair.render (96000 + 30000);

    CHECK (near (out[20000], 0.5f));
    CHECK (firstChange (out, 20000, 0.5f) >= 60000);            // nothing before the trigger
    CHECK (firstChange (out, 20000, 0.5f) <= 60001);            // the fade-in of the next song begins on it
    CHECK (near (out[60000 + 2000], 0.75f));                    // both sound: the tail and the next song
    CHECK (near (out[96000 + 10000], 0.25f));                   // the tail ended; the next song carries on
    CHECK (pair.transitions.takeHandover());
    CHECK (! pair.a.playingNow());
}

TEST ("transition: a segue gap delays the next song by that many frames (SL-20)")
{
    Pair pair (96000);
    TransitionCommand arm;
    arm.type = TransitionCommand::Type::arm;
    arm.kind = TransitionCommand::Kind::segue;
    arm.trigger = 40000;
    arm.gap = 6000;
    pair.transitions.post (arm);

    pair.a.post ([] { Command c; c.type = Command::Type::play; return c; }());
    const auto out = pair.render (96000 + 8000);
    const auto change = firstChange (out, 20000, 0.5f);
    CHECK (change >= 46000 && change <= 46001);
}

TEST ("transition: a crossfade starts the next song at the fade and fades the outgoing one out (SL-30)")
{
    Pair pair (96000);
    TransitionCommand arm;
    arm.type = TransitionCommand::Type::arm;
    arm.kind = TransitionCommand::Kind::crossfade;
    arm.trigger = 48000;
    arm.fade = 24000;
    pair.transitions.post (arm);

    pair.a.post ([] { Command c; c.type = Command::Type::play; return c; }());
    const auto out = pair.render (48000 + 30000);

    CHECK (near (out[47000], 0.5f));
    CHECK (near (out[48000 + 5000], 0.5f * 0.9f + 0.25f, 0.2f)); // both sound while A fades
    CHECK (near (out[48000 + 26000], 0.25f));                    // A has faded out; B plays at full level
    CHECK (pair.transitions.takeHandover());
}

TEST ("transition: a pad transition clicks and holds until Play, then starts on the next downbeat (SL-30, AT-30)")
{
    Pair pair (96000);

    // A click grid of 120 BPM in 4/4 from the trigger: a bar is 96000 frames.
    Plan clickPlan;
    PlanSegment segment;
    segment.start = 0;
    segment.end = 1000000;
    segment.src = -1;
    segment.click = true;
    clickPlan.segments.push_back (segment);
    clickPlan.lengthFrames = 1000000;
    clickPlan.gridOrigin = 0.0;
    clickPlan.beatFrames = 24000.0;
    clickPlan.beatsPerBar = 4;

    TransitionCommand arm;
    arm.type = TransitionCommand::Type::arm;
    arm.kind = TransitionCommand::Kind::pad;
    arm.trigger = 60000;
    arm.click = true;
    arm.clickChannel = 1;
    arm.clickWidth = 1;
    arm.clickPlan = &clickPlan;
    pair.transitions.post (arm);

    pair.a.post ([] { Command c; c.type = Command::Type::play; return c; }());
    const auto before = pair.render (60000 + 30000); // to 30000 frames into the transition
    CHECK (pair.transitions.phase() == Transitioner::Phase::padWait);

    TransitionCommand play;
    play.type = TransitionCommand::Type::startNext;
    pair.transitions.post (play);
    const auto after = pair.render (120000);
    (void) before;

    // B starts on the next bar line of the click: 96000 frames after the trigger (frame 156000 of the transition,
    // 66000 into the second render, which began 30000 frames in).
    const auto start = firstChange (after, 40000, 0.0f);
    CHECK (start >= 66000 && start <= 66001);
    CHECK (near (after[66000 + 2000], 0.25f));
    CHECK (pair.transitions.takeHandover());
}

TEST ("transition: a segue at the very end of the plan (no tail) still starts the next song (SL-30)")
{
    Pair pair (96000);
    TransitionCommand arm;
    arm.type = TransitionCommand::Type::arm;
    arm.kind = TransitionCommand::Kind::segue;
    arm.trigger = 96000; // the end of the plan: nothing after it to fade
    arm.gap = 0;
    pair.transitions.post (arm);

    pair.a.post ([] { Command c; c.type = Command::Type::play; return c; }());
    const auto out = pair.render (96000 + 20000);

    CHECK (near (out[50000], 0.5f));
    CHECK (firstChange (out, 20000, 0.5f) >= 96000);
    CHECK (near (out[96000 + 5000], 0.25f));
    CHECK (pair.transitions.takeHandover());
}
