using Microsoft.Extensions.DependencyInjection.Extensions;
using ReelBolt.Shared.Storage;
using System.Text;
using Amazon.S3;
using Azure.AI.OpenAI;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Agents.FileProcessing;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Activity;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Database;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data;
using ReelBolt.Shared.DataProtection;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Observability;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.GitHub;

namespace ReelBolt.Inference.Api;

/// <summary>
/// The Inference API's entire service registration, extracted from Program.cs so the composition
/// root can be built and validated by tests (see InferenceApiCompositionRootTests) — a controller
/// whose constructor dependency is never registered here 500s on every action in production even
/// when its unit tests (which inject mocks) are green. Program.cs keeps logging and the pipeline.
/// </summary>
public static class InferenceApiServiceCollectionExtensions
{
    // Security S-1: the Npgsql meter tags every pool instrument with db.client.connection.pool.name,
    // which otherwise defaults to the connection string minus Password (SSL Password, host, user and
    // database would reach the OTLP collector). A fixed name keeps connection-string material out.
    public const string DataSourceName = "reelbolt-inference-api";

    // Npgsql pool cap when neither Database:MaxPoolSize nor the connection string sets one (budget: docs/database-connections.md).
    public const int DefaultMaxPoolSize = 16;

    /// <summary>
    /// The credit-account provisioner and the first-video grant abuse checks it depends on (disposable/MX, Tor exit list,
    /// ipinfo). Its own method so a test can resolve the production wiring.
    /// </summary>
    public static IServiceCollection AddFirstVideoGrant(this IServiceCollection services)
    {
        services.AddSingleton<ReelBolt.Inference.Api.Services.Billing.ICreditAccountProvisioner, ReelBolt.Inference.Api.Services.Billing.CreditAccountProvisioner>();
        // First-video grant abuse checks: disposable/MX, Tor exit list (background refresh), ipinfo privacy. All fail open.
        services.AddSingleton<ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.IMailDomainResolver, ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.DnsMailDomainResolver>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.TorExitList>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.ITorExitList>(sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.TorExitList>());
        services.AddHttpClient(ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.TorExitListRefresher.HttpClientName).RemoveAllLoggers();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.TorExitListRefresher>();
        // A singleton on purpose: the per-IP cache lives in the client.
        // RemoveAllLoggers: the default handler logs the request URL, which carries the sign-up IP.
        services.AddHttpClient(ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.IpInfoPrivacyClient.HttpClientName).RemoveAllLoggers();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.IIpReputationClient>(sp =>
            new ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.IpInfoPrivacyClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.IpInfoPrivacyClient.HttpClientName),
                sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.IFirstVideoEligibility, ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.FirstVideoEligibility>();
        return services;
    }

    /// <summary>
    /// The Inference API's authentication schemes: API key, session JWT and, when <c>EngineJwt:SigningKey</c>
    /// is set, the WorkflowEngine's on-behalf-of token. Separate from the composition root so it is testable.
    /// </summary>
    public static IServiceCollection AddInferenceApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // The default scheme is a policy scheme: "Bearer rbk_..." is judged by the ApiKey handler alone, every
        // other request by JwtBearer exactly as before (a failed key never falls through to JWT).
        // With EngineJwt:SigningKey set (EngineJwtSettings; a bad value throws here, so the API refuses to start)
        // a token naming the engine issuer is judged by the engine scheme alone, and the session scheme stops
        // honouring the engine marker. Without it, nothing below changes.
        EngineJwtSettings? engineJwt = EngineJwtSettings.Read(configuration);
        EngineActorMode sessionMode = engineJwt is null
            ? EngineActorMode.Legacy
            : engineJwt.AcceptLegacyOnBehalfOf
                ? EngineActorMode.SessionSchemeAcceptingLegacy
                : EngineActorMode.SessionSchemeWithEngineKey;
        Microsoft.AspNetCore.Authentication.AuthenticationBuilder authentication = services.AddAuthentication(ApiKeyAuthentication.PolicyScheme)
            .AddPolicyScheme(ApiKeyAuthentication.PolicyScheme, "JWT or API key", policy =>
                policy.ForwardDefaultSelector = ctx => ApiKeyAuthentication.IsApiKeyRequest(ctx.Request)
                    ? ApiKeyAuthentication.Scheme
                    : engineJwt is not null && EngineTokenAuthentication.IsEngineTokenRequest(ctx.Request, engineJwt.Issuer)
                        ? EngineTokenAuthentication.Scheme
                        : JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthentication.Scheme, _ => { })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
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
                // The token's org/role/platformAdmin claims are a hint; re-read the database (30 s cache).
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                        ctx.HttpContext.RequestServices.GetRequiredService<MembershipValidator>().ValidateAsync(ctx, sessionMode),
                    // A public route with no usable credential answers the documented api_key_required envelope.
                    OnChallenge = ReelBolt.Inference.Api.Services.PublicApi.PublicApiAuthResponses.WriteChallengeAsync
                };
            });
        if (engineJwt is not null)
        {
            // The WorkflowEngine's on-behalf-of tokens. The principal is marked so UseEngineTokenGuard confines it
            // to the routes the engine calls; role and membership still come from the database.
            authentication.AddJwtBearer(EngineTokenAuthentication.Scheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = engineJwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = engineJwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = engineJwt.SecurityKey,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        await ctx.HttpContext.RequestServices.GetRequiredService<MembershipValidator>()
                            .ValidateAsync(ctx, EngineActorMode.EngineScheme);
                        if (ctx.Result is null)
                            EngineTokenAuthentication.MarkEnginePrincipal(ctx.HttpContext);
                    }
                };
            });
        }
        return services;
    }

    public static IServiceCollection AddInferenceApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // --- Database ---
        // Resolved once here, so a bad Database:MaxPoolSize fails startup rather than the first query.
        string? connectionString = configuration.GetConnectionString("DefaultConnection");
        if (connectionString is not null)
            connectionString = PostgresLiveness.Apply(
                PostgresPoolSizing.ApplyMaxPoolSize(connectionString, configuration, DefaultMaxPoolSize));
        services.AddDbContext<InferenceApiDbContext>(options =>
            options.UseNpgsql(connectionString,
                npgsql => npgsql
                    .MigrationsHistoryTable("__EFMigrationsHistory_Api")
                    .ConfigureDataSource(dataSource => dataSource.Name = DataSourceName)));
        // Server-wide connection count + max_connections for the near-limit alert (Npgsql provider only),
        // on its own one-connection data source outside this pool (budget: +1 per process).
        services.AddHostedService<PostgresConnectionSampler>();

        // --- Authentication ---
        services.AddInferenceApiAuthentication(configuration);
        services.AddSingleton<EngineAdminTokenIssuer>();
        services.AddScoped<IEngineCallCredential, EngineCallCredential>();
        services.AddAuthorization();
        services.Configure<Services.ApiKeys.PublicApiOptions>(configuration.GetSection(Services.ApiKeys.PublicApiOptions.SectionName));
        services.AddScoped<Services.ApiKeys.IApiKeyService, Services.ApiKeys.ApiKeyService>();
        services.AddHostedService<Services.ApiKeys.ApiKeyExpirySweeper>();
        services.AddScoped<Services.ApiKeys.IApiKeyUsageReader, Services.ApiKeys.ApiKeyUsageReader>();
        services.AddScoped<Services.ApiKeys.IApiKeyContext, Services.ApiKeys.HttpApiKeyContext>();
        services.AddScoped<IApiKeyRateCounterStore, ApiKeyRateCounterStore>();
        services.AddScoped<ApiKeyRateLimiter>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<PublicApiGuardFilter>();
        services.AddSingleton<ApiKeyAuditWriter>();
        services.AddSingleton<IApiKeyAuditWriter>(sp => sp.GetRequiredService<ApiKeyAuditWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<ApiKeyAuditWriter>());
        // S-6: a key refused by the scope-guard middleware is audited through the same writer.
        services.AddSingleton<IScopeRefusalAuditor, ApiKeyScopeRefusalAuditor>();
        services.AddHostedService<PublicApiMaintenanceService>();
        // Public run starts (T-007): the shared start sequence, the queue's dispatcher and the end-of-request hook
        // (a no-op until the webhook ticket replaces it).
        services.AddScoped<Services.PublicApi.Runs.PublicRunStartService>();
        // Webhooks (T-009): the run-request events become deliveries, the worker sends them, the service is shared by
        // the public and dashboard routes. The secret protector has a data-protection purpose of its own.
        services.AddSingleton<Services.PublicApi.Runs.IApiRunRequestEvents, Services.Webhooks.ApiRunRequestWebhookEvents>();
        services.AddSingleton<Services.Webhooks.WebhookSecretProtector>();
        services.AddSingleton<Services.Webhooks.IWebhookHostResolver, Services.Webhooks.DnsWebhookHostResolver>();
        services.AddSingleton<Services.Webhooks.IWebhookHttpClientFactory, Services.Webhooks.WebhookHttpClientFactory>();
        services.AddScoped<Services.Webhooks.WebhookUrlValidator>();
        services.AddScoped<Services.Webhooks.WebhookEndpointService>();
        services.AddScoped<Services.Webhooks.WebhookEventWriter>();
        services.AddHostedService<Services.Webhooks.WebhookDeliveryWorker>();
        services.AddHostedService<Services.PublicApi.Runs.ApiRunQueueDispatcher>();
        services.AddMemoryCache();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Inference.VoiceLibraryCache>();
        services.AddSingleton<MembershipValidator>();

        // --- HTTP Context & Current User ---
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IProjectAccessService, ProjectAccessService>();
        services.AddScoped<IActivityFeedService, ActivityFeedService>();
        services.AddScoped<Services.Projects.ProjectTagActivityService>();
        // T-006: the two dashboard paths the public API shares verbatim (project create, file delete).
        services.AddScoped<Services.Projects.IProjectCreationService, Services.Projects.ProjectCreationService>();
        services.AddScoped<Services.Projects.IProjectFileDeletionService, Services.Projects.ProjectFileDeletionService>();
        services.AddScoped<Services.Uploads.IDirectUploadFinalizer, Services.Uploads.DirectUploadFinalizer>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Pronunciation.ProjectPronunciationService>();
        // Post text for a finished video: the context reader, the writer and the per user+project hourly cap (PostTexts:*).
        services.AddOptions<ReelBolt.Inference.Api.Services.PostTexts.PostTextOptions>()
            .BindConfiguration(ReelBolt.Inference.Api.Services.PostTexts.PostTextOptions.SectionName);
        services.AddSingleton<ReelBolt.Inference.Api.Services.PostTexts.PostTextRateLimiter>();
        services.AddScoped<ReelBolt.Inference.Api.Services.PostTexts.PostTextContextBuilder>();
        services.AddScoped<ReelBolt.Inference.Api.Services.PostTexts.PostTextVideoReader>();
        services.AddScoped<ReelBolt.Inference.Api.Services.PostTexts.WebSearchHashtagSource>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.PostTexts.IHashtagSource, ReelBolt.Inference.Api.Services.PostTexts.ModelSuggestedHashtagSource>();
        services.AddScoped<ReelBolt.Inference.Api.Services.PostTexts.IPostTextWriter, ReelBolt.Inference.Api.Services.PostTexts.PostTextWriter>();
        // Design systems (SPEC 9): summaries, version reads/writes and the approve/reject/restore
        // state machine. Scoped — it reads the request's DbContext.
        services.AddScoped<Services.DesignSystems.IDesignSystemService, Services.DesignSystems.DesignSystemService>();
        services.AddScoped<Services.DesignSystems.IDesignBuildEstimateGate, Services.DesignSystems.DesignBuildEstimateGate>();
        services.AddScoped<Services.DesignSystems.IDesignSystemIngestService, Services.DesignSystems.DesignSystemIngestService>();
        // P3-13 brand PDF (SPEC 16.4/16.5): the one named client that sends PDF bytes to the
        // import-worker. Base address and the 120 s timeout live here; the bearer from
        // Import:Worker:Token is applied per request inside the client and never logged.
        services.AddHttpClient(Services.DesignSystems.ImportWorkerClient.HttpClientName, (sp, client) =>
        {
            string? baseUrl = sp.GetRequiredService<IConfiguration>()["Import:Worker:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
                client.BaseAddress = uri;

            client.Timeout = Services.DesignSystems.ImportWorkerClient.Timeout;
        });
        services.AddScoped(sp => new Services.DesignSystems.ImportWorkerClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(Services.DesignSystems.ImportWorkerClient.HttpClientName),
            sp.GetRequiredService<IConfiguration>()));
        // Project PDFs: text read through the same worker client (never parsed in this process), cached per PDF version.
        services.AddSingleton<Services.Indexing.ProjectPdfTextCache>();
        services.AddScoped<Services.Indexing.IProjectFilePdfText, Services.Indexing.ProjectFilePdfText>();
        // Zip unpack: the worker client (same import-worker HttpClient), the queue signal, the processor and its sweeping worker.
        Services.Archives.ArchiveUnpackClientRegistration.AddArchiveUnpackClient(services);
        services.AddScoped<Services.Archives.IArchiveUnpackGateway, Services.Archives.ArchiveUnpackGateway>();
        services.AddSingleton<Services.Archives.ArchiveUnpackSignal>();
        services.AddScoped<Services.Archives.IArchiveUnpackScheduler, Services.Archives.ArchiveUnpackScheduler>();
        services.AddScoped<Services.Archives.ArchiveUnpackProcessor>();
        services.AddHostedService<Services.Archives.ArchiveUnpackWorker>();
        // Phase 3 drift (SPEC 13.2, stages 0/1). Scoped — the queue's worker opens a scope per item.
        services.AddScoped<Services.DesignSystems.IDesignSystemDriftService, Services.DesignSystems.DesignSystemDriftService>();
        // Sync paths only enqueue (DriftHooks.Notify); one worker drains the bounded queue in its own
        // scope under a system tenant, and the sweeper re-queues covered projects and expires stale
        // proposals on a timer. Per process, like the cloud and GitHub queues.
        services.AddSingleton(_ => new Services.DesignSystems.DesignDriftQueue());
        services.AddHostedService<Services.DesignSystems.DesignSystemDriftWorker>();
        services.AddHostedService(sp => new Services.DesignSystems.DesignSystemDriftSweeper(
            sp.GetRequiredService<Services.DesignSystems.DesignDriftQueue>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<Services.DesignSystems.DesignSystemDriftSweeper>>()));
        services.AddSingleton<ITenantContext, TenantContext>();

        // --- OpenTelemetry (WP F9) --- exporter + extra instrumentation only when
        // OTEL_EXPORTER_OTLP_ENDPOINT is set. The tenant comes from ITenantContext, which reads the
        // validated token, so an ASP.NET span is tagged when it ends (after authentication).
        services.AddReelBoltTelemetry(
            configuration,
            "ReelBolt.Inference.Api",
            sp => sp.GetService<ITenantContext>()?.OrganizationId,
            InferenceTenancyDiagnostics.MeterName,
            ReelBolt.Inference.Api.Services.ChangeRequests.ChangeRequestDiagnostics.MeterName,
            // Output verdict latency: produced-at to first stored verdict.
            OutputModerationDiagnostics.MeterName,
            // workflow-apps P2-3: the lint gate's would-block counter, so the false-positive review has data.
            ReelBolt.Inference.Api.Services.Workflows.WorkflowLintDiagnostics.MeterName,
            // F11: the spend report's alert counter must be exported, or an alert nobody can see is
            // not an alert.
            ReelBolt.Inference.Api.Services.Background.SpendReportDiagnostics.MeterName,
            ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse.FirstVideoAbuseDiagnostics.MeterName,
            // saas-promotions T-016: confirmation mails refused by a cap, so a flood is visible.
            ReelBolt.Inference.Api.Services.Marketing.MarketingDiagnostics.MeterName,
            // saas-promotions T-034: reserved payments refused at the overshoot bound.
            ReelBolt.Inference.Api.Services.Billing.BillingEventProcessor.MeterName,
            // db-connection-hygiene: server-wide connections vs max_connections, for the near-limit alert.
            PostgresConnectionSampler.MeterName,
            // Lost-webhook recovery: webhook outcomes/failures, reconcile runs and the stuck-watch backlog.
            ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingWebhookDiagnostics.MeterName,
            // saas-model-catalog T-100: boot-time catalog upgrade steps that failed and were rolled back.
            ReelBolt.Inference.Api.Data.ModelCatalogUpgraderDiagnostics.MeterName,
            // alpha-launch T-018: server and client errors, so a failure reaches the watched destination.
            ReelBolt.Inference.Api.Observability.ErrorTrackingDiagnostics.MeterName,
            // alpha-launch T-018: the four alpha funnel events the browser reports.
            ReelBolt.Inference.Api.Services.Telemetry.AlphaFunnelDiagnostics.MeterName,
            // security-headers T-003: CSP/COEP violation reports by outcome.
            ReelBolt.Inference.Api.Services.Telemetry.CspReportDiagnostics.MeterName);

        // alpha-launch T-018: the browser telemetry ingest endpoints. Off by default, so a deployment
        // that has not opted in answers 404 exactly as it did before.
        services.Configure<ReelBolt.Inference.Api.Services.Telemetry.AlphaTelemetryOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Telemetry.AlphaTelemetryOptions.SectionName));
        // The durable "once per user, per event" marker, so one caller cannot drive the funnel
        // counter without bound.
        services.AddScoped<
            ReelBolt.Inference.Api.Services.Telemetry.IAlphaFunnelClaims,
            ReelBolt.Inference.Api.Services.Telemetry.AlphaFunnelClaimStore>();
        // product-events-funnel T-002: the one writer for product_events — a plain append for a consent
        // decision, and the atomic claim-and-append the browser funnel ingest uses.
        services.AddScoped<
            ReelBolt.Inference.Api.Services.Telemetry.IProductEventLog,
            ReelBolt.Inference.Api.Services.Telemetry.ProductEventLog>();
        // product-analytics: best-effort server emitters and the visitor-row retention sweep.
        services.AddScoped<
            ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter,
            ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Telemetry.ProductEventsRetentionService>();
        // security-headers T-003: anonymous CSP/COEP violation counts (daily aggregate, 90-day sweep).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ReelBolt.Inference.Api.Services.Telemetry.CspReportRateLimiter>();
        services.AddScoped<
            ReelBolt.Inference.Api.Services.Telemetry.ICspReportStore,
            ReelBolt.Inference.Api.Services.Telemetry.CspReportStore>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Telemetry.CspReportRetentionService>();

        // --- MinIO / S3 ---
        services.AddReelBoltS3(configuration);
        // One instance, forwarded under both seams: the byte store and the read-only listing the
        // retention sweep's orphan pass resolves (optionally - it skips itself when absent).
        services.AddSingleton<S3FileStorageService>();
        // Project versioning: every API consumer of IFileStorageService gets the copy-on-change decorator
        // (it protects old bytes before an overwrite/delete/move of a live userFiles/agentFiles key).
        // IObjectListing stays on the raw store: listing never mutates.
        services.AddSingleton<IFileStorageService>(sp => new ProjectHistoryProtectingStorage(
            sp.GetRequiredService<S3FileStorageService>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<ProjectHistoryProtectingStorage>>(),
            sp.GetService<TimeProvider>()));
        services.AddSingleton<IObjectListing>(sp => sp.GetRequiredService<S3FileStorageService>());
        // Library-owned brand assets: the same bucket/client, under the
        // design-libraries/{org}/{library}/assets/{asset} prefix.
        services.AddSingleton<ILibraryAssetStorage, S3LibraryAssetStorage>();
        // Built-in sound-effects library: renderer (ffmpeg), platform-prefix store and pack downloader.
        services.AddSingleton<ReelBolt.Inference.Api.Services.SfxLibrary.ISfxRenderer, ReelBolt.Inference.Api.Services.SfxLibrary.FfmpegSfxRenderer>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.SfxLibrary.ISfxLibraryStorage, ReelBolt.Inference.Api.Services.SfxLibrary.S3SfxLibraryStorage>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.SfxLibrary.ISfxLibraryObjectCopier, ReelBolt.Inference.Api.Services.SfxLibrary.S3SfxLibraryObjectCopier>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.SfxLibrary.SfxLibraryAddRateLimiter>();
        services.AddScoped<ReelBolt.Inference.Api.Services.SfxLibrary.SfxLibraryProjectAdder>();
        services.AddHttpClient(ReelBolt.Inference.Api.Services.SfxLibrary.HttpSfxPackFetcher.HttpClientName);
        services.AddSingleton<ReelBolt.Inference.Api.Services.SfxLibrary.ISfxPackFetcher, ReelBolt.Inference.Api.Services.SfxLibrary.HttpSfxPackFetcher>();
        // One instance per process = one replica: its semaphore is the per-replica fast path and its
        // guard takes the cross-replica advisory lock.
        services.AddSingleton<ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock>();

        // --- Direct uploads (docs/design/local-folder-sync-and-storage.md Part 3): single PUT or S3
        // multipart, shared by the browser routes and the desktop app's folder sync. The multipart store
        // uses the same two clients AddReelBoltS3 registers (in-cluster + public-endpoint presigner).
        services.AddSingleton<ReelBolt.Inference.Api.Services.Uploads.IMultipartObjectStore>(sp =>
            new ReelBolt.Inference.Api.Services.Uploads.S3MultipartObjectStore(
                sp.GetRequiredService<IAmazonS3>(),
                sp.GetRequiredKeyedService<IAmazonS3>(S3ClientFactory.PresignServiceKey),
                configuration["MinIO:BucketName"] ?? "reelforge",
                S3ClientFactory.ResolvePublicEndpoint(configuration)));
        services.AddScoped<ReelBolt.Inference.Api.Services.Uploads.IDirectUploadService, ReelBolt.Inference.Api.Services.Uploads.DirectUploadService>();
        // The one post-add path (index/summarize text, derivatives for media, activity) and the
        // SSRF-guarded import from a public https URL (FileImport:MaxBytes / FileImport:TimeoutSeconds).
        services.AddScoped<ReelBolt.Inference.Api.Services.Uploads.IProjectFileAddedEffects, ReelBolt.Inference.Api.Services.Uploads.ProjectFileAddedEffects>();
        // Analysis at upload: the project's choice plus the operator's switch, read in one place for every upload path.
        services.Configure<ReelBolt.Inference.Api.Services.Uploads.UploadAnalysisOptions>(configuration.GetSection(ReelBolt.Inference.Api.Services.Uploads.UploadAnalysisOptions.SectionName));
        services.AddScoped<ReelBolt.Inference.Api.Services.Uploads.IUploadAnalysisSetting, ReelBolt.Inference.Api.Services.Uploads.UploadAnalysisSetting>();
        services.Configure<ReelBolt.Inference.Api.Services.Uploads.UrlImportOptions>(configuration.GetSection(ReelBolt.Inference.Api.Services.Uploads.UrlImportOptions.SectionName));
        services.AddScoped<ReelBolt.Inference.Api.Services.Uploads.IUrlImportService, ReelBolt.Inference.Api.Services.Uploads.UrlImportService>();

        // --- Recipe marketplace: off unless Marketplace:Enabled (docs/design/recipe-marketplace.md) ---
        services.Configure<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Marketplace.MarketplaceOptions.SectionName));
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.IMarketplaceTextCheck, ReelBolt.Inference.Api.Services.Marketplace.MarketplaceTextCheck>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketplace.IMarketplaceCapLock, ReelBolt.Inference.Api.Services.Marketplace.MarketplaceCapLock>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.IMarketplacePreviewDispatcher, ReelBolt.Inference.Api.Services.Marketplace.MassTransitMarketplacePreviewDispatcher>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketplace.IMarketplacePreviewObjects, ReelBolt.Inference.Api.Services.Marketplace.S3MarketplacePreviewObjects>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.IMarketplacePreviewCsamGate, ReelBolt.Inference.Api.Services.Marketplace.MarketplacePreviewCsamGate>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.IMarketplacePreviewModerator, ReelBolt.Inference.Api.Services.Marketplace.MarketplacePreviewModerator>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplacePreviewFinalizer>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketplace.MarketplacePreviewSweeper>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplacePublishService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.IRecipeSearch, ReelBolt.Inference.Api.Services.Marketplace.NpgsqlRecipeSearch>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplacePublicQuery>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceCopyService>();
        // Community recipes as assistant and MCP tools (search, get, Propose/Apply copy); flag off answers "not available".
        services.AddScoped<AssistantMarketplaceTools>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceRatingService>();
        services.Configure<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceRunCountOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Marketplace.MarketplaceRunCountOptions.SectionName));
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceRunCounts>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceRunCountSweeper>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceRetentionSweeper>();

        // --- Recipe marketplace moderation: reports, admin API, safety delisting, author notice ---
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketplace.IMarketplaceObjectStore, ReelBolt.Inference.Api.Services.Marketplace.S3MarketplaceObjectStore>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.IMarketplaceAuthorNotifier, ReelBolt.Inference.Api.Services.Marketplace.MarketplaceAuthorNotifier>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceReportService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.IReviewQueueQuery, ReelBolt.Inference.Api.Services.Marketplace.NpgsqlReviewQueueQuery>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceAdminService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceSafetyService>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceSafetySweeper>();

        // --- CSAM hash matching (Project Arachnid Shield) ---
        // Fail open by default and off until an account is configured: Csam:Arachnid:Enabled plus a
        // username/password and base URL. The named client is the ONLY outbound host this control can
        // reach; no general moderation endpoint is registered on this path. The sweeper covers rows
        // written by cloud/GitHub sync, the assistant and the WorkflowEngine, which never run the
        // post-add path.
        services.Configure<ReelBolt.Inference.Api.Services.Csam.CsamOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Csam.CsamOptions.SectionName));
        services.AddHttpClient<ReelBolt.Inference.Api.Services.Csam.IArachnidShieldClient, ReelBolt.Inference.Api.Services.Csam.ArachnidShieldClient>(
            ReelBolt.Inference.Api.Services.Csam.ArachnidShieldClient.HttpClientName,
            (sp, client) =>
            {
                ReelBolt.Inference.Api.Services.Csam.CsamOptions options =
                    sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.Csam.CsamOptions>>().Value;
                string? baseUrl = options.Arachnid.BaseUrl;
                if (!string.IsNullOrWhiteSpace(baseUrl)
                    && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
                {
                    // Relative requests resolve here and nowhere else.
                    client.BaseAddress = new Uri(uri.ToString().TrimEnd('/') + "/");
                }

                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Arachnid.TimeoutSeconds));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A 307/308 must not re-send the raw media body to a host other than the configured
                // Arachnid base URL. Every redirect is a failed scan (fail open), never a second host.
                AllowAutoRedirect = false
            });
        services.AddSingleton<ReelBolt.Inference.Api.Services.Csam.IPdqHasher, ReelBolt.Inference.Api.Services.Csam.PdqHasher>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Csam.ICsamQuarantineService, ReelBolt.Inference.Api.Services.Csam.CsamQuarantineService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Csam.ICsmaHashMatcher, ReelBolt.Inference.Api.Services.Csam.CsamHashMatcher>();
        // The output-moderation lane asks for a held project's pending scans now (one request queue, served by the
        // sweeper's own task), and the sweeper wakes the lane when a scan is done (ISSUES-ig-reel-2026-11-03 #6).
        services.AddSingleton<ReelBolt.Inference.Api.Services.Csam.CsamScanRequests>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Csam.ICsamScanRequests>(
            sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Csam.CsamScanRequests>());
        services.AddSingleton<ReelBolt.Inference.Api.Services.Moderation.IOutputModerationWake,
            ReelBolt.Inference.Api.Services.Moderation.OutputModerationWake>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Csam.CsamScanSweeper>();
        // Never silent: credentials present while the master switch is off is logged once at boot.
        services.AddHostedService<ReelBolt.Inference.Api.Services.Csam.CsamConfigurationWarningService>();

        // --- Vector Search options ---
        services.AddOptions<VectorSearchOptions>()
            .Bind(configuration.GetSection(VectorSearchOptions.SectionName))
            .Validate(o => QdrantClientFactory.Validate(o) is null, "VectorSearch:QdrantApiKey requires an https VectorSearch:QdrantUrl.")
            .ValidateOnStart();

        // --- Inference provider abstraction (chat clients for agents, e.g. file summarization) ---
        // File-system ring by default; DataProtection:Store=Database moves it into data_protection_keys
        // (see DataProtectionRegistration; the application name there must never change, D20).
        services.AddReelBoltDataProtection<InferenceApiDbContext>(configuration, readOnlyRing: false);

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        // A LocalRunner provider reaches the user's own model through the runner gateway's relay: the same
        // Compute:GatewayBaseUrl / Compute:InternalToken (or RUNNER_INTERNAL_TOKEN) the compute fabric uses.
        services.Configure<LocalRunnerGatewayOptions>(configuration.GetSection(LocalRunnerGatewayOptions.SectionName));
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<LocalRunnerGatewayOptions>, LocalRunnerGatewayOptionsValidator>();
        services.AddOptions<LocalRunnerGatewayOptions>().ValidateOnStart();
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<ITranscriptionClientFactory, TranscriptionClientFactory>();
        services.AddSingleton<IDecisionClientFactory, DecisionClientFactory>();
        services.AddSingleton<ISpeechSynthesisClientFactory, SpeechSynthesisClientFactory>();
        // Same registration as the WorkflowEngine's AddVideoGeneration: InferenceProvidersController
        // needs it (connection tests for video-generation providers); it has no dependencies and
        // caches one client per resolved provider configuration, so it must be a singleton.
        services.AddSingleton<IVideoGenerationClientFactory, VideoGenerationClientFactory>();
        // Same reasoning and the same lifetime as the video factory above: InferenceProvidersController
        // needs it for connection tests on ImageGeneration/ImageEdit rows. It caches one client per
        // resolved provider configuration and takes an optional IUsageRecorder (a singleton), so every
        // image it returns is metered at the choke point rather than at each call site.
        services.AddSingleton<IImageClientFactory, ImageClientFactory>();
        // And the analysis arm (Segmentation / Depth), same lifetime and same reasoning: the
        // controller needs it for connection tests on a FalAi row, and the factory caches one client
        // per resolved provider configuration.
        services.AddSingleton<IImageAnalysisClientFactory, ImageAnalysisClientFactory>();
        services.AddScoped<IInferenceProviderStore, InferenceApiProviderStore>();
        // The resolver is a singleton that keeps one snapshot per organization; this accessor tells
        // it which one the current request / background scope is.
        services.AddSingleton<IInferenceTenantAccessor, ApiInferenceTenantAccessor>();
        services.AddReelBoltEntitlements(configuration);
        services.AddReelBoltUsageRecorder<InferenceApiDbContext>();
        services.AddReelBoltMeteringOptions(configuration);

        // Content moderation (content-moderation T-002): the shared policy/client with an API-owned audit
        // table. The factory is stateless per call; the gate is scoped because it writes through the
        // request's DbContext. The retention sweeper owns its own scope per tick.
        services.Configure<ContentModerationOptions>(
            configuration.GetSection(ContentModerationOptions.SectionName));
        services.AddSingleton<IContentModerationClientFactory, ContentModerationClientFactory>();
        services.AddScoped<IContentModerationGate,
            ReelBolt.Inference.Api.Services.Moderation.ContentModerationService>();
        // The publish/egress gate: the one fail-closed surface.
        services.AddScoped<ReelBolt.Inference.Api.Services.Moderation.PublishModeration>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Moderation.IStoredCopyGate>(sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Moderation.PublishModeration>());
        services.AddHostedService<ReelBolt.Inference.Api.Services.Moderation.ContentModerationRetentionService>();

        // Ingest moderation (content-moderation T-003): the shared sampler drives ffmpeg as a child
        // process, the gate is scoped with the request/worker scope, and the queue is a fast path over
        // the row status the worker sweeps.
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<IModerationMediaSampler>(sp => new FfmpegModerationMediaSampler(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IOptions<ContentModerationOptions>>().Value));
        services.AddSingleton<IContentModerationEnqueuer, ContentModerationEnqueuer>();
        services.AddSingleton<IBackgroundTaskQueue<ContentModerationTask>>(
            _ => new ChannelBackgroundTaskQueue<ContentModerationTask>(100));
        services.AddScoped<ContentModerationProcessor>();
        services.AddHostedService<ContentModerationWorker>();
        // Output verdicts: judged once, in the background, by ONE consumer (the step-completed consumer
        // and the views offer; the sweep backfills). Publish reads the stored verdict.
        services.AddSingleton<IBackgroundTaskQueue<OutputModerationTask>>(
            _ => new ChannelBackgroundTaskQueue<OutputModerationTask>(100));
        services.AddSingleton<IOutputModerationEnqueuer, OutputModerationEnqueuer>();
        services.AddScoped<OutputModerationProcessor>();
        services.AddHostedService<OutputModerationWorker>();
        services.AddReelBoltPlanEntitlements<InferenceApiDbContext>(configuration);
        // One definition of storage used, shared by the quota, the billing summary and /storage/usage.
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.IStorageUsageService, ReelBolt.Inference.Api.Services.Billing.StorageUsageService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.IPlanGateService, ReelBolt.Inference.Api.Services.Billing.PlanGateService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.ICreditBalanceReader, ReelBolt.Inference.Api.Services.Billing.CreditBalanceReader>();
        services.AddSingleton<IInferenceProviderResolver, InferenceProviderResolver>();

        // Rerank (platform-docs stage B): an inference-provider capability served by OpenAICompatible rows only.
        // Singleton: the platform-docs index (a singleton) takes it as its optional stage-B seam; every dependency is a singleton.
        services.AddSingleton<IRerankClientFactory, RerankClientFactory>();
        // Web search (agent-web-search): the metered client factory; the engine registers the same one.
        services.AddSingleton<IWebSearchClientFactory, WebSearchClientFactory>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.PlatformDocs.IPlatformDocsRerankStage,
            ReelBolt.Inference.Api.Services.PlatformDocs.PlatformDocsRerankStage>();
        services.AddSingleton<IAgentChatClientProvider, AgentChatClientProvider>();

        // --- Embeddings (Qdrant): an inference-provider capability (Embedding). ProviderEmbeddingGenerator
        // resolves lazily per call — Embedding default row, then the legacy AzureOpenAI:* config — so a
        // missing config only fails vector search, never the file endpoints that construct it. ---
        services.AddSingleton<IEmbeddingClientFactory, EmbeddingClientFactory>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, ProviderEmbeddingGenerator>();

        services.AddSingleton<IFileChunker, SimpleTokenChunker>();
        services.AddSingleton<IVectorIndexService, QdrantVectorIndexService>();
        services.AddScoped<VectorSearchQueryService>();
        services.AddScoped<WorkflowTemplateProvisioningService>();
        services.AddScoped<IProjectReadinessFacts, ProjectReadinessFacts>();
        services.AddScoped<WorkflowExecutionService>();
        services.AddScoped<IWorkflowExecutionService>(sp => sp.GetRequiredService<WorkflowExecutionService>());
        // The per-run design-system pin (SPEC 9.1): the WorkflowExecutionService builds one on every queue.
        services.AddScoped<IDesignSystemPinService, DesignSystemPinService>();
        // Project versioning (initiative project-versioning, contract section 4): the capture service and
        // its options. The factory passes an optional TimeProvider so a test can pin the clock.
        services.Configure<ReelBolt.Inference.Api.Services.History.ProjectHistoryOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.History.ProjectHistoryOptions.SectionName));
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectHistoryService>(sp =>
            new ReelBolt.Inference.Api.Services.History.ProjectHistoryService(
                sp.GetRequiredService<InferenceApiDbContext>(),
                sp.GetRequiredService<IFileStorageService>(),
                sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.History.ProjectHistoryOptions>>(),
                sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.History.ProjectHistoryService>>(),
                sp.GetService<TimeProvider>()));
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectRestoreLock>(sp =>
            sp.GetRequiredService<ReelBolt.Inference.Api.Data.InferenceApiDbContext>().Database.IsNpgsql()
                && connectionString is { Length: > 0 }
                ? new ReelBolt.Inference.Api.Services.History.PostgresProjectRestoreLock(connectionString)
                : ReelBolt.Inference.Api.Services.History.InProcessProjectRestoreLock.Shared);
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectRestoreKindHandler, ReelBolt.Inference.Api.Services.History.FileRestoreKindHandler>();
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectRestoreKindHandler, ReelBolt.Inference.Api.Services.History.ProjectRestoreKindHandler>();
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectRestoreKindHandler, ReelBolt.Inference.Api.Services.History.PronunciationRestoreKindHandler>();
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectRestoreKindHandler, ReelBolt.Inference.Api.Services.History.DesignSystemRestoreKindHandler>();
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectRestoreKindHandler, ReelBolt.Inference.Api.Services.History.WorkflowRestoreKindHandler>();
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectRestoreKindHandler, ReelBolt.Inference.Api.Services.History.TimelineRestoreKindHandler>();
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectHistoryRestoreService, ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser, ReelBolt.Inference.Api.Services.History.ProjectHistoryEraser>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.History.ProjectFileHashSweeper>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.History.ProjectHistoryRetentionService>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.History.ProjectHistoryIdleCaptureService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.History.ProjectHistoryAmbientFilter>();
        // Composition revisions (docs/design/composition-revisions.md): the request side — the hidden
        // per-project revision workflow, availability, the request row and the version history.
        services.AddScoped<ReelBolt.Inference.Api.Services.Compositions.ICompositionRevisionService,
            ReelBolt.Inference.Api.Services.Compositions.CompositionRevisionService>();
        // Change requests on any step of a finished run (docs/design/change-requests.md): the request side.
        services.AddScoped<ReelBolt.Inference.Api.Services.ChangeRequests.IStepChangeRequestService,
            ReelBolt.Inference.Api.Services.ChangeRequests.StepChangeRequestService>();
        // Designed graphics for the picture editor (docs/editor.md "Designed graphics"): requests,
        // estimates and settling the engine's PNG into a layer.
        services.AddScoped<ReelBolt.Inference.Api.Services.Graphics.IGraphicDesignService,
            ReelBolt.Inference.Api.Services.Graphics.GraphicDesignService>();
        // AI-assist proposals: the per user+project hourly cap (EditorProposals:MaxPerHourPerUserProject).
        services.AddOptions<ReelBolt.Inference.Api.Services.Editing.EditProposalOptions>()
            .BindConfiguration(ReelBolt.Inference.Api.Services.Editing.EditProposalOptions.SectionName);
        services.AddSingleton<ReelBolt.Inference.Api.Services.Editing.EditProposalRateLimiter>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Editing.EditProposalJobStore>();
        // R11.2: the grounded part of a proposal's prompt (Editing:Proposals:MaxTranscriptChars).
        services.AddOptions<ReelBolt.Inference.Api.Services.Editing.EditProposalGroundingOptions>()
            .BindConfiguration(ReelBolt.Inference.Api.Services.Editing.EditProposalGroundingOptions.SectionName);
        // R12.2 hand-off package: size/file caps and the per-user concurrent stream cap (Editing:HandOff:*).
        services.AddOptions<ReelBolt.Inference.Api.Services.Editing.HandOffOptions>()
            .BindConfiguration(ReelBolt.Inference.Api.Services.Editing.HandOffOptions.SectionName);
        services.AddSingleton<ReelBolt.Inference.Api.Services.Editing.HandOffLimiter>();
        services.AddScoped<IWorkflowEditDiffService, WorkflowEditDiffService>();
        // A10: the save-time check that every step's inference-provider reference names a row this
        // project's organization can actually use. Scoped — it reads the request's DbContext.
        services.AddScoped<StepProviderOwnershipValidator>();
        // The pre-run linter (docs/workflow-linter.md): facts from the database; WL018 wraps AppManifestValidator.FindOrphans.
        services.AddScoped<IWorkflowLintFactsSource, DbWorkflowLintFactsSource>();
        services.AddScoped<IAppManifestOrphanSource, AppManifestOrphanSource>();
        services.AddScoped<IWorkflowLinter, WorkflowLinter>();
        services.AddScoped<WorkflowLintGate>();
        // App inputs (docs/design/app-inputs.md 6.2, 11.2): the run-start check and WL033's pricer. Lane L4: the model source re-reads the organization's selectable models at
        // every check (docs/design/app-inputs.md 8.3), and the options endpoint lists them for one model input.
        services.AddScoped<IAppInputModelResolverSource>(sp => new AppInputModelResolverSource(
            sp.GetRequiredService<ReelBolt.Inference.Api.Data.InferenceApiDbContext>(),
            sp.GetService<ReelBolt.Shared.Inference.IInferenceProviderResolver>(),
            sp.GetService<ReelBolt.Shared.Tenancy.IOrganizationEntitlements>()));
        services.AddScoped<AppInputModelOptionsService>();
        services.AddScoped(sp => new AppInputValueValidator(
            sp.GetRequiredService<ReelBolt.Inference.Api.Data.InferenceApiDbContext>(),
            sp.GetService<IAppInputModelResolverSource>(),
            sp.GetService<ReelBolt.Shared.Tenancy.IOrganizationEntitlements>(),
            null,
            sp.GetService<IAppInputSpendPricer>()));
        services.AddScoped<IAppInputSpendPricer, AppInputSpendPricer>();
        services.AddScoped(sp => new VideoSourceGate(
            sp.GetRequiredService<ReelBolt.Inference.Api.Data.InferenceApiDbContext>(),
            ReelBolt.Shared.Workflows.VideoSourceLimits.From(sp.GetRequiredService<IConfiguration>()),
            sp.GetRequiredService<IConfiguration>().GetValue<int?>("VideoSources:MaxTooDemandingFailuresPerDay")
                ?? VideoSourceGate.DefaultMaxTooDemandingPerDay,
            sp.GetService<ILogger<VideoSourceGate>>()));
        // WorkflowStepSchemasController's only dependency. Immutable after construction (it builds
        // its config-type cache once), so one instance is shared, as AssistantToolProvider already does.
        services.AddSingleton<StepConfigSchemaExporter>();
        services.AddScoped<IAssistantExecutionGuard, AssistantExecutionGuard>();
        // Best-effort user-activity notifier (docs/notifications.md): reports file additions and
        // thumbnail-graphic edits to the Go API, which mails the opted-in workspace members.
        // Scoped: it reads the request's DbContext and IPublishEndpoint.
        services.AddScoped<IUserActivityNotifier, UserActivityNotifier>();
        // Avatars (docs/design/avatars.md): the operator kill switch Avatars:Enabled (default false: every avatar
        // route answers 404 while both Avatars:Enabled and Avatars:Objects:Enabled are off), the fail-closed reference checks, and the one delete routine (route and retention sweep).
        // Phase 1 has no vendor uploads, so vendor-side deletes answer not_supported.
        // Read tolerantly: a mistyped switch is off (and logged once at startup), never an exception on a request.
        services.AddOptions<ReelBolt.Inference.Api.Services.Avatars.AvatarOptions>()
            .Configure<IConfiguration>(ReelBolt.Inference.Api.Services.Avatars.AvatarOptions.Apply);
        services.AddHostedService<ReelBolt.Inference.Api.Services.Avatars.AvatarSwitchInstaller>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Avatars.IAvatarVendorAssetDeleter,
            ReelBolt.Inference.Api.Services.Avatars.NotSupportedAvatarVendorAssetDeleter>();
        // Objects (docs/design/avatar-objects.md, AO-1): each picture of a new object avatar is shown to the default
        // Vision provider, which must answer that it shows no person; with no Vision provider an object is refused (VISION_PROVIDER_REQUIRED).
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.IObjectReferencePersonCheck,
            ReelBolt.Inference.Api.Services.Avatars.ObjectReferencePersonCheck>();
        // Objects also get their identity facts (colour, make and model, marks, scale) read once by the same provider (fail open).
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.IAvatarIdentityDescriber,
            ReelBolt.Inference.Api.Services.Avatars.AvatarIdentityDescriber>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.AvatarReferenceChecks>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.ProjectAvatarDeletion>();
        // Avatars and objects in the picture editor (docs/design/avatar-objects.md): the API's reader of usable
        // subjects (the one rule, the digest check, the pictures), the copy of a picture that is sent, and THE ONE rule
        // for who may allow sending media for one call, asked by every surface (the picture editor, subject clips,
        // variants, object extraction, and the assistant and MCP tools of each): the app session on the surface's own
        // route, an API key with media.egress, MCP under the user's own switch, the chat under an always-asked card.
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.IProjectSubjectReader,
            ReelBolt.Inference.Api.Services.Avatars.ProjectSubjectReader>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Avatars.ISubjectPictureScaler,
            ReelBolt.Inference.Api.Services.Avatars.FfmpegSubjectPictureScaler>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.IMediaEgressCallPermission,
            ReelBolt.Inference.Api.Services.Avatars.MediaEgressCallPermission>();
        // Clips with avatars and objects from the editor (docs/design/avatar-objects.md "Generating a clip with avatars from
        // the editor"): the one service behind the subject-clips routes and, later, the assistant and MCP tool.
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.ISubjectClipService,
            ReelBolt.Inference.Api.Services.Avatars.SubjectClipService>();
        // Workspace avatar library (docs/design/avatar-objects.md): the organization-prefix object store and the one
        // service every caller (REST, the import route, the retention sweep, the storage sweep) goes through.
        services.AddSingleton<ReelBolt.Inference.Api.Services.Avatars.Library.IAvatarLibraryStore,
            ReelBolt.Inference.Api.Services.Avatars.Library.S3AvatarLibraryStore>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.Library.AvatarLibraryService>();
        // Avatar builds (docs/design/avatar-objects.md "Avatar variants", "Pulling an object out of a video"): the one
        // create path of an object avatar, the build service behind the routes and the assistant and MCP tools, and its
        // worker and sweeper. Whether one call may send media is the one rule registered above.
        services.AddOptions<ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildOptions>()
            .BindConfiguration(ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildOptions.SectionName);
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.ObjectAvatarCreator>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildQueue>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarVariantPictures>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildFiles>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildRuns>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildProcessor>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Avatars.Builds.IAvatarBuildService,
            ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildService>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildWorker>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Avatars.Builds.AvatarBuildSweeper>();
        // Guest review links (Wave 4, docs/design/guest-review-links.md): limits, the token-to-link resolver, the
        // pinned-render finder, the owner-notification coalescer, and the per-replica sliding write window.
        services.AddOptions<ReelBolt.Inference.Api.Services.Editing.ReviewLinks.ReviewLinkOptions>()
            .BindConfiguration(ReelBolt.Inference.Api.Services.Editing.ReviewLinks.ReviewLinkOptions.SectionName);
        services.AddScoped<ReelBolt.Inference.Api.Services.Editing.ReviewLinks.ReviewRenderResolver>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Editing.ReviewLinks.ReviewLinkResolver>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Editing.ReviewLinks.ReviewLinkActivityNotifier>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Editing.ReviewLinks.ReviewLinkWriteLimiter>();
        // One key per deployment, resolved lazily and shared by every mutating assistant tool, so
        // this must be a singleton: a per-scope instance would derive its own key and reject tokens
        // its sibling had just minted.
        services.AddSingleton<IConfirmationTokenService, ConfirmationTokenService>();
        // The assistant's per-area tool surfaces. Plain scoped services: each depends only on this
        // request's DbContext, caller and collaborators, and AssistantToolProvider composes their
        // GetTools(context) lists — so a new surface is a registration plus one append there.
        services.AddScoped<AssistantProjectTools>();
        services.AddScoped<AssistantFileTools>();
        services.AddScoped<AssistantUploadTools>();
        services.AddScoped<AssistantWorkflowTools>();
        // Patch and clone tools. The provider lists their tools.
        services.AddScoped<AssistantWorkflowPatchTools>();
        services.AddScoped<AssistantRevisionTools>();
        services.AddScoped<AssistantChangeRequestTools>();
        // Green-screen insert (Propose/Apply). The provider lists its tools.
        services.AddScoped<AssistantGreenScreenTools>();
        // Audio cleanup: the service the REST route and the assistant tools share.
        services.AddScoped<ReelBolt.Inference.Api.Services.Media.AudioCleanupService>();
        // Pictures and videos over the film (Propose/Apply). The provider lists its tools.
        services.AddScoped<AssistantMediaOverlayTools>();
        services.AddScoped<AssistantAudioCleanupTools>();
        // Video enhancement (Propose/Apply) through the same IVideoEnhanceService as the editor routes.
        services.AddScoped<AssistantVideoEnhanceTools>();
        // A project's narration voices: list, browse the provider's public library, add a library voice (Propose/Apply).
        services.AddScoped<ReelBolt.Inference.Api.Services.Voices.ProjectVoiceLibraryService>();
        services.AddScoped<AssistantVoiceTools>();
        // A project's avatars: one read-only list (consent is only given in the app's Avatars tab); kill switch Avatars:Enabled.
        services.AddScoped<AssistantAvatarTools>();
        // The events a video analysis found: two read-only tools (list by section/type, describe a window).
        services.AddScoped<AssistantVideoEventsTools>();
        services.AddScoped<AssistantRecipeTools>();
        // Output links (GET /api/v1/output-links/{token}) and the output tools that mint them. The link
        // service shares the confirmation key, so it is a singleton like that service; the resolver,
        // describer and tools read the request's DbContext. Registering the tools here does not expose
        // them: AssistantToolProvider composes the surfaces it lists.
        services.AddSingleton<ReelBolt.Inference.Api.Services.Outputs.IOutputLinkService>(sp =>
            new ReelBolt.Inference.Api.Services.Outputs.OutputLinkService(
                sp.GetRequiredService<IConfirmationTokenService>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetService<TimeProvider>()));
        services.AddScoped<ReelBolt.Inference.Api.Services.Outputs.IOutputLinkResolver, ReelBolt.Inference.Api.Services.Outputs.OutputLinkResolver>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Outputs.IOutputDescriber, ReelBolt.Inference.Api.Services.Outputs.OutputDescriber>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Outputs.IStorageObjectCopier, ReelBolt.Inference.Api.Services.Outputs.S3StorageObjectCopier>();
        // S-74: the process-local bound on the stills extractions LookAtOutput may start (one instance per process,
        // shared by every scoped tool instance, so two members cannot each spend the whole allowance).
        services.AddSingleton<ReelBolt.Inference.Api.Services.Assistant.StillsExtractionGate>();
        services.AddScoped<AssistantOutputTools>();
        services.AddScoped<AssistantAdminTools>();
        // The provider connection test, shared by the admin page's Test button (the controller builds
        // its own instance from the same factories) and the assistant's TestInferenceProvider tool.
        services.AddScoped<InferenceProviderTester>();
        // Shared CRUD/test logic behind the platform and the org-scoped provider endpoints (A9b).
        services.AddScoped<InferenceProviderAdminService>();
        // The model catalog's price book (pre-fills a platform capability's prices) and the boot-time upgrader
        // (compute provider, one-time price port, video variants, consolidation; docs/design/model-catalog.md §4.2).
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.PriceBook>();
        services.AddScoped<ModelCatalogUpgrader>();
        services.AddScoped<AssistantKnowledgeTools>();
        // Editing styles (docs/editing-styles.md): the run-time style/format resolver shared by the
        // execute endpoints and the assistant, and the assistant's style surface.
        services.AddScoped<IRunStyleResolver, RunStyleResolver>();
        services.AddScoped<IBriefFileResolver, BriefFileResolver>();
        services.AddScoped<AssistantStyleTools>();
        // The design-system surface (SPEC 15): reads the project's brand system and pending changes,
        // proposes a Draft and decides a pending change through token-carrying pairs.
        services.AddScoped<AssistantDesignSystemTools>();
        // Project history (versions): list, diff, save a named version, restore through propose/apply pairs.
        services.AddScoped<AssistantHistoryTools>();
        // Social publishing: reads of accounts, queue and performance; schedule and cancel as always-ask pairs (never over MCP).
        services.AddScoped<AssistantSocialTools>();
        // The picture editor surface: its writes go through the editor's own controller actions.
        services.AddScoped<ReelBolt.Inference.Api.Services.Assistant.IImageDocumentCommands,
            ReelBolt.Inference.Api.Services.Assistant.ControllerImageDocumentCommands>();
        services.AddScoped<AssistantPictureTools>();
        services.AddScoped<AssistantPictureDocumentTools>();
        // Process-wide count of assistant turns in flight, so background summarization and indexing
        // can give the person at the keyboard priority on the shared providers.
        services.AddSingleton<AssistantActivityMonitor>();
        services.AddSingleton<AssistantTurnRegistry>();
        // The "ask permission" step in front of every assistant tool that writes
        // (docs/design/assistant-approvals.md): the broker pairs a waiting turn with the user's click, and
        // the card builder words the card from names it reads (scoped: it shares the request's DbContext).
        services.AddSingleton<AssistantApprovalBroker>();
        services.AddScoped<AssistantApprovalCardBuilder>();

        // --- Platform documentation index (docs/platform-docs-search.md): the shipped /docs tree,
        // chunked by heading and embedded into a global Qdrant collection the assistant searches
        // (SearchPlatformDocs) and retrieves from on every turn (PlatformDocsContextBuilder). ---
        services.Configure<PlatformDocsOptions>(configuration.GetSection(PlatformDocsOptions.SectionName));
        services.AddSingleton<PlatformDocsCorpusProvider>();
        services.AddSingleton<QdrantPlatformDocsIndex>();
        services.AddSingleton<IPlatformDocsSearch>(sp => sp.GetRequiredService<QdrantPlatformDocsIndex>());
        services.AddScoped<PlatformDocsContextBuilder>();
        services.AddHostedService<PlatformDocsIndexingService>();
        services.AddHttpClient(DecisionCalibrationClient.HttpClientName, (sp, client) =>
        {
            client.BaseAddress = new Uri(
                sp.GetRequiredService<IConfiguration>()["WorkflowEngine:BaseUrl"] ?? "http://workflow-engine:8080");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IDecisionCalibrationClient, DecisionCalibrationClient>();

        // --- Editor v2 (P8): AI-assist proposals run through the matching built-in agent's chat provider ---
        services.AddSingleton<ReelBolt.Inference.Api.Services.Editing.IEditProposalModel, ReelBolt.Inference.Api.Services.Editing.AgentEditProposalModel>();

        // --- File Summarizer Agent ---
        services.AddSingleton<IReelBoltAgent, FileSummarizerAgentImpl>();
        services.AddSingleton<IAgentRegistry, AgentRegistry>();

        // --- Background Task Queue (file summarization) ---
        // Uploads only ever TryQueue (never wait); the service's sweeper drains Pending rows the
        // queue had no room for, and yields to in-flight assistant turns (AssistantActivityMonitor).
        services.Configure<FileSummarizationOptions>(configuration.GetSection(FileSummarizationOptions.SectionName));
        services.Configure<FileIndexingOptions>(configuration.GetSection(FileIndexingOptions.SectionName));
        services.AddSingleton<IBackgroundTaskQueue<FileSummarizationTask>>(sp =>
            new ChannelBackgroundTaskQueue<FileSummarizationTask>(
                sp.GetRequiredService<IOptions<FileSummarizationOptions>>().Value.QueueCapacity));
        services.AddHostedService<FileSummarizationService>();

        // --- Billing (B9): webhook processing + provider boundary; endpoints 404 unless Billing:Enabled ---
        // BillingOptions itself is registered by AddReelBoltEntitlements (ReelBolt.Shared.Billing); only the
        // Paddle half is API-local, and it binds its own section so no duplicate BillingOptions exists.
        // Validated on start so a misconfigured environment fails the boot instead of quietly
        // selling nothing, or (worse) resolving to the wrong Paddle account. An install with no
        // Paddle API key is not "misconfigured", it is simply not selling, and still boots —
        // DescribeProblem() is only ever non-null once an API key declares the intent to bill.
        services.AddOptions<ReelBolt.Inference.Api.Services.Billing.PaddleOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Billing.PaddleOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Billing.PaddleOptions>,
            ReelBolt.Inference.Api.Services.Billing.PaddleOptionsValidator>();
        // Which provider takes new checkouts (Billing:Provider, default Paddle). An unknown value fails the boot.
        services.AddOptions<ReelBolt.Inference.Api.Services.Billing.BillingProviderOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Billing.BillingProviderOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Billing.BillingProviderOptions>,
            ReelBolt.Inference.Api.Services.Billing.BillingProviderOptionsValidator>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.BillingEventProcessor>(sp => new ReelBolt.Inference.Api.Services.Billing.BillingEventProcessor(
            sp.GetRequiredService<ReelBolt.Inference.Api.Data.InferenceApiDbContext>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.Billing.BillingEventProcessor>>(),
            promotionOptions: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReelBolt.Inference.Api.Services.Billing.Promotions.PromotionOptions>>(),
            // The funnel writer the payment step emits alpha.first_payment through (the fallback when no
            // scope factory is supplied) and the scope factory it opens after the money commits so a failed
            // funnel write can never roll a payment back. The fresh scope resolves the registered
            // IProductEventLog, so a decorator or replacement reaches the payment path.
            productEvents: sp.GetRequiredService<ReelBolt.Inference.Api.Services.Telemetry.IProductEventLog>(),
            scopeFactory: sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>()));
        // Security R1: the per-org claim serializing subscription changes. Postgres: a session advisory
        // lock shared by every replica; any other provider (Sqlite/InMemory tests): one process's claim.
        // Built from the configured string given to UseNpgsql, not DatabaseFacade.GetConnectionString():
        // with ConfigureDataSource that one has no Password, so the lock could not authenticate.
        // S-4/S-23: Billing:ChangeLock caps the lock's unpooled connections per replica (validated on start).
        services.AddOptions<ReelBolt.Inference.Api.Services.Billing.BillingChangeLockOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Billing.BillingChangeLockOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Billing.BillingChangeLockOptions>,
            ReelBolt.Inference.Api.Services.Billing.BillingChangeLockOptionsValidator>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.IBillingChangeLock>(sp =>
        {
            Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database =
                sp.GetRequiredService<ReelBolt.Inference.Api.Data.InferenceApiDbContext>().Database;
            return database.IsNpgsql() && connectionString is { Length: > 0 }
                ? new ReelBolt.Inference.Api.Services.Billing.PostgresBillingChangeLock(
                    connectionString,
                    sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.Billing.BillingChangeLockOptions>>().Value)
                : ReelBolt.Inference.Api.Services.Billing.InProcessBillingChangeLock.Shared;
        });

        // POST /billing/checkout/confirm: 10 calls per organization per minute, shared by every request of this process.
        services.AddSingleton<ReelBolt.Inference.Api.Services.Billing.CheckoutConfirmLimiter>();

        // Lost-webhook recovery (Billing:Reconcile): feeds the provider's live state through the same
        // BillingEventProcessor. A no-op while Billing:Enabled or Billing:Reconcile:Enabled is false.
        services.AddOptions<ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileOptions>,
            ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileOptionsValidator>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.Reconcile.IBillingReconciler>(sp => new ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconciler(
            sp.GetRequiredService<ReelBolt.Inference.Api.Data.InferenceApiDbContext>(),
            sp.GetServices<ReelBolt.Inference.Api.Services.Billing.IBillingProvider>(),
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.Billing.BillingEventProcessor>(),
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.Billing.IBillingChangeLock>(),
            sp.GetRequiredService<IOptions<ReelBolt.Shared.Billing.BillingOptions>>(),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileOptions>>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconciler>>(),
            sp.GetService<ITenantContext>(),
            sp.GetService<TimeProvider>(),
            // The optional provider selection: omitted, a Stripe checkout with no local row resolved to the first configured provider (Paddle).
            sp.GetService<IOptions<ReelBolt.Inference.Api.Services.Billing.BillingProviderOptions>>()));
        // The periodic sweep: overdue changes, lost renewals and cancellations, one org scope at a time.
        services.AddHostedService(sp => new ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<ReelBolt.Shared.Billing.BillingOptions>>(),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileOptions>>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileSweeper>>(),
            sp.GetService<TimeProvider>()));
        // Annual subscriptions are paid once and refilled monthly: this sweep grants months 1..11 of each term.
        services.AddOptions<ReelBolt.Inference.Api.Services.Billing.AnnualGrantOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Billing.AnnualGrantOptions.SectionName));
        services.AddHostedService(sp => new ReelBolt.Inference.Api.Services.Billing.AnnualCreditGrantSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<ReelBolt.Shared.Billing.BillingOptions>>(),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.Billing.AnnualGrantOptions>>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.Billing.AnnualCreditGrantSweeper>>(),
            sp.GetService<TimeProvider>()));

        // Paddle (merchant of record, D11) over a plain HttpClient: no SDK. Inert until Billing:Paddle credentials are set.
        services.AddHttpClient<ReelBolt.Inference.Api.Services.Billing.Paddle.PaddleBillingProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddTransient<ReelBolt.Inference.Api.Services.Billing.IBillingProvider>(sp =>
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.Billing.Paddle.PaddleBillingProvider>());

        // Stripe (optionally Managed Payments) over a plain HttpClient: no SDK. Inert until Billing:Stripe credentials are set;
        // the validator only enforces when an API key is set or Billing:Provider is Stripe.
        services.AddOptions<ReelBolt.Inference.Api.Services.Billing.Stripe.StripeOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Billing.Stripe.StripeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Billing.Stripe.StripeOptions>,
            ReelBolt.Inference.Api.Services.Billing.Stripe.StripeOptionsValidator>();
        services.AddHttpClient<ReelBolt.Inference.Api.Services.Billing.Stripe.StripeBillingProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddTransient<ReelBolt.Inference.Api.Services.Billing.IBillingProvider>(sp =>
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.Billing.Stripe.StripeBillingProvider>());

        // Assistant:ExecutionGuardLockWaitMilliseconds (0..60000) is read at call time by AssistantExecutionGuard;
        // this startup check fails the boot on a bad value instead of the first assistant run.
        services.AddOptions<ReelBolt.Inference.Api.Services.Workflows.AssistantExecutionGuardOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Workflows.AssistantExecutionGuardOptions.SectionName))
            .Validate(
                o => o.ExecutionGuardLockWaitMilliseconds is >= 0 and <= 60000,
                "Assistant:ExecutionGuardLockWaitMilliseconds must be between 0 and 60000.")
            .ValidateOnStart();

        // Promotions: codes, automatic discounts, pack bonuses and admin credit grants.
        // The code-guessing limiter is process-wide, so it is a singleton over an injectable clock.
        services.AddSingleton(sp => new ReelBolt.Inference.Api.Services.Billing.Promotions.PromotionCodeAttemptLimiter(
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        // S-11: CheckoutLockWaitMilliseconds is validated on start (1..60000); a bad value fails the boot.
        services.AddOptions<ReelBolt.Inference.Api.Services.Billing.Promotions.PromotionOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Billing.Promotions.PromotionOptions.SectionName))
            .Validate(
                o => o.CheckoutLockWaitMilliseconds is >= 1 and <= 60000,
                "Billing:Promotions:CheckoutLockWaitMilliseconds must be between 1 and 60000.")
            .ValidateOnStart();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.Promotions.IPromotionService, ReelBolt.Inference.Api.Services.Billing.Promotions.PromotionService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.Promotions.IPromotionPlanPriceService, ReelBolt.Inference.Api.Services.Billing.Promotions.PromotionPlanPriceService>();
        // Platform-admin manual plan grants (a subscriptions row with provider NULL plus its audit row).
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.Promotions.IPlanGrantService, ReelBolt.Inference.Api.Services.Billing.Promotions.PlanGrantService>();
        // C10: cancels the provider transactions of expired and superseded promotion reservations.
        services.AddHostedService<ReelBolt.Inference.Api.Services.Billing.Promotions.PromotionCheckoutSweeper>();

        // --- GitHub App integration (docs/github-app.md) ---
        // Inert until GitHub:AppId + the private key + the webhook secret are configured: the webhook
        // answers 503, linking is refused, and no outbound call is ever made. The app client is a
        // singleton so its installation-token cache is shared (a scoped typed client would re-mint a
        // token per request), over a named HttpClient from the factory.
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.AddHttpClient(GitHubAppClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<IGitHubAppClient>(sp => new GitHubAppClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubAppClient.HttpClientName),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>(),
            sp.GetRequiredService<ILogger<GitHubAppClient>>()));
        services.AddSingleton<IGitHubWebhookVerifier, GitHubWebhookVerifier>();
        // Connecting a person's GitHub account (user-to-server OAuth). Scoped, like the rest: it reads
        // the request's own (user, organization) pair and touches the DbContext per call.
        services.AddScoped<IGitHubUserOAuthService, GitHubUserOAuthService>();
        services.AddScoped<IGitHubAccountService, GitHubAccountService>();
        services.AddScoped<IGitHubLinkService, GitHubLinkService>();
        services.AddScoped<IGitHubRepositoryPullService, GitHubRepositoryPullService>();
        services.AddScoped<IGitHubDeliveryProcessor, GitHubDeliveryProcessor>();
        // One on-demand sync per project at a time, and a short cooldown on "Sync now". Singleton: the
        // window has to outlive the request that opened it.
        services.AddSingleton(_ => new GitHubSyncThrottle());
        // "Sync now", a new link and a changed selection are queued and pulled here, off the request:
        // a pull of a few hundred files outlives nginx's 60 s and the browser, and must not be
        // cancelled when the request is. The worker holds the throttle lease while it pulls.
        services.AddSingleton<GitHubSyncQueue>();
        services.AddHostedService<GitHubSyncWorker>();
        // Drains the delivery queue a verified push writes: the webhook acknowledges immediately and
        // this owns the slow half, so a large repository cannot make GitHub abandon the delivery.
        services.AddHostedService<GitHubPushSyncService>();

        // --- Cloud storage (Google Drive, OneDrive/SharePoint, Dropbox folder sync) ---
        // docs/design/cloud-drive-sync.md. Inert until a provider's CloudStorage:<Provider>:ClientId and
        // ClientSecret are set: an unconfigured provider is hidden and its endpoints answer 503. Each
        // provider is a stateless singleton over its own named HttpClient; the OneDrive client must not
        // follow redirects, because Graph's download answers 302 to a pre-authenticated host that must
        // never receive the bearer token (the provider follows it itself, without Authorization).
        services.Configure<ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageOptions.SectionName));
        AddCloudStorageHttpClient(services, ReelBolt.Inference.Api.Services.CloudStorage.Providers.GoogleDriveStorageProvider.HttpClientName);
        AddCloudStorageHttpClient(services, ReelBolt.Inference.Api.Services.CloudStorage.Providers.MicrosoftGraphStorageProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        AddCloudStorageHttpClient(services, ReelBolt.Inference.Api.Services.CloudStorage.Providers.DropboxStorageProvider.HttpClientName);
        services.AddSingleton(sp => new ReelBolt.Inference.Api.Services.CloudStorage.Providers.GoogleDriveStorageProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ReelBolt.Inference.Api.Services.CloudStorage.Providers.GoogleDriveStorageProvider.HttpClientName),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageOptions>>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.CloudStorage.Providers.GoogleDriveStorageProvider>>()));
        services.AddSingleton(sp => new ReelBolt.Inference.Api.Services.CloudStorage.Providers.MicrosoftGraphStorageProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ReelBolt.Inference.Api.Services.CloudStorage.Providers.MicrosoftGraphStorageProvider.HttpClientName),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageOptions>>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.CloudStorage.Providers.MicrosoftGraphStorageProvider>>()));
        services.AddSingleton(sp => new ReelBolt.Inference.Api.Services.CloudStorage.Providers.DropboxStorageProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ReelBolt.Inference.Api.Services.CloudStorage.Providers.DropboxStorageProvider.HttpClientName),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageOptions>>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.CloudStorage.Providers.DropboxStorageProvider>>()));
        services.AddSingleton<ReelBolt.Inference.Api.Services.CloudStorage.ICloudStorageProvider>(sp =>
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.CloudStorage.Providers.GoogleDriveStorageProvider>());
        services.AddSingleton<ReelBolt.Inference.Api.Services.CloudStorage.ICloudStorageProvider>(sp =>
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.CloudStorage.Providers.MicrosoftGraphStorageProvider>());
        services.AddSingleton<ReelBolt.Inference.Api.Services.CloudStorage.ICloudStorageProvider>(sp =>
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.CloudStorage.Providers.DropboxStorageProvider>());
        services.AddSingleton<ReelBolt.Inference.Api.Services.CloudStorage.ICloudStorageProviderRegistry,
            ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageProviderRegistry>();
        services.AddScoped<ReelBolt.Inference.Api.Services.CloudStorage.ICloudStorageConnectionService,
            ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageConnectionService>();
        // Social publishing (docs/social-publishing.md). Inert until a platform's SocialPublishing:<Platform>:ClientId
        // and ClientSecret are set; no adapter is registered yet, so every platform is hidden until its layer ships.
        // Adapters register as ISocialPlatform singletons.
        services.Configure<ReelBolt.Inference.Api.Services.SocialPublishing.SocialPublishingOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.SocialPublishing.SocialPublishingOptions.SectionName));
        services.AddSingleton<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialPlatformRegistry,
            ReelBolt.Inference.Api.Services.SocialPublishing.SocialPlatformRegistry>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.SocialPublishing.SocialRefreshLock>();
        services.AddScoped<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialConnectionService,
            ReelBolt.Inference.Api.Services.SocialPublishing.SocialConnectionService>();
        // The publish engine: scoped service plus the hosted sweeper (claims by conditional UPDATE, so replicas never double-send).
        services.AddScoped<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialOutputResolver,
            ReelBolt.Inference.Api.Services.SocialPublishing.SocialOutputResolver>();
        services.AddScoped<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialPostModeration,
            ReelBolt.Inference.Api.Services.SocialPublishing.SocialPostModeration>();
        services.AddScoped<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialPublishingService,
            ReelBolt.Inference.Api.Services.SocialPublishing.SocialPublishingService>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.SocialPublishing.SocialPublishSweeper>();
        services.AddScoped<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialMetricsCollector, ReelBolt.Inference.Api.Services.SocialPublishing.SocialMetricsCollector>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.SocialPublishing.SocialMetricsSweeper>();
        // Platform adapters: stateless singletons over their own named HttpClient (video uploads run through them, so the
        // timeout is SocialPublishing:HttpTimeoutSeconds). No redirects: a bearer token must never follow one.
        foreach (string clientName in new[] { ReelBolt.Inference.Api.Services.SocialPublishing.YouTubeSocialPlatform.HttpClientName, ReelBolt.Inference.Api.Services.SocialPublishing.LinkedInSocialPlatform.HttpClientName })
        {
            services.AddHttpClient(clientName, (sp, client) =>
                client.Timeout = TimeSpan.FromSeconds(Math.Max(10, sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.SocialPublishing.SocialPublishingOptions>>().Value.HttpTimeoutSeconds)))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        }
        services.AddSingleton<ReelBolt.Inference.Api.Services.SocialPublishing.SocialQuotaCounter>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialPlatform>(sp => new ReelBolt.Inference.Api.Services.SocialPublishing.YouTubeSocialPlatform(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ReelBolt.Inference.Api.Services.SocialPublishing.YouTubeSocialPlatform.HttpClientName),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.SocialPublishing.SocialPublishingOptions>>(),
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.SocialPublishing.SocialQuotaCounter>()));
        services.AddSingleton<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialPlatform>(sp => new ReelBolt.Inference.Api.Services.SocialPublishing.LinkedInSocialPlatform(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ReelBolt.Inference.Api.Services.SocialPublishing.LinkedInSocialPlatform.HttpClientName),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.SocialPublishing.SocialPublishingOptions>>()));
        services.AddScoped<ReelBolt.Inference.Api.Services.CloudStorage.ICloudSyncService,
            ReelBolt.Inference.Api.Services.CloudStorage.CloudSyncService>();
        // Every sync is queued and run by the worker (2 at a time, own scope, stopping token only),
        // never inside a request; the sweeper re-queues stale links every SweepIntervalMinutes.
        services.AddSingleton(_ => new ReelBolt.Inference.Api.Services.CloudStorage.CloudSyncQueue());
        services.AddHostedService<ReelBolt.Inference.Api.Services.CloudStorage.CloudSyncWorker>();
        services.AddHostedService(sp => new ReelBolt.Inference.Api.Services.CloudStorage.CloudSyncSweeper(
            sp.GetRequiredService<ReelBolt.Inference.Api.Services.CloudStorage.CloudSyncQueue>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageOptions>>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.CloudStorage.CloudSyncSweeper>>()));

        // --- Media derivative coverage (media-derivative-backfill) ---
        // The upload path publishes its derivative request best-effort; this sweep is the durable
        // repair. It re-derives "video source with no proxy" from the source rows, so a publish lost
        // to a broker block (or one that never happened, for the back catalogue) is retried on the
        // next tick. See MediaDerivativeBackfillOptions for why a sweeper, not an outbox.
        services.Configure<ReelBolt.Inference.Api.Services.Media.MediaDerivativeBackfillOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Media.MediaDerivativeBackfillOptions.SectionName));
        services.AddScoped<ReelBolt.Inference.Api.Services.Media.MediaDerivativeCoverageService>();
        services.AddHostedService(sp => new ReelBolt.Inference.Api.Services.Media.MediaDerivativeBackfillService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.Media.MediaDerivativeBackfillOptions>>(),
            sp.GetRequiredService<ILogger<ReelBolt.Inference.Api.Services.Media.MediaDerivativeBackfillService>>()));

        // --- .ts type repair (issue 8, 2026-11-03) ---
        // Rows stored before the upload/sync/import paths read a .ts file's bytes were typed as video
        // (the extension maps call every .ts a transport stream). One pass at startup re-types them from
        // their first bytes; a re-typed row no longer matches, so the pass is idempotent.
        services.Configure<ReelBolt.Inference.Api.Services.Uploads.TransportStreamMimeBackfillOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Uploads.TransportStreamMimeBackfillOptions.SectionName));
        services.AddScoped<ReelBolt.Inference.Api.Services.Uploads.TransportStreamMimeBackfill>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Uploads.TransportStreamMimeBackfillService>();

        // --- Computer folder sync (docs/design/local-folder-sync-and-storage.md, Part 1) ---
        // The desktop app signs every /api/v1/device/** request with its Ed25519 key; the filter
        // authenticates it before model binding. Nonce cache and manifest buffer are per process.
        services.AddSingleton<ReelBolt.Inference.Api.Services.FolderSync.DeviceNonceCache>();
        services.AddSingleton(_ => new ReelBolt.Inference.Api.Services.FolderSync.FolderSyncManifestBuffer());
        services.AddScoped<ReelBolt.Inference.Api.Services.FolderSync.DeviceRequestAuthenticator>();
        services.AddScoped<ReelBolt.Inference.Api.Services.FolderSync.DeviceAuthenticationFilter>();
        services.AddScoped<ReelBolt.Inference.Api.Services.FolderSync.DeviceProjectAccess>();
        services.AddScoped<ReelBolt.Inference.Api.Services.FolderSync.LocalFolderSyncService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.FolderSync.DeviceSelfRemoval>();

        // --- Startup cleanup ---
        // ensure any messages left in the RabbitMQ queues are purged and mark any
        // executions that were "Running" when the service stopped as cancelled.
        services.AddHostedService<StartupCleanupService>();
        // Deletes expired / long-revoked cloned voices (vendor model, reference recordings, row) —
        // see docs/voiceover.md "Voices and consent".
        services.AddHostedService<ProjectVoiceRetentionService>();
        // Deletes expired / long-revoked avatars through the one delete routine, sends the 7-day warning and purges
        // audit events older than three years (docs/design/avatars.md "Retention sweep"). Staging switch:
        // ProjectAvatars:RetentionSweepEnabled.
        services.AddHostedService<ProjectAvatarRetentionService>();
        // Daily GB-day storage metering per organization; off unless Billing:StorageSampler:Enabled.
        services.AddHostedService<ReelBolt.Inference.Api.Services.Billing.StorageUsageSampler>();
        // F10 per-plan storage retention: soft-marks generated storage past the org's RetentionDays,
        // then purges it after a 7-day grace period. Off unless Billing:StorageRetention:Enabled.
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.StorageRetentionService>();
        // F11 step 6: the periodic spend report off the B ledger and its threshold alert. Off unless
        // Billing:SpendReport:Enabled, so self-host is untouched.
        services.Configure<ReelBolt.Inference.Api.Services.Background.SpendReportOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Background.SpendReportOptions.SectionName));
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.DailySpendReportService>();
        // The daily operator digest (platform step failures, runs by status, new feedback) by mail through the
        // Go relay. Off unless Operations:FailureDigest:Enabled (OPERATOR_DIGEST_ENABLED); enabled without a
        // recipient fails at startup.
        services.AddOptions<ReelBolt.Inference.Api.Services.Background.FailureDigestOptions>()
            .Bind(configuration.GetSection(ReelBolt.Inference.Api.Services.Background.FailureDigestOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Background.FailureDigestOptions>,
            ReelBolt.Inference.Api.Services.Background.FailureDigestOptionsValidator>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.OperatorFailureDigestService>();
        // MCP OAuth hygiene (docs/design/mcp-oauth.md §5): removes expired/consumed authorization
        // codes, expired/recently-revoked refresh tokens and never-used client registrations, so the
        // tables the OAuth feature added stay bounded. On by default (McpOAuth:Purge:Enabled) — it
        // only deletes rows that are already unusable.
        services.Configure<ReelBolt.Inference.Api.Services.Background.McpOAuthPurgeOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Background.McpOAuthPurgeOptions.SectionName));
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.McpOAuthPurgeService>();
        // Editor render hygiene: deletes hidden TimelineRender workflows with no execution older than
        // an hour (left by render attempts before the start became one transaction). At startup, then
        // hourly; on by default, Editing:RenderOrphanReaper:Enabled=false turns it off. Not a migration.
        services.Configure<ReelBolt.Inference.Api.Services.Background.TimelineRenderOrphanReaperOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Background.TimelineRenderOrphanReaperOptions.SectionName));
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.TimelineRenderOrphanReaper>();
        // R10.4 "Preview 3 s": preview files expire after Editing:Preview:RetentionMinutes (60); the per-user cap is
        // Editing:Preview:MaxPerHourPerUserProject (30). The orphan reaper above covers preview workflows too.
        services.Configure<ReelBolt.Inference.Api.Services.Editing.TimelinePreviewOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Editing.TimelinePreviewOptions.SectionName));
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.TimelinePreviewSweeper>();
        // Editor retention (D14): a deleted timeline is restorable for Editing:TrashRetentionDays (30) and
        // then purged hourly; automatic version checkpoints expire after Editing:AutoCheckpointDays (30),
        // pruned daily. Named versions and revision 1 are never pruned.
        services.Configure<ReelBolt.Inference.Api.Services.Editing.EditingRetentionOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Editing.EditingRetentionOptions.SectionName));
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.TimelineTrashPurgeService>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Assistant.IAssistantWakeTurnRunner, ReelBolt.Inference.Api.Services.Assistant.AssistantWakeTurnRunner>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.AssistantRunWakeService>();
        // Drain mode (zero-downtime deploy): inert unless /tmp/reelbolt-drain appears.
        ReelBolt.Shared.Drain.DrainServiceCollectionExtensions.AddReelBoltDrain(services);
        services.AddSingleton<ReelBolt.Inference.Api.Middleware.InFlightRequestCounter>();
        services.AddSingleton<ReelBolt.Shared.Drain.IDrainWorkSource>(sp => sp.GetRequiredService<ReelBolt.Inference.Api.Middleware.InFlightRequestCounter>());
        services.AddSingleton<ReelBolt.Shared.Drain.IDrainWorkSource, ReelBolt.Inference.Api.Middleware.WakeTurnDrainWork>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.TimelineSnapshotPruneService>();
        // "Find this object" for every entry (the editor panel and the assistant), the server-side timeline writer, and the
        // finalizer that writes a finished analysis's regionEffects onto the clips the assistant named.
        services.AddScoped<ReelBolt.Inference.Api.Services.ObjectAnalysis.IObjectAnalysisService, ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectAnalysisService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Media.IVideoEnhanceService, ReelBolt.Inference.Api.Services.Media.VideoEnhanceService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Follow.SubjectTrackService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Follow.FollowPathService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.EditTimelines.IEditTimelineWriter, ReelBolt.Inference.Api.Services.EditTimelines.EditTimelineWriter>();
        services.AddScoped<ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectEffectApplier>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Background.ObjectEffectApplySweeper>();
        // C6: lazy, idempotent trial-credit provisioning (own short-lived scope + context per call).
        services.AddFirstVideoGrant();
        // B7: the pre-run cost estimate and the execute preflight. Singleton for the same reason as
        // the provisioner — it opens its own short-lived scope per call — and it takes the same
        // "Billing" section the engine binds (Enabled gates it, Billing:Estimate:* tunes the window).
        services.Configure<ReelBolt.Shared.Metering.StepCreditEstimateOptions>(
            configuration.GetSection(ReelBolt.Shared.Metering.StepCreditEstimateOptions.SectionName));
        services.AddSingleton<ReelBolt.Inference.Api.Services.Billing.IWorkflowCostEstimator, ReelBolt.Inference.Api.Services.Billing.WorkflowCostEstimator>();
        // B7: the shared execute preflight. Scoped — it reads this request's DbContext — while the
        // estimator it calls is the singleton above (it opens its own short-lived scope per call).
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.IWorkflowExecutionPreflight, ReelBolt.Inference.Api.Services.Billing.WorkflowExecutionPreflight>();

        // Decision 9: non-workflow charging (assistant/MCP allowance, editor ops, timeline proposals). Binds the
        // same "Billing" section; Billing:Enabled=false makes every path a no-op. Fresh context per ledger call.
        services.Configure<ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeOptions.SectionName));
        // The turn wall clock vs the stale turn-hold age, checked when the host starts: a bad Assistant:MaxTurnSeconds
        // fails the boot instead of the first assistant turn.
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeOptions>,
            ReelBolt.Inference.Api.Services.Billing.TurnClockValidation>();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeOptions>,
            ReelBolt.Inference.Api.Services.Billing.TurnBudgetValidation>();
        // with Billing:Enabled a host without the charge service fails the boot, never runs uncharged.
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeOptions>,
            ReelBolt.Inference.Api.Services.Billing.ChargeServiceRequiredValidation>();
        services.AddOptions<ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeOptions>().ValidateOnStart();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.IAssistantAllowanceService, ReelBolt.Inference.Api.Services.Billing.AssistantAllowanceService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Billing.INonWorkflowChargeService>(
            sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeService>());

        // --- C9: data-subject rights (export, deletion and erasure) ---
        // The export is a singleton hosted service so the controller and the queue share one
        // in-flight set and one sweeper; the purge is a plain hosted service that opens its own scope.
        // Both read Account:* and are inert on their own when disabled.
        services.Configure<ReelBolt.Inference.Api.Services.Account.AccountOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Account.AccountOptions.SectionName));
        services.AddHttpClient<
            ReelBolt.Inference.Api.Services.Account.IAccountMailClient,
            ReelBolt.Inference.Api.Services.Account.GoAccountMailClient>(
            ReelBolt.Inference.Api.Services.Account.IAccountMailClient.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<ReelBolt.Inference.Api.Services.Account.AccountExportService>();
        services.AddHostedService(sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Account.AccountExportService>());
        services.AddScoped<ReelBolt.Inference.Api.Services.Account.IAccountBillingCanceller,
            ReelBolt.Inference.Api.Services.Account.AccountBillingCanceller>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Account.AccountPurgeService>();

        // --- saas-promotions: marketing consent (not billing-gated) ---
        // Keys in platform_secrets (marketing-link-token-k1, marketing-suppression-pepper), the
        // wording registry next to the assembly, the confirmation-mail queue in front of the Go relay,
        // and the daily pending-row sweeper.
        services.Configure<ReelBolt.Inference.Api.Services.Marketing.MarketingOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Marketing.MarketingOptions.SectionName));
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketing.IMarketingSecretStore,
            ReelBolt.Inference.Api.Services.Marketing.PlatformSecretKeyStore>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketing.IMarketingLinkTokens,
            ReelBolt.Inference.Api.Services.Marketing.MarketingLinkTokens>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketing.IMarketingSuppressionHasher,
            ReelBolt.Inference.Api.Services.Marketing.MarketingSuppressionHasher>();
        services.AddSingleton(sp =>
        {
            string? path = sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.Marketing.MarketingOptions>>().Value.WordingPath;
            return ReelBolt.Inference.Api.Services.Marketing.MarketingWording.LoadFile(
                string.IsNullOrWhiteSpace(path) ? ReelBolt.Inference.Api.Services.Marketing.MarketingWording.DefaultPath : path);
        });
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketing.MarketingMailOutbox>();
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketing.IMarketingMailOutbox>(
            sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Marketing.MarketingMailOutbox>());
        services.AddHostedService(sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Marketing.MarketingMailOutbox>());
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.IMailRateCounterStore,
            ReelBolt.Inference.Api.Services.Marketing.PostgresMailRateCounterStore>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.MarketingConsentService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.MarketingNameReconciler>();
        services.Configure<ReelBolt.Inference.Api.Services.Marketing.MarketingCampaignOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Marketing.MarketingCampaignOptions.SectionName));
        services.AddHttpClient<ReelBolt.Inference.Api.Services.Marketing.IBrevoCampaignClient,
            ReelBolt.Inference.Api.Services.Marketing.BrevoCampaignClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
            // The default handler logs the request URL at Information; the contact calls carry the address in the path.
            .RemoveAllLoggers();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.MarketingCampaignService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.BrevoContactGuard>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.BrevoErasureOutbox>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.IBrevoErasureDrain>(
            sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Marketing.BrevoErasureOutbox>());
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketing.BrevoErasureWorker>();
        // Tracked links of the new-user funnel (docs/design/new-user-funnel.md): the anonymous redirect
        // GET /api/v1/l/{code} and the platform-admin links routes. Attribution:UrlCaptureEnabled mirrors the
        // Go API's ATTRIBUTION_URL_CAPTURE_ENABLED so the links funnel can say whether its account counts are complete.
        services.Configure<ReelBolt.Inference.Api.Services.Marketing.Links.MarketingLinksOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Marketing.Links.MarketingLinksOptions.SectionName));
        services.Configure<ReelBolt.Inference.Api.Services.Telemetry.AttributionOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Telemetry.AttributionOptions.SectionName));
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.Links.TrackedLinkService>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.Links.ITrackedLinkDirectory>(
            sp => sp.GetRequiredService<ReelBolt.Inference.Api.Services.Marketing.Links.TrackedLinkService>());
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.Links.ITrackedLinkFunnelReader,
            ReelBolt.Inference.Api.Services.Marketing.Links.TrackedLinkFunnelReader>();
        // funnel-first-run-help: off by default (FirstRunReminder:Enabled), dry-run by default. The Brevo sender is
        // resolved only when Enabled and not DryRun; otherwise the no-op sender is used.
        services.Configure<ReelBolt.Inference.Api.Services.FirstRunReminder.FirstRunReminderOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.FirstRunReminder.FirstRunReminderOptions.SectionName));
        services.AddHttpClient<ReelBolt.Inference.Api.Services.FirstRunReminder.BrevoTransactionalFirstRunReminderSender>(
            c => c.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();
        services.AddScoped<ReelBolt.Inference.Api.Services.FirstRunReminder.IFirstRunReminderSender>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.FirstRunReminder.FirstRunReminderOptions>>().Value;
            return o.Enabled && !o.DryRun
                ? sp.GetRequiredService<ReelBolt.Inference.Api.Services.FirstRunReminder.BrevoTransactionalFirstRunReminderSender>()
                : new ReelBolt.Inference.Api.Services.FirstRunReminder.NoOpFirstRunReminderSender();
        });
        services.AddHostedService<ReelBolt.Inference.Api.Services.FirstRunReminder.FirstRunReminderSweeper>();
        services.Configure<ReelBolt.Inference.Api.Services.Marketing.MarketingContactSyncOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Marketing.MarketingContactSyncOptions.SectionName));
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketing.ContactSyncSchedule>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.ContactSyncService>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketing.BrevoContactSyncWorker>();
        services.AddScoped<ReelBolt.Inference.Api.Services.Marketing.MarketingCampaignPipeline>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketing.MarketingCampaignWorker>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketing.PendingConsentSweeper>();
        // C4: the keyed recipient reference every mail log line uses (Mail:LogRefKey, compose
        // Mail__LogRefKey from MAIL_LOG_REF_KEY), and one startup warning each for an unset log key
        // or site forward token.
        services.Configure<ReelBolt.Inference.Api.Services.Marketing.MailOptions>(
            configuration.GetSection(ReelBolt.Inference.Api.Services.Marketing.MailOptions.SectionName));
        services.AddSingleton<ReelBolt.Inference.Api.Services.Marketing.MailLogRef>();
        services.AddHostedService<ReelBolt.Inference.Api.Services.Marketing.MarketingStartupChecks>();

        // --- MassTransit / RabbitMQ ---
        services.AddMassTransit(x =>
        {
            x.AddConsumer<ProjectFileIndexingConsumer>();
            // Output verdicts: a finished step offers its output to the moderation lane.
            x.AddConsumer<OutputModerationConsumer>();
            // Picture previews: a finished picture step stores its 320/480/1920 px WebP previews.
            x.AddConsumer<ImagePreviewPrewarmConsumer>();
            // Public API webhooks: run events become deliveries (its own queue; the Go API keeps its copies).
            x.AddConsumer<WebhookRunEventsConsumer>();
            // activation.run_failed: a run that ends Failed records one product event.
            x.AddConsumer<RunFailedProductEventConsumer>();
            // R09.1: engine answers for on-demand media analysis
            x.AddConsumer<MediaAnalysisStartedConsumer>();
            x.AddConsumer<MediaAnalysisFinishedConsumer>();
            // recipe marketplace: the engine's preview result (a no-op unless Marketplace:Enabled)
            x.AddConsumer<ReelBolt.Inference.Api.Consumers.MarketplacePreviewFinishedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(configuration["RabbitMQ:Host"] ?? "localhost", "/", h =>
                {
                    h.Username(configuration["RabbitMQ:Username"] ?? "guest");
                    h.Password(configuration["RabbitMQ:Password"] ?? "guest");
                });
                cfg.ConfigureEndpoints(context);
            });
        });

        // --- Controllers & Swagger ---
        // A polymorphic body (an edit timeline's items, discriminated by "type") is accepted with the
        // discriminator anywhere in the object. JavaScript builds objects in whatever key order the
        // code wrote them, so `{ id, type: "mediaClip", ... }` from the editor used to fail the whole
        // PATCH with "must specify a type discriminator" (a 500) and the edit never saved.
        services.AddScoped<ReelBolt.Inference.Api.Middleware.CreditsExhaustedResultFilter>();
        services.AddControllers(mvc =>
        {
            mvc.Filters.AddService<ReelBolt.Inference.Api.Middleware.CreditsExhaustedResultFilter>();
            mvc.Filters.AddService<ReelBolt.Inference.Api.Services.History.ProjectHistoryAmbientFilter>();
        }).AddJsonOptions(options => options.JsonSerializerOptions.AllowOutOfOrderMetadataProperties = true)
            .ConfigureApiBehaviorOptions(api => api.InvalidModelStateResponseFactory = ReelBolt.Inference.Api.Services.PublicApi.PublicApiAuthResponses.InvalidModelState);
        services.AddOpenApi();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(ConfigureSwagger);

        return services;
    }

    /// <summary>The Swagger generation options of the running app: the internal document and the public one.</summary>
    public static void ConfigureSwagger(Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions options)
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "ReelBolt Inference API", Version = "v1" });
        ReelBolt.Inference.Api.Services.PublicApi.PublicApiOpenApi.Configure(options);
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
    }

    /// <summary>
    /// One cloud-storage provider's named HttpClient, its timeout from
    /// <c>CloudStorage:HttpTimeoutSeconds</c> (downloads of up to MaxFileBytes run through it).
    /// </summary>
    private static IHttpClientBuilder AddCloudStorageHttpClient(IServiceCollection services, string name) =>
        services.AddHttpClient(name, (sp, client) =>
        {
            int seconds = sp.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.CloudStorage.CloudStorageOptions>>().Value.HttpTimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(10, seconds));
        });
}
