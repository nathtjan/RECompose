// SPDX-License-Identifier: AGPL-3.0-or-later
//
// A loaded song: its tracks, their streams and their mixer settings. Built on the
// message thread, read by the disk thread (streams) and the audio thread (mixing).
// Mixer settings are atomics written by the message thread; everything the audio thread
// changes lives in the "audio thread only" fields.
#pragma once

#include "audio/block_queue.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <atomic>
#include <memory>
#include <string>
#include <vector>

namespace r8b
{
class CDSPResampler24;
}

namespace rec::audio
{
// Where the next blocks should start. Packed into one atomic so readers always see a
// matching generation and frame: generation in the top 16 bits, frame in the low 48.
struct StreamRequest
{
    std::uint32_t generation = 0;
    std::int64_t frame = 0;

    static std::uint64_t pack (StreamRequest r) noexcept
    {
        return (static_cast<std::uint64_t> (r.generation & 0xffffu) << 48)
             | (static_cast<std::uint64_t> (r.frame) & 0xffffffffffffull);
    }

    static StreamRequest unpack (std::uint64_t v) noexcept
    {
        return { static_cast<std::uint32_t> (v >> 48), static_cast<std::int64_t> (v & 0xffffffffffffull) };
    }
};

struct TrackSpec
{
    std::string id;
    std::string file;
    int outputChannel = 0; // first device channel, 0-based
    int outputWidth = 2;   // 1 = mono channel, 2 = channel pair
    float gainDb = 0.0f;
    bool mute = false;
    bool solo = false;
    bool soloSafe = false; // click, guide, spoken guide keep playing when others solo (PB-82)
    std::string generated; // "click": no file, the engine synthesizes it (PB-70)
};

// The arrangement as the engine executes it (PB-40): contiguous segments in plan frames, each
// reading the stems from `src` (an engine-rate source frame) or playing silence (src < 0).
// Immutable once published; a Song keeps every plan it was given until it is destroyed, so the
// audio and disk threads can hold a pointer without a lock.
struct PlanSegment
{
    std::int64_t start = 0;
    std::int64_t end = 0;
    std::int64_t src = -1;
    std::int64_t fadeIn = 0;  // frames
    std::int64_t fadeOut = 0; // frames
    std::vector<std::uint8_t> muted; // per track index, 1 = silenced in this segment
    bool click = false;              // the click generator sounds on the beats in this segment (PB-70)
};

struct Plan
{
    std::vector<PlanSegment> segments;
    std::int64_t lengthFrames = 0;

    // The song grid in plan frames (PB-32): beat 0 is bar 1 beat 1, earlier beats are negative.
    double gridOrigin = 0.0;
    double beatFrames = 0.0;
    int beatsPerBar = 4;
};

// A read position in one stem: resampler state for the engine rate, kept across contiguous
// segments so a plan that plays on straight through has no seam.
struct SourceStream
{
    std::unique_ptr<r8b::CDSPResampler24> resamplers[kMaxTrackChannels];
    std::int64_t pos = 0;         // next source frame to read (at the file's rate)
    std::int64_t nextEngine = 0;  // engine-rate source frame of the next output frame
    bool valid = false;
    std::vector<double> pending[kMaxTrackChannels]; // resampled frames not yet used

    SourceStream();
    ~SourceStream();
    void clear();
};

struct Track
{
    Track (TrackSpec s, std::size_t queueCapacity);
    ~Track();

    TrackSpec spec;

    // Fixed after load.
    int channels = 0;           // 1 or 2 (extra source channels are ignored)
    double sourceRate = 0.0;
    std::int64_t sourceFrames = 0;
    bool missing = false;       // the file could not be opened (REL-50): the track is silent
    bool generatedClick = false; // synthesized from the plan's grid instead of read from a file
    juce::String problem;

    BlockQueue queue;

    // Mixer settings: written by the message thread, read by the audio thread.
    std::atomic<float> gain { 1.0f }; // linear
    std::atomic<bool> mute { false };
    std::atomic<bool> solo { false };
    std::atomic<int> outputChannel { 0 };
    std::atomic<int> outputWidth { 2 };

    // Meter: the audio thread keeps the max; the snapshot publisher takes and resets it.
    std::atomic<float> peak { 0.0f };

    // Set by the disk thread once it has queued a block for this request generation.
    std::atomic<std::uint32_t> bufferedGeneration { 0xffffffffu };

    // Disk thread only.
    std::unique_ptr<juce::AudioFormatReader> reader;
    bool resampling = false;
    std::uint32_t diskGeneration = 0xffffffffu;
    std::int64_t diskNextFrame = 0;   // plan frame of the next block to write
    std::int64_t diskLastEnd = -1;    // plan frame where the previous block ended, -1 after a seek
    std::size_t diskSegment = 0;
    SourceStream streams[2];          // the playing stream and, briefly, the one it replaced
    int mainStream = 0;
    int tailRemaining = 0;            // frames of the outgoing stream still to blend in (PB-55)
    int fadeInRemaining = 0;          // frames of the incoming fade-in still to apply
    std::vector<float> readBuffer[kMaxTrackChannels];
    std::vector<float> mainBuffer[kMaxTrackChannels];
    std::vector<float> tailBuffer[kMaxTrackChannels];
    std::vector<double> resampleInput;

    // Audio thread only.
    float appliedGain = 0.0f;
    float planGain = 1.0f; // the per-segment mute, ramped over 10 ms (AT-12)
};

struct Song
{
    std::string id;
    double sampleRate = 48000.0; // engine rate: the device rate the streams are resampled to
    std::int64_t lengthFrames = 0;
    std::vector<std::unique_ptr<Track>> tracks;

    // The plan being played. Published before the stream request is bumped, so a thread that
    // sees the new request also sees the new plan. Never freed before the song (see PlanSegment).
    std::atomic<const Plan*> plan { nullptr };
    std::vector<std::unique_ptr<Plan>> plans; // message thread only

    std::atomic<std::uint64_t> request { StreamRequest::pack ({ 1, 0 }) };
    std::atomic<float> masterGain { 1.0f };
    std::atomic<float> masterPeak[2] { 0.0f, 0.0f };

    // Bumps the generation and sets the frame the streams should start from. Safe from
    // any thread (message thread for seeks, audio thread for the rewind at the end).
    StreamRequest requestFrame (std::int64_t frame) noexcept;
    StreamRequest currentRequest() const noexcept { return StreamRequest::unpack (request.load()); }

    // Message thread: makes `next` the current plan and returns it. The caller then bumps the
    // stream request (requestFrame) so both threads start from the new plan.
    const Plan* publishPlan (std::unique_ptr<Plan> next);
    std::int64_t planFrames() const noexcept
    {
        const auto* p = plan.load (std::memory_order_acquire);
        return p != nullptr ? p->lengthFrames : lengthFrames;
    }

    // True when every playable track has its first block at the current request.
    bool isBuffered() const noexcept;
};

// Opens every file and prepares the streams. Missing or unreadable files become silent
// tracks with a problem text; the rest play (REL-50).
std::unique_ptr<Song> loadSong (std::string id, std::vector<TrackSpec> specs, double engineRate,
                                std::int64_t startFrame, juce::AudioFormatManager& formats);

float decibelsToGain (float db) noexcept;

// One segment [0, length) that reads the stems from the start: how a song plays until the app
// sends a plan, and what the M1 tests exercise.
std::unique_ptr<Plan> identityPlan (std::int64_t lengthFrames);

// The blend lengths of a seam where the source jumps (PB-55).
inline constexpr int kSpliceOutMs = 10; // milliseconds
inline constexpr int kSpliceInMs = 2;
} // namespace rec::audio
