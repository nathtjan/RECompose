// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Engine;

/// <summary>
/// Constants of the engine protocol (docs/engine-protocol.md). Must match
/// engine/src/protocol.h; bump both together.
/// </summary>
public static class EngineProtocol
{
    /// <summary>Must equal REC_PROTOCOL_VERSION in engine/src/protocol.h.</summary>
    public const int Version = 7;

    /// <summary>Frames larger than this are a protocol error (both directions).</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;
}

/// <summary>The engine sent something that breaks the protocol.</summary>
public sealed class EngineProtocolException(string message) : Exception(message);

/// <summary>The engine answered a request with {"type":"error"}.</summary>
public sealed class EngineErrorException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The connection to the engine closed while a request was waiting.</summary>
public sealed class EngineDisconnectedException(string message) : Exception(message);

/// <summary>The engine could not be started or reached. The message is shown to the user.</summary>
public sealed class EngineUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
