using ReelBolt.Shared.Storage;
using System.Text;
using Amazon.S3;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Observability;
using ReelBolt.Shared.DataProtection;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Drain;
using ReelBolt.WorkflowEngine.Drain;
using ReelBolt.Shared.Data;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Analysis;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Agents.Quality;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Agents.Translation;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Guardrails;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Observability;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Skills;
using ReelBolt.WorkflowEngine.Services.Styles;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using ReelBolt.WorkflowEngine.Workers;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine;

/// <summary>
/// The WorkflowEngine's entire service registration, extracted from Program.cs so the composition
/// root can be built and validated by tests (see WorkflowEngineCompositionRootTests): every
/// controller and every <see cref="IStepExecutor"/> must be constructible from exactly what
/// production registers. Program.cs keeps logging, the pipeline and startup migrations.
/// </summary>
public static class WorkflowEngineServiceCollectionExtensions
{
    // Security S-1: the Npgsql meter tags every pool instrument with db.client.connection.pool.name,
    // which otherwise defaults to the connection string minus Password (SSL Password, host, user and
    // database would reach the OTLP collector). A fixed name keeps connection-string material out.
    public const string DataSourceName = "reelbolt-workflow-engine";

    // Npgsql pool cap when neither Database:MaxPoolSize nor the connection string sets one (budget: docs/database-connections.md).
    public const int DefaultMaxPoolSize = 12;

    /// <summary>The engine's inbound bearer scheme. Separate from the composition root so it is testable.</summary>
    public static IServiceCollection AddWorkflowEngineAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // With EngineJwt:SigningKey set (EngineJwtSettings; a bad value throws here, so the engine refuses to
        // start), the engine holds no session key: it accepts ONLY the 60-second admin tokens the Inference
        // API mints with the engine key, and a session token is refused. Without it, nothing changes.
        EngineJwtSettings? engineJwt = EngineJwtSettings.Read(configuration);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = engineJwt is not null
                    ? new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = EngineJwtSettings.AdminIssuer,
                        ValidateAudience = true,
                        ValidAudience = EngineJwtSettings.AdminAudience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = engineJwt.SecurityKey,
                        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                        ValidateLifetime = true,
                        RequireExpirationTime = true
                    }
                    : new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = configuration["Jwt:Issuer"],
                        ValidateAudience = true,
                        ValidAudience = configuration["Jwt:Audience"],
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"] ?? string.Empty)),
                        ValidateLifetime = true
                    };
                // Same database re-check as the Inference API (shared TokenMembershipValidator), in both modes: the
                // user must still exist, iat must not predate tokens_invalidated_at, an org claim needs a live
                // membership, and platformAdmin survives only while application_users.is_admin agrees
                // (AdminController and DecisionCalibrationController read the rewritten claim).
                options.Events = new JwtBearerEvents { OnTokenValidated = ValidateTokenMembershipAsync };
            });
        services.AddMemoryCache();
        services.TryAddSingleton<TokenMembershipValidator>();
        return services;
    }

    /// <summary>
    /// The engine's JwtBearer <c>OnTokenValidated</c> hook. A person's token (GUID <c>sub</c>, including the MCP
    /// <c>assistant.admin</c> token and the engine's own on-behalf-of token) goes through
    /// <see cref="TokenMembershipValidator.ValidatePrincipalAsync"/>; a refusal is a 401. A non-user subject (a runner's
    /// <c>device:</c> token) has no database row to check: it keeps authenticating as before but loses any
    /// org-role/platform-admin authority, so it can never reach an admin route.
    /// </summary>
    public static async Task ValidateTokenMembershipAsync(TokenValidatedContext context)
    {
        if (context.Principal is not { } principal)
        {
            context.Fail("Token has no principal.");
            return;
        }

        string? sub = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sub is not null && sub.StartsWith("device:", StringComparison.Ordinal))
        {
            TokenMembershipValidator.StripAuthorityClaims(principal.Identities.First());
            return;
        }

        IServiceProvider sp = context.HttpContext.RequestServices;
        TokenMembershipValidator validator = sp.GetRequiredService<TokenMembershipValidator>();
        WorkflowEngineDbContext db = sp.GetRequiredService<WorkflowEngineDbContext>();
        if (await validator.ValidatePrincipalAsync(principal, db, context.HttpContext.RequestAborted) is { } failure)
        {
            sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(WorkflowEngineServiceCollectionExtensions))
                .LogWarning("Rejected token for subject {Subject}: {Reason}", sub, failure);
            context.Fail(failure);
        }
    }

    public static IServiceCollection AddWorkflowEngineServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // --- Database ---
        // AddDbContextFactory (not AddDbContext): the singleton video-generation gate/executor need
        // IDbContextFactory<WorkflowEngineDbContext>, and EF Core's AddDbContextFactory also registers
        // WorkflowEngineDbContext itself as Scoped, so every existing scoped consumer is unaffected.
        // The connection string is resolved once here, so a bad Database:MaxPoolSize fails startup.
        string? connectionString = configuration.GetConnectionString("DefaultConnection");
        if (connectionString is not null)
            connectionString = PostgresLiveness.Apply(
                PostgresPoolSizing.ApplyMaxPoolSize(connectionString, configuration, DefaultMaxPoolSize));
        services.AddDbContextFactory<WorkflowEngineDbContext>(options =>
            options.UseNpgsql(connectionString,
                npgsql => npgsql
                    .MigrationsHistoryTable("__EFMigrationsHistory_Workflow")
                    .ConfigureDataSource(dataSource => dataSource.Name = DataSourceName)));

        // --- Authentication ---
        services.AddWorkflowEngineAuthentication(configuration);
        services.AddAuthorization();
        services.AddMemoryCache();
        services.TryAddSingleton<TokenMembershipValidator>();

        // --- Inference provider abstraction ---
        // File-system ring by default; DataProtection:Store=Database moves it into data_protection_keys
        // (see DataProtectionRegistration; the application name there must never change, D20).
        services.AddReelBoltDataProtection<WorkflowEngineDbContext>(configuration, readOnlyRing: true);

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        // A LocalRunner provider reaches the user's own model through the runner gateway's relay: the same
        // Compute:GatewayBaseUrl / Compute:InternalToken (or RUNNER_INTERNAL_TOKEN) the compute fabric uses.
        services.Configure<LocalRunnerGatewayOptions>(configuration.GetSection(LocalRunnerGatewayOptions.SectionName));
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<LocalRunnerGatewayOptions>, LocalRunnerGatewayOptionsValidator>();
        services.AddOptions<LocalRunnerGatewayOptions>().ValidateOnStart();
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddScoped<IInferenceProviderStore, WorkflowEngineProviderStore>();
        // One resolver snapshot per organization; the accessor reads the execution's organization
        // (IWorkflowExecutionContextAccessor is registered further down).
        services.AddSingleton<IInferenceTenantAccessor, EngineInferenceTenantAccessor>();
        services.AddReelBoltEntitlements(configuration);
        services.AddReelBoltUsageRecorder<WorkflowEngineDbContext>();
        services.AddReelBoltMeteringOptions(configuration);
        services.AddReelBoltPlanEntitlements<WorkflowEngineDbContext>(configuration);
        services.AddSingleton<IInferenceProviderResolver, InferenceProviderResolver>();
        // Cost routing (see docs/decision-models.md § cost routing) decorates the chat-client seam,
        // so it applies to every Agent step without any agent, agent base class or additional
        // executor statement knowing it exists. The inner provider stays registered under its own
        // concrete type, and the interface is the decorator: with no ambient routing intent (the
        // default, and the only state any deployment that has not opted in ever reaches) the
        // decorator is a pure pass-through, so the resolved behaviour is AgentChatClientProvider's.
        services.AddSingleton<AgentChatClientProvider>();
        services.AddSingleton<Execution.Decision.CostRouting.CostRoutingAmbientState>();
        services.AddSingleton<Execution.Decision.CostRouting.ICostRoutingGate, Execution.Decision.CostRouting.CostRoutingGate>();
        services.AddSingleton<IAgentChatClientProvider>(sp => new Execution.Decision.CostRouting.CostRoutingChatClientProvider(
            sp.GetRequiredService<AgentChatClientProvider>(),
            sp.GetRequiredService<IInferenceProviderResolver>(),
            sp.GetRequiredService<IChatClientFactory>(),
            sp.GetRequiredService<Execution.Decision.CostRouting.CostRoutingAmbientState>(),
            sp.GetRequiredService<ILogger<Execution.Decision.CostRouting.CostRoutingChatClientProvider>>()));
        services.AddSingleton<ITranscriptionClientFactory, TranscriptionClientFactory>();
        // Voiceover word alignment re-transcribes an already-emitted line WAV through the same
        // ITranscriptionClient seam, so it registers beside the factory it consumes. Singleton for
        // the same reason VoiceoverStepExecutor — its only consumer — is: it holds no per-execution
        // mutable state, only singletons (resolver, transcription factory, workspace, logger).
        services.AddSingleton<IVoiceoverAlignmentService, VoiceoverAlignmentService>();
        // Reads project_voices through the engine's read-only mapping, per call, inside its own
        // scope — singleton for the same reason the alignment service is.
        services.AddSingleton<IProjectVoiceResolver, ProjectVoiceResolver>();
        // Avatars (docs/avatars.md): read per call in its own scope, references hashed as they are now. Phase 1 has no
        // consumer that spends money; an unusable avatar fails closed, never "no avatar".
        services.AddSingleton<Services.Avatars.IAvatarReferenceDigestReader, Services.Avatars.S3AvatarReferenceDigestReader>();
        services.AddSingleton<Services.Avatars.IAvatarResolver, Services.Avatars.AvatarResolver>();
        services.AddSingleton<Services.Avatars.IAvatarReuseGuard, Services.Avatars.AvatarReuseGuard>();
        // Avatars phase 3, the still-then-animate path: the speech that drives a talking head and the still it animates.
        services.AddSingleton<Services.Avatars.IAvatarSpeechTrackSource, Services.Avatars.AvatarSpeechTrackSource>();
        services.AddSingleton<Services.Avatars.IAvatarStillMaker, Services.Avatars.AvatarStillMaker>();
        // Object fidelity (docs/design/avatar-objects.md): one Vision call per take that shows an object, against its pictures and
        // identity facts; fail open (no provider = unavailable, never a failed step).
        services.AddSingleton<Services.Avatars.ISubjectFidelityFrameSource, Services.Avatars.FfmpegSubjectFidelityFrameSource>();
        services.AddSingleton<Services.Avatars.ISubjectFidelityCheck, Services.Avatars.VisionSubjectFidelityCheck>();
        // Read through the same tolerant helper as the Inference API (ReelBolt.Shared.Avatars.AvatarSwitchConfiguration), so
        // the two cannot disagree: a switch that cannot be read is off, never an exception; a number is not a mode. The
        // object switch is nested (Avatars:Objects:Enabled, the key the API reads) (docs/design/avatar-objects.md).
        services.AddOptions<Services.Avatars.AvatarGenerationOptions>()
            .Configure(o => Services.Avatars.AvatarGenerationOptions.Apply(o, configuration));
        services.AddHostedService<Services.Avatars.AvatarSwitchStartupCheck>();
        // The project pronunciation glossary (written-accent respellings), read per call in its own scope.
        services.AddSingleton<IProjectPronunciationResolver, ProjectPronunciationResolver>();
        // Re-checks those voices when a recorded Voiceover result is reused (cache hit / resume / replay).
        services.AddSingleton<IVoiceReuseGuard, VoiceReuseGuard>();
        services.AddSingleton<IDecisionClientFactory, DecisionClientFactory>();
        services.AddSingleton<ISpeechSynthesisClientFactory, SpeechSynthesisClientFactory>();
        // The image arm (ImageGeneration / ImageEdit). Registered in the engine as well as in the
        // Inference API because the engine is where generation and editing actually run; the factory
        // is a singleton so its per-configuration client cache is worth anything, and it takes the
        // singleton IUsageRecorder so every returned image is metered where it is produced.
        services.AddSingleton<IImageClientFactory, ImageClientFactory>();
        // The subject-detection arm (Segmentation / Depth) — fal.ai behind one queue contract. The
        // editor is what resolves it today; the engine registers it so a step that needs a matte or a
        // depth map resolves against the same provider rows rather than a second configuration path.
        services.AddSingleton<IImageAnalysisClientFactory, ImageAnalysisClientFactory>();
        services.AddSingleton<IDecisionGate, ReelBolt.WorkflowEngine.Execution.Decision.DecisionGate>();
        services.AddScoped<ReelBolt.WorkflowEngine.Services.Decision.IDecisionCalibrationService, ReelBolt.WorkflowEngine.Services.Decision.DecisionCalibrationService>();

        // Singleton because the memo is keyed by EXECUTION id, not by request scope: the execution
        // runner (WorkflowExecutionRunner) is a singleton IHostedService, so a scoped registration
        // would live and die with the message scope and would not be per-execution. Bounded
        // (oldest-first cap + Evict on completion) — the completion call is
        // WorkflowExecutionRunner.RunAsync's finally, the one exit every execution takes exactly
        // once whether it completed, failed or was cancelled — so it can never grow without limit.
        services.AddSingleton<IDecisionQuestionMemoizer, DecisionQuestionMemoizer>();

        // --- Guardrails (hard rules + probabilistic tool-call/input screening) ---
        // Singleton for the same reason IDecisionGate is: it depends only on singletons
        // (IInferenceProviderResolver, IDecisionClientFactory, IOptions, ILogger). Defaults are Off /
        // bounded, and the shipped configuration carries no hard rules, so this registration changes
        // no behaviour until an operator configures it.
        services.Configure<GuardrailConfig>(configuration.GetSection(GuardrailConfig.SectionName));
        services.AddSingleton<IGuardrailScreener, GuardrailScreener>();
        // A service only: nothing calls it yet. The media manager wires it into the VideoAnalyze view,
        // and with no Decision provider configured it flags nothing and renders an empty block — so an
        // unwired or provider-less install is byte-identical to one without this registration.
        services.AddSingleton<IInputSpanScreener, InputSpanScreener>();

        // --- Content moderation (content-moderation T-005) ---
        // The engine gate resolves the moderation provider, records one $0 usage event and appends an
        // audit row through the engine's read-only mapping. Every dependency is a singleton (the usage
        // recorder and IDbContextFactory already are), so the gate is too.
        services.Configure<ContentModerationOptions>(configuration.GetSection(ContentModerationOptions.SectionName));
        // Narration spend bound (docs/voiceover.md "Fit"): the per-step admitted-words ceiling.
        services.Configure<VoiceoverFitOptions>(configuration.GetSection(VoiceoverFitOptions.SectionName));
        services.AddSingleton<IContentModerationClientFactory, ContentModerationClientFactory>();
        // Web search (agent-web-search): builds the METERED client from a resolved WebSearch provider. Takes the
        // optional singleton IUsageRecorder, so searches are metered where they run.
        services.AddSingleton<IWebSearchClientFactory, WebSearchClientFactory>();
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<IModerationMediaSampler>(sp => new FfmpegModerationMediaSampler(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IOptions<ContentModerationOptions>>().Value));
        services.AddSingleton<IContentModerationGate, Services.Moderation.ContentModerationGate>();

        // --- Video editing (ffmpeg/ffprobe pipeline) ---
        services.Configure<VideoEditingOptions>(configuration.GetSection(VideoEditingOptions.SectionName));
        // IVideoToolRunner MUST be a singleton: its concurrency-limiting semaphore (R13) is only
        // process-wide if exactly one instance exists for the lifetime of the WorkflowEngine process.
        services.AddSingleton<IVideoToolRunner, FfmpegVideoToolRunner>();

        // --- Media derivatives (editor v2 P2-M1: the media_derivatives writer) ---
        // MediaDerivativeService is pure production (an already-downloaded source in, local artifacts
        // out), so it is a singleton like the runner it drives.
        services.AddSingleton<MediaDerivativeService>();
        // The engine-owned progress seam. Its default implementation logs a structured line per stage
        // transition and raises an OTel ActivityEvent on the run's span, so progress is observable
        // today without publishing WorkflowStepProgress under invented step ids — see
        // IMediaDerivativeProgressSink.
        services.AddSingleton<IMediaDerivativeProgressSink, LoggingMediaDerivativeProgressSink>();
        // Singleton + hosted service, exactly like WorkflowExecutionRunner and for the same reason:
        // the run must survive the message scope it was handed off from, and its cancellation must be
        // tied to the process lifetime rather than to the transport's. Registered as BOTH so the
        // consumer can inject it directly and the host still drives Start/Stop on that one instance.
        services.AddSingleton<MediaDerivativeDispatcher>();
        services.AddSingleton<MediaAnalysisService>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Segmentation.SegmentationMaskPreviewService>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Segmentation.SegmentationMaskPreviewDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ReelBolt.WorkflowEngine.Services.Segmentation.SegmentationMaskPreviewDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<MediaDerivativeDispatcher>());

        // Recipe marketplace preview job (off unless Marketplace:Enabled): runs on the derivative lane, see MarketplacePreviewDispatcher.
        services.Configure<ReelBolt.WorkflowEngine.Services.Marketplace.MarketplacePreviewOptions>(o =>
        {
            configuration.GetSection(ReelBolt.WorkflowEngine.Services.Marketplace.MarketplacePreviewOptions.SectionName).Bind(o);
            o.Enabled = configuration.GetValue<bool>("Marketplace:Enabled");
        });
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Marketplace.IMarketplacePreviewStorage, ReelBolt.WorkflowEngine.Services.Marketplace.S3MarketplacePreviewStorage>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Marketplace.MarketplacePreviewService>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Marketplace.MarketplacePreviewDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ReelBolt.WorkflowEngine.Services.Marketplace.MarketplacePreviewDispatcher>());

        // --- Video generation (options, spending gate, client factory, normalizer, step executor) ---
        services.AddVideoGeneration(configuration);
        services.AddSingleton<IScratchDiskGuard, ScratchDiskGuard>();
        services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
        services.AddSingleton<ISilenceDetector, FfmpegSilenceDetector>();
        services.AddSingleton<IShotDetector, FfmpegShotDetector>();
        // The per-source pass cache VideoAnalyze reads and writes (VideoEditing:SourcePassCache:Enabled, default on).
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Caching.ISourcePassCache, ReelBolt.WorkflowEngine.Services.Video.Caching.SourcePassCache>();
        // Fills that cache for a video right after its derivatives, when the request asks for it
        // (VideoEditing:SourcePassCache:WarmOnUpload, default on). The derivative dispatcher takes it as an optional dependency.
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Caching.SourcePassWarmUp>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Events.FrameSignalSampler>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Events.VideoEventDetector>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Events.IVideoEventDetector>(sp => sp.GetRequiredService<ReelBolt.WorkflowEngine.Services.Video.Events.VideoEventDetector>());
        services.AddSingleton<IAudioExtractor, FfmpegAudioExtractor>();
        services.Configure<ReelBolt.WorkflowEngine.Services.Video.Cleanup.LocalCleanupOptions>(
            configuration.GetSection(ReelBolt.WorkflowEngine.Services.Video.Cleanup.LocalCleanupOptions.SectionName));
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Cleanup.ILocalAudioCleaner, ReelBolt.WorkflowEngine.Services.Video.Cleanup.LocalAudioCleaner>();
        services.Configure<ReelBolt.WorkflowEngine.Services.Video.Enhance.LocalEnhanceOptions>(
            configuration.GetSection(ReelBolt.WorkflowEngine.Services.Video.Enhance.LocalEnhanceOptions.SectionName));
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Enhance.ILocalVideoEnhancer, ReelBolt.WorkflowEngine.Services.Video.Enhance.LocalVideoEnhancer>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Enhance.EnhancedAudioRemux>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Enhance.IEnhancedAudioRemux>(sp => sp.GetRequiredService<ReelBolt.WorkflowEngine.Services.Video.Enhance.EnhancedAudioRemux>());
        services.AddSingleton<IFrameGridSampler, FfmpegFrameGridSampler>();
        services.AddSingleton<IKeyframeExtractor, FfmpegKeyframeExtractor>();
        services.AddSingleton<ISharpnessSampler, FfmpegSharpnessSampler>();
        services.AddSingleton<IShotCaptioner, VisionShotCaptioner>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Events.IEventDescriber, ReelBolt.WorkflowEngine.Services.Video.Events.VisionEventDescriber>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Tracking.IObjectLocator, ReelBolt.WorkflowEngine.Services.Video.Tracking.VisionObjectLocator>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Tracking.IObjectTrackingService, ReelBolt.WorkflowEngine.Services.Video.Tracking.ObjectTrackingService>();

        // Text-prompted video segmentation (initiative "prompted region effects"): the content-addressed
        // mask store, the fal client factory and the analyze pass that calls a hosted segmenter.
        // Singletons for the same reason their siblings are — stateless beyond their dependencies — and
        // the factory additionally caches one HttpClient per (provider, frame geometry). Without the
        // factory registration every segmentation target refused SEGMENTATION_NO_PROVIDER even with a
        // configured VideoSegmentation provider row; it is registered here so the capability can run.
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Segmentation.ISegmentationMaskStore,
            ReelBolt.WorkflowEngine.Services.Segmentation.SegmentationMaskStore>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Tracking.IVideoSegmentationClientFactory,
            ReelBolt.WorkflowEngine.Services.Video.Tracking.FalAiVideoSegmentationClientFactory>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.Tracking.ISegmentationTrackingService,
            ReelBolt.WorkflowEngine.Services.Video.Tracking.SegmentationTrackingService>();

        // --- MinIO / S3 ---
        services.AddReelBoltS3(configuration);

        // --- Compute fabric: verification of runner-produced output (D12) ---
        // The verifier and its store are singletons because both are stateless beyond their
        // dependencies (the S3 clients and IVideoToolRunner they use are already singletons). Nothing
        // resolves them yet — D7b/D8 are the callers — but they are registered here so the feature is
        // resolvable from the composition root the moment its callers land, and so
        // WorkflowEngineCompositionRootTests exercises them.
        services.Configure<ReelBolt.WorkflowEngine.Services.Compute.OutputVerificationOptions>(
            configuration.GetSection(ReelBolt.WorkflowEngine.Services.Compute.OutputVerificationOptions.SectionName));
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Compute.IRunnerOutputStore,
            ReelBolt.WorkflowEngine.Services.Compute.S3RunnerOutputStore>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Compute.IOutputVerifier,
            ReelBolt.WorkflowEngine.Services.Compute.OutputVerifier>();

        // --- Compute fabric: target selection and the gateway client (D6) ---
        // Compute:Mode defaults to Local and the whole block is inert until it is set to Fabric:
        // ComputeTargetSelector answers Local before reading the database or calling the gateway, so a
        // self-host install that has never heard of the fabric behaves exactly as it did before.
        // The gateway's internal API is a REQUIRED shared token and answers 503 when the gateway has
        // none, which the client reports as "the fabric is not deployed" rather than as an error.
        services.Configure<ComputeOptions>(configuration.GetSection(ComputeOptions.SectionName));
        services.PostConfigure<ComputeOptions>(o => o.PoolStorageEndpoint = ReelBolt.Shared.Storage.S3ClientFactory.ResolvePublicEndpoint(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(RunnerGatewayClient.HttpClientName, client =>
            client.BaseAddress = new Uri(configuration["Compute:GatewayBaseUrl"] ?? "http://runner-gateway:8090", UriKind.Absolute));
        services.AddSingleton<IExecutionSessionGate, ExecutionSessionGate>();
        services.AddSingleton<IComputeEntitlements, DbComputeEntitlements>();
        services.AddSingleton<RunnerGatewayClient>();
        services.AddSingleton<IComputeTarget, ComputeTargetSelector>();

        // --- Compute fabric: media sessions (D11) ---
        // The factory is the ONLY place an executor gets an IMediaWorkspace from, so "where does this
        // media step run" is answered by the D6 selector and nowhere else. With Compute:Mode=Local it
        // returns the very LocalMediaWorkspace the executors used to build for themselves, which is
        // what keeps docker compose (and the full .NET test suite) byte-identical.
        services.Configure<ReelBolt.WorkflowEngine.Services.Compute.RemoteMediaOptions>(
            configuration.GetSection(ReelBolt.WorkflowEngine.Services.Compute.RemoteMediaOptions.SectionName));
        services.PostConfigure<ReelBolt.WorkflowEngine.Services.Compute.RemoteMediaOptions>(o =>
        {
            if (o.MaxStageBytes <= 0)
                o.MaxStageBytes = ReelBolt.Shared.Workflows.VideoSourceLimits.From(configuration).MaxBytes;
        });
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Compute.IMediaWorkspaceFactory,
            ReelBolt.WorkflowEngine.Services.Compute.MediaWorkspaceFactory>();

        // --- Agents ---
        services.AddSingleton<IReelBoltAgent, CodeStructureAnalyzerAgent>();
        services.AddSingleton<IReelBoltAgent, DependencyAnalyzerAgent>();
        services.AddSingleton<IReelBoltAgent, ComponentInventoryAnalyzerAgent>();
        services.AddSingleton<IReelBoltAgent, RouteAndApiAnalyzerAgent>();
        services.AddSingleton<IReelBoltAgent, StyleAndThemeExtractorAgent>();
        services.AddSingleton<IReelBoltAgent, RemotionComponentTranslatorAgent>();
        services.AddSingleton<IReelBoltAgent, AnimationStrategyAgentImpl>();
        services.AddSingleton<IReelBoltAgent, DirectorAgentImpl>();
        services.AddSingleton<IReelBoltAgent, ScriptwriterAgentImpl>();
        services.AddSingleton<IReelBoltAgent, AuthorAgentImpl>();
        services.AddSingleton<IReelBoltAgent, ReviewAgentImpl>();
        services.AddSingleton<IReelBoltAgent, VideoStoryEditorAgent>();
        services.AddSingleton<IReelBoltAgent, MotionGraphicsPlannerAgent>();
        services.AddSingleton<IReelBoltAgent, VideoReviewAgentImpl>();
        services.AddSingleton<IReelBoltAgent, MusicSupervisorAgent>();
        services.AddSingleton<IReelBoltAgent, VideoEditDirectorAgent>();
        services.AddSingleton<IReelBoltAgent, MotionGraphicsDirectorAgent>();
        services.AddSingleton<IReelBoltAgent, ColoristAgent>();
        services.AddSingleton<IReelBoltAgent, ColorGradeDirectorAgent>();
        services.AddSingleton<IReelBoltAgent, SoundDesignerAgent>();
        services.AddSingleton<IReelBoltAgent, ShotDirectorAgent>();
        services.AddSingleton<IReelBoltAgent, NarrationWriterAgent>();
        services.AddSingleton<IReelBoltAgent, PickupPlannerAgent>();
        services.AddSingleton<IReelBoltAgent, NarrationTranslatorAgent>();
        services.AddSingleton<IReelBoltAgent, CompositionReviserAgent>();
        services.AddSingleton<IReelBoltAgent, GraphicDesignerAgent>();
        services.AddSingleton<IReelBoltAgent, TakeSelectorAgent>();
        services.AddSingleton<IReelBoltAgent, DailiesDirectorAgent>();
        services.AddSingleton<IReelBoltAgent, OverlayPlannerAgent>();
        services.AddSingleton<IReelBoltAgent, MotionDirectorAgent>();
        services.AddSingleton<IReelBoltAgent, DynamicsPlannerAgent>();
        services.AddSingleton<IAgentRegistry, AgentRegistry>();
        services.AddSingleton<IAgentToolProvider, AgentToolProvider>();
        services.AddSingleton<IProjectFileWorkspace, ProjectFileWorkspace>();
        services.AddSingleton<ProjectFileAgentTools>();
        // DesignSystemRead tools (SPEC §12): read-only over the run's pinned design/product documents.
        services.AddSingleton<DesignSystemAgentTools>();
        // EventsRead tools: the progressive-context windows onto the run's own video analysis (free, read-only).
        services.AddSingleton<VideoEventsAgentTools>();
        // MomentLook tool: an on-request look at one moment of the run's own analysis (one small, metered Vision call).
        services.AddSingleton<MomentLookAgentTools>();
        // WebSearch tool (agent-web-search): caps and the kill switch come from the "WebSearch" section.
        services.Configure<WebSearchToolOptions>(configuration.GetSection(WebSearchToolOptions.SectionName));
        services.AddSingleton<WebSearchAgentTools>();
        // The sandbox seam (D7a): every sandbox operation the React/Remotion tools perform goes
        // through ISandboxSession. Self-host resolves it to the sandbox-executor over HTTP (D7b's
        // factory answers exactly that when Compute:Mode is Local, before it reads the database or the
        // gateway); a fabric deployment gets the runner-backed session with its workspace journal.
        services.AddSingleton<LocalSandboxSessionFactory>();
        services.Configure<SandboxSessionOptions>(configuration.GetSection(SandboxSessionOptions.SectionName));
        services.AddSingleton<ISandboxJournalStore, S3SandboxJournalStore>();
        services.AddSingleton<SandboxWorkspaceJournal>();
        services.AddSingleton<ISandboxExecutionScopeResolver, DbSandboxExecutionScopeResolver>();
        services.AddSingleton<ComputeSandboxSessionFactory>();
        services.AddSingleton<ISandboxSessionFactory>(sp => sp.GetRequiredService<ComputeSandboxSessionFactory>());
        // The execution-end hook (D7b step 4): WorkflowExecutionRunner closes the pinned session and
        // seals its journal from RunAsync's finally, whatever the execution's outcome.
        services.AddSingleton<ISandboxSessionRegistry>(sp => sp.GetRequiredService<ComputeSandboxSessionFactory>());
        // Composition revisions (docs/design/composition-revisions.md): the library's layers, each
        // behind its own interface — store (rows + archives), capture/restore (sandbox transfer), and
        // the revision runner the Revise step (and, later, the editor's live mode) drives. Registered
        // before the sandbox tools, which capture a workspace after every successful video render.
        services.AddSingleton<Services.Compositions.ICompositionWorkspaceStore, Services.Compositions.CompositionWorkspaceStore>();
        services.AddSingleton<Services.Compositions.ICompositionWorkspaceCapture, Services.Compositions.CompositionWorkspaceCapture>();
        services.AddSingleton<Services.Compositions.ICompositionWorkspaceRestorer, Services.Compositions.CompositionWorkspaceRestorer>();
        services.AddSingleton<Services.Compositions.ICompositionRevisionRunner, Services.Compositions.CompositionRevisionRunner>();
        // Designed graphics for the picture editor (docs/editor.md "Designed graphics"): the request
        // store (the API-owned request row) and the runner the DesignGraphic step drives. It reuses the
        // composition library's store, capture and restore for the graphic's source.
        services.AddSingleton<Services.Graphics.IGraphicDesignRequestStore, Services.Graphics.GraphicDesignRequestStore>();
        services.AddSingleton<Services.Graphics.IGraphicDesignRunner, Services.Graphics.GraphicDesignRunner>();
        // StageFont's Google Fonts source (docs/design/sandbox-customization.md 2.1): flag-gated, default off;
        // the client never follows redirects and the fetcher pins host, commit, size and hash.
        services.Configure<Services.Fonts.GoogleFontsOptions>(configuration.GetSection(Services.Fonts.GoogleFontsOptions.SectionName));
        services.AddSingleton<Services.Fonts.IFontBlobCache, Services.Fonts.S3FontBlobCache>();
        services.AddHttpClient<Services.Fonts.IGoogleFontFetcher, Services.Fonts.GoogleFontFetcher>(Services.Fonts.GoogleFontFetcher.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.AddSingleton<ReactRemotionSandboxTools>();
        // A restored workspace's oversized videos get the same delivery copy a fresh staging makes
        // (docs/sandbox-service.md "Oversized staged videos"); the restorer calls it after every import.
        services.AddSingleton<Services.Compositions.IRestoredWorkspaceVideos>(sp => sp.GetRequiredService<ReactRemotionSandboxTools>());
        // workflow control tools provide helpers such as FailWorkflow
        services.AddSingleton<WorkflowControlAgentTools>();
        // Skills: loads the vendored SKILL.md corpus (inference/skills/, Skills:CorpusPath) once, and
        // binds UseSkill/ReadSkillResource tool instances to a resolved skill set per agent construction
        // (see ReelBoltAgentBase.CreateAgentAsync) — replaces the old live-GitHub-fetch RemotionSkills*.
        services.AddSingleton<ISkillCorpusService, SkillCorpusService>();
        // Block library (inference/blocks/, Blocks:CorpusPath): hash-verified catalog loaded once, and the
        // ListBlocks/GetBlock/AddBlock tools over it (ToolGroup.SandboxBlocks).
        services.AddSingleton<Services.Blocks.IBlockCatalog, Services.Blocks.BlockCatalog>();
        services.AddSingleton<BlockAgentTools>();
        services.AddSingleton<ISkillAgentToolsFactory, SkillAgentToolsFactory>();

        services.AddSingleton<IWorkflowExecutionContextAccessor, WorkflowExecutionContextAccessor>();
        services.AddHttpClient();
        // The ONE client for engine -> Inference API calls: attaches the execution initiator's
        // short-lived on-behalf-of bearer (InferenceApiClientDriftGuardTests enforces exclusivity).
        services.AddSingleton<OnBehalfOfTokenIssuer>(sp => new OnBehalfOfTokenIssuer(sp.GetRequiredService<IConfiguration>()));
        // The engine has no API-key audit trail of its own; scope refusals are a no-op there.
        services.TryAddSingleton<IScopeRefusalAuditor, NoOpScopeRefusalAuditor>();
        services.AddSingleton<IExecutionUserResolver, ExecutionUserResolver>();
        services.AddTransient<InferenceApiAuthHandler>();
        services.AddHttpClient(InferenceApiClient.Name, client =>
            client.BaseAddress = new Uri(configuration["InferenceApi:BaseUrl"] ?? "http://inference:8080", UriKind.Absolute))
            .AddHttpMessageHandler<InferenceApiAuthHandler>();
        services.Configure<WorkflowHardeningOptions>(configuration.GetSection(WorkflowHardeningOptions.SectionName));

        // Deterministic, prompt-only bound on how much prior-step output is concatenated into an agent's
        // input (see docs/workflow-execution-optimization.md). Purely additive: StepExecutionContext
        // treats a null BudgetOptions as "budgeting off" and reproduces the previous concatenation
        // byte-for-byte, so this binding is the only thing that turns the feature on.
        services.Configure<AgentInputBudgetOptions>(configuration.GetSection(AgentInputBudgetOptions.SectionName));

        // Optional, DEFAULT-OFF relevance signal over that budget (context relevance, issue #95 §3j):
        // when the composed input is over budget, a Decision model ranks the droppable prior-step parts
        // so the least relevant one is given up first instead of the oldest. Off unless the nested
        // WorkflowEngine:ContextBudget:Relevance section is enabled AND a Decision provider resolves;
        // AgentInputBudget.Apply's optional ordering parameter defaults to null, which reproduces the
        // oldest-first drop order byte-for-byte, so leaving these two registrations out would also be a
        // complete no-op rather than a broken feature.
        services.Configure<ContextRelevanceOptions>(configuration.GetSection(ContextRelevanceOptions.SectionName));
        services.AddSingleton<IContextRelevanceSelector, ContextRelevanceSelector>();
        // Attached to the options instance the executor ALREADY hands every StepExecutionContext, so the
        // context reaches the selector without a new constructor parameter anywhere. Post-configure (not
        // configure) so a section binding can never overwrite it, and the setter is internal precisely so
        // the binder never sees it as a bindable property in the first place.
        services.AddOptions<AgentInputBudgetOptions>()
            .PostConfigure<IContextRelevanceSelector>(
                (budgetOptions, relevanceSelector) => budgetOptions.RelevanceSelector = relevanceSelector);

        // --- Cross-execution step-result cache ---
        // Lets a repeated execution reuse a prior successful step's output instead of re-calling the agent
        // or re-running ffmpeg; see docs/workflow-execution-optimization.md for the key composition and
        // for why side-effecting agents (ProjectWrite/SandboxAuthoring/SandboxRender) are never cached.
        // WorkflowExecutorService resolves all of this optionally, so leaving these registrations out
        // makes the cache a complete no-op rather than a broken feature.
        services.Configure<StepCacheOptions>(configuration.GetSection(StepCacheOptions.SectionName));
        // Pure and stateless — safe as singletons, and WorkflowExecutorService takes them directly.
        services.AddSingleton<IStepCachePolicy, StepCachePolicy>();
        services.AddSingleton<IStepCacheKeyBuilder, StepCacheKeyBuilder>();
        // Both depend on the Scoped WorkflowEngineDbContext, so they must be Scoped too.
        services.AddScoped<IProjectFileFingerprintProvider, ProjectFileFingerprintProvider>();
        services.AddScoped<ReelBolt.WorkflowEngine.Services.Video.ISfxLibraryLookup, ReelBolt.WorkflowEngine.Services.Video.EfSfxLibraryLookup>();
        services.AddScoped<IStepResultCache, StepResultCache>();

        // --- Editing styles (docs/editing-styles.md) ---
        // Stateless: turns a run's style + format override into per-agent prompt nudges.
        services.AddSingleton<IStyleNudgeService, StyleNudgeService>();

        // --- Design system ambient (docs/design-system.md) ---
        services.Configure<DesignSystemAmbientOptions>(configuration.GetSection(DesignSystemAmbientOptions.SectionName));
        // Depends on the scoped WorkflowEngineDbContext (read-only design_system_versions).
        services.AddScoped<PinnedDocumentLoader>();

        // --- Step Executors ---
        // Consumed by AgentStepExecutor (AgentType.MotionGraphicsPlanner steps) and by
        // GraphicsRoomStepExecutor (StepType.GraphicsRoom's inEdit view enrichment).
        services.AddSingleton<IMotionGraphicsPlacementAnnotator, MotionGraphicsPlacementAnnotator>();
        services.AddSingleton<IDynamicsPlannerViewBuilder, DynamicsPlannerViewBuilder>();
        // Placement stills the graphics agents see (docs/design/graphics-see-and-size.md): one metered
        // Vision call per graphics step; "unavailable" without a Vision provider.
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.IPlacementStillGrabber, ReelBolt.WorkflowEngine.Services.Video.FfmpegPlacementStillGrabber>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Services.Video.IPlacementStillsDescriber, ReelBolt.WorkflowEngine.Services.Video.PlacementStillsDescriber>();
        services.AddSingleton<IStepExecutor, AgentStepExecutor>();
        services.AddSingleton<IStepExecutor, ConditionalStepExecutor>();
        services.AddSingleton<IStepExecutor, ForEachStepExecutor>();
        services.AddSingleton<IStepExecutor, ReviewLoopStepExecutor>();
        services.AddSingleton<IStepExecutor, ParallelStepExecutor>();
        services.AddSingleton<IStepExecutor, ExtractStepExecutor>();
        services.AddSingleton<IStepExecutor, VideoAnalyzeStepExecutor>();
        services.AddSingleton<IStepExecutor, VideoCompileStepExecutor>();
        services.AddSingleton<IStepExecutor, EditRoomStepExecutor>();
        services.AddSingleton<IStepExecutor, GraphicsRoomStepExecutor>();
        services.AddSingleton<IStepExecutor, ColorGradeRoomStepExecutor>();
        services.AddSingleton<IStepExecutor, DailiesRoomStepExecutor>();
        services.AddSingleton<Execution.Replay.IStepChangeRequestContext, Execution.Replay.StepChangeRequestContext>();
        services.AddSingleton<INarrationLineShortener, NarrationLineShortener>();
        services.AddSingleton<IStepExecutor, VoiceoverStepExecutor>();
        services.AddSingleton<IStepExecutor, ThumbnailGenerateStepExecutor>();
        services.AddSingleton<Services.Compositions.IAlsoRemadeNoteSource, Services.Compositions.AlsoRemadeNoteSource>();
        services.AddSingleton<IStepExecutor, ReviseStepExecutor>();
        services.AddSingleton<IStepExecutor, GraphicDesignStepExecutor>();
        // Editor v2 (P4): renders an edit_timelines document to a delivery preset; owns no table.
        services.AddSingleton<IStepExecutor, TimelineRenderStepExecutor>();
        // Product design system (P2): skeleton today, P2-13 implements Parse/Compose/Require.
        services.AddSingleton<IStepExecutor, DesignSystemStepExecutor>();
        services.AddSingleton<IStepExecutor, ExtractFrameStepExecutor>();
        services.AddSingleton<IStepExecutor, PinInsertStepExecutor>();
        // Video enhancement: free local operations plus hosted AI upscale / frame interpolation. One factory serves both client
        // interfaces (it caches one client per resolved row); the executor reserves a ledger row through the shared budget gate.
        services.AddSingleton<ReelBolt.Shared.Inference.VideoEnhanceClientFactory>();
        services.AddSingleton<ReelBolt.Shared.Inference.IVideoUpscaleClientFactory>(sp => sp.GetRequiredService<ReelBolt.Shared.Inference.VideoEnhanceClientFactory>());
        services.AddSingleton<ReelBolt.Shared.Inference.IFrameInterpolationClientFactory>(sp => sp.GetRequiredService<ReelBolt.Shared.Inference.VideoEnhanceClientFactory>());
        services.AddSingleton<ReelBolt.Shared.Inference.IVideoOutpaintClientFactory>(sp => sp.GetRequiredService<ReelBolt.Shared.Inference.VideoEnhanceClientFactory>());
        services.AddSingleton<IStepExecutor, VideoEnhanceStepExecutor>();
        // T-108: music and sound-effect generation. The factory caches one client per resolved row (singleton like
        // the video factory); the probe reads each take's duration with ffprobe before it is metered; the executor
        // shares AddVideoGeneration's budget gate and ledger.
        services.AddSingleton<IAudioGenerationClientFactory, AudioGenerationClientFactory>();
        services.AddSingleton<Services.Inference.AudioGenerationDurationProbe>();
        services.AddSingleton<IStepExecutor, AudioGenerateStepExecutor>();

        // ImageGenerate: pictures from a prompt, or one edited by an instruction. Shares AddVideoGeneration's budget gate and
        // ledger (scope Image); the image client factory (registered above) meters each delivered picture.
        services.AddSingleton<IStepExecutor, ImageGenerateStepExecutor>();

        // Audio cleanup: free local filters plus hosted voice isolation. The factory caches one client per resolved row
        // (singleton like the audio-generation one); the executor needs no budget gate or ledger, because the engine holds
        // credits for the step and the step meters the delivered minutes through the usage recorder.
        services.AddSingleton<IAudioIsolationClientFactory, AudioIsolationClientFactory>();
        services.AddSingleton<IStepExecutor, AudioCleanupStepExecutor>();

        // --- Workflow Executor ---
        // Singleton: WorkflowExecutorService is Scoped (a new instance per consumed message), so the
        // running-execution's cancellation tokens must live somewhere every scope can reach, or a stop
        // request handled by a separate consumer instance silently cancels nothing — see
        // ExecutionCancellationRegistry's doc comment.
        services.AddSingleton<ExecutionCancellationRegistry>();
        services.AddSingleton<ExecutionRunFlags>();
        services.AddScoped<WorkflowExecutorService>();

        // Singleton + hosted service: owns the process-lifetime cancellation token every execution runs
        // under, and the semaphore that is now the real MaxConcurrency limit. Registered as BOTH so the
        // consumer can inject it directly and the host still drives StartAsync/StopAsync on the same
        // instance (AddHostedService alone would construct a second one). See WorkflowExecutionRunner's
        // summary for why executions no longer run inline inside the MassTransit consumer.
        // F11: the platform kill switches, read from platform_settings through a ten-second cache,
        // and the admission guard that applies them (plus the cross-replica per-organisation
        // concurrency count) before anything is dispatched. Both singletons: the runner is one.
        services.AddSingleton<IPlatformSwitches, PlatformSwitches>();
        services.AddSingleton<ExecutionAdmissionGuard>();
        services.AddSingleton<WorkflowExecutionRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkflowExecutionRunner>());
        services.AddScoped<IWorkflowEventPublisher, WorkflowEventPublisher>();

        // F4: execution leases (one renewal timer per process) and the recovery sweeper every replica
        // runs. The lease service is a singleton registered as BOTH itself and a hosted service so the
        // runner/executor can stamp leases on the same instance the host drives.
        services.AddSingleton<ExecutionLeaseService>();
        services.AddHostedService(sp => sp.GetRequiredService<ExecutionLeaseService>());
        services.AddHostedService<ExecutionRecoverySweeper>();
        // F10 slice 3: the engine-side half of the per-plan retention job — it retires this engine's
        // own workflow_step_cache_entries rows, which the Inference API's sweep cannot reach (the
        // table is not mapped by InferenceApiDbContext at all). Disabled by default
        // (Billing:StepCacheRetention:Enabled), so self-host is untouched.
        services.AddHostedService<StepCacheRetentionSweeper>();
        // B6a: the credit gate (Billing:Enabled=false makes it a no-op) and its refund hook. Registered
        // before the TryAdd below so the credit hook wins over the no-op.
        services.Configure<CreditGateOptions>(configuration.GetSection(CreditGateOptions.SectionName));
        // B6b/B7: the step-side reservation size, bound from the same "Billing" section (the
        // executor's WorkflowExecutorService takes it as IOptions<StepCreditOptions>). B7 replaced the
        // single placeholder figure with an estimate off the usage ledger: UsageHistoryCreditHoldEstimator
        // is SCOPED, which is one instance per execution, so a run loads its organization's sample set
        // once and reuses it for every step. StepCreditOptions now only owns the explicit override and
        // the B0 default a missing estimator falls back to.
        services.Configure<StepCreditOptions>(configuration.GetSection(StepCreditOptions.SectionName));
        services.Configure<StepCreditEstimateOptions>(configuration.GetSection(StepCreditEstimateOptions.SectionName));
        services.AddScoped<ICreditHoldEstimator, UsageHistoryCreditHoldEstimator>();
        services.AddSingleton<ICreditGate, CreditGate>();
        services.AddSingleton<IPlatformFailureHook, CreditPlatformFailureHook>();
        services.TryAddSingleton<IPlatformFailureHook, NoOpPlatformFailureHook>();
        // S-3: closes every hold a step left open once the step or its run is over (a settle whose
        // retries all failed, a successful run, a crash). Runs on every replica; the ledger keys make it
        // exactly-once. Billing:SettleSweep:*, validated at startup; a no-op while Billing:Enabled=false.
        services.AddSettleSweepOptions(configuration);
        services.AddHostedService<UnsettledHoldSweeper>();

        // --- MassTransit / RabbitMQ ---
        int maxConcurrency = configuration.GetValue("WorkflowEngine:MaxConcurrency", 4);

        services.AddMassTransit(x =>
        {
            // existing request consumer
            x.AddConsumer<WorkflowExecutionRequestedConsumer>();
            // consumer for stop requests published by Go API
            x.AddConsumer<WorkflowExecutionStopRequestedConsumer>();
            // consumer for media-derivative requests published by the Inference API (editor v2 P2):
            // it confirms the source and hands off, so its deliveries are acked in milliseconds
            x.AddConsumer<MediaDerivativesConsumer>();
            // recipe marketplace preview: same low-priority lane and queue as derivatives
            x.AddConsumer<MarketplacePreviewConsumer>();
            // R09.1: on-demand analysis of one media file (silence, shots, motion, transcript)
            x.AddConsumer<MediaAnalysisConsumer>();
            // editor-wysiwyg: browser-decodable mask preview of one segmentation track (hands off like the derivatives)
            x.AddConsumer<SegmentationMaskPreviewConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(configuration["RabbitMQ:Host"] ?? "localhost", "/", h =>
                {
                    h.Username(configuration["RabbitMQ:Username"] ?? "guest");
                    h.Password(configuration["RabbitMQ:Password"] ?? "guest");
                });

                // B8c: declare the execution queue as a PRIORITY queue, so a Pro/Team run is handed
                // out before a standard one that has been waiting longer. Without this argument the
                // AMQP priority the API stamps on the message is accepted and ignored, and the queue
                // stays plain FIFO - a silent no-op rather than an error, which is why the
                // registration and the publish site are tested together.
                //
                // DEPLOYMENT NOTE (read before changing the default). x-max-priority is a queue
                // ARGUMENT: a broker answers a re-declaration that differs from the existing queue
                // with PRECONDITION_FAILED (406), so an engine upgrading onto a broker that already
                // holds an argument-less "workflow-execution" queue fails to start its bus until that
                // queue is deleted (it must be drained/empty first - messages in it are not migrated).
                // Setting WorkflowEngine:QueuePriority:MaxPriority to 0 keeps the pre-B8c declaration
                // for such a deployment and disables the ordering; it is the only value that does.
                // Drain mode: each endpoint reports itself at Ready so a drain can stop it without stopping the bus.
                var drainTarget = context.GetRequiredService<ReelBolt.WorkflowEngine.Drain.EngineDrainTarget>();

                int configuredQueueMaxPriority = configuration.GetValue(
                    "WorkflowEngine:QueuePriority:MaxPriority", (int)RunPriority.DefaultQueueMax);
                byte queueMaxPriority = (byte)Math.Clamp(configuredQueueMaxPriority, 0, RunPriority.Max);


                cfg.ReceiveEndpoint("workflow-execution", e =>
                {
                    drainTarget.Expect(e);
                    if (queueMaxPriority > 0)
                    {
                        ((IRabbitMqReceiveEndpointConfigurator)e).EnablePriority(queueMaxPriority);
                    }

                    e.UseMessageRetry(r =>
                    {
                        r.Intervals(
                            TimeSpan.FromSeconds(5),
                            TimeSpan.FromSeconds(15),
                            TimeSpan.FromSeconds(30));
                        r.IgnoreDrainRelease();
                    });
                    e.RequeueOnDrainRelease();
                    // MaxConcurrency running + a bounded number of consumers blocked in fair-share
                    // admission (A12): a waiter holds an unacked delivery that has done no work.
                    int admissionWaiters = Math.Max(0, configuration.GetValue("WorkflowEngine:AdmissionWaiters", 16));
                    e.PrefetchCount = maxConcurrency + admissionWaiters;
                    e.ConcurrentMessageLimit = maxConcurrency + admissionWaiters;
                    e.ConfigureConsumer<WorkflowExecutionRequestedConsumer>(context);
                });

                // separate endpoint for stop requests so they don't compete with normal executions
                cfg.ReceiveEndpoint("workflow-stop-requests", e =>
                {
                    // Drain is only signalled after a new engine is healthy, so stop requests still have a consumer.
                    drainTarget.Expect(e);
                    // fanout exchange binding created automatically by MassTransit when consumer is added
                    e.ConfigureConsumer<WorkflowExecutionStopRequestedConsumer>(context);
                });

                // separate endpoint for media-derivative requests: they are hint-driven background
                // work (a proxy, a filmstrip, a waveform) and must never compete for, or hold open, a
                // workflow execution's delivery. No UseMessageRetry here on purpose — the consumer
                // never throws, so a retry policy would be dead code; a lost delivery is repaired by
                // the publisher's next request for the same source.
                cfg.ReceiveEndpoint("media-derivatives", e =>
                {
                    drainTarget.Expect(e);
                    e.ConfigureConsumer<MediaDerivativesConsumer>(context);
                    e.ConfigureConsumer<MarketplacePreviewConsumer>(context);
                });

                // Mask previews: hint-driven, one short encode at a time on the dispatcher's own worker.
                cfg.ReceiveEndpoint("segmentation-mask-preview", e =>
                {
                    e.ConfigureConsumer<SegmentationMaskPreviewConsumer>(context);
                });

                // R09.1: analysis is heavier than a derivative (a whole-file ffmpeg pass, an ASR call) and its
                // requester is waiting, so it has its own queue; one at a time per engine keeps the host calm.
                cfg.ReceiveEndpoint("media-analysis", e =>
                {
                    drainTarget.Expect(e);
                    e.PrefetchCount = 1;
                    e.ConcurrentMessageLimit = 1;
                    e.ConfigureConsumer<MediaAnalysisConsumer>(context);
                });
            });
        });

        // --- Background Workers ---
        services.AddHostedService<WorkflowWorkerPool>();

        // Drain mode (zero-downtime deploy): inert unless /tmp/reelbolt-drain appears.
        services.AddReelBoltDrain();
        services.AddSingleton<MediaAnalysisInFlight>();
        services.AddSingleton<ReelBolt.Shared.Drain.IDrainWorkSource>(sp => sp.GetRequiredService<MediaAnalysisInFlight>());
        services.AddSingleton<ReelBolt.Shared.Drain.IDrainWorkSource, ReelBolt.WorkflowEngine.Drain.RunnerDrainWork>();
        services.AddSingleton<ReelBolt.Shared.Drain.IDrainWorkSource, ReelBolt.WorkflowEngine.Drain.DerivativeDrainWork>();
        services.AddSingleton<ReelBolt.WorkflowEngine.Drain.EngineDrainTarget>();
        services.AddSingleton<ReelBolt.Shared.Drain.IDrainTarget>(sp => sp.GetRequiredService<ReelBolt.WorkflowEngine.Drain.EngineDrainTarget>());

        // helper for low-level RabbitMQ operations (message removal)
        services.AddSingleton<RabbitMqHelper>();

        // --- OpenTelemetry ---
        // WP F9: exporter and extra instrumentation only when OTEL_EXPORTER_OTLP_ENDPOINT is set.
        // The org id comes from the singleton execution accessor (an AsyncLocal), so it is also
        // visible to spans started by agents and executors deep inside a run.
        services.AddReelBoltTelemetry(
            configuration,
            ReelBoltDiagnostics.ServiceName,
            sp => sp.GetService<IWorkflowExecutionContextAccessor>()?.Current?.OrganizationId,
            InferenceTenancyDiagnostics.MeterName,
            ReelBolt.WorkflowEngine.Execution.ChangeRequestEngineDiagnostics.MeterName,
            // reelbolt.billing.unpriced_usage: a step's Platform usage that no price row prices is metered here.
            ReelBolt.Shared.Metering.UnpricedUsageDiagnostics.MeterName);

        // --- Controllers & Swagger ---
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "ReelBolt Workflow Engine", Version = "v1" });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme.",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecuritySchemeReference("Bearer", document),
                    new List<string>()
                }
            });
        });

        return services;
    }
}
