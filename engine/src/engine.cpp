// SPDX-License-Identifier: AGPL-3.0-or-later
#include "engine.h"

#include "message_handler.h"

#include <algorithm>
#include <cmath>
#include <cstring>

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
        slot.player.setPads (&pads_);
    devices_.addChangeListener (this);
    startTimerHz (10);
}

Engine::~Engine()
{
    stopTimer();
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
    if (type == "loadSong")     return loadSong (message, slotFor (message));
    if (type == "unloadSong")   return unloadSong (slotFor (message));
    if (type == "setTransition") return setTransition (message);
    if (type == "startNext")    return startNext (message);
    if (type == "promoteNext")  return promoteNext();
    if (type == "play" || type == "pause" || type == "stop" || type == "seek")
        return transport (type, message);
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
    if (! callbackActive_.load())
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

    if (! callbackActive_.load())
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
        specs.push_back (spec);
    }

    const auto start = static_cast<std::int64_t> (number (message, "startFrame", 0.0));
    auto song = audio::loadSong (songId.toStdString(), specs, engineRate(), start, formats_);
    if (const auto master = message.getProperty ("masterGainDb", {}); ! master.isVoid())
        song->masterGain.store (audio::decibelsToGain (static_cast<float> (static_cast<double> (master))));

    s.specs = std::move (specs);
    s.planSpec.clear(); // a new song plays as written until the app sends its plan
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

std::unique_ptr<audio::Plan> Engine::buildPlan (double rate, const Slot& s) const
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
    for (const auto& s : *segments)
    {
        PlanSegmentSpec spec;
        spec.start = number (s, "start", -1.0);
        spec.end = number (s, "end", -1.0);
        if (spec.start < 0.0 || spec.end < spec.start)
            return makeError ({}, "badMessage", "A plan segment needs start and end, in order.");
        if (! parsed.empty() && std::abs (spec.start - previousEnd) > 1e-6)
            return makeError ({}, "badMessage", "Plan segments must be contiguous.");
        previousEnd = spec.end;

        const auto src = s.getProperty ("src", {});
        spec.silent = src.isVoid() || ! (src.isInt() || src.isInt64() || src.isDouble());
        spec.src = spec.silent ? 0.0 : static_cast<double> (src);
        spec.fadeIn = std::max (0.0, number (s, "fadeIn", 0.0));
        spec.fadeOut = std::max (0.0, number (s, "fadeOut", 0.0));
        spec.click = flag (s, "click", false);
        spec.key = juce::jlimit (-24, 24, static_cast<int> (number (s, "key", 0.0)));
        spec.songKey = juce::jlimit (-1, 11, static_cast<int> (number (s, "songKey", -1.0)));
        spec.pad = juce::jlimit (-1, 11, static_cast<int> (number (s, "pad", -1.0)));
        if (const auto* mute = s.getProperty ("mute", {}).getArray())
            for (const auto& id : *mute)
                if (id.isString())
                    spec.mute.push_back (id.toString().toStdString());
        parsed.push_back (std::move (spec));
    }

    s.planSpec = std::move (parsed);
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
        .set ("starved", static_cast<juce::int64> (s.player.starvedBlocks()))
        .set ("xruns", device != nullptr ? device->getXRunCount() : 0)
        .set ("cpu", devices_.getCpuUsage())
        .set ("deviceOpen", callbackActive_.load())
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
    if (! callbackActive_.load())
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
    const auto key = stateName (s.player.state()).toString() + ":" + juce::String (s.player.position()) + ":"
                   + (s.song != nullptr ? juce::String (s.song->id) + (s.song->isBuffered() ? "b" : "") : juce::String())
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
void Engine::audioDeviceIOCallbackWithContext (const float* const*, int, float* const* outputs, int numOutputs,
                                               int numSamples, const juce::AudioIODeviceCallbackContext&)
{
    processBlock (outputs, numOutputs, numSamples, false);
}

void Engine::processBlock (float* const* outputs, int numOutputs, int numSamples, bool offline) noexcept
{
    for (int ch = 0; ch < numOutputs; ++ch)
        if (outputs[ch] != nullptr)
            std::memset (outputs[ch], 0, sizeof (float) * static_cast<std::size_t> (numSamples));

    // Both songs play into the same buffers; the transition runner starts the next one on its sample.
    transitions_.process (cur().player, nxt().player, pads_, outputs, numOutputs, numSamples, offline);
    pads_.process (outputs, numOutputs, numSamples, offline);
}

void Engine::audioDeviceAboutToStart (juce::AudioIODevice* device)
{
    const auto rate = device->getCurrentSampleRate();
    for (auto& slot : slots_)
        slot.player.prepare (rate, device->getCurrentBufferSizeSamples(), device->getActiveOutputChannels().getHighestBit() + 1);
    transitions_.prepare (rate);
    pads_.prepare (rate);
    deviceRate_.store (rate);
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
}
} // namespace rec
