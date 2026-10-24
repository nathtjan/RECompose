// SPDX-License-Identifier: AGPL-3.0-or-later
//
// recompose-engine: the audio engine process. The app starts it with
//   recompose-engine --endpoint <socket path> [--idle-exit-seconds N]
// and talks to it over that socket (docs/engine-protocol.md). The import jobs use the
// one-shot modes in tools.h.
//
// Threads: the main thread runs JUCE's message loop (the device manager, the engine
// state); the server thread moves bytes; the disk and audio threads stream and play.
#include "engine.h"
#include "engine_server.h"
#include "ipc/local_socket.h"
#include "protocol.h"
#include "tools.h"

#include <juce_events/juce_events.h>

#include <atomic>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>

#if defined(_WIN32)
  #include <process.h>
#else
  #include <unistd.h>
#endif

#if defined(__APPLE__)
  #include <CoreFoundation/CoreFoundation.h>
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
                  "       recompose-engine probe <file>\n"
                  "       recompose-engine peaks <file> <out> [framesPerBucket]\n"
                  "       recompose-engine --version\n");
}
} // namespace

int main (int argc, char* argv[])
{
    const rec::EngineIdentity identity { RECOMPOSE_ENGINE_VERSION,
                                         juce::SystemStats::getJUCEVersion().toStdString(),
                                         currentPid() };

    if (argc >= 2 && std::strcmp (argv[1], "probe") == 0)
    {
        if (argc != 3)
        {
            printUsage();
            return 64;
        }
        return rec::tools::probe (argv[2]);
    }

    if (argc >= 2 && std::strcmp (argv[1], "peaks") == 0)
    {
        if (argc < 4 || argc > 5)
        {
            printUsage();
            return 64;
        }
        return rec::tools::peaks (argv[2], argv[3], argc == 5 ? std::atoi (argv[4]) : 480);
    }

    rec::EngineServer::Options options;

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

    juce::ScopedJuceInitialiser_GUI juceRuntime; // this thread becomes the message thread
    auto engine = std::make_unique<rec::Engine>();

    options.requests = [&engine] (const juce::String& type, const juce::var& message) -> juce::var
    {
        auto reply = juce::MessageManager::callSync ([&] { return engine->handle (type, message); });
        return reply.has_value() ? *reply : juce::var();
    };
    options.outgoing = [&engine] (bool connected) { return engine->takeOutgoing (connected); };
    options.busy = [&engine] { return engine->isPlaying(); };

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

    std::atomic<bool> finished { false };

   #if defined(__APPLE__)
    // JUCE's message queue is a CFRunLoop source on the main run loop. A non-standalone
    // JUCE process has no NSApplication, so runDispatchLoop() would return at once; run the
    // main run loop directly instead.
    auto* mainLoop = CFRunLoopGetCurrent();
    std::thread control ([&server, &finished, mainLoop]
    {
        server.run();
        finished.store (true);
        CFRunLoopWakeUp (mainLoop);
    });

    while (! finished.load())
        CFRunLoopRunInMode (kCFRunLoopDefaultMode, 0.25, false);
   #else
    std::thread control ([&server, &finished]
    {
        server.run();
        finished.store (true);
        juce::MessageManager::getInstance()->stopDispatchLoop();
    });

    juce::MessageManager::getInstance()->runDispatchLoop();
   #endif

    server.stop();
    control.join();
    runningServer = nullptr;
    engine.reset();
    return 0;
}
