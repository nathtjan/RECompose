// SPDX-License-Identifier: AGPL-3.0-or-later
#include "engine_server.h"

#include "ipc/frame.h"
#include "protocol.h"

#include <array>
#include <chrono>
#include <memory>

namespace rec
{
namespace
{
    using Clock = std::chrono::steady_clock;

    struct Connection
    {
        Connection (ipc::LocalSocket s, const EngineIdentity& identity, const RequestDelegate& requests)
            : socket (std::move (s)), decoder (kMaxFrameBytes), handler (identity, requests)
        {
        }

        ipc::LocalSocket socket;
        ipc::FrameDecoder decoder;
        MessageHandler handler;
    };

    bool send (ipc::LocalSocket& socket, const std::string& payload)
    {
        const auto frame = ipc::encodeFrame (payload);
        return socket.writeAll (frame.data(), frame.size());
    }
} // namespace

EngineServer::EngineServer (Options options, EngineIdentity identity)
    : options_ (std::move (options)), identity_ (std::move (identity))
{
}

bool EngineServer::start (std::string& error)
{
    listener_ = ipc::LocalListener::listen (options_.endpoint, error);
    return listener_.isOpen();
}

EngineServer::ExitReason EngineServer::run()
{
    std::unique_ptr<Connection> connection;
    auto lastConnected = Clock::now();
    const auto idleLimit = std::chrono::duration<double> (options_.idleExitSeconds);
    std::array<char, 64 * 1024> buffer {};

    while (! stopRequested_.load())
    {
        // A new connection (an app that restarted, or a second app) replaces the current one.
        const int acceptWait = connection ? 0 : options_.pollIntervalMs;
        if (auto incoming = listener_.accept (acceptWait); incoming.isOpen())
            connection = std::make_unique<Connection> (std::move (incoming), identity_, options_.requests);

        // Snapshots and events from the engine; dropped while no app is connected.
        if (options_.outgoing)
            for (const auto& message : options_.outgoing (connection != nullptr))
                if (connection != nullptr && ! send (connection->socket, message))
                    connection.reset();

        if (! connection)
        {
            if (options_.busy && options_.busy())
                lastConnected = Clock::now(); // still playing: the band needs the music (REL-34)
            else if (Clock::now() - lastConnected >= idleLimit)
                return ExitReason::idle;
            continue;
        }

        lastConnected = Clock::now();

        // Short waits while connected so snapshots go out at display rate.
        const auto wait = connection->socket.waitReadable (options_.outgoing ? 10 : options_.pollIntervalMs);
        if (wait == ipc::Wait::timeout)
            continue;

        const long received = wait == ipc::Wait::ready ? connection->socket.readSome (buffer.data(), buffer.size()) : -1;
        if (received <= 0)
        {
            connection.reset(); // the app went away; keep running
            continue;
        }

        connection->decoder.append (buffer.data(), static_cast<std::size_t> (received));

        if (connection->decoder.failed())
        {
            send (connection->socket, toJson (makeError ({}, errors::badMessage, "Frame too large.")));
            connection.reset();
            continue;
        }

        bool close = false;
        while (! close)
        {
            const auto payload = connection->decoder.next();
            if (! payload)
                break;

            const auto result = connection->handler.handle (*payload);
            for (const auto& reply : result.replies)
                send (connection->socket, reply);

            if (result.shutdownRequested)
                return ExitReason::shutdownRequested;

            close = result.closeConnection;
        }

        if (close)
            connection.reset();
    }

    return ExitReason::stopped;
}
} // namespace rec
