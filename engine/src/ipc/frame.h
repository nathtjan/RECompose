// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Framing for the engine protocol: each message is a 4-byte little-endian payload
// length followed by that many bytes of UTF-8 JSON.
#pragma once

#include <cstddef>
#include <cstdint>
#include <deque>
#include <optional>
#include <string>
#include <string_view>

namespace rec::ipc
{
// Returns the frame for one payload (length prefix + payload).
std::string encodeFrame (std::string_view payload);

// Splits a byte stream back into payloads. Feed it whatever the socket returns.
class FrameDecoder
{
public:
    explicit FrameDecoder (std::uint32_t maxFrameBytes);

    // Appends received bytes. Complete frames become available through next().
    void append (const char* data, std::size_t size);

    // The next complete payload, if any.
    std::optional<std::string> next();

    // True once a frame header announced more than maxFrameBytes. The stream can't be
    // resynchronised after that; the connection must be closed.
    bool failed() const noexcept { return failed_; }

private:
    void parse();

    std::uint32_t maxFrameBytes_;
    std::string buffer_;
    std::deque<std::string> ready_;
    bool failed_ = false;
};
} // namespace rec::ipc
