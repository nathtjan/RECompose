// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using RECompose.Engine.Ipc;

namespace RECompose.Engine.Tests;

public class FrameCodecTests
{
    [Fact]
    public async Task Frames_round_trip_with_a_little_endian_length_prefix()
    {
        using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes(new string('x', 300));

        await FrameCodec.WriteFrameAsync(stream, payload, TestContext.Current.CancellationToken);

        var bytes = stream.ToArray();
        Assert.Equal(304, bytes.Length);
        Assert.Equal(new byte[] { 0x2c, 0x01, 0x00, 0x00 }, bytes[..4]);

        stream.Position = 0;
        Assert.Equal(payload, await FrameCodec.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
        Assert.Null(await FrameCodec.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_truncated_frame_is_a_protocol_error()
    {
        using var stream = new MemoryStream([5, 0, 0, 0, (byte)'a', (byte)'b']);
        await Assert.ThrowsAsync<EngineProtocolException>(
            async () => await FrameCodec.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_oversized_frame_is_a_protocol_error()
    {
        using var stream = new MemoryStream([0xff, 0xff, 0xff, 0x7f]);
        await Assert.ThrowsAsync<EngineProtocolException>(
            async () => await FrameCodec.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }
}
