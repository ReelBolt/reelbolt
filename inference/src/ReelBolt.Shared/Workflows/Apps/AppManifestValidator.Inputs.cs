using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The app-input rules of the manifest validator (docs/design/app-inputs.md 11.1): each message is a plain sentence
/// naming the input and, where there is one, the step. The model checks that need the organization (selectable,
/// priced, on the plan, serving the derived job) are lane L4's; here only the shape is checked.
/// </summary>
public static partial class AppManifestValidator
{
    /// <summary>The message refusing app inputs while the feature is off.</summary>
    public const string AppInputsOffMessage =
        "App inputs (kind \"input\") and the request template are not available yet. Remove them, or expose brief, files or notes inputs.";

    /// <summary>True when the manifest carries any member that only exists from version 2.</summary>
    public static bool UsesVersion2(AppManifest manifest) =>
        manifest.RequestTemplate is not null
        || (manifest.Inputs ?? []).Any(i => i.Kind == AppManifestVocabulary.KindInput || i.More is not null || HasTypedMembers(i)
            || i.LabelKey is not null);

    private static bool HasTypedMembers(AppManifestInput i) =>
        i.Id is not null || i.Type is not null || i.Help is not null || i.Example is not null || i.Required is not null
        || i.MaxLength is not null || i.Step is not null || i.Unit is not null || i.Options is not null || i.ShowWhen is not null
        || i.Capability is not null || i.Job is not null || i.Allowed is not null || i.Recommended is not null || i.Agent is not null
        || i.OnLabel is not null || i.OffLabel is not null || i.HelpKey is not null || i.ExampleKey is not null
        || i.Permission is not null;

    private static void ValidateAppInputs(AppManifest manifest, IReadOnlyList<AppBindingStep>? steps, List<string> errors)
    {
        List<AppManifestInput> typed = [.. (manifest.Inputs ?? []).Where(i => i.Kind == AppManifestVocabulary.KindInput)];
        if (steps is null)
        {
            if (typed.Count > 0 || manifest.RequestTemplate is not null)
                errors.Add(AppInputsOffMessage);
            return;
        }

        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, steps);
        // Problems inside step configs are the save validator's (StepConfigSaveValidator); the manifest's own are here.
        errors.AddRange(map.Problems.Where(p => p.StepOrder is null).Select(p => p.Message));

        if (manifest.RequestTemplate is { } template)
        {
            CheckText("The request template", template, AppInputTypes.MaxRequestTemplate, errors);
            if (string.IsNullOrWhiteSpace(AppInputPlaceholders.Literal(template)) && AppInputPlaceholders.Ids(template).Count == 0)
                errors.Add("The request template is empty.");
        }

        Dictionary<string, AppManifestInput> byId = new(StringComparer.Ordinal);
        foreach (AppManifestInput input in typed)
        {
            string name = DisplayName(input);
            if (input.Id is null || !AppInputPlaceholders.IdPattern().IsMatch(input.Id))
            {
                errors.Add($"The input \"{name}\" needs an id: a lower-case letter then letters and digits, at most 32 characters.");
                continue;
            }

            if (AppManifestVocabulary.BriefFields.Contains(input.Id) || input.Id is "notes" or "files" or "inputs")
                errors.Add($"The input id \"{input.Id}\" is a brief field name; choose another.");
            if (!byId.TryAdd(input.Id, input))
                errors.Add($"The input id \"{input.Id}\" is used twice.");
        }

        HashSet<string> exposedBriefFields = new((manifest.Inputs ?? [])
            .Where(i => i.Kind == AppManifestVocabulary.KindBrief && i.Field is not null).Select(i => i.Field!), StringComparer.Ordinal);

        foreach (AppManifestInput input in byId.Values)
            ValidateTypedInput(input, byId, map, steps, exposedBriefFields, errors);

        // Rule 1: every placeholder, binding and condition names a declared input.
        foreach (string id in map.Uses.Select(u => u.InputId).Distinct(StringComparer.Ordinal))
        {
            if (!byId.ContainsKey(id))
                errors.Add($"\"{id}\" is used ({Where(map.UsesOf(id)[0], steps)}) but the app declares no input with that id.");
        }
    }

    private static void ValidateTypedInput(
        AppManifestInput input,
        IReadOnlyDictionary<string, AppManifestInput> byId,
        AppBindingMap map,
        IReadOnlyList<AppBindingStep> steps,
        IReadOnlySet<string> exposedBriefFields,
        List<string> errors)
    {
        string name = DisplayName(input);
        string id = input.Id!;
        if (!AppInputTypes.TryParse(input.Type, out AppInputType type))
        {
            errors.Add($"The input \"{name}\" needs a type: {string.Join(", ", AppInputTypes.WireNames)}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(input.Label) && input.LabelKey is null)
            errors.Add($"The input \"{name}\" needs a label.");
        CheckText($"The help of \"{name}\"", input.Help, AppInputTypes.MaxHelp, errors);
        CheckText($"The example of \"{name}\"", input.Example, AppInputTypes.MaxExample, errors);
        CheckText($"The on label of \"{name}\"", input.OnLabel, AppManifestVocabulary.MaxLabel, errors);
        CheckText($"The off label of \"{name}\"", input.OffLabel, AppManifestVocabulary.MaxLabel, errors);

        CheckMembers(input, type, name, errors);
        CheckTypeShape(input, type, name, errors);

        IReadOnlyList<AppInputUse> uses = map.UsesOf(id);

        // Rule 3: every input is used somewhere.
        if (uses.Count == 0)
            errors.Add($"The input \"{name}\" is not used by any step, the request template or a condition. Use it or remove it.");

        foreach (AppInputUse use in uses)
            CheckUse(input, type, name, use, steps, exposedBriefFields, errors);

        // 4.3: a text input fits every placeholder target.
        if (AppInputTypes.IsText(type))
        {
            int effective = AppInputValueChecker.EffectiveMaxLength(input with { MaxLength = null }, map);
            if (effective < 1)
                errors.Add($"The input \"{name}\" has no room left: the text around it already fills the setting it goes into.");
            else if (input.MaxLength is { } own && own > effective)
                errors.Add($"The input \"{name}\" allows {own} characters, but where it is used only {effective} fit. Set its maximum to {effective} or less.");
        }

        // A default must be a valid value.
        if (input.Default is { ValueKind: not JsonValueKind.Null } def
            && !AppInputValueChecker.TryCanonical(input, def, AppInputValueChecker.EffectiveMaxLength(input, map), out _, out string? defaultError))
            errors.Add($"The default of \"{name}\" is not valid: {defaultError}");

        // Rule 5: showWhen.
        if (input.ShowWhen is { } when)
            CheckShowWhen(input, when, name, byId, map, steps, errors);
    }

    private static void CheckMembers(AppManifestInput input, AppInputType type, string name, List<string> errors)
    {
        void Refuse(bool present, string member)
        {
            if (present)
                errors.Add($"The input \"{name}\" ({AppInputTypes.WireName(type)}) cannot have {member}.");
        }

        Refuse(input.MaxLength is not null && !AppInputTypes.IsText(type), "a maximum length");
        Refuse((input.Min is not null || input.Max is not null) && type is not (AppInputType.Number or AppInputType.Duration), "min or max");
        Refuse((input.Step is not null || input.Unit is not null) && type != AppInputType.Number, "a step or unit");
        Refuse(input.Options is not null && type != AppInputType.Choice, "options");
        Refuse(input.Choices is not null && type is not (AppInputType.Duration or AppInputType.Shape), "choices");
        Refuse((input.OnLabel is not null || input.OffLabel is not null) && type != AppInputType.Toggle, "on/off labels");
        Refuse((input.Capability is not null || input.Job is not null || input.Allowed is not null || input.Recommended is not null || input.Agent is not null)
            && type != AppInputType.Model, "model settings");
    }

    private static void CheckTypeShape(AppManifestInput input, AppInputType type, string name, List<string> errors)
    {
        switch (type)
        {
            case AppInputType.ShortText:
            case AppInputType.LongText:
                if (input.MaxLength is { } max && (max < 1 || max > AppInputTypes.TextCap(type)))
                    errors.Add($"The maximum length of \"{name}\" must be between 1 and {AppInputTypes.TextCap(type)}.");
                break;
            case AppInputType.Number:
            case AppInputType.Duration:
                if (input.Min is { } min && input.Max is { } max2 && min > max2)
                    errors.Add($"The minimum of \"{name}\" is above its maximum.");
                if (type == AppInputType.Duration && (input.Min is { } dmin && (dmin < 1 || dmin != Math.Floor(dmin)) || input.Max is { } dmax && dmax != Math.Floor(dmax)))
                    errors.Add($"\"{name}\" counts whole seconds, at least 1.");
                if (input.Step is <= 0)
                    errors.Add($"The step of \"{name}\" must be above 0.");
                if (input.Unit is { } unit && !AppInputTypes.Units.Contains(unit))
                    errors.Add($"The unit of \"{name}\" must be one of: {string.Join(", ", AppInputTypes.Units)}.");
                if (type == AppInputType.Duration && input.Choices is { } durations
                    && (durations.Count is 0 or > AppInputTypes.MaxOptions
                        || durations.Any(d => d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out int s) || s < 1)))
                    errors.Add($"The choices of \"{name}\" must be 1 to {AppInputTypes.MaxOptions} whole numbers of seconds.");
                break;
            case AppInputType.Choice:
                if (input.Options is not { Count: > 0 } options || options.Count > AppInputTypes.MaxOptions)
                {
                    errors.Add($"\"{name}\" must offer between 1 and {AppInputTypes.MaxOptions} options.");
                    break;
                }

                for (int i = 0; i < options.Count; i++)
                {
                    AppInputOption option = options[i];
                    if (option.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)
                        || option.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(option.Value.GetString()))
                        errors.Add($"Option {i + 1} of \"{name}\" needs a value: a word or a number.");
                    if (string.IsNullOrWhiteSpace(option.Label) && option.LabelKey is null)
                        errors.Add($"Option {i + 1} of \"{name}\" needs a label.");
                    CheckText($"The label of option {i + 1} of \"{name}\"", option.Label, AppInputTypes.MaxOptionLabel, errors);
                    CheckText($"The hint of option {i + 1} of \"{name}\"", option.Hint, AppInputTypes.MaxOptionHint, errors);
                    if (options.Take(i).Any(o => AppInputValueChecker.SameScalar(o.Value, option.Value)))
                        errors.Add($"\"{name}\" offers the same value twice.");
                }

                break;
            case AppInputType.Shape:
                if (input.Choices is { } shapes
                    && (shapes.Count == 0 || shapes.Any(s => s.ValueKind != JsonValueKind.String || !AppInputTypes.Shapes.Contains(s.GetString()!))))
                    errors.Add($"The shapes of \"{name}\" must be some of: {string.Join(", ", AppInputTypes.Shapes)}.");
                break;
            case AppInputType.Model:
                if (input.Capability is null || !AppInputTypes.ModelCapabilities.Contains(input.Capability))
                    errors.Add($"\"{name}\" must name what kind of model it picks: {string.Join(", ", AppInputTypes.ModelCapabilities)}.");
                if (input.Job is { } job && !AppInputTypes.ModelJobs.Contains(job))
                    errors.Add($"The job of \"{name}\" must be one of: {string.Join(", ", AppInputTypes.ModelJobs)}.");
                if (input.Allowed is { } allowed
                    && (allowed.Count is 0 or > AppInputTypes.MaxAllowedModels || allowed.Contains(Guid.Empty) || allowed.Distinct().Count() != allowed.Count))
                    errors.Add($"The allowed models of \"{name}\" must be 1 to {AppInputTypes.MaxAllowedModels} different models.");
                if (input.Recommended is { } recommended && input.Allowed is { Count: > 0 } list && !list.Contains(recommended))
                    errors.Add($"The recommended model of \"{name}\" must be one of its allowed models.");
                if (input.Agent is { } agent && (agent == Guid.Empty || input.Capability is not (null or "Chat")))
                    errors.Add($"\"{name}\" picks an agent's model, so it must pick a Chat model.");
                break;
        }
    }

    private static void CheckUse(
        AppManifestInput input, AppInputType type, string name, AppInputUse use, IReadOnlyList<AppBindingStep> steps,
        IReadOnlySet<string> exposedBriefFields, List<string> errors)
    {
        string where = Where(use, steps);
        switch (use.Kind)
        {
            case AppInputUseKind.Placeholder:
                if (!AppInputTypes.IsText(type))
                    errors.Add($"\"{name}\" is placed inside text ({where}), so it must be a short or long text input.");
                break;
            case AppInputUseKind.RequestTemplate:
                if (type == AppInputType.Model)
                    errors.Add($"\"{name}\" picks a model and cannot be written into the request template.");
                break;
            case AppInputUseKind.Condition:
                // Rule 4.
                if (type is not (AppInputType.Toggle or AppInputType.Choice or AppInputType.Number or AppInputType.Duration))
                    errors.Add($"A condition ({where}) reads \"{name}\"; conditions can read only switches, choices, numbers and durations.");
                break;
            case AppInputUseKind.Value when use.Field is { } field:
                CheckValueTarget(input, type, name, field, use, steps, exposedBriefFields, errors, where);
                break;
        }
    }

    private static void CheckValueTarget(
        AppManifestInput input, AppInputType type, string name, AppBindableField field, AppInputUse use, IReadOnlyList<AppBindingStep> steps,
        IReadOnlySet<string> exposedBriefFields, List<string> errors, string where)
    {
        // A spend cap (a person sets the most credits a step may spend): never hidden, never a mapped table of values, so
        // an author cannot park a high cap in a default the person cannot see.
        if (field.SpendCap)
        {
            if (use.Binding is { IsMapped: true } || type != AppInputType.Number)
                errors.Add($"\"{name}\" sets a spend cap ({where}), so it must be a plain number input, not a choice or a mapped switch.");
            if (input.ShowWhen is not null || AppInputPlacement.IsUnderMore(input))
                errors.Add($"\"{name}\" sets a spend cap ({where}), so it must always be shown on the page: no \"show when\" condition and not under \"More options\".");
            if (input.Default is null or { ValueKind: JsonValueKind.Null })
                errors.Add($"\"{name}\" sets a spend cap ({where}), so it must carry a visible default (the credits the page shows).");
            if (input.Max is not { } cap || (field.Max is { } ceiling && cap > ceiling))
                errors.Add($"\"{name}\" sets a spend cap ({where}), so it needs a maximum{(field.Max is { } m ? $" of at most {m:0.###} credits" : "")}.");
        }

        // A mapped binding (docs/design/app-inputs.md 27): a switch or a choice lists what each of its values writes, so the
        // setting's own type does not need to match; every listed value was checked against the setting at save.
        if (use.Binding is { IsMapped: true } mapped)
        {
            CheckMappedBinding(input, type, name, mapped, where, errors);
            if (field.GovernedByBriefField is { } governing && exposedBriefFields.Contains(governing))
                errors.Add($"\"{name}\" sets {where}, which the app's \"{governing}\" field already decides. Use that field instead.");
            return;
        }

        // Rule 2: compatible type.
        if (!field.Types.Contains(type))
        {
            errors.Add($"\"{name}\" is a {AppInputTypes.WireName(type)} input, but {where} takes {string.Join(" or ", field.Types.Select(AppInputTypes.WireName))}.");
            return;
        }

        // Rule 7: an exposed brief field governs this setting.
        if (field.GovernedByBriefField is { } brief && exposedBriefFields.Contains(brief))
            errors.Add($"\"{name}\" sets {where}, which the app's \"{brief}\" field already decides. Use that field instead.");

        if (field.IsPseudo && !field.IsModel)
            return; // @spokenText: its text limits are the input's effective length and the value checker's paragraphs

        if (field.IsPseudo)
        {
            // A VideoGenerate step's model is VideoGeneration, or LipSync for a talking head (judged just below).
            bool videoModel = field.Path == AppBindableCatalog.ModelKey && field.StepType == StepType.VideoGenerate;
            if (field.Capabilities is { } caps && input.Capability is { } cap && !caps.Contains(cap) && !(videoModel && cap == nameof(InferenceProviderCapability.LipSync)))
                errors.Add($"\"{name}\" picks a {cap} model, but {where} needs {string.Join(" or ", caps)}.");
            else if (videoModel && input.Capability is { } picked
                && steps.FirstOrDefault(x => x.StepOrder == use.StepOrder) is { } modelStep
                && AppModelRules.CapabilityFor(modelStep) is { } needed && picked != needed)
                errors.Add($"\"{name}\" picks a {picked} model, but {where} needs a {needed} model"
                    + (needed == nameof(InferenceProviderCapability.LipSync) ? " (it makes an avatar talk)." : "."));
            if (input.Agent is not null)
                errors.Add($"\"{name}\" picks an agent's model and cannot also set {where}.");
            return;
        }

        // 4.3: ranges, choices and defaults fit the target.
        if (type is AppInputType.Number or AppInputType.Duration)
        {
            double? min = input.Min ?? field.Min;
            double? max = input.Max ?? field.Max;
            if (field.Min is { } fmin && (min is null || min < fmin) || field.Max is { } fmax && (max is null || max > fmax))
                errors.Add($"\"{name}\" must stay within what {where} takes: {Range(field)}. Set its minimum and maximum inside that range.");
            if (field.Integer && type == AppInputType.Number && input.Step is { } step && step != Math.Floor(step))
                errors.Add($"{where} takes whole numbers, so \"{name}\" must go in whole steps.");
            foreach (JsonElement choice in input.Choices ?? [])
            {
                if (choice.ValueKind == JsonValueKind.Number && !Fits(field, choice))
                    errors.Add($"The choice {choice.GetRawText()} of \"{name}\" is outside what {where} takes: {Range(field)}.");
            }
        }
        else if (type == AppInputType.Choice)
        {
            foreach (AppInputOption option in input.Options ?? [])
            {
                if (!Fits(field, option.Value))
                    errors.Add($"The option {option.Value.GetRawText()} of \"{name}\" is not something {where} takes{(field.Choices is { } c ? $" ({string.Join(", ", c)})" : $": {Range(field)}")}.");
            }
        }
        else if (type == AppInputType.Shape)
        {
            IEnumerable<string> offered = input.Choices is { Count: > 0 } shapes ? shapes.Select(s => s.GetString() ?? string.Empty) : AppInputTypes.Shapes;
            foreach (string shape in offered)
            {
                if (field.Choices is { } c && !c.Contains(shape, StringComparer.Ordinal))
                    errors.Add($"The shape {shape} of \"{name}\" is not one {where} takes.");
            }
        }
    }

    private static void CheckMappedBinding(AppManifestInput input, AppInputType type, string name, AppValueBinding mapped, string where, List<string> errors)
    {
        if (type is not (AppInputType.Toggle or AppInputType.Choice))
        {
            errors.Add($"\"{name}\" is a {AppInputTypes.WireName(type)} input; only a switch or a choice can list what each value writes ({where}).");
            return;
        }

        foreach (string key in mapped.Values!.Keys)
        {
            bool known = type == AppInputType.Toggle
                ? key is "true" or "false"
                : (input.Options ?? []).Any(o => AppValueBinding.KeyMatches(key, o.Value));
            if (!known)
            {
                errors.Add(type == AppInputType.Toggle
                    ? $"{where} lists \"{key}\" for the switch \"{name}\"; a switch is \"true\" or \"false\"."
                    : $"{where} lists \"{key}\", which is not an option of \"{name}\".");
            }
        }
    }

    private static void CheckShowWhen(
        AppManifestInput input, AppInputShowWhen when, string name, IReadOnlyDictionary<string, AppManifestInput> byId,
        AppBindingMap map, IReadOnlyList<AppBindingStep> steps, List<string> errors)
    {
        if (!byId.TryGetValue(when.Input, out AppManifestInput? controller) || when.Input == input.Id)
        {
            errors.Add($"\"{name}\" is shown depending on \"{when.Input}\", which is not another input of this app.");
            return;
        }

        if (!AppInputTypes.TryParse(controller.Type, out AppInputType controllerType) || controllerType is not (AppInputType.Toggle or AppInputType.Choice))
        {
            errors.Add($"\"{name}\" can be shown depending on a switch or a choice only.");
            return;
        }

        if (controller.ShowWhen is not null)
        {
            errors.Add($"\"{name}\" depends on \"{DisplayName(controller)}\", which is itself shown only sometimes; one level only.");
            return;
        }

        if (!AppInputValueChecker.TryCanonical(controller, when.EqualsValue, AppInputTypes.MaxLongText, out _, out _))
        {
            errors.Add($"\"{name}\" is shown when \"{DisplayName(controller)}\" has a value it cannot have.");
            return;
        }

        // Every controller value under which the input is hidden: it must not be consumed on what runs then (9.3).
        IEnumerable<JsonElement> hiddenWhen = controllerType == AppInputType.Toggle
            ? [JsonSerializer.SerializeToElement(when.EqualsValue.ValueKind != JsonValueKind.True)]
            : (controller.Options ?? []).Select(o => o.Value).Where(v => !AppInputValueChecker.SameScalar(v, when.EqualsValue));
        IReadOnlySet<int> consumers = map.ConsumingSteps(input.Id!, steps);
        foreach (JsonElement hidden in hiddenWhen)
        {
            IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(steps, new Dictionary<string, JsonElement> { [controller.Id!] = hidden });
            int? clash = consumers.Where(reachable.Contains).Cast<int?>().FirstOrDefault();
            if (clash is { } order)
            {
                errors.Add($"\"{name}\" is hidden when \"{DisplayName(controller)}\" is {hidden.GetRawText()}, but step {order} still uses it then. Show it, or make that step run only on the other branch.");
                break;
            }
        }
    }

    private static bool Fits(AppBindableField field, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => field.Choices is null
            && (field.Min is not { } min || value.GetDouble() >= min)
            && (field.Max is not { } max || value.GetDouble() <= max)
            && (!field.Integer || value.GetDouble() == Math.Floor(value.GetDouble())),
        JsonValueKind.String => field.Choices is { } c ? c.Contains(value.GetString()!, StringComparer.OrdinalIgnoreCase) : field.Min is null && field.Max is null,
        _ => false
    };

    private static string Range(AppBindableField field) =>
        $"{field.Min?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any"} to {field.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any"}";

    private static string Where(AppInputUse use, IReadOnlyList<AppBindingStep> steps)
    {
        if (use.StepOrder is not { } order)
            return "the request template";
        AppBindingStep? step = steps.FirstOrDefault(s => s.StepOrder == order);
        string label = step?.Name is { Length: > 0 } stepName ? $"step {order} \"{Clip(Clean(stepName) ?? string.Empty)}\"" : $"step {order} ({step?.StepType ?? StepType.Agent})";
        return use.Kind switch
        {
            AppInputUseKind.Condition => $"the condition of {label}",
            AppInputUseKind.AgentModel => $"the agent of {label}",
            _ => $"\"{use.Field?.Label ?? use.Where}\" of {label}"
        };
    }
}
