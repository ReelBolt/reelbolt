namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// One tool's directory-facing annotations, in the shape MCP calls <c>annotations</c>.
/// </summary>
/// <param name="Title">A short human title (never empty, at most 64 characters).</param>
/// <param name="ReadOnlyHint">True when the tool neither writes data nor starts a generation.</param>
/// <param name="DestructiveHint">
/// True for every tool that creates, updates or deletes data, and for <c>ExecuteWorkflow</c>
/// (it starts work that spends credits). The connector checklist treats these as always-prompt.
/// </param>
/// <param name="IdempotentHint">True when repeating the same call has the same effect.</param>
/// <param name="OpenWorldHint">True when the tool reaches a model, a vendor, an arbitrary URL or a queued run.</param>
public sealed record AssistantToolAnnotation(
    string Title,
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool IdempotentHint,
    bool OpenWorldHint);

/// <summary>
/// The annotations every assistant/MCP tool carries (Anthropic connector review: every tool needs a
/// title and the applicable read-only/destructive hint).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only is a classification, not a naming convention.</b> The destructive half comes from
/// <see cref="AssistantApprovalCatalog.IsGated"/> — every <c>Apply*</c> tool plus
/// <c>ExecuteWorkflow</c>, the same list the in-app approval cards use. The read half is
/// <see cref="AssistantApprovalCatalog.IsProposal"/> (a proposal writes nothing and mints a token)
/// plus the explicit <see cref="ReadOnlyTools"/> set below. There is no fallback: a registered tool
/// that is neither gated, nor a proposal, nor listed here fails
/// <c>AssistantMcpCatalogTests.Every_tool_is_classified_as_read_only_or_destructive</c>.
/// </para>
/// <para>
/// <b>Titles are presentation only.</b> A gated tool borrows its approval-card fallback title
/// ("Create a project"); everything else is humanized from the registered name. The catalog test
/// asserts non-empty and at most 64 characters.
/// </para>
/// </remarks>
public static class AssistantToolAnnotations
{
    /// <summary>
    /// Tools that read, probe or reserve delivery state but never change the user's data. Proposals
    /// are read-only by classification and do not need an entry here.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "ListAgents", "ListStepSchemas", "GetStepCatalog", "GetProjectReadiness", "EstimateWorkflowRun",
        "ListProjectFiles", "ValidateWorkflow",
        "ListProjects", "GetProject", "ListWorkflows",
        "ReadProjectFile", "ReadProjectFileLines", "GetProjectFileOutline",
        "ListAgentsAdmin", "ListInferenceProviders", "ListSkills", "GetDecisionCalibration",
        "SearchPlatformDocs", "ListWorkflowTemplates", "GetWorkflowTemplate", "GetWorkflow",
        "ListWorkflowExecutions", "GetWorkflowExecution", "ListStyles",
        "DescribePictureDocument", "ListPictureTools", "LookAtPicture",
        "GetDesignSystem", "ListDesignChanges", "ListProjectVersions", "GetProjectChanges",
        "GetOutput", "LookAtOutput", "GetMusicBeats",
        "GetExecutionStatus", "WatchExecution", "GetCreditBalance",
        "GetChangeOptions", "SearchMarketplaceRecipes", "GetMarketplaceRecipe",
        "GetChangeOptions",
        "ListProjectVoices", "SearchVoiceLibrary", "SearchSoundEffects",
        "ListProjectAvatars", "ListAvatarLibrary",
        "ListVideoEvents", "DescribeVideoWindow",
        "ListSocialAccounts", "ListScheduledPosts", "GetPostPerformance",
    };

    /// <summary>
    /// Tools that reach an open world: a model, a vendor, an arbitrary URL, or a run that will.
    /// Everything else operates only on ReelBolt's own data.
    /// </summary>
    public static readonly IReadOnlySet<string> OpenWorldTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "LookAtPicture", "LookAtOutput", "TestInferenceProvider", "WritePostText",
        "ExecuteWorkflow",
        "ApplyRunPictureTool", "ApplyDesignGraphic", "ApplyReviseComposition", "ApplyStepChange",
        "ApplyImportFileFromUrl",
        "SearchVoiceLibrary", "ApplyAddLibraryVoice",
        "ApplyCreateInferenceProvider", "ApplyUpdateInferenceProvider",
        "ApplyExtractObjectFromVideo", "ApplyCreateAvatarVariant", "ApplyGenerateSubjectClip",
    };

    /// <summary>
    /// Writes that are not gated by the in-app approval catalog (so the user is not asked in chat)
    /// but still modify stored state, which the directory annotation must say honestly.
    /// <c>TestInferenceProvider</c> writes the capability's last-test columns. <c>WritePostText</c>
    /// spends credits (and may run web searches), so MCP clients must not treat it as free.
    /// </summary>
    public static readonly IReadOnlySet<string> DestructiveNonGated = new HashSet<string>(StringComparer.Ordinal)
    {
        "TestInferenceProvider", "WritePostText",
    };

    /// <summary>Writes that can safely be repeated with the same arguments.</summary>
    public static readonly IReadOnlySet<string> IdempotentWrites = new HashSet<string>(StringComparer.Ordinal)
    {
        "ApplyUploadFile", "ApplyWorkflow",
    };

    private static readonly IReadOnlyDictionary<string, string> TitleOverrides =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GetStepCatalog"] = "Get the step catalog",
            ["GetMusicBeats"] = "Get music beats",
            ["GetProjectReadiness"] = "Check project readiness",
            ["EstimateWorkflowRun"] = "Estimate a workflow run",
            ["ListAgentsAdmin"] = "List agents (admin)",
            ["GetDecisionCalibration"] = "Get decision calibration",
            ["SearchPlatformDocs"] = "Search the documentation",
            ["ListWorkflowTemplates"] = "List workflow recipes",
            ["GetWorkflowTemplate"] = "Get a workflow recipe",
            ["ListWorkflowExecutions"] = "List workflow runs",
            ["GetWorkflowExecution"] = "Get a workflow run",
            ["ListPictureTools"] = "List picture tools",
            ["GetCreditBalance"] = "Get the credit balance",
            ["GetChangeOptions"] = "Get change options",
            ["SearchMarketplaceRecipes"] = "Search community recipes",
            ["GetMarketplaceRecipe"] = "Get a community recipe",
            ["ListProjectVoices"] = "List the project's voices",
            ["SearchVoiceLibrary"] = "Search the voice library",
            ["SearchSoundEffects"] = "Search the sound effects library",
            ["ListProjectAvatars"] = "List the project's avatars",
            ["ListAvatarLibrary"] = "List the workspace avatar library",
            ["ListVideoEvents"] = "List the moments found in a video",
            ["DescribeVideoWindow"] = "Describe the stretch between two moments",
            ["ListSocialAccounts"] = "List connected social accounts",
            ["ListScheduledPosts"] = "List scheduled and past posts",
            ["GetPostPerformance"] = "Get how published posts performed",
        };

    /// <summary>The annotation for a registered (in-app) tool name.</summary>
    public static AssistantToolAnnotation For(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        bool destructive = AssistantApprovalCatalog.IsGated(toolName) || DestructiveNonGated.Contains(toolName);
        bool readOnly = !destructive
            && (AssistantApprovalCatalog.IsProposal(toolName) || ReadOnlyTools.Contains(toolName));
        return new AssistantToolAnnotation(
            Title(toolName),
            readOnly,
            destructive,
            readOnly || IdempotentWrites.Contains(toolName),
            OpenWorldTools.Contains(toolName));
    }

    /// <summary>A short human title: the approval card's fallback for a gated tool, else the humanized name.</summary>
    public static string Title(string toolName)
    {
        if (TitleOverrides.TryGetValue(toolName, out string? overrideTitle))
            return overrideTitle;
        if (AssistantApprovalCatalog.IsGated(toolName))
            return AssistantApprovalCatalog.Describe(toolName).FallbackTitle;
        return Humanize(toolName);
    }

    /// <summary>
    /// Registered tool names that are neither gated, nor proposals, nor in
    /// <see cref="ReadOnlyTools"/> — the drift guard's failure list.
    /// </summary>
    public static IReadOnlyList<string> Unclassified(IEnumerable<string> registered) =>
    [
        .. registered
            .Where(name => !AssistantApprovalCatalog.IsGated(name)
                && !AssistantApprovalCatalog.IsProposal(name)
                && !DestructiveNonGated.Contains(name)
                && !ReadOnlyTools.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>A gated tool that is also listed as read-only, which would let a mutation skip the prompt.</summary>
    public static IReadOnlyList<string> Contradictions(IEnumerable<string> registered) =>
    [
        .. registered
            .Where(name => (AssistantApprovalCatalog.IsGated(name) || DestructiveNonGated.Contains(name))
                && ReadOnlyTools.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    private static string Humanize(string name)
    {
        var words = new List<string>();
        int start = 0;
        for (int i = 1; i < name.Length; i++)
        {
            bool boundary = char.IsUpper(name[i])
                && (char.IsLower(name[i - 1])
                    || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (!boundary)
                continue;
            words.Add(name[start..i]);
            start = i;
        }
        words.Add(name[start..]);

        string joined = string.Join(' ', words).ToLowerInvariant();
        return char.ToUpperInvariant(joined[0]) + joined[1..];
    }
}
