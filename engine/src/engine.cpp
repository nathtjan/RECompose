// SPDX-License-Identifier: AGPL-3.0-or-later
#include "engine.h"

#include "message_handler.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <thread>

namespace rec
{
namespace
{
    constexpr double kDefaultEngineRate = 48000.0;
    constexpr int kMaxOutputChannels = 64;

    struct Obj
    {
        juce::DynamicObject::Ptr p { new juce::DynamicObject() };

        Obj& set (const char* key, const juce::var& value)
        {
            p->setProperty (key, value);
            return *this;
        }

        operator juce::var() const { return juce::var (p.get()); }
    };

    double number (const juce::var& message, const char* key, double fallback)
    {
        const auto value = message.getProperty (key, {});
        return value.isInt() || value.isInt64() || value.isDouble() ? static_cast<double> (value) : fallback;
    }

    bool flag (const juce::var& message, const char* key, bool fallback)
    {
        const auto value = message.getProperty (key, {});
        return value.isBool() ? static_cast<bool> (value) : fallback;
    }

    juce::String text (const juce::var& message, const char* key)
    {
        const auto value = message.getProperty (key, {});
        return value.isString() ? value.toString() : juce::String();
    }

    float gainToDecibels (float gain) { return gain <= 0.0f ? -100.0f : 20.0f * std::log10 (gain); }

    juce::var stateName (audio::TransportState state)
    {
        switch (state)
        {
            case audio::TransportState::playing: return "playing";
            case audio::TransportState::paused: return "paused";
            case audio::TransportState::stopped: break;
        }
        return "stopped";
    }

    juce::var toArray (const juce::StringArray& values)
    {
        juce::Array<juce::var> result;
        for (const auto& v : values)
            result.add (v);
        return result;
    }
} // namespace

Engine::Engine()
{
    formats_.registerBasicFormats();
    disk_ = std::make_unique<audio::DiskStreamer>();
    for (auto& slot : slots_)
    {
        slot.player.setPads (&pads_);
        slot.player.setMidiOut (&midiQueue_);
    }
    midiThread_ = std::thread ([this] { runMidiOut(); });
    devices_.addChangeListener (this);
    startTimerHz (10);
}

Engine::~Engine()
{
    midiStop_.store (true);
    midiThread_.join();
    stopTimer();
    silent_.stop();
    midiInputs_.clear(); // the MIDI thread stops before anything it pushes to goes away
    devices_.removeChangeListener (this);
    devices_.removeAudioCallback (this);
    devices_.closeAudioDevice();
    disk_.reset(); // the disk thread stops before any song or pad set is freed
    for (auto& slot : slots_)
    {
        slot.song.reset();
        slot.retiring.clear();
    }
    padBanks_.clear();
}

//==============================================================================
juce::var Engine::handle (const juce::String& type, const juce::var& message)
{
    if (type == "listDevices")  return listDevices();
    if (type == "setDevice")    return setDevice (message);
    if (type == "setFallbackDevice") return setFallbackDevice (message);
    if (type == "switchToMain") return switchToMain();
    if (type == "injectDeviceLoss") return injectDeviceLoss (message);
    if (type == "setVoiceInput") return setVoiceInput (message);
    if (type == "injectVoice")  return injectVoice (message);
    if (type == "playClip")     return playClip (message);
    if (type == "loadSong")     return loadSong (message, slotFor (message));
    if (type == "unloadSong")   return unloadSong (slotFor (message));
    if (type == "setTransition") return setTransition (message);
    if (type == "startNext")    return startNext (message);
    if (type == "promoteNext")  return promoteNext();
    if (type == "play" || type == "pause" || type == "stop" || type == "seek")
        return transport (type, message);
    if (type == "setMidiInputs") return setMidiInputs (message);
    if (type == "injectMidi")   return injectMidi (message);
    if (type == "setMidiOutput") return setMidiOutput (message);
    if (type == "sendMidi")     return sendMidi (message);
    if (type == "go")           return go();
    if (type == "panic")        return panic();
    if (type == "setTrack")     return setTrack (message);
    if (type == "setMaster")    return setMaster (message);
    if (type == "setPlan")      return setPlan (message, slotFor (message));
    if (type == "getState")     return getState();
    if (type == "renderOffline") return renderOffline (message);
    if (type == "loadPadSet")   return loadPadSet (message);
    if (type == "pad")          return pad (message);

    return makeError ({}, "unknownType", "Unknown message type: " + type);
}

std::vector<std::string> Engine::takeOutgoing (bool connected)
{
    const std::lock_guard lock (outgoingMutex_);
    std::vector<std::string> result;
    result.swap (outgoing_);
    if (! connected)
        result.clear();
    return result;
}

void Engine::push (const juce::var& message)
{
    const std::lock_guard lock (outgoingMutex_);
    if (outgoing_.size() < 512) // nobody reading: don't grow without bound
        outgoing_.push_back (toJson (message));
}

Engine::Slot& Engine::slotFor (const juce::var& message)
{
    return text (message, "slot") == "next" ? nxt() : cur();
}

double Engine::engineRate()
{
    const auto rate = deviceRate_.load();
    return rate > 0.0 ? rate : kDefaultEngineRate;
}

void Engine::post (Slot& s, const audio::Command& command)
{
    s.player.post (command);
    disk_->wake();
    if (! processing())
    {
        s.player.processCommandsOnly();
        collectRetired();
    }
}

//==============================================================================
// Pads (protocol 6, SL-40…46): a pad set is opened here and sounds on its own player.
void Engine::openPadBank (double rate)
{
    auto bank = audio::loadPadBank (padSetId_, padFiles_, rate, formats_);
    disk_->addPads (bank.get());
    audio::PadCommand command;
    command.type = audio::PadCommand::Type::setBank;
    command.bank = bank.get();
    padBanks_.push_back (std::move (bank));
    pads_.post (command);
    disk_->wake();
}

juce::var Engine::loadPadSet (const juce::var& message)
{
    const auto* samples = message.getProperty ("samples", {}).getArray();
    if (samples == nullptr)
        return makeError ({}, "badMessage", "loadPadSet needs samples.");

    padSetId_ = text (message, "id").toStdString();
    padFiles_ = {};
    for (const auto& s : *samples)
    {
        const auto pc = static_cast<int> (number (s, "pitchClass", -1.0));
        if (pc >= 0 && pc < audio::kPadKeys)
            padFiles_[static_cast<std::size_t> (pc)] = text (s, "file").toStdString();
    }

    openPadBank (engineRate());
    const auto& bank = *padBanks_.back();
    juce::Array<juce::var> keys;
    for (int k = 0; k < audio::kPadKeys; ++k)
    {
        const auto& track = *bank.keys[static_cast<std::size_t> (k)].track;
        keys.add (Obj().set ("pitchClass", k).set ("missing", track.missing).set ("problem", track.problem));
    }
    return Obj().set ("type", "padSet").set ("id", juce::String (padSetId_)).set ("keys", keys);
}

juce::var Engine::pad (const juce::var& message)
{
    auto send = [this] (audio::PadCommand command) { pads_.post (command); };

    if (const auto v = message.getProperty ("gainDb", {}); ! v.isVoid())
    {
        audio::PadCommand c;
        c.type = audio::PadCommand::Type::setGain;
        c.value = audio::decibelsToGain (static_cast<float> (static_cast<double> (v)));
        send (c);
    }
    if (const auto v = message.getProperty ("fadeSec", {}); ! v.isVoid())
    {
        audio::PadCommand c;
        c.type = audio::PadCommand::Type::setFade;
        c.frames = static_cast<std::int64_t> (std::llround (std::max (0.01, static_cast<double> (v)) * engineRate()));
        send (c);
    }
    if (const auto v = message.getProperty ("follow", {}); v.isBool())
    {
        audio::PadCommand c;
        c.type = audio::PadCommand::Type::setFollow;
        c.key = static_cast<bool> (v) ? 1 : 0;
        send (c);
    }
    if (const auto output = message.getProperty ("output", {}); output.isObject())
    {
        audio::PadCommand c;
        c.type = audio::PadCommand::Type::setOutput;
        c.channel = static_cast<int> (number (output, "channel", 0));
        c.width = static_cast<int> (number (output, "width", 2)) == 1 ? 1 : 2;
        send (c);
    }
    if (message.hasProperty ("key"))
    {
        // A pitch class starts that pad (crossfading from another); null stops it (SL-42).
        const auto v = message.getProperty ("key", {});
        audio::PadCommand c;
        c.type = audio::PadCommand::Type::play;
        c.key = v.isInt() || v.isInt64() || v.isDouble() ? juce::jlimit (-1, audio::kPadKeys - 1, static_cast<int> (v)) : -1;
        send (c);
        disk_->wake();
    }

    if (! processing())
        collectRetired();
    return Obj().set ("type", "ok");
}

//==============================================================================
juce::var Engine::deviceInfo()
{
    auto* device = devices_.getCurrentAudioDevice();
    if (device == nullptr)
        return {};

    juce::Array<juce::var> rates, sizes;
    for (auto r : device->getAvailableSampleRates())
        rates.add (r);
    for (auto s : device->getAvailableBufferSizes())
        sizes.add (s);

    return Obj()
        .set ("type", devices_.getCurrentAudioDeviceType())
        .set ("name", device->getName())
        .set ("sampleRate", device->getCurrentSampleRate())
        .set ("bufferSize", device->getCurrentBufferSizeSamples())
        .set ("outputChannels", toArray (device->getOutputChannelNames()))
        .set ("inputChannels", toArray (device->getInputChannelNames()))
        .set ("activeOutputs", device->getActiveOutputChannels().countNumberOfSetBits())
        .set ("sampleRates", rates)
        .set ("bufferSizes", sizes)
        .set ("outputLatency", device->getOutputLatencyInSamples());
}

juce::var Engine::listDevices()
{
    juce::Array<juce::var> types;
    for (auto* type : devices_.getAvailableDeviceTypes())
    {
        type->scanForDevices();
        types.add (Obj().set ("name", type->getTypeName()).set ("devices", toArray (type->getDeviceNames (false))).set ("inputs", toArray (type->getDeviceNames (true))));
    }

    juce::Array<juce::var> midi;
    for (const auto& device : juce::MidiInput::getAvailableDevices())
        midi.add (device.name);

    juce::Array<juce::var> midiOut;
    for (const auto& device : juce::MidiOutput::getAvailableDevices())
        midiOut.add (device.name);

    return Obj().set ("type", "devices").set ("types", types).set ("current", deviceInfo()).set ("midiInputs", midi).set ("midiOutputs", midiOut);
}

// Protocol 13 (SET-30): the MIDI output the plan's messages go to, by name; `virtual` makes a source of that name
// (macOS and Linux). Without a name MIDI out is closed; the messages are still reported in `midiOut`.
juce::var Engine::setMidiOutput (const juce::var& message)
{
    const auto name = text (message, "name");
    std::unique_ptr<juce::MidiOutput> port;
    if (flag (message, "virtual", false) && name.isNotEmpty())
    {
#if JUCE_MAC || JUCE_LINUX
        port = juce::MidiOutput::createNewDevice (name);
#endif
    }
    else if (name.isNotEmpty())
    {
        for (const auto& device : juce::MidiOutput::getAvailableDevices())
            if (device.name == name)
                port = juce::MidiOutput::openDevice (device.identifier);
    }

    const bool open = port != nullptr;
    {
        const std::lock_guard lock (midiOutMutex_);
        midiOut_ = std::move (port);
    }
    return Obj().set ("type", "midiOutput").set ("name", open ? name : juce::String()).set ("open", open);
}

// Protocol 13: messages sent now (a test note, AUTO-21 resend on play), each `[status, data…]`.
juce::var Engine::sendMidi (const juce::var& message)
{
    std::vector<audio::PlanMidi> messages;
    if (const auto* list = message.getProperty ("messages", {}).getArray())
    {
        for (const auto& m : *list)
        {
            audio::PlanMidi out;
            out.id = static_cast<int> (number (m, "id", 0.0));
            if (const auto* bytes = m.getProperty ("bytes", {}).getArray())
                for (const auto& b : *bytes)
                    if (out.size < 3)
                        out.bytes[out.size++] = static_cast<std::uint8_t> (juce::jlimit (0, 255, static_cast<int> (b)));
            messages.push_back (out);
        }
    }
    sendMidiNow (messages);
    return Obj().set ("type", "ok");
}

void Engine::sendMidiNow (const std::vector<audio::PlanMidi>& messages)
{
    if (messages.empty())
        return;
    juce::Array<juce::var> sent;
    {
        const std::lock_guard lock (midiOutMutex_);
        for (const auto& m : messages)
        {
            if (m.size > 0)
            {
                midiChannels_.fetch_or (1u << (m.bytes[0] & 0x0f));
                if (midiOut_ != nullptr)
                    midiOut_->sendMessageNow (juce::MidiMessage (m.bytes, m.size));
            }
            juce::Array<juce::var> bytes;
            for (int i = 0; i < m.size; ++i)
                bytes.add (static_cast<int> (m.bytes[i]));
            sent.add (Obj().set ("id", m.id).set ("bytes", bytes));
        }
    }
    push (Obj().set ("type", "midiOut").set ("messages", sent));
}

// The MIDI thread: sends what the players queued, a millisecond or so after the audio thread played past it.
void Engine::runMidiOut()
{
    std::vector<audio::PlanMidi> batch;
    while (! midiStop_.load())
    {
        batch.clear();
        audio::PlanMidi m;
        while (midiQueue_.pop (m))
            batch.push_back (m);
        sendMidiNow (batch);
        std::this_thread::sleep_for (std::chrono::milliseconds (1));
    }
}

// Protocol 9 (LIVE-52): the MIDI inputs to listen to, by name; `all` listens to every input. Each message becomes a `midiIn`
// event for the app, which owns what it means (the control map is in RECompose.Core).
juce::var Engine::setMidiInputs (const juce::var& message)
{
    midiInputs_.clear();
    const bool all = flag (message, "all", false);
    juce::StringArray wanted;
    if (const auto* names = message.getProperty ("names", {}).getArray())
        for (const auto& name : *names)
            wanted.add (name.toString());
    midiAll_ = all;
    midiWanted_ = wanted;
    midiMissing_.clear();
    lastMidiCheckMs_ = juce::Time::getMillisecondCounter();

    juce::Array<juce::var> open;
    for (const auto& device : juce::MidiInput::getAvailableDevices())
    {
        if (! all && ! wanted.contains (device.name))
            continue;
        if (auto input = juce::MidiInput::openDevice (device.identifier, this))
        {
            input->start();
            open.add (device.name);
            midiInputs_.push_back (std::move (input));
        }
    }

    for (const auto& name : wanted)
        if (! open.contains (name))
            midiMissing_.add (name);
    return Obj().set ("type", "midiInputs").set ("open", open).set ("missing", toArray (midiMissing_));
}

// Tests: the same event a device would produce.
juce::var Engine::injectMidi (const juce::var& message)
{
    pushMidi (text (message, "kind"), static_cast<int> (number (message, "channel", 1.0)), static_cast<int> (number (message, "number", 0.0)),
              static_cast<int> (number (message, "value", 0.0)));
    return Obj().set ("type", "ok");
}

void Engine::pushMidi (const juce::String& kind, int channel, int number, int value)
{
    push (Obj().set ("type", "midiIn").set ("kind", kind).set ("channel", channel).set ("number", number).set ("value", value));
}

void Engine::handleIncomingMidiMessage (juce::MidiInput*, const juce::MidiMessage& m)
{
    if (m.isNoteOn())
        pushMidi ("note", m.getChannel(), m.getNoteNumber(), m.getVelocity());
    else if (m.isNoteOff())
        pushMidi ("note", m.getChannel(), m.getNoteNumber(), 0);
    else if (m.isController())
        pushMidi ("cc", m.getChannel(), m.getControllerNumber(), m.getControllerValue());
    else if (m.isProgramChange())
        pushMidi ("pc", m.getChannel(), m.getProgramChangeNumber(), 0);
}

juce::var Engine::setDevice (const juce::var& message)
{
    // Tests that render offline set this so a machine with an audio device does not also play in real time.
    if (juce::SystemStats::getEnvironmentVariable ("RECOMPOSE_NO_DEVICE", {}) == "1")
        return makeError ({}, "deviceError", "Audio output is switched off for this run.");

    const DeviceChoice choice { text (message, "deviceType"), text (message, "name"), number (message, "sampleRate", 0.0),
                                static_cast<int> (number (message, "bufferSize", 0.0)) };
    if (const auto error = openDevice (choice); error.isNotEmpty())
    {
        // The device that was open is closed now. With one chosen before, the clock keeps time and the engine opens
        // it again when it can (REL-21).
        if (! main_.empty())
        {
            if (! silent_.running())
                silent_.start (engineRate(), deviceChannels_.load(), lastCallbackNs_.load());
            route_ = Route::silent;
            lostAtMs_ = juce::Time::getMillisecondCounter();
            silentPaused_ = false;
        }
        return makeError ({}, "deviceError", error);
    }

    // The main device is whatever this opened (an empty name means the type's default).
    main_ = { devices_.getCurrentAudioDeviceType(), devices_.getCurrentAudioDevice()->getName(), choice.sampleRate, choice.bufferSize };
    route_ = Route::main;
    mainGone_ = false;
    mainBack_ = false;
    silentPaused_ = false;
    return Obj().set ("type", "device").set ("device", deviceInfo());
}

//==============================================================================
// Device loss (protocol 10, REL-20…24). The engine notices a device that went away (JUCE closes it, opens another of
// its own choosing, or it stops calling back), keeps the clock with the silent clock, and moves to the fallback device
// when one is set. A returning main device takes over again by itself from the silent clock; from the fallback the
// app decides (Switch back, or at the next section boundary).
const char* Engine::routeName (Route route)
{
    switch (route)
    {
        case Route::main: return "main";
        case Route::fallback: return "fallback";
        case Route::silent: return "silent";
        case Route::none: break;
    }
    return "none";
}

// Opens a device while the clock keeps running: our callback is off the device manager while the device opens, the
// silent clock renders if something is playing, and the new device's start (audioDeviceAboutToStart) stops it again.
juce::String Engine::openDevice (const DeviceChoice& choice)
{
    if (! deviceInitialised_)
    {
        const auto error = devices_.initialise (0, kMaxOutputChannels, nullptr, true);
        deviceInitialised_ = true;
        juce::ignoreUnused (error);
    }

    if (callbackRegistered_)
    {
        const auto last = lastCallbackNs_.load();
        devices_.removeAudioCallback (this);
        callbackRegistered_ = false;
        if (isPlaying() && ! silent_.running())
            silent_.start (engineRate(), deviceChannels_.load(), last);
    }

    if (choice.type.isNotEmpty() && choice.type != devices_.getCurrentAudioDeviceType())
        devices_.setCurrentAudioDeviceType (choice.type, true);

    auto setup = devices_.getAudioDeviceSetup();
    if (choice.name.isNotEmpty())
        setup.outputDeviceName = choice.name;
    setup.inputDeviceName = {};
    setup.useDefaultInputChannels = false;
    setup.inputChannels.clear();
    setup.useDefaultOutputChannels = false;
    setup.outputChannels.clear();
    setup.outputChannels.setRange (0, kMaxOutputChannels, true);
    if (choice.sampleRate > 0.0)
        setup.sampleRate = choice.sampleRate;
    if (choice.bufferSize > 0)
        setup.bufferSize = choice.bufferSize;

    if (voiceChannel_ >= 0)
    {
        setup.inputDeviceName = voiceDevice_.isNotEmpty() ? voiceDevice_ : setup.outputDeviceName;
        setup.inputChannels.setBit (voiceChannel_);
    }

    auto error = devices_.setAudioDeviceSetup (setup, true);
    if (voiceChannel_ >= 0 && (error.isNotEmpty() || devices_.getCurrentAudioDevice() == nullptr))
    {
        // The music matters more than the mic: open the outputs alone and say why voice is off.
        push (Obj().set ("type", "voiceError").set ("message", error.isNotEmpty() ? error : juce::String ("The voice input could not be opened.")));
        setup.inputDeviceName = {};
        setup.inputChannels.clear();
        error = devices_.setAudioDeviceSetup (setup, true);
    }
    seenCallbacks_ = callbacks_.load();
    seenCallbacksAtMs_ = juce::Time::getMillisecondCounter();
    if (error.isNotEmpty() || devices_.getCurrentAudioDevice() == nullptr)
        return error.isNotEmpty() ? error : juce::String ("No audio device could be opened.");

    devices_.addAudioCallback (this);
    callbackRegistered_ = true;
    return {};
}

bool Engine::isPresent (const DeviceChoice& choice, bool scan)
{
    if (choice.empty() || (&choice == &main_ && mainGone_) || (&choice == &fallback_ && fallbackGone_))
        return false;
    if (! deviceInitialised_)
        return false;

    for (auto* type : devices_.getAvailableDeviceTypes())
    {
        if (choice.type.isNotEmpty() && type->getTypeName() != choice.type)
            continue;
        if (scan)
            type->scanForDevices();
        if (type->getDeviceNames (false).contains (choice.name))
            return true;
    }
    return false;
}

void Engine::checkDevice (bool forcePresence)
{
    const auto now = juce::Time::getMillisecondCounter();

    if (route_ == Route::main || route_ == Route::fallback)
    {
        const auto& want = route_ == Route::main ? main_ : fallback_;
        const bool gone = route_ == Route::main ? mainGone_ : fallbackGone_;
        if (const auto count = callbacks_.load(); count != seenCallbacks_)
        {
            seenCallbacks_ = count;
            seenCallbacksAtMs_ = now;
        }
        auto* device = devices_.getCurrentAudioDevice();
        const bool stalled = now - seenCallbacksAtMs_ > 500;
        if (gone || device == nullptr || device->getName() != want.name || ! device->isPlaying() || stalled)
        {
            onDeviceLost();
            return;
        }
    }

    if ((route_ == Route::fallback || route_ == Route::silent) && ! main_.empty() && (forcePresence || now - lastPresenceCheckMs_ >= 1000))
    {
        lastPresenceCheckMs_ = now;
        const bool present = isPresent (main_, true);
        if (present && route_ == Route::silent)
        {
            reopenMain(); // the sound returns at the bar the clock has reached (REL-22)
        }
        else if (present && ! mainBack_)
        {
            mainBack_ = true;
            push (Obj().set ("type", "mainDeviceBack").set ("name", main_.name));
        }
        else if (! present)
        {
            mainBack_ = false;
        }
    }

    // The silent clock runs for a minute at most; then the song pauses where it is (REL-21).
    if (route_ == Route::silent && ! silentPaused_ && now - lostAtMs_ > static_cast<juce::uint32> (silentLimitSec_ * 1000.0))
    {
        silentPaused_ = true;
        for (auto& slot : slots_)
        {
            if (slot.player.state() == audio::TransportState::playing)
            {
                audio::Command command;
                command.type = audio::Command::Type::pause;
                post (slot, command);
            }
        }
        push (Obj().set ("type", "silentClockStopped").set ("afterSec", silentLimitSec_));
    }
}

void Engine::onDeviceLost()
{
    const bool fromFallback = route_ == Route::fallback;
    const auto lostName = fromFallback ? fallback_.name : main_.name;
    const auto last = lastCallbackNs_.load();
    const auto now = juce::Time::getMillisecondCounter();

    if (callbackRegistered_)
    {
        devices_.removeAudioCallback (this);
        callbackRegistered_ = false;
    }
    devices_.closeAudioDevice(); // and not whatever device JUCE opened in its place
    if (! silent_.running())
        silent_.start (engineRate(), deviceChannels_.load(), last);
    lostAtMs_ = now;
    lastPresenceCheckMs_ = now;
    silentPaused_ = false;
    mainBack_ = false;

    if (! fromFallback && isPresent (fallback_, true) && openDevice (fallback_).isEmpty())
    {
        route_ = Route::fallback;
        push (Obj().set ("type", "deviceLost").set ("name", lostName).set ("route", "fallback").set ("fallback", fallback_.name).set ("device", deviceInfo()));
        return;
    }

    route_ = Route::silent;
    push (Obj().set ("type", "deviceLost").set ("name", lostName).set ("route", "silent"));
}

bool Engine::reopenMain()
{
    const bool fromFallback = route_ == Route::fallback;
    if (openDevice (main_).isEmpty())
    {
        route_ = Route::main;
        mainBack_ = false;
        silentPaused_ = false;
        push (Obj().set ("type", "deviceRestored").set ("name", main_.name).set ("device", deviceInfo()));
        return true;
    }

    // It is listed but would not open: back to the fallback if it is still there, else the clock keeps time.
    if (fromFallback && isPresent (fallback_, false) && openDevice (fallback_).isEmpty())
        return false;
    if (! silent_.running())
        silent_.start (engineRate(), deviceChannels_.load(), lastCallbackNs_.load());
    if (route_ != Route::silent)
    {
        route_ = Route::silent;
        lostAtMs_ = juce::Time::getMillisecondCounter();
        silentPaused_ = false;
        push (Obj().set ("type", "deviceLost").set ("name", fromFallback ? fallback_.name : main_.name).set ("route", "silent"));
    }
    return false;
}

juce::var Engine::setFallbackDevice (const juce::var& message)
{
    fallback_ = { text (message, "deviceType"), text (message, "name"), 0.0, 0 };
    fallbackGone_ = false;
    return Obj().set ("type", "fallback").set ("name", fallback_.name).set ("present", isPresent (fallback_, true));
}

juce::var Engine::switchToMain()
{
    if (route_ == Route::main)
        return Obj().set ("type", "device").set ("device", deviceInfo());
    if (! isPresent (main_, true))
        return makeError ({}, "deviceError", "The main audio device is not connected.");
    if (! reopenMain())
        return makeError ({}, "deviceError", "The main audio device could not be opened.");
    return Obj().set ("type", "device").set ("device", deviceInfo());
}

// Tests: the engine sees the main or the fallback device as unplugged (present false) or back (true), the same paths a
// real device takes. Without any device open (tests that render offline) a loss starts the silent clock.
// Protocol 12 (VC-1, VC-10): the talkback mic is a channel of the input of the device the engine plays through (`device` names
// another input device where the driver allows one, as WASAPI does). The device opens again with it.
juce::var Engine::setVoiceInput (const juce::var& message)
{
    const int channel = static_cast<int> (number (message, "channel", -1.0));
    const auto device = text (message, "device");
    if (channel != voiceChannel_ || device != voiceDevice_)
    {
        voiceChannel_ = std::max (-1, channel);
        voiceDevice_ = device;
        voiceOn_.store (false);
        if (route_ == Route::main || route_ == Route::fallback)
            if (const auto error = openDevice (route_ == Route::main ? main_ : fallback_); error.isNotEmpty())
                return makeError ({}, "deviceError", error);
    }
    return Obj().set ("type", "voice").set ("channel", voiceChannel_).set ("device", voiceDevice_).set ("on", voiceOn_.load());
}

namespace
{
juce::String toPcm16Base64 (const std::vector<float>& samples)
{
    juce::MemoryOutputStream out;
    for (auto x : samples)
        out.writeShort (static_cast<short> (juce::jlimit (-32768, 32767, juce::roundToInt (x * 32767.0f))));
    return juce::Base64::toBase64 (out.getData(), out.getDataSize());
}
} // namespace

// Tests only: 16 kHz audio (`samples`, 16-bit PCM in base64) straight into the voice detector.
juce::var Engine::injectVoice (const juce::var& message)
{
    juce::MemoryOutputStream decoded;
    if (! juce::Base64::convertFromBase64 (decoded, text (message, "samples")))
        return makeError ({}, "badMessage", "samples is not base64.");
    std::vector<float> samples (decoded.getDataSize() / 2);
    const auto* pcm = static_cast<const char*> (decoded.getData());
    for (std::size_t i = 0; i < samples.size(); ++i)
        samples[i] = static_cast<float> (static_cast<juce::int16> (juce::ByteOrder::littleEndianShort (pcm + 2 * i))) / 32768.0f;
    voice_.inject (std::move (samples));
    return Obj().set ("type", "ok");
}

// Protocol 12 (VC-50): a spoken clip once on the given channels, replacing one that sounds.
juce::var Engine::playClip (const juce::var& message)
{
    juce::String problem;
    auto clip = audio::loadClip (juce::File (text (message, "file")), engineRate(), formats_, problem);
    if (clip == nullptr)
        return makeError ({}, "badMessage", problem);
    const auto output = message.getProperty ("output", {});
    clip->channel = juce::jlimit (0, kMaxOutputChannels - 1, static_cast<int> (number (output, "channel", 0.0)));
    clip->width = static_cast<int> (number (output, "width", 2.0)) == 1 ? 1 : 2;
    clip->gain = juce::Decibels::decibelsToGain (static_cast<float> (number (message, "gainDb", -12.0)));
    const double seconds = static_cast<double> (clip->samples.size()) / engineRate();

    std::erase_if (clips_, [] (const auto& c) { return c->done.load(); });
    clipPlayer_.start (clip.get());
    clips_.push_back (std::move (clip));
    return Obj().set ("type", "clip").set ("durationSec", seconds);
}

// The VAD thread: an utterance for the app's recogniser (protocol 12).
void Engine::pushUtterance (const std::vector<float>& samples, double durationSec, double trailingSec)
{
    push (Obj().set ("type", "utterance").set ("rate", audio::VoiceActivity::kRate).set ("durationSec", durationSec)
              .set ("trailingSec", trailingSec).set ("samples", toPcm16Base64 (samples)));
}

juce::var Engine::injectDeviceLoss (const juce::var& message)
{
    if (const auto limit = number (message, "silentLimitSec", -1.0); limit >= 0.0)
        silentLimitSec_ = limit;
    const bool present = flag (message, "present", false);
    if (text (message, "device") == "fallback")
        fallbackGone_ = ! present;
    else
        mainGone_ = ! present;

    if (! present && route_ == Route::none)
    {
        for (auto& slot : slots_)
            slot.player.prepare (engineRate(), audio::SilentClock::kBlock, deviceChannels_.load());
        transitions_.prepare (engineRate());
        pads_.prepare (engineRate());
        silent_.start (engineRate(), deviceChannels_.load());
        route_ = Route::silent;
        lostAtMs_ = juce::Time::getMillisecondCounter();
        silentPaused_ = false;
        push (Obj().set ("type", "deviceLost").set ("name", juce::String()).set ("route", "silent"));
    }
    else if (present && route_ == Route::silent && main_.empty())
    {
        silent_.stop();
        route_ = Route::none;
        push (Obj().set ("type", "deviceRestored").set ("name", juce::String()));
    }
    else
    {
        checkDevice (true);
    }

    return Obj().set ("type", "ok").set ("route", routeName (route_));
}

// A controller that is unplugged is dropped and opened again when it returns, checked every 2 s (REL-50).
void Engine::checkMidiInputs()
{
    if (! midiAll_ && midiWanted_.isEmpty())
        return;
    const auto now = juce::Time::getMillisecondCounter();
    if (now - lastMidiCheckMs_ < 2000)
        return;
    lastMidiCheckMs_ = now;

    const auto available = juce::MidiInput::getAvailableDevices();
    bool changed = false;
    for (auto it = midiInputs_.begin(); it != midiInputs_.end();)
    {
        const auto id = (*it)->getIdentifier();
        if (std::none_of (available.begin(), available.end(), [&id] (const auto& d) { return d.identifier == id; }))
        {
            midiMissing_.addIfNotAlreadyThere ((*it)->getName());
            it = midiInputs_.erase (it);
            changed = true;
        }
        else
        {
            ++it;
        }
    }

    for (const auto& device : available)
    {
        if (! midiAll_ && ! midiWanted_.contains (device.name))
            continue;
        if (std::any_of (midiInputs_.begin(), midiInputs_.end(), [&device] (const auto& in) { return in->getIdentifier() == device.identifier; }))
            continue;
        if (auto input = juce::MidiInput::openDevice (device.identifier, this))
        {
            input->start();
            midiMissing_.removeString (device.name);
            midiInputs_.push_back (std::move (input));
            changed = true;
        }
    }

    if (changed)
    {
        juce::Array<juce::var> open;
        for (const auto& in : midiInputs_)
            open.add (in->getName());
        push (Obj().set ("type", "midiInputsChanged").set ("open", open).set ("missing", toArray (midiMissing_)));
    }
}

// Each new xrun is logged with where the song was (REL-24).
void Engine::checkXruns()
{
    auto* device = devices_.getCurrentAudioDevice();
    const int count = device != nullptr ? std::max (0, device->getXRunCount()) : 0;
    if (count > lastXruns_)
        push (Obj().set ("type", "xrun").set ("count", count).set ("frame", static_cast<juce::int64> (cur().toPlanFrames (cur().player.position()))));
    lastXruns_ = count;
}

//==============================================================================
juce::var Engine::songInfo (const Slot& s)
{
    if (s.song == nullptr)
        return {};

    juce::Array<juce::var> tracks;
    for (std::size_t i = 0; i < s.song->tracks.size(); ++i)
    {
        const auto& track = *s.song->tracks[i];
        const auto& spec = s.specs[i];
        tracks.add (Obj()
                        .set ("id", juce::String (spec.id))
                        .set ("channels", track.channels)
                        .set ("sourceRate", track.sourceRate)
                        .set ("sourceFrames", static_cast<juce::int64> (track.sourceFrames))
                        .set ("missing", track.missing)
                        .set ("problem", track.problem)
                        .set ("renders", static_cast<int> (track.renders.size()))
                        .set ("gainDb", spec.gainDb)
                        .set ("mute", spec.mute)
                        .set ("solo", spec.solo)
                        .set ("output", Obj().set ("channel", spec.outputChannel).set ("width", spec.outputWidth)));
    }

    return Obj()
        .set ("songId", juce::String (s.song->id))
        .set ("sampleRate", s.song->sampleRate)
        .set ("lengthFrames", static_cast<juce::int64> (s.song->lengthFrames))
        .set ("masterGainDb", gainToDecibels (s.song->masterGain.load()))
        .set ("tracks", tracks);
}

void Engine::replaceSong (Slot& s, std::unique_ptr<audio::Song> next)
{
    if (next != nullptr)
        disk_->addSong (next.get());

    audio::Command command;
    command.type = audio::Command::Type::setSong;
    command.song = next.get();

    if (s.song != nullptr)
        s.retiring.push_back (std::move (s.song));
    s.song = std::move (next);

    post (s, command);
}

void Engine::collectRetired()
{
    for (auto& s : slots_)
    {
        while (auto* retired = s.player.takeRetiredSong())
        {
            disk_->removeSong (retired);
            s.retiring.erase (std::remove_if (s.retiring.begin(), s.retiring.end(),
                                              [retired] (const auto& song) { return song.get() == retired; }),
                              s.retiring.end());
        }
    }
    while (auto* bank = pads_.takeRetiredBank())
    {
        disk_->removePads (bank);
        padBanks_.erase (std::remove_if (padBanks_.begin(), padBanks_.end(),
                                         [bank] (const auto& b) { return b.get() == bank; }),
                         padBanks_.end());
    }
}

juce::var Engine::loadSong (const juce::var& message, Slot& s)
{
    const auto songId = text (message, "songId");
    const auto* tracks = message.getProperty ("tracks", {}).getArray();
    if (songId.isEmpty() || tracks == nullptr)
        return makeError ({}, "badMessage", "loadSong needs songId and tracks.");

    std::vector<audio::TrackSpec> specs;
    for (const auto& t : *tracks)
    {
        audio::TrackSpec spec;
        spec.id = text (t, "id").toStdString();
        spec.file = text (t, "file").toStdString();
        const auto output = t.getProperty ("output", {});
        spec.outputChannel = static_cast<int> (number (output, "channel", 0));
        spec.outputWidth = static_cast<int> (number (output, "width", 2)) == 1 ? 1 : 2;
        spec.gainDb = static_cast<float> (number (t, "gainDb", 0.0));
        spec.mute = flag (t, "mute", false);
        spec.solo = flag (t, "solo", false);
        spec.soloSafe = flag (t, "soloSafe", false);
        spec.generated = text (t, "generated").toStdString();
        spec.transposes = flag (t, "transposes", false);
        if (const auto* renders = t.getProperty ("renders", {}).getArray())
            for (const auto& r : *renders)
                if (const auto file = text (r, "file"); file.isNotEmpty())
                    spec.renders.push_back ({ file.toStdString(), static_cast<int> (number (r, "semitones", 0.0)), number (r, "tempo", 1.0) });
        specs.push_back (spec);
    }

    const auto start = static_cast<std::int64_t> (number (message, "startFrame", 0.0));
    auto song = audio::loadSong (songId.toStdString(), specs, engineRate(), start, formats_);
    if (const auto master = message.getProperty ("masterGainDb", {}); ! master.isVoid())
        song->masterGain.store (audio::decibelsToGain (static_cast<float> (static_cast<double> (master))));

    s.specs = std::move (specs);
    s.planSpec.clear(); // a new song plays as written until the app sends its plan
    s.holdSpec.clear();
    s.midiSpec.clear();
    s.callSpec.clear();
    s.pendingSwapFrame = -1;
    s.grid = {};
    s.tempo = 1.0;
    s.hasPlan = false;
    replaceSong (s, std::move (song));
    if (&s == &cur())
        armTransition(); // the new song has no plan yet: nothing to trigger from

    for (std::size_t i = 0; i < s.song->tracks.size(); ++i)
        if (s.song->tracks[i]->missing)
            push (Obj().set ("type", "fileMissing").set ("trackId", juce::String (s.specs[i].id)).set ("problem", s.song->tracks[i]->problem));

    auto reply = songInfo (s);
    reply.getDynamicObject()->setProperty ("type", "song");
    return reply;
}

juce::var Engine::unloadSong (Slot& s)
{
    s.specs.clear();
    s.planSpec.clear();
    s.holdSpec.clear();
    s.midiSpec.clear();
    s.callSpec.clear();
    s.pendingSwapFrame = -1;
    s.grid = {};
    s.tempo = 1.0;
    s.hasPlan = false;
    replaceSong (s, nullptr);
    return Obj().set ("type", "ok");
}

void Engine::reloadAtRate (double rate)
{
    if (! padBanks_.empty() && std::abs (padBanks_.back()->sampleRate - rate) > 0.5)
        openPadBank (rate); // the pads follow the device rate too; the old set fades out

    for (auto& slot : slots_)
        reloadSlotAtRate (slot, rate);
    armTransition();
}

void Engine::reloadSlotAtRate (Slot& s, double rate)
{
    if (s.song == nullptr || std::abs (s.song->sampleRate - rate) < 0.5)
        return;

    const auto oldRate = s.song->sampleRate;
    const auto position = static_cast<std::int64_t> (static_cast<double> (s.player.position()) * rate / oldRate);
    const auto master = s.song->masterGain.load();

    auto song = audio::loadSong (s.song->id, s.specs, rate, position, formats_);
    song->masterGain.store (master);
    if (s.hasPlan)
    {
        auto plan = buildPlan (rate, s);
        const auto length = plan->lengthFrames;
        song->publishPlan (std::move (plan));
        song->request.store (audio::StreamRequest::pack ({ 1, std::clamp<std::int64_t> (position, 0, length) }));
    }
    replaceSong (s, std::move (song));

    auto event = songInfo (s);
    event.getDynamicObject()->setProperty ("type", "songReloaded");
    if (&s == &nxt())
        event.getDynamicObject()->setProperty ("slot", "next");
    push (event);
}

juce::var Engine::transport (const juce::String& type, const juce::var& message)
{
    auto& s = cur();
    if (s.song == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");

    audio::Command command;
    if (type == "play")
        command.type = audio::Command::Type::play;
    else if (type == "pause")
        command.type = audio::Command::Type::pause;
    else if (type == "stop")
        command.type = audio::Command::Type::stop;
    else
    {
        command.type = audio::Command::Type::seek;
        command.frame = std::clamp<std::int64_t> (s.toOutputFrames (static_cast<std::int64_t> (number (message, "frame", 0.0))), 0, s.song->planFrames());
        command.generation = s.song->requestFrame (command.frame).generation; // start buffering now (PB-56)
    }

    post (s, command);
    if (type == "stop")
    {
        // Stop during a transition stops the transition click and leaves the next song cued (SL-32).
        transitions_.post ({});
        if (nxt().song != nullptr)
            post (nxt(), command);
        armTransition();
    }
    return Obj().set ("type", "ok");
}

// PB-52: releases the hold the playhead waits at.
juce::var Engine::go()
{
    auto& s = cur();
    if (s.song == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");
    audio::Command command;
    command.type = audio::Command::Type::go;
    post (s, command);
    return Obj().set ("type", "ok");
}

// UI-40, REL-40 (basic): the song stops with a 100 ms fade, the pads and the transition click stop, the next song stays cued.
juce::var Engine::panic()
{
    audio::Command command;
    command.type = audio::Command::Type::panic;
    for (auto* s : { &cur(), &nxt() })
        if (s->song != nullptr)
            post (*s, command);

    transitions_.post ({});
    audio::PadCommand stopPad;
    stopPad.type = audio::PadCommand::Type::play;
    stopPad.key = -1;
    pads_.post (stopPad);
    armTransition();

    // REL-40: All Notes Off and All Sound Off on every channel MIDI out has used.
    std::vector<audio::PlanMidi> off;
    const auto used = midiChannels_.load();
    for (std::uint8_t ch = 0; ch < 16; ++ch)
        if ((used & (1u << ch)) != 0)
            for (const std::uint8_t cc : { std::uint8_t { 123 }, std::uint8_t { 120 } })
                off.push_back ({ 0, { static_cast<std::uint8_t> (0xB0 | ch), cc, 0 }, 3, 0 });
    sendMidiNow (off);
    return Obj().set ("type", "ok");
}

juce::var Engine::setTrack (const juce::var& message)
{
    auto& s = cur();
    if (s.song == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");

    const auto id = text (message, "trackId").toStdString();
    for (std::size_t i = 0; i < s.specs.size(); ++i)
    {
        if (s.specs[i].id != id)
            continue;

        auto& spec = s.specs[i];
        auto& track = *s.song->tracks[i];
        if (const auto v = message.getProperty ("gainDb", {}); ! v.isVoid())
        {
            spec.gainDb = static_cast<float> (static_cast<double> (v));
            track.gain.store (audio::decibelsToGain (spec.gainDb));
        }
        if (const auto v = message.getProperty ("mute", {}); v.isBool())
            track.mute.store (spec.mute = static_cast<bool> (v));
        if (const auto v = message.getProperty ("solo", {}); v.isBool())
            track.solo.store (spec.solo = static_cast<bool> (v));
        if (const auto v = message.getProperty ("transposes", {}); v.isBool())
        {
            // The disk thread crossfades into the new key from the blocks it has not written yet, at most the
            // read-ahead (4 s) later; it is a preparation setting (PB-80 "#"), so no re-stream and no gap.
            track.transposes.store (spec.transposes = static_cast<bool> (v));
        }
        if (const auto output = message.getProperty ("output", {}); output.isObject())
        {
            spec.outputChannel = static_cast<int> (number (output, "channel", spec.outputChannel));
            spec.outputWidth = static_cast<int> (number (output, "width", spec.outputWidth)) == 1 ? 1 : 2;
            track.outputChannel.store (spec.outputChannel);
            track.outputWidth.store (spec.outputWidth);
        }
        return Obj().set ("type", "ok");
    }

    return makeError ({}, "unknownTrack", "No track with id " + juce::String (id));
}

juce::var Engine::setMaster (const juce::var& message)
{
    auto& s = cur();
    if (s.song == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");
    s.song->masterGain.store (audio::decibelsToGain (static_cast<float> (number (message, "gainDb", 0.0))));
    return Obj().set ("type", "ok");
}

std::unique_ptr<audio::Plan> Engine::buildPlan (double rate, const Slot& s)
{
    // Plan seconds become output frames (divided by the tempo); sources stay source frames.
    auto plan = std::make_unique<audio::Plan>();
    const double tempo = s.tempo;
    const auto frames = [rate, tempo] (double planSeconds) { return static_cast<std::int64_t> (std::llround (planSeconds / tempo * rate)); };
    const auto sourceFrames = [rate] (double seconds) { return static_cast<std::int64_t> (std::llround (seconds * rate)); };
    plan->tempo = tempo;
    plan->gridOrigin = s.grid.origin / tempo * rate;
    plan->beatFrames = s.grid.beat / tempo * rate;
    plan->beatsPerBar = s.grid.beatsPerBar;

    for (const auto& spec : s.planSpec)
    {
        audio::PlanSegment segment;
        segment.start = frames (spec.start);
        segment.end = frames (spec.end);
        if (segment.end <= segment.start)
            continue;

        const auto length = segment.end - segment.start;
        segment.src = spec.silent ? -1 : std::max<std::int64_t> (0, sourceFrames (spec.src));
        segment.fadeIn = std::min (length, frames (spec.fadeIn));
        segment.fadeOut = std::min (length, frames (spec.fadeOut));
        segment.click = spec.click;
        segment.key = spec.key;
        segment.songKey = spec.songKey;
        segment.pad = spec.pad;
        segment.muted.assign (s.specs.size(), 0);
        for (const auto& id : spec.mute)
            for (std::size_t i = 0; i < s.specs.size(); ++i)
                if (s.specs[i].id == id)
                    segment.muted[i] = 1;

        plan->lengthFrames = std::max (plan->lengthFrames, segment.end);
        plan->segments.push_back (std::move (segment));
    }

    for (const auto& hold : s.holdSpec)
    {
        audio::PlanHold h;
        h.frame = frames (hold.at);
        h.pad = hold.pad;
        h.padKey = hold.padKey;
        h.click = hold.click;
        plan->holds.push_back (h);
    }

    for (const auto& m : s.midiSpec)
    {
        audio::PlanMidi out;
        out.frame = frames (m.at);
        std::copy_n (m.bytes, 3, out.bytes);
        out.size = m.size;
        out.id = m.id;
        plan->midi.push_back (out);
    }

    // Each clip is read once per song and rate; a call whose file cannot be read is left out.
    for (const auto& c : s.callSpec)
    {
        const auto key = c.file + "|" + std::to_string (c.from) + "|" + std::to_string (c.length) + "|" + std::to_string (rate) + (c.normalize ? "|n" : "");
        auto& clip = s.song->clips[key];
        if (clip == nullptr)
        {
            juce::String problem;
            clip = audio::loadClip (juce::File (c.file), rate, formats_, problem, c.from, c.length);
            if (clip != nullptr && c.normalize)
            {
                float peak = 0.0f;
                for (const auto x : clip->samples)
                    peak = std::max (peak, std::abs (x));
                if (peak > 0.0f)
                    for (auto& x : clip->samples)
                        x *= 0.5011872f / peak; // −6 dBFS (AUTO-31)
            }
        }
        if (clip == nullptr)
        {
            s.song->clips.erase (key);
            continue;
        }
        plan->calls.push_back ({ frames (c.at), clip.get(), audio::decibelsToGain (static_cast<float> (c.gainDb)) });
    }

    return plan;
}

// PB-40: the arrangement as contiguous timed segments. The app builds the plan (RECompose.Core);
// the engine only executes it. The playhead stays where `position` says (plan seconds), or where it was.
juce::var Engine::setPlan (const juce::var& message, Slot& s)
{
    if (s.song == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");

    const auto* segments = message.getProperty ("segments", {}).getArray();
    if (segments == nullptr)
        return makeError ({}, "badMessage", "setPlan needs segments.");

    std::vector<PlanSegmentSpec> parsed;
    double previousEnd = 0.0;
    for (const auto& seg : *segments)
    {
        PlanSegmentSpec spec;
        spec.start = number (seg, "start", -1.0);
        spec.end = number (seg, "end", -1.0);
        if (spec.start < 0.0 || spec.end < spec.start)
            return makeError ({}, "badMessage", "A plan segment needs start and end, in order.");
        if (! parsed.empty() && std::abs (spec.start - previousEnd) > 1e-6)
            return makeError ({}, "badMessage", "Plan segments must be contiguous.");
        previousEnd = spec.end;

        const auto src = seg.getProperty ("src", {});
        spec.silent = src.isVoid() || ! (src.isInt() || src.isInt64() || src.isDouble());
        spec.src = spec.silent ? 0.0 : static_cast<double> (src);
        spec.fadeIn = std::max (0.0, number (seg, "fadeIn", 0.0));
        spec.fadeOut = std::max (0.0, number (seg, "fadeOut", 0.0));
        spec.click = flag (seg, "click", false);
        spec.key = juce::jlimit (-24, 24, static_cast<int> (number (seg, "key", 0.0)));
        spec.songKey = juce::jlimit (-1, 11, static_cast<int> (number (seg, "songKey", -1.0)));
        spec.pad = juce::jlimit (-1, 11, static_cast<int> (number (seg, "pad", -1.0)));
        if (const auto* mute = seg.getProperty ("mute", {}).getArray())
            for (const auto& id : *mute)
                if (id.isString())
                    spec.mute.push_back (id.toString().toStdString());
        parsed.push_back (std::move (spec));
    }

    // Protocol 8: where the clock waits for GO (PB-52).
    std::vector<HoldSpec> holds;
    if (const auto* list = message.getProperty ("holds", {}).getArray())
    {
        for (const auto& h : *list)
        {
            HoldSpec hold;
            hold.at = std::max (0.0, number (h, "at", 0.0));
            hold.pad = flag (h, "pad", false);
            hold.padKey = juce::jlimit (-1, 11, static_cast<int> (number (h, "padKey", -1.0)));
            hold.click = flag (h, "click", false);
            holds.push_back (hold);
        }
        std::sort (holds.begin(), holds.end(), [] (const HoldSpec& a, const HoldSpec& b) { return a.at < b.at; });
    }

    // Protocol 13: MIDI messages and spoken calls (AUTO-5, AUTO-21, AUTO-30, AUTO-31, PB-71).
    std::vector<MidiSpec> midi;
    if (const auto* list = message.getProperty ("midi", {}).getArray())
    {
        for (const auto& m : *list)
        {
            MidiSpec spec;
            spec.at = std::max (0.0, number (m, "at", 0.0));
            spec.id = static_cast<int> (number (m, "id", 0.0));
            if (const auto* bytes = m.getProperty ("bytes", {}).getArray())
                for (const auto& b : *bytes)
                    if (spec.size < 3)
                        spec.bytes[spec.size++] = static_cast<std::uint8_t> (juce::jlimit (0, 255, static_cast<int> (b)));
            midi.push_back (spec);
        }
        std::stable_sort (midi.begin(), midi.end(), [] (const MidiSpec& a, const MidiSpec& b) { return a.at < b.at; });
    }

    std::vector<CallSpec> calls;
    if (const auto* list = message.getProperty ("calls", {}).getArray())
    {
        for (const auto& c : *list)
        {
            CallSpec spec;
            spec.at = std::max (0.0, number (c, "at", 0.0));
            spec.file = text (c, "file").toStdString();
            spec.from = number (c, "from", -1.0);
            spec.length = std::max (0.0, number (c, "length", 0.0));
            spec.gainDb = number (c, "gainDb", 0.0);
            spec.normalize = flag (c, "normalize", false);
            if (! spec.file.empty())
                calls.push_back (std::move (spec));
        }
        std::stable_sort (calls.begin(), calls.end(), [] (const CallSpec& a, const CallSpec& b) { return a.at < b.at; });
    }

    const auto previousTempo = s.tempo;
    s.planSpec = std::move (parsed);
    s.holdSpec = std::move (holds);
    s.midiSpec = std::move (midi);
    s.callSpec = std::move (calls);
    s.hasPlan = true;

    // Protocol 5: the entry tempo, 50–150 % (PB-60).
    s.tempo = juce::jlimit (0.5, 1.5, number (message, "tempo", 1.0));

    // Protocol 4: the song grid the click follows (PB-70). Without it the click has no beats.
    const auto grid = message.getProperty ("grid", {});
    s.grid = {};
    if (grid.isObject())
    {
        s.grid.origin = number (grid, "origin", 0.0);
        s.grid.beat = std::max (0.0, number (grid, "beat", 0.0));
        s.grid.beatsPerBar = std::max (1, static_cast<int> (number (grid, "beatsPerBar", 4.0)));
    }

    auto plan = buildPlan (s.song->sampleRate, s);
    const auto length = plan->lengthFrames;

    // Protocol 8: while playing, `swapAt` (plan seconds) makes the plan take effect there with no seek. The frames
    // before it are the same in both plans. A swap that finds the playhead past its frame takes effect at once.
    const auto swapAt = message.getProperty ("swapAt", {});
    const bool swap = (swapAt.isDouble() || swapAt.isInt() || swapAt.isInt64()) && s.player.state() == audio::TransportState::playing
                      && ! s.player.waitingForData() && std::abs (previousTempo - s.tempo) < 1.0e-9 && s.song->plan.load() != nullptr;
    if (swap)
    {
        auto frame = static_cast<std::int64_t> (std::llround (static_cast<double> (swapAt) / s.tempo * s.song->sampleRate));
        if (s.pendingSwapFrame >= 0 && s.player.position() < s.pendingSwapFrame)
            frame = std::min (frame, s.pendingSwapFrame); // an earlier swap still waits: the new plan takes over from there
        s.pendingSwapFrame = frame;

        audio::Command command;
        command.type = audio::Command::Type::swapPlan;
        command.plan = s.song->publishSwap (std::move (plan), frame);
        command.frame = frame;
        post (s, command);
        if (&s == &cur())
            armTransition();
        return Obj().set ("type", "plan").set ("frames", static_cast<juce::int64> (s.toPlanFrames (length))).set ("swapped", true);
    }

    s.pendingSwapFrame = -1;
    const auto position = message.getProperty ("position", {});
    const auto wanted = position.isVoid() ? s.player.position()
                                          : static_cast<std::int64_t> (std::llround (number (message, "position", 0.0) / s.tempo * s.song->sampleRate));
    const auto frame = std::clamp<std::int64_t> (wanted, 0, length);

    audio::Command command;
    command.type = audio::Command::Type::setPlan;
    command.plan = s.song->publishPlan (std::move (plan)); // before the request, so the disk thread sees both
    command.frame = frame;
    command.generation = s.song->requestFrame (frame).generation;
    post (s, command);
    if (&s == &cur())
        armTransition(); // the trigger is a frame of this plan

    return Obj().set ("type", "plan").set ("frames", static_cast<juce::int64> (s.toPlanFrames (length)));
}

juce::var Engine::getState()
{
    return Obj()
        .set ("type", "state")
        .set ("song", songInfo (cur()))
        .set ("next", songInfo (nxt()))
        .set ("device", deviceInfo())
        .set ("snapshot", snapshot());
}

juce::var Engine::renderOffline (const juce::var& message)
{
    auto& s = cur();
    if (s.song == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");

    const int frames = juce::jlimit (0, 48000 * 60 * 20, static_cast<int> (number (message, "frames", 0.0)));
    const int channels = juce::jlimit (1, kMaxOutputChannels, static_cast<int> (number (message, "channels", 2.0)));
    const auto path = text (message, "path");

    const bool hadDevice = callbackActive_.load();
    if (hadDevice)
        devices_.removeAudioCallback (this);
    const bool hadSilent = silent_.running(); // one renderer at a time
    if (hadSilent)
        silent_.stop();

    for (auto& slot : slots_)
        slot.player.prepare (s.song->sampleRate, 512, channels);
    transitions_.prepare (s.song->sampleRate);
    pads_.prepare (s.song->sampleRate);
    juce::AudioBuffer<float> buffer (channels, frames);
    std::vector<float*> pointers (static_cast<std::size_t> (channels));

    for (int done = 0; done < frames;)
    {
        const int n = std::min (512, frames - done);
        for (int c = 0; c < channels; ++c)
            pointers[static_cast<std::size_t> (c)] = buffer.getWritePointer (c, done);
        processBlock (pointers.data(), channels, n, true);
        done += n;
    }
    collectRetired();

    juce::Array<juce::var> peaks, rms;
    for (int c = 0; c < channels; ++c)
    {
        peaks.add (buffer.getMagnitude (c, 0, frames));
        rms.add (frames > 0 ? buffer.getRMSLevel (c, 0, frames) : 0.0f);
    }

    if (path.isNotEmpty())
    {
        const juce::File file (path);
        file.deleteFile();
        std::unique_ptr<juce::OutputStream> stream = std::make_unique<juce::FileOutputStream> (file);
        juce::WavAudioFormat wav;
        const auto options = juce::AudioFormatWriterOptions{}
                                 .withSampleRate (s.song->sampleRate)
                                 .withNumChannels (channels)
                                 .withBitsPerSample (32)
                                 .withSampleFormat (juce::AudioFormatWriterOptions::SampleFormat::floatingPoint);
        if (auto writer = wav.createWriterFor (stream, options))
            writer->writeFromAudioSampleBuffer (buffer, 0, frames);
    }

    if (hadDevice)
        devices_.addAudioCallback (this);
    if (hadSilent)
        silent_.start (s.song->sampleRate, channels);
    seenCallbacks_ = callbacks_.load(); // the device was off on purpose: no stall
    seenCallbacksAtMs_ = juce::Time::getMillisecondCounter();

    return Obj()
        .set ("type", "rendered")
        .set ("frames", frames)
        .set ("channels", channels)
        .set ("peak", peaks)
        .set ("rms", rms)
        .set ("snapshot", snapshot());
}

//==============================================================================
juce::var Engine::snapshot()
{
    auto& s = cur();
    juce::Array<juce::var> meters;
    float master[2] { 0.0f, 0.0f };
    juce::var songId;
    juce::int64 length = 0, planLength = 0;
    bool buffered = false;
    double rate = engineRate();

    if (s.song != nullptr)
    {
        songId = juce::String (s.song->id);
        length = s.song->lengthFrames;
        planLength = s.toPlanFrames (s.song->planFrames());
        buffered = s.song->isBuffered();
        rate = s.song->sampleRate;
        for (std::size_t i = 0; i < s.song->tracks.size(); ++i)
            meters.add (Obj().set ("id", juce::String (s.specs[i].id)).set ("peak", s.song->tracks[i]->peak.exchange (0.0f)));
        master[0] = s.song->masterPeak[0].exchange (0.0f);
        master[1] = s.song->masterPeak[1].exchange (0.0f);
    }

    auto* device = devices_.getCurrentAudioDevice();

    juce::Array<juce::var> masterArray;
    masterArray.add (master[0]);
    masterArray.add (master[1]);

    return Obj()
        .set ("type", "snapshot")
        .set ("state", stateName (s.player.state()))
        .set ("frame", static_cast<juce::int64> (s.toPlanFrames (s.player.position())))
        .set ("t", static_cast<juce::int64> (s.player.positionTimeNs()))
        .set ("latency", static_cast<juce::int64> (s.toPlanFrames (outputLatency_.load())))
        .set ("sampleRate", rate)
        .set ("songId", songId)
        .set ("lengthFrames", length)
        .set ("planFrames", planLength)
        .set ("buffered", buffered)
        .set ("waiting", s.player.waitingForData())
        .set ("holding", s.player.holding())
        .set ("holdSec", s.player.holdFrames() > 0 && s.song != nullptr ? static_cast<double> (s.player.holdFrames()) / s.song->sampleRate : 0.0)
        .set ("starved", static_cast<juce::int64> (s.player.starvedBlocks()))
        .set ("xruns", device != nullptr ? device->getXRunCount() : 0)
        .set ("cpu", devices_.getCpuUsage())
        .set ("deviceOpen", callbackActive_.load())
        .set ("route", routeName (route_))
        .set ("lostSec", route_ == Route::silent || route_ == Route::fallback ? static_cast<double> (juce::Time::getMillisecondCounter() - lostAtMs_) / 1000.0 : 0.0)
        .set ("mainBack", mainBack_)
        .set ("voice", voiceChannel_ >= 0 ? Obj().set ("on", voiceOn_.load()).set ("level", voiceLevelDb_).set ("hearing", voice_.hearing()) : juce::var())
        .set ("meters", meters)
        .set ("master", masterArray)
        .set ("pad", Obj().set ("key", pads_.key() >= 0 ? juce::var (pads_.key()) : juce::var()).set ("level", pads_.level()))
        .set ("next", nxt().song != nullptr
                          ? Obj().set ("songId", juce::String (nxt().song->id)).set ("buffered", nxt().song->isBuffered()).set ("state", stateName (nxt().player.state()))
                          : juce::var())
        .set ("transition", transitionName());
}

void Engine::timerCallback()
{
    checkDevice (false);
    checkXruns();
    checkMidiInputs();

    if (! processing())
        for (auto& slot : slots_)
            slot.player.processCommandsOnly();
    collectRetired();

    // The next song took over (a segue or a crossfade), or the current one reached its end (SL-30, PB-25).
    if (transitions_.takeHandover())
        promote();
    if (cur().player.takeEnded())
    {
        const auto phase = transitions_.phase();
        if ((phase == audio::Transitioner::Phase::idle || phase == audio::Transitioner::Phase::armed) && cur().song != nullptr)
            push (Obj().set ("type", "songEnded").set ("songId", juce::String (cur().song->id)));
    }
    nxt().player.takeEnded(); // the song that has just handed over
    auto& s = cur();

    const bool playing = isPlaying();
    if (playing && getTimerInterval() != 16)
        startTimer (16); // display rate while something moves (PB-36)
    else if (! playing && getTimerInterval() != 100)
        startTimer (100);

    // While playing: every tick. Otherwise only when something changed, plus a slow heartbeat.
    voiceLevelDb_ = voice_.takeLevelDb();
    const auto key = (voiceChannel_ >= 0 ? juce::String (juce::roundToInt (voiceLevelDb_ / 6.0f)) + (voice_.hearing() ? "h" : "") : juce::String())
                   + stateName (s.player.state()).toString() + ":" + juce::String (s.player.position()) + ":"
                   + (s.song != nullptr ? juce::String (s.song->id) + (s.song->isBuffered() ? "b" : "") : juce::String())
                   + (callbackActive_.load() ? "d" : "") + routeName (route_);
    ++idleTicks_;
    if (playing || key != lastSnapshotKey_ || idleTicks_ >= 10)
    {
        lastSnapshotKey_ = key;
        idleTicks_ = 0;
        push (snapshot());
    }
}

//==============================================================================
// Transitions (protocol 7, SL-20, SL-30). The app describes the transition out of the current song; the
// audio thread starts the next song on the exact sample.
namespace
{
    const char* kindName (audio::Transitioner::Phase phase)
    {
        switch (phase)
        {
            case audio::Transitioner::Phase::armed: return "armed";
            case audio::Transitioner::Phase::segueGap: return "segue";
            case audio::Transitioner::Phase::padWait: return "waiting";
            case audio::Transitioner::Phase::handing: return "handing";
            case audio::Transitioner::Phase::idle: break;
        }
        return "none";
    }
} // namespace

juce::String Engine::transitionName() const
{
    return kindName (transitions_.phase());
}

void Engine::armTransition()
{
    const auto& s = cur();
    audio::TransitionCommand command;
    command.type = audio::TransitionCommand::Type::arm;
    command.kind = s.song != nullptr ? transitionSpec_.kind : audio::TransitionCommand::Kind::none;

    if (command.kind != audio::TransitionCommand::Kind::none)
    {
        const auto rate = s.song->sampleRate;
        const auto frames = [rate] (double seconds) { return static_cast<std::int64_t> (std::llround (seconds * rate)); };
        command.trigger = static_cast<std::int64_t> (std::llround (transitionSpec_.atSec / s.tempo * rate)); // plan seconds to output frames
        command.gap = frames (transitionSpec_.gapSec);
        command.fade = frames (transitionSpec_.crossfadeSec);
        command.padKey = transitionSpec_.padKey;
        command.fadePadOut = transitionSpec_.fadePadOut;
        command.autoStart = transitionSpec_.autoStartSec >= 0.0 ? frames (transitionSpec_.autoStartSec) : -1;
        command.clickChannel = transitionSpec_.clickChannel;
        command.clickWidth = transitionSpec_.clickWidth;

        if (command.kind == audio::TransitionCommand::Kind::pad && transitionSpec_.click && transitionSpec_.clickBpm > 0.0)
        {
            // The transition click runs in real time at its own tempo, from the trigger.
            auto plan = std::make_unique<audio::Plan>();
            audio::PlanSegment segment;
            segment.start = 0;
            segment.end = std::int64_t (1) << 50;
            segment.src = -1;
            segment.click = true;
            plan->segments.push_back (segment);
            plan->lengthFrames = segment.end;
            plan->gridOrigin = 0.0;
            plan->beatFrames = 60.0 / transitionSpec_.clickBpm * rate;
            plan->beatsPerBar = std::max (1, transitionSpec_.beatsPerBar);
            command.click = true;
            command.clickPlan = plan.get();
            transitionPlans_.push_back (std::move (plan)); // the audio thread may hold an earlier one: keep them all (a few hundred bytes each)
        }
    }

    transitions_.post (command);
}

juce::var Engine::setTransition (const juce::var& message)
{
    const auto type = text (message, "kind");
    TransitionSpec spec;
    if (type == "segue")
        spec.kind = audio::TransitionCommand::Kind::segue;
    else if (type == "crossfade")
        spec.kind = audio::TransitionCommand::Kind::crossfade;
    else if (type == "pad")
        spec.kind = audio::TransitionCommand::Kind::pad;
    else if (type != "none" && type != "stop" && type != "carryOver")
        return makeError ({}, "badMessage", "Unknown transition type: " + type);

    spec.atSec = std::max (0.0, number (message, "at", 0.0));
    spec.gapSec = std::max (0.0, number (message, "gapSec", 0.0));
    spec.crossfadeSec = std::max (0.0, number (message, "crossfadeSec", 0.0));
    spec.autoStartSec = number (message, "autoStartSec", -1.0);
    if (const auto padInfo = message.getProperty ("pad", {}); padInfo.isObject())
    {
        const auto key = padInfo.getProperty ("key", {});
        spec.padKey = key.isInt() || key.isInt64() || key.isDouble() ? juce::jlimit (-1, audio::kPadKeys - 1, static_cast<int> (key)) : -1;
        spec.fadePadOut = flag (padInfo, "fadeOut", true);
        if (const auto click = padInfo.getProperty ("click", {}); click.isObject())
        {
            spec.click = true;
            spec.clickBpm = number (click, "bpm", 120.0);
            spec.beatsPerBar = static_cast<int> (number (click, "beatsPerBar", 4.0));
            spec.clickChannel = static_cast<int> (number (click, "channel", 0.0));
            spec.clickWidth = static_cast<int> (number (click, "width", 2.0)) == 1 ? 1 : 2;
        }
    }

    transitionSpec_ = spec;
    armTransition();
    return Obj().set ("type", "ok");
}

juce::var Engine::startNext (const juce::var& message)
{
    if (nxt().song == nullptr)
        return makeError ({}, "noSong", "No next song is loaded.");
    audio::TransitionCommand command;
    command.type = audio::TransitionCommand::Type::startNext;
    const auto retune = message.getProperty ("retuneTo", {});
    command.retune = retune.isInt() || retune.isInt64() || retune.isDouble() ? juce::jlimit (-1, audio::kPadKeys - 1, static_cast<int> (retune)) : -1;
    transitions_.post (command);
    disk_->wake();
    return Obj().set ("type", "ok");
}

// The next song becomes the current one: after a segue or a crossfade, or when the app moves on from a stopped song (SL-12).
void Engine::promote()
{
    if (nxt().song == nullptr)
        return;
    cur_.store (1 - cur_.load());
    transitionSpec_ = {};
    armTransition();
    auto& s = cur();
    push (Obj().set ("type", "songChanged").set ("songId", juce::String (s.song->id)));
}

juce::var Engine::promoteNext()
{
    if (nxt().song == nullptr)
        return makeError ({}, "noSong", "No next song is loaded.");
    if (cur().player.state() == audio::TransportState::playing)
        return makeError ({}, "badMessage", "The current song is playing.");
    promote();
    auto reply = songInfo (cur());
    reply.getDynamicObject()->setProperty ("type", "song");
    return reply;
}

//==============================================================================
void Engine::audioDeviceIOCallbackWithContext (const float* const* inputs, int numInputs, float* const* outputs, int numOutputs,
                                               int numSamples, const juce::AudioIODeviceCallbackContext&)
{
    // Only the voice channel is open, so it is the first (and only) input.
    if (numInputs > 0 && inputs[0] != nullptr && voiceOn_.load (std::memory_order_relaxed))
        voice_.write (inputs[0], numSamples);

    if (silent_.running())
    {
        // The silent clock still owns the graph (the device started before it was told to stop): one renderer at a time.
        for (int ch = 0; ch < numOutputs; ++ch)
            if (outputs[ch] != nullptr)
                std::memset (outputs[ch], 0, sizeof (float) * static_cast<std::size_t> (numSamples));
        return;
    }
    processBlock (outputs, numOutputs, numSamples, false);
    callbacks_.fetch_add (1, std::memory_order_relaxed);
    lastCallbackNs_.store (audio::SilentClock::steadyNowNs(), std::memory_order_relaxed);
}

void Engine::processBlock (float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept
{
    for (int ch = 0; ch < numOutputs; ++ch)
        if (outputs[ch] != nullptr)
            std::memset (outputs[ch], 0, sizeof (float) * static_cast<std::size_t> (numSamples));

    // Both songs play into the same buffers; the transition runner starts the next one on its sample.
    transitions_.process (cur().player, nxt().player, pads_, outputs, numOutputs, numSamples, offline);
    pads_.process (outputs, numOutputs, numSamples, offline);
    clipPlayer_.process (outputs, numOutputs, numSamples);
}

void Engine::audioDeviceAboutToStart (juce::AudioIODevice* device)
{
    silent_.stop(); // the device takes over from the clock at the frame it reached
    const auto rate = device->getCurrentSampleRate();
    deviceChannels_.store (std::max (2, device->getActiveOutputChannels().getHighestBit() + 1));
    for (auto& slot : slots_)
        slot.player.prepare (rate, device->getCurrentBufferSizeSamples(), device->getActiveOutputChannels().getHighestBit() + 1);
    transitions_.prepare (rate);
    pads_.prepare (rate);
    deviceRate_.store (rate);
    voice_.prepare (rate);
    voiceOn_.store (voiceChannel_ >= 0 && device->getActiveInputChannels().countNumberOfSetBits() > 0);
    outputLatency_.store (device->getOutputLatencyInSamples());
    callbackActive_.store (true);

    for (const auto& slot : slots_)
    {
        if (slot.song != nullptr && std::abs (slot.song->sampleRate - rate) > 0.5)
        {
            juce::MessageManager::callAsync ([this, rate] { reloadAtRate (rate); });
            break;
        }
    }
}

void Engine::audioDeviceStopped()
{
    callbackActive_.store (false);
}

void Engine::audioDeviceError (const juce::String& message)
{
    push (Obj().set ("type", "deviceError").set ("message", message));
}

void Engine::changeListenerCallback (juce::ChangeBroadcaster*)
{
    push (Obj().set ("type", "deviceChanged").set ("device", deviceInfo()));
    checkDevice (true); // a device came or went: look now rather than at the next second
}
} // namespace rec
