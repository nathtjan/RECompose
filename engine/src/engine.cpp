// SPDX-License-Identifier: AGPL-3.0-or-later
#include "engine.h"

#include "message_handler.h"

#include <cmath>

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
    devices_.addChangeListener (this);
    startTimerHz (10);
}

Engine::~Engine()
{
    stopTimer();
    devices_.removeChangeListener (this);
    devices_.removeAudioCallback (this);
    devices_.closeAudioDevice();
    disk_.reset(); // the disk thread stops before any song is freed
    song_.reset();
    retiring_.clear();
}

//==============================================================================
juce::var Engine::handle (const juce::String& type, const juce::var& message)
{
    if (type == "listDevices")  return listDevices();
    if (type == "setDevice")    return setDevice (message);
    if (type == "loadSong")     return loadSong (message);
    if (type == "unloadSong")   return unloadSong();
    if (type == "play" || type == "pause" || type == "stop" || type == "seek")
        return transport (type, message);
    if (type == "setTrack")     return setTrack (message);
    if (type == "setMaster")    return setMaster (message);
    if (type == "getState")     return getState();
    if (type == "renderOffline") return renderOffline (message);

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

double Engine::engineRate()
{
    const auto rate = deviceRate_.load();
    return rate > 0.0 ? rate : kDefaultEngineRate;
}

void Engine::post (const audio::Command& command)
{
    player_.post (command);
    disk_->wake();
    if (! callbackActive_.load())
    {
        player_.processCommandsOnly();
        collectRetired();
    }
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
        types.add (Obj().set ("name", type->getTypeName()).set ("devices", toArray (type->getDeviceNames (false))));
    }

    return Obj().set ("type", "devices").set ("types", types).set ("current", deviceInfo());
}

juce::var Engine::setDevice (const juce::var& message)
{
    if (! deviceInitialised_)
    {
        const auto error = devices_.initialise (0, kMaxOutputChannels, nullptr, true);
        deviceInitialised_ = true;
        devices_.addAudioCallback (this);
        juce::ignoreUnused (error);
    }

    const auto type = text (message, "deviceType");
    const auto name = text (message, "name");

    if (type.isNotEmpty() && type != devices_.getCurrentAudioDeviceType())
        devices_.setCurrentAudioDeviceType (type, true);

    auto setup = devices_.getAudioDeviceSetup();
    if (name.isNotEmpty())
        setup.outputDeviceName = name;
    setup.inputDeviceName = {};
    setup.useDefaultInputChannels = false;
    setup.inputChannels.clear();
    setup.useDefaultOutputChannels = false;
    setup.outputChannels.clear();
    setup.outputChannels.setRange (0, kMaxOutputChannels, true);
    if (const auto rate = number (message, "sampleRate", 0.0); rate > 0.0)
        setup.sampleRate = rate;
    if (const auto size = static_cast<int> (number (message, "bufferSize", 0.0)); size > 0)
        setup.bufferSize = size;

    const auto error = devices_.setAudioDeviceSetup (setup, true);
    if (error.isNotEmpty() || devices_.getCurrentAudioDevice() == nullptr)
        return makeError ({}, "deviceError", error.isNotEmpty() ? error : juce::String ("No audio device could be opened."));

    return Obj().set ("type", "device").set ("device", deviceInfo());
}

//==============================================================================
juce::var Engine::songInfo()
{
    if (song_ == nullptr)
        return {};

    juce::Array<juce::var> tracks;
    for (std::size_t i = 0; i < song_->tracks.size(); ++i)
    {
        const auto& track = *song_->tracks[i];
        const auto& spec = specs_[i];
        tracks.add (Obj()
                        .set ("id", juce::String (spec.id))
                        .set ("channels", track.channels)
                        .set ("sourceRate", track.sourceRate)
                        .set ("sourceFrames", static_cast<juce::int64> (track.sourceFrames))
                        .set ("missing", track.missing)
                        .set ("problem", track.problem)
                        .set ("gainDb", spec.gainDb)
                        .set ("mute", spec.mute)
                        .set ("solo", spec.solo)
                        .set ("output", Obj().set ("channel", spec.outputChannel).set ("width", spec.outputWidth)));
    }

    return Obj()
        .set ("songId", juce::String (song_->id))
        .set ("sampleRate", song_->sampleRate)
        .set ("lengthFrames", static_cast<juce::int64> (song_->lengthFrames))
        .set ("masterGainDb", gainToDecibels (song_->masterGain.load()))
        .set ("tracks", tracks);
}

void Engine::replaceSong (std::unique_ptr<audio::Song> next)
{
    if (next != nullptr)
        disk_->addSong (next.get());

    audio::Command command;
    command.type = audio::Command::Type::setSong;
    command.song = next.get();

    if (song_ != nullptr)
        retiring_.push_back (std::move (song_));
    song_ = std::move (next);

    post (command);
}

void Engine::collectRetired()
{
    while (auto* retired = player_.takeRetiredSong())
    {
        disk_->removeSong (retired);
        retiring_.erase (std::remove_if (retiring_.begin(), retiring_.end(),
                                         [retired] (const auto& s) { return s.get() == retired; }),
                         retiring_.end());
    }
}

juce::var Engine::loadSong (const juce::var& message)
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
        specs.push_back (spec);
    }

    const auto start = static_cast<std::int64_t> (number (message, "startFrame", 0.0));
    auto song = audio::loadSong (songId.toStdString(), specs, engineRate(), start, formats_);
    if (const auto master = message.getProperty ("masterGainDb", {}); ! master.isVoid())
        song->masterGain.store (audio::decibelsToGain (static_cast<float> (static_cast<double> (master))));

    specs_ = std::move (specs);
    replaceSong (std::move (song));

    for (std::size_t i = 0; i < song_->tracks.size(); ++i)
        if (song_->tracks[i]->missing)
            push (Obj().set ("type", "fileMissing").set ("trackId", juce::String (specs_[i].id)).set ("problem", song_->tracks[i]->problem));

    auto reply = songInfo();
    reply.getDynamicObject()->setProperty ("type", "song");
    return reply;
}

juce::var Engine::unloadSong()
{
    specs_.clear();
    replaceSong (nullptr);
    return Obj().set ("type", "ok");
}

void Engine::reloadAtRate (double rate)
{
    if (song_ == nullptr || std::abs (song_->sampleRate - rate) < 0.5)
        return;

    const auto oldRate = song_->sampleRate;
    const auto position = static_cast<std::int64_t> (static_cast<double> (player_.position()) * rate / oldRate);
    const auto master = song_->masterGain.load();

    auto song = audio::loadSong (song_->id, specs_, rate, position, formats_);
    song->masterGain.store (master);
    replaceSong (std::move (song));

    auto event = songInfo();
    event.getDynamicObject()->setProperty ("type", "songReloaded");
    push (event);
}

juce::var Engine::transport (const juce::String& type, const juce::var& message)
{
    if (song_ == nullptr)
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
        command.frame = std::clamp<std::int64_t> (static_cast<std::int64_t> (number (message, "frame", 0.0)), 0, song_->lengthFrames);
        command.generation = song_->requestFrame (command.frame).generation; // start buffering now (PB-56)
    }

    post (command);
    return Obj().set ("type", "ok");
}

juce::var Engine::setTrack (const juce::var& message)
{
    if (song_ == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");

    const auto id = text (message, "trackId").toStdString();
    for (std::size_t i = 0; i < specs_.size(); ++i)
    {
        if (specs_[i].id != id)
            continue;

        auto& spec = specs_[i];
        auto& track = *song_->tracks[i];
        if (const auto v = message.getProperty ("gainDb", {}); ! v.isVoid())
        {
            spec.gainDb = static_cast<float> (static_cast<double> (v));
            track.gain.store (audio::decibelsToGain (spec.gainDb));
        }
        if (const auto v = message.getProperty ("mute", {}); v.isBool())
            track.mute.store (spec.mute = static_cast<bool> (v));
        if (const auto v = message.getProperty ("solo", {}); v.isBool())
            track.solo.store (spec.solo = static_cast<bool> (v));
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
    if (song_ == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");
    song_->masterGain.store (audio::decibelsToGain (static_cast<float> (number (message, "gainDb", 0.0))));
    return Obj().set ("type", "ok");
}

juce::var Engine::getState()
{
    return Obj()
        .set ("type", "state")
        .set ("song", songInfo())
        .set ("device", deviceInfo())
        .set ("snapshot", snapshot());
}

juce::var Engine::renderOffline (const juce::var& message)
{
    if (song_ == nullptr)
        return makeError ({}, "noSong", "No song is loaded.");

    const int frames = juce::jlimit (0, 48000 * 60 * 20, static_cast<int> (number (message, "frames", 0.0)));
    const int channels = juce::jlimit (1, kMaxOutputChannels, static_cast<int> (number (message, "channels", 2.0)));
    const auto path = text (message, "path");

    const bool hadDevice = callbackActive_.load();
    if (hadDevice)
        devices_.removeAudioCallback (this);

    player_.prepare (song_->sampleRate, 512, channels);
    juce::AudioBuffer<float> buffer (channels, frames);
    std::vector<float*> pointers (static_cast<std::size_t> (channels));

    for (int done = 0; done < frames;)
    {
        const int n = std::min (512, frames - done);
        for (int c = 0; c < channels; ++c)
            pointers[static_cast<std::size_t> (c)] = buffer.getWritePointer (c, done);
        player_.process (pointers.data(), channels, n, true);
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
                                 .withSampleRate (song_->sampleRate)
                                 .withNumChannels (channels)
                                 .withBitsPerSample (32)
                                 .withSampleFormat (juce::AudioFormatWriterOptions::SampleFormat::floatingPoint);
        if (auto writer = wav.createWriterFor (stream, options))
            writer->writeFromAudioSampleBuffer (buffer, 0, frames);
    }

    if (hadDevice)
        devices_.addAudioCallback (this);

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
    juce::Array<juce::var> meters;
    float master[2] { 0.0f, 0.0f };
    juce::var songId;
    juce::int64 length = 0;
    bool buffered = false;
    double rate = engineRate();

    if (song_ != nullptr)
    {
        songId = juce::String (song_->id);
        length = song_->lengthFrames;
        buffered = song_->isBuffered();
        rate = song_->sampleRate;
        for (std::size_t i = 0; i < song_->tracks.size(); ++i)
            meters.add (Obj().set ("id", juce::String (specs_[i].id)).set ("peak", song_->tracks[i]->peak.exchange (0.0f)));
        master[0] = song_->masterPeak[0].exchange (0.0f);
        master[1] = song_->masterPeak[1].exchange (0.0f);
    }

    auto* device = devices_.getCurrentAudioDevice();

    juce::Array<juce::var> masterArray;
    masterArray.add (master[0]);
    masterArray.add (master[1]);

    return Obj()
        .set ("type", "snapshot")
        .set ("state", stateName (player_.state()))
        .set ("frame", static_cast<juce::int64> (player_.position()))
        .set ("t", static_cast<juce::int64> (player_.positionTimeNs()))
        .set ("latency", outputLatency_.load())
        .set ("sampleRate", rate)
        .set ("songId", songId)
        .set ("lengthFrames", length)
        .set ("buffered", buffered)
        .set ("waiting", player_.waitingForData())
        .set ("starved", static_cast<juce::int64> (player_.starvedBlocks()))
        .set ("xruns", device != nullptr ? device->getXRunCount() : 0)
        .set ("cpu", devices_.getCpuUsage())
        .set ("deviceOpen", callbackActive_.load())
        .set ("meters", meters)
        .set ("master", masterArray);
}

void Engine::timerCallback()
{
    if (! callbackActive_.load())
        player_.processCommandsOnly();
    collectRetired();

    const bool playing = isPlaying();
    if (playing && getTimerInterval() != 16)
        startTimer (16); // display rate while something moves (PB-36)
    else if (! playing && getTimerInterval() != 100)
        startTimer (100);

    // While playing: every tick. Otherwise only when something changed, plus a slow heartbeat.
    const auto key = stateName (player_.state()).toString() + ":" + juce::String (player_.position()) + ":"
                   + (song_ != nullptr ? juce::String (song_->id) + (song_->isBuffered() ? "b" : "") : juce::String())
                   + (callbackActive_.load() ? "d" : "");
    ++idleTicks_;
    if (playing || key != lastSnapshotKey_ || idleTicks_ >= 10)
    {
        lastSnapshotKey_ = key;
        idleTicks_ = 0;
        push (snapshot());
    }
}

//==============================================================================
void Engine::audioDeviceIOCallbackWithContext (const float* const*, int, float* const* outputs, int numOutputs,
                                               int numSamples, const juce::AudioIODeviceCallbackContext&)
{
    player_.process (outputs, numOutputs, numSamples, false);
}

void Engine::audioDeviceAboutToStart (juce::AudioIODevice* device)
{
    const auto rate = device->getCurrentSampleRate();
    player_.prepare (rate, device->getCurrentBufferSizeSamples(), device->getActiveOutputChannels().getHighestBit() + 1);
    deviceRate_.store (rate);
    outputLatency_.store (device->getOutputLatencyInSamples());
    callbackActive_.store (true);

    if (song_ != nullptr && std::abs (song_->sampleRate - rate) > 0.5)
        juce::MessageManager::callAsync ([this, rate] { reloadAtRate (rate); });
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
}
} // namespace rec
