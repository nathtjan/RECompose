// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The engine behind the protocol: audio device, loaded song, disk streamer and player.
// handle() and everything else run on the JUCE message thread; the server thread only
// moves bytes. Snapshots and events are queued here and sent by the server thread.
#pragma once

#include "audio/disk_streamer.h"
#include "audio/pads.h"
#include "audio/player.h"
#include "audio/silent_clock.h"
#include "audio/song.h"
#include "audio/transition.h"
#include "audio/voice_input.h"

#include <juce_audio_devices/juce_audio_devices.h>
#include <juce_audio_formats/juce_audio_formats.h>
#include <juce_events/juce_events.h>

#include <array>
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
               private juce::MidiInputCallback,
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
    // A song playing, or a pad sounding: either keeps the engine alive without an app (REL-34, SL-40).
    bool isPlaying() const noexcept
    {
        return slots_[0].player.state() == audio::TransportState::playing || slots_[1].player.state() == audio::TransportState::playing || pads_.key() >= 0;
    }

    // Exposed for tests (message thread).
    audio::Player& player() noexcept { return cur().player; }

private:
    // The plan as the app sent it, in plan seconds, so a device rate change can derive it again.
    struct PlanSegmentSpec
    {
        double start = 0, end = 0, src = 0, fadeIn = 0, fadeOut = 0;
        bool silent = false;
        bool click = false;
        int key = 0;
        int songKey = -1;
        int pad = -1;
        std::vector<std::string> mute;
    };

    // Where the plan clock waits for GO, in plan seconds (protocol 8, PB-52).
    struct HoldSpec
    {
        double at = 0;
        bool pad = false;
        int padKey = -1;
        bool click = false;
    };

    // The song grid of the plan in seconds (protocol 4): bar 1 beat 1, the beat length, beats per bar.
    struct GridSpec
    {
        double origin = 0, beat = 0;
        int beatsPerBar = 4;
    };

    // A loaded song with its player and the state the app sent for it. The engine keeps the current song
    // and the next one (protocol 7); they swap roles when the next takes over (SL-30).
    struct Slot
    {
        audio::Player player;
        std::unique_ptr<audio::Song> song;
        std::vector<std::unique_ptr<audio::Song>> retiring;
        std::vector<audio::TrackSpec> specs; // current mixer state of the loaded song, for getState/reload
        std::vector<PlanSegmentSpec> planSpec;
        std::vector<HoldSpec> holdSpec;
        std::int64_t pendingSwapFrame = -1; // a swap the playhead has not reached yet, in output frames
        GridSpec grid;
        double tempo = 1.0; // the entry tempo (protocol 5): plan seconds per output second
        bool hasPlan = false;

        // The app's frames are plan frames; the engine counts output frames.
        std::int64_t toPlanFrames (std::int64_t outputFrames) const { return static_cast<std::int64_t> (std::llround (static_cast<double> (outputFrames) * tempo)); }
        std::int64_t toOutputFrames (std::int64_t planFrames) const { return static_cast<std::int64_t> (std::llround (static_cast<double> (planFrames) / tempo)); }
    };

    // The transition out of the current song as the app sent it (protocol 7), kept so it can be armed
    // again when the plan or the device rate changes.
    struct TransitionSpec
    {
        audio::TransitionCommand::Kind kind = audio::TransitionCommand::Kind::none;
        double atSec = 0, gapSec = 0, crossfadeSec = 0, autoStartSec = -1;
        int padKey = -1;
        bool click = false, fadePadOut = true;
        double clickBpm = 120;
        int beatsPerBar = 4, clickChannel = 0, clickWidth = 2;
    };

    Slot& cur() noexcept { return slots_[static_cast<std::size_t> (cur_.load())]; }
    Slot& nxt() noexcept { return slots_[static_cast<std::size_t> (1 - cur_.load())]; }
    const Slot& cur() const noexcept { return slots_[static_cast<std::size_t> (cur_.load())]; }
    Slot& slotFor (const juce::var& message);

    // The device the engine plays through (REL-20…23): the main one the app chose, the fallback that takes over when it
    // is lost, or the silent clock when neither is there.
    enum class Route { none, main, fallback, silent };

    struct DeviceChoice
    {
        juce::String type, name;
        double sampleRate = 0;
        int bufferSize = 0;
        bool empty() const { return name.isEmpty(); }
    };

    // Requests.
    juce::var listDevices();
    juce::var setDevice (const juce::var& message);
    juce::var setFallbackDevice (const juce::var& message);
    juce::var switchToMain();
    juce::var injectDeviceLoss (const juce::var& message);
    juce::var setVoiceInput (const juce::var& message);
    juce::var injectVoice (const juce::var& message);
    juce::var loadSong (const juce::var& message, Slot& s);
    juce::var unloadSong (Slot& s);
    juce::var setTransition (const juce::var& message);
    juce::var startNext (const juce::var& message);
    juce::var promoteNext();
    void armTransition();
    juce::String transitionName() const;
    void promote();
    void processBlock (float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept;
    juce::var transport (const juce::String& type, const juce::var& message);
    juce::var go();
    juce::var panic();
    juce::var setMidiInputs (const juce::var& message);
    juce::var injectMidi (const juce::var& message);
    void pushMidi (const juce::String& kind, int channel, int number, int value);
    juce::var setTrack (const juce::var& message);
    juce::var setMaster (const juce::var& message);
    juce::var setPlan (const juce::var& message, Slot& s);
    juce::var getState();
    juce::var renderOffline (const juce::var& message);
    juce::var loadPadSet (const juce::var& message);
    juce::var pad (const juce::var& message);
    void openPadBank (double rate);

    // Devices (message thread).
    juce::String openDevice (const DeviceChoice& choice);
    bool isPresent (const DeviceChoice& choice, bool scan);
    void checkDevice (bool forcePresence);
    void onDeviceLost();
    bool reopenMain();
    void checkMidiInputs();
    void checkXruns();
    static const char* routeName (Route route);

    // Somebody renders the audio graph: the device callback or the silent clock. When nobody does, the message thread
    // drains the players' commands itself.
    bool processing() const noexcept { return callbackActive_.load() || silent_.running(); }

    // Helpers.
    juce::var deviceInfo();
    juce::var songInfo (const Slot& s);
    juce::var snapshot();
    void post (Slot& s, const audio::Command& command);
    void replaceSong (Slot& s, std::unique_ptr<audio::Song> next);
    void collectRetired();
    double engineRate();
    void push (const juce::var& message);
    void reloadAtRate (double rate);
    std::unique_ptr<audio::Plan> buildPlan (double rate, const Slot& s) const;
    void reloadSlotAtRate (Slot& s, double rate);

    // juce::AudioIODeviceCallback (audio thread except where noted)
    void audioDeviceIOCallbackWithContext (const float* const* inputs, int numInputs, float* const* outputs,
                                           int numOutputs, int numSamples,
                                           const juce::AudioIODeviceCallbackContext& context) override;
    void audioDeviceAboutToStart (juce::AudioIODevice* device) override;
    void audioDeviceStopped() override;
    void audioDeviceError (const juce::String& message) override;

    // juce::MidiInputCallback (the MIDI thread): foot controllers and keyboards become midiIn events (protocol 9, LIVE-52).
    void handleIncomingMidiMessage (juce::MidiInput* source, const juce::MidiMessage& message) override;

    // juce::ChangeListener (message thread): the device manager changed device.
    void changeListenerCallback (juce::ChangeBroadcaster*) override;

    // juce::Timer (message thread): snapshots, retiring songs, commands without a device.
    void timerCallback() override;

    std::vector<std::unique_ptr<juce::MidiInput>> midiInputs_;
    bool midiAll_ = false;
    juce::StringArray midiWanted_;
    juce::StringArray midiMissing_;
    juce::uint32 lastMidiCheckMs_ = 0;
    juce::AudioFormatManager formats_;
    juce::AudioDeviceManager devices_;
    bool deviceInitialised_ = false;
    std::unique_ptr<audio::DiskStreamer> disk_;
    std::array<Slot, 2> slots_;
    std::atomic<int> cur_ { 0 };
    audio::Transitioner transitions_;
    TransitionSpec transitionSpec_;
    std::vector<std::unique_ptr<audio::Plan>> transitionPlans_; // click grids the audio thread may still hold
    audio::PadPlayer pads_;
    std::vector<std::unique_ptr<audio::PadBank>> padBanks_; // the one sounding and any still fading out
    std::string padSetId_;
    std::array<std::string, audio::kPadKeys> padFiles_;

    std::atomic<bool> callbackActive_ { false };
    std::atomic<int> outputLatency_ { 0 };
    std::atomic<double> deviceRate_ { 0.0 };
    std::atomic<int> deviceChannels_ { 2 };
    std::atomic<std::uint64_t> callbacks_ { 0 };     // device blocks played, to tell a device that stopped calling back
    std::atomic<std::int64_t> lastCallbackNs_ { 0 }; // SilentClock::steadyNowNs() of the last one

    DeviceChoice main_, fallback_;
    Route route_ = Route::none;
    bool callbackRegistered_ = false;
    bool mainGone_ = false, fallbackGone_ = false; // injectDeviceLoss: the engine sees these as unplugged
    bool mainBack_ = false;                        // on the fallback with the main device present again
    bool silentPaused_ = false;
    double silentLimitSec_ = 60.0;
    juce::uint32 lostAtMs_ = 0, lastPresenceCheckMs_ = 0, seenCallbacksAtMs_ = 0;
    std::uint64_t seenCallbacks_ = 0;
    int lastXruns_ = 0;

    // It renders through the slots, the transitions and the pads above; ~Engine stops it before anything else.
    audio::SilentClock silent_ { [this] (float* const* outputs, int numOutputs, int numSamples) { processBlock (outputs, numOutputs, numSamples, false); } };

    // The talkback mic (protocol 12, VC-10): a channel of the device's input, or −1.
    int voiceChannel_ = -1;
    juce::String voiceDevice_;
    std::atomic<bool> voiceOn_ { false };
    float voiceLevelDb_ = -100.0f;
    audio::VoiceInput voice_ { [this] (std::vector<float>&& samples, double durationSec, double trailingSec) { pushUtterance (samples, durationSec, trailingSec); } };
    void pushUtterance (const std::vector<float>& samples, double durationSec, double trailingSec);

    std::mutex outgoingMutex_;
    std::vector<std::string> outgoing_;
    juce::String lastSnapshotKey_;
    int idleTicks_ = 0;
};
} // namespace rec
