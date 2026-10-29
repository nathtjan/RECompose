// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Offline playback tests on generated WAV files: transport fades (PB-20…22), sample-exact
// seeks (PB-26, PB-55), mixer and solo-safe (PB-80, PB-82), routing (PB-85), resampling
// quality (PB-2), missing files (REL-50).
#include "check.h"

#include "audio/click.h"
#include "audio/disk_streamer.h"
#include "audio/midi_out.h"
#include "audio/player.h"
#include "audio/song.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <chrono>
#include <cmath>
#include <functional>
#include <string>
#include <vector>

using namespace rec::audio;

namespace
{
constexpr double kRate = 48000.0;

juce::File tempDir()
{
    static const auto dir = []
    {
        auto d = juce::File::getSpecialLocation (juce::File::tempDirectory)
                     .getChildFile ("rec-player-tests-" + juce::String (juce::Random::getSystemRandom().nextInt (1000000)));
        d.createDirectory();
        return d;
    }();
    return dir;
}

// Writes a float WAV whose sample (frame, channel) is f(frame, channel).
juce::File writeWav (const juce::String& name, double rate, int channels, int frames,
                     const std::function<float (int frame, int channel)>& f)
{
    const auto file = tempDir().getChildFile (name);
    file.deleteFile();
    juce::AudioBuffer<float> buffer (channels, frames);
    for (int c = 0; c < channels; ++c)
        for (int i = 0; i < frames; ++i)
            buffer.setSample (c, i, f (i, c));

    std::unique_ptr<juce::OutputStream> stream = std::make_unique<juce::FileOutputStream> (file);
    juce::WavAudioFormat wav;
    auto writer = wav.createWriterFor (stream, juce::AudioFormatWriterOptions{}
                                                   .withSampleRate (rate)
                                                   .withNumChannels (channels)
                                                   .withBitsPerSample (32)
                                                   .withSampleFormat (juce::AudioFormatWriterOptions::SampleFormat::floatingPoint));
    writer->writeFromAudioSampleBuffer (buffer, 0, frames);
    return file;
}

// The ramp encodes the frame number, so any output sample tells which source frame it is.
float ramp (int frame) { return static_cast<float> (frame % 100000) / 100000.0f; }
int decodeRamp (float value) { return static_cast<int> (std::lround (value * 100000.0f)); }

struct Rig
{
    juce::AudioFormatManager formats;
    DiskStreamer disk;
    Player player;
    std::unique_ptr<Song> song;
    int channels = 2;

    explicit Rig (std::vector<TrackSpec> specs, int outputChannels = 2, double rate = kRate)
        : channels (outputChannels)
    {
        formats.registerBasicFormats();
        song = loadSong ("song", std::move (specs), rate, 0, formats);
        disk.addSong (song.get());
        player.prepare (rate, 512, outputChannels);
        Command load;
        load.type = Command::Type::setSong;
        load.song = song.get();
        player.post (load);
        player.processCommandsOnly();
    }

    ~Rig()
    {
        Command unload;
        unload.type = Command::Type::setSong;
        player.post (unload);
        player.processCommandsOnly();
        disk.removeSong (song.get());
    }

    void send (Command::Type type, std::int64_t frame = 0)
    {
        Command c;
        c.type = type;
        c.frame = frame;
        if (type == Command::Type::seek)
            c.generation = song->requestFrame (frame).generation;
        player.post (c);
        disk.wake();
    }

    void setPlan (std::unique_ptr<Plan> plan, std::int64_t frame = 0)
    {
        Command c;
        c.type = Command::Type::setPlan;
        c.plan = song->publishPlan (std::move (plan));
        c.frame = frame;
        c.generation = song->requestFrame (frame).generation;
        player.post (c);
        disk.wake();
        player.processCommandsOnly();
    }

    // Renders frames offline; returns one vector per output channel.
    std::vector<std::vector<float>> render (int frames)
    {
        std::vector<std::vector<float>> out (static_cast<std::size_t> (channels), std::vector<float> (static_cast<std::size_t> (frames)));
        std::vector<float*> ptrs (static_cast<std::size_t> (channels));
        for (int done = 0; done < frames;)
        {
            const int n = std::min (512, frames - done);
            for (int c = 0; c < channels; ++c)
                ptrs[static_cast<std::size_t> (c)] = out[static_cast<std::size_t> (c)].data() + done;
            player.process (ptrs.data(), channels, n, true);
            done += n;
        }
        return out;
    }
};

TrackSpec spec (const juce::File& file, const char* id, int channel = 0, int width = 2)
{
    TrackSpec s;
    s.id = id;
    s.file = file.getFullPathName().toStdString();
    s.outputChannel = channel;
    s.outputWidth = width;
    return s;
}

PlanSegment seg (std::int64_t start, std::int64_t end, std::int64_t src, std::int64_t fadeIn = 0,
                 std::int64_t fadeOut = 0, std::vector<std::uint8_t> muted = {})
{
    PlanSegment s;
    s.start = start;
    s.end = end;
    s.src = src;
    s.fadeIn = fadeIn;
    s.fadeOut = fadeOut;
    s.muted = std::move (muted);
    return s;
}

std::unique_ptr<Plan> makePlan (std::vector<PlanSegment> segments)
{
    auto plan = std::make_unique<Plan>();
    for (const auto& s : segments)
        plan->lengthFrames = std::max (plan->lengthFrames, s.end);
    plan->segments = std::move (segments);
    return plan;
}

bool near (float a, float b, float tol = 1.0e-5f) { return std::abs (a - b) <= tol; }
} // namespace

TEST ("player: plays the file sample-exactly after the 10 ms fade-in (PB-20)")
{
    const auto file = writeWav ("ramp.wav", kRate, 1, 96000, [] (int i, int) { return ramp (i); });
    Rig rig ({ spec (file, "ramp", 0, 1) }, 1);

    rig.send (Command::Type::play);
    const auto out = rig.render (20000);

    // Fade-in covers the first 480 frames (10 ms at 48 kHz).
    CHECK (out[0][100] < ramp (100));
    bool exact = true;
    for (int i = 600; i < 20000; ++i)
        exact = exact && near (out[0][static_cast<std::size_t> (i)], ramp (i));
    CHECK (exact);
    CHECK (rig.player.position() == 20000);
    CHECK (rig.player.state() == TransportState::playing);
}

TEST ("player: a seek while stopped starts exactly at the target (PB-26, PB-55)")
{
    const auto file = writeWav ("ramp2.wav", kRate, 1, 200000, [] (int i, int) { return ramp (i); });
    Rig rig ({ spec (file, "ramp", 0, 1) }, 1);

    rig.send (Command::Type::seek, 123457);
    rig.send (Command::Type::play);
    const auto out = rig.render (4000);

    bool exact = true;
    for (int i = 1000; i < 4000; ++i)
        exact = exact && decodeRamp (out[0][static_cast<std::size_t> (i)]) == (123457 + i) % 100000;
    CHECK (exact);
}

TEST ("player: a seek while playing fades out, then resumes exactly at the target (PB-26)")
{
    const auto file = writeWav ("ramp3.wav", kRate, 1, 400000, [] (int i, int) { return ramp (i); });
    Rig rig ({ spec (file, "ramp", 0, 1) }, 1);

    rig.send (Command::Type::play);
    rig.render (10000);
    rig.send (Command::Type::seek, 250000);
    const auto out = rig.render (20000);

    // After the fade-out there is a short silence while the target buffers; the first
    // sound after it is frame 250000, and from there every frame follows in order.
    int silentAt = -1;
    for (int i = 0; i < 20000 && silentAt < 0; ++i)
        if (std::abs (out[0][static_cast<std::size_t> (i)]) < 1.0e-9f)
            silentAt = i;
    int resumed = -1;
    for (int i = silentAt; silentAt >= 0 && i < 20000 && resumed < 0; ++i)
        if (std::abs (out[0][static_cast<std::size_t> (i)]) > 1.0e-9f)
            resumed = i;
    CHECK (silentAt > 0 && silentAt < 1000);
    CHECK (resumed > 0);

    bool exact = resumed > 0;
    for (int i = resumed + 600; exact && i < 20000; ++i) // past the 10 ms fade-in
        exact = decodeRamp (out[0][static_cast<std::size_t> (i)]) == (250000 + (i - resumed)) % 100000;
    CHECK (exact);

    // The outgoing audio faded to silence (no hard cut): the first samples are below the source.
    CHECK (out[0][300] < ramp (10300));
}

TEST ("player: pause fades out over 20 ms and keeps the position; play resumes there (PB-21)")
{
    const auto file = writeWav ("dc.wav", kRate, 1, 200000, [] (int, int) { return 0.5f; });
    Rig rig ({ spec (file, "dc", 0, 1) }, 1);

    rig.send (Command::Type::play);
    rig.render (4800);
    rig.send (Command::Type::pause);
    const auto out = rig.render (4800);

    CHECK (out[0][0] > 0.4f);                 // still sounding at the start of the fade
    CHECK (near (out[0][1200], 0.0f));        // silent after 20 ms (960 frames) plus one chunk
    CHECK (rig.player.state() == TransportState::paused);
    const auto pausedAt = rig.player.position();
    rig.render (4800);
    CHECK (rig.player.position() == pausedAt);
}

TEST ("player: stop fades out over 50 ms and returns to the start (PB-22)")
{
    const auto file = writeWav ("dc2.wav", kRate, 1, 200000, [] (int, int) { return 0.5f; });
    Rig rig ({ spec (file, "dc", 0, 1) }, 1);

    rig.send (Command::Type::play);
    rig.render (9600);
    rig.send (Command::Type::stop);
    const auto out = rig.render (4800);

    CHECK (out[0][1200] > 0.1f);              // 25 ms in: still fading
    CHECK (near (out[0][2800], 0.0f));        // after 50 ms: silent
    CHECK (rig.player.state() == TransportState::stopped);
    CHECK (rig.player.position() == 0);
}

TEST ("player: mute, solo and solo-safe click (PB-80, PB-82)")
{
    const auto a = writeWav ("a.wav", kRate, 1, 96000, [] (int, int) { return 0.25f; });
    const auto b = writeWav ("b.wav", kRate, 1, 96000, [] (int, int) { return 0.125f; });
    const auto c = writeWav ("c.wav", kRate, 1, 96000, [] (int, int) { return 0.0625f; });

    auto click = spec (c, "click", 2, 1);
    click.soloSafe = true;
    Rig rig ({ spec (a, "a", 0, 1), spec (b, "b", 1, 1), click }, 3);

    rig.send (Command::Type::play);
    auto out = rig.render (4800);
    CHECK (near (out[0][4000], 0.25f) && near (out[1][4000], 0.125f) && near (out[2][4000], 0.0625f));

    rig.song->tracks[1]->solo.store (true); // solo b
    out = rig.render (4800);
    CHECK (near (out[0][4000], 0.0f));
    CHECK (near (out[1][4000], 0.125f));
    CHECK (near (out[2][4000], 0.0625f)); // the click keeps playing

    rig.song->tracks[1]->solo.store (false);
    rig.song->tracks[0]->mute.store (true);
    out = rig.render (4800);
    CHECK (near (out[0][4000], 0.0f) && near (out[1][4000], 0.125f));

    rig.song->tracks[0]->mute.store (false);
    rig.song->tracks[0]->gain.store (decibelsToGain (-6.0f));
    out = rig.render (4800);
    CHECK (near (out[0][4000], 0.25f * 0.501187f, 1.0e-4f));
}

TEST ("player: routing: mono to a pair is equal on both, stereo to mono is summed at -3 dB (PB-85)")
{
    const auto mono = writeWav ("m.wav", kRate, 1, 96000, [] (int, int) { return 0.2f; });
    const auto stereo = writeWav ("s.wav", kRate, 2, 96000, [] (int, int c) { return c == 0 ? 0.3f : 0.1f; });

    Rig rig ({ spec (mono, "mono", 0, 2), spec (stereo, "stereo", 2, 1) }, 4);
    rig.send (Command::Type::play);
    const auto out = rig.render (4800);

    CHECK (near (out[0][4000], 0.2f) && near (out[1][4000], 0.2f));
    CHECK (near (out[2][4000], (0.3f + 0.1f) * 0.70710678f));
    CHECK (near (out[3][4000], 0.0f));
}

TEST ("player: 44.1 kHz stems play at 48 kHz with THD+N below -90 dB (PB-2)")
{
    const double sourceRate = 44100.0;
    const double frequency = 1000.0;
    const auto file = writeWav ("sine441.wav", sourceRate, 1, 44100 * 3, [&] (int i, int)
    {
        return 0.5f * static_cast<float> (std::sin (2.0 * juce::MathConstants<double>::pi * frequency * i / sourceRate));
    });

    Rig rig ({ spec (file, "sine", 0, 1) }, 1, kRate);
    rig.send (Command::Type::play);
    const auto out = rig.render (48000 * 2);

    // Least-squares fit of a 1 kHz sine at 48 kHz over a steady stretch, then the residual.
    const int from = 9600, to = 86400;
    double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0;
    for (int i = from; i < to; ++i)
    {
        const double w = 2.0 * juce::MathConstants<double>::pi * frequency * i / kRate;
        const double s = std::sin (w), c = std::cos (w), y = out[0][static_cast<std::size_t> (i)];
        ss += s * s; sc += s * c; cc += c * c; ys += y * s; yc += y * c;
    }
    const double det = ss * cc - sc * sc;
    const double a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det;

    double signal = 0, residual = 0;
    for (int i = from; i < to; ++i)
    {
        const double w = 2.0 * juce::MathConstants<double>::pi * frequency * i / kRate;
        const double fit = a * std::sin (w) + b * std::cos (w);
        const double y = out[0][static_cast<std::size_t> (i)];
        signal += fit * fit;
        residual += (y - fit) * (y - fit);
    }
    const double thdN = 10.0 * std::log10 (residual / signal);
    std::printf ("  resampled THD+N: %.1f dB, amplitude %.4f\n", thdN, std::sqrt (a * a + b * b));
    CHECK (thdN < -90.0);
    CHECK (std::abs (std::sqrt (a * a + b * b) - 0.5) < 0.001);
}

TEST ("player: the end of the song stops and returns to the start (PB-25)")
{
    const auto file = writeWav ("short.wav", kRate, 1, 6000, [] (int, int) { return 0.5f; });
    Rig rig ({ spec (file, "short", 0, 1) }, 1);

    rig.send (Command::Type::play);
    rig.render (12000);
    CHECK (rig.player.state() == TransportState::stopped);
    CHECK (rig.player.position() == 0);
}

TEST ("player: a missing file is silent and the rest plays (REL-50)")
{
    const auto file = writeWav ("ok.wav", kRate, 1, 96000, [] (int, int) { return 0.25f; });
    TrackSpec gone;
    gone.id = "gone";
    gone.file = tempDir().getChildFile ("does-not-exist.wav").getFullPathName().toStdString();

    Rig rig ({ spec (file, "ok", 0, 1), gone }, 1);
    CHECK (rig.song->tracks[1]->missing);
    CHECK (rig.song->tracks[1]->problem == "File missing");

    rig.send (Command::Type::play);
    const auto out = rig.render (4800);
    CHECK (near (out[0][4000], 0.25f));
}

// ---- Plans (PB-40, PB-55, AT-3, AT-12) ------------------------------------------------

TEST ("plan: sections play in the order of the plan, repeats included (PB-40)")
{
    const auto file = writeWav ("plan-order.wav", kRate, 1, 60000, [] (int i, int) { return ramp (i); });
    Rig rig ({ spec (file, "ramp", 0, 1) }, 1);
    rig.setPlan (makePlan ({ seg (0, 2000, 5000), seg (2000, 4000, 5000), seg (4000, 6000, 20000) }));

    rig.send (Command::Type::play);
    const auto out = rig.render (6500);

    bool first = true, second = true, third = true;
    for (int i = 600; i < 1990; ++i)
        first = first && decodeRamp (out[0][static_cast<std::size_t> (i)]) == 5000 + i;
    for (int i = 2500; i < 3990; ++i) // past the 10 ms blend at the seam
        second = second && decodeRamp (out[0][static_cast<std::size_t> (i)]) == 5000 + (i - 2000);
    for (int i = 4500; i < 5990; ++i)
        third = third && decodeRamp (out[0][static_cast<std::size_t> (i)]) == 20000 + (i - 4000);
    CHECK (first);
    CHECK (second);
    CHECK (third);

    // The plan ends at its last segment: stopped, back at the start.
    CHECK (rig.player.state() == TransportState::stopped);
    CHECK (rig.player.position() == 0);
    bool silent = true;
    for (int i = 6000; i < 6500; ++i)
        silent = silent && std::abs (out[0][static_cast<std::size_t> (i)]) < 1.0e-9f;
    CHECK (silent);
}

TEST ("plan: contiguous segments have no seam")
{
    const auto file = writeWav ("plan-contiguous.wav", kRate, 1, 60000, [] (int i, int) { return ramp (i); });
    Rig rig ({ spec (file, "ramp", 0, 1) }, 1);
    rig.setPlan (makePlan ({ seg (0, 3000, 1000), seg (3000, 6000, 4000) }));

    rig.send (Command::Type::play);
    const auto out = rig.render (6000);

    bool exact = true;
    for (int i = 600; i < 6000; ++i)
        exact = exact && decodeRamp (out[0][static_cast<std::size_t> (i)]) == 1000 + i;
    CHECK (exact);
}

TEST ("plan: a splice lands on the boundary and does not pop (PB-55, AT-3)")
{
    // A tone, so a hard cut would be a step; the seam jumps by a non-multiple of the period.
    const auto file = writeWav ("plan-tone.wav", kRate, 1, 80000, [] (int i, int)
                                { return 0.5f * std::sin (6.2831853f * 440.0f * static_cast<float> (i) / 48000.0f); });
    Rig rig ({ spec (file, "tone", 0, 1) }, 1);
    rig.setPlan (makePlan ({ seg (0, 8000, 1000), seg (8000, 16000, 30077) }));

    rig.send (Command::Type::play);
    const auto out = rig.render (16000);

    // Incoming material is on the boundary to within a sample once the 10 ms blend is over.
    float worst = 0.0f;
    for (int i = 8500; i < 12000; ++i)
    {
        const float expected = 0.5f * std::sin (6.2831853f * 440.0f * static_cast<float> (30077 + (i - 8000)) / 48000.0f);
        worst = std::max (worst, std::abs (out[0][static_cast<std::size_t> (i)] - expected));
    }
    CHECK (worst < 1.0e-4f);

    // No step at the seam: the largest sample-to-sample change near it stays within what two tones
    // blending produce, far below the full-scale jump a hard cut makes.
    float seam = 0.0f, tone = 0.0f;
    for (int i = 7900; i < 8300; ++i)
        seam = std::max (seam, std::abs (out[0][static_cast<std::size_t> (i)] - out[0][static_cast<std::size_t> (i - 1)]));
    for (int i = 3000; i < 3400; ++i)
        tone = std::max (tone, std::abs (out[0][static_cast<std::size_t> (i)] - out[0][static_cast<std::size_t> (i - 1)]));
    CHECK (seam < tone * 2.5f + 1.0e-3f);
}

TEST ("plan: a mute ramps over 10 ms in the segment it applies to (AT-12)")
{
    const auto a = writeWav ("plan-a.wav", kRate, 1, 60000, [] (int, int) { return 0.5f; });
    const auto b = writeWav ("plan-b.wav", kRate, 1, 60000, [] (int, int) { return 0.25f; });
    Rig rig ({ spec (a, "a", 0, 1), spec (b, "b", 0, 1) }, 1);
    rig.setPlan (makePlan ({ seg (0, 4000, 0), seg (4000, 8000, 4000, 0, 0, { 1, 0 }), seg (8000, 12000, 8000) }));

    rig.send (Command::Type::play);
    const auto out = rig.render (12000);

    CHECK (near (out[0][3000], 0.75f, 1.0e-3f));
    CHECK (near (out[0][4000 + 240], 0.5f, 0.02f));  // half way down the ramp
    CHECK (near (out[0][4000 + 600], 0.25f, 1.0e-3f));
    CHECK (near (out[0][7000], 0.25f, 1.0e-3f));
    CHECK (near (out[0][8000 + 600], 0.75f, 1.0e-3f)); // unmuted again

    bool monotonic = true;
    for (int i = 4001; i < 4480; ++i)
        monotonic = monotonic && out[0][static_cast<std::size_t> (i)] <= out[0][static_cast<std::size_t> (i - 1)] + 1.0e-6f;
    CHECK (monotonic);
}

TEST ("plan: fades are dB-linear from −60 dB and skip solo-safe tracks (PB-40)")
{
    const auto a = writeWav ("plan-fade-a.wav", kRate, 1, 60000, [] (int, int) { return 1.0f; });
    const auto b = writeWav ("plan-fade-b.wav", kRate, 1, 60000, [] (int, int) { return 1.0f; });
    auto safe = spec (b, "b", 1, 1);
    safe.soloSafe = true;
    Rig rig ({ spec (a, "a", 0, 1), safe }, 2);
    rig.setPlan (makePlan ({ seg (0, 14400, 0, 4800, 4800) }));

    rig.send (Command::Type::play);
    const auto out = rig.render (14400);

    CHECK (near (out[0][2400], std::pow (10.0f, -1.5f), 2.0e-3f)); // half way up, −30 dB
    CHECK (near (out[0][7200], 1.0f, 1.0e-3f));
    CHECK (near (out[0][14400 - 2400], std::pow (10.0f, -1.5f), 2.0e-3f));
    CHECK (out[0][14390] < 0.002f);
    CHECK (near (out[1][7200], 1.0f, 1.0e-3f));
    CHECK (near (out[1][14400 - 2400], 1.0f, 1.0e-3f)); // the solo-safe track plays through
}

TEST ("plan: a silent segment plays nothing and the next one carries on (PB-40)")
{
    const auto file = writeWav ("plan-silent.wav", kRate, 1, 60000, [] (int, int) { return 0.5f; });
    Rig rig ({ spec (file, "a", 0, 1) }, 1);
    rig.setPlan (makePlan ({ seg (0, 3000, 0), seg (3000, 6000, -1), seg (6000, 9000, 0) }));

    rig.send (Command::Type::play);
    const auto out = rig.render (9000);

    CHECK (near (out[0][2000], 0.5f, 1.0e-3f));
    bool silent = true;
    for (int i = 3600; i < 6000; ++i) // after the 10 ms ring-out of the previous segment
        silent = silent && std::abs (out[0][static_cast<std::size_t> (i)]) < 1.0e-6f;
    CHECK (silent);
    CHECK (near (out[0][8000], 0.5f, 1.0e-3f));
}

TEST ("plan: a new plan keeps the playhead where it was told (PB-40)")
{
    const auto file = writeWav ("plan-resume.wav", kRate, 1, 100000, [] (int i, int) { return ramp (i); });
    Rig rig ({ spec (file, "ramp", 0, 1) }, 1);
    rig.setPlan (makePlan ({ seg (0, 4000, 10000), seg (4000, 8000, 10000) }), 5000);

    rig.send (Command::Type::play);
    const auto out = rig.render (2000);

    bool exact = true;
    for (int i = 800; i < 2000; ++i) // plan frame 5000 + i is source 10000 + 1000 + i
        exact = exact && decodeRamp (out[0][static_cast<std::size_t> (i)]) == 11000 + i;
    CHECK (exact);
}

namespace
{
// Sign changes of a click, a stand-in for its pitch: the accent (beat 1) is higher.
int signChanges (const std::vector<float>& x, std::size_t from, std::size_t count)
{
    int n = 0;
    for (auto i = from + 1; i < from + count; ++i)
        if ((x[i - 1] < 0.0f) != (x[i] < 0.0f))
            ++n;
    return n;
}

bool silentBetween (const std::vector<float>& x, std::size_t from, std::size_t to)
{
    for (auto i = from; i < to; ++i)
        if (std::abs (x[i]) > 1.0e-6f)
            return false;
    return true;
}
} // namespace

TEST ("click: a count-in clicks on the grid to the sample, accented on beat 1 (PB-70, SL-36)")
{
    // 120 BPM in 4/4: a beat is 24000 frames. Bar 1 is at 120000; the count-in bar before it clicks.
    const auto file = writeWav ("click-song.wav", kRate, 1, 200000, [] (int, int) { return 0.0f; });
    TrackSpec click;
    click.id = "generated-click";
    click.generated = "click";
    click.outputChannel = 0;
    click.outputWidth = 1;
    click.soloSafe = true;
    Rig rig ({ spec (file, "music", 0, 1), click }, 1);

    auto plan = makePlan ({ seg (0, 24000, 0), seg (24000, 120000, -1), seg (120000, 168000, 0) });
    plan->segments[1].click = true;
    plan->gridOrigin = 120000.0;
    plan->beatFrames = 24000.0;
    plan->beatsPerBar = 4;
    rig.setPlan (std::move (plan));

    rig.send (Command::Type::play);
    const auto out = rig.render (168000);
    const auto& x = out[0];
    const auto clickLength = static_cast<std::size_t> (kClickMs * kRate / 1000.0);

    // Each beat of the count-in starts on its frame at −6 dBFS; nothing comes before it.
    for (const std::size_t beat : { 24000u, 48000u, 72000u, 96000u })
    {
        CHECK (near (x[beat], kClickPeak, 1.0e-4f));
        CHECK (std::abs (x[beat - 1]) < 1.0e-6f);
    }
    CHECK (silentBetween (x, 0, 24000));                       // the segment before has no click
    CHECK (silentBetween (x, 24000 + clickLength, 48000));     // between beats
    CHECK (silentBetween (x, 96000 + clickLength, 168000));    // bar 1 is music: the click stops

    // Beat 1 of the count-in bar is the accent.
    CHECK (signChanges (x, 24000, clickLength) > signChanges (x, 48000, clickLength));
}

TEST ("automation: plan MIDI goes out as the playhead passes it, and a seek jumps over it (protocol 13, AUTO-5)")
{
    const auto file = writeWav ("midi-song.wav", kRate, 1, 120000, [] (int, int) { return 0.0f; });
    Rig rig ({ spec (file, "music", 0, 1) }, 1);
    MidiOutQueue queue;
    rig.player.setMidiOut (&queue);

    auto plan = makePlan ({ seg (0, 100000, 0) });
    for (const auto& [frame, id] : { std::pair { 1000, 1 }, std::pair { 50000, 2 }, std::pair { 90000, 3 } })
        plan->midi.push_back ({ frame, { 0x90, 60, 127 }, 3, id });
    rig.setPlan (std::move (plan));

    const auto taken = [&queue]
    {
        std::vector<int> ids;
        PlanMidi m;
        while (queue.pop (m))
            ids.push_back (m.id);
        return ids;
    };

    rig.send (Command::Type::play);
    rig.render (20000);
    CHECK (taken() == std::vector<int> { 1 });
    rig.send (Command::Type::seek, 80000);
    rig.render (20000);
    CHECK (taken() == std::vector<int> { 3 }); // 50000 was never played
    rig.player.setMidiOut (nullptr);
}

TEST ("automation: spoken calls are rendered into the speech track on their frame (protocol 13, AUTO-31)")
{
    const auto file = writeWav ("speech-song.wav", kRate, 1, 60000, [] (int, int) { return 0.0f; });
    TrackSpec speech;
    speech.id = "generated-speech";
    speech.generated = "speech";
    speech.outputChannel = 0;
    speech.outputWidth = 1;
    Rig rig ({ spec (file, "music", 0, 1), speech }, 1);

    Clip clip;
    clip.samples.assign (100, 0.5f);
    auto plan = makePlan ({ seg (0, 48000, 0) });
    plan->calls.push_back ({ 24000, &clip, 0.5f });
    rig.setPlan (std::move (plan));

    rig.send (Command::Type::play);
    const auto out = rig.render (48000);
    CHECK (near (out[0][24000], 0.25f));
    CHECK (near (out[0][24099], 0.25f));
    CHECK (silentBetween (out[0], 1000, 24000));
    CHECK (silentBetween (out[0], 24100, 48000));
}

TEST ("click: rendering in pieces gives the same samples as in one go (PB-70)")
{
    Plan plan;
    PlanSegment all;
    all.start = 0;
    all.end = 100000;
    all.src = -1;
    all.click = true;
    plan.segments.push_back (all);
    plan.lengthFrames = 100000;
    plan.gridOrigin = 1000.5; // off the frame grid: each beat is rounded once
    plan.beatFrames = 20833.333;
    plan.beatsPerBar = 3;

    std::vector<float> whole (100000, 0.0f), pieces (100000, 0.0f);
    renderClick (plan, 0, 100000, kRate, whole.data());
    for (int at = 0; at < 100000; at += 777)
        renderClick (plan, at, std::min (777, 100000 - at), kRate, pieces.data() + at);

    bool same = true;
    for (std::size_t i = 0; i < whole.size(); ++i)
        same = same && whole[i] == pieces[i];
    CHECK (same);
    CHECK (near (whole[static_cast<std::size_t> (beatFrame (plan, 0))], kClickPeak, 1.0e-4f));
    CHECK (beatFrame (plan, 1) == 21834); // 1000.5 + 20833.333 = 21833.833, rounded once
}

namespace
{
constexpr double kPi = 3.141592653589793;

// Rising zero crossings per second over [from, from + count): the pitch of a sine.
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

std::size_t loudest (const std::vector<float>& x, std::size_t from, std::size_t to)
{
    std::size_t best = from;
    for (auto i = from; i < to; ++i)
        if (std::abs (x[i]) > std::abs (x[best]))
            best = i;
    return best;
}

TrackSpec transposing (const juce::File& file, const char* id, int channel)
{
    auto s = spec (file, id, channel, 1);
    s.transposes = true;
    return s;
}
} // namespace

TEST ("key: +12 doubles the pitch of a transposing track; a track that doesn't transpose is untouched (PB-58, DM-12, AT-11)")
{
    const auto sine = writeWav ("key-sine.wav", kRate, 1, 200000, [] (int i, int) { return 0.5f * static_cast<float> (std::sin (2.0 * kPi * 440.0 * i / kRate)); });
    const auto click = writeWav ("key-ramp.wav", kRate, 1, 200000, [] (int i, int) { return ramp (i); });
    Rig rig ({ transposing (sine, "music", 0), spec (click, "click", 1, 1) }, 2);

    auto plan = makePlan ({ seg (0, 96000, 0) });
    plan->segments[0].key = 12;
    rig.setPlan (std::move (plan));

    rig.send (Command::Type::play);
    const auto out = rig.render (96000);

    const auto f = frequency (out[0], 24000, 48000);
    CHECK (f > 870.0 && f < 890.0);

    bool exact = true; // the click's samples come through bit for bit (after Play's 10 ms fade-in)
    for (int i = 1000; i < 96000; ++i)
        exact = exact && decodeRamp (out[1][static_cast<std::size_t> (i)]) == i;
    CHECK (exact);
}

TEST ("key: a shifted track stays on the source's timeline (PB-58)")
{
    // A single impulse at source frame 30000 on silence.
    const auto file = writeWav ("key-impulse.wav", kRate, 1, 120000, [] (int i, int) { return i == 30000 ? 0.9f : 0.0f; });
    Rig rig ({ transposing (file, "music", 0) }, 1);

    auto plan = makePlan ({ seg (0, 96000, 0) });
    plan->segments[0].key = 2;
    rig.setPlan (std::move (plan));

    rig.send (Command::Type::play);
    const auto out = rig.render (96000);

    const auto at = static_cast<long> (loudest (out[0], 0, 96000));
    CHECK (std::abs (at - 30000) <= 240); // within 5 ms: the stretcher smears a click, it doesn't move it
}

TEST ("tempo: at 80 % the pitch stays and the plan takes 1.25 times as long (PB-60)")
{
    const auto sine = writeWav ("tempo-sine.wav", kRate, 1, 200000, [] (int i, int) { return 0.5f * static_cast<float> (std::sin (2.0 * kPi * 440.0 * i / kRate)); });
    Rig rig ({ transposing (sine, "music", 0) }, 1);

    // 1 s of source played at 80 %: 1.25 s of output frames (the engine converts plan seconds this way).
    auto plan = makePlan ({ seg (0, 60000, 0) });
    plan->tempo = 0.8;
    rig.setPlan (std::move (plan));

    rig.send (Command::Type::play);
    const auto out = rig.render (60000);
    const auto f = frequency (out[0], 12000, 36000);
    CHECK (f > 432.0 && f < 448.0);
    CHECK (rms (out[0], 12000, 36000) > 0.25f);

    rig.render (512); // the end of the plan: stopped, back at the start
    CHECK (rig.player.state() == TransportState::stopped);
}

TEST ("key: a key change on a contiguous boundary crossfades with no gap (PB-58)")
{
    const auto sine = writeWav ("lift-sine.wav", kRate, 1, 200000, [] (int i, int) { return 0.5f * static_cast<float> (std::sin (2.0 * kPi * 440.0 * i / kRate)); });
    Rig rig ({ transposing (sine, "music", 0) }, 1);

    // The second segment continues the same audio two semitones up: a key lift without a jump.
    auto plan = makePlan ({ seg (0, 48000, 0), seg (48000, 96000, 48000) });
    plan->segments[1].key = 2;
    rig.setPlan (std::move (plan));

    rig.send (Command::Type::play);
    const auto out = rig.render (96000);

    const auto before = frequency (out[0], 12000, 30000);
    const auto after = frequency (out[0], 60000, 30000);
    CHECK (before > 435.0 && before < 445.0);
    CHECK (after > 487.0 && after < 501.0); // 440 · 2^(2/12) = 493.9 Hz

    // The two keys beat against each other while they crossfade (440 and 494 Hz cancel for a moment every
    // 18.5 ms), so the level is measured over 20 ms windows, longer than a beat: a gap would be silence
    // across a whole window, beating isn't.
    bool noGap = true;
    for (std::size_t at = 46000; at < 52000; at += 480)
        noGap = noGap && rms (out[0], at, 960) > 0.5f * 0.354f;
    CHECK (noGap);
}

TEST ("key: 16 shifted stereo stems are produced well ahead of real time (PB-91)")
{
    const auto file = writeWav ("load-noise.wav", kRate, 2, 48000 * 12, [] (int i, int c) {
        return 0.2f * static_cast<float> (std::sin (0.031 * i + c) * std::sin (0.0007 * i));
    });
    std::vector<TrackSpec> specs;
    for (int t = 0; t < 16; ++t)
    {
        auto s = spec (file, "", 0, 2);
        s.id = "stem-" + std::to_string (t);
        s.transposes = true;
        specs.push_back (s);
    }
    Rig rig (std::move (specs), 2);
    auto plan = makePlan ({ seg (0, 48000 * 10, 0) });
    plan->segments[0].key = 2;
    rig.setPlan (std::move (plan));

    rig.send (Command::Type::play);
    const auto started = std::chrono::steady_clock::now();
    rig.render (48000 * 10);
    const auto seconds = std::chrono::duration<double> (std::chrono::steady_clock::now() - started).count();
    const auto speed = 10.0 / seconds;
    std::fprintf (stderr, "  16 stereo stems at +2 semitones: %.1fx real time\n", speed);
    CHECK (speed > 2.0); // the disk thread has to stay ahead with room for jumps and the next song
}
