using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// One pin of a <c>StepType.PinInsert</c> step: put <paramref name="Content"/> on the tracked screen
/// <paramref name="RegionId"/> (<c>r{n}</c>, exactly as the analysis offered it), or on every offered screen when
/// <paramref name="RegionId"/> is null.
/// </summary>
/// <param name="RegionId">An offered insert region id, or null for every offered screen (not feature-tracked objects).</param>
/// <param name="Content">The content, by reference (<see cref="InsertContentRef"/>): a project file, an earlier step of this run, or a step result of this project. Resolved server-side.</param>
public sealed record PinInsertEntry(string? RegionId, InsertContentRef Content);

/// <summary>
/// JSON configuration for a <c>StepType.PinInsert</c> workflow step, deserialised from
/// <c>WorkflowStep.PinInsertConfigJson</c>. Deterministic, non-LLM and free: the "pinning step" without an agent.
/// It reads the tracked screens a <c>VideoAnalyze</c> step offered and emits a <c>MotionGraphicsPlanOutput</c>
/// carrying screen inserts only (no overlays), so a <c>VideoCompile</c> with <c>graphicsPlan: Step N</c> and
/// <c>enableInserts: true</c> consumes it unchanged. Every content key is resolved by the server; none is ever
/// supplied by a model.
/// </summary>
/// <param name="Analysis">The <c>VideoAnalyze</c> step (run with insert-region detection) whose offered regions are pinned: <c>{"from":"Step","stepOrder":1}</c> or <c>{"from":"Previous"}</c>.</param>
/// <param name="Pins">One entry per content; an entry naming a region wins over the catch-all (null region) entry.</param>
/// <param name="RequireApplied">
/// True (default): every pin must resolve (an offered region, content that exists and is a video or image), else
/// the step fails with <c>PIN_INSERT_UNRESOLVED</c> naming each problem. False: a pin that cannot resolve is
/// skipped and reported under <c>pinInsert.skipped</c>, and the step completes with whatever did.
/// </param>
[StepCard(
    "Pins content onto the screens tracked in the footage and plans the inserts, without a model.",
    ["A footage analysis with tracked screens from an earlier VideoAnalyze step"],
    "A graphics plan with screen inserts only",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits,
    ReadableBy = ["VideoCompile"],
    PairsWith = ["VideoCompile: reads the plan as its graphics"],
    OutputContract = "MotionGraphicsPlanOutput")]
public sealed record PinInsertStepConfig(
    ExtractInputRef Analysis,
    IReadOnlyList<PinInsertEntry> Pins,
    bool RequireApplied = true)
{
    /// <summary>Most pins one step accepts (the compile's own MaxInserts caps what is composited).</summary>
    public const int MaxPins = 16;

    /// <summary>One serialiser for run time and save time (Web defaults, enums as strings).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Regex RegionIdPattern = new("^r[0-9]{1,4}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True when <paramref name="regionId"/> has the shape of an offered insert region id (<c>r{n}</c>).</summary>
    public static bool IsRegionId(string? regionId) => regionId is not null && RegionIdPattern.IsMatch(regionId);

    /// <summary>Every reason this config may not be saved or run, in field order; empty means valid. Never throws.</summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (Analysis is null)
            errors.Add("Analysis is required: the VideoAnalyze step whose tracked screens are pinned.");
        else if (Analysis.From == ExtractInputSource.Step)
        {
            if (Analysis.StepOrder is not > 0)
                errors.Add("Analysis from=Step requires a positive stepOrder.");
        }
        else if (Analysis.From != ExtractInputSource.Previous)
        {
            errors.Add($"Analysis must be from=Step (with stepOrder) or from=Previous, not {Analysis.From}.");
        }

        if (Pins is null || Pins.Count == 0)
        {
            errors.Add("Pins needs at least one entry.");
            return errors;
        }

        if (Pins.Count > MaxPins)
            errors.Add($"Pins has {Pins.Count} entries; at most {MaxPins} are allowed.");

        HashSet<string> regions = new(StringComparer.Ordinal);
        int catchAll = 0;
        for (int i = 0; i < Pins.Count; i++)
        {
            PinInsertEntry? pin = Pins[i];
            if (pin is null)
            {
                errors.Add($"Pins[{i}] is empty.");
                continue;
            }

            if (pin.RegionId is null)
                catchAll++;
            else if (!IsRegionId(pin.RegionId))
                errors.Add($"Pins[{i}].regionId '{Bound(pin.RegionId)}' is not an insert region id (r0, r1, ...).");
            else if (!regions.Add(pin.RegionId))
                errors.Add($"Pins[{i}].regionId '{pin.RegionId}' is pinned twice.");

            if (pin.Content is null)
                errors.Add($"Pins[{i}].content is required.");
            else if (pin.Content.Validate() is { } contentError)
                errors.Add($"Pins[{i}].content: {contentError}");
        }

        if (catchAll > 1)
            errors.Add("At most one pin may leave regionId empty (every offered screen).");

        return errors;
    }

    private static string Bound(string value) => value.Length <= 20 ? value : value[..20] + "...";
}
