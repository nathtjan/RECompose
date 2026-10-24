// SPDX-License-Identifier: AGPL-3.0-or-later
#include "ipc/local_socket.h"

#include <cstring>
#include <filesystem>
#include <system_error>
#include <utility>

#if defined(_WIN32)
  #ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN
  #endif
  #ifndef NOMINMAX
    #define NOMINMAX
  #endif
  #include <winsock2.h>
  #include <afunix.h>
#else
  #include <cerrno>
  #include <sys/select.h>
  #include <sys/socket.h>
  #include <sys/time.h>
  #include <sys/un.h>
  #include <unistd.h>
#endif

namespace rec::ipc
{
namespace
{
#if defined(_WIN32)
    SOCKET toNative (NativeSocket s) noexcept { return static_cast<SOCKET> (s); }
    void closeNative (NativeSocket s) noexcept { ::closesocket (toNative (s)); }
    std::string lastError() { return "WSA error " + std::to_string (::WSAGetLastError()); }
#else
    int toNative (NativeSocket s) noexcept { return s; }
    void closeNative (NativeSocket s) noexcept { ::close (s); }
    std::string lastError() { return std::strerror (errno); }
#endif

    std::filesystem::path toPath (const std::string& utf8)
    {
        return std::filesystem::path (std::u8string (reinterpret_cast<const char8_t*> (utf8.data()), utf8.size()));
    }

    void removeFile (const std::string& utf8) noexcept
    {
        std::error_code ignored;
        std::filesystem::remove (toPath (utf8), ignored);
    }

    bool makeAddress (const std::string& path, sockaddr_un& address, std::string& error)
    {
        std::memset (&address, 0, sizeof (address));
        address.sun_family = AF_UNIX;

        if (path.empty() || path.size() > maxSocketPathLength())
        {
            error = "socket path is empty or longer than " + std::to_string (maxSocketPathLength()) + " bytes: " + path;
            return false;
        }

        std::memcpy (address.sun_path, path.data(), path.size());
        return true;
    }

    NativeSocket openStreamSocket()
    {
        const auto s = ::socket (AF_UNIX, SOCK_STREAM, 0);
       #if defined(__APPLE__)
        if (s >= 0)
        {
            int on = 1;
            ::setsockopt (s, SOL_SOCKET, SO_NOSIGPIPE, &on, sizeof (on));
        }
       #endif
        return static_cast<NativeSocket> (s);
    }

    Wait waitFor (NativeSocket s, int timeoutMs)
    {
        fd_set readSet;
        FD_ZERO (&readSet);
        FD_SET (toNative (s), &readSet);

        timeval timeout {};
        timeout.tv_sec = timeoutMs / 1000;
        timeout.tv_usec = (timeoutMs % 1000) * 1000;

       #if defined(_WIN32)
        const int result = ::select (0, &readSet, nullptr, nullptr, &timeout);
       #else
        int result;
        do
        {
            result = ::select (toNative (s) + 1, &readSet, nullptr, nullptr, &timeout);
        } while (result < 0 && errno == EINTR);
       #endif

        if (result < 0)
            return Wait::error;

        return result == 0 ? Wait::timeout : Wait::ready;
    }
} // namespace

//==============================================================================
SocketLibrary::SocketLibrary()
{
   #if defined(_WIN32)
    WSADATA data {};
    ok_ = ::WSAStartup (MAKEWORD (2, 2), &data) == 0;
   #else
    ok_ = true;
   #endif
}

SocketLibrary::~SocketLibrary()
{
   #if defined(_WIN32)
    if (ok_)
        ::WSACleanup();
   #endif
}

std::size_t maxSocketPathLength() noexcept
{
    return sizeof (sockaddr_un::sun_path) - 1;
}

//==============================================================================
NativeSocket LocalSocket::invalidHandle() noexcept
{
   #if defined(_WIN32)
    return static_cast<NativeSocket> (INVALID_SOCKET);
   #else
    return -1;
   #endif
}

LocalSocket::~LocalSocket() { close(); }

LocalSocket::LocalSocket (LocalSocket&& other) noexcept : handle_ (std::exchange (other.handle_, invalidHandle())) {}

LocalSocket& LocalSocket::operator= (LocalSocket&& other) noexcept
{
    if (this != &other)
    {
        close();
        handle_ = std::exchange (other.handle_, invalidHandle());
    }
    return *this;
}

bool LocalSocket::isOpen() const noexcept { return handle_ != invalidHandle(); }

void LocalSocket::close() noexcept
{
    if (isOpen())
        closeNative (std::exchange (handle_, invalidHandle()));
}

LocalSocket LocalSocket::connect (const std::string& path, std::string& error)
{
    sockaddr_un address {};
    if (! makeAddress (path, address, error))
        return {};

    LocalSocket socket (openStreamSocket());
    if (! socket.isOpen())
    {
        error = "socket(): " + lastError();
        return {};
    }

    if (::connect (toNative (socket.handle_), reinterpret_cast<const sockaddr*> (&address), sizeof (address)) != 0)
    {
        error = "connect(" + path + "): " + lastError();
        return {};
    }

    return socket;
}

Wait LocalSocket::waitReadable (int timeoutMs) const
{
    return isOpen() ? waitFor (handle_, timeoutMs) : Wait::error;
}

long LocalSocket::readSome (char* buffer, std::size_t size)
{
    if (! isOpen())
        return -1;

   #if defined(_WIN32)
    const int received = ::recv (toNative (handle_), buffer, static_cast<int> (size), 0);
   #else
    ssize_t received;
    do
    {
        received = ::recv (handle_, buffer, size, 0);
    } while (received < 0 && errno == EINTR);
   #endif

    return received < 0 ? -1 : static_cast<long> (received);
}

bool LocalSocket::writeAll (const char* data, std::size_t size)
{
    while (size > 0 && isOpen())
    {
       #if defined(_WIN32)
        const int sent = ::send (toNative (handle_), data, static_cast<int> (size), 0);
       #elif defined(MSG_NOSIGNAL)
        const auto sent = ::send (handle_, data, size, MSG_NOSIGNAL);
       #else
        const auto sent = ::send (handle_, data, size, 0);
       #endif

        if (sent <= 0)
        {
           #if ! defined(_WIN32)
            if (sent < 0 && errno == EINTR)
                continue;
           #endif
            return false;
        }

        data += sent;
        size -= static_cast<std::size_t> (sent);
    }

    return size == 0;
}

//==============================================================================
LocalListener::~LocalListener() { close(); }

LocalListener::LocalListener (LocalListener&& other) noexcept
    : socket_ (std::move (other.socket_)), path_ (std::exchange (other.path_, {}))
{
}

LocalListener& LocalListener::operator= (LocalListener&& other) noexcept
{
    if (this != &other)
    {
        close();
        socket_ = std::move (other.socket_);
        path_ = std::exchange (other.path_, {});
    }
    return *this;
}

LocalListener LocalListener::listen (const std::string& path, std::string& error)
{
    sockaddr_un address {};
    if (! makeAddress (path, address, error))
        return {};

    std::error_code ignored;
    std::filesystem::create_directories (toPath (path).parent_path(), ignored);
    removeFile (path); // a socket file left behind by an engine that didn't shut down cleanly

    LocalListener listener;
    listener.socket_ = LocalSocket (openStreamSocket());
    if (! listener.socket_.isOpen())
    {
        error = "socket(): " + lastError();
        return {};
    }

    const auto native = listener.socket_.native();

    if (::bind (toNative (native), reinterpret_cast<const sockaddr*> (&address), sizeof (address)) != 0)
    {
        error = "bind(" + path + "): " + lastError();
        return {};
    }

    listener.path_ = path;

    if (::listen (toNative (native), 4) != 0)
    {
        error = "listen(" + path + "): " + lastError();
        return {};
    }

    return listener;
}

LocalSocket LocalListener::accept (int timeoutMs)
{
    if (! socket_.isOpen() || socket_.waitReadable (timeoutMs) != Wait::ready)
        return {};

    const auto client = ::accept (toNative (socket_.native()), nullptr, nullptr);

   #if defined(_WIN32)
    if (client == INVALID_SOCKET)
        return {};
   #else
    if (client < 0)
        return {};
   #endif

    return LocalSocket (static_cast<NativeSocket> (client));
}

void LocalListener::close() noexcept
{
    const bool wasOpen = socket_.isOpen();
    socket_.close();

    if (wasOpen && ! path_.empty())
        removeFile (path_);

    path_.clear();
}
} // namespace rec::ipc
