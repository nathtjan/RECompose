// SPDX-License-Identifier: AGPL-3.0-or-later
#include "ipc/frame.h"

namespace rec::ipc
{
std::string encodeFrame (std::string_view payload)
{
    const auto size = static_cast<std::uint32_t> (payload.size());
    std::string frame;
    frame.reserve (4 + payload.size());
    frame.push_back (static_cast<char> (size & 0xffu));
    frame.push_back (static_cast<char> ((size >> 8) & 0xffu));
    frame.push_back (static_cast<char> ((size >> 16) & 0xffu));
    frame.push_back (static_cast<char> ((size >> 24) & 0xffu));
    frame.append (payload);
    return frame;
}

FrameDecoder::FrameDecoder (std::uint32_t maxFrameBytes) : maxFrameBytes_ (maxFrameBytes) {}

void FrameDecoder::append (const char* data, std::size_t size)
{
    if (failed_)
        return;

    buffer_.append (data, size);
    parse();
}

std::optional<std::string> FrameDecoder::next()
{
    if (ready_.empty())
        return std::nullopt;

    auto payload = std::move (ready_.front());
    ready_.pop_front();
    return payload;
}

void FrameDecoder::parse()
{
    std::size_t offset = 0;

    while (buffer_.size() - offset >= 4)
    {
        const auto* p = reinterpret_cast<const unsigned char*> (buffer_.data() + offset);
        const auto size = static_cast<std::uint32_t> (p[0])
                        | (static_cast<std::uint32_t> (p[1]) << 8)
                        | (static_cast<std::uint32_t> (p[2]) << 16)
                        | (static_cast<std::uint32_t> (p[3]) << 24);

        if (size > maxFrameBytes_)
        {
            failed_ = true;
            buffer_.clear();
            return;
        }

        if (buffer_.size() - offset - 4 < size)
            break;

        ready_.emplace_back (buffer_, offset + 4, size);
        offset += 4 + size;
    }

    buffer_.erase (0, offset);
}
} // namespace rec::ipc
