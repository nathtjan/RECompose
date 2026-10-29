// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The engine protocol: the only interface between the app and the engine process.
// Documented in docs/engine-protocol.md. Bump REC_PROTOCOL_VERSION together with
// EngineProtocol.Version (src/RECompose.Engine/EngineProtocol.cs) on any change to a
// message shape or the framing.
#pragma once

#include <cstdint>

namespace rec
{
inline constexpr int REC_PROTOCOL_VERSION = 11;

// Frames larger than this are a protocol error (both directions).
inline constexpr std::uint32_t kMaxFrameBytes = 16u * 1024u * 1024u;

// Error codes carried in {"type":"error","code":...}.
namespace errors
{
    inline constexpr const char* badMessage = "badMessage";
    inline constexpr const char* helloRequired = "helloRequired";
    inline constexpr const char* protocolMismatch = "protocolMismatch";
    inline constexpr const char* unknownType = "unknownType";
} // namespace errors
} // namespace rec
