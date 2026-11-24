using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Shared.Workflows;

/// <summary>One thing a step needs. <c>LintRule</c> is the linter rule id that checks it, or null for a plain statement.</summary>
public sealed record StepCardNeed(string Kind, string Description, string? LintRule);

/// <summary>What a step writes: the shape in words, the named output contract when there is one, and who reads it.</summary>
public sealed record StepCardProduces(string Shape, string? OutputContract, IReadOnlyList<string> ReadableBy);

/// <summary>A step that usually follows, and how it reads this one.</summary>
public sealed record StepCardPairing(string Next, string Via);

/// <summary>A mistake the linter catches on this step, and the kind of fix it offers.</summary>
public sealed record StepCardMistake(string RuleId, string Name, string Fix);

/// <summary>
/// One setting of a step an app input may fill (docs/design/app-inputs.md 7.1), reflected from
/// <see cref="AppBindableAttribute"/>. <see cref="Mode"/> is <c>placeholder</c> (write <c>{{input.id}}</c> inside the
/// text) or <c>value</c> (add the setting to the step's <c>appInputs</c> map); <see cref="Types"/> are input type wire names.
/// <see cref="ValueKind"/> (appended, docs/design/app-inputs.md 27) is the JSON kind the setting holds (<c>text</c>,
/// <c>number</c>, <c>boolean</c>): what a switch or a choice may list for it in a mapped binding.
/// </summary>
public sealed record StepCardBindableField(
    string Path,
    string Mode,
    IReadOnlyList<string> Types,
    string Label,
    double? Min = null,
    double? Max = null,
    int? MaxLength = null,
    IReadOnlyList<string>? Choices = null,
    string? GovernedByBriefField = null,
    IReadOnlyList<string>? Capabilities = null,
    string ValueKind = "text");

/// <summary>
/// One step type's card (docs/design/workflow-apps.md section 7.1). <see cref="ConfigFields"/> is the config's JSON
/// Schema (null for a step type without a config record, or when the card is listed with the others).
/// Prices are never part of a card: only the cost class.
/// </summary>
public sealed record StepCard(
    string StepType,
    string Purpose,
    string CostClass,
    IReadOnlyList<StepCardNeed> Needs,
    StepCardProduces Produces,
    IReadOnlyList<string> ReadsFromRun,
    IReadOnlyList<StepCardPairing> PairsWith,
    IReadOnlyList<StepCardMistake> CommonMistakes,
    JsonNode? ConfigFields,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<StepCardBindableField>? BindableFields = null);

/// <summary>What a recipe contains, from <see cref="WorkflowTemplateCatalog"/>: names and classes only.</summary>
public sealed record TemplateFact(
    string Key,
    string Name,
    string Description,
    string CostClass,
    IReadOnlyList<string> StepTypes,
    IReadOnlyList<string> AgentTypes,
    IReadOnlyList<string> BriefFieldsRead);

/// <summary>
/// The step catalog, generated from code (docs/design/workflow-apps.md section 7.1). The hand-written part is the
/// <see cref="StepCardAttribute"/> on each config type (or on the <see cref="StepType"/> member when there is no
/// config record); everything else is reflected from the single sources of truth: the config schemas
/// (<see cref="StepConfigSchemaExporter"/>), <see cref="StepCompatibilityRules"/> (the linter's own rule table),
/// <see cref="BriefFieldConsumers"/> and <see cref="AppCostClass"/>. A drift-guard test fails the build when a
/// <see cref="StepType"/> has no card. No price appears anywhere here: cost class only.
/// </summary>
public static class StepCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<StepType, StepCardAttribute>> Attributes = new(LoadAttributes);

    /// <summary>The hand-written attribute for a step type, or null when nobody wrote one (the drift guard fails then).</summary>
    public static StepCardAttribute? AttributeFor(StepType stepType) =>
        Attributes.Value.TryGetValue(stepType, out StepCardAttribute? a) ? a : null;

    /// <summary>The cost class derived from the step type (the class every app card shows).</summary>
    public static string DeriveCostClass(StepType stepType) => AppCostClass.Derive([new AppStepFacts(stepType)]);

    /// <summary>The retrieval filter key (<c>free</c>, <c>credits</c>, <c>voice</c>, <c>generation</c>) for a cost class.</summary>
    public static string CostClassKey(string costClass) => costClass switch
    {
        AppCostClass.NoCredits => "free",
        AppCostClass.UsesCredits => "credits",
        AppCostClass.UsesCreditsAndVoice => "voice",
        AppCostClass.UsesCreditsAndAiShots => "generation",
        _ => "credits"
    };

    /// <summary>
    /// One card per <see cref="StepType"/>, in enum order. Config schemas and the app-bindable fields
    /// (docs/design/app-inputs.md 7.1) are included only when asked for; the assistant's <c>GetStepCatalog</c> starts
    /// asking for the bindable fields in lane L8, once app inputs can be saved.
    /// </summary>
    public static IReadOnlyList<StepCard> GetAll(bool includeConfigFields = false, bool includeBindableFields = false)
    {
        StepConfigSchemaExporter exporter = new();
        return Enum.GetValues<StepType>().Select(t => Build(t, includeConfigFields ? exporter : null, includeBindableFields)).ToList();
    }

    /// <summary>The card of one step type, with its config schema (and its app-bindable fields when asked for).</summary>
    public static StepCard Get(StepType stepType, bool includeBindableFields = false) =>
        Build(stepType, new StepConfigSchemaExporter(), includeBindableFields);

    /// <summary>The recipe facts, one per template, in catalog order.</summary>
    public static IReadOnlyList<TemplateFact> GetTemplateFacts() =>
        WorkflowTemplateCatalog.GetAll().Where(t => AppStepFacts.PersonAvatarsEnabled || !WorkflowTemplateCatalog.IsAvatarTemplate(t.Key)).Select(t =>
        {
            IReadOnlyList<AppStepFacts> facts = AppStepFacts.FromTemplate(t);
            return new TemplateFact(
                t.Key,
                t.Name,
                t.Description,
                AppCostClass.Derive(facts),
                t.Steps.Select(s => s.StepType.ToString()).Distinct().ToList(),
                t.Steps.Where(s => s.StepType == StepType.Agent).Select(s => s.AgentType.ToString()).Distinct().ToList(),
                BriefFieldConsumers.FieldsReadBy(facts).OrderBy(f => f, StringComparer.Ordinal).ToList());
        }).ToList();

    /// <summary>The plain text of a step's generated-fact chunk (what retrieval indexes).</summary>
    public static string FactText(StepCard card)
    {
        StringBuilder sb = new();
        sb.Append(card.StepType).Append(" step. ").Append(card.Purpose).Append('\n');
        sb.Append("Cost: ").Append(card.CostClass).Append(".\n");
        foreach (StepCardNeed need in card.Needs)
            sb.Append("Needs: ").Append(need.Description).Append(need.LintRule is null ? "" : $" (check {need.LintRule})").Append('\n');
        sb.Append("Produces: ").Append(card.Produces.Shape);
        if (card.Produces.OutputContract is not null)
            sb.Append(" (contract ").Append(card.Produces.OutputContract).Append(')');
        sb.Append('\n');
        if (card.Produces.ReadableBy.Count > 0)
            sb.Append("Read by: ").AppendJoin(", ", card.Produces.ReadableBy).Append('\n');
        if (card.ReadsFromRun.Count > 0)
            sb.Append("Reads from the run brief: ").AppendJoin(", ", card.ReadsFromRun).Append('\n');
        foreach (StepCardPairing p in card.PairsWith)
            sb.Append("Pairs with ").Append(p.Next).Append(": ").Append(p.Via).Append('\n');
        foreach (StepCardMistake m in card.CommonMistakes)
            sb.Append("Common mistake ").Append(m.RuleId).Append(' ').Append(m.Name).Append(", fix: ").Append(m.Fix).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>The plain text of a recipe's generated-fact chunk.</summary>
    public static string FactText(TemplateFact t)
    {
        StringBuilder sb = new();
        sb.Append("Recipe ").Append(t.Name).Append(" (key ").Append(t.Key).Append("). ").Append(t.Description).Append('\n');
        sb.Append("Cost: ").Append(t.CostClass).Append(".\n");
        sb.Append("Step types: ").AppendJoin(", ", t.StepTypes).Append('\n');
        if (t.AgentTypes.Count > 0)
            sb.Append("Agents: ").AppendJoin(", ", t.AgentTypes).Append('\n');
        if (t.BriefFieldsRead.Count > 0)
            sb.Append("Brief fields it reads: ").AppendJoin(", ", t.BriefFieldsRead).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static StepCard Build(StepType stepType, StepConfigSchemaExporter? exporter, bool includeBindableFields)
    {
        StepCardAttribute attr = AttributeFor(stepType)
            ?? throw new InvalidOperationException($"StepType {stepType} has no [StepCard]; add one on its config type or enum member.");

        List<StepCardNeed> needs = [];
        List<StepCardMistake> mistakes = [];
        foreach (StepCompatibilityRule rule in StepCompatibilityRules.All.Where(r => StepCompatibilityRules.AppliesTo(r.Id).Contains(stepType)))
        {
            foreach (string need in rule.Needs)
                needs.Add(new StepCardNeed(NeedKind(rule.FixKind), need, rule.Id));
            mistakes.Add(new StepCardMistake(rule.Id, rule.Name, FixSentence(rule.FixKind)));
        }
        needs.AddRange(attr.Needs.Select(n => new StepCardNeed("input", n, null)));

        IReadOnlyList<string> reads = BriefFieldConsumers.FieldsReadBy(new AppStepFacts(stepType))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();

        List<StepCardPairing> pairs = [];
        foreach (string pair in attr.PairsWith)
        {
            int i = pair.IndexOf(':', StringComparison.Ordinal);
            pairs.Add(i < 0 ? new StepCardPairing(pair.Trim(), string.Empty) : new StepCardPairing(pair[..i].Trim(), pair[(i + 1)..].Trim()));
        }

        JsonNode? schema = null;
        if (exporter is not null)
        {
            StepConfigSchemaResult result = exporter.GetSchema(stepType);
            schema = result.HasConfig ? result.Schema : null;
        }

        return new StepCard(
            stepType.ToString(),
            attr.Purpose,
            DeriveCostClass(stepType),
            needs,
            new StepCardProduces(attr.Produces, attr.OutputContract, attr.ReadableBy),
            reads,
            pairs,
            mistakes,
            schema,
            includeBindableFields ? BindableFieldsOf(stepType) : null);
    }

    /// <summary>The settings of a step type an app input may fill (empty for a type with none).</summary>
    public static IReadOnlyList<StepCardBindableField> BindableFieldsOf(StepType stepType) =>
    [
        .. AppBindableCatalog.For(stepType).Select(f => new StepCardBindableField(
            f.Path,
            f.Mode == AppBindingMode.Placeholder ? "placeholder" : "value",
            [.. f.Types.Select(AppInputTypes.WireName)],
            f.Label,
            f.Min,
            f.Max,
            f.MaxLength,
            f.Choices,
            f.GovernedByBriefField,
            f.Capabilities,
            f.ValueKind switch { AppBindableValueKind.Number => "number", AppBindableValueKind.Boolean => "boolean", _ => "text" }))
    ];

    /// <summary>The kind of thing a need is (what the author must have), derived from the rule's fix kind.</summary>
    internal static string NeedKind(LintFixKind fix) => fix switch
    {
        LintFixKind.RetargetReference or LintFixKind.AddStep => "step",
        LintFixKind.UploadFiles => "file",
        LintFixKind.ChooseProvider => "provider",
        LintFixKind.ChangePlanOrChoice => "plan",
        LintFixKind.ApproveDesignSystem => "designSystem",
        LintFixKind.ChooseVoice => "voice",
        LintFixKind.AllowEgress => "permission",
        LintFixKind.ChangeSetting => "setting",
        _ => "input"
    };

    internal static string FixSentence(LintFixKind fix) => fix switch
    {
        LintFixKind.RetargetReference => "Point the reference at the right earlier step",
        LintFixKind.ChangeSetting => "Change the step setting the message names",
        LintFixKind.AddStep => "Add the missing step",
        LintFixKind.UploadFiles => "Upload the missing media to the project",
        LintFixKind.ChooseProvider => "Connect or choose a provider",
        LintFixKind.ChangePlanOrChoice => "Choose a different plan, resolution or step",
        LintFixKind.ApproveDesignSystem => "Approve the project's design system",
        LintFixKind.ChooseVoice => "Pick a usable voice",
        LintFixKind.AllowEgress => "The customer gives the permission to send source media to the vendor, in the app",
        LintFixKind.RemoveReference => "Remove the reference that points at nothing",
        LintFixKind.FillInput => "Fill in the app field the message names, on the run page",
        LintFixKind.ChooseModel => "Choose another model",
        _ => "Read the message; no automatic fix"
    };

    private static IReadOnlyDictionary<StepType, StepCardAttribute> LoadAttributes()
    {
        Dictionary<StepType, StepCardAttribute> map = [];
        foreach (FieldInfo field in typeof(StepType).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetCustomAttribute<StepCardAttribute>() is { } a)
                map[(StepType)field.GetValue(null)!] = a;
        }
        foreach (Type type in typeof(StepType).Assembly.GetTypes())
        {
            if (type.Namespace != typeof(StepCatalog).Namespace || type.GetCustomAttribute<StepCardAttribute>() is not { } a)
                continue;
            string name = type.Name.EndsWith("StepConfig", StringComparison.Ordinal) ? type.Name[..^"StepConfig".Length] : type.Name;
            if (Enum.TryParse(name, out StepType stepType))
                map[stepType] = a;
        }
        return map;
    }
}
