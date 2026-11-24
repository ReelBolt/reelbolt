using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The ONE place <see cref="AssistantToolProvider"/> is constructed. The chat transport
/// (<see cref="Controllers.AssistantChatController"/>) and the tool bridge
/// (<see cref="Controllers.AssistantToolBridgeController"/>) both compose the assistant's closed tool
/// set through this factory, so the catalog the bridge publishes and the set the chat turn runs
/// cannot drift: a new tool surface is added to <see cref="AssistantToolProvider.GetTools"/> and to
/// the DI registrations, and both transports pick it up with no second list to maintain.
/// </summary>
/// <remarks>
/// <para>
/// The per-area surfaces are resolved from the caller's own request scope rather than passed in, so
/// they share that request's <see cref="InferenceApiDbContext"/>, its <see cref="ICurrentUser"/> and
/// — critically — its singleton confirmation-token service. A caller that resolved the surfaces from
/// a different scope could hand minted tokens to a verifier holding a different key.
/// </para>
/// <para>
/// The request-scoped <see cref="ICurrentUser"/> is handed to the provider as an interface, never a
/// user id: authority stays re-derived inside each gated tool body on the call that is executing.
/// </para>
/// </remarks>
public static class AssistantToolProviderFactory
{
    /// <param name="db">The request's DbContext, shared with the surfaces.</param>
    /// <param name="diffService">The workflow diff/validation service.</param>
    /// <param name="executionService">The workflow execution service.</param>
    /// <param name="executionGuard">The assistant rate-limit guard.</param>
    /// <param name="currentUser">The request-scoped caller — the source of every authority decision.</param>
    /// <param name="services">
    /// The request's service provider, from which the per-area tool surfaces and the optional
    /// collaborators are resolved.
    /// </param>
    public static AssistantToolProvider Create(
        InferenceApiDbContext db,
        IWorkflowEditDiffService diffService,
        IWorkflowExecutionService executionService,
        IAssistantExecutionGuard executionGuard,
        ICurrentUser currentUser,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(diffService);
        ArgumentNullException.ThrowIfNull(executionService);
        ArgumentNullException.ThrowIfNull(executionGuard);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(services);

        return new AssistantToolProvider(
            db,
            diffService,
            executionService,
            executionGuard,
            currentUser,
            services.GetRequiredService<AssistantProjectTools>(),
            services.GetRequiredService<AssistantFileTools>(),
            services.GetRequiredService<AssistantWorkflowTools>(),
            services.GetRequiredService<AssistantAdminTools>(),
            services.GetRequiredService<AssistantKnowledgeTools>(),
            services.GetRequiredService<AssistantStyleTools>(),
            services.GetRequiredService<AssistantDesignSystemTools>(),
            services.GetRequiredService<AssistantPictureTools>(),
            services.GetRequiredService<AssistantOutputTools>(),
            services.GetRequiredService<AssistantUploadTools>(),
            // Not registered by name in the composition root (outside this surface's files); built from the
            // request scope's own services, exactly as a registration would.
            ActivatorUtilities.GetServiceOrCreateInstance<AssistantRunTools>(services),
            services.GetRequiredService<AssistantRevisionTools>(),
            services.GetRequiredService<AssistantChangeRequestTools>(),
            services.GetRequiredService<AssistantWorkflowPatchTools>(),
            services.GetRequiredService<AssistantPictureDocumentTools>(),
            services.GetRequiredService<AssistantGreenScreenTools>(),
            services.GetRequiredService<IProjectAccessService>(),
            null,
            null,
            // B7: the same pre-run credit preflight every other execute path runs. Optional, and
            // resolved from the request scope; a host that never registers it gets the tool's own
            // fallback (a null estimator that refuses nothing).
            services.GetService<IWorkflowExecutionPreflight>(),
            services.GetService<ReelBolt.Inference.Api.Services.DesignSystems.IDesignBuildEstimateGate>(),
            // agent-model-choice: names the model each agent runs on in ListAgents; optional like the preflight.
            services.GetService<ReelBolt.Shared.Inference.IInferenceProviderResolver>(),
            // T-202: the brief-file resolver for ExecuteWorkflow's briefFileId.
            services.GetService<IBriefFileResolver>(),
            services.GetService<WorkflowLintGate>(),
            services.GetRequiredService<AssistantRecipeTools>(),
            // The pre-run linter ValidateWorkflow and ProposeWorkflow run; optional like the lint gate.
            services.GetService<IWorkflowLinter>(),
            services.GetService<ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter>(),
            services.GetService<ReelBolt.Inference.Api.Services.Workflows.VideoSourceGate>(),
            services.GetRequiredService<AssistantMediaOverlayTools>(),
            // Not registered by name in the composition root (outside this surface's files): built from the request scope.
            services.GetService<ReelBolt.Inference.Api.Services.ObjectAnalysis.IObjectAnalysisService>() is null
                ? null
                : ActivatorUtilities.GetServiceOrCreateInstance<AssistantObjectEffectTools>(services),
            // Optional like the media overlay tools: a host that never registers it simply has no cleanup tools.
            services.GetService<AssistantAudioCleanupTools>(),
            // Optional the same way: a host that never registers it has no voice tools.
            services.GetService<AssistantVoiceTools>(),
            // Optional the same way: a host that never registers it has no avatar tool.
            services.GetService<AssistantAvatarTools>(),
            // docs/design/app-inputs.md 6.2: the app-input run check, registered with the switch it reads.
            services.GetService<ReelBolt.Inference.Api.Services.Workflows.AppInputValueValidator>(),
            // Video enhancement: the same IVideoEnhanceService the editor's routes use.
            services.GetService<ReelBolt.Inference.Api.Services.Media.IVideoEnhanceService>() is null
                ? null
                : ActivatorUtilities.GetServiceOrCreateInstance<AssistantVideoEnhanceTools>(services),
            services.GetService<AssistantMarketplaceTools>(),
            services.GetService<AssistantHistoryTools>(),
            // The post-text tool runs the app write route; a host without the writer has no such tool.
            services.GetService<ReelBolt.Inference.Api.Services.PostTexts.IPostTextWriter>() is null
                ? null
                : ActivatorUtilities.GetServiceOrCreateInstance<AssistantPostTextTools>(services),
            // Optional the same way: a host that never registers it has no video event tools.
            services.GetService<AssistantVideoEventsTools>(),
            // Optional the same way: a host that never registers it has no social publishing tools.
            services.GetService<AssistantSocialTools>(),
            // The one media rule every surface asks (the run's own fallback is the same rule over the request's database).
            services.GetService<ReelBolt.Inference.Api.Services.Avatars.IMediaEgressCallPermission>());

    }
}
