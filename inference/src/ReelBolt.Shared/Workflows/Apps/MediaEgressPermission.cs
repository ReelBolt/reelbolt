using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>How much a step needs the permission to send the customer's media to an outside AI service.</summary>
public enum MediaEgressNeedLevel
{
    /// <summary>The step sends nothing.</summary>
    None,

    /// <summary>
    /// The step may send media, depending on what the run decides (a planned shot that names a frame of the footage, an
    /// avatar a run may choose): not known before the run, so the field is offered but not required. Without it such a
    /// plan still stops the step <c>EGRESS_REFUSED</c>, before anything is bought.
    /// </summary>
    Optional,

    /// <summary>Without the permission the step fails <c>EGRESS_REFUSED</c>.</summary>
    Required
}

/// <summary>What is known about the run brief's avatar when a need is worked out.</summary>
public enum MediaEgressBriefAvatar
{
    /// <summary>No avatar comes from the brief.</summary>
    None,

    /// <summary>The app offers an avatar picker, so a run may choose one.</summary>
    Offered,

    /// <summary>This run chose an avatar.</summary>
    Chosen
}

/// <summary>
/// The need of one step. <see cref="Reason"/> is a closed code (<see cref="MediaEgressNeed"/> constants, the WL015
/// variants); <see cref="StoredOn"/> is the step's own saved <c>allowSourceMediaEgress</c>.
/// </summary>
public sealed record MediaEgressStepNeed(
    int StepOrder, StepType StepType, string? StepName, MediaEgressNeedLevel Level, string? Reason, bool StoredOn)
{
    /// <summary>True when the step needs or may use the permission and its saved switch is off.</summary>
    public bool Pending => Level != MediaEgressNeedLevel.None && !StoredOn;
}

/// <summary>
/// The ONE definition of "this step sends the customer's media to an outside AI service" (docs/design/app-inputs.md
/// "Permission to send media as an app field"), shared by the pre-run linter (WL015), the app's derived permission
/// field, the run gate and the binder. Pure: it reads the step's own config and nothing else.
/// </summary>
public static class MediaEgressNeed
{
    /// <summary>The config member every classified step type carries (camelCase on the wire).</summary>
    public const string ConfigMember = "allowSourceMediaEgress";

    public const string TracksAnObjectByName = "tracksAnObjectByName";
    public const string StartsFromFrame = "startsFromFrame";
    public const string ShowsAnAvatar = "showsAnAvatar";
    public const string PlannedFrames = "plannedFrames";

    /// <summary>
    /// A plan-sourced step that may also show planned subjects (<c>PlannedSubjects</c> = Auto, the default): the same
    /// permission lets the planner pick the project's avatars of people and objects and send their pictures to the model
    /// provider, so the reason says so. A step with <c>plannedSubjects</c> off keeps <see cref="PlannedFrames"/>.
    /// </summary>
    public const string PlannedFramesAndSubjects = "plannedFramesAndSubjects";
    public const string EnhancesVideoWithAi = "enhancesVideoWithAi";
    public const string CleansAudioWithAi = "cleansAudioWithAi";
    public const string EditsPictureWithAi = "editsPictureWithAi";

    /// <summary>
    /// Every step type whose config carries <c>AllowSourceMediaEgress</c>. A drift test fails when a config type gains
    /// the member without an arm in <see cref="For"/>.
    /// </summary>
    public static readonly IReadOnlyList<StepType> StepTypes =
        [StepType.VideoAnalyze, StepType.VideoGenerate, StepType.AudioCleanup, StepType.VideoEnhance, StepType.ImageGenerate];

    /// <summary>The need of <paramref name="step"/>, given what is known about the brief's avatar.</summary>
    public static MediaEgressStepNeed For(AppBindingStep step, MediaEgressBriefAvatar avatar = MediaEgressBriefAvatar.None)
    {
        JsonObject? config = Parse(step.ConfigJson);
        bool storedOn = Bool(config, ConfigMember);
        (MediaEgressNeedLevel level, string? reason) = step.StepType switch
        {
            StepType.VideoAnalyze => Analyze(config),
            StepType.VideoGenerate => Generate(config, avatar),
            StepType.VideoEnhance => EnumName(Child(config, "engine"), ["Local", "Hosted"]) == "Hosted"
                ? (MediaEgressNeedLevel.Required, EnhancesVideoWithAi)
                : (MediaEgressNeedLevel.None, null),
            StepType.AudioCleanup => EnumName(Child(config, "target"), ["VoiceBasic", "Hum", "Wind", "Hiss", "Echo", "IsolateVoice", "RemoveMusic"]) is "IsolateVoice" or "RemoveMusic"
                ? (MediaEgressNeedLevel.Required, CleansAudioWithAi)
                : (MediaEgressNeedLevel.None, null),
            StepType.ImageGenerate => EnumName(Child(config, "mode"), ["Generate", "Edit"]) == "Edit"
                ? (MediaEgressNeedLevel.Required, EditsPictureWithAi)
                : (MediaEgressNeedLevel.None, null),
            _ => (MediaEgressNeedLevel.None, (string?)null)
        };
        return new MediaEgressStepNeed(step.StepOrder, step.StepType, step.Name, level, reason, storedOn);
    }

    /// <summary>What the step sends, in plain words (the tail of the WL015 sentence and of an approval card line).</summary>
    public static string Sends(string? reason) => reason switch
    {
        TracksAnObjectByName => "tracks an object by name",
        StartsFromFrame => "starts from a frame of your footage",
        ShowsAnAvatar => "shows an avatar, so it sends that person's pictures, videos and voice samples",
        PlannedFrames => "may start a clip from a frame of your footage",
        PlannedFramesAndSubjects => "may start a clip from a frame of your footage, and may show the people and objects of your project's avatars, so it can send their pictures to the outside AI company that makes the video",
        EnhancesVideoWithAi => "sends the video to an outside AI service to enhance it",
        CleansAudioWithAi => "sends the audio to an outside AI service to clean it",
        EditsPictureWithAi => "sends the picture to an outside AI service to edit it",
        _ => "sends your media to an outside AI service"
    };

    /// <summary>The short form used in the field's description: "shows an avatar", "finds the object".</summary>
    public static string ShortReason(string? reason) => reason switch
    {
        TracksAnObjectByName => "finds the object",
        StartsFromFrame => "starts from a frame of your footage",
        ShowsAnAvatar => "shows an avatar",
        PlannedFrames => "can start a clip from your footage",
        PlannedFramesAndSubjects => "can start a clip from your footage or show your avatars",
        EnhancesVideoWithAi => "enhances the video",
        CleansAudioWithAi => "cleans the audio",
        EditsPictureWithAi => "edits the picture",
        _ => "sends your media"
    };

    private static (MediaEgressNeedLevel, string?) Analyze(JsonObject? config) =>
        Child(config, "trackObjects") is JsonArray targets
        && targets.OfType<JsonObject>().Any(t => EnumName(Child(t, "method"), ["Feature", "Segmentation"]) == "Segmentation")
            ? (MediaEgressNeedLevel.Required, TracksAnObjectByName)
            : (MediaEgressNeedLevel.None, null);

    private static (MediaEgressNeedLevel, string?) Generate(JsonObject? config, MediaEgressBriefAvatar avatar)
    {
        if (config is null)
            return (MediaEgressNeedLevel.None, null);
        bool frames = Child(config, "firstFrame") is JsonObject || Child(config, "lastFrame") is JsonObject
            || HasGuid(config, "firstFrameProjectFileId") || HasGuid(config, "lastFrameProjectFileId");
        if (frames)
            return (MediaEgressNeedLevel.Required, StartsFromFrame);
        if (HasGuid(config, "avatarId"))
            return (MediaEgressNeedLevel.Required, ShowsAnAvatar);

        // Several subjects per clip (docs/design/avatar-objects.md): a step that names its own subjects sends the media
        // of each of them, people and objects alike.
        if (Child(config, "subjects") is JsonArray { Count: > 0 })
            return (MediaEgressNeedLevel.Required, ShowsAnAvatar);

        bool planSourced = EnumName(Child(config, "planSource"), ["Inline", "StepRef"]) == "StepRef";
        if (planSourced)
        {
            // With planned subjects on (Auto, the default) the planner may also pick the project's avatars, and their
            // pictures go to the model provider under the same permission: the reason says so.
            bool subjectsOff = EnumName(Child(config, "plannedSubjects"), ["Auto", "Off"]) == "Off";
            return (MediaEgressNeedLevel.Optional, subjectsOff ? PlannedFrames : PlannedFramesAndSubjects);
        }

        // An inline step that names no avatar and no frame takes the run brief's avatar (RunBriefOverrides): it sends that
        // person's media only for a run that chose one (a talking head included; with no avatar it has nothing to send).
        if (avatar == MediaEgressBriefAvatar.Chosen)
            return (MediaEgressNeedLevel.Required, ShowsAnAvatar);
        return avatar == MediaEgressBriefAvatar.Offered
            ? (MediaEgressNeedLevel.Optional, ShowsAnAvatar)
            : (MediaEgressNeedLevel.None, null);
    }

    internal static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonNode? Child(JsonObject? node, string name)
    {
        if (node is null)
            return null;
        foreach ((string key, JsonNode? value) in node)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    internal static bool Bool(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    private static bool HasGuid(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.GetValueKind() == JsonValueKind.String
        && Guid.TryParse(value.GetValue<string>(), out Guid id) && id != Guid.Empty;

    /// <summary>An enum member written by name or by number; null for anything else.</summary>
    private static string? EnumName(JsonNode? node, string[] names)
    {
        if (node is not JsonValue value)
            return null;
        if (value.GetValueKind() == JsonValueKind.String)
            return names.FirstOrDefault(n => string.Equals(n, value.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
        return value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out int index) && index >= 0 && index < names.Length
            ? names[index]
            : null;
    }
}

/// <summary>
/// The needs of a workflow's steps as one answer. <see cref="Steps"/> holds only steps with a need; a step the run
/// cannot reach is left out when the reach is known.
/// </summary>
public sealed record MediaEgressSummary(IReadOnlyList<MediaEgressStepNeed> Steps)
{
    /// <summary>True when some step needs or may use the permission: the app shows the field.</summary>
    public bool Present => Steps.Count > 0;

    /// <summary>True when a step that cannot run without the permission has its saved switch off: the field must be on to run.</summary>
    public bool Required => Steps.Any(s => s is { Level: MediaEgressNeedLevel.Required, StoredOn: false });

    /// <summary>True when every step with a need already has its saved switch on: the field is shown on and changes nothing.</summary>
    public bool AlreadyAllowed => Present && Steps.All(s => s.StoredOn);

    /// <summary>The steps the field would switch on for a run.</summary>
    public IReadOnlyList<MediaEgressStepNeed> Pending => [.. Steps.Where(s => s.Pending)];
}

/// <summary>
/// The permission to send the customer's media to an outside AI service as a field of the app
/// (docs/design/app-inputs.md "Permission to send media as an app field"). The field is DERIVED from the steps and
/// never stored: <see cref="WithField"/> adds it wherever a manifest is read, <see cref="Strip"/> removes it wherever
/// one is saved, and the manifest validator refuses any hand-written input that uses its id or its marker. It is bound
/// by code only (<see cref="TryGrant"/>, called by <see cref="AppInputBinder"/>): the config member stays off the
/// <c>[AppBindable]</c> allowlist, so no author- or assistant-written binding can ever name it. A grant applies to one
/// run's snapshot; it turns a step's switch on and never off.
/// </summary>
public static class MediaEgressPermission
{
    /// <summary>The id of the field, and the key of its value in <c>brief.inputs</c>.</summary>
    public const string InputId = "allowMediaEgress";

    /// <summary>The value of the manifest input's <c>permission</c> member (a closed vocabulary of one).</summary>
    public const string Marker = "mediaEgress";

    public const string Label = "Allow sending your media to AI services";
    public const string LabelKey = "permission.mediaEgress.label";

    /// <summary>The refusal of a run that needs the permission and did not get it (<c>APP_INPUT_MISSING</c>).</summary>
    public const string RequiredMessage = "Allow sending your media to AI services to run this app.";

    /// <summary>The refusal of a grant from a caller that may not give it (<c>APP_INPUT_INVALID</c>, fail closed).</summary>
    public const string NotAllowedHereMessage = "This permission can only be given in the app. Open the app page and allow it there.";

    /// <summary>The refusal of a change request that names the field: a permission belongs to the run that was given it.</summary>
    public const string NotChangeableMessage = "The permission to send media is given when you run the app. Run the app again to change it.";

    // ---- who allowed it (StoredRunBrief.AllowMediaEgressBy) ----
    public const string ByApp = "app";
    public const string ByApiKey = "api_key";
    public const string ByMcp = "mcp";
    public const string ByAssistantCard = "assistant_card";

    /// <summary>The closed set of grantors a stored brief may name; anything else is no grant (fail closed).</summary>
    public static readonly IReadOnlyList<string> Grantors = [ByApp, ByApiKey, ByMcp, ByAssistantCard];

    /// <summary>True for a known grantor.</summary>
    public static bool IsGrantor(string? by) => by is not null && Grantors.Contains(by, StringComparer.Ordinal);

    /// <summary>True when the values carry the field set to <c>true</c> (the only value that means anything).</summary>
    public static bool IsRequested(IReadOnlyDictionary<string, JsonElement>? values) =>
        values is not null && values.TryGetValue(InputId, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>True for the system field itself: the id together with the marker.</summary>
    public static bool IsField(AppManifestInput input) =>
        input.Kind == AppManifestVocabulary.KindInput
        && string.Equals(input.Id, InputId, StringComparison.Ordinal)
        && string.Equals(input.Permission, Marker, StringComparison.Ordinal);

    /// <summary>True for an input that uses the reserved id or carries a <c>permission</c> member at all.</summary>
    public static bool IsReserved(AppManifestInput input) =>
        input.Permission is not null || string.Equals(input.Id, InputId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The avatar knowledge a manifest gives: offered when it has the brief's avatar picker.</summary>
    public static MediaEgressBriefAvatar AvatarOf(AppManifest? manifest) =>
        (manifest?.Inputs ?? []).Any(i => i.Kind == AppManifestVocabulary.KindBrief && i.Field == BriefFieldConsumers.Avatar)
            ? MediaEgressBriefAvatar.Offered
            : MediaEgressBriefAvatar.None;

    /// <summary>The needs of <paramref name="steps"/>; with <paramref name="reachable"/> only the steps the run can reach.</summary>
    public static MediaEgressSummary Describe(
        IReadOnlyList<AppBindingStep> steps, MediaEgressBriefAvatar avatar, IReadOnlySet<int>? reachable = null) =>
        new([.. steps
            .Where(s => reachable is null || reachable.Contains(s.StepOrder))
            .Select(s => MediaEgressNeed.For(s, avatar))
            .Where(n => n.Level != MediaEgressNeedLevel.None)
            .OrderBy(n => n.StepOrder)]);

    /// <summary>
    /// The field for these needs, or null when no step has one. Required when a step that cannot run without the
    /// permission has its saved switch off; shown on (<c>default: true</c>) when every step already has it on.
    /// </summary>
    public static AppManifestInput? Field(MediaEgressSummary summary)
    {
        if (!summary.Present)
            return null;
        return new AppManifestInput(
            AppManifestVocabulary.KindInput,
            Label: Label,
            Default: summary.AlreadyAllowed ? JsonSerializer.SerializeToElement(true) : null,
            Id: InputId,
            Type: AppInputTypes.WireName(AppInputType.Toggle),
            Help: Help(summary),
            Required: summary.Required ? true : null,
            More: false,
            LabelKey: LabelKey,
            Permission: Marker);
    }

    /// <summary>"Needed by: Generate Video (shows an avatar), Analyze video (finds the object)", at most 160 characters.</summary>
    public static string Help(MediaEgressSummary summary)
    {
        IReadOnlyList<MediaEgressStepNeed> named = summary.Pending.Count > 0 ? summary.Pending : summary.Steps;
        string head = summary.AlreadyAllowed ? "Already allowed for: " : summary.Required ? "Needed by: " : "Used by: ";
        string text = head + string.Join(", ", named.Select(n => $"{StepName(n)} ({MediaEgressNeed.ShortReason(n.Reason)})"));
        return text.Length <= AppInputTypes.MaxHelp ? text : text[..(AppInputTypes.MaxHelp - 1)].TrimEnd() + "…";
    }

    /// <summary>The step's label, else its type in words.</summary>
    public static string StepName(MediaEgressStepNeed need) =>
        !string.IsNullOrWhiteSpace(need.StepName) ? need.StepName! : $"step {need.StepOrder} ({need.StepType})";

    /// <summary>
    /// <paramref name="manifest"/> with the derived field (replacing any input that uses its id), or without one when no
    /// step has a need. Returns the same instance when nothing changes, so a workflow with no such step is untouched.
    /// The field sits last: the app page shows it in its own block above the Run button.
    /// </summary>
    public static AppManifest? WithField(AppManifest? manifest, IReadOnlyList<AppBindingStep> steps, MediaEgressBriefAvatar? avatar = null)
    {
        AppManifestInput? field = Field(Describe(steps, avatar ?? AvatarOf(manifest)));
        bool carries = (manifest?.Inputs ?? []).Any(IsReserved);
        if (field is null && !carries)
            return manifest;

        List<AppManifestInput> inputs = [.. (manifest?.Inputs ?? []).Where(i => !IsReserved(i))];
        if (field is not null)
            inputs.Add(field);
        AppManifest source = manifest ?? new AppManifest();
        return source with
        {
            Version = field is null ? source.Version : Math.Max(source.Version, AppManifestVocabulary.Version2),
            Inputs = inputs
        };
    }

    /// <summary>A manifest without the system field (what is stored): the field is derived, so saving it means nothing.</summary>
    public static AppManifest Strip(AppManifest manifest) =>
        manifest.Inputs is { } inputs && inputs.Any(IsField)
            ? manifest with { Inputs = [.. inputs.Where(i => !IsField(i))] }
            : manifest;

    /// <summary>
    /// Turns the step's switch on for one run: true (and the step with the member set) only for a step that has a need
    /// and whose switch is off. A step with no need, or one already on, is returned untouched; nothing is ever switched off.
    /// </summary>
    public static bool TryGrant(AppBindingStep step, MediaEgressBriefAvatar avatar, out AppBindingStep granted)
    {
        granted = step;
        if (!MediaEgressNeed.For(step, avatar).Pending || MediaEgressNeed.Parse(step.ConfigJson) is not { } config)
            return false;
        config[AppValueBinding.MemberName(config, MediaEgressNeed.ConfigMember)] = true;
        granted = step with { ConfigJson = config.ToJsonString() };
        return true;
    }

    /// <summary>The steps a grant covers and what each sends, for an approval card, a run response and the run's own record.</summary>
    public static IReadOnlyList<(string StepName, string Sends)> Sent(MediaEgressSummary summary) =>
        [.. summary.Steps.Select(n => (StepName(n), MediaEgressNeed.Sends(n.Reason)))];
}
