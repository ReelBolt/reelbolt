using System.Globalization;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>One named violation: where in the payload and what is wrong.</summary>
public sealed record ChangeRequestViolation(string Path, string Message);

/// <summary>
/// Closed-catalogue validation of a change request against <see cref="StepChangeCapabilities"/> and the
/// step's stored output: named violations, nothing written (docs/design/change-requests.md).
/// </summary>
public static class ChangeRequestValidator
{
    public const string AllItems = "*";

    public static IReadOnlyList<ChangeRequestViolation> Validate(StepType type, ChangeRequestPayload? payload, string? outputJson) =>
        Validate(type, null, payload, outputJson);

    /// <summary>
    /// Validates a request against the capability of the step: its step type, and for an Agent step the agent type it hosts.
    /// </summary>
    public static IReadOnlyList<ChangeRequestViolation> Validate(StepType type, AgentType? agent, ChangeRequestPayload? payload, string? outputJson)
    {
        List<ChangeRequestViolation> v = [];
        StepChangeCapability cap = StepChangeCapabilities.For(type, agent);
        if (!cap.Supported)
        {
            v.Add(new("stepType", cap.NotSupportedReason ?? "change requests are not supported for this step"));
            return v;
        }
        if (payload is null)
        {
            v.Add(new("", "the request is missing"));
            return v;
        }

        string instruction = payload.Instruction ?? string.Empty;
        if (instruction.Length > StepChangeCapabilities.MaxInstructionLength)
            v.Add(new("instruction", $"must be at most {StepChangeCapabilities.MaxInstructionLength} characters"));
        if (instruction.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            v.Add(new("instruction", "must not contain control characters"));

        HashSet<string> known = [.. StepChangeCapabilities.ListItems(type, agent, outputJson).Select(i => i.Id)];
        IReadOnlyList<string> itemIds = payload.ItemIds ?? [];
        if (itemIds.Count > StepChangeCapabilities.MaxItems)
            v.Add(new("itemIds", $"at most {StepChangeCapabilities.MaxItems} items"));
        for (int i = 0; i < itemIds.Count && i < StepChangeCapabilities.MaxItems; i++)
        {
            if (string.IsNullOrEmpty(itemIds[i]) || !known.Contains(itemIds[i]))
                v.Add(new($"itemIds[{i}]", "unknown item"));
        }

        var overrides = payload.Overrides ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();
        if (overrides.Count > StepChangeCapabilities.MaxItems)
            v.Add(new("overrides", $"at most {StepChangeCapabilities.MaxItems} items"));
        int taken = 0;
        foreach ((string item, IReadOnlyDictionary<string, JsonElement> keys) in overrides.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (++taken > StepChangeCapabilities.MaxItems)
                break;
            if (item != AllItems && !known.Contains(item))
                v.Add(new($"overrides.{item}", "unknown item"));
            foreach ((string key, JsonElement value) in (keys ?? new Dictionary<string, JsonElement>()).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                ChangeOverrideSpec? spec = cap.Overrides.FirstOrDefault(s => s.Key == key);
                string path = $"overrides.{item}.{key}";
                if (spec is null)
                    v.Add(new(path, "unknown override"));
                else
                    CheckValue(spec, value, path, v);
            }
        }

        bool empty = string.IsNullOrWhiteSpace(instruction)
            && itemIds.Count == 0
            && overrides.Values.All(k => k is null || k.Count == 0);
        // A script is also changed by words alone ("make scene 2 calmer"), as long as it says which scene(s).
        bool instructionOnItems = cap.AcceptsInstructionWithItems && !string.IsNullOrWhiteSpace(instruction) && itemIds.Count > 0;
        if (cap.AcceptsInstructionWithItems && !string.IsNullOrWhiteSpace(instruction) && itemIds.Count == 0
            && overrides.Values.Any(k => k is { Count: > 0 }))
            v.Add(new("itemIds", "name the scene the instruction is about"));
        else if (empty && !cap.AllowsPlainRetry)
            v.Add(new("", "the request asks for nothing: give an instruction, items or overrides"));
        else if (!cap.AllowsPlainRetry && cap.Overrides.Count > 0 && overrides.Values.All(k => k is null || k.Count == 0) && !instructionOnItems)
        {
            v.Add(new("overrides", cap.AcceptsInstructionWithItems
                ? $"say what to change: set one of {string.Join(", ", cap.Overrides.Select(o => o.Key))}, or give an instruction AND name the item(s) it applies to"
                : $"say what to change: set one of {string.Join(", ", cap.Overrides.Select(o => o.Key))} (an instruction alone changes nothing)"));
        }
        // A hostile request must not buy a hostile-sized answer.
        return v.Count > MaxViolations ? v.GetRange(0, MaxViolations) : v;
    }

    private const int MaxViolations = 50;

    private static void CheckValue(ChangeOverrideSpec spec, JsonElement value, string path, List<ChangeRequestViolation> v)
    {
        switch (spec.Type)
        {
            case ChangeOverrideType.Text:
                if (spec.MultiLine && value.ValueKind == JsonValueKind.Array)
                {
                    List<string> lines = [];
                    foreach (JsonElement line in value.EnumerateArray())
                    {
                        if (line.ValueKind != JsonValueKind.String)
                        {
                            v.Add(new(path, "must be text, or a list of text lines"));
                            return;
                        }
                        lines.Add(line.GetString() ?? string.Empty);
                    }
                    CheckText(spec, string.Join('\n', lines), lines.Count, path, v);
                    return;
                }
                if (value.ValueKind != JsonValueKind.String)
                {
                    v.Add(new(path, spec.MultiLine ? "must be text, or a list of text lines" : "must be text"));
                    return;
                }
                string s = value.GetString() ?? string.Empty;
                CheckText(spec, s, s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length, path, v);
                break;
            case ChangeOverrideType.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double d) || !double.IsFinite(d))
                    v.Add(new(path, "must be a number"));
                else if ((spec.Min is double lo && d < lo) || (spec.Max is double hi && d > hi))
                    v.Add(new(path, string.Create(CultureInfo.InvariantCulture, $"must be between {spec.Min} and {spec.Max}")));
                break;
            case ChangeOverrideType.Flag:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    v.Add(new(path, "must be true or false"));
                break;
            case ChangeOverrideType.Enum:
                if (value.ValueKind != JsonValueKind.String
                    || spec.Values is null
                    || !spec.Values.Contains(value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    v.Add(new(path, $"must be one of: {string.Join(", ", spec.Values ?? [])}"));
                break;
            case ChangeOverrideType.PronunciationList:
                CheckPronunciation(spec, value, path, v);
                break;
        }
    }

    private static void CheckText(ChangeOverrideSpec spec, string s, int lineCount, string path, List<ChangeRequestViolation> v)
    {
        if (string.IsNullOrWhiteSpace(s))
            v.Add(new(path, "must not be empty"));
        else if (spec.MaxLength is int max && s.Length > max)
            v.Add(new(path, $"must be at most {max} characters"));
        else if (s.Any(c => char.IsControl(c) && !(spec.MultiLine && c is '\n' or '\r')))
            v.Add(new(path, "must not contain control characters"));
        else if (spec.MultiLine && spec.MaxEntries is int lines && lineCount > lines)
            v.Add(new(path, $"at most {lines} lines"));
    }

    private static void CheckPronunciation(ChangeOverrideSpec spec, JsonElement value, string path, List<ChangeRequestViolation> v)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            v.Add(new(path, "must be a list of {term, spelling}"));
            return;
        }
        int count = value.GetArrayLength();
        if (spec.MaxEntries is int max && count > max)
            v.Add(new(path, $"at most {max} entries"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (i >= (spec.MaxEntries ?? int.MaxValue))
                break;
            string p = $"{path}[{i++}]";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                v.Add(new(p, "must be {term, spelling}"));
                continue;
            }
            foreach (JsonProperty prop in entry.EnumerateObject())
                if (prop.Name is not ("term" or "spelling"))
                    v.Add(new($"{p}.{prop.Name}", "unknown field"));
            // The same rules as the project's pronunciation glossary: a way of writing the word so the voice stresses
            // it right (a written accent, `Novara` -> `Novàra`), not a phonetic alphabet.
            foreach (string error in PronunciationGlossaryValidator.Validate(Str(entry, "term"), Str(entry, "spelling")))
                v.Add(new(error.StartsWith("spelling", StringComparison.Ordinal) ? $"{p}.spelling" : $"{p}.term", error));
            string term = PronunciationGlossaryValidator.Normalize(Str(entry, "term"));
            if (term.Length > 0 && !seen.Add(term))
                v.Add(new($"{p}.term", "appears twice in this line's list"));
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
