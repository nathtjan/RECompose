// SPDX-License-Identifier: AGPL-3.0-or-later
#include "message_handler.h"

#include "protocol.h"

namespace rec
{
namespace
{
    juce::var object() { return juce::var (new juce::DynamicObject()); }

    juce::var reply (const char* type, const juce::var& requestId)
    {
        auto message = object();
        message.getDynamicObject()->setProperty ("type", type);
        if (! requestId.isVoid())
            message.getDynamicObject()->setProperty ("re", requestId);
        return message;
    }
} // namespace

std::string toJson (const juce::var& value)
{
    const auto options = juce::JSON::FormatOptions{}.withSpacing (juce::JSON::Spacing::none);
    return juce::JSON::toString (value, options).toStdString();
}

juce::var makeError (const juce::var& requestId, const char* code, const juce::String& message)
{
    auto error = reply ("error", requestId);
    error.getDynamicObject()->setProperty ("code", code);
    error.getDynamicObject()->setProperty ("message", message);
    return error;
}

MessageHandler::MessageHandler (EngineIdentity identity) : identity_ (std::move (identity)) {}

HandlerResult MessageHandler::handle (const std::string& payload)
{
    HandlerResult result;

    juce::var message;
    const auto parsed = juce::JSON::parse (juce::String::fromUTF8 (payload.data(), static_cast<int> (payload.size())), message);

    if (parsed.failed() || ! message.isObject())
    {
        result.replies.push_back (toJson (makeError ({}, errors::badMessage, "Expected a JSON object.")));
        return result;
    }

    const auto id = message.getProperty ("id", {});
    const auto type = message.getProperty ("type", {}).toString();

    if (type.isEmpty())
    {
        result.replies.push_back (toJson (makeError (id, errors::badMessage, "The message has no type.")));
        return result;
    }

    if (type == "hello")
    {
        result.replies.push_back (toJson (hello (message, id, result)));
        return result;
    }

    // shutdown works before hello so any app version can stop an engine of another version
    // (for example one left running across an update). hello and shutdown never change shape.
    if (type == "shutdown")
    {
        result.replies.push_back (toJson (reply ("ok", id)));
        result.shutdownRequested = true;
        return result;
    }

    if (! helloDone_)
    {
        result.replies.push_back (toJson (makeError (id, errors::helloRequired, "Send hello first.")));
        return result;
    }

    if (type == "ping")
    {
        result.replies.push_back (toJson (reply ("pong", id)));
    }
    else
    {
        result.replies.push_back (toJson (makeError (id, errors::unknownType, "Unknown message type: " + type)));
    }

    return result;
}

juce::var MessageHandler::hello (const juce::var& message, const juce::var& id, HandlerResult& result)
{
    const auto clientProtocol = message.getProperty ("protocol", {});

    if (! clientProtocol.isInt() && ! clientProtocol.isInt64() && ! clientProtocol.isDouble())
        return makeError (id, errors::badMessage, "hello needs a numeric protocol version.");

    if (static_cast<int> (clientProtocol) != REC_PROTOCOL_VERSION)
    {
        result.closeConnection = true;
        return makeError (id, errors::protocolMismatch,
                          "The app speaks protocol " + clientProtocol.toString() + ", this engine speaks "
                              + juce::String (REC_PROTOCOL_VERSION) + ".");
    }

    helloDone_ = true;

    auto answer = reply ("hello", id);
    auto* fields = answer.getDynamicObject();
    fields->setProperty ("protocol", REC_PROTOCOL_VERSION);
    fields->setProperty ("engine", juce::String (identity_.engineVersion));
    fields->setProperty ("juce", juce::String (identity_.juceVersion));
    fields->setProperty ("pid", static_cast<juce::int64> (identity_.pid));
    return answer;
}
} // namespace rec
