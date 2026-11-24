using Microsoft.Extensions.DependencyInjection.Extensions;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.VideoGeneration;

/// <summary>
/// One place that makes <c>StepType.VideoGenerate</c> runnable. Every registration here is a
/// singleton because <see cref="VideoGenerateStepExecutor"/> is (like every <see cref="IStepExecutor"/>),
/// and a singleton must never capture a scoped service: the gate and the executor therefore take
/// <c>IDbContextFactory&lt;WorkflowEngineDbContext&gt;</c> (registered by Program.cs's
/// <c>AddDbContextFactory</c>) and create a short-lived context per call instead of injecting one.
///
/// <para>Relies on these being registered elsewhere: <see cref="IInferenceProviderResolver"/>,
/// <see cref="Storage.IProjectFileWorkspace"/>, <see cref="IVideoToolRunner"/> and
/// <c>IOptions&lt;VideoEditingOptions&gt;</c> (for <see cref="GeneratedClipNormalizer"/>), plus
/// <see cref="Compute.IMediaWorkspaceFactory"/> and <see cref="Video.IKeyframeExtractor"/> — the seam
/// and the ffmpeg operation that cut a planned shot's first/last frame out of its source, so the
/// generated clip can be handed to the provider with a real image on its request.</para>
/// </summary>
public static class VideoGenerationServiceCollectionExtensions
{
    public static IServiceCollection AddVideoGeneration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<VideoGenerationOptions>(configuration.GetSection(VideoGenerationOptions.SectionName));

        // Spending guardrail. Singleton-safe: it owns no DbContext and serialises reservations on a
        // process-wide lock, which is only process-wide if there is exactly one instance anyway.
        services.AddSingleton<IVideoGenerationBudgetGate, VideoGenerationBudgetGate>();

        // Caches one client/HttpClient per resolved provider configuration — must be a singleton
        // for that cache to be worth anything.
        services.AddSingleton<IVideoGenerationClientFactory, VideoGenerationClientFactory>();

        services.AddSingleton<GeneratedClipNormalizer>();
        // AV-9 B seam: no C2PA signer is vetted yet, so avatar clips are marked with container metadata only.
        services.TryAddSingleton<IGeneratedClipProvenanceSigner, NoProvenanceSigner>();
        services.AddSingleton<IStepExecutor, VideoGenerateStepExecutor>();

        return services;
    }
}
