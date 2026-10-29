// SPDX-License-Identifier: AGPL-3.0-or-later
#include "voice_input.h"

#include <algorithm>
#include <chrono>
#include <cmath>

namespace rec::audio
{
namespace
{
constexpr int kStartFrames = 3, kEndFrames = 25, kHistoryFrames = 15, kMinSpeechFrames = 8;
}

void VoiceActivity::reset()
{
    pending_.clear();
    history_.clear();
    utterance_.clear();
    floorDb_ = -60.0;
    speechRun_ = silenceRun_ = speechFrames_ = 0;
    speaking_ = false;
}

void VoiceActivity::feed (const float* samples, int count)
{
    for (int i = 0; i < count; ++i)
    {
        pending_.push_back (samples[i]);
        if (static_cast<int> (pending_.size()) == kFrame)
        {
            frame (pending_.data());
            pending_.clear();
        }
    }
}

void VoiceActivity::frame (const float* x)
{
    double sum = 0.0;
    for (int i = 0; i < kFrame; ++i)
        sum += static_cast<double> (x[i]) * x[i];
    const double db = 10.0 * std::log10 (sum / kFrame + 1e-12);
    const bool speech = db > std::max (floorDb_ + 10.0, -50.0);

    if (! speaking_)
    {
        // The floor falls at once and rises slowly, so steady noise (a fan, the band through the mic) is not speech.
        floorDb_ = db < floorDb_ ? db : floorDb_ + 0.05;
        history_.insert (history_.end(), x, x + kFrame);
        if (static_cast<int> (history_.size()) > kHistoryFrames * kFrame)
            history_.erase (history_.begin(), history_.begin() + kFrame);
        speechRun_ = speech ? speechRun_ + 1 : 0;
        if (speechRun_ >= kStartFrames)
        {
            speaking_ = true;
            utterance_ = history_;
            history_.clear();
            speechFrames_ = speechRun_;
            silenceRun_ = 0;
        }
        return;
    }

    utterance_.insert (utterance_.end(), x, x + kFrame);
    speechFrames_ += speech ? 1 : 0;
    silenceRun_ = speech ? 0 : silenceRun_ + 1;
    if (silenceRun_ >= kEndFrames || static_cast<int> (utterance_.size()) >= kMaxSamples)
        finish();
}

void VoiceActivity::finish()
{
    const int trailing = silenceRun_ * kFrame;
    speaking_ = false;
    speechRun_ = silenceRun_ = 0;
    if (speechFrames_ >= kMinSpeechFrames && sink_) // under 160 ms of speech is a knock, not a word
        sink_ (std::move (utterance_), trailing);
    utterance_.clear();
    speechFrames_ = 0;
}

//==============================================================================
VoiceInput::VoiceInput (Sink sink)
    : ring_ (kRing, 0.0f),
      sink_ (std::move (sink)),
      vad_ ([this] (std::vector<float>&& samples, int trailing) {
          const auto n = static_cast<double> (samples.size());
          sink_ (std::move (samples), n / VoiceActivity::kRate, trailing / static_cast<double> (VoiceActivity::kRate));
      })
{
    thread_ = std::thread ([this] { run(); });
}

VoiceInput::~VoiceInput()
{
    stop_.store (true);
    thread_.join();
}

void VoiceInput::prepare (double rate)
{
    rate_.store (rate > 0.0 ? rate : 48000.0);
    reset_.store (true);
}

void VoiceInput::write (const float* samples, int count) noexcept
{
    auto head = head_.load (std::memory_order_relaxed);
    const auto tail = tail_.load (std::memory_order_acquire);
    float peak = peak_.load (std::memory_order_relaxed);
    for (int i = 0; i < count; ++i)
    {
        peak = std::max (peak, std::abs (samples[i]));
        if (head - tail >= kRing)
            break; // the VAD thread is behind: drop the rest rather than wait
        ring_[head % kRing] = samples[i];
        ++head;
    }
    peak_.store (peak, std::memory_order_relaxed);
    head_.store (head, std::memory_order_release);
}

void VoiceInput::inject (std::vector<float> samples16k)
{
    const std::lock_guard lock (injectMutex_);
    injected_.insert (injected_.end(), samples16k.begin(), samples16k.end());
}

float VoiceInput::takeLevelDb() noexcept
{
    const auto peak = peak_.exchange (0.0f);
    return peak > 1e-5f ? 20.0f * std::log10 (peak) : -100.0f;
}

void VoiceInput::run()
{
    std::vector<float> block, out;
    block.reserve (4096);
    out.reserve (4096);
    while (! stop_.load())
    {
        if (reset_.exchange (false))
        {
            vad_.reset();
            phase_ = 0.0;
            tail_.store (head_.load());
        }

        // shortcut: linear interpolation without an anti-alias filter; good enough for speech recognition, add a
        // low-pass if recognition suffers on 44.1/48 kHz inputs.
        const auto head = head_.load (std::memory_order_acquire);
        auto tail = tail_.load (std::memory_order_relaxed);
        const double step = rate_.load() / VoiceActivity::kRate;
        out.clear();
        while (tail < head)
        {
            const float x = ring_[tail % kRing];
            ++tail;
            while (phase_ < 1.0)
            {
                out.push_back (last_ + static_cast<float> (phase_) * (x - last_));
                phase_ += step;
            }
            phase_ -= 1.0;
            last_ = x;
        }
        tail_.store (tail, std::memory_order_release);

        {
            const std::lock_guard lock (injectMutex_);
            out.insert (out.end(), injected_.begin(), injected_.end());
            injected_.clear();
        }

        if (! out.empty())
            vad_.feed (out.data(), static_cast<int> (out.size()));
        hearing_.store (vad_.hearing());
        std::this_thread::sleep_for (std::chrono::milliseconds (10));
    }
}
} // namespace rec::audio
