using System.Text;
using Kagura.Core.Agents;

namespace Kagura.Tests;

public class RingBufferTests
{
    [Fact]
    public void Empty_buffer_snapshots_to_empty_array()
    {
        var rb = new RingBuffer(64);
        Assert.Empty(rb.Snapshot());
        Assert.Equal(0, rb.TotalWritten);
    }

    [Fact]
    public void Below_capacity_writes_are_returned_in_order()
    {
        var rb = new RingBuffer(64);
        rb.Write("hello "u8);
        rb.Write("world"u8);
        Assert.Equal("hello world", Encoding.UTF8.GetString(rb.Snapshot()));
        Assert.Equal(11, rb.TotalWritten);
    }

    [Fact]
    public void At_capacity_returns_exactly_the_window()
    {
        var rb = new RingBuffer(8);
        rb.Write("abcdefgh"u8);
        Assert.Equal("abcdefgh", Encoding.UTF8.GetString(rb.Snapshot()));
        Assert.Equal(8, rb.TotalWritten);
    }

    [Fact]
    public void Over_capacity_in_one_write_keeps_the_tail()
    {
        var rb = new RingBuffer(4);
        rb.Write("abcdefgh"u8);
        Assert.Equal("efgh", Encoding.UTF8.GetString(rb.Snapshot()));
        Assert.Equal(8, rb.TotalWritten);
    }

    [Fact]
    public void Wrapping_writes_keep_the_most_recent_window()
    {
        var rb = new RingBuffer(4);
        rb.Write("ab"u8);
        rb.Write("cd"u8);
        rb.Write("ef"u8);
        Assert.Equal("cdef", Encoding.UTF8.GetString(rb.Snapshot()));
        Assert.Equal(6, rb.TotalWritten);
    }

    [Fact]
    public void Default_capacity_is_256_KB()
    {
        var rb = new RingBuffer();
        Assert.Equal(256 * 1024, rb.Capacity);
    }

    [Fact]
    public void Large_stream_only_retains_last_capacity_bytes()
    {
        var rb = new RingBuffer(16);
        var rng = new Random(1);
        var stream = new byte[10_000];
        rng.NextBytes(stream);
        // Write in oddly sized chunks
        var i = 0;
        while (i < stream.Length)
        {
            var len = Math.Min(rng.Next(1, 50), stream.Length - i);
            rb.Write(stream.AsSpan(i, len));
            i += len;
        }
        var snap = rb.Snapshot();
        Assert.Equal(16, snap.Length);
        Assert.True(stream.AsSpan(stream.Length - 16).SequenceEqual(snap));
    }
}
