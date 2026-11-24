using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ReelBolt.Shared.Storage;

/// <summary>
/// The one place an <see cref="IAmazonS3"/> is configured, for both services. Keys are still
/// named <c>MinIO:*</c> for historical reasons; the backend is Garage (self-hosted) or R2 (cloud).
/// </summary>
public static class S3ClientFactory
{
    /// <summary>DI key of the client signed against <c>MinIO:PublicEndpoint</c>.</summary>
    public const string PresignServiceKey = "presign";

    public static string ResolveEndpoint(IConfiguration configuration) =>
        configuration["MinIO:Endpoint"] ?? "http://localhost:9000";

    /// <summary>
    /// The endpoint a presigned URL must name: SigV4 signs the Host header, so a URL minted against
    /// an in-cluster host (<c>http://garage:3900</c>) is unusable from a desktop runner. Falls back
    /// to <c>MinIO:Endpoint</c> when no public endpoint is configured.
    /// </summary>
    public static string ResolvePublicEndpoint(IConfiguration configuration)
    {
        string? publicEndpoint = configuration["MinIO:PublicEndpoint"];
        return string.IsNullOrWhiteSpace(publicEndpoint) ? ResolveEndpoint(configuration) : publicEndpoint;
    }

    public static IAmazonS3 Create(IConfiguration configuration, string serviceUrl)
    {
        bool forcePathStyle = !bool.TryParse(configuration["MinIO:ForcePathStyle"], out bool parsed) || parsed;
        AmazonS3Config config = new()
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = forcePathStyle,
            // Garage enforces the SigV4 region baked into garage.toml's s3_api.s3_region (R2 wants
            // "auto"). AuthenticationRegion is honored since AWSSDK.S3 3.7.103.13; older versions
            // dropped it silently (aws/aws-sdk-net#2540) and every request 403'd.
            AuthenticationRegion = configuration["MinIO:Region"] ?? "garage",
            // SDK v4's default (WHEN_SUPPORTED) sends uploads as aws-chunked/trailer-signed with a
            // CRC32 checksum, which Garage does not implement ("Invalid payload signature").
            // WHEN_REQUIRED only drops the CRC trailer and the checksum header. It does NOT restore
            // plain single-shot SigV4: PutObject/UploadPart still go out as
            // STREAMING-AWS4-HMAC-SHA256-PAYLOAD, which R2 rejects (see DisableChunkedUploads below).
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        EnsureUploadPipelineCustomizer();
        return new AmazonS3Client(
            configuration["MinIO:AccessKey"] ?? string.Empty,
            configuration["MinIO:SecretKey"] ?? string.Empty,
            config);
    }

    private static readonly object CustomizerLock = new();
    private static bool _customizerRegistered;

    /// <summary>
    /// Registers (once per process) the pipeline customizer that turns off aws-chunked uploads.
    /// <c>Create</c> runs twice per process (main and "presign" client); the customizer's
    /// <see cref="IRuntimePipelineCustomizer.UniqueName"/> is stable and the flag guards the rest.
    /// Must run before the first <see cref="AmazonS3Client"/> is constructed, because the SDK
    /// applies customizers while building a client's pipeline.
    /// </summary>
    internal static void EnsureUploadPipelineCustomizer()
    {
        lock (CustomizerLock)
        {
            if (_customizerRegistered)
            {
                return;
            }

            RuntimePipelineCustomizerRegistry.Instance.Register(new DisableChunkedUploadsCustomizer());
            _customizerRegistered = true;
        }
    }

    /// <summary>
    /// Why this exists: AWSSDK.S3 4.x sends every PutObject/UploadPart as an aws-chunked
    /// streaming-signature body. Cloudflare R2 does not implement it and answers
    /// <c>Amazon.S3.AmazonS3Exception: STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented</c>;
    /// <c>RequestChecksumCalculation.WHEN_REQUIRED</c> does not change that, and neither does a
    /// <c>BeforeRequestEvent</c> hook (it fires after marshalling). The only point that works is a
    /// handler BEFORE the <see cref="Marshaller"/> that flips
    /// <c>UseChunkEncoding = false</c> on the original request. Garage accepts plain single-shot
    /// SigV4 too, so this is unconditional.
    /// A non-seekable body cannot be hashed up front, so over https it also gets
    /// <c>DisablePayloadSigning = true</c> (UNSIGNED-PAYLOAD; TLS protects the body). The SDK
    /// refuses that over plain http, so such a request is left chunked, which Garage over http
    /// accepts.
    /// </summary>
    internal static void DisableChunkedUploads(AmazonWebServiceRequest? request, bool https)
    {
        switch (request)
        {
            case PutObjectRequest put:
                (put.UseChunkEncoding, put.DisablePayloadSigning) = Decide(put.InputStream, https, put.DisablePayloadSigning);
                break;
            case UploadPartRequest part:
                (part.UseChunkEncoding, part.DisablePayloadSigning) = Decide(part.InputStream, https, part.DisablePayloadSigning);
                break;
        }
    }

    private static (bool UseChunkEncoding, bool? DisablePayloadSigning) Decide(Stream? body, bool https, bool? alreadyUnsigned)
    {
        if (body is not { CanSeek: false })
        {
            return (false, alreadyUnsigned);
        }

        // Non-seekable: cannot be hashed up front. https -> unsigned payload; http -> stay chunked.
        return https ? (false, true) : (true, alreadyUnsigned);
    }

    private sealed class DisableChunkedUploadsCustomizer : IRuntimePipelineCustomizer
    {
        public string UniqueName => "ReelBolt.S3.DisableChunkedUploads";

        public void Customize(Type serviceClientType, RuntimePipeline pipeline)
        {
            if (!typeof(AmazonS3Client).IsAssignableFrom(serviceClientType))
            {
                return;
            }

            pipeline.AddHandlerBefore<Marshaller>(new DisableChunkedUploadsHandler());
        }
    }

    private sealed class DisableChunkedUploadsHandler : PipelineHandler
    {
        public override void InvokeSync(IExecutionContext executionContext)
        {
            Apply(executionContext);
            base.InvokeSync(executionContext);
        }

        public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            Apply(executionContext);
            return base.InvokeAsync<T>(executionContext);
        }

        private static void Apply(IExecutionContext executionContext)
        {
            IRequestContext ctx = executionContext.RequestContext;
            string? url = ctx.ClientConfig?.ServiceURL;
            bool https = string.IsNullOrEmpty(url)
                ? !(ctx.ClientConfig?.UseHttp ?? false)
                : url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            DisableChunkedUploads(ctx.OriginalRequest, https);
        }
    }

    /// <summary>
    /// Registers the default <see cref="IAmazonS3"/>, the keyed "presign" client and
    /// <see cref="IPresignedUrlService"/>. Both services call this once in place of their own S3 block.
    /// </summary>
    public static IServiceCollection AddReelBoltS3(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAmazonS3>(_ => Create(configuration, ResolveEndpoint(configuration)));
        services.AddKeyedSingleton<IAmazonS3>(PresignServiceKey,
            (_, _) => Create(configuration, ResolvePublicEndpoint(configuration)));
        services.AddSingleton<IPresignedUrlService>(sp => new PresignedUrlService(
            sp.GetRequiredService<IAmazonS3>(),
            sp.GetRequiredKeyedService<IAmazonS3>(PresignServiceKey),
            configuration["MinIO:BucketName"] ?? "reelforge",
            ResolvePublicEndpoint(configuration)));
        return services;
    }
}
