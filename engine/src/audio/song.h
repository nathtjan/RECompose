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
    std::unique_ptr<r8b::CDSPResampler24> resamplers[kMaxTrackChannels];
    std::uint32_t diskGeneration = 0xffffffffu;
    std::int64_t diskNextFrame = 0;   // song frame of the next block to write
    std::int64_t diskSourcePos = 0;   // next source frame to read
    std::vector<double> pending[kMaxTrackChannels]; // resampled frames not yet in a block
    std::vector<float> readBuffer[kMaxTrackChannels];
    std::vector<double> resampleInput;

    // Audio thread only.
    float appliedGain = 0.0f;
};

struct Song
{
    std::string id;
    double sampleRate = 48000.0; // engine rate: the device rate the streams are resampled to
    std::int64_t lengthFrames = 0;
    std::vector<std::unique_ptr<Track>> tracks;

    std::atomic<std::uint64_t> request { StreamRequest::pack ({ 1, 0 }) };
    std::atomic<float> masterGain { 1.0f };
    std::atomic<float> masterPeak[2] { 0.0f, 0.0f };

    // Bumps the generation and sets the frame the streams should start from. Safe from
    // any thread (message thread for seeks, audio thread for the rewind at the end).
    StreamRequest requestFrame (std::int64_t frame) noexcept;
    StreamRequest currentRequest() const noexcept { return StreamRequest::unpack (request.load()); }

    // True when every playable track has its first block at the current request.
    bool isBuffered() const noexcept;
};

// Opens every file and prepares the streams. Missing or unreadable files become silent
// tracks with a problem text; the rest play (REL-50).
std::unique_ptr<Song> loadSong (std::string id, std::vector<TrackSpec> specs, double engineRate,
                                std::int64_t startFrame, juce::AudioFormatManager& formats);

float decibelsToGain (float db) noexcept;
} // namespace rec::audio
