// SPDX-License-Identifier: AGPL-3.0-or-later
#include "check.h"

#include "ipc/frame.h"

using rec::ipc::encodeFrame;
using rec::ipc::FrameDecoder;

TEST ("frame: the length prefix is 4 bytes, little-endian")
{
    const auto frame = encodeFrame (std::string (300, 'x'));
    CHECK (frame.size() == 304);
    CHECK (static_cast<unsigned char> (frame[0]) == 0x2c); // 300 = 0x012c
    CHECK (static_cast<unsigned char> (frame[1]) == 0x01);
    CHECK (frame[2] == 0 && frame[3] == 0);
}

TEST ("frame: payloads survive being split at every byte")
{
    const auto stream = encodeFrame (R"({"type":"hello"})") + encodeFrame ("") + encodeFrame (R"({"type":"ping"})");

    FrameDecoder decoder (1024);
    std::vector<std::string> payloads;
    for (char c : stream)
    {
        decoder.append (&c, 1);
        while (auto p = decoder.next())
            payloads.push_back (*p);
    }

    CHECK (payloads.size() == 3);
    CHECK (payloads.size() == 3 && payloads[0] == R"({"type":"hello"})");
    CHECK (payloads.size() == 3 && payloads[1].empty());
    CHECK (payloads.size() == 3 && payloads[2] == R"({"type":"ping"})");
    CHECK (! decoder.failed());
}

TEST ("frame: an oversized header fails the decoder")
{
    FrameDecoder decoder (16);
    const auto frame = encodeFrame (std::string (17, 'x'));
    decoder.append (frame.data(), frame.size());
    CHECK (decoder.failed());
    CHECK (! decoder.next().has_value());
}
