// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The engine behind the protocol: audio device, loaded song, disk streamer and player.
// handle() and everything else run on the JUCE message thread; the server thread only
// moves bytes. Snapshots and events are queued here and sent by the server thread.
#pragma once

#include "audio/disk_streamer.h"
#include "audio/player.h"
#include "audio/song.h"

#include <juce_audio_devices/juce_audio_devices.h>
#include <juce_audio_formats/juce_audio_formats.h>
#include <juce_events/juce_events.h>

#include <atomic>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace rec
{
class Engine : private juce::AudioIODeviceCallback,
               private juce::ChangeListener,
               private juce::Timer
{
public:
    Engine();
    ~Engine() override;

    // Message thread. Returns the complete reply object (type and fields, without "re"),
    // or an error made with makeError().
    juce::var handle (const juce::String& type, const juce::var& message);

    // Server thread: snapshots and events waiting to be sent. With connected = false the
    // queue is just cleared.
    std::vector<std::string> takeOutgoing (bool connected);

    // Any thread.
    bool isPlaying() const noexcept { return player_.state() == audio::TransportState::playing; }

    // Exposed for tests (message thread).
    audio::Player& player() noexcept { return player_; }

private:
    // Requests.
    juce::var listDevices();
    juce::var setDevice (const juce::var& message);
    juce::var loadSong (const juce::var& message);
    juce::var unloadSong();
    juce::var transport (const juce::String& type, const juce::var& message);
    juce::var setTrack (const juce::var& message);
    juce::var setMaster (const juce::var& message);
    juce::var setPlan (const juce::var& message);
    juce::var getState();
    juce::var renderOffline (const juce::var& message);

    // Helpers.
    juce::var deviceInfo();
    juce::var songInfo();
    juce::var snapshot();
    void post (const audio::Command& command);
    void replaceSong (std::unique_ptr<audio::Song> next);
    void collectRetired();
    double engineRate();
    void push (const juce::var& message);
    void reloadAtRate (double rate);
    std::unique_ptr<audio::Plan> buildPlan (double rate) const;

    // juce::AudioIODeviceCallback (audio thread except where noted)
    void audioDeviceIOCallbackWithContext (const float* const* inputs, int numInputs, float* const* outputs,
                                           int numOutputs, int numSamples,
                                           const juce::AudioIODeviceCallbackContext& context) override;
    void audioDeviceAboutToStart (juce::AudioIODevice* device) override;
    void audioDeviceStopped() override;
    void audioDeviceError (const juce::String& message) override;

    // juce::ChangeListener (message thread): the device manager changed device.
    void changeListenerCallback (juce::ChangeBroadcaster*) override;

    // juce::Timer (message thread): snapshots, retiring songs, commands without a device.
    void timerCallback() override;

    juce::AudioFormatManager formats_;
    juce::AudioDeviceManager devices_;
    bool deviceInitialised_ = false;
    std::unique_ptr<audio::DiskStreamer> disk_;
    audio::Player player_;
    std::unique_ptr<audio::Song> song_;
    std::vector<std::unique_ptr<audio::Song>> retiring_;
    std::vector<audio::TrackSpec> specs_; // current mixer state of the loaded song, for getState/reload

    // The plan as the app sent it, in plan seconds, so a device rate change can derive it again.
    struct PlanSegmentSpec
    {
        double start = 0, end = 0, src = 0, fadeIn = 0, fadeOut = 0;
        bool silent = false;
        bool click = false;
        int key = 0;
        std::vector<std::string> mute;
    };

    // The entry tempo (protocol 5): plan seconds per output second. The engine counts output frames;
    // everything the app sends or receives in frames is in plan frames and is converted here.
    double tempo_ = 1.0;
    std::int64_t toPlanFrames (std::int64_t outputFrames) const { return static_cast<std::int64_t> (std::llround (static_cast<double> (outputFrames) * tempo_)); }
    std::int64_t toOutputFrames (std::int64_t planFrames) const { return static_cast<std::int64_t> (std::llround (static_cast<double> (planFrames) / tempo_)); }
    std::vector<PlanSegmentSpec> planSpec_;

    // The song grid of the plan in seconds (protocol 4): bar 1 beat 1, the beat length, beats per bar.
    struct GridSpec
    {
        double origin = 0, beat = 0;
        int beatsPerBar = 4;
    };
    GridSpec gridSpec_;
    bool hasPlan_ = false;

    std::atomic<bool> callbackActive_ { false };
    std::atomic<int> outputLatency_ { 0 };
    std::atomic<double> deviceRate_ { 0.0 };

    std::mutex outgoingMutex_;
    std::vector<std::string> outgoing_;
    juce::String lastSnapshotKey_;
    int idleTicks_ = 0;
};
} // namespace rec
