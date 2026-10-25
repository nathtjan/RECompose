// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Pads (SL-40…46, PB-72): one file per pitch class, played independently of the song transport,
// so a pad keeps sounding across loads, stops and transitions. A key press fades that key in and
// any other out (SL-42); with Follow song the pad takes the key heard in the plan, key lifts
// included (SL-44); pad bars in the plan start and end a pad (ARR-39).
//
// The disk thread reads only the keys that sound, looping each between its loop points with an
// equal-power crossfade (SL-46). The audio thread mixes them with their fades (PB-3: no allocation,
// locks or I/O there); commands arrive through a FIFO like the song player's.
#pragma once

#include "audio/song.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <memory>
#include <string>

namespace rec::audio
{
inline constexpr int kPadKeys = 12;

// One pitch class of a pad set.
struct PadKey
{
    std::unique_ptr<Track> track;  // the file, its reader, resamplers and block queue
    std::int64_t loopStart = 0;    // engine-rate frames
    std::int64_t loopEnd = 0;
    std::int64_t crossfade = 0;

    // Set by the audio thread: the disk thread fills this key while it sounds. Bumping the
    // generation restarts it from the top of the file.
    std::atomic<bool> wanted { false };
    std::atomic<std::uint32_t> generation { 0 };

    // Disk thread only.
    std::uint32_t diskGeneration = 0xffffffffu;
    std::int64_t diskPos = 0;
    bool inCrossfade = false;
};

struct PadBank
{
    std::string id;
    double sampleRate = 48000.0;
    std::array<PadKey, kPadKeys> keys;
};

// Opens the files of a pad set (message thread). Missing files make silent keys.
// `files` is indexed by pitch class (0 = C); an empty name leaves that key silent.
std::unique_ptr<PadBank> loadPadBank (std::string id, const std::array<std::string, kPadKeys>& files, double engineRate,
                                      juce::AudioFormatManager& formats);

// Disk thread: keeps the sounding keys' queues filled. Returns the blocks written.
int fillPads (PadBank& bank, int maxBlocks);

struct PadCommand
{
    enum class Type
    {
        setBank,   // a new pad set; a sounding key crossfades to the same key of the new set (SL-41)
        play,      // `key` fades in, every other key fades out (SL-42); key < 0 = stop
        setGain,   // the pad volume, linear (SL-45)
        setFade,   // the fade time in frames (SL-45)
        setFollow, // follow the key heard in the plan (SL-44)
        setOutput, // device channel and width of the Pad output (PB-85)
    };

    Type type = Type::play;
    int key = -1;
    float value = 0.0f;
    std::int64_t frames = 0;
    int channel = 0;
    int width = 2;
    PadBank* bank = nullptr;
};

class PadPlayer
{
public:
    PadPlayer();

    bool post (const PadCommand& command) noexcept; // message thread
    void prepare (double sampleRate);               // message thread, before process()

    // Audio thread: adds the pads to the outputs.
    void process (float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept;

    // Audio thread, from the song player at each segment it enters: Follow song and pad bars.
    void onSegment (const PlanSegment& segment) noexcept;

    // Message thread: a bank the audio side no longer uses, or nullptr.
    PadBank* takeRetiredBank() noexcept;

    // Snapshot values (any thread): the key that sounds (-1 for none) and its level 0…1.
    int key() const noexcept { return key_.load (std::memory_order_relaxed); }
    float level() const noexcept { return level_.load (std::memory_order_relaxed); }

private:
    struct Voice
    {
        float gain = 0.0f;
        float target = 0.0f;
        float step = 0.0f;
        std::uint32_t readGeneration = 0;
        int offset = 0; // frames of the front block already played
    };

    void drain() noexcept;
    void handle (const PadCommand& command) noexcept;
    void start (int bankIndex, int key) noexcept;
    void fadeTo (Voice& voice, float target) noexcept;
    void retire (PadBank* bank) noexcept;
    int pull (PadKey& key, Voice& voice, float* left, float* right, int frames, bool offline) noexcept;

    static constexpr int kFifoSize = 64;
    std::array<PadCommand, kFifoSize> commands_ {};
    std::atomic<int> read_ { 0 }, write_ { 0 };
    std::array<PadBank*, 8> retired_ {};
    std::atomic<int> retiredRead_ { 0 }, retiredWrite_ { 0 };

    // Audio thread only. Bank 0 sounds; bank 1 is the previous set fading out after a change.
    std::array<PadBank*, 2> banks_ { nullptr, nullptr };
    std::array<std::array<Voice, kPadKeys>, 2> voices_ {};
    int playing_ = -1;        // the key asked for, -1 = none
    bool fromPadBars_ = false; // the pad sounds because pad bars started it
    bool follow_ = false;
    float gain_ = 0.5011872f;  // −6 dB (SL-45)
    std::int64_t fadeFrames_ = 4 * 48000;
    int channel_ = 0;
    int width_ = 2;
    double sampleRate_ = 48000.0;
    std::vector<float> left_, right_;

    std::atomic<int> key_ { -1 };
    std::atomic<float> level_ { 0.0f };
};
} // namespace rec::audio
