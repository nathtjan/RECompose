// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The engine's control loop: owns the listening socket, serves one app connection at a
// time and decides when the process should exit. Runs on a normal thread, never the
// audio thread.
//
// Lifetime rules (REL-34): the engine keeps running when the app disconnects, so a UI
// crash can't stop the music. A newly started app reattaches to the running engine (a
// new connection replaces the old one). The engine exits on "shutdown", on stop(), or
// once no app has been connected for idleExitSeconds while nothing is playing.
#pragma once

#include "ipc/local_socket.h"
#include "message_handler.h"

#include <atomic>
#include <functional>
#include <string>
#include <vector>

namespace rec
{
class EngineServer
{
public:
    struct Options
    {
        std::string endpoint;
        double idleExitSeconds = 10.0;
        int pollIntervalMs = 100;

        // Optional hooks to the engine (absent in protocol-only tests).
        RequestDelegate requests;                                   // other request types
        std::function<std::vector<std::string> (bool connected)> outgoing; // snapshots, events
        std::function<bool()> busy;                                 // playing: don't exit when idle
    };

    enum class ExitReason
    {
        shutdownRequested,
        idle,
        stopped
    };

    EngineServer (Options options, EngineIdentity identity);

    // Binds the endpoint. On failure returns false and sets error.
    bool start (std::string& error);

    // Serves connections until one of the exit conditions. Call after start().
    ExitReason run();

    // Thread-safe; makes run() return within one poll interval.
    void stop() noexcept { stopRequested_.store (true); }

private:
    Options options_;
    EngineIdentity identity_;
    ipc::LocalListener listener_;
    std::atomic<bool> stopRequested_ { false };
};
} // namespace rec
