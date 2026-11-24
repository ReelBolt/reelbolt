using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>What one sample did, reduced to the facts the scoring table in eval-goals.md talks about.</summary>
internal sealed record EvalObservation(
    string Route,
    string? TemplateKey,
    bool HasProposal,
    IReadOnlyDictionary<string, string> Brief,
    int Questions,
    IReadOnlyList<string> NamedTemplateKeys,
    IReadOnlyList<string> HallucinatedKeys,
    IReadOnlySet<string> LintIds,
    bool IdShown,
    IReadOnlyList<string> ProposalStepTypes,
    IReadOnlyList<string>? TiedKeys = null,
    string? FirstProposalStepsJson = null,
    bool Failed = false);

/// <summary>Per-sample verdicts (before the 2-of-3 majority).</summary>
internal sealed record EvalSampleScore(
    bool RouteOk,
    bool? TemplateOk,
    IReadOnlyDictionary<string, bool> BriefFields,
    int BriefExtras,
    IReadOnlyDictionary<string, bool> Lint,
    bool QuestionPolicyOk,
    bool UnneededQuestion,
    bool? PaidDisclosed,
    bool Hallucinated,
    bool UnrequestedPaid,
    bool IdShown,
    bool Failed = false);

/// <summary>Turns a transcript into an observation, then scores it against the fixture entry.</summary>
internal static class EvalScoring
{
    public static readonly string[] BriefFieldNames =
        ["language", "format", "resolution", "lengthSec", "designSystem", "stylePreset", "voice", "tone"];

    private static readonly HashSet<string> PaidStepTypes = new(["VideoGenerate", "AudioGenerate", "Voiceover"], StringComparer.OrdinalIgnoreCase);

    private static readonly Regex GuidRx = new(
        @"(?<![0-9A-Za-z])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex StoragePathRx = new(@"projects/[0-9a-fA-F-]{8,}/", RegexOptions.Compiled);
    private static readonly Regex LintRx = new(@"\bWL\d{3}\b", RegexOptions.Compiled);
    private static readonly Regex QuestionRx = new(@"[?？](?=\s|$|[\)\]""'*_])", RegexOptions.Compiled);
    private static readonly Regex BacktickRx = new(@"`([a-z0-9]+(?:-[a-z0-9]+){2,})`", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A sentence that asks for approval or offers the next step is not a clarifying question.
    private static readonly Regex OfferRx = new(
        @"\b(shall i|should i (go ahead|apply|run|proceed|start|create|build|set)|want me to|would you like me|do you want me|ready for me|let me know|ok to|okay to|sound good|go ahead|"
        + @"vuoi che|desideri che|quieres que|te parece|soll ich|möchten sie, dass ich|willst du, dass ich|voulez-vous que je|veux-tu que je|souhaitez-vous que je|quer que eu|queres que eu|deseja que eu)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PaidWordsRx = new(
        @"credit|cost|price|pric|spend|spent|charge|paid|usd|\$|€|costa|costo|cuesta|coût|prix|coûte|kosten|preis|custo|custa|preço|crédit|crédito|crediti|gast|dépens",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static HashSet<string> CatalogKeys() =>
        WorkflowTemplateCatalog.GetAll().Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A proposal counts only when the tool accepted it: its answer is not an error object.</summary>
    public static bool IsAcceptedProposal(EvalToolCall c)
    {
        if (c.Name is not ("ProposeCreateFromTemplate" or "ProposeWorkflow")) return false;
        if (string.IsNullOrWhiteSpace(c.Result)) return false;
        JsonElement r = Parse(c.Result);
        if (r.ValueKind == JsonValueKind.Object)
            return !r.TryGetProperty("error", out _) && !r.TryGetProperty("errors", out _);
        return !c.Result.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Questions that ask for missing information: approval prompts and offers are dropped.</summary>
    public static int CountClarifyingQuestions(string reply)
    {
        int n = 0;
        foreach (string sentence in Regex.Split(reply, @"(?<=[.!?？])\s+|\n+"))
            if (QuestionRx.IsMatch(sentence) && !OfferRx.IsMatch(sentence)) n++;
        return n;
    }

    public static EvalObservation Observe(EvalTranscript t, Func<Guid, string?> agentTypeOf)
    {
        HashSet<string> catalog = CatalogKeys();
        var named = new List<string>();
        var briefMerged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        EvalToolCall? proposal = null, firstProposal = null;

        foreach (EvalToolCall c in t.Calls)
        {
            JsonElement args = Parse(c.ArgsJson);
            if (args.ValueKind == JsonValueKind.Object)
            {
                if (c.Name is "GetWorkflowTemplate" or "ProposeCreateFromTemplate" or "ProposeCreateWorkflowFromTemplate"
                    && (TryString(args, "templateKey") ?? TryString(args, "template")) is { Length: > 0 } k)
                    named.Add(k.Trim());
                if (c.Name is "ExecuteWorkflow" or "ProposeCreateFromTemplate" or "ProposeWorkflow" or "ApplyWorkflow")
                    MergeBrief(args, briefMerged);
            }
            if (IsAcceptedProposal(c))
            {
                firstProposal ??= c;
                proposal = c;
            }
        }

        // Agent type and step type of each step of the last accepted proposal.
        var agentTypes = new List<string>();
        var stepTypes = new List<string>();
        var stepConfigs = new List<string>();
        if (proposal is { Name: "ProposeWorkflow" } && Parse(proposal.ArgsJson) is { ValueKind: JsonValueKind.Object } pa
            && pa.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in steps.EnumerateArray())
            {
                string? at = null;
                if (TryString(s, "agentDefinitionId") is { } id && Guid.TryParse(id, out Guid g)) at = agentTypeOf(g);
                agentTypes.Add(at ?? "?");
                stepTypes.Add(TryString(s, "stepType") ?? "Agent");
                stepConfigs.Add(ConfigBlob(s));
            }
        }

        string route;
        string? chosen = null;
        List<string>? tied = null;
        string reply = t.Reply ?? "";
        int questions = 0;
        if (t.Error is not null)
        {
            route = "failed";
        }
        else if (proposal is null)
        {
            questions = CountClarifyingQuestions(reply);
            route = questions > 0 ? "clarify_first" : "decline_with_alternative";
        }
        else if (proposal.Name == "ProposeCreateFromTemplate")
        {
            JsonElement a = Parse(proposal.ArgsJson);
            chosen = TryString(a, "templateKey") ?? TryString(a, "template");
            bool patched = a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("patch", out JsonElement patch)
                && patch.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                && (patch.ValueKind == JsonValueKind.Object || patch.GetArrayLength() > 0);
            route = patched ? "template_with_patch" : "template";
        }
        else
        {
            var match = BestMatch(agentTypes, stepTypes, stepConfigs, named.Where(catalog.Contains).Distinct().ToList());
            route = match.Key is null || match.Ratio < 0.6 ? "custom_graph" : match.Exact ? "template" : "template_with_patch";
            chosen = route == "custom_graph" ? null : match.Key;
            tied = match.Tied;
        }

        var halluc = named.Where(k => !catalog.Contains(k)).ToList();
        // A key written in code font in the reply is used as a template key; plain words (green-screen, quick-win) are prose.
        foreach (Match m in BacktickRx.Matches(reply))
            if (!catalog.Contains(m.Groups[1].Value) && LooksLikeKey(m.Groups[1].Value, catalog)) halluc.Add(m.Groups[1].Value);

        var lint = new HashSet<string>(StringComparer.Ordinal);
        foreach (EvalToolCall c in t.Calls)
            foreach (Match m in LintRx.Matches(c.Result ?? "")) lint.Add(m.Value);
        foreach (Match m in LintRx.Matches(reply)) lint.Add(m.Value);

        bool idShown = GuidRx.IsMatch(reply) || StoragePathRx.IsMatch(reply);
        return new EvalObservation(route, chosen, proposal is not null, briefMerged, questions, named,
            halluc.Distinct().ToList(), lint, idShown, stepTypes, tied, firstProposal?.ArgsJson, t.Error is not null);
    }

    private static bool LooksLikeKey(string token, HashSet<string> catalog)
    {
        string[] parts = token.Split('-');
        string prefix = parts[0] + "-" + parts[1];
        return catalog.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static EvalSampleScore Score(EvalGoal goal, EvalObservation o, string reply)
    {
        EvalExpected e = goal.Expected;
        bool templateGoal = !goal.IsPlanned && e.Route is "template" or "template_with_patch";

        if (o.Failed)
        {
            // An errored or timed-out sample earns nothing: every metric is a miss, never a decline pass.
            return new EvalSampleScore(false, templateGoal ? false : null,
                BriefFieldNames.Where(e.Brief.ContainsKey).ToDictionary(f => f, _ => false), 0,
                e.Lint.ToDictionary(r => r, _ => false), false, false, e.Paid ? false : null, false, false, false, true);
        }

        bool routeOk = o.Route == goal.EffectiveRoute;

        bool? templateOk = null;
        if (templateGoal)
        {
            var good = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { e.TemplateKey ?? "" };
            foreach (string a in e.Alternates) good.Add(a);
            templateOk = o.TemplateKey is not null
                && (good.Contains(o.TemplateKey) || (o.TiedKeys?.Contains(o.TemplateKey) == true && o.TiedKeys.Any(good.Contains)));
        }

        var fields = new Dictionary<string, bool>();
        foreach (string f in BriefFieldNames)
            if (e.Brief.TryGetValue(f, out JsonElement want))
                fields[f] = o.Brief.TryGetValue(f, out string? got) && FieldMatches(f, want, got, o.Brief);
        int extras = BriefFieldNames.Count(f => !e.Brief.ContainsKey(f) && o.Brief.TryGetValue(f, out string? v) && !string.IsNullOrWhiteSpace(v));

        var lint = e.Lint.ToDictionary(r => r, r => o.LintIds.Contains(r));

        bool clarifyGoal = goal.EffectiveRoute == "clarify_first";
        bool askedAny = o.Questions > 0;
        bool policyOk = clarifyGoal ? askedAny && o.Questions <= 2 : !askedAny;
        bool unneeded = !clarifyGoal && askedAny;

        bool? paid = e.Paid ? PaidWordsRx.IsMatch(reply) : null;

        bool unrequestedPaid = false;
        if (o.HasProposal)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowedKeys = new List<string?> { e.TemplateKey };
            allowedKeys.AddRange(e.Alternates);
            foreach (WorkflowTemplateDefinition tpl in WorkflowTemplateCatalog.GetAll()
                         .Where(x => allowedKeys.Contains(x.Key, StringComparer.OrdinalIgnoreCase)))
                foreach (var s in tpl.Steps) allowed.Add(s.StepType.ToString());
            if (e.Brief.TryGetValue("voice", out JsonElement v) && v.GetString() is "narrated" or "project-voice")
                allowed.Add("Voiceover");
            unrequestedPaid = o.ProposalStepTypes.Any(s => PaidStepTypes.Contains(s) && !allowed.Contains(s));
        }

        return new EvalSampleScore(routeOk, templateOk, fields, extras, lint, policyOk, unneeded, paid,
            o.HallucinatedKeys.Count > 0, unrequestedPaid, o.IdShown);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static bool FieldMatches(string field, JsonElement want, string got, IReadOnlyDictionary<string, string> brief)
    {
        got = got.Trim();
        switch (field)
        {
            case "lengthSec":
                return double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out double g)
                    && want.TryGetDouble(out double w) && w > 0 && Math.Abs(g - w) / w <= 0.10;
            case "format":
                return string.Equals(NormalizeFormat(want.GetString() ?? ""), NormalizeFormat(got), StringComparison.OrdinalIgnoreCase);
            case "voice":
                return (want.GetString() ?? "").ToLowerInvariant() switch
                {
                    "project-voice" => got.Equals("narrated", StringComparison.OrdinalIgnoreCase) && brief.ContainsKey("voiceId"),
                    var x => x.Equals(got, StringComparison.OrdinalIgnoreCase),
                };
            default:
                return string.Equals(want.ValueKind == JsonValueKind.String ? want.GetString() : want.ToString(), got, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Landscape 16:9, Vertical 9:16, Portrait 4:5 and Square 1:1 are four different formats.</summary>
    public static string NormalizeFormat(string s) => s.Trim().ToLowerInvariant() switch
    {
        "16:9" or "landscape" or "horizontal" => "landscape",
        "9:16" or "vertical" => "vertical",
        "4:5" or "portrait" => "portrait",
        "1:1" or "square" => "square",
        var x => x,
    };

    private static void MergeBrief(JsonElement args, Dictionary<string, string> into)
    {
        if (TryString(args, "outputFormat") is { Length: > 0 } fmt) into["format"] = fmt;
        if (TryString(args, "outputResolution") is { Length: > 0 } res) into["resolution"] = res;
        if (TryString(args, "stylePresetKey") is { Length: > 0 } sp) into["stylePreset"] = sp;
        if (!args.TryGetProperty("brief", out JsonElement b)) return;
        if (b.ValueKind == JsonValueKind.String) b = Parse(b.GetString() ?? "");
        if (b.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty p in b.EnumerateObject())
        {
            string name = p.Name;
            if (string.Equals(name, "voice", StringComparison.OrdinalIgnoreCase))
            {
                string? mode = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()
                    : p.Value.ValueKind == JsonValueKind.Object ? TryString(p.Value, "mode") : null;
                if (!string.IsNullOrEmpty(mode)) into["voice"] = mode;
                if (p.Value.ValueKind == JsonValueKind.Object && TryString(p.Value, "projectVoiceId") is { Length: > 0 } vid) into["voiceId"] = vid;
                continue;
            }
            string? val = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                _ => null,
            };
            string? canon = BriefFieldNames.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            if (canon is not null && !string.IsNullOrWhiteSpace(val)) into[canon] = val!;
        }
    }

    /// <summary>All the step's *ConfigJson values, canonicalised, so two steps compare by what they configure.</summary>
    private static string ConfigBlob(JsonElement step)
    {
        var parts = new List<string>();
        foreach (JsonProperty p in step.EnumerateObject().Where(p => p.Name.EndsWith("ConfigJson", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                parts.Add(p.Name.ToLowerInvariant() + "=" + Canonical(p.Value.GetString()!));
        return string.Join(";", parts);
    }

    private static string TemplateConfigBlob(WorkflowTemplateStepDefinition s)
    {
        var parts = new List<string>();
        foreach (var prop in s.GetType().GetProperties().Where(p => p.Name.EndsWith("ConfigJson", StringComparison.Ordinal)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            if (prop.GetValue(s) is string json && !string.IsNullOrWhiteSpace(json))
                parts.Add(prop.Name.ToLowerInvariant() + "=" + Canonical(json));
        return string.Join(";", parts);
    }

    // Inputs a template declares for the customer to fill in (the clip, the content, the object to track):
    // filling them is using the template, not editing it, so they are ignored when comparing configurations.
    private static readonly HashSet<string> DeclaredInputKeys = new(
        ["placeholders", "insertContentRef", "insertContentProjectFileId", "allowSourceMediaEgress"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeclaredItemKeys = new(["label", "target"], StringComparer.OrdinalIgnoreCase);

    private static string Canonical(string json)
    {
        try
        {
            System.Text.Json.Nodes.JsonNode? node = System.Text.Json.Nodes.JsonNode.Parse(json);
            StripDeclaredInputs(node, inItemList: false);
            return node?.ToJsonString() ?? "";
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static void StripDeclaredInputs(System.Text.Json.Nodes.JsonNode? node, bool inItemList)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o:
                foreach (string k in o.Select(p => p.Key).ToList())
                {
                    // Only the clip a source points at is a declared input; the source kind and step wiring are the template's.
                    if (k.Equals("source", StringComparison.OrdinalIgnoreCase) && o[k] is System.Text.Json.Nodes.JsonObject src)
                    {
                        foreach (string sk in src.Select(p => p.Key).ToList())
                            if (sk.Equals("projectFileId", StringComparison.OrdinalIgnoreCase)) src.Remove(sk);
                        StripDeclaredInputs(src, inItemList: false);
                    }
                    else if (DeclaredInputKeys.Contains(k) || (inItemList && DeclaredItemKeys.Contains(k))) o.Remove(k);
                    else StripDeclaredInputs(o[k], k is "trackObjects" or "regionEffects");
                }
                break;
            case System.Text.Json.Nodes.JsonArray arr:
                foreach (System.Text.Json.Nodes.JsonNode? item in arr) StripDeclaredInputs(item, inItemList);
                break;
        }
    }

    private sealed record Match3(string? Key, double Ratio, bool Exact, List<string> Tied);

    /// <summary>
    /// The template the proposal is closest to, by (agent type, step type) per step. Ties are broken by how many steps
    /// carry the template's own configuration, never by catalog order; the keys still tied are returned so the
    /// scorer can accept any of them that the fixture lists as correct.
    /// </summary>
    private static Match3 BestMatch(
        IReadOnlyList<string> agents, IReadOnlyList<string> stepTypes, IReadOnlyList<string> configs, IReadOnlyList<string> candidates)
    {
        IEnumerable<WorkflowTemplateDefinition> pool = candidates.Count > 0
            ? WorkflowTemplateCatalog.GetAll().Where(t => candidates.Contains(t.Key, StringComparer.OrdinalIgnoreCase))
            : WorkflowTemplateCatalog.GetAll();
        var proposal = agents.Select((a, i) => a + "|" + stepTypes[i]).ToList();
        var scored = new List<(string Key, double Ratio, int ConfigHits, bool Exact)>();
        foreach (WorkflowTemplateDefinition t in pool)
        {
            var seq = t.Steps.Select(s => s.AgentType + "|" + s.StepType).ToList();
            int lcs = Lcs(proposal, seq);
            double ratio = proposal.Count == 0 && seq.Count == 0 ? 1 : (double)lcs / Math.Max(proposal.Count, seq.Count);
            int hits = 0;
            bool sameSeq = proposal.SequenceEqual(seq);
            if (sameSeq)
                for (int i = 0; i < seq.Count; i++)
                    if (configs[i] == TemplateConfigBlob(t.Steps[i])) hits++;
            scored.Add((t.Key, ratio, hits, sameSeq && hits == seq.Count));
        }
        if (scored.Count == 0) return new Match3(null, 0, false, []);
        double best = scored.Max(x => x.Ratio);
        var top = scored.Where(x => Math.Abs(x.Ratio - best) < 1e-9).ToList();
        int bestHits = top.Max(x => x.ConfigHits);
        var winners = top.Where(x => x.ConfigHits == bestHits).ToList();
        return new Match3(winners[0].Key, best, winners.Any(w => w.Exact), winners.Select(w => w.Key).ToList());
    }

    private static int Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
            for (int j = 1; j <= b.Count; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[a.Count, b.Count];
    }

    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json).RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    private static string? TryString(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out JsonElement v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;
}

/// <summary>Majority and interval arithmetic for the aggregate report.</summary>
internal static class EvalStats
{
    /// <summary>A goal passes a metric when at least two of three samples do (strict majority of n).</summary>
    public static bool Majority(int passes, int n) => n > 0 && passes * 2 > n;

    /// <summary>95 % Wilson score interval for k successes of n.</summary>
    public static (double Low, double High) Wilson(int k, int n)
    {
        if (n == 0) return (0, 0);
        const double z = 1.959964;
        double p = (double)k / n;
        double denom = 1 + z * z / n;
        double centre = (p + z * z / (2 * n)) / denom;
        double half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / denom;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }
}
