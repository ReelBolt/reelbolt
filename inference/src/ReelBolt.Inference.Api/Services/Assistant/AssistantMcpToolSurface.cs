using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>One tool as the MCP catalog publishes it.</summary>
public sealed record McpToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    AssistantToolAnnotation Annotation);

/// <summary>
/// How one MCP tool call runs: one underlying assistant tool, or a Propose/Apply pair collapsed
/// into one call.
/// </summary>
/// <param name="Name">The MCP name that was called (what <c>mcp_tool_calls</c> records).</param>
/// <param name="ChargeToolName">The real tool whose cost classification pre-claims the call.</param>
/// <param name="AdminToolName">The real tool checked against the admin opt-in (null for a non-admin tool).</param>
/// <param name="Steps">The tool(s) to invoke, in order.</param>
/// <param name="MintsAndConsumesToken">
/// True when the first step returns a <c>confirmationToken</c> that the second step consumes in the
/// same server call. The token is never returned to the MCP client.
/// </param>
public sealed record McpToolCallPlan(
    string Name,
    string ChargeToolName,
    string? AdminToolName,
    IReadOnlyList<AIFunction> Steps,
    bool MintsAndConsumesToken);

/// <summary>
/// The MCP-only projection of the assistant tool set.
/// </summary>
/// <remarks>
/// <para>
/// <b>The in-app assistant is untouched.</b> It keeps every <c>Propose*</c> / <c>Apply*</c> pair,
/// its approval cards and auto mode. This class changes only what the MCP catalog lists and how
/// an MCP call is routed: MCP clients already ask their own user before a destructive tool, so the
/// second in-product approval layer is removed on that surface.
/// </para>
/// <para>
/// <b>A collapsed call still mints and verifies the HMAC token.</b> The controller invokes
/// <c>Propose*</c>, reads <c>confirmationToken</c> from its JSON, and invokes <c>Apply*</c> with
/// that token in the same request. The token never leaves the server; the Apply body still
/// re-checks authority per call, and every refusal (stale token, plan gate, authority, the
/// <c>ApplyDecideDesignChange(approve)</c> MCP rule) is returned unchanged. A proposal that
/// refuses is returned as-is and nothing is applied.
/// </para>
/// <para>
/// <b>Upload is a protocol, not an approval.</b> <c>ProposeUploadFile</c> hands out a presigned
/// PUT and <c>ApplyUploadFile</c> records the bytes the client uploaded between the two calls;
/// collapsing them would make uploads impossible. They are published as <c>BeginUpload</c>
/// (read-only) and <c>CompleteUpload</c> (destructive).
/// </para>
/// </remarks>
public static class AssistantMcpToolSurface
{
    /// <summary>One collapsed pair: the MCP name plus the two real tools it runs.</summary>
    public sealed record CollapsedPair(string Name, string ProposeName, string ApplyName, string Description);

    private static readonly JsonElement EmptyObjectSchema =
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

    /// <summary>Every collapsed pair, in catalog order.</summary>
    public static readonly IReadOnlyList<CollapsedPair> Collapsed =
    [
        new("CreateFromTemplate", "ProposeCreateFromTemplate", "ApplyCreateFromTemplate",
            "Creates a workflow from a built-in recipe: instantiates the recipe, binds the run brief, applies the optional closed patches, lints the result and saves it. Returns the new workflow's id and name, the cost class, what the project still needs before a run, and a hint. It does not start a run."),
        new("SaveWorkflow", "ProposeWorkflow", "ApplyWorkflow",
            "Creates a workflow, or replaces an existing workflow's steps, from a list of step definitions. Returns the workflow's id and name, the project and the step count; an edit whose stored steps already match answers alreadyApplied."),
        new("CreateProject", "ProposeCreateProject", "ApplyCreateProject",
            "Creates a project in the active workspace, optionally with tags and a folder given by name, and returns its id and name."),
        new("UpdateProject", "ProposeUpdateProject", "ApplyUpdateProject",
            "Renames a project, changes its description, and optionally sets its tags and folder by name, and returns the updated project."),
        new("DeleteProject", "ProposeDeleteProject", "ApplyDeleteProject",
            "Deletes a project and everything in it, and returns the deleted project's id and name."),
        new("SavePronunciation", "ProposeSavePronunciation", "ApplySavePronunciation",
            "Saves a pronunciation entry (term and spelling) for the project, so narration and voiceover say the term as written."),
        new("AddSoundEffect", "ProposeAddSoundEffect", "ApplyAddSoundEffect",
            "Copies one built-in library sound (a name from SearchSoundEffects), or with starterSet=true a starter set of about 24 varied sounds, into the project as ordinary audio files. Free: no credits, not counted against storage. Adding a sound already in the project changes nothing."),
        new("WriteProjectFile", "ProposeWriteProjectFile", "ApplyWriteProjectFile",
            "Creates or replaces a text file in a project and returns the file's id, name, path and size."),
        new("EditProjectFile", "ProposeEditProjectFile", "ApplyEditProjectFile",
            "Replaces an exact span of an existing project file in place and returns the file's id, size and the number of lines changed."),
        new("InsertProjectFileLines", "ProposeInsertProjectFileLines", "ApplyInsertProjectFileLines",
            "Inserts lines at a position in an existing project file and returns the file's id, size and the new line count."),
        new("DeleteProjectFile", "ProposeDeleteProjectFile", "ApplyDeleteProjectFile",
            "Deletes one project file and reports the file's name and id."),
        new("DeleteWorkflow", "ProposeDeleteWorkflow", "ApplyDeleteWorkflow",
            "Deletes a workflow that has no run in progress and reports its name."),
        new("StopExecution", "ProposeStopExecution", "ApplyStopExecution",
            "Stops a queued or running workflow execution. Work already done stays charged; the run ends cancelled."),
        new("CreateInferenceProvider", "ProposeCreateInferenceProvider", "ApplyCreateInferenceProvider",
            "Adds an AI service connection (provider) to the workspace, with its models and capabilities, and returns the provider's id."),
        new("UpdateInferenceProvider", "ProposeUpdateInferenceProvider", "ApplyUpdateInferenceProvider",
            "Updates an AI service connection's name, endpoint, key, models or capabilities."),
        new("DeleteInferenceProvider", "ProposeDeleteInferenceProvider", "ApplyDeleteInferenceProvider",
            "Deletes an AI service connection and the models and capabilities under it."),
        new("AssignAgentSkills", "ProposeAssignAgentSkills", "ApplyAssignAgentSkills",
            "Replaces the skills assigned to a custom agent and returns the agent's id and its assigned skill names."),
        new("SaveStyle", "ProposeSaveStyle", "ApplySaveStyle",
            "Creates or replaces a saved editing style and returns its id and name."),
        new("AddTextLayer", "ProposeAddTextLayer", "ApplyAddTextLayer",
            "Adds a text layer to an open picture document and returns a plain-language summary and the new layer's id."),
        new("EditPictureLayers", "ProposeEditPictureLayers", "ApplyEditPictureLayers",
            "Changes an open picture document's layers (position, size, rotation, opacity, visibility, lock, blend mode, label or text) and returns a plain-language summary and the changed layer ids."),
        new("DeletePictureLayers", "ProposeDeletePictureLayers", "ApplyDeletePictureLayers",
            "Deletes one or more layers from an open picture document and returns a plain-language summary and the deleted layer ids."),
        new("ChangePictureCanvas", "ProposeChangePictureCanvas", "ApplyChangePictureCanvas",
            "Changes an open picture document's canvas size or background and returns a plain-language summary."),
        new("RunPictureTool", "ProposeRunPictureTool", "ApplyRunPictureTool",
            "Runs one paid AI picture tool (erase, fill, expand, replace background, relight, upscale, edit, generate or remove background) on an open picture document and returns a plain-language summary and the new layer ids. The proposal half prices the tool; the call spends credits."),
        new("DesignGraphic", "ProposeDesignGraphic", "ApplyDesignGraphic",
            "Queues a designed graphic for an open picture document, renders it as a still on a transparent canvas and returns the request id; the result becomes a generated layer."),
        new("SaveDesignSystem", "ProposeSaveDesignSystem", "ApplySaveDesignSystem",
            "Saves validated design-system arguments as a draft version for a project and returns the version id and number."),
        new("DecideDesignChange", "ProposeDecideDesignChange", "ApplyDecideDesignChange",
            "Approves or rejects a pending design-system change proposal. Approving a brand change is allowed only in the in-app chat card or on the Design system page; over MCP it is refused. Rejecting is allowed over MCP."),
        new("SaveOutputAsFile", "ProposeSaveOutputAsFile", "ApplySaveOutputAsFile",
            "Copies a render or output into the project's files and returns the new file's id, name, path and size."),
        new("ImportFileFromUrl", "ProposeImportFileFromUrl", "ApplyImportFileFromUrl",
            "Downloads one public https file into a project and returns the new file's id, name, path, size and MIME type."),
        new("ReviseComposition", "ProposeReviseComposition", "ApplyReviseComposition",
            "Queues a revision run that changes a finished run's film and returns the revision id and the new run's id; the run is charged by the workflow credit gate."),
        new("StepChange", "ProposeStepChange", "ApplyStepChange",
            "Queues a new linked run that redoes one step of a finished run and returns the change request id and the new run's id; the run is charged by the workflow credit gate."),
        new("PatchWorkflow", "ProposePatchWorkflow", "ApplyPatchWorkflow",
            "Applies a list of edits to a workflow's steps in place (set a step config, rename a step, set an allowed step field) and returns the workflow's id and name."),
        new("CloneWorkflow", "ProposeCloneWorkflow", "ApplyCloneWorkflow",
            "Copies a workflow's steps into a new workflow in the same or another project and returns the new workflow's id and name."),
        new("CreatePictureDocument", "ProposeCreatePictureDocument", "ApplyCreatePictureDocument",
            "Creates a picture document from a stored image or a blank canvas and returns the document's id, name, width and height."),
        new("GreenScreenInsert", "ProposeGreenScreenInsert", "ApplyGreenScreenInsert",
            "Creates a workflow from the green-screen insert recipe with the chosen content pinned into the tracked screens. It saves the workflow and starts no run."),
        new("ObjectEffect", "ProposeObjectEffect", "ApplyObjectEffect",
            "Finds an object (a face, a license plate, a product) in a video clip on an editor timeline by name and puts an effect on it: blur, pixelate, highlight, spotlight, colorpop or outline. It queues an object search that spends credits (free when the same search was done before) and the finished effect is added to the timeline when the search completes. It needs the project's one-time consent to send video frames to the object-detection service, which only the user can give in the editor."),
        new("CopyMarketplaceRecipe", "ProposeCopyMarketplaceRecipe", "ApplyCopyMarketplaceRecipe",
            "Copies a community recipe (by name or by the handle SearchMarketplaceRecipes returned) into the active project as an ordinary workflow. It saves the workflow, starts no run, charges nothing and leaves egress consent off."),
        new("CleanAudio", "ProposeCleanAudio", "ApplyCleanAudio",
            "Cleans the audio of a project file or a finished render (hum, wind, hiss, echo or speech, all free and local) and starts the run without waiting; returns the execution id. The AI cleanups need the customer's OK in the editor and are refused here."),
        new("EnhanceVideo", "ProposeEnhanceVideo", "ApplyEnhanceVideo",
            "Enhances one video of a project file or a finished render: upscale, smooth slow motion, denoise or motion blur. The local operations are free; the AI (hosted) upscale and slow motion are paid and run only under the project's recorded consent, which only the user can give in the editor. It starts the run without waiting and returns the execution id."),
        new("AddLibraryVoice", "ProposeAddLibraryVoice", "ApplyAddLibraryVoice",
            "Adds one public voice of the speech provider's voice library (a voiceId from SearchVoiceLibrary) to the project's voices, optionally under a displayName, and returns the new voice's id, which a run's brief takes as voice.projectVoiceId. No consent is needed for a library voice; an id that is not a public library voice is refused, and a voice the project already has answers alreadyAdded with its id. Cloned voices can only be made by the user in the project's Voices tab."),
        new("SetMediaOverlay", "ProposeSetMediaOverlay", "ApplySetMediaOverlay",
            "Shows a project picture or video over the film at a chosen moment, form, corner, size, opacity and sound. It saves the change to a VideoCompile step and starts no run."),
        new("SaveProjectVersion", "ProposeSaveProjectVersion", "ApplySaveProjectVersion",
            "Saves the project's current state as a named version (a restore point) and returns the version's number and name."),
        new("CreateObjectAvatar", "ProposeCreateObjectAvatar", "ApplyCreateObjectAvatar",
            "Makes an OBJECT avatar (a product, a vehicle, a building: not a person) from pictures and short videos of the project named by path, and returns its name, kind and counts. Free. It needs the user's object statement: pass statement (the text verbatim, as the user read and accepted it) and locale; a call without it is answered with the text to show. Every picture is checked for people first. A person's avatar is made only in the app."),
        new("ExtractObjectFromVideo", "ProposeExtractObjectFromVideo", "ApplyExtractObjectFromVideo",
            "Pulls an object out of a project video (by path) into a new OBJECT avatar, in the background: up to 6 frames, cut to the object when an object-finding model is set up and sending the frames is allowed (billed), else whole frames (free). It needs the user's object statement (statement verbatim and locale). ListProjectAvatars shows it under builds until it is ready."),
        new("CreateAvatarVariant", "ProposeCreateAvatarVariant", "ApplyCreateAvatarVariant",
            "Makes a variant of an OBJECT avatar (by name): from other project files (free), or by the picture model changing its pictures, optionally its videos too (paid, in the background). It needs the user's object statement (statement verbatim and locale). A person's variants are made only in the app."),
        new("ImportLibraryAvatar", "ProposeImportLibraryAvatar", "ApplyImportLibraryAvatar",
            "Brings one workspace avatar library item (a person or an object, by name) into the project as a linked import whose files are copied into the project, and returns its name and kind. Free. A project that already holds it answers alreadyImported."),
        new("GenerateSubjectClip", "ProposeGenerateSubjectClip", "ApplyGenerateSubjectClip",
            "Starts a generated video clip that shows one to three avatars or objects of the project (by name, at most one person) and returns the run's executionId without waiting; each take is added to the project's files as a video. Paid: the clip is charged by the workflow credit gate. The avatars' pictures and videos are sent to the AI video service."),
        new("RestoreProjectVersion", "ProposeRestoreProjectVersion", "ApplyRestoreProjectVersion",
            "Restores a saved project version by number: the named items (files, workflows, timelines, design system, pronunciation, project details) with scope \"item\". Restoring the whole project (scope \"project\") is refused over MCP and needs the in-app card or the History page. A safety version of the current state is saved first. Returns what was restored, removed or skipped, by name."),
    ];

    /// <summary>
    /// In-app tools that are NOT on the MCP surface at all: posting in the user's name on a public channel needs the user's own
    /// click on the in-app card (the always-ask class), so over MCP the social pairs are neither listed nor callable, and the
    /// bodies refuse an MCP initiator themselves as well. Reading accounts, the queue and performance stays published.
    /// </summary>
    public static readonly IReadOnlySet<string> NotOverMcp = new HashSet<string>(StringComparer.Ordinal)
    {
        "ProposeSchedulePost", "ApplySchedulePost", "ProposeCancelScheduledPost", "ApplyCancelScheduledPost",
    };

    /// <summary>The upload protocol steps, published under names that say what they are.</summary>
    public static readonly CollapsedPair BeginUpload = new("BeginUpload", "ProposeUploadFile", "",
        "Prepares an upload and returns where to PUT the bytes: one presigned URL, or a part list for a large file. Nothing is recorded until CompleteUpload.");

    public static readonly CollapsedPair CompleteUpload = new("CompleteUpload", "", "ApplyUploadFile",
        "Records a file whose bytes were uploaded to the URL(s) BeginUpload returned. Repeating the call returns the same file.");

    private static readonly IReadOnlyDictionary<string, CollapsedPair> ByMcpName =
        Collapsed.Append(BeginUpload).Append(CompleteUpload)
            .ToDictionary(pair => pair.Name, StringComparer.Ordinal);

    /// <summary>
    /// The in-app pair names mapped to their MCP names, for descriptions of tools that survive
    /// uncollapsed. A read tool often mentions a sibling pair by its in-app name ("refused by
    /// ProposeWorkflow"); over MCP that name does not exist, so the reference is rewritten to the
    /// name the MCP client can actually call.
    /// </summary>
    private static readonly IReadOnlyList<(string From, string To)> PairNames =
    [
        .. Collapsed.Append(BeginUpload).Append(CompleteUpload)
            .SelectMany(pair => new (string, string)[]
            {
                (pair.ProposeName, pair.Name),
                (pair.ApplyName, pair.Name),
            })
            .Where(pair => pair.Item1.Length > 0),
    ];

    private static string McpDescription(string description)
    {
        foreach ((string from, string to) in PairNames)
            description = description.Replace(from, to, StringComparison.Ordinal);
        return description;
    }

    private static readonly IReadOnlyDictionary<string, string> TitleOverrides =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SaveWorkflow"] = "Save a workflow",
            ["SavePronunciation"] = "Save a pronunciation",
            ["AddSoundEffect"] = "Add a sound effect",
            ["SaveOutputAsFile"] = "Save a render to files",
            ["SaveDesignSystem"] = "Save the design system",
            ["SaveProjectVersion"] = "Save a project version",
            ["RestoreProjectVersion"] = "Restore a project version",
            ["StepChange"] = "Change steps of a run",
            ["PatchWorkflow"] = "Patch a workflow",
            ["CloneWorkflow"] = "Copy a workflow",
            ["GreenScreenInsert"] = "Set up a green-screen insert",
            ["ObjectEffect"] = "Put an effect on an object in a clip",
            ["EnhanceVideo"] = "Enhance a video",
            ["SetMediaOverlay"] = "Show a picture on the video",
            ["CopyMarketplaceRecipe"] = "Copy a community recipe",
            ["CleanAudio"] = "Clean the audio",
            ["AddLibraryVoice"] = "Add a library voice",
            ["CreateObjectAvatar"] = "Make an object avatar",
            ["ExtractObjectFromVideo"] = "Take an object out of a video",
            ["CreateAvatarVariant"] = "Make a variant of an object",
            ["ImportLibraryAvatar"] = "Import an avatar from the library",
            ["GenerateSubjectClip"] = "Make a clip with avatars",
            ["RunPictureTool"] = "Run a picture tool",
            ["ImportFileFromUrl"] = "Import a file from a URL",
            ["StopExecution"] = "Stop a run",
            ["BeginUpload"] = "Begin an upload",
            ["CompleteUpload"] = "Complete an upload",
        };

    /// <summary>
    /// Projects the registered assistant tools into MCP declarations. <paramref name="hiddenAdminTools"/>
    /// is the admin-gated subset when the caller's opt-in is off; a collapsed entry is hidden when its
    /// Apply half is in that set.
    /// </summary>
    public static IReadOnlyList<McpToolDescriptor> Project(
        IReadOnlyList<AIFunction> tools,
        IReadOnlySet<string> hiddenAdminTools)
    {
        Dictionary<string, AIFunction> byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var result = new List<McpToolDescriptor>();
        HashSet<string> emitted = new(StringComparer.Ordinal);

        foreach (AIFunction tool in tools)
        {
            if (NotOverMcp.Contains(tool.Name))
                continue;

            // The pair members are represented by one collapsed entry at the Apply's position.
            if (tool.Name.StartsWith("Apply", StringComparison.Ordinal)
                && ByMcpName.Values.FirstOrDefault(p => p.ApplyName == tool.Name) is { } pair)
            {
                if (!emitted.Add(pair.Name) || hiddenAdminTools.Contains(pair.ApplyName))
                    continue;

                if (pair.ProposeName.Length == 0)
                    result.Add(CollapsedDescriptor(pair, propose: null, tool));
                else if (byName.TryGetValue(pair.ProposeName, out AIFunction? proposition))
                    result.Add(CollapsedDescriptor(pair, proposition, tool));
                // A pair with only its Apply half registered is not published: there would be no
                // server-side proposal to mint the token the Apply body verifies.
                continue;
            }

            if (string.Equals(tool.Name, BeginUpload.ProposeName, StringComparison.Ordinal))
            {
                if (emitted.Add(BeginUpload.Name))
                    result.Add(CollapsedDescriptor(BeginUpload, tool, apply: null));
                continue;
            }

            if (string.Equals(tool.Name, CompleteUpload.ApplyName, StringComparison.Ordinal))
            {
                if (emitted.Add(CompleteUpload.Name))
                    result.Add(CollapsedDescriptor(CompleteUpload, propose: null, tool));
                continue;
            }

            if (tool.Name.StartsWith("Propose", StringComparison.Ordinal))
                continue; // represented by its collapsed Apply entry

            if (hiddenAdminTools.Contains(tool.Name))
                continue;

            result.Add(new McpToolDescriptor(
                tool.Name,
                McpDescription(tool.Description ?? string.Empty),
                InputSchemaOf(tool),
                AssistantToolAnnotations.For(tool.Name)));
        }

        return result;
    }

    /// <summary>
    /// Resolves an MCP name to its execution plan. Returns false for an unknown name and for the
    /// retired <c>Propose*</c> / <c>Apply*</c> names: the MCP surface exposes the collapsed tools only.
    /// </summary>
    public static bool TryResolve(string mcpName, IReadOnlyList<AIFunction> tools, out McpToolCallPlan plan)
    {
        Dictionary<string, AIFunction> byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        if (NotOverMcp.Contains(mcpName))
        {
            plan = null!;
            return false;
        }

        if (ByMcpName.TryGetValue(mcpName, out CollapsedPair? pair))
        {
            if (pair.ProposeName.Length == 0 || pair.ApplyName.Length == 0)
            {
                string singleName = pair.ProposeName.Length > 0 ? pair.ProposeName : pair.ApplyName;
                if (!byName.TryGetValue(singleName, out AIFunction? single))
                {
                    plan = null!;
                    return false;
                }

                plan = new McpToolCallPlan(mcpName, single.Name, null, [single], MintsAndConsumesToken: false);
                return true;
            }

            if (byName.TryGetValue(pair.ProposeName, out AIFunction? propose)
                && byName.TryGetValue(pair.ApplyName, out AIFunction? apply))
            {
                plan = new McpToolCallPlan(mcpName, apply.Name, apply.Name, [propose, apply], MintsAndConsumesToken: true);
                return true;
            }

            plan = null!;
            return false;
        }

        if (mcpName.StartsWith("Propose", StringComparison.Ordinal)
            || mcpName.StartsWith("Apply", StringComparison.Ordinal))
        {
            plan = null!;
            return false;
        }

        if (byName.TryGetValue(mcpName, out AIFunction? tool))
        {
            plan = new McpToolCallPlan(mcpName, tool.Name, tool.Name, [tool], MintsAndConsumesToken: false);
            return true;
        }

        plan = null!;
        return false;
    }

    /// <summary>
    /// Runs a plan. A collapsed pair runs Propose then Apply in this one call: the proposal's
    /// <c>confirmationToken</c> is read from its JSON and handed to Apply; it never reaches the
    /// client. A proposal that refuses (an error, a lint finding, an authority refusal) returns its
    /// own JSON and nothing is applied.
    /// </summary>
    public static async Task<object?> InvokeAsync(
        McpToolCallPlan plan,
        AIFunctionArguments arguments,
        CancellationToken ct)
    {
        if (!plan.MintsAndConsumesToken)
            return await plan.Steps[0].InvokeAsync(arguments, ct);

        object? proposed = await plan.Steps[0].InvokeAsync(arguments, ct);
        if (!TryReadConfirmationToken(TextOf(proposed), out string? token))
            return proposed;

        var applyArguments = new AIFunctionArguments();
        foreach ((string name, object? value) in arguments)
            applyArguments[name] = value;
        // The server-minted token wins even if a caller tried to supply one.
        applyArguments["confirmationToken"] = token;
        return await plan.Steps[1].InvokeAsync(applyArguments, ct);
    }

    /// <summary>Reads <c>confirmationToken</c> from a proposal's JSON result; false for a refusal or malformed JSON.</summary>
    public static bool TryReadConfirmationToken(string text, out string? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("confirmationToken", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } found)
            {
                token = found;
                return true;
            }
        }
        catch (JsonException)
        {
            // A plain-string result (a refusal) has no token; return it unchanged.
        }

        return false;
    }

    private static string TextOf(object? raw) => raw switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(raw)
    };

    /// <summary>
    /// The app-field sentence of a collapsed entry (docs/design/app-inputs.md 29, the L8 narrowing), appended only when the real
    /// tool takes the argument.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (Func<AIFunction, bool> Takes, string Note)> AppInputNotes =
        new Dictionary<string, (Func<AIFunction, bool>, string)>(StringComparer.Ordinal)
        {
            ["SaveWorkflow"] = (t => HasProperty(t, "appManifest"),
                " appManifest (optional) saves the app page with the steps: its own fields (types, labels, defaults, choices, models) and the request template; a step setting reads a field through {{input.<id>}} or its appInputs entry."),
            ["PatchWorkflow"] = (t => (t.Description ?? string.Empty).Contains("setAppInput", StringComparison.Ordinal),
                " The edits also manage the app's own fields: setAppInput, removeAppInput, bindAppInput (a step setting reads a field) and setAppPage (title, order, request template)."),
            ["StepChange"] = (t => HasProperty(t, "inputs"),
                " For a run of an app with its own fields it also takes inputs (new field values) and brief (run settings: language, length, voice, files, notes), alone or together with changes to steps in one request; only the steps that use what changed are redone."),
            ["CreateFromTemplate"] = (t => HasProperty(t, "inputs"),
                " brief.inputs sets the recipe's own fields; the values become the new app page's defaults."),
            // The one media rule (docs/design/avatar-objects.md): over MCP a per-call allowMediaEgress counts only while the user
            // has let connected tools send their media.
            ["ExtractObjectFromVideo"] = (t => HasProperty(t, "allowMediaEgress"), McpMediaNote),
            ["CreateAvatarVariant"] = (t => HasProperty(t, "allowMediaEgress"), McpMediaNote),
            ["GenerateSubjectClip"] = (t => HasProperty(t, "allowMediaEgress"), McpMediaNote),
            ["RunPictureTool"] = (t => HasProperty(t, "allowMediaEgress"),
                " With subjects (names of avatars or objects to show) the call may take allowMediaEgress: true when the project has not recorded its agreement to send their pictures; it counts only while the user has let connected tools send their media (Settings > Assistant)."),
        };

    private const string McpMediaNote =
        " allowMediaEgress: true allows sending the avatars' media for this one call when the project has not recorded its agreement; it counts only while the user has let connected tools send their media (Settings > Assistant), and records nothing.";

    private static bool HasProperty(AIFunction tool, string name) =>
        tool.JsonSchema.ValueKind == JsonValueKind.Object
        && tool.JsonSchema.GetRawText().Contains($"\"{name}\"", StringComparison.Ordinal);

    private static McpToolDescriptor CollapsedDescriptor(
        CollapsedPair pair, AIFunction? propose, AIFunction? apply)
    {
        AIFunction schemaSource = propose ?? apply!;
        AssistantToolAnnotation annotation = apply is null
            ? AssistantToolAnnotations.For(propose!.Name)
            : AssistantToolAnnotations.For(apply.Name);
        string description = AppInputNotes.TryGetValue(pair.Name, out (Func<AIFunction, bool> Takes, string Note) note) && note.Takes(schemaSource)
            ? pair.Description + note.Note
            : pair.Description;
        return new McpToolDescriptor(
            pair.Name,
            description,
            WithoutConfirmationToken(InputSchemaOf(schemaSource)),
            new AssistantToolAnnotation(
                Title(pair.Name),
                ReadOnlyHint: apply is null && annotation.ReadOnlyHint,
                DestructiveHint: apply is not null,
                IdempotentHint: annotation.IdempotentHint,
                OpenWorldHint: annotation.OpenWorldHint));
    }

    private static string Title(string mcpName) =>
        TitleOverrides.TryGetValue(mcpName, out string? title) ? title : AssistantToolAnnotations.Title(mcpName);

    private static JsonElement InputSchemaOf(AIFunction tool) =>
        tool.JsonSchema.ValueKind == JsonValueKind.Undefined ? EmptyObjectSchema : tool.JsonSchema;

    /// <summary>Removes the pair's token from a proposal schema if one were ever declared there.</summary>
    private static JsonElement WithoutConfirmationToken(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out JsonElement properties)
            || !properties.TryGetProperty("confirmationToken", out _))
            return schema;

        JsonNode? parsed = JsonNode.Parse(schema.GetRawText());
        if (parsed is not JsonObject root || root["properties"] is not JsonObject props)
            return schema;
        props.Remove("confirmationToken");
        if (root["required"] is JsonArray required)
        {
            for (int i = required.Count - 1; i >= 0; i--)
            {
                if (required[i]?.GetValue<string>() == "confirmationToken")
                    required.RemoveAt(i);
            }
        }
        return JsonSerializer.SerializeToElement(root);
    }
}
