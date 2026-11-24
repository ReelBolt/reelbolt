using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>
/// The share of a target step's COST a change request re-makes, and what that does to the step's credit hold
/// (docs/change-requests.md "What it costs"). Pure. The share is weighted by what each item costs to make (a clip by
/// its seconds, a narration line by its UTF-8 bytes, an audio take by its seconds), not by how many items there are:
/// one 14 s clip among three 2 s clips is 70% of the step, not 25%. A request for the whole step (no item named, or a
/// <c>*</c> override), a step type with no sized items, an output that cannot be read, or ANY item whose size is
/// unknown holds the whole step.
/// </summary>
public static class StepChangeScope
{
    /// <summary>
    /// The cost-weighted fraction of the step the request re-makes, in (0, 1]; 1 when the whole step, when the output has
    /// no readable items, when the step type has no sized items, or when any item's size is unknown (never a count guess).
    /// </summary>
    public static double Compute(StepType type, ChangeRequestPayload? payload, string? outputJson) =>
        Compute(type, null, payload, outputJson);

    /// <summary>
    /// <see cref="Compute(StepType, ChangeRequestPayload?, string?)"/> that knows the agent of an Agent step: a script change
    /// by structured overrides is made without a model call (about nothing; everything after it is priced in full by the
    /// planner), a model rewrite holds the whole step.
    /// </summary>
    public static double Compute(StepType type, AgentType? agent, ChangeRequestPayload? payload, string? outputJson)
    {
        if (payload is not null && type == StepType.Agent && agent == AgentType.ScriptwriterAgent)
            return ScriptChange.CostFraction(payload, outputJson);
        if (payload is null || type is not (StepType.Voiceover or StepType.VideoGenerate or StepType.AudioGenerate))
            return 1d;
        IReadOnlyList<StepItem> items = StepChangeCapabilities.ListItems(type, outputJson);
        if (items.Count == 0)
            return 1d;

        // The units that are actually made (and billed): a shot with several takes is made of its takes.
        List<StepItem> leaves = [.. items
            .Where(i => StepChangeCapabilities.ShotIdOf(i.Id) != i.Id || !items.Any(o => o.Id != i.Id && StepChangeCapabilities.ShotIdOf(o.Id) == i.Id))];
        if (leaves.Count == 0)
            return 1d;

        StepChangeSelection? selection = Select(type, leaves, payload);
        if (selection is null)
            return 1d;
        return Math.Clamp(selection.Redone.Sum(r => r.Weight) / selection.Total, double.Epsilon, 1d);
    }

    /// <summary>One item a request re-makes: its weight (seconds / characters, as billed) and the override it carries.</summary>
    public sealed record StepChangeRedoneItem(StepItem Item, double Weight, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Override);

    /// <summary>The named items a request re-makes and the whole step's weight.</summary>
    public sealed record StepChangeSelection(IReadOnlyList<StepChangeRedoneItem> Redone, double Total);

    /// <summary>
    /// The items a request re-makes with their billed weights, or null when the whole step is redone or any size is
    /// unknown (nothing to price per item).
    /// </summary>
    public static StepChangeSelection? SelectRedone(StepType type, ChangeRequestPayload? payload, string? outputJson)
    {
        if (payload is null || type is not (StepType.Voiceover or StepType.VideoGenerate or StepType.AudioGenerate))
            return null;
        IReadOnlyList<StepItem> items = StepChangeCapabilities.ListItems(type, outputJson);
        if (items.Count == 0)
            return null;
        List<StepItem> leaves = [.. items
            .Where(i => StepChangeCapabilities.ShotIdOf(i.Id) != i.Id || !items.Any(o => o.Id != i.Id && StepChangeCapabilities.ShotIdOf(o.Id) == i.Id))];
        return leaves.Count == 0 ? null : Select(type, leaves, payload);
    }

    private static StepChangeSelection? Select(StepType type, List<StepItem> leaves, ChangeRequestPayload payload)
    {
        Dictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>> overrides = [];
        HashSet<string> named = new(payload.ItemIds ?? [], StringComparer.Ordinal);
        bool star = false;
        foreach ((string key, IReadOnlyDictionary<string, System.Text.Json.JsonElement> keys) in payload.Overrides
            ?? new Dictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>>())
        {
            if (key == ChangeRequestValidator.AllItems)
            {
                star |= keys is { Count: > 0 };
            }
            else
            {
                named.Add(key);
                overrides[key] = keys;
            }
        }

        if (star || named.Count == 0)
            return null;

        double total = 0d;
        List<StepChangeRedoneItem> redone = [];
        foreach (StepItem leaf in leaves)
        {
            double w = BaseWeight(type, leaf);
            if (!double.IsFinite(w) || w <= 0d)
                return null; // an unknown size anywhere: hold the whole step
            total += w;
            string shot = StepChangeCapabilities.ShotIdOf(leaf.Id);
            if (!named.Contains(leaf.Id) && !named.Contains(shot))
                continue;
            overrides.TryGetValue(leaf.Id, out IReadOnlyDictionary<string, System.Text.Json.JsonElement>? own);
            if (own is null)
                overrides.TryGetValue(shot, out own);
            redone.Add(new StepChangeRedoneItem(leaf, RemadeWeight(type, w, own), own));
        }

        double sum = redone.Sum(r => r.Weight);
        if (sum <= 0d || !double.IsFinite(sum) || !double.IsFinite(total) || total <= 0d)
            return null;
        return new StepChangeSelection(redone, total);
    }

    // What an item costs to make: a clip its seconds, a narration line its characters (its seconds when it has no
    // text), an audio take its seconds. Unknown/zero = not priceable.
    private static double BaseWeight(StepType type, StepItem item)
    {
        if (type == StepType.Voiceover && !string.IsNullOrWhiteSpace(item.Text))
            return System.Text.Encoding.UTF8.GetByteCount(item.Text); // billed per UTF-8 byte
        return item.DurationSec ?? 0d;
    }

    // An override that makes an item BIGGER bills the new size: a longer line, a longer take. A clip's duration is not
    // an override, so a prompt or retake is the old size.
    private static double RemadeWeight(StepType type, double current, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? own)
    {
        if (own is null)
            return current;
        if (type == StepType.Voiceover && own.TryGetValue("text", out System.Text.Json.JsonElement t)
            && t.ValueKind == System.Text.Json.JsonValueKind.String && t.GetString() is { } text)
            return Math.Max(current, System.Text.Encoding.UTF8.GetByteCount(text));
        if (type == StepType.AudioGenerate && own.TryGetValue("durationSec", out System.Text.Json.JsonElement d)
            && d.ValueKind == System.Text.Json.JsonValueKind.Number && d.TryGetDouble(out double sec) && double.IsFinite(sec))
            return Math.Max(current, sec);
        return current;
    }

    /// <summary>
    /// The hold for the target step scaled by its request's fraction: the full-step hold times the fraction, rounded up,
    /// never below one item's share of it and never above the full-step hold. A missing, non-finite or out-of-range
    /// fraction leaves the hold as it was.
    /// </summary>
    public static long ApplyToHold(long fullMillis, double? fraction)
    {
        if (fullMillis <= 0 || fraction is not double f || !double.IsFinite(f) || f <= 0d || f >= 1d)
            return fullMillis;
        long scaled = (long)Math.Ceiling(fullMillis * f);
        return Math.Clamp(scaled, 1, fullMillis);
    }
}
