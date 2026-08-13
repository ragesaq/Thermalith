using Niimbot.Net.Transport;
using Xunit;

namespace Niimbot.Net.Tests;

/// <summary>
/// The OS-independent core of the BLE read path: notification payloads pushed from a callback
/// thread must surface through the transport read contract — ordered, reassemblable, 0 on idle.
/// </summary>
public class AsyncByteQueueTests
{
    [Fact]
    public async Task Bytes_surface_in_order_across_fragmented_appends()
    {
        using var queue = new AsyncByteQueue();
        // A packet split mid-frame across two notifications (the documented BLE behavior).
        queue.Append(new byte[] { 0x55, 0x55, 0xD9, 0x09, 0x1F, 0x90 });
        queue.Append(new byte[] { 0x04, 0x4C, 0x16, 0xAA, 0xAA });

        var buffer = new byte[32];
        var total = 0;
        while (total < 11)
        {
            var read = await queue.ReadAsync(buffer.AsMemory(total), timeoutMs: 1000);
            Assert.True(read > 0, "expected buffered bytes");
            total += read;
        }

        Assert.Equal(11, total);
        Assert.Equal(new byte[] { 0x55, 0x55, 0xD9, 0x09, 0x1F, 0x90, 0x04, 0x4C, 0x16, 0xAA, 0xAA },
            buffer[..total]);
    }

    [Fact]
    public async Task Read_smaller_than_a_segment_keeps_the_remainder()
    {
        using var queue = new AsyncByteQueue();
        queue.Append(new byte[] { 1, 2, 3, 4, 5 });

        var small = new byte[2];
        Assert.Equal(2, await queue.ReadAsync(small, timeoutMs: 1000));
        Assert.Equal(new byte[] { 1, 2 }, small);

        var rest = new byte[8];
        Assert.Equal(3, await queue.ReadAsync(rest, timeoutMs: 1000));
        Assert.Equal(new byte[] { 3, 4, 5 }, rest[..3]);
    }

    [Fact]
    public async Task Idle_line_returns_zero_after_the_timeout()
    {
        using var queue = new AsyncByteQueue();
        Assert.Equal(0, await queue.ReadAsync(new byte[8], timeoutMs: 30));
    }

    [Fact]
    public async Task Cancellation_returns_zero_instead_of_throwing()
    {
        using var queue = new AsyncByteQueue();
        using var cts = new CancellationTokenSource(20);
        Assert.Equal(0, await queue.ReadAsync(new byte[8], timeoutMs: 5000, cts.Token));
    }

    [Fact]
    public async Task Dispose_wakes_a_parked_reader_with_zero()
    {
        var queue = new AsyncByteQueue();
        var pending = queue.ReadAsync(new byte[8], timeoutMs: 5000).AsTask();
        await Task.Delay(30);

        queue.Dispose();

        Assert.Equal(0, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Append_after_dispose_is_ignored()
    {
        var queue = new AsyncByteQueue();
        queue.Dispose();
        queue.Append(new byte[] { 1, 2, 3 });
        Assert.Equal(0, await queue.ReadAsync(new byte[8], timeoutMs: 10));
    }

    [Fact]
    public async Task Writer_thread_and_reader_thread_transfer_a_large_stream_intact()
    {
        using var queue = new AsyncByteQueue();
        var payload = new byte[16 * 1024];
        new Random(42).NextBytes(payload);

        // Push in awkward chunk sizes from another thread, like GATT notifications would arrive.
        var writer = Task.Run(async () =>
        {
            var offset = 0;
            var sizes = new[] { 1, 7, 20, 3, 182, 20, 500 };
            var i = 0;
            while (offset < payload.Length)
            {
                var take = Math.Min(sizes[i++ % sizes.Length], payload.Length - offset);
                queue.Append(payload.AsSpan(offset, take));
                offset += take;
                if (i % 11 == 0)
                    await Task.Delay(1);
            }
        });

        var received = new byte[payload.Length];
        var total = 0;
        var buffer = new byte[257]; // deliberately unaligned with writer chunk sizes
        while (total < payload.Length)
        {
            var read = await queue.ReadAsync(buffer, timeoutMs: 2000);
            Assert.True(read > 0, $"stream stalled at {total} bytes");
            buffer.AsSpan(0, read).CopyTo(received.AsSpan(total));
            total += read;
        }

        await writer;
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task Fragmented_packets_reassemble_through_the_accumulator()
    {
        // End-to-end shape of the BLE read path: fragments in, whole packets out — using the real
        // fragmentation example from the community wiki (5555d9091f90044c000001000016aaaa split
        // before its tail).
        using var queue = new AsyncByteQueue();
        var accumulator = new Framing.PacketAccumulator();
        queue.Append(Convert.FromHexString("5555D9091F90044C000001000016"));
        queue.Append(Convert.FromHexString("AAAA"));

        var buffer = new byte[64];
        Framing.NiimbotPacket? packet = null;
        for (var i = 0; i < 4 && packet is null; i++)
        {
            var read = await queue.ReadAsync(buffer, timeoutMs: 500);
            if (read > 0)
                accumulator.Append(buffer.AsSpan(0, read));
            packet = accumulator.TryRead();
        }

        Assert.NotNull(packet);
        Assert.Equal(0xD9, packet!.Command);
    }
}
