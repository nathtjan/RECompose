// SPDX-License-Identifier: AGPL-3.0-or-later
//
// A loaded song: its tracks, their streams and their mixer settings. Built on the
// message thread, read by the disk thread (streams) and the audio thread (mixing).
// Mixer settings are atomics written by the message thread; everything the audio thread
// changes lives in the "audio thread only" fields.
#pragma once

#include "audio/block_queue.h"
#include "audio/clip_player.h"
#include "audio/stretcher.h"

#include <juce_audio_formats/juce_audio_formats.h>

#include <atomic>
#include <cmath>
#include <map>
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

// A stem rendered at a key and a tempo (protocol 11, PB-5, REL-10): the source shifted by `semitones` and
// stretched to play at `tempo`, so frame f of the file is source frame f × tempo. Rendered offline by the
// `render` tool; the engine plays it straight instead of processing the stem live.
struct RenderSpec
{
    std::string file;
    int semitones = 0;
    double tempo = 1.0;
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
    std::string generated; // "click" (PB-70) or "speech" (the plan's calls, protocol 13): no file, the engine renders it
    bool transposes = false; // follows the segment's key (DM-12: music yes, click and guide no)
    std::vector<RenderSpec> renders; // protocol 11: played instead of live key and tempo where they match
    double offsetSec = 0.0;  // protocol 14: the file starts this long after the song start (negative: its first seconds are skipped)
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
    int key = 0;                     // semitones for tracks that transpose: entry key + lift (PB-41, PB-58)
    int songKey = -1;                // the pitch class heard, for pads that follow the song (SL-44); -1 unknown
    int pad = -1;                    // pad bars: the pitch class of the pad (ARR-39); -1 none
};

// Where the plan clock waits for GO (PB-52): the playhead stops at `frame` until released.
struct PlanHold
{
    std::int64_t frame = 0;
    bool pad = false;   // the pad sounds while it waits
    int padKey = -1;    // its pitch class, -1 none
    bool click = false; // a free-running click at the song tempo
};

// A MIDI message the plan sends when the playhead passes `frame` while playing (protocol 13, AUTO-5, AUTO-21).
// A message of size 0 sends nothing and is only reported back (a log line). `id` is the app's, 0 for none.
struct PlanMidi
{
    std::int64_t frame = 0;
    std::uint8_t bytes[3] {};
    std::uint8_t size = 0;
    std::int32_t id = 0;
};

// A spoken call (protocol 13, AUTO-30, AUTO-31, PB-71): a clip the song owns, rendered into the speech track from `frame`.
struct PlanCall
{
    std::int64_t frame = 0;
    const Clip* clip = nullptr;
    float gain = 1.0f;
};

struct Plan
{
    std::vector<PlanSegment> segments;
    std::vector<PlanHold> holds; // ascending frames
    std::vector<PlanMidi> midi;  // ascending frames
    std::vector<PlanCall> calls; // ascending frames

    // Every published plan has a version. A plan published as a swap (protocol 8) replaces `previous`
    // from `swapFrame` on: the frames before it are identical, so playback does not stop.
    std::uint32_t version = 0;
    std::int64_t swapFrame = -1;
    const Plan* previous = nullptr;
    std::int64_t lengthFrames = 0;

    // The song grid in plan frames (PB-32): beat 0 is bar 1 beat 1, earlier beats are negative.
    double gridOrigin = 0.0;
    double beatFrames = 0.0;
    int beatsPerBar = 4;

    // Source frames per output frame (tf, PB-30). The engine's frames are output frames: segment
    // times and the grid are divided by it, sources advance by it (protocol 5).
    double tempo = 1.0;
};

// A read position in one stem: resampler state for the engine rate, kept across contiguous
// segments so a plan that plays on straight through has no seam. A stretched stream also runs
// its stem through a Stretcher for key and tempo (PB-58, PB-60); a bypassed one is untouched.
struct SourceStream
{
    std::unique_ptr<r8b::CDSPResampler24> resamplers[kMaxTrackChannels];
    std::int64_t pos = 0;         // next source frame to read (at the file's rate)
    std::int64_t nextEngine = 0;  // engine-rate source frame the reader delivers next
    bool valid = false;
    std::vector<double> pending[kMaxTrackChannels]; // resampled frames not yet used

    double nextSource = 0.0;      // engine-rate source frame of the next output frame
    int render = -1;              // which of the track's renders it reads, -1 the stem itself
    bool stretched = false;
    int semitones = 0;
    double inputOwed = 0.0;       // fractional input frames carried between blocks (tempo)
    std::unique_ptr<Stretcher> stretcher; // made on first use, on the disk thread

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
    std::int64_t offsetFrames = 0; // spec.offsetSec at the engine rate: source frame f of the song is file frame f - offsetFrames
    bool missing = false;      // the file could not be opened (REL-50): the track is silent
    bool generatedClick = false; // synthesized from the plan's grid instead of read from a file
    bool generatedSpeech = false; // the plan's spoken calls (protocol 13)
    juce::String problem;

    BlockQueue queue;

    // Mixer settings: written by the message thread, read by the audio thread.
    std::atomic<float> gain { 1.0f }; // linear
    std::atomic<bool> transposes { false }; // read by the disk thread (PB-80 "#")
    std::atomic<bool> mute { false };
    std::atomic<bool> solo { false };
    std::atomic<int> outputChannel { 0 };
    std::atomic<int> outputWidth { 2 };

    // Meter: the audio thread keeps the max; the snapshot publisher takes and resets it.
    std::atomic<float> peak { 0.0f };

    // Set by the disk thread once it has queued a block for this request generation.
    std::atomic<std::uint32_t> bufferedGeneration { 0xffffffffu };

    // Renders that opened (fixed after load); a render that is missing or at another rate is left out
    // and the stem is processed live instead (REL-14).
    struct Render
    {
        std::unique_ptr<juce::AudioFormatReader> reader;
        int semitones = 0;
        double tempo = 1.0;
    };
    std::vector<Render> renders;

    // The render for this key and tempo, or -1.
    int findRender (int semitones, double tempo) const noexcept
    {
        for (std::size_t i = 0; i < renders.size(); ++i)
            if (renders[i].semitones == semitones && std::abs (renders[i].tempo - tempo) < 1.0e-6)
                return static_cast<int> (i);
        return -1;
    }

    // Disk thread only.
    std::unique_ptr<juce::AudioFormatReader> reader;
    bool resampling = false;
    std::uint32_t diskGeneration = 0xffffffffu;
    std::uint32_t diskVersion = 0xffffffffu; // the plan version of the blocks being written
    const Plan* diskPlan = nullptr;          // the plan the streams currently follow
    std::int64_t diskNextFrame = 0;   // plan frame of the next block to write
    std::int64_t diskLastEnd = -1;    // plan frame where the previous block ended, -1 after a seek
    std::size_t diskSegment = 0;
    SourceStream streams[2];          // the playing stream and, briefly, the one it replaced
    int mainStream = 0;
    int tailRemaining = 0;            // frames of the outgoing stream still to blend in (PB-55)
    int tailLength = 1;               // the whole blend: 10 ms at a splice, 50 ms at a key change
    int fadeInRemaining = 0;          // frames of the incoming fade-in still to apply
    int fadeInLength = 1;
    std::vector<float> stretchInput[kMaxTrackChannels];
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
    std::uint32_t lastVersion = 0;            // message thread only

    // The clips the plans' calls play, by file, range and rate (message thread). Kept with the plans for the song's life.
    std::map<std::string, std::unique_ptr<Clip>> clips;

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

    // Message thread: publishes `next` as a swap at `swapFrame` (output frames) without moving the stream
    // request. The disk thread rewinds to the frame and writes the new plan's blocks from there.
    const Plan* publishSwap (std::unique_ptr<Plan> next, std::int64_t swapFrame);
    std::int64_t planFrames() const noexcept
    {
        const auto* p = plan.load (std::memory_order_acquire);
        return p != nullptr ? p->lengthFrames : lengthFrames;
    }

    // True when every playable track has its first block at the current request.
    bool isBuffered() const noexcept;
};

// Opens a track's file and prepares its streams for the engine rate. Returns its length in engine
// frames; a missing or unreadable file makes a silent track with a problem text (REL-50).
std::int64_t openTrackFile (Track& track, double engineRate, juce::AudioFormatManager& formats);

// Opens every file and prepares the streams. Missing or unreadable files become silent
// tracks with a problem text; the rest play (REL-50).
std::unique_ptr<Song> loadSong (std::string id, std::vector<TrackSpec> specs, double engineRate,
                                std::int64_t startFrame, juce::AudioFormatManager& formats);

float decibelsToGain (float db) noexcept;

// One segment [0, length) that reads the stems from the start: how a song plays until the app
// sends a plan, and what the M1 tests exercise.
std::unique_ptr<Plan> identityPlan (std::int64_t lengthFrames);

// The blend lengths of a seam where the source jumps (PB-55), and of a key change on a contiguous
// boundary (PB-58: the outgoing and the incoming key crossfade).
inline constexpr int kSpliceOutMs = 10; // milliseconds
inline constexpr int kSpliceInMs = 2;
inline constexpr int kKeyChangeMs = 50;
} // namespace rec::audio
