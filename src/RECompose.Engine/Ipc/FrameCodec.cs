// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;

namespace RECompose.Engine.Ipc;

/// <summary>
/// Framing for the engine protocol: a 4-byte little-endian payload length, then the
/// payload (UTF-8 JSON). Matches engine/src/ipc/frame.cpp.
/// </summary>
public static class FrameCodec
{
    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > EngineProtocol.MaxFrameBytes)
            throw new EngineProtocolException($"Frame of {payload.Length} bytes is larger than {EngineProtocol.MaxFrameBytes}.");

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsMemory(4));

        // One write per frame so concurrent writers (serialised by the caller) never interleave.
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one payload. Returns null when the stream ends cleanly between frames.
    /// Throws <see cref="EngineProtocolException"/> for a truncated or oversized frame.
    /// </summary>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var headerRead = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
            return null;
        if (headerRead < header.Length)
            throw new EngineProtocolException("The engine closed the connection in the middle of a frame header.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > EngineProtocol.MaxFrameBytes)
            throw new EngineProtocolException($"The engine sent a frame of {length} bytes, more than {EngineProtocol.MaxFrameBytes}.");

        var payload = new byte[length];
        if (await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false) < payload.Length)
            throw new EngineProtocolException("The engine closed the connection in the middle of a frame.");

        return payload;
    }

    private static async ValueTask<int> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}
