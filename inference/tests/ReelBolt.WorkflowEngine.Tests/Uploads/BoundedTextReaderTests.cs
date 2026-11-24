using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.IO;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

public class BoundedTextReaderTests
{
    /// <summary>
    /// An infinite-ish stream of 'a' that never materialises more than the caller asks for, so the
    /// test proves the reader's cap by counting what the stream actually served.
    /// </summary>
    private sealed class CountingStream(long length) : Stream
    {
        private long _remaining = length;

        public long BytesServed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => length - _remaining;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)'a', offset, n);
            _remaining -= n;
            BytesServed += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..n].Fill((byte)'a');
            _remaining -= n;
            BytesServed += n;
            return ValueTask.FromResult(n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Never_pulls_more_than_cap_plus_one_from_a_10_MiB_stream()
    {
        using var stream = new CountingStream(10L * 1024 * 1024);

        BoundedText result = await BoundedTextReader.ReadAsync(stream, 1000, CancellationToken.None);

        stream.BytesServed.Should().BeLessThanOrEqualTo(1001);
        result.Truncated.Should().BeTrue();
        result.Text.Length.Should().Be(1000);
        result.BytesRead.Should().Be(1000);
    }

    [Fact]
    public async Task Exact_cap_is_not_truncated()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 1000)));

        BoundedText result = await BoundedTextReader.ReadAsync(stream, 1000, CancellationToken.None);

        result.Truncated.Should().BeFalse();
        result.Text.Length.Should().Be(1000);
        result.BytesRead.Should().Be(1000);
    }

    [Fact]
    public async Task Empty_stream()
    {
        using var stream = new MemoryStream();

        BoundedText result = await BoundedTextReader.ReadAsync(stream, 1000, CancellationToken.None);

        result.Text.Should().BeEmpty();
        result.Truncated.Should().BeFalse();
        result.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_utf8_becomes_replacement_char_and_does_not_throw()
    {
        using var stream = new MemoryStream(new byte[] { 0xFF, 0x41 });

        BoundedText result = await BoundedTextReader.ReadAsync(stream, 1000, CancellationToken.None);

        result.Text.Should().Contain("\uFFFD");
        result.Text.Should().Contain("A");
    }

    [Fact]
    public async Task Bom_is_dropped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'];
        using var stream = new MemoryStream(bytes);

        BoundedText result = await BoundedTextReader.ReadAsync(stream, 1000, CancellationToken.None);

        result.Text.Should().Be("hi");
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Zero_cap_throws()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BoundedTextReader.ReadAsync(stream, 0, CancellationToken.None));
    }
}
