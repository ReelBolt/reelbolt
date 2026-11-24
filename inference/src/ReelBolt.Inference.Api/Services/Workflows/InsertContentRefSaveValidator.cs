using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// T-106: save-time rules for the insert content references of a VideoCompile step
/// (<c>insertContentRef</c> and each <c>insertContents[].insertContentRef</c>). Shape and order are checked here;
/// that a <c>StepResult</c> reference belongs to the workflow's project is checked with the T-010/T-014 project-scoped
/// lookup (<see cref="StepConfigSaveValidator.ValidateStepResultSourcesAsync"/>), which reads its ids from
/// <see cref="RefsOf"/>.
/// </summary>
public static class InsertContentRefSaveValidator
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Step types whose result can be a video or an image, i.e. what a <c>from=Step</c> reference can name once
    /// the run reaches the compile (QA-P1-3). The engine's own rule is only "the step's latest result has an
    /// output object under this project" (<c>InsertContentRefResolver</c>), so a type that CAN upload a render is
    /// nameable at run time whether or not the author flipped another switch:
    /// <list type="bullet">
    /// <item><see cref="StepType.Agent"/> — an author's render, uploaded by
    /// <c>RenderVideoAndUploadToStorage</c> and recorded as the step's output object (the same set
    /// <see cref="StepOptionsSaveValidator"/> accepts for <c>registerOutputAsProjectFile</c>);</item>
    /// <item><see cref="StepType.VideoGenerate"/> — a generated clip, uploaded under
    /// <c>agentFiles/generated/</c> (a render, image-free but a real file);</item>
    /// <item>the compilers and revisions (<see cref="StepType.VideoCompile"/>, <see cref="StepType.TimelineRender"/>,
    /// <see cref="StepType.Revise"/>) and the still producers (<see cref="StepType.ExtractFrame"/>,
    /// <see cref="StepType.ThumbnailGenerate"/>).</item>
    /// </list>
    /// Every other type is refused: what it writes is audio, a score, a decision or a plan — see
    /// <see cref="NeverPlayableReason"/>, which is what the refusal tells the author.
    /// </summary>
    private static readonly HashSet<StepType> PlayableOutputs =
    [
        StepType.Agent, StepType.VideoGenerate, StepType.VideoCompile, StepType.TimelineRender, StepType.Revise,
        StepType.ExtractFrame, StepType.ThumbnailGenerate
    ];

    /// <summary>
    /// T-206: what a PinInsert pin's content may name by step — every playable output above, plus an Agent step (an
    /// Author's render, the usual thing to pin) and a VideoGenerate step. Whether that step really rendered is the
    /// executor's to say at run time (<c>PIN_INSERT_UNRESOLVED</c>).
    /// </summary>
    private static readonly HashSet<StepType> PinnableOutputs =
    [
        .. PlayableOutputs, StepType.Agent, StepType.VideoGenerate
    ];

    /// <summary>T-206: every pin content reference of a PinInsert config; unparseable members are reported as malformed.</summary>
    public static IReadOnlyList<InsertContentRef> PinRefsOf(string? configJson)
    {
        List<InsertContentRef> found = [];
        if (string.IsNullOrWhiteSpace(configJson))
            return found;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && TryGet(doc.RootElement, "pins", out JsonElement pins) && pins.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement pin in pins.EnumerateArray())
                {
                    if (pin.ValueKind != JsonValueKind.Object || !TryGet(pin, "content", out JsonElement content) || content.ValueKind != JsonValueKind.Object)
                        continue;
                    try
                    {
                        if (content.Deserialize<InsertContentRef>(Options) is { } reference)
                            found.Add(reference);
                    }
                    catch (JsonException)
                    {
                        found.Add(new InsertContentRef(string.Empty));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Invalid JSON is reported by StepConfigSaveValidator's own PinInsert arm.
        }

        return found;
    }

    private static bool TryGet(JsonElement owner, string name, out JsonElement value)
    {
        foreach (JsonProperty property in owner.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// What a step type that is NOT in <see cref="PlayableOutputs"/> produces instead, in the words of the
    /// refusal: the author is told which kind of step they named, not only that it was the wrong kind.
    /// </summary>
    private static string NeverPlayableReason(StepType type) => type switch
    {
        StepType.Voiceover or StepType.AudioGenerate => "audio",
        StepType.ImageGenerate => "pictures",
        StepType.ReviewLoop => "review scores",
        StepType.EditRoom or StepType.GraphicsRoom or StepType.ColorGradeRoom or StepType.DailiesRoom =>
            "an editorial decision document",
        _ => "a plan, decision or JSON document"
    };

    /// <summary>
    /// Every reference a VideoCompile config carries; a member that does not parse is skipped (the executor owns that).
    /// Member names are matched case-insensitively, as the engine binds them (JsonSerializerDefaults.Web), and every
    /// property whose name matches is read — a superset of the one the engine binds when a name repeats in another
    /// case (S-62, the S-04 pattern).
    /// </summary>
    public static IReadOnlyList<InsertContentRef> RefsOf(string? configJson)
    {
        List<InsertContentRef> found = [];
        if (string.IsNullOrWhiteSpace(configJson))
            return found;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return found;

            Add(doc.RootElement, found);
            foreach (JsonElement entries in Members(doc.RootElement, "insertContents"))
            {
                if (entries.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement entry in entries.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object)
                        Add(entry, found);
            }

            // Pictures and videos over the film: every mediaOverlays[].content is the same
            // InsertContentRef carrier, so it must go through the same project-scope and
            // earlier-playable-step checks as insertContentRef.
            foreach (JsonElement entries in Members(doc.RootElement, "mediaOverlays"))
            {
                if (entries.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement entry in entries.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object)
                        AddMediaOverlayContent(entry, found);
            }
        }
        catch (JsonException)
        {
            // Invalid JSON is reported by the generic checks of StepConfigSaveValidator.
        }

        return found;
    }

    /// <summary>True when a step of this type can produce a video or image a later step can read.</summary>
    public static bool CanProducePlayableOutput(StepType type) => PlayableOutputs.Contains(type);

    /// <summary>Every value of <paramref name="owner"/> whose property name equals <paramref name="name"/> in any case.</summary>
    private static IEnumerable<JsonElement> Members(JsonElement owner, string name)
    {
        foreach (JsonProperty property in owner.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                yield return property.Value;
    }

    private static void Add(JsonElement owner, List<InsertContentRef> found)
    {
        foreach (JsonElement node in Members(owner, "insertContentRef"))
        {
            if (node.ValueKind != JsonValueKind.Object)
                continue;
            try
            {
                if (node.Deserialize<InsertContentRef>(Options) is { } reference)
                    found.Add(reference);
            }
            catch (JsonException)
            {
                found.Add(new InsertContentRef(string.Empty)); // unparseable: report as malformed
            }
        }
    }

    /// <summary>Every <c>mediaOverlays[].content</c> reference of a VideoCompile config (all matches, any case).</summary>
    private static void AddMediaOverlayContent(JsonElement owner, List<InsertContentRef> found)
    {
        foreach (JsonElement node in Members(owner, "content"))
        {
            if (node.ValueKind != JsonValueKind.Object)
                continue;
            try
            {
                if (node.Deserialize<InsertContentRef>(Options) is { } reference)
                    found.Add(reference);
            }
            catch (JsonException)
            {
                found.Add(new InsertContentRef(string.Empty)); // unparseable: report as malformed
            }
        }
    }

    /// <summary>One error per malformed reference, and per Step reference that does not name an earlier step with a video or image output.</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<CreateWorkflowStepRequest> steps)
    {
        List<CreateWorkflowStepRequest> all = steps.ToList();
        List<string> errors = [];
        foreach (CreateWorkflowStepRequest step in all)
        {
            // S-66: the type as the save path stores it (padded, any case or numeric), not an Ordinal match.
            if (!StepConfigSaveValidator.TryParseStepType(step.StepType, out StepType stepType))
                continue;

            // T-206: a PinInsert pin's content may name an earlier step's video or image output.
            if (stepType == StepType.PinInsert)
            {
                errors.AddRange(ValidatePinInsert(step, all));
                continue;
            }

            if (stepType != StepType.VideoCompile)
                continue;

            foreach (InsertContentRef reference in RefsOf(step.VideoCompileConfigJson))
            {
                string prefix = $"Step {step.StepOrder} ({StepType.VideoCompile}): ";
                if (reference.Validate() is { } problem)
                {
                    errors.Add(prefix + problem);
                    continue;
                }

                if (reference.Kind != InsertContentRef.StepKind)
                    continue;

                CreateWorkflowStepRequest? target = all.FirstOrDefault(s => s.StepOrder == reference.StepOrder);
                if (target is null || reference.StepOrder >= step.StepOrder)
                {
                    errors.Add(prefix + $"insertContentRef from=Step stepOrder={reference.StepOrder} must name an earlier step of this workflow.");
                    continue;
                }

                if (!StepConfigSaveValidator.TryParseStepType(target.StepType, out StepType targetType))
                {
                    errors.Add(prefix + NotPlayable(reference, $"step {reference.StepOrder} has no step type that can be read"));
                    continue;
                }

                if (!PlayableOutputs.Contains(targetType))
                    errors.Add(prefix + NotPlayable(reference, $"step {reference.StepOrder} is {targetType}, which produces {NeverPlayableReason(targetType)}"));
            }
        }

        return errors;
    }

    /// <summary>
    /// T-206: a PinInsert's analysis must be an earlier VideoAnalyze step (when named by order) and each pin's
    /// Step content an earlier step that can produce a video or image. Shape errors are the config's own
    /// (<see cref="PinInsertStepConfig.Validate"/>, reported by <see cref="StepConfigSaveValidator"/>).
    /// </summary>
    private static IEnumerable<string> ValidatePinInsert(CreateWorkflowStepRequest step, List<CreateWorkflowStepRequest> all)
    {
        string prefix = $"Step {step.StepOrder} ({StepType.PinInsert}): ";
        PinInsertStepConfig? config = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(step.PinInsertConfigJson))
                config = JsonSerializer.Deserialize<PinInsertStepConfig>(step.PinInsertConfigJson, PinInsertStepConfig.JsonOptions);
        }
        catch (JsonException)
        {
            yield break; // reported by the config arm
        }

        if (config?.Analysis is { From: ExtractInputSource.Step, StepOrder: > 0 } analysis)
        {
            CreateWorkflowStepRequest? target = all.FirstOrDefault(s => s.StepOrder == analysis.StepOrder);
            if (target is null || analysis.StepOrder >= step.StepOrder
                || !string.Equals(target.StepType, nameof(StepType.VideoAnalyze), StringComparison.Ordinal))
            {
                yield return prefix + $"analysis stepOrder={analysis.StepOrder} must name an earlier VideoAnalyze step of this workflow.";
            }
        }

        foreach (InsertContentRef reference in PinRefsOf(step.PinInsertConfigJson))
        {
            if (reference.Validate() is not null || reference.Kind != InsertContentRef.StepKind)
                continue; // malformed ones are the config arm's

            CreateWorkflowStepRequest? target = all.FirstOrDefault(s => s.StepOrder == reference.StepOrder);
            if (target is null || reference.StepOrder >= step.StepOrder)
            {
                yield return prefix + $"pin content from=Step stepOrder={reference.StepOrder} must name an earlier step of this workflow.";
                continue;
            }

            if (!Enum.TryParse(target.StepType ?? nameof(StepType.Agent), out StepType targetType) || !PinnableOutputs.Contains(targetType))
                yield return prefix + $"pin content from=Step stepOrder={reference.StepOrder} must name a step that renders a video or image.";
        }
    }
    /// The one sentence a reference to a step that cannot be inserted gets: the rule, what CAN be named, and —
    /// in <paramref name="whatWasNamed"/> — which kind of step was named instead and why it has nothing to insert.
    /// </summary>
    private static string NotPlayable(InsertContentRef reference, string whatWasNamed) =>
        $"insertContentRef from=Step stepOrder={reference.StepOrder} must name an earlier step that produces a video or image " +
        "output (an Agent render, a generated clip, a compile, a timeline render, a revision or a still); " + whatWasNamed + ".";
}
