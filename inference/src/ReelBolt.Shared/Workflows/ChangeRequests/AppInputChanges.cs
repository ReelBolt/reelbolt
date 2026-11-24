using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>
/// The codes of a change request that changes app-input values (docs/design/app-inputs.md 10.2, 25). The value checks
/// themselves answer with the run gate's own <c>APP_INPUT_*</c> codes (<see cref="AppInputValueProblem"/>).
/// </summary>
public static class AppInputChangeCodes
{
    /// <summary>409: a changed value would take another branch of the app; a different branch is a different film.</summary>
    public const string ChangesBranch = "INPUT_CHANGES_BRANCH";

    /// <summary>400: no value differs from the run's.</summary>
    public const string Unchanged = "APP_INPUTS_UNCHANGED";

    /// <summary>409: nothing on the run's path uses the changed fields, so there is nothing to redo.</summary>
    public const string NotUsed = "APP_INPUT_NOT_USED";

    /// <summary>409: the app changed since the run, so the run's steps can no longer be re-made from its fields.</summary>
    public const string Stale = "APP_INPUTS_STALE";

    /// <summary>
    /// 400 (with <c>field</c>): a run setting a change cannot honour, because it changes the whole film (shape, size, look)
    /// or is not a run setting at all (docs/design/app-inputs.md 29). Run the app again instead.
    /// </summary>
    public const string BriefNotChangeable = "BRIEF_FIELD_NOT_CHANGEABLE";

    /// <summary>409 (with <c>field</c>): nothing on the run's path reads the changed run setting.</summary>
    public const string BriefNotUsed = "BRIEF_FIELD_NOT_USED";

    /// <summary>The sentence of <see cref="ChangesBranch"/>.</summary>
    public const string ChangesBranchMessage =
        "This change picks another path through the app, so it makes a different video. Run it again instead.";

    /// <summary>The sentence of <see cref="Stale"/>.</summary>
    public const string StaleMessage =
        "This app changed after this run, so its fields can no longer be changed here. Run it again instead.";
}

/// <summary>
/// The stored shape of an app-input change on its <c>step_change_requests</c> rows (one row per step it re-makes): the
/// overrides column holds <c>{"@appInputs": {"values": {inputId: value}}}</c>, the changed values only. The item key
/// <c>@appInputs</c> can never be a step item id, so no executor reads such a row as overrides: the engine's change
/// context skips it and the step runs from its own re-bound config.
/// </summary>
public static class AppInputChangeRows
{
    /// <summary>The reserved item key.</summary>
    public const string MarkerItem = "@appInputs";

    /// <summary>The key under the marker that holds the changed values.</summary>
    public const string ValuesKey = "values";

    /// <summary>
    /// The key under the marker that holds the run settings (brief fields) the change sets, in words
    /// (docs/design/app-inputs.md 29): a list of <see cref="BriefChangeLine"/>. Absent when no run setting changed.
    /// </summary>
    public const string BriefKey = "brief";

    private static readonly JsonSerializerOptions LineOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The overrides JSON of a row of an app-input change.</summary>
    public static string Serialize(IReadOnlyDictionary<string, JsonElement> changed) => Serialize(changed, null);

    /// <summary>
    /// The overrides JSON of a row of an app-input change that may also set run settings: the changed values under
    /// <see cref="ValuesKey"/> (possibly none) and, only when there are any, the run settings in words under <see cref="BriefKey"/>.
    /// </summary>
    public static string Serialize(IReadOnlyDictionary<string, JsonElement> changed, IReadOnlyList<BriefChangeLine>? brief)
    {
        Dictionary<string, JsonElement> sorted = changed
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Dictionary<string, JsonElement> marker = new(StringComparer.Ordinal) { [ValuesKey] = JsonSerializer.SerializeToElement(sorted) };
        if (brief is { Count: > 0 })
            marker[BriefKey] = JsonSerializer.SerializeToElement(brief, LineOptions);
        return JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, JsonElement>> { [MarkerItem] = marker });
    }

    /// <summary>The run settings a row's change sets, in words; empty when none (or the row is not an app-input change).</summary>
    public static IReadOnlyList<BriefChangeLine> BriefLines(string? overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson) || !overridesJson.Contains(MarkerItem, StringComparison.Ordinal))
            return [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(overridesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(MarkerItem, out JsonElement marker)
                || marker.ValueKind != JsonValueKind.Object
                || !marker.TryGetProperty(BriefKey, out JsonElement lines)
                || lines.ValueKind != JsonValueKind.Array)
                return [];
            return lines.Deserialize<List<BriefChangeLine>>(LineOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>True when <paramref name="overridesJson"/> is the overrides of an app-input change.</summary>
    public static bool IsAppInputRow(string? overridesJson) => Values(overridesJson) is not null;

    /// <summary>The changed values a row stores, or null when the row is not an app-input change.</summary>
    public static IReadOnlyDictionary<string, JsonElement>? Values(string? overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson) || !overridesJson.Contains(MarkerItem, StringComparison.Ordinal))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(overridesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(MarkerItem, out JsonElement marker)
                || marker.ValueKind != JsonValueKind.Object
                || !marker.TryGetProperty(ValuesKey, out JsonElement values)
                || values.ValueKind != JsonValueKind.Object)
                return null;
            return values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One run setting (brief field) a change sets, as stored and listed (docs/design/app-inputs.md 29): its key
/// (<c>brief:language</c>, <c>brief:notes</c>, <c>brief:files:Video</c>), its label and the value before and after in words.
/// </summary>
public sealed record BriefChangeLine(string Key, string Label, string Before, string After);

/// <summary>What an app-input change re-makes, or why it is refused.</summary>
/// <param name="Changed">The input ids whose value differs, in id order.</param>
/// <param name="Seeds">The steps on the run's path that consume a changed value (the replay plan's targets); never a Conditional.</param>
public sealed record AppInputChangeDecision(
    IReadOnlyList<string> Changed,
    IReadOnlySet<int> Seeds,
    string? RefusalCode = null,
    string? RefusalMessage = null,
    string? InputId = null)
{
    public bool Refused => RefusalCode is not null;
}

/// <summary>
/// The pure rules of a change request that changes app-input values (docs/design/app-inputs.md 10.2): the seeds of the
/// replay are the steps that consume a changed value (the binder's consumer map: placeholders, whole-value bindings,
/// <c>@model</c>, the request template for every step that reads the request, an agent's model for that agent's steps);
/// everything else is reused. A change that would make the run take another branch is refused
/// (<see cref="AppInputChangeCodes.ChangesBranch"/>), as is one a condition reads that the values alone do not decide
/// (it might). Shared by the API's request path and its capabilities.
/// </summary>
public static class AppInputChangePlanner
{
    /// <summary>The ids whose canonical value differs between the two sets (a value present on one side only counts).</summary>
    public static IReadOnlyList<string> ChangedIds(
        IReadOnlyDictionary<string, JsonElement>? before, IReadOnlyDictionary<string, JsonElement>? after)
    {
        before ??= new Dictionary<string, JsonElement>();
        after ??= new Dictionary<string, JsonElement>();
        return
        [
            .. before.Keys.Union(after.Keys, StringComparer.Ordinal)
                .Where(id => !(before.TryGetValue(id, out JsonElement b) && after.TryGetValue(id, out JsonElement a)
                    && string.Equals(b.GetRawText(), a.GetRawText(), StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>The steps that consume <paramref name="inputId"/> (Conditionals excluded: the replay re-evaluates every branch).</summary>
    public static IReadOnlySet<int> Consumers(AppManifest? manifest, IReadOnlyList<AppBindingStep> steps, string inputId)
    {
        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, steps);
        HashSet<int> conditionals = [.. steps.Where(s => s.StepType == StepType.Conditional).Select(s => s.StepOrder)];
        return new HashSet<int>(map.ConsumingSteps(inputId, steps).Where(o => !conditionals.Contains(o)));
    }

    /// <summary>True when a Conditional reads <paramref name="inputId"/> (the field picks a branch).</summary>
    public static bool DecidesBranch(IReadOnlyList<AppBindingStep> steps, string inputId) =>
        steps.Any(s => s.StepType == StepType.Conditional && StaticBranchResolver.InputIds(s.ConditionExpression).Contains(inputId));

    /// <summary>
    /// The seeds of a change from <paramref name="before"/> to <paramref name="after"/> (both canonical), or the refusal:
    /// <see cref="AppInputChangeCodes.Unchanged"/> when nothing differs, <see cref="AppInputChangeCodes.ChangesBranch"/>
    /// when the reachable steps differ or a changed value is read by a condition the values alone do not decide,
    /// <see cref="AppInputChangeCodes.NotUsed"/> when no step on the run's path consumes a changed value.
    /// </summary>
    public static AppInputChangeDecision Decide(
        AppManifest? manifest,
        IReadOnlyList<AppBindingStep> steps,
        IReadOnlyDictionary<string, JsonElement>? before,
        IReadOnlyDictionary<string, JsonElement>? after)
    {
        IReadOnlyList<string> changed = ChangedIds(before, after);
        if (changed.Count == 0)
            return new AppInputChangeDecision(changed, new HashSet<int>(), AppInputChangeCodes.Unchanged, "Nothing would change: every field already has this value.");

        foreach (string id in changed)
        {
            bool undecided = steps.Any(s => s.StepType == StepType.Conditional
                && StaticBranchResolver.InputIds(s.ConditionExpression).Contains(id)
                && !StaticBranchResolver.IsInputDecided(s.ConditionExpression));
            if (undecided)
                return new AppInputChangeDecision(changed, new HashSet<int>(), AppInputChangeCodes.ChangesBranch, AppInputChangeCodes.ChangesBranchMessage, id);
        }

        IReadOnlySet<int> reachBefore = StaticBranchResolver.ReachableStepOrders(steps, before);
        IReadOnlySet<int> reachAfter = StaticBranchResolver.ReachableStepOrders(steps, after);
        if (!reachBefore.SetEquals(reachAfter))
        {
            string? culprit = changed.FirstOrDefault(id => DecidesBranch(steps, id));
            return new AppInputChangeDecision(changed, new HashSet<int>(), AppInputChangeCodes.ChangesBranch, AppInputChangeCodes.ChangesBranchMessage, culprit);
        }

        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, steps);
        HashSet<int> conditionals = [.. steps.Where(s => s.StepType == StepType.Conditional).Select(s => s.StepOrder)];
        HashSet<int> seeds = [];
        foreach (string id in changed)
            seeds.UnionWith(map.ConsumingSteps(id, steps).Where(o => !conditionals.Contains(o) && reachAfter.Contains(o)));

        return seeds.Count == 0
            ? new AppInputChangeDecision(changed, seeds, AppInputChangeCodes.NotUsed,
                "Nothing in this run uses the changed fields, so there is nothing to redo.", changed[0])
            : new AppInputChangeDecision(changed, seeds);
    }

    /// <summary>
    /// For a switch or a choice that picks a branch: the values (of its options, or on/off) that would take another
    /// branch than <paramref name="values"/> does. Empty for a field no condition reads; null when the values cannot be
    /// listed (a number or a text read by a condition: the server judges each value when it is asked).
    /// </summary>
    public static IReadOnlyList<JsonElement>? BranchChangingValues(
        AppManifestInput input, IReadOnlyList<AppBindingStep> steps, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (input.Id is not { } id || !DecidesBranch(steps, id))
            return [];
        AppInputTypes.TryParse(input.Type, out AppInputType type);
        IEnumerable<JsonElement> candidates = type switch
        {
            AppInputType.Toggle => [JsonSerializer.SerializeToElement(true), JsonSerializer.SerializeToElement(false)],
            AppInputType.Choice or AppInputType.Shape => (input.Options ?? []).Select(o => o.Value),
            _ => []
        };
        List<JsonElement> listed = [.. candidates];
        if (listed.Count == 0)
            return null;

        IReadOnlySet<int> reach = StaticBranchResolver.ReachableStepOrders(steps, values);
        List<JsonElement> changing = [];
        foreach (JsonElement candidate in listed)
        {
            Dictionary<string, JsonElement> next = new(values, StringComparer.Ordinal) { [id] = candidate };
            bool undecided = steps.Any(s => s.StepType == StepType.Conditional
                && StaticBranchResolver.InputIds(s.ConditionExpression).Contains(id)
                && !StaticBranchResolver.IsInputDecided(s.ConditionExpression));
            if (undecided || !StaticBranchResolver.ReachableStepOrders(steps, next).SetEquals(reach))
                changing.Add(candidate);
        }

        return changing;
    }
}
