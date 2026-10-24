// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Turns one incoming message into replies. Pure: no sockets, no threads, no audio, so
// every protocol rule is unit-tested. One handler per connection (it remembers whether
// the client has said hello).
#pragma once

#include <juce_core/juce_core.h>

#include <string>
#include <vector>

namespace rec
{
// Who this engine is, reported in the hello reply.
struct EngineIdentity
{
    std::string engineVersion; // e.g. "0.1.0"
    std::string juceVersion;   // e.g. "JUCE v9.0.4"
    long long pid = 0;
};

struct HandlerResult
{
    std::vector<std::string> replies; // JSON payloads, in order
    bool closeConnection = false;     // after sending the replies
    bool shutdownRequested = false;   // after sending the replies
};

class MessageHandler
{
public:
    explicit MessageHandler (EngineIdentity identity);

    HandlerResult handle (const std::string& payload);

private:
    juce::var hello (const juce::var& message, const juce::var& id, HandlerResult& result);

    EngineIdentity identity_;
    bool helloDone_ = false;
};

// Helpers shared with the server and tests.
std::string toJson (const juce::var& value);
juce::var makeError (const juce::var& requestId, const char* code, const juce::String& message);
} // namespace rec
