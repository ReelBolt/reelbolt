#nullable enable
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using ReelBolt.Shared.Storage;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// R2 answers "STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented" to the SDK's default aws-chunked
/// uploads. These tests run real PutObject/UploadPart calls through a real client pipeline whose
/// HTTP stage is stubbed (no network). The customizer mutates the original request before
/// marshalling, so the flags on the request object afterwards are what the SDK actually marshalled.
/// </summary>
public class S3ChunkedUploadCustomizerTests
{
    private sealed class StubFactory : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("") };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"");
            return Task.FromResult(r);
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    private static AmazonS3Client NewClient(string serviceUrl)
    {
        S3ClientFactory.EnsureUploadPipelineCustomizer();
        AmazonS3Config config = new()
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            HttpClientFactory = new StubFactory(),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        return new AmazonS3Client("AK", "SK-SK-SK-SK-SK-SK-SK", config);
    }

    private const string R2 = "https://acct.eu.r2.cloudflarestorage.com";

    [Fact]
    public async Task PutObject_SeekableStream_Https_DisablesChunkEncoding()
    {
        AmazonS3Client client = NewClient(R2);
        PutObjectRequest req = new() { BucketName = "bkt", Key = "k", InputStream = new MemoryStream(new byte[1024]) };
        req.UseChunkEncoding.Should().BeTrue("the SDK default is chunked");

        await client.PutObjectAsync(req);

        req.UseChunkEncoding.Should().BeFalse();
        req.DisablePayloadSigning.Should().NotBe(true, "a seekable body is signed normally");
    }

    [Fact]
    public async Task PutObject_NonSeekableStream_Https_DisablesChunkingAndPayloadSigning()
    {
        AmazonS3Client client = NewClient(R2);
        PutObjectRequest req = new() { BucketName = "bkt", Key = "k", InputStream = new NonSeekableStream(new byte[1024]) };
        req.Headers.ContentLength = 1024;

        await client.PutObjectAsync(req);

        req.UseChunkEncoding.Should().BeFalse();
        req.DisablePayloadSigning.Should().BeTrue();
    }

    [Fact]
    public async Task PutObject_NonSeekableStream_Http_StaysChunkedAndDoesNotThrow()
    {
        AmazonS3Client client = NewClient("http://garage:3900");
        PutObjectRequest req = new() { BucketName = "bkt", Key = "k", InputStream = new NonSeekableStream(new byte[1024]) };
        req.Headers.ContentLength = 1024;

        Func<Task> act = () => client.PutObjectAsync(req);

        await act.Should().NotThrowAsync();
        req.DisablePayloadSigning.Should().NotBe(true, "the SDK refuses unsigned payloads over http");
        req.UseChunkEncoding.Should().BeTrue();
    }

    [Fact]
    public async Task PutObject_SeekableStream_Http_DisablesChunkEncoding()
    {
        AmazonS3Client client = NewClient("http://garage:3900");
        PutObjectRequest req = new() { BucketName = "bkt", Key = "k", InputStream = new MemoryStream(new byte[1024]) };

        await client.PutObjectAsync(req);

        req.UseChunkEncoding.Should().BeFalse();
    }

    [Fact]
    public async Task UploadPart_Https_DisablesChunkEncoding_AndUnsignsNonSeekable()
    {
        AmazonS3Client client = NewClient(R2);
        UploadPartRequest seekable = new()
        {
            BucketName = "bkt",
            Key = "k",
            UploadId = "u",
            PartNumber = 1,
            PartSize = 1024,
            InputStream = new MemoryStream(new byte[1024])
        };
        UploadPartRequest streaming = new()
        {
            BucketName = "bkt",
            Key = "k",
            UploadId = "u",
            PartNumber = 2,
            PartSize = 1024,
            InputStream = new NonSeekableStream(new byte[1024])
        };

        await client.UploadPartAsync(seekable);
        await client.UploadPartAsync(streaming);

        seekable.UseChunkEncoding.Should().BeFalse();
        seekable.DisablePayloadSigning.Should().NotBe(true);
        streaming.UseChunkEncoding.Should().BeFalse();
        streaming.DisablePayloadSigning.Should().BeTrue();
    }

    [Fact]
    public void EnsureUploadPipelineCustomizer_IsIdempotent()
    {
        S3ClientFactory.EnsureUploadPipelineCustomizer();
        Action again = S3ClientFactory.EnsureUploadPipelineCustomizer;
        again.Should().NotThrow();
    }
}
