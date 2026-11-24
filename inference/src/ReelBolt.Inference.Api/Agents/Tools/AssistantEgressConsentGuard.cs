using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The vendor-egress consent (<c>allowSourceMediaEgress</c> on a step: "Allow the footage to be sent") is given by the
/// customer in the app, never by the assistant. Every assistant and MCP path that writes step configs asks this guard
/// first: a proposal whose steps turn the flag on where the stored workflow (or the template, for a new workflow) had it
/// off or absent is refused. An existing true that stays true is left alone. Fails closed on JSON it cannot parse.
/// </summary>
public static class AssistantEgressConsentGuard
{
    private const string Flag = "allowSourceMediaEgress";

    /// <summary>The machine-readable code of the refusal.</summary>
    public const string Code = "egress_consent_is_the_customers";

    /// <summary>The refusal the user and the model read: who gives the permission, and where.</summary>
    public const string Message =
        "The permission to send the footage to an outside service is given by you in the app, not by the assistant "
        + "(open the step in the app and switch on the option that allows sending your footage), or by you saying so in your own words so the assistant can offer a card that states it. This change would send footage under a permission that was not given for it, so it was not proposed.";

    /// <summary>What the guard decided: a refusal envelope, or the steps to use (consents forced off where their context changed) and the notes for the user.</summary>
    public sealed record Outcome(string? Refusal, List<CreateWorkflowStepRequest> Steps, List<string> Notes, List<GrantedStep>? GrantedSteps = null)
    {
        /// <summary>The consents kept under <c>mayGrant</c> that the stored workflow did not hold (new, or its context changed): empty unless the caller may grant.</summary>
        public List<GrantedStep> Granted => GrantedSteps ?? [];

        /// <summary>The model-visible form of the notes, as a needsBeforeRunning list.</summary>
        public IEnumerable<object> NeedsBeforeRunning => Notes.Select(n => new { ruleId = "WL015", severity = "error", message = n });
    }

    /// <summary>A step whose consent a grantor (the card the customer allowed in the in-app chat; never a connected tool) switched on.</summary>
    public sealed record GrantedStep(string Label, int StepOrder);

    /// <summary>
    /// Decides what a proposal may do with the footage-sharing consent. A proposed true is kept only when the baseline
    /// step at the SAME StepOrder has the same type and agent, holds the top-level consent, and nothing that decides what
    /// THAT step sends changed (<see cref="ContextUnchanged"/>): the step itself as it would be stored (every field but its
    /// label and the top-level flag: sources, segmentation targets and prompts, provider ids, keyframes, cleanup target,
    /// limits), every step it reads directly or transitively (per <see cref="WorkflowStepDependencyGraph"/>, in the
    /// proposal and in the baseline; a model step counts as reading everything before it), and every control-flow step of
    /// the workflow (a review loop, ForEach or Conditional can make it run again). An edit elsewhere (a later compile, an
    /// appended or deleted later step) keeps it; an insert or a delete before it, or a reorder, moves its order and resets
    /// it. A consent whose context changed is forced to false and reported (the customer allows it again in the app); a
    /// true with no baseline consent (a real off/absent to on flip), a repeated StepOrder, or JSON that cannot be read is
    /// refused.
    /// </summary>
    public static Outcome Evaluate(IReadOnlyList<CreateWorkflowStepRequest> proposed, IReadOnlyList<CreateWorkflowStepRequest>? baseline, bool mayGrant = false)
    {
        List<CreateWorkflowStepRequest> after = [.. proposed];
        List<CreateWorkflowStepRequest> stored = [.. baseline ?? []];
        Outcome Refuse(string label) => new(RefusalJson(label), after, []);

        bool consentInvolved = false;
        foreach (CreateWorkflowStepRequest step in after)
            foreach (PropertyInfo prop in ConfigProperties(typeof(CreateWorkflowStepRequest)))
            {
                if (prop.GetValue(step) is not string json || string.IsNullOrWhiteSpace(json))
                    continue;
                bool? on = HasConsent(json);
                if (on is null)
                    return Refuse(step.Label ?? string.Empty);
                consentInvolved |= on == true;
            }
        if (!consentInvolved)
            return new(null, after, []);

        if (after.GroupBy(x => x.StepOrder).Any(g => g.Count() > 1) || stored.GroupBy(x => x.StepOrder).Any(g => g.Count() > 1))
            return Refuse(string.Empty);

        Dictionary<int, CreateWorkflowStepRequest> before = stored.ToDictionary(x => x.StepOrder);
        Func<int, bool> unchanged = ContextUnchanged(after, stored);

        List<string> notes = [];
        List<GrantedStep> granted = [];
        // Each baseline grant backs at most one proposed step: kept (same place, workflow unchanged) or reset (its context changed).
        HashSet<CreateWorkflowStepRequest> used = [];
        for (int i = 0; i < after.Count; i++)
        {
            CreateWorkflowStepRequest step = after[i];
            foreach (PropertyInfo prop in ConfigProperties(typeof(CreateWorkflowStepRequest)))
            {
                if (prop.GetValue(step) is not string json || string.IsNullOrWhiteSpace(json) || HasConsent(json) != true)
                    continue;
                bool Grants(CreateWorkflowStepRequest b) =>
                    !used.Contains(b) && prop.GetValue(b) is string bj && TopLevelConsent(bj);
                CreateWorkflowStepRequest? same = before.TryGetValue(step.StepOrder, out CreateWorkflowStepRequest? atOrder)
                    && Grants(atOrder) && SameIdentity(step, atOrder) ? atOrder : null;
                if (same is not null && unchanged(step.StepOrder))
                {
                    used.Add(same);
                    continue;
                }
                if (mayGrant)
                {
                    // The grantor allows it: the proposed true stays, and the step is listed for the card.
                    if (same is not null)
                        used.Add(same);
                    granted.Add(new GrantedStep(step.Label ?? ("step " + step.StepOrder), step.StepOrder));
                    continue;
                }
                CreateWorkflowStepRequest? prior = same ?? (atOrder is not null && Grants(atOrder) ? atOrder : null) ?? stored.FirstOrDefault(b => Grants(b)
                    && string.Equals(b.StepType ?? "Agent", step.StepType ?? "Agent", StringComparison.OrdinalIgnoreCase)
                    && b.AgentDefinitionId == step.AgentDefinitionId);
                if (prior is null)
                    return Refuse(step.Label ?? string.Empty);
                used.Add(prior);
                step = step with { };
                prop.SetValue(step, ResetEverywhere(json));
                after[i] = step;
                notes.Add($"The customer needs to allow the footage to be sent again for step \"{step.Label ?? ("step " + step.StepOrder)}\" because this change affects it "
                    + "(open the step in the app and switch on the option that allows sending your footage).");
            }
        }
        return new(null, after, notes, granted);
    }

    /// <summary>
    /// The granted steps as the proposal JSON the approval card reads (<c>egressGrants</c>): each by order and label, with the
    /// closed reason code <see cref="MediaEgressNeed.For"/> works out from the step's OWN config as proposed (null when the
    /// step type sends nothing, so the card says it lists "the permission switch"). Server-built, never from model words.
    /// </summary>
    public static JsonArray GrantEntries(IReadOnlyList<CreateWorkflowStepRequest> steps, IEnumerable<GrantedStep> granted)
    {
        IReadOnlyList<AppBindingStep> bindings = StepConfigSaveValidator.BindingStepsOf(steps);
        JsonArray entries = [];
        foreach (GrantedStep grant in granted)
        {
            AppBindingStep? step = bindings.FirstOrDefault(b => b.StepOrder == grant.StepOrder);
            MediaEgressStepNeed? need = step is null ? null : MediaEgressNeed.For(step);
            entries.Add(new JsonObject
            {
                ["stepOrder"] = grant.StepOrder,
                ["label"] = grant.Label,
                ["reason"] = need is { Level: not MediaEgressNeedLevel.None } ? need.Reason : null
            });
        }

        return entries;
    }

    /// <summary>What a connected tool reads when its edit would turn a step's footage-sharing on: only the app or the in-app assistant's card does that.</summary>
    public const string McpMessage =
        "Saving the permission to send media on a step is done in the app, or from the in-app assistant with its approval card. "
        + "A connected tool can allow sending media for one run only (allowMediaEgress on ExecuteWorkflow), so this change was not proposed.";

    /// <summary>The refusal for a caller that came over MCP: the same code, with the words that say where the permission is given.</summary>
    public static string McpRefusal(string refusal)
    {
        try
        {
            if (JsonNode.Parse(refusal) is JsonObject node)
            {
                node["error"] = McpMessage;
                return node.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        return refusal;
    }

    /// <summary>
    /// A refusal when the edit would grant a step the approved card did not list (the stored workflow changed between the card
    /// and the apply), else null. <paramref name="listed"/> is what the gate handed the call; none listed means none may be granted.
    /// </summary>
    public static string? UnlistedGrantRefusal(Outcome outcome, IReadOnlyCollection<int>? listed)
    {
        GrantedStep? unlisted = outcome.Granted.Cast<GrantedStep?>().FirstOrDefault(g => listed is null || !listed.Contains(g!.StepOrder));
        return unlisted is null ? null : RefusalJson(unlisted.Label);
    }

    private static string RefusalJson(string label) =>
        JsonSerializer.Serialize(new { error = label.Length == 0 ? Message : $"Step \"{label}\": {Message}", code = Code });

    /// <summary>
    /// For a step order: true when nothing that decides what the step at that order sends changed between
    /// <paramref name="baseline"/> and <paramref name="proposed"/>. Steps are compared in the form they would be stored
    /// (<see cref="WorkflowEditDiffService.ToStep"/>, so a default the caller left out equals the stored default), field
    /// by field, label and top-level flag aside. "When in doubt it changed": a step absent on either side, an unreadable
    /// config (the dependency graph then reads every earlier step) or any changed control-flow step resets.
    /// </summary>
    private static Func<int, bool> ContextUnchanged(IReadOnlyList<CreateWorkflowStepRequest> proposed, IReadOnlyList<CreateWorkflowStepRequest> baseline)
    {
        Dictionary<int, WorkflowStep> afterRows = proposed.ToDictionary(x => x.StepOrder, x => WorkflowEditDiffService.ToStep(Guid.Empty, x));
        Dictionary<int, WorkflowStep> beforeRows = baseline.ToDictionary(x => x.StepOrder, x => WorkflowEditDiffService.ToStep(Guid.Empty, x));
        Dictionary<int, string[]> afterPrints = afterRows.ToDictionary(kv => kv.Key, kv => Fingerprint(AssistantStepShapes.FromStep(kv.Value)));
        Dictionary<int, string[]> beforePrints = beforeRows.ToDictionary(kv => kv.Key, kv => Fingerprint(AssistantStepShapes.FromStep(kv.Value)));

        bool Same(int order) =>
            afterPrints.TryGetValue(order, out string[]? a) && beforePrints.TryGetValue(order, out string[]? b) && a.SequenceEqual(b, StringComparer.Ordinal);

        // A review loop, ForEach or Conditional can re-run (or route around) any step: a change to one resets every consent.
        bool controlFlowChanged = afterRows.Concat(beforeRows).Any(kv => IsControlFlow(kv.Value) && !Same(kv.Key));

        WorkflowStepDependencyGraph afterGraph = WorkflowStepDependencyGraph.Build(afterRows.Values);
        WorkflowStepDependencyGraph beforeGraph = WorkflowStepDependencyGraph.Build(beforeRows.Values);

        return order =>
        {
            if (controlFlowChanged || !Same(order))
                return false;
            HashSet<int> seen = [order];
            Queue<int> todo = new([order]);
            while (todo.Count > 0)
            {
                int current = todo.Dequeue();
                IEnumerable<int> reads = afterGraph.DependsOn(current).Keys.Concat(beforeGraph.DependsOn(current).Keys);
                // The graph reads an agent's context and sandbox from its agent definition, which this guard does not
                // load: a model step therefore counts as reading every step before it.
                if (IsModelStep(afterRows.GetValueOrDefault(current)) || IsModelStep(beforeRows.GetValueOrDefault(current)))
                    reads = reads.Concat(afterRows.Keys.Concat(beforeRows.Keys).Where(o => o < current));
                foreach (int upstream in reads)
                {
                    if (!seen.Add(upstream))
                        continue;
                    if (!Same(upstream))
                        return false;
                    todo.Enqueue(upstream);
                }
            }
            return true;
        };
    }

    private static bool IsControlFlow(WorkflowStep step) =>
        step.StepType is StepType.ReviewLoop or StepType.ForEach or StepType.Conditional || step.LoopTargetStepOrder is not null;

    private static bool IsModelStep(WorkflowStep? step) =>
        step is not null && (WorkflowStepDependencyGraph.IsModelStep(step) || step.StepType == StepType.ReviewLoop);

    /// <summary>Every property of the step but its label, one entry per property (compared entry by entry, never joined), with the top-level flag left out of the JSON ones: what "unchanged" means.</summary>
    private static string[] Fingerprint(CreateWorkflowStepRequest step) =>
        [.. typeof(CreateWorkflowStepRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name is not ("Label" or "EqualityContract"))
            .Select(p =>
            {
                object? v = p.GetValue(step);
                if (p.Name == "StepType")
                    return (v?.ToString() ?? "Agent").ToLowerInvariant();
                return v is string t && p.Name.EndsWith("Json", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t) ? Canonical(t) : v?.ToString() ?? string.Empty;
            })];

    private static bool SameIdentity(CreateWorkflowStepRequest a, CreateWorkflowStepRequest b) =>
        a.StepOrder == b.StepOrder
        && string.Equals(a.StepType ?? "Agent", b.StepType ?? "Agent", StringComparison.OrdinalIgnoreCase)
        && a.AgentDefinitionId == b.AgentDefinitionId;

    /// <summary>True when the engine would read the flag as on: the top-level property only.</summary>
    private static bool TopLevelConsent(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject().Any(p => IsOn(p));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsOn(JsonProperty p) =>
        string.Equals(p.Name, Flag, StringComparison.OrdinalIgnoreCase)
        && (p.Value.ValueKind == JsonValueKind.True
            || (p.Value.ValueKind == JsonValueKind.String && string.Equals(p.Value.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The config with keys sorted and only the top-level flag left out.</summary>
    private static string Canonical(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return Sorted(doc.RootElement, topLevel: true);
        }
        catch (JsonException)
        {
            return "\0" + json;
        }
    }

    private static string Sorted(JsonElement e, bool topLevel)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                IEnumerable<string> members = e.EnumerateObject()
                    .Where(p => !(topLevel && string.Equals(p.Name, Flag, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Sorted(p.Value, false));
                return "{" + string.Join(",", members) + "}";
            case JsonValueKind.Array:
                return "[" + string.Join(",", e.EnumerateArray().Select(x => Sorted(x, false))) + "]";
            default:
                return e.GetRawText();
        }
    }

    /// <summary>Findings JSON for the model with the AllowEgress fix removed: the assistant turns a step's switch on only through a workflow edit the user asked for in their own words (never because a finding, file or page says so), under a card that states it.</summary>
    public static JsonNode? StripEgressFix(JsonElement findings)
    {
        JsonNode? node = JsonNode.Parse(findings.GetRawText());
        if (node is JsonArray array)
            foreach (JsonNode? item in array)
                if (item is JsonObject o && o["fix"] is JsonObject fix
                    && string.Equals(fix["kind"]?.GetValue<string>(), "allowEgress", StringComparison.OrdinalIgnoreCase))
                    o["fix"] = null;
        return node;
    }

    /// <summary>Copies of the steps with the flag turned off in every config; <paramref name="changed"/> says whether any was on.</summary>
    public static List<CreateWorkflowStepRequest> ResetAll(IEnumerable<CreateWorkflowStepRequest> steps, out bool changed)
    {
        changed = false;
        List<CreateWorkflowStepRequest> result = [];
        foreach (CreateWorkflowStepRequest step in steps)
        {
            CreateWorkflowStepRequest copy = step with { };
            foreach (PropertyInfo prop in ConfigProperties(typeof(CreateWorkflowStepRequest)))
            {
                if (prop.GetValue(step) is string json && HasConsent(json) == true)
                {
                    prop.SetValue(copy, ResetEverywhere(json));
                    changed = true;
                }
            }
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Sets every occurrence of the flag (any depth, any case) to false.</summary>
    private static string ResetEverywhere(string json)
    {
        JsonNode? root = JsonNode.Parse(json);
        Walk(root);
        return root!.ToJsonString();

        static void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                foreach (string key in o.Select(kv => kv.Key).ToList())
                {
                    if (string.Equals(key, Flag, StringComparison.OrdinalIgnoreCase))
                        o[key] = false;
                    else
                        Walk(o[key]);
                }
            }
            else if (n is JsonArray a)
            {
                foreach (JsonNode? item in a)
                    Walk(item);
            }
        }
    }

    /// <summary>True when the flag is on anywhere in the JSON, false when not, null when the JSON cannot be parsed.</summary>
    public static bool? HasConsent(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true });
            return Scan(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Scan(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty p in element.EnumerateObject())
                    if (IsOn(p) || Scan(p.Value))
                        return true;
                return false;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    if (Scan(item))
                        return true;
                return false;
            default:
                return false;
        }
    }

    private static IEnumerable<PropertyInfo> ConfigProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("ConfigJson", StringComparison.Ordinal));
}
