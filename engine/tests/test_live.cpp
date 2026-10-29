// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Live plan changes and holds offline (PB-47, PB-52, PB-55, ARR-90, AT-13, AT-27). The source is a ramp, one value
// per frame, so the output says exactly which source frame played at every output frame.
#include "check.h"

#include "audio/click.h"
#include "audio/disk_streamer.h"
#include "audio/player.h"
#include "audio/song.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <cmath>
#include <vector>

using namespace rec::audio;

namespace
{
constexpr double kRate = 48000.0;
constexpr float kStep = 1.0e-6f;

float ramp (std::int64_t frame) { return 0.01f + static_cast<float> (frame) * kStep; }

juce::File rampWav (int frames)
{
    static const auto dir = []
    {
        auto d = juce::File::getSpecialLocation (juce::File::tempDirectory)
                     .getChildFile ("rec-live-tests-" + juce::String (juce::Random::getSystemRandom().nextInt (1000000)));
        d.createDirectory();
        return d;
    }();
    const auto file = dir.getChildFile ("ramp.wav");
    juce::AudioBuffer<float> buffer (1, frames);
    for (int i = 0; i < frames; ++i)
        buffer.setSample (0, i, ramp (i));
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

std::unique_ptr<Plan> plan (std::initializer_list<PlanSegment> segments, std::initializer_list<PlanHold> holds = {})
{
    auto p = std::make_unique<Plan>();
    for (const auto& s : segments)
    {
        p->segments.push_back (s);
        p->lengthFrames = std::max (p->lengthFrames, s.end);
    }
    p->holds = holds;
    return p;
}

PlanSegment seg (std::int64_t start, std::int64_t end, std::int64_t src)
{
    PlanSegment s;
    s.start = start;
    s.end = end;
    s.src = src;
    return s;
}

struct Rig
{
    juce::AudioFormatManager formats;
    DiskStreamer disk;
    Player player;
    std::unique_ptr<Song> song;
    std::vector<float> left, right;

    explicit Rig (std::unique_ptr<Plan> first, bool withClick = false)
    {
        formats.registerBasicFormats();
        TrackSpec music;
        music.id = "t";
        music.file = rampWav (400000).getFullPathName().toStdString();
        music.outputChannel = 0;
        music.outputWidth = 1;
        std::vector<TrackSpec> specs { music };
        if (withClick)
        {
            TrackSpec click;
            click.id = "click";
            click.generated = "click";
            click.outputChannel = 1;
            click.outputWidth = 1;
            click.soloSafe = true;
            specs.push_back (click);
        }

        song = loadSong ("s", specs, kRate, 0, formats);
        disk.addSong (song.get());
        player.prepare (kRate, 512, 2);

        Command set;
        set.type = Command::Type::setSong;
        set.song = song.get();
        player.post (set);
        player.processCommandsOnly();

        Command c;
        c.type = Command::Type::setPlan;
        c.plan = song->publishPlan (std::move (first));
        c.frame = 0;
        c.generation = song->requestFrame (0).generation;
        player.post (c);
        player.processCommandsOnly();
        disk.wake();

        Command play;
        play.type = Command::Type::play;
        player.post (play);
    }

    ~Rig()
    {
        Command unload;
        unload.type = Command::Type::setSong;
        player.post (unload);
        player.processCommandsOnly();
        disk.removeSong (song.get());
    }

    void render (int frames)
    {
        for (int done = 0; done < frames;)
        {
            const int n = std::min (512, frames - done);
            std::vector<float> a (static_cast<std::size_t> (n)), b (static_cast<std::size_t> (n));
            float* out[2] { a.data(), b.data() };
            player.process (out, 2, n, true);
            left.insert (left.end(), a.begin(), a.end());
            right.insert (right.end(), b.begin(), b.end());
            done += n;
        }
    }

    void swap (std::unique_ptr<Plan> next, std::int64_t frame)
    {
        Command c;
        c.type = Command::Type::swapPlan;
        c.plan = song->publishSwap (std::move (next), frame);
        c.frame = frame;
        player.post (c);
        disk.wake();
    }

    void send (Command::Type type)
    {
        Command c;
        c.type = type;
        player.post (c);
        disk.wake();
    }
};

bool follows (const std::vector<float>& out, std::int64_t from, std::int64_t to, std::int64_t srcAtFrom, float tol = 2.0e-6f)
{
    for (auto n = from; n < to; ++n)
        if (std::abs (out[static_cast<std::size_t> (n)] - ramp (srcAtFrom + (n - from))) > tol)
        {
            std::fprintf (stderr, "  first difference at %lld: %g, expected %g\n", static_cast<long long> (n), out[static_cast<std::size_t> (n)], ramp (srcAtFrom + (n - from)));
            return false;
        }
    return true;
}
} // namespace

TEST ("swap: a plan that carries on from the same source plays on with no seam (ARR-90)")
{
    constexpr std::int64_t swapAt = 20000;
    Rig rig (plan ({ seg (0, 300000, 0) }));
    rig.render (5000);
    rig.swap (plan ({ seg (0, swapAt, 0), seg (swapAt, 300000, swapAt) }), swapAt);
    rig.render (60000);

    CHECK (follows (rig.left, 1000, 65000, 1000));
}

TEST ("swap: onto a jump, the old plan plays up to the frame and the new one from it (PB-47, PB-55)")
{
    constexpr std::int64_t swapAt = 20000;
    constexpr std::int64_t target = 150000;
    Rig rig (plan ({ seg (0, 300000, 0) }));
    rig.render (5000);
    rig.swap (plan ({ seg (0, swapAt, 0), seg (swapAt, 200000, target) }), swapAt);
    rig.render (40000);

    CHECK (follows (rig.left, 1000, swapAt, 1000));
    CHECK (follows (rig.left, swapAt + 600, swapAt + 20000, target + 600));

    // The seam is a splice, not a gap: something always sounds across it.
    bool silent = false;
    for (auto n = swapAt; n < swapAt + 480; ++n)
        silent = silent || std::abs (rig.left[static_cast<std::size_t> (n)]) < 0.005f;
    CHECK (! silent);
}

TEST ("swap: one that arrives after the playhead passed its frame takes effect at once")
{
    Rig rig (plan ({ seg (0, 300000, 0) }));
    rig.render (30000);
    rig.swap (plan ({ seg (0, 10000, 0), seg (10000, 300000, 150000) }), 10000);
    rig.render (30000);

    // Playing on from where the playhead was, in the new plan: source 150000 + (n - 10000).
    CHECK (follows (rig.left, 35000, 55000, 150000 + 35000 - 10000));
}

TEST ("swap: two in a row, the later one wins and the earlier frame still counts")
{
    Rig rig (plan ({ seg (0, 300000, 0) }));
    rig.render (5000);
    rig.swap (plan ({ seg (0, 30000, 0), seg (30000, 300000, 100000) }), 30000);
    rig.swap (plan ({ seg (0, 20000, 0), seg (20000, 300000, 200000) }), 20000);
    rig.render (40000);

    CHECK (follows (rig.left, 1000, 20000, 1000));
    CHECK (follows (rig.left, 21000, 40000, 200000 + 1000));
}

TEST ("hold: the playhead waits at the marker, the stems are silent, and GO moves on (PB-52, AT-13)")
{
    constexpr std::int64_t holdAt = 20000;
    Rig rig (plan ({ seg (0, 300000, 0) }, { PlanHold { holdAt, false, -1, false } }));
    rig.render (40000);

    CHECK (follows (rig.left, 1000, holdAt, 1000));
    float after = 0.0f;
    for (std::size_t n = holdAt; n < 40000; ++n)
        after = std::max (after, std::abs (rig.left[n]));
    CHECK (after == 0.0f);
    CHECK (rig.player.holding());
    CHECK (rig.player.position() == holdAt);
    CHECK (rig.player.holdFrames() > 15000);

    rig.send (Command::Type::go);
    rig.render (10000);
    CHECK (! rig.player.holding());
    CHECK (follows (rig.left, 40000, 49000, holdAt));
}

TEST ("hold: a click runs at the song tempo while it waits and stops at GO (PB-52)")
{
    constexpr std::int64_t holdAt = 20000;
    auto p = plan ({ seg (0, 300000, 0) }, { PlanHold { holdAt, false, -1, true } });
    p->beatFrames = 24000.0;
    p->beatsPerBar = 4;
    Rig rig (std::move (p), true);
    rig.render (holdAt + 60000);

    float duringHold = 0.0f;
    for (std::size_t n = holdAt; n < rig.right.size(); ++n)
        duringHold = std::max (duringHold, std::abs (rig.right[n]));
    CHECK (duringHold > 0.1f);

    // Clicks 24000 frames apart (the beat), the first one as the hold starts.
    CHECK (std::abs (rig.right[holdAt + 24000 + 30]) > 0.01f);
    CHECK (std::abs (rig.right[holdAt + 12000]) < 1.0e-4f);

    rig.send (Command::Type::go);
    rig.render (50000);
    float afterGo = 0.0f;
    for (std::size_t n = holdAt + 60000 + 2000; n < rig.right.size(); ++n)
        afterGo = std::max (afterGo, std::abs (rig.right[n]));
    CHECK (afterGo < 1.0e-4f);
}

TEST ("hold: a swap that removes the hold releases the playhead (PB-47)")
{
    constexpr std::int64_t holdAt = 20000;
    Rig rig (plan ({ seg (0, 300000, 0) }, { PlanHold { holdAt, false, -1, false } }));
    rig.render (30000);
    CHECK (rig.player.holding());

    rig.swap (plan ({ seg (0, holdAt, 0), seg (holdAt, 300000, 100000) }), holdAt);
    rig.render (20000);
    CHECK (! rig.player.holding());
    CHECK (follows (rig.left, 36000, 48000, 100000 + 6000));
}

TEST ("panic: during a hold it stops at once, while playing it fades out in 100 ms (UI-40)")
{
    {
        Rig rig (plan ({ seg (0, 300000, 0) }, { PlanHold { 20000, false, -1, false } }));
        rig.render (30000);
        CHECK (rig.player.holding());
        rig.send (Command::Type::panic);
        rig.render (2000);
        CHECK (! rig.player.holding());
        CHECK (rig.player.state() == TransportState::stopped);
    }

    Rig rig (plan ({ seg (0, 300000, 0) }));
    rig.render (10000);
    rig.send (Command::Type::panic);
    rig.render (10000);
    CHECK (rig.player.state() == TransportState::stopped);

    // Silent by 100 ms (4800 frames) after the panic, and still sounding half way.
    const std::size_t at = 10000;
    CHECK (std::abs (rig.left[at + 2400]) > 0.001f);
    CHECK (std::abs (rig.left[at + 6000]) < 1.0e-6f);
}

TEST ("soak: 200 random jumps each land on their source frame with no starved block (AT-47, reduced)")
{
    std::vector<PlanSegment> segments { seg (0, 60000, 0) };
    auto build = [&segments]
    {
        auto p = std::make_unique<Plan>();
        p->segments = segments;
        p->lengthFrames = segments.back().end;
        return p;
    };

    Rig rig (build());
    rig.render (2000);
    juce::Random random (4711);
    int landed = 0;
    for (int i = 0; i < 200; ++i)
    {
        const auto now = static_cast<std::int64_t> (rig.left.size());
        const auto at = now + 1000 + random.nextInt (3000);
        const auto target = static_cast<std::int64_t> (random.nextInt (330000));

        // The new plan is the old one up to the swap frame, then a jump (a live action, PB-47).
        while (! segments.empty() && segments.back().start >= at)
            segments.pop_back();
        segments.back().end = at;
        segments.push_back (seg (at, at + 60000, target));
        rig.swap (build(), at);

        rig.render (static_cast<int> (at - now) + 2500);
        if (follows (rig.left, at + 600, at + 2500, target + 600))
            ++landed;
    }

    CHECK (landed == 200);
    CHECK (rig.player.starvedBlocks() == 0);
}
