// SPDX-License-Identifier: AGPL-3.0-or-later
//
// recompose-engine: the audio engine process. The app starts it with
//   recompose-engine --endpoint <socket path> [--idle-exit-seconds N]
// and talks to it over that socket (docs/engine-protocol.md).
//
// Milestone 0: the control loop and the handshake. Milestone 1 (WP1) runs JUCE's message
// loop on the main thread for the AudioDeviceManager and moves this loop to its own thread.
#include "engine_server.h"
#include "ipc/local_socket.h"
#include "protocol.h"

#include <juce_core/juce_core.h>

#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#if defined(_WIN32)
  #include <process.h>
#else
  #include <unistd.h>
#endif

namespace
{
rec::EngineServer* runningServer = nullptr;

extern "C" void onSignal (int)
{
    if (runningServer != nullptr)
        runningServer->stop();
}

long long currentPid()
{
   #if defined(_WIN32)
    return static_cast<long long> (_getpid());
   #else
    return static_cast<long long> (::getpid());
   #endif
}

void printUsage()
{
    std::fprintf (stderr,
                  "Usage: recompose-engine --endpoint <socket path> [--idle-exit-seconds N]\n"
                  "       recompose-engine --version\n");
}
} // namespace

int main (int argc, char* argv[])
{
    rec::EngineServer::Options options;
    const rec::EngineIdentity identity { RECOMPOSE_ENGINE_VERSION,
                                         juce::SystemStats::getJUCEVersion().toStdString(),
                                         currentPid() };

    for (int i = 1; i < argc; ++i)
    {
        const std::string arg = argv[i];

        if (arg == "--version")
        {
            std::printf ("recompose-engine %s (%s), protocol %d\n",
                         identity.engineVersion.c_str(), identity.juceVersion.c_str(), rec::REC_PROTOCOL_VERSION);
            return 0;
        }

        if (arg == "--endpoint" && i + 1 < argc)
            options.endpoint = argv[++i];
        else if (arg == "--idle-exit-seconds" && i + 1 < argc)
            options.idleExitSeconds = std::atof (argv[++i]);
        else
        {
            printUsage();
            return 64;
        }
    }

    if (options.endpoint.empty())
    {
        printUsage();
        return 64;
    }

    rec::ipc::SocketLibrary sockets;
    if (! sockets.ok())
    {
        std::fprintf (stderr, "recompose-engine: the socket library could not be initialised\n");
        return 70;
    }

    rec::EngineServer server (options, identity);
    std::string error;
    if (! server.start (error))
    {
        std::fprintf (stderr, "recompose-engine: %s\n", error.c_str());
        return 69;
    }

    runningServer = &server;
    std::signal (SIGINT, onSignal);
    std::signal (SIGTERM, onSignal);
   #if ! defined(_WIN32)
    std::signal (SIGPIPE, SIG_IGN); // a vanished app must never kill the engine (REL-34)
   #endif

    server.run();

    runningServer = nullptr;
    return 0;
}
