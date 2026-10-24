// SPDX-License-Identifier: AGPL-3.0-or-later
//
// A Unix domain socket (AF_UNIX, SOCK_STREAM) on Windows 10 1803+, macOS and Linux, so
// the app and the engine use one transport everywhere. Paths are UTF-8. Blocking calls
// take a timeout so the caller can keep checking its stop flag.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

namespace rec::ipc
{
#if defined(_WIN32)
using NativeSocket = std::uintptr_t; // SOCKET
#else
using NativeSocket = int;
#endif

// Initialises the platform socket library for the lifetime of the object (WSAStartup on
// Windows, nothing elsewhere). Create one before using sockets.
class SocketLibrary
{
public:
    SocketLibrary();
    ~SocketLibrary();
    SocketLibrary (const SocketLibrary&) = delete;
    SocketLibrary& operator= (const SocketLibrary&) = delete;
    bool ok() const noexcept { return ok_; }

private:
    bool ok_ = false;
};

// Result of waiting on a socket.
enum class Wait
{
    ready,
    timeout,
    error
};

// A connected stream socket. Move-only; closes on destruction.
class LocalSocket
{
public:
    LocalSocket() = default;
    explicit LocalSocket (NativeSocket handle) noexcept : handle_ (handle) {}
    ~LocalSocket();
    LocalSocket (LocalSocket&& other) noexcept;
    LocalSocket& operator= (LocalSocket&& other) noexcept;
    LocalSocket (const LocalSocket&) = delete;
    LocalSocket& operator= (const LocalSocket&) = delete;

    // Connects to a listening endpoint. Returns a closed socket and sets error on failure.
    static LocalSocket connect (const std::string& path, std::string& error);

    bool isOpen() const noexcept;

    NativeSocket native() const noexcept { return handle_; }

    // Waits up to timeoutMs for data (or end of stream) to be readable.
    Wait waitReadable (int timeoutMs) const;

    // Reads what is available, up to size bytes. Returns the byte count, 0 at end of
    // stream, or -1 on error.
    long readSome (char* buffer, std::size_t size);

    // Writes every byte. Returns false if the peer went away.
    bool writeAll (const char* data, std::size_t size);

    void close() noexcept;

private:
    NativeSocket handle_ = invalidHandle();
    static NativeSocket invalidHandle() noexcept;
};

// A listening endpoint. Removes a stale socket file before binding and removes its own
// file when closed.
class LocalListener
{
public:
    LocalListener() = default;
    ~LocalListener();
    LocalListener (LocalListener&& other) noexcept;
    LocalListener& operator= (LocalListener&& other) noexcept;
    LocalListener (const LocalListener&) = delete;
    LocalListener& operator= (const LocalListener&) = delete;

    static LocalListener listen (const std::string& path, std::string& error);

    bool isOpen() const noexcept { return socket_.isOpen(); }

    // Waits up to timeoutMs for a client. Returns a closed socket on timeout or error.
    LocalSocket accept (int timeoutMs);

    void close() noexcept;

private:
    LocalSocket socket_;
    std::string path_;
};

// Longest path the platform accepts for a socket address (bytes, excluding the terminator).
std::size_t maxSocketPathLength() noexcept;
} // namespace rec::ipc
