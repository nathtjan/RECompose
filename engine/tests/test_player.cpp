// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Offline playback tests on generated WAV files: transport fades (PB-20…22), sample-exact
// seeks (PB-26, PB-55), mixer and solo-safe (PB-80, PB-82), routing (PB-85), resampling
// quality (PB-2), missing files (REL-50).
#include "check.h"

#include "audio/disk_streamer.h"
#include "audio/player.h"
#include "audio/song.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <cmath>
#include <functional>
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
