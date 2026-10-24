// SPDX-License-Identifier: AGPL-3.0-or-later
#include "check.h"

#include "engine_server.h"
#include "ipc/frame.h"
#include "protocol.h"

#include <atomic>
#include <chrono>
#include <filesystem>
#include <thread>

namespace
{
std::string tempEndpoint (const char* tag)
{
    static std::atomic<int> counter { 0 };
    const auto name = std::string ("rec-") + tag + "-" + std::to_string (juce::Random::getSystemRandom().nextInt (1000000))
                    + "-" + std::to_string (counter++) + ".sock";
    const auto path = (std::filesystem::temp_directory_path() / name).u8string();
    return { reinterpret_cast<const char*> (path.data()), path.size() };
}

rec::EngineIdentity identity() { return { "0.1.0", "JUCE test", 1 }; }

// Sends one request and waits up to 2 s for one reply.
juce::var request (rec::ipc::LocalSocket& socket, const std::string& payload)
{
    const auto frame = rec::ipc::encodeFrame (payload);
    if (! socket.writeAll (frame.data(), frame.size()))
        return {};

    rec::ipc::FrameDecoder decoder (rec::kMaxFrameBytes);
    char buffer[4096];
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds (2);

    while (std::chrono::steady_clock::now() < deadline)
    {
        if (auto reply = decoder.next())
            return juce::JSON::parse (juce::String (*reply));

        if (socket.waitReadable (50) != rec::ipc::Wait::ready)
            continue;

        const long n = socket.readSome (buffer, sizeof (buffer));
        if (n <= 0)
            return {};
        decoder.append (buffer, static_cast<std::size_t> (n));
    }

    return {};
}

rec::ipc::LocalSocket connectWithRetry (const std::string& endpoint)
{
    std::string error;
    for (int attempt = 0; attempt < 100; ++attempt)
    {
        auto socket = rec::ipc::LocalSocket::connect (endpoint, error);
        if (socket.isOpen())
            return socket;
        std::this_thread::sleep_for (std::chrono::milliseconds (20));
    }
    std::fprintf (stderr, "connect failed: %s\n", error.c_str());
    return {};
}

rec::EngineServer::Options options (const std::string& endpoint, double idleExitSeconds)
{
    rec::EngineServer::Options o;
    o.endpoint = endpoint;
    o.idleExitSeconds = idleExitSeconds;
    o.pollIntervalMs = 20;
    return o;
}

const std::string hello = R"({"type":"hello","id":1,"protocol":)" + std::to_string (rec::REC_PROTOCOL_VERSION) + "}";
} // namespace

TEST ("server: hello and shutdown over a real socket")
{
    const auto endpoint = tempEndpoint ("roundtrip");
    rec::EngineServer server (options (endpoint, 30.0), identity());
    std::string error;
    CHECK (server.start (error));

    auto reason = rec::EngineServer::ExitReason::stopped;
    std::thread loop ([&] { reason = server.run(); });

    auto socket = connectWithRetry (endpoint);
    CHECK (socket.isOpen());
    CHECK (request (socket, hello)["type"].toString() == "hello");
    CHECK (request (socket, R"({"type":"shutdown","id":2})")["type"].toString() == "ok");

    loop.join();
    CHECK (reason == rec::EngineServer::ExitReason::shutdownRequested);
}

TEST ("server: keeps running when the app disconnects, and a new app can attach")
{
    const auto endpoint = tempEndpoint ("reattach");
    rec::EngineServer server (options (endpoint, 30.0), identity());
    std::string error;
    CHECK (server.start (error));
    std::thread loop ([&] { server.run(); });

    {
        auto first = connectWithRetry (endpoint);
        CHECK (request (first, hello)["type"].toString() == "hello");
    } // first app "crashes"

    auto second = connectWithRetry (endpoint);
    CHECK (request (second, hello)["type"].toString() == "hello");
    CHECK (request (second, R"({"type":"ping","id":2})")["type"].toString() == "pong");

    server.stop();
    loop.join();
}

TEST ("server: exits by itself when no app is connected")
{
    const auto endpoint = tempEndpoint ("idle");
    rec::EngineServer server (options (endpoint, 0.2), identity());
    std::string error;
    CHECK (server.start (error));

    const auto started = std::chrono::steady_clock::now();
    const auto reason = server.run();
    const auto elapsed = std::chrono::steady_clock::now() - started;

    CHECK (reason == rec::EngineServer::ExitReason::idle);
    CHECK (elapsed < std::chrono::seconds (2));
}

TEST ("server: replaces a stale socket file")
{
    const auto endpoint = tempEndpoint ("stale");
    {
        juce::File (juce::String (endpoint)).replaceWithText ("left behind");
    }

    rec::EngineServer server (options (endpoint, 30.0), identity());
    std::string error;
    CHECK (server.start (error));
    if (! error.empty())
        std::fprintf (stderr, "%s\n", error.c_str());
    std::thread loop ([&] { server.run(); });

    auto socket = connectWithRetry (endpoint);
    CHECK (request (socket, hello)["type"].toString() == "hello");

    server.stop();
    loop.join();
}
