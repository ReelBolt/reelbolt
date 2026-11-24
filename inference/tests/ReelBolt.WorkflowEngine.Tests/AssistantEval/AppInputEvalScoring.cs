using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>One app-page operation a goal expects (fixture <c>expected.score.ops[]</c>); <c>As</c>/<c>Input</c> link a field to its binding.</summary>
internal sealed record AppInputEvalOp(
    string Op,
    string? Type = null,
    bool? Required = null,
    bool? Example = null,
    JsonElement? Default = null,
    string? Capability = null,
    bool? More = null,
    string? Recommended = null,
    string? As = null,
    string? Field = null,
    string? Mode = null,
    string? Input = null,
    string? Id = null);

/// <summary>What a correct sample does for one app-input goal (fixture <c>expected.score</c>).</summary>
/// <param name="Proposal"><c>patch</c> (ProposePatchWorkflow), <c>patchOrSave</c> (it or ProposeWorkflow) or <c>execute</c> (ExecuteWorkflow).</param>
internal sealed record AppInputEvalSpec(
    string Proposal,
    bool Apply,
    IReadOnlyList<AppInputEvalOp> Ops,
    string? ConditionalReads,
    bool ConditionalEndBranch,
    IReadOnlyList<string> Alternates,
    IReadOnlyDictionary<string, JsonElement> Inputs,
    IReadOnlyList<string> OnlyRemoves,
    IReadOnlyList<string> MustNot);

internal sealed record AppInputEvalGoal(string Id, string Locale, string Goal, IReadOnlyList<string> Tools, AppInputEvalSpec Score);

/// <summary>The verdict of one sample: passed, which route it took and every miss in plain words.</summary>
internal sealed record AppInputEvalScore(bool Passed, string Route, IReadOnlyList<string> Misses);

/// <summary>
/// The app-input eval (docs/design/app-inputs.md 13.3, 29): the five goals of <c>app-input-eval-goals.json</c> scored from a
/// transcript, with no model call. The scorer reads the proposal and apply calls' arguments: the patch operations
/// (<c>setAppInput</c>, <c>bindAppInput</c>, <c>removeAppInput</c>, <c>setAppPage</c>), an app page and its bindings saved
/// through <c>ProposeWorkflow</c>, a Conditional that reads a field, and <c>brief.inputs</c> of <c>ExecuteWorkflow</c>. Kept
/// apart from the pinned 50-goal recipe set (<see cref="EvalFixture"/>), which it never reads.
/// </summary>
internal static class AppInputEvalScoring
{
    public const string RelativePath = "docs/design/workflow-apps/app-input-eval-goals.json";

    private static readonly Regex GuidRx = new(
        @"(?<![0-9A-Za-z])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Za-z])", RegexOptions.Compiled);

    private static readonly Regex PriceRx = new(
        @"\d[\d.,]*\s*(credits?|crediti|usd|\$|€|eur)\b|[$€]\s*\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlaceholderRx = new(@"\{\{\s*input\.([A-Za-z][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    private static readonly Regex QuestionRx = new(@"[?？](?=\s|$)", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string LocateFile()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, RelativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{RelativePath} not found above {AppContext.BaseDirectory}");
    }

    public static IReadOnlyList<AppInputEvalGoal> Load(string? path = null)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path ?? LocateFile()));
        return [.. doc.RootElement.GetProperty("goals").EnumerateArray().Select(Parse)];
    }

    private static AppInputEvalGoal Parse(JsonElement g)
    {
        JsonElement e = g.GetProperty("expected");
        JsonElement s = e.GetProperty("score");
        Dictionary<string, JsonElement> inputs = new(StringComparer.Ordinal);
        if (s.TryGetProperty("inputs", out JsonElement i) && i.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in i.EnumerateObject()) inputs[p.Name] = p.Value.Clone();
        JsonElement? conditional = s.TryGetProperty("conditional", out JsonElement c) ? c : null;
        AppInputEvalSpec spec = new(
            s.GetProperty("proposal").GetString()!,
            s.TryGetProperty("apply", out JsonElement apply) && apply.GetBoolean(),
            s.TryGetProperty("ops", out JsonElement ops) ? ops.Deserialize<List<AppInputEvalOp>>(Web)! : [],
            conditional?.GetProperty("reads").GetString(),
            conditional is { } cond && cond.TryGetProperty("endBranch", out JsonElement end) && end.GetBoolean(),
            Strings(s, "alternates"),
            inputs,
            Strings(s, "onlyRemoves"),
            Strings(s, "mustNot"));
        return new AppInputEvalGoal(
            g.GetProperty("id").GetString()!, g.GetProperty("locale").GetString()!, g.GetProperty("goal").GetString()!,
            Strings(e, "tools"), spec);
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(x => x.GetString() ?? "")]
            : [];

    // ------------------------------------------------------------------ observation

    /// <summary>One app-page edit the sample made, from patch operations or a saved page (normalized: op lower-case).</summary>
    internal sealed record SeenOp(string Op, JsonElement Raw, int StepOrder, string? StepType);

    private static JsonElement ParseArgs(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool IsError(string? result) =>
        string.IsNullOrWhiteSpace(result) || result.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? Obj(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { } text && text.TrimStart().StartsWith('{')) return ParseArgs(text);
        return v.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? v : null;
    }

    /// <summary>
    /// The app-page edits of a transcript: the operations of the last accepted patch proposal (its Apply when there is one),
    /// plus a page saved through ProposeWorkflow (each field a setAppInput; each step setting that reads a field a bindAppInput).
    /// </summary>
    internal static IReadOnlyList<SeenOp> Ops(EvalTranscript t)
    {
        List<SeenOp> seen = [];
        // The applied edits when the Apply call carries them, else the last accepted proposal's.
        EvalToolCall? patch = t.Calls.LastOrDefault(c => c.Name is "ApplyPatchWorkflow" or "PatchWorkflow" && Obj(ParseArgs(c.ArgsJson), "operations") is not null)
            ?? t.Calls.LastOrDefault(c => c.Name == "ProposePatchWorkflow" && !IsError(c.Result));
        if (patch is not null && Obj(ParseArgs(patch.ArgsJson), "operations") is { ValueKind: JsonValueKind.Array } ops)
        {
            foreach (JsonElement op in ops.EnumerateArray())
            {
                string name = (Str(op, "op") ?? string.Empty).ToLowerInvariant();
                int order = op.TryGetProperty("stepOrder", out JsonElement so) && so.TryGetInt32(out int o) ? o : 0;
                seen.Add(new SeenOp(name, op, order, null));
            }
        }

        EvalToolCall? save = t.Calls.LastOrDefault(c => c.Name is "ApplyWorkflow" or "SaveWorkflow" && Obj(ParseArgs(c.ArgsJson), "steps") is not null)
            ?? t.Calls.LastOrDefault(c => c.Name == "ProposeWorkflow" && !IsError(c.Result));
        if (save is not null)
        {
            JsonElement args = ParseArgs(save.ArgsJson);
            if (Obj(args, "appManifest") is { } page && Obj(page, "inputs") is { ValueKind: JsonValueKind.Array } fields)
            {
                foreach (JsonElement field in fields.EnumerateArray().Where(f => Str(f, "kind") == "input"))
                    seen.Add(new SeenOp("setappinput", JsonSerializer.SerializeToElement(new { op = "setAppInput", input = field }), 0, null));
            }

            if (Obj(args, "steps") is { ValueKind: JsonValueKind.Array } steps)
            {
                foreach (JsonElement step in steps.EnumerateArray())
                {
                    int order = step.TryGetProperty("stepOrder", out JsonElement so) && so.TryGetInt32(out int o) ? o : 0;
                    string? type = Str(step, "stepType");
                    seen.Add(new SeenOp("step", step, order, type));
                    foreach (JsonProperty member in step.EnumerateObject().Where(m => m.Name.EndsWith("ConfigJson", StringComparison.Ordinal)))
                    {
                        JsonElement config = member.Value.ValueKind == JsonValueKind.String ? ParseArgs(member.Value.GetString()!) : member.Value;
                        foreach (SeenOp bind in BindingsOf(config, order))
                            seen.Add(bind);
                    }
                }
            }
        }

        return seen;
    }

    /// <summary>A saved step config's bindings as bindAppInput operations: each <c>appInputs</c> entry and each <c>{{input.id}}</c>.</summary>
    private static IEnumerable<SeenOp> BindingsOf(JsonElement config, int order)
    {
        if (config.ValueKind != JsonValueKind.Object) yield break;
        if (config.TryGetProperty("appInputs", out JsonElement map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in map.EnumerateObject())
            {
                string? input = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : Str(p.Value, "input");
                yield return new SeenOp("bindappinput", JsonSerializer.SerializeToElement(new { op = "bindAppInput", stepOrder = order, field = p.Name, inputId = input, mode = "value" }), order, null);
            }
        }

        foreach (JsonProperty p in config.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
        {
            foreach (Match m in PlaceholderRx.Matches(p.Value.GetString()!))
                yield return new SeenOp("bindappinput", JsonSerializer.SerializeToElement(new { op = "bindAppInput", stepOrder = order, field = p.Name, inputId = m.Groups[1].Value, mode = "placeholder" }), order, null);
        }
    }

    // ------------------------------------------------------------------ scoring

    /// <summary>Scores one transcript against one goal; every miss is listed (a sample passes with none).</summary>
    public static AppInputEvalScore Score(AppInputEvalGoal goal, EvalTranscript t)
    {
        AppInputEvalSpec spec = goal.Score;
        List<string> misses = [];
        bool proposed = spec.Proposal switch
        {
            "patch" => t.Calls.Any(c => c.Name is "ProposePatchWorkflow" or "PatchWorkflow"),
            "patchOrSave" => t.Calls.Any(c => c.Name is "ProposePatchWorkflow" or "PatchWorkflow" or "ProposeWorkflow" or "SaveWorkflow"),
            "execute" => t.Calls.Any(c => c.Name == "ExecuteWorkflow"),
            _ => false
        };

        // An allowed alternate: asking first (one or two questions, no proposal and no run).
        if (!proposed && spec.Alternates.Contains("clarify_first"))
        {
            int questions = QuestionRx.Matches(t.Reply).Count;
            if (questions is >= 1 and <= 2)
                return Finish("clarify_first", MustNots(spec, t, misses));
        }

        if (!proposed)
            misses.Add($"no {spec.Proposal} proposal");
        if (spec.Apply && !t.Calls.Any(c => c.Name is "ApplyPatchWorkflow" or "ApplyWorkflow" or "PatchWorkflow" or "SaveWorkflow"))
            misses.Add("the proposal was not applied");

        IReadOnlyList<SeenOp> seen = Ops(t);
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        foreach (AppInputEvalOp expected in spec.Ops)
        {
            if (!seen.Any(s => Matches(expected, s, aliases, t)))
                misses.Add($"missing {Describe(expected)}");
        }

        if (spec.ConditionalReads is { } reads)
        {
            string? id = aliases.GetValueOrDefault(reads);
            // A Conditional reads the field, and (when asked) a branch of the app finishes the run ("end").
            bool found = id is not null && seen.Any(s => s.Op == "step" && s.StepType == "Conditional"
                    && (Str(s.Raw, "conditionExpression") ?? string.Empty).Contains($"input.{id}", StringComparison.Ordinal))
                && (!spec.ConditionalEndBranch || seen.Any(s => s.Op == "step" && s.StepType == "Conditional"
                    && (Str(s.Raw, "trueBranchStepOrder") == "end" || Str(s.Raw, "falseBranchStepOrder") == "end")));
            if (!found)
                misses.Add($"no Conditional reading input.{id ?? reads}{(spec.ConditionalEndBranch ? " with a branch that finishes" : string.Empty)}");
        }

        if (spec.OnlyRemoves.Count > 0)
        {
            string[] others = [.. seen.Where(s => s.Op == "removeappinput").Select(s => Str(s.Raw, "id") ?? "?").Where(id => !spec.OnlyRemoves.Contains(id))];
            if (others.Length > 0)
                misses.Add($"also removes {string.Join(", ", others)}");
        }

        if (spec.Inputs.Count > 0)
            ScoreInputs(spec, t, misses);

        return Finish(spec.Proposal, MustNots(spec, t, misses));

        AppInputEvalScore Finish(string route, List<string> all) => new(all.Count == 0, route, all);
    }

    private static void ScoreInputs(AppInputEvalSpec spec, EvalTranscript t, List<string> misses)
    {
        EvalToolCall? run = t.Calls.LastOrDefault(c => c.Name == "ExecuteWorkflow");
        JsonElement? inputs = run is null ? null : Obj(ParseArgs(run.ArgsJson), "brief") is { } brief ? Obj(brief, "inputs") : null;
        foreach ((string id, JsonElement want) in spec.Inputs)
        {
            if (inputs is not { ValueKind: JsonValueKind.Object } got || !got.TryGetProperty(id, out JsonElement have))
            {
                misses.Add($"brief.inputs.{id} missing");
                continue;
            }

            bool same = want.ValueKind == JsonValueKind.String
                ? have.ValueKind == JsonValueKind.String && NormalizeText(have.GetString()!) == NormalizeText(want.GetString()!)
                : string.Equals(have.GetRawText(), want.GetRawText(), StringComparison.Ordinal);
            if (!same)
                misses.Add($"brief.inputs.{id} is {have.GetRawText()}, expected {want.GetRawText()}");
        }
    }

    /// <summary>Text compared as a person reads it: trimmed, outer quotes and a final full stop dropped, curly quotes straight.</summary>
    internal static string NormalizeText(string text) =>
        text.Trim().Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"').Trim('"', '\'').TrimEnd('.', '!').Trim();

    private static List<string> MustNots(AppInputEvalSpec spec, EvalTranscript t, List<string> misses)
    {
        foreach (string rule in spec.MustNot)
        {
            switch (rule)
            {
                case "egress" when t.Calls.Any(c => Regex.IsMatch(c.ArgsJson, "\"allowSourceMediaEgress\"\\s*:\\s*true", RegexOptions.IgnoreCase)):
                    misses.Add("turns on allowSourceMediaEgress");
                    break;
                case "ids" when GuidRx.IsMatch(t.Reply):
                    misses.Add("shows an id in the reply");
                    break;
                case "prices" when PriceRx.IsMatch(t.Reply):
                    misses.Add("quotes a price in credits or money");
                    break;
            }
        }

        return misses;
    }

    private static bool Matches(AppInputEvalOp want, SeenOp seen, Dictionary<string, string> aliases, EvalTranscript t)
    {
        if (!string.Equals(seen.Op, want.Op, StringComparison.OrdinalIgnoreCase)) return false;
        JsonElement raw = seen.Raw;
        switch (want.Op.ToLowerInvariant())
        {
            case "setappinput":
                {
                    JsonElement input = Obj(raw, "input") ?? default;
                    if (input.ValueKind != JsonValueKind.Object) return false;
                    if (want.Type is { } type && !string.Equals(Str(input, "type"), type, StringComparison.Ordinal)) return false;
                    if (want.Required == true && !(input.TryGetProperty("required", out JsonElement r) && r.ValueKind == JsonValueKind.True)) return false;
                    if (want.Example == true && string.IsNullOrWhiteSpace(Str(input, "example"))) return false;
                    if (want.More == true && !(input.TryGetProperty("more", out JsonElement m) && m.ValueKind == JsonValueKind.True)) return false;
                    if (want.Capability is { } cap && !string.Equals(Str(input, "capability"), cap, StringComparison.Ordinal)) return false;
                    if (want.Default is { } def)
                    {
                        bool has = input.TryGetProperty("default", out JsonElement d);
                        bool falsy = def.ValueKind == JsonValueKind.False;
                        if (falsy ? has && d.ValueKind != JsonValueKind.False : !has || d.GetRawText() != def.GetRawText()) return false;
                    }

                    if (want.Recommended == "cheapest" && !RecommendsCheapest(input, t)) return false;
                    if (want.As is { } alias && Str(input, "id") is { } id) aliases[alias] = id;
                    return true;
                }
            case "bindappinput":
                {
                    if (want.Field is { } field && !string.Equals(Str(raw, "field"), field, StringComparison.Ordinal)) return false;
                    if (want.Mode is { } mode && !string.Equals(Str(raw, "mode"), mode, StringComparison.OrdinalIgnoreCase)) return false;
                    string? bound = Str(raw, "inputId");
                    if (want.Mode == "placeholder" && bound is null && Str(raw, "template") is { } template
                        && PlaceholderRx.Match(template) is { Success: true } m)
                        bound = m.Groups[1].Value;
                    return want.Input is null || (aliases.TryGetValue(want.Input, out string? id) && id == bound);
                }
            case "removeappinput":
                return want.Id is null || string.Equals(Str(raw, "id"), want.Id, StringComparison.Ordinal);
            default:
                return true;
        }
    }

    /// <summary>
    /// The model field recommends the cheapest option the sample was shown: among the model options any tool answer listed
    /// (objects with an <c>id</c> and a <c>costIndex</c>, or a <c>price</c> in words), the one marked "cheapest" or with the lowest
    /// cost index. True when the sample was shown no options but recommended one (there is nothing to compare).
    /// </summary>
    private static bool RecommendsCheapest(JsonElement input, EvalTranscript t)
    {
        string? recommended = Str(input, "recommended");
        if (recommended is null) return false;
        List<(string Id, double Cost)> options = [];
        foreach (EvalToolCall call in t.Calls.Where(c => !string.IsNullOrWhiteSpace(c.Result)))
        {
            foreach (JsonElement option in Descend(ParseArgs(call.Result!)).Where(o => o.ValueKind == JsonValueKind.Object))
            {
                if (Str(option, "id") is not { } id) continue;
                if (option.TryGetProperty("costIndex", out JsonElement ci) && ci.TryGetDouble(out double cost))
                    options.Add((id, cost));
                else if (Str(option, "price") is { } price)
                    options.Add((id, price == "cheapest" ? 0 : double.MaxValue));
            }
        }

        if (options.Count == 0) return true;
        string cheapest = options.OrderBy(o => o.Cost).First().Id;
        return string.Equals(recommended, cheapest, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<JsonElement> Descend(JsonElement e)
    {
        yield return e;
        if (e.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in e.EnumerateObject())
                foreach (JsonElement x in Descend(p.Value)) yield return x;
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in e.EnumerateArray())
                foreach (JsonElement x in Descend(item)) yield return x;
    }

    private static string Describe(AppInputEvalOp op) => op.Op switch
    {
        "setAppInput" => $"setAppInput ({op.Type}{(op.Required == true ? ", required" : string.Empty)}{(op.More == true ? ", under More options" : string.Empty)}{(op.Recommended is { } r ? $", recommends the {r}" : string.Empty)})",
        "bindAppInput" => $"bindAppInput ({op.Field}, {op.Mode})",
        "removeAppInput" => $"removeAppInput ({op.Id})",
        _ => op.Op
    };
}
