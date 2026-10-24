// SPDX-License-Identifier: AGPL-3.0-or-later
#include "check.h"

#include "message_handler.h"
#include "protocol.h"

namespace
{
rec::EngineIdentity identity() { return { "0.1.0", "JUCE v9.0.4", 4242 }; }

juce::var parse (const std::string& json) { return juce::JSON::parse (juce::String (json)); }

std::string helloMessage (int protocol)
{
    return R"({"type":"hello","id":1,"protocol":)" + std::to_string (protocol) + R"(,"client":"tests"})";
}
} // namespace

TEST ("handler: hello reports the protocol, versions and pid")
{
    rec::MessageHandler handler (identity());
    const auto result = handler.handle (helloMessage (rec::REC_PROTOCOL_VERSION));

    CHECK (result.replies.size() == 1);
    const auto reply = parse (result.replies.at (0));
    CHECK (reply["type"].toString() == "hello");
    CHECK (static_cast<int> (reply["re"]) == 1);
    CHECK (static_cast<int> (reply["protocol"]) == rec::REC_PROTOCOL_VERSION);
    CHECK (reply["engine"].toString() == "0.1.0");
    CHECK (reply["juce"].toString() == "JUCE v9.0.4");
    CHECK (static_cast<juce::int64> (reply["pid"]) == 4242);
    CHECK (! result.closeConnection && ! result.shutdownRequested);
}

TEST ("handler: a protocol mismatch is an error and closes the connection")
{
    rec::MessageHandler handler (identity());
    const auto result = handler.handle (helloMessage (rec::REC_PROTOCOL_VERSION + 1));

    const auto reply = parse (result.replies.at (0));
    CHECK (reply["type"].toString() == "error");
    CHECK (reply["code"].toString() == rec::errors::protocolMismatch);
    CHECK (result.closeConnection);
}

TEST ("handler: requests before hello are refused")
{
    rec::MessageHandler handler (identity());
    const auto reply = parse (handler.handle (R"({"type":"ping","id":7})").replies.at (0));
    CHECK (reply["code"].toString() == rec::errors::helloRequired);
    CHECK (static_cast<int> (reply["re"]) == 7);
}

TEST ("handler: shutdown works before hello, so any app version can stop the engine")
{
    rec::MessageHandler handler (identity());
    const auto result = handler.handle (R"({"type":"shutdown","id":9})");
    CHECK (parse (result.replies.at (0))["type"].toString() == "ok");
    CHECK (result.shutdownRequested);
}

TEST ("handler: ping, shutdown and unknown types after hello")
{
    rec::MessageHandler handler (identity());
    handler.handle (helloMessage (rec::REC_PROTOCOL_VERSION));

    CHECK (parse (handler.handle (R"({"type":"ping","id":2})").replies.at (0))["type"].toString() == "pong");

    const auto unknown = parse (handler.handle (R"({"type":"dance","id":3})").replies.at (0));
    CHECK (unknown["code"].toString() == rec::errors::unknownType);

    const auto shutdown = handler.handle (R"({"type":"shutdown","id":4})");
    CHECK (parse (shutdown.replies.at (0))["type"].toString() == "ok");
    CHECK (shutdown.shutdownRequested);
}

TEST ("handler: malformed messages get badMessage")
{
    rec::MessageHandler handler (identity());
    CHECK (parse (handler.handle ("not json").replies.at (0))["code"].toString() == rec::errors::badMessage);
    CHECK (parse (handler.handle ("[1,2]").replies.at (0))["code"].toString() == rec::errors::badMessage);
    CHECK (parse (handler.handle (R"({"id":1})").replies.at (0))["code"].toString() == rec::errors::badMessage);
}
