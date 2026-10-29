// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The talkback mic (VC-10): the audio callback writes the voice channel into a lock-free ring, a thread of its own
// resamples it to 16 kHz and finds utterances with an energy VAD. Recognition is not done here (REL-50): each
// utterance goes to the app, which hands it to the `listen` worker.
#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>

namespace rec::audio
{
// Energy VAD on 16 kHz mono in 20 ms frames: speech is a frame 10 dB above the noise floor (and above −50 dBFS);
// an utterance starts after 60 ms of speech with 300 ms before it, ends after 500 ms without, and is cut at 6 s.
class VoiceActivity
{
public:
    static constexpr int kRate = 16000;
    static constexpr int kFrame = 320;
    static constexpr int kMaxSamples = 6 * kRate;

    // An utterance: its samples and how many trailing samples were silence.
    using Sink = std::function<void (std::vector<float>&& samples, int trailingSilence)>;

    explicit VoiceActivity (Sink sink) : sink_ (std::move (sink)) {}

    void feed (const float* samples, int count);
    bool hearing() const noexcept { return speaking_; }
    void reset();

private:
    void frame (const float* x);
    void finish();

    Sink sink_;
    std::vector<float> pending_;   // part of a frame
    std::vector<float> history_;   // the last 300 ms, for the start of an utterance
    std::vector<float> utterance_;
    double floorDb_ = -60.0;
    int speechRun_ = 0, silenceRun_ = 0, speechFrames_ = 0;
    bool speaking_ = false;
};

class VoiceInput
{
public:
    // On the VAD thread: an utterance at 16 kHz, its length and how much of its end is silence, in seconds.
    using Sink = std::function<void (std::vector<float>&& samples16k, double durationSec, double trailingSec)>;

    explicit VoiceInput (Sink sink);
    ~VoiceInput();

    // Message thread, the device stopped or not yet calling: the input rate.
    void prepare (double rate);

    // Audio thread: real-time safe (a copy into the ring and a peak).
    void write (const float* samples, int count) noexcept;

    // Any thread: 16 kHz audio straight to the VAD, for tests (injectVoice).
    void inject (std::vector<float> samples16k);

    // The peak since the last call, in dBFS, and whether speech is going on (VC-5).
    float takeLevelDb() noexcept;
    bool hearing() const noexcept { return hearing_.load(); }

private:
    void run();

    static constexpr std::size_t kRing = 1 << 17; // 2.7 s at 48 kHz
    std::vector<float> ring_;
    std::atomic<std::size_t> head_ { 0 }, tail_ { 0 };
    std::atomic<float> peak_ { 0.0f };
    std::atomic<double> rate_ { 48000.0 };
    std::atomic<bool> hearing_ { false }, stop_ { false }, reset_ { false };

    std::mutex injectMutex_;
    std::vector<float> injected_;

    Sink sink_;
    VoiceActivity vad_;
    double phase_ = 0.0;
    float last_ = 0.0f;
    std::thread thread_;
};
} // namespace rec::audio
