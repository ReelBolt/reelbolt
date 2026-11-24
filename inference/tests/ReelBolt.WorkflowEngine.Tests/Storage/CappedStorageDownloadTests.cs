using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Storage;

/// <summary>
/// T-364: a capped download (the brand-font stage) enforces the cap on the bytes that actually arrive, while
/// streaming — an object whose declared size lied, or that grew after the HEAD, is never fully copied.
/// </summary>
public sealed class CappedStorageDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "capped-download-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Zeros up to <see cref="Length"/> bytes, counting what was read.</summary>
    private sealed class CountingStream(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - BytesRead);
            Array.Clear(buffer, offset, n);
            BytesRead += n;
            return n;
        }
    }

    private static ProjectFileWorkspace Workspace(Stream body)
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = body });
        return new ProjectFileWorkspace(s3.Object, Mock.Of<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), NullLogger<ProjectFileWorkspace>.Instance);
    }

    [Fact]
    public async Task An_object_over_the_cap_stops_streaming_and_leaves_no_file()
    {
        Guid project = Guid.NewGuid();
        CountingStream body = new(64L * 1024 * 1024);
        string destination = Path.Combine(_dir, "brand-font");
        const long cap = 1024 * 1024;

        Func<Task> act = () => ((IProjectFileWorkspace)Workspace(body)).DownloadStorageKeyToFileAsync(project, $"projects/{project}/userFiles/f.ttf", destination, cap, CancellationToken.None);

        await act.Should().ThrowAsync<StorageObjectTooLargeException>();
        body.BytesRead.Should().BeLessThanOrEqualTo(cap + 256 * 1024, "the cap is enforced while streaming, not after the whole object arrived");
        File.Exists(destination).Should().BeFalse();
    }

    [Fact]
    public async Task An_object_within_the_cap_is_copied_whole()
    {
        Guid project = Guid.NewGuid();
        string destination = Path.Combine(_dir, "brand-font");

        await ((IProjectFileWorkspace)Workspace(new CountingStream(4096))).DownloadStorageKeyToFileAsync(project, $"projects/{project}/userFiles/f.ttf", destination, 4096, CancellationToken.None);

        new FileInfo(destination).Length.Should().Be(4096);
    }

    /// <summary>Yields <c>before</c> bytes, then fails the way a dropped connection does.</summary>
    private sealed class BreakingStream(int before) : Stream
    {
        private int _given;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _given; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_given >= before)
                throw new IOException("connection reset");
            int n = Math.Min(count, before - _given);
            _given += n;
            return n;
        }
    }

    [Fact]
    public async Task A_download_that_breaks_mid_stream_leaves_no_partial_file()
    {
        Guid project = Guid.NewGuid();
        string destination = Path.Combine(_dir, "brand-font");

        Func<Task> act = () => ((IProjectFileWorkspace)Workspace(new BreakingStream(1000))).DownloadStorageKeyToFileAsync(project, $"projects/{project}/userFiles/f.ttf", destination, 4096, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        File.Exists(destination).Should().BeFalse("a half-copied font must never look like a staged one");
    }
}
