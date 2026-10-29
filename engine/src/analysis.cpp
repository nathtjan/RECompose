// SPDX-License-Identifier: AGPL-3.0-or-later
#include "analysis.h"

#include "message_handler.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <CDSPResampler.h>

#if RECOMPOSE_WITH_WHISPER
  #include <whisper.h>
#endif

#include <algorithm>
#include <cmath>
#include <complex>
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>
#include <thread>

namespace rec::analysis
{
namespace
{
    constexpr double pi = 3.14159265358979323846;

    void print (const juce::var& value) { std::printf ("%s\n", toJson (value).c_str()); }

    int fail (const juce::String& problem, int code)
    {
        auto* o = new juce::DynamicObject();
        juce::var answer (o);
        o->setProperty ("ok", false);
        o->setProperty ("problem", problem);
        print (answer);
        return code;
    }

    /// The whole file as one mono channel (channels averaged).
    bool readMono (const char* path, std::vector<float>& mono, double& sampleRate, juce::String& problem)
    {
        juce::AudioFormatManager formats;
        formats.registerBasicFormats();
        const juce::File file (juce::String::fromUTF8 (path));
        if (! file.existsAsFile())
        {
            problem = "File missing";
            return false;
        }

        std::unique_ptr<juce::AudioFormatReader> reader (formats.createReaderFor (file));
        if (reader == nullptr)
        {
            problem = "Can't read this file";
            return false;
        }

        sampleRate = reader->sampleRate;
        const auto total = reader->lengthInSamples;
        if (total <= 0 || total > std::int64_t (1) << 31)
        {
            problem = total <= 0 ? "The file is empty" : "The file is too long to analyse";
            return false;
        }

        const int channels = juce::jmax (1, static_cast<int> (reader->numChannels));
        mono.assign (static_cast<size_t> (total), 0.0f);
        juce::AudioBuffer<float> buffer (channels, 65536);
        for (juce::int64 start = 0; start < total; start += buffer.getNumSamples())
        {
            const int n = static_cast<int> (juce::jmin<juce::int64> (buffer.getNumSamples(), total - start));
            reader->read (buffer.getArrayOfWritePointers(), channels, start, n);
            for (int c = 0; c < channels; ++c)
            {
                const float* src = buffer.getReadPointer (c);
                float* dst = mono.data() + start;
                for (int i = 0; i < n; ++i)
                    dst[i] += src[i] / static_cast<float> (channels);
            }
        }

        return true;
    }

    /// In-place radix-2 FFT (size a power of two).
    void fft (std::vector<std::complex<double>>& a)
    {
        const size_t n = a.size();
        for (size_t i = 1, j = 0; i < n; ++i)
        {
            size_t bit = n >> 1;
            for (; j & bit; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                std::swap (a[i], a[j]);
        }

        for (size_t len = 2; len <= n; len <<= 1)
        {
            const double angle = -2 * pi / static_cast<double> (len);
            const std::complex<double> wlen (std::cos (angle), std::sin (angle));
            for (size_t i = 0; i < n; i += len)
            {
                std::complex<double> w (1);
                for (size_t j = 0; j < len / 2; ++j)
                {
                    const auto u = a[i + j];
                    const auto v = a[i + j + len / 2] * w;
                    a[i + j] = u + v;
                    a[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    double centroid (const float* x, int available, double sampleRate)
    {
        constexpr int size = 1024;
        std::vector<std::complex<double>> bins (size);
        for (int i = 0; i < size; ++i)
        {
            const double window = 0.5 - 0.5 * std::cos (2 * pi * i / (size - 1));
            bins[static_cast<size_t> (i)] = i < available ? x[i] * window : 0.0;
        }
        fft (bins);

        double weighted = 0, total = 0;
        for (int k = 1; k < size / 2; ++k)
        {
            const double magnitude = std::abs (bins[static_cast<size_t> (k)]);
            weighted += magnitude * k * sampleRate / size;
            total += magnitude;
        }
        return total > 0 ? weighted / total : 0.0;
    }

    juce::var number (double v) { return juce::var (std::round (v * 1e6) / 1e6); }
} // namespace

std::vector<Onset> findOnsets (const float* samples, int count, double sampleRate)
{
    std::vector<Onset> result;
    if (count <= 0 || sampleRate <= 0)
        return result;

    // Envelope: the peak of each 1 ms block.
    const int block = juce::jmax (1, static_cast<int> (std::lround (sampleRate / 1000)));
    const int blocks = count / block;
    std::vector<float> env (static_cast<size_t> (blocks));
    float overall = 0;
    for (int b = 0; b < blocks; ++b)
    {
        float peak = 0;
        for (int i = 0; i < block; ++i)
            peak = std::max (peak, std::abs (samples[b * block + i]));
        env[static_cast<size_t> (b)] = peak;
        overall = std::max (overall, peak);
    }
    if (overall <= 1e-5f)
        return result;

    // An onset: the envelope is within 30 dB of the loudest hit, has risen by 10 dB over
    // the last 20 ms, and comes at least 60 ms after the previous onset.
    const float threshold = std::max (overall * 0.0316f, 1e-4f);
    const int lookBack = 20, refractory = 60;
    int last = -refractory;
    for (int b = 0; b < blocks; ++b)
    {
        const float e = env[static_cast<size_t> (b)];
        if (e < threshold || b - last < refractory)
            continue;
        float before = b > 0 ? e : 0.0f; // the file starts in silence
        for (int j = std::max (0, b - lookBack); j < b; ++j)
            before = std::min (before, env[static_cast<size_t> (j)]);
        if (e < before * 3.16f)
            continue;

        // Local peak over the next 30 ms, then the first sample above 10 % of it.
        const int from = std::max (0, (b - 2) * block);
        const int to = std::min (count, from + static_cast<int> (0.032 * sampleRate));
        float local = 0;
        for (int i = from; i < to; ++i)
            local = std::max (local, std::abs (samples[i]));
        int attack = from;
        while (attack < to && std::abs (samples[attack]) < local * 0.1f)
            ++attack;

        Onset onset;
        onset.time = attack / sampleRate;
        onset.peakDb = 20 * std::log10 (std::max (local, 1e-6f));
        onset.centroidHz = centroid (samples + attack, count - attack, sampleRate);
        result.push_back (onset);
        last = b;
    }

    return result;
}

std::vector<Span> findSpeech (const float* x, int count, double joinGapSec, double padSec)
{
    constexpr int frame = 160; // 10 ms at 16 kHz
    std::vector<Span> spans;
    const int frames = count / frame;
    if (frames == 0)
        return spans;

    std::vector<double> db (static_cast<size_t> (frames));
    double loudest = -120;
    for (int f = 0; f < frames; ++f)
    {
        double sum = 0;
        for (int i = 0; i < frame; ++i)
            sum += double (x[f * frame + i]) * x[f * frame + i];
        db[static_cast<size_t> (f)] = 10 * std::log10 (sum / frame + 1e-12);
        loudest = std::max (loudest, db[static_cast<size_t> (f)]);
    }

    // Speech: within 40 dB of the loudest frame and above −60 dBFS.
    const double threshold = std::max (loudest - 40, -60.0);
    int start = -1, quiet = 0;
    const int join = static_cast<int> (joinGapSec * 100);
    for (int f = 0; f <= frames; ++f)
    {
        const bool loud = f < frames && db[static_cast<size_t> (f)] > threshold;
        if (loud)
        {
            if (start < 0)
                start = f;
            quiet = 0;
        }
        else if (start >= 0 && (++quiet > join || f == frames))
        {
            const int end = f - quiet + 1;
            if (end - start >= 8) // at least 80 ms
                spans.push_back ({ start / 100.0, end / 100.0 });
            start = -1;
            quiet = 0;
        }
    }

    const double length = count / 16000.0;
    for (auto& s : spans)
    {
        s.start = std::max (0.0, s.start - padSec);
        s.end = std::min (length, s.end + padSec);
    }

    // Long stretches are split so each fits whisper's 30 s window.
    std::vector<Span> fitted;
    for (const auto& s : spans)
    {
        for (double t = s.start; t < s.end; t += 28.0)
            fitted.push_back ({ t, std::min (s.end, t + 28.0) });
    }
    return fitted;
}

int onsets (const char* file)
{
    std::vector<float> mono;
    double rate = 0;
    juce::String problem;
    if (! readMono (file, mono, rate, problem))
        return fail (problem, 2);

    const auto found = findOnsets (mono.data(), static_cast<int> (mono.size()), rate);

    auto* o = new juce::DynamicObject();
    juce::var answer (o);
    o->setProperty ("ok", true);
    o->setProperty ("sampleRate", rate);
    o->setProperty ("durationSec", static_cast<double> (mono.size()) / rate);
    juce::Array<juce::var> list;
    for (const auto& onset : found)
    {
        juce::Array<juce::var> row { number (onset.time), number (onset.peakDb), number (onset.centroidHz) };
        list.add (row);
    }
    o->setProperty ("onsets", list);
    print (answer);
    return 0;
}

#if RECOMPOSE_WITH_WHISPER
namespace
{
    void quietLog (ggml_log_level level, const char* text, void*)
    {
        if (level == GGML_LOG_LEVEL_ERROR)
            std::fputs (text, stderr);
    }

    std::vector<float> to16k (const std::vector<float>& mono, double rate)
    {
        if (std::abs (rate - 16000) < 0.5)
            return mono;
        const int inLength = static_cast<int> (mono.size());
        const int outLength = static_cast<int> (std::llround (inLength * 16000.0 / rate));
        std::vector<double> in (mono.begin(), mono.end());
        std::vector<double> out (static_cast<size_t> (outLength));
        r8b::CDSPResampler24 resampler (rate, 16000, 65536);
        resampler.oneshot (in.data(), inLength, out.data(), outLength);
        return std::vector<float> (out.begin(), out.end());
    }

    std::string trim (const std::string& s)
    {
        const auto a = s.find_first_not_of (" \t\r\n");
        if (a == std::string::npos)
            return {};
        const auto b = s.find_last_not_of (" \t\r\n");
        return s.substr (a, b - a + 1);
    }
} // namespace

int transcribe (int argc, char* argv[])
{
    // argv: transcribe <file> <model> [--prompt text] [--threads N]
    if (argc < 4)
        return fail ("Usage: transcribe <file> <model> [--prompt <text>] [--threads N]", 64);

    const char* file = argv[2];
    const char* model = argv[3];
    std::string prompt;
    int threads = static_cast<int> (std::clamp (std::thread::hardware_concurrency() / 2, 1u, 4u));
    for (int i = 4; i + 1 < argc; i += 2)
    {
        if (std::strcmp (argv[i], "--prompt") == 0)
            prompt = argv[i + 1];
        else if (std::strcmp (argv[i], "--threads") == 0)
            threads = std::max (1, std::atoi (argv[i + 1]));
    }

    std::vector<float> mono;
    double rate = 0;
    juce::String problem;
    if (! readMono (file, mono, rate, problem))
        return fail (problem, 2);

    if (! juce::File (juce::String::fromUTF8 (model)).existsAsFile())
        return fail ("The speech model is missing", 3);

    whisper_log_set (quietLog, nullptr);
    auto contextParams = whisper_context_default_params();
    contextParams.use_gpu = false;
    // Word times from cross-attention alignment (DTW) are far better than the token-level
    // heuristic, which spreads a window's words evenly. The alignment heads are per model.
    const auto modelName = juce::File (juce::String::fromUTF8 (model)).getFileName();
    if (modelName.contains ("base.en"))
    {
        contextParams.flash_attn = false;
        contextParams.dtw_token_timestamps = true;
        contextParams.dtw_aheads_preset = WHISPER_AHEADS_BASE_EN;
    }
    std::unique_ptr<whisper_context, void (*) (whisper_context*)> context (
        whisper_init_from_file_with_params (model, contextParams), whisper_free);
    if (context == nullptr)
        return fail ("The speech model could not be loaded", 3);

    const auto audio = to16k (mono, rate);
    mono.clear();
    mono.shrink_to_fit();
    const auto spans = findSpeech (audio.data(), static_cast<int> (audio.size()));

    juce::Array<juce::var> words;
    const whisper_token eot = whisper_token_eot (context.get());

    // Calls are short, and whisper hears an isolated half-second word poorly and spends a
    // full 30 s encoder pass on it. So the stretches are packed into windows of up to 28 s,
    // one second of silence apart, and each word is mapped back to the stretch it fell in.
    struct Placed
    {
        double windowStart, sourceStart, length;
    };
    struct Window
    {
        std::vector<float> samples;
        std::vector<Placed> placed;
    };
    std::vector<Window> windows;
    constexpr double gap = 1.5, lead = 0.3, maxWindow = 28.0;
    for (const auto& span : spans)
    {
        const double length = span.end - span.start;
        if (windows.empty() || windows.back().samples.size() / 16000.0 + gap + length > maxWindow)
        {
            windows.emplace_back();
            windows.back().samples.assign (static_cast<size_t> (lead * 16000), 0.0f);
        }
        auto& w = windows.back();
        if (! w.placed.empty())
            w.samples.resize (w.samples.size() + static_cast<size_t> (gap * 16000), 0.0f);
        w.placed.push_back ({ w.samples.size() / 16000.0, span.start, length });
        const int from = static_cast<int> (span.start * 16000);
        const int to = std::min (static_cast<int> (audio.size()), static_cast<int> (span.end * 16000));
        w.samples.insert (w.samples.end(), audio.begin() + from, audio.begin() + to);
    }

    for (size_t wi = 0; wi < windows.size(); ++wi)
    {
        auto& window = windows[wi];
        window.samples.resize (std::max<size_t> (window.samples.size() + static_cast<size_t> (lead * 16000), 17600), 0.0f);

        auto params = whisper_full_default_params (WHISPER_SAMPLING_GREEDY);
        params.n_threads = threads;
        params.language = "en";
        params.translate = false;
        params.no_context = true;
        params.single_segment = false;
        params.print_progress = false;
        params.print_realtime = false;
        params.print_special = false;
        params.print_timestamps = false;
        params.token_timestamps = true;
        params.suppress_blank = true;
        params.suppress_nst = true;
        params.temperature = 0.0f;
        params.no_speech_thold = 0.6f;
        params.initial_prompt = prompt.empty() ? nullptr : prompt.c_str();

        if (whisper_full (context.get(), params, window.samples.data(), static_cast<int> (window.samples.size())) != 0)
            continue;

        // Words from the tokens: a token that starts with a space starts a word; the
        // others (word pieces, punctuation) continue it. Times are the tokens' own.
        struct Word
        {
            std::string text;
            double start = 0, end = 0, p = 0, aligned = -1;
            int tokens = 0;
        };
        std::vector<Word> found;
        const int segments = whisper_full_n_segments (context.get());
        for (int s = 0; s < segments; ++s)
        {
            for (int t = 0; t < whisper_full_n_tokens (context.get(), s); ++t)
            {
                const auto data = whisper_full_get_token_data (context.get(), s, t);
                if (data.id >= eot)
                    continue; // special and timestamp tokens
                std::string piece = whisper_token_to_str (context.get(), data.id);
                if (piece.empty())
                    continue;
                const bool startsWord = piece[0] == ' ' || found.empty();
                if (startsWord)
                {
                    Word w;
                    w.start = data.t0 / 100.0;
                    found.push_back (w);
                }
                auto& word = found.back();
                word.text += piece;
                word.end = std::max (word.end, data.t1 / 100.0);
                if (contextParams.dtw_token_timestamps && data.t_dtw >= 0 && word.aligned < 0)
                    word.aligned = data.t_dtw / 100.0;
                word.p += data.p;
                ++word.tokens;
            }
        }

        size_t previous = 0; // words come in order, so their stretches never go back
        for (auto& w : found)
        {
            const auto text = trim (w.text);
            if (text.empty() || text[0] == '[' || text[0] == '(' || text[0] == '*')
                continue; // [BLANK_AUDIO], (music)
            bool hasLetters = false;
            for (const char c : text)
                hasLetters = hasLetters || std::isalnum (static_cast<unsigned char> (c));
            if (! hasLetters)
                continue;

            // The stretch nearest to where the word was aligned (or its token time).
            const double at = w.aligned >= 0 ? w.aligned : (w.start + std::max (w.end, w.start)) / 2;
            size_t homeIndex = previous;
            double best = 1e9;
            for (size_t i = previous; i < window.placed.size(); ++i)
            {
                const auto& placed = window.placed[i];
                const double distance = at < placed.windowStart ? placed.windowStart - at
                                      : at > placed.windowStart + placed.length ? at - placed.windowStart - placed.length
                                      : 0.0;
                if (distance < best)
                {
                    best = distance;
                    homeIndex = i;
                }
            }
            previous = homeIndex;
            const Placed* home = &window.placed[homeIndex];

            // Inside the stretch, the token times are good enough for bar-level starts;
            // a word that seems to start outside its stretch starts at its aligned time.
            const double offset = home->sourceStart - home->windowStart;
            double wordStart = w.start, wordEnd = std::max (w.end, w.start + 0.05);
            if (w.aligned >= 0 && (wordStart < home->windowStart - 0.1 || wordStart > home->windowStart + home->length))
            {
                wordStart = std::max (home->windowStart, w.aligned - 0.15);
                wordEnd = std::min (home->windowStart + home->length, w.aligned + 0.3);
            }
            const double start = std::clamp (wordStart + offset, home->sourceStart, home->sourceStart + home->length);
            const double end = std::clamp (wordEnd + offset, start, home->sourceStart + home->length);
            juce::Array<juce::var> row { juce::String::fromUTF8 (text.c_str()), number (start), number (end),
                                         number (w.tokens > 0 ? w.p / w.tokens : 0.0) };
            words.add (row);
        }

        std::fprintf (stderr, "progress %.3f\n", static_cast<double> (wi + 1) / static_cast<double> (windows.size()));
        std::fflush (stderr);
    }

    auto* o = new juce::DynamicObject();
    juce::var answer (o);
    o->setProperty ("ok", true);
    o->setProperty ("model", juce::File (juce::String::fromUTF8 (model)).getFileNameWithoutExtension());
    o->setProperty ("chunks", static_cast<int> (spans.size()));
    o->setProperty ("words", words);
    print (answer);
    return 0;
}

// The voice worker (VC-10): the model stays loaded; each stdin line {id, prompt, samples} (16 kHz 16-bit PCM in base64)
// gets one stdout line {id, text, confidence}. It exits when stdin closes.
int listen (int argc, char* argv[])
{
    const char* model = argv[2];
    int threads = std::clamp (static_cast<int> (std::thread::hardware_concurrency()) / 2, 1, 4);
    for (int i = 3; i + 1 < argc; i += 2)
        if (std::strcmp (argv[i], "--threads") == 0)
            threads = std::max (1, std::atoi (argv[i + 1]));

    if (! juce::File (juce::String::fromUTF8 (model)).existsAsFile())
        return fail ("The speech model is missing", 3);
    whisper_log_set (quietLog, nullptr);
    auto contextParams = whisper_context_default_params();
    contextParams.use_gpu = false;
    std::unique_ptr<whisper_context, void (*) (whisper_context*)> context (
        whisper_init_from_file_with_params (model, contextParams), whisper_free);
    if (context == nullptr)
        return fail ("The speech model could not be loaded", 3);
    const whisper_token eot = whisper_token_eot (context.get());
    {
        auto* ready = new juce::DynamicObject();
        juce::var answer (ready);
        ready->setProperty ("ok", true);
        print (answer);
        std::fflush (stdout);
    }

    std::string line;
    int c;
    while (true)
    {
        line.clear();
        while ((c = std::getchar()) != EOF && c != '\n')
            line.push_back (static_cast<char> (c));
        if (c == EOF && line.empty())
            return 0;

        const auto request = juce::JSON::parse (juce::String::fromUTF8 (line.data(), static_cast<int> (line.size())));
        juce::MemoryOutputStream decoded;
        juce::Base64::convertFromBase64 (decoded, request.getProperty ("samples", {}).toString());
        std::vector<float> samples (decoded.getDataSize() / 2);
        const auto* pcm = static_cast<const char*> (decoded.getData());
        for (std::size_t i = 0; i < samples.size(); ++i)
            samples[i] = static_cast<float> (static_cast<juce::int16> (juce::ByteOrder::littleEndianShort (pcm + 2 * i))) / 32768.0f;
        samples.resize (std::max<std::size_t> (samples.size() + 4800, 17600), 0.0f); // whisper wants over a second

        const auto prompt = request.getProperty ("prompt", {}).toString().toStdString();
        auto params = whisper_full_default_params (WHISPER_SAMPLING_GREEDY);
        params.n_threads = threads;
        params.language = "en";
        params.no_context = true;
        params.single_segment = true;
        params.print_progress = params.print_realtime = params.print_special = params.print_timestamps = false;
        params.suppress_blank = true;
        params.suppress_nst = true;
        params.temperature = 0.0f;
        params.initial_prompt = prompt.empty() ? nullptr : prompt.c_str();

        std::string text;
        double p = 0;
        int tokens = 0;
        if (whisper_full (context.get(), params, samples.data(), static_cast<int> (samples.size())) == 0)
        {
            for (int s = 0; s < whisper_full_n_segments (context.get()); ++s)
            {
                text += whisper_full_get_segment_text (context.get(), s);
                for (int t = 0; t < whisper_full_n_tokens (context.get(), s); ++t)
                {
                    const auto data = whisper_full_get_token_data (context.get(), s, t);
                    if (data.id < eot)
                    {
                        p += data.p;
                        ++tokens;
                    }
                }
            }
        }
        const auto heard = trim (text);
        auto* o = new juce::DynamicObject();
        juce::var answer (o);
        o->setProperty ("id", request.getProperty ("id", 0));
        o->setProperty ("text", heard.empty() || heard[0] == '[' || heard[0] == '(' ? juce::String() : juce::String::fromUTF8 (heard.c_str()));
        o->setProperty ("confidence", tokens > 0 ? p / tokens : 0.0);
        print (answer);
        std::fflush (stdout);
    }
}
#else
int transcribe (int, char*[])
{
    return fail ("This engine was built without speech recognition", 3);
}

int listen (int, char*[])
{
    return fail ("This engine was built without speech recognition", 3);
}
#endif
} // namespace rec::analysis
