using System.Globalization;
using System.Text.Json;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>A value that cannot be accepted. <see cref="Code"/> is <c>APP_INPUT_MISSING</c> or <c>APP_INPUT_INVALID</c>.</summary>
public sealed record AppInputValueProblem(string Code, string InputId, string Message)
{
    public const string Missing = "APP_INPUT_MISSING";
    public const string Invalid = "APP_INPUT_INVALID";

    /// <summary>A model value outside the app's allowed list, or one the server cannot use (docs/design/app-inputs.md 8.3).</summary>
    public const string ModelUnavailable = "APP_INPUT_MODEL_UNAVAILABLE";
}

/// <summary>The canonical values to store and bind, and the problems; values are usable only when there is no problem.</summary>
public sealed record AppInputCheckResult(IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<AppInputValueProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// The typed, manifest-aware check of <c>brief.inputs</c> (docs/design/app-inputs.md 6.2 items 1-3 and 6). Pure: the
/// API's <c>AppInputValueValidator</c> (lane L2) adds the model re-resolution (item 4, lane L4) and the dry-run bind
/// plus each bound config's <c>Validate()</c> (item 5). It refuses unknown ids, wrong types, out-of-range numbers,
/// unknown choices and text over its effective cap (never cut); cleans text (<see cref="AppInputText"/>); fills
/// defaults (a toggle with neither value nor default is <c>false</c>); drops the values of hidden inputs; and refuses
/// a missing required, visible input with <c>APP_INPUT_MISSING</c>. The returned values are the canonical form.
/// </summary>
public static class AppInputValueChecker
{
    /// <summary>Checks raw values against the manifest's <c>input</c> inputs, with the steps for the effective text caps.</summary>
    public static AppInputCheckResult Check(AppManifest manifest, IReadOnlyList<AppBindingStep> steps, IReadOnlyDictionary<string, JsonElement>? raw)
    {
        List<AppManifestInput> inputs = [.. (manifest.Inputs ?? []).Where(i => i.Kind == AppManifestVocabulary.KindInput && i.Id is not null)];
        Dictionary<string, AppManifestInput> byId = inputs.GroupBy(i => i.Id!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, steps);
        List<AppInputValueProblem> problems = [];
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        Dictionary<string, string> refused = new(StringComparer.Ordinal);

        foreach ((string id, JsonElement value) in raw ?? new Dictionary<string, JsonElement>())
        {
            if (!byId.TryGetValue(id, out AppManifestInput? input))
            {
                problems.Add(new AppInputValueProblem(AppInputValueProblem.Invalid, id, $"This app has no input named \"{Clip(id)}\"."));
                continue;
            }

            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            if (!TryCanonical(input, value, EffectiveMaxLength(input, map), out JsonElement canonical, out string? error)
                || !FitsSpokenText(input, canonical, map, out error))
            {
                // Reported below only when the input is shown: a hidden field's stale value is dropped, not refused.
                refused[id] = error!;
                continue;
            }

            // A permission is only ever stored as a grant: "off" is the absence of the value (never a stored false).
            if (input.Permission is not null && canonical.ValueKind != JsonValueKind.True)
                continue;
            if (!IsEmpty(canonical))
                values[id] = canonical;
        }

        foreach (AppManifestInput input in inputs)
        {
            if (values.ContainsKey(input.Id!) || refused.ContainsKey(input.Id!))
                continue;
            // A permission is never defaulted on: its value must be explicit in the run's own request.
            if (input.Permission is not null)
                continue;
            if (input.Default is { } def && def.ValueKind != JsonValueKind.Null
                && TryCanonical(input, def, EffectiveMaxLength(input, map), out JsonElement canonical, out _) && !IsEmpty(canonical))
                values[input.Id!] = canonical;
            else if (AppInputTypes.TryParse(input.Type, out AppInputType type) && type == AppInputType.Toggle)
                values[input.Id!] = JsonSerializer.SerializeToElement(false);
        }

        // showWhen is one level deep, so the controllers' values are final here.
        foreach (AppManifestInput input in inputs)
        {
            bool shown = IsShown(input, values);
            if (!shown)
            {
                values.Remove(input.Id!);
                continue;
            }

            if (refused.TryGetValue(input.Id!, out string? reason))
            {
                problems.Add(new AppInputValueProblem(
                    reason == UnlistedModel(input) ? AppInputValueProblem.ModelUnavailable : AppInputValueProblem.Invalid, input.Id!, reason));
                continue;
            }

            if (input.Required == true && input.Permission is null && !values.ContainsKey(input.Id!))
                problems.Add(new AppInputValueProblem(AppInputValueProblem.Missing, input.Id!, $"Fill in “{Label(input)}”."));
        }

        return new AppInputCheckResult(values, problems);
    }

    /// <summary>True when the input is shown with these values (no <c>showWhen</c>, or its controller has the value).</summary>
    public static bool IsShown(AppManifestInput input, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (input.ShowWhen is not { } when)
            return true;
        return values.TryGetValue(when.Input, out JsonElement current) && SameScalar(current, when.EqualsValue);
    }

    /// <summary>
    /// The longest text an input may carry: its own <c>maxLength</c>, the type's cap, and for each placeholder target the
    /// target's cap minus the text around the placeholders, shared between them (docs/design/app-inputs.md 4.3).
    /// </summary>
    public static int EffectiveMaxLength(AppManifestInput input, AppBindingMap map)
    {
        int cap = AppInputTypes.TryParse(input.Type, out AppInputType type) && AppInputTypes.IsText(type) ? AppInputTypes.TextCap(type) : AppInputTypes.MaxLongText;
        if (input.MaxLength is { } own)
            cap = Math.Min(cap, own);
        if (input.Id is null)
            return cap;
        foreach (AppInputUse use in map.UsesOf(input.Id))
        {
            if (use is { Kind: AppInputUseKind.Placeholder, Field.MaxLength: { } targetCap } && use.PlaceholdersInTarget > 0)
                cap = Math.Min(cap, (targetCap - use.LiteralLength) / use.PlaceholdersInTarget);
            else if (use is { Kind: AppInputUseKind.Value, Field: { IsModel: false, MaxLength: { } wholeCap } })
                cap = Math.Min(cap, wholeCap);
        }

        return cap;
    }

    /// <summary>
    /// The canonical form of one value of one input, or the plain reason it is refused. Shared by the queue-time check and
    /// the save-time check of defaults.
    /// </summary>
    public static bool TryCanonical(AppManifestInput input, JsonElement value, int maxLength, out JsonElement canonical, out string? error)
    {
        canonical = default;
        error = null;
        string label = Label(input);
        if (!AppInputTypes.TryParse(input.Type, out AppInputType type))
        {
            error = $"“{label}” has an unknown type.";
            return false;
        }

        switch (type)
        {
            case AppInputType.ShortText:
            case AppInputType.LongText:
                if (value.ValueKind != JsonValueKind.String)
                    return Fail($"“{label}” must be text.", out error);
                string text = AppInputText.Clean(value.GetString(), multiLine: type == AppInputType.LongText);
                if (AppInputText.Length(text) > maxLength)
                    return Fail($"“{label}” is too long: at most {maxLength} characters.", out error);
                canonical = JsonSerializer.SerializeToElement(text);
                return true;

            case AppInputType.Number:
            case AppInputType.Duration:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
                    return Fail($"“{label}” must be a number.", out error);
                if (type == AppInputType.Duration && (number != Math.Floor(number) || number < 1))
                    return Fail($"“{label}” must be a whole number of seconds.", out error);
                if (input.Min is { } min && number < min || input.Max is { } max && number > max)
                    return Fail($"“{label}” must be between {Format(input.Min)} and {Format(input.Max)}.", out error);
                if (type == AppInputType.Duration && input.Choices is { Count: > 0 } durations
                    && !durations.Any(c => c.ValueKind == JsonValueKind.Number && c.GetDouble() == number))
                    return Fail($"“{label}” must be one of {string.Join(", ", durations.Select(c => c.GetRawText()))} seconds.", out error);
                if (type == AppInputType.Number && input.Step is { } step && step > 0 && input.Min is { } from
                    && Math.Abs(Math.Round((number - from) / step) * step - (number - from)) > 1e-9)
                    return Fail($"“{label}” must go in steps of {Format(step)}.", out error);
                canonical = number == Math.Floor(number) && Math.Abs(number) < int.MaxValue
                    ? JsonSerializer.SerializeToElement((int)number)
                    : JsonSerializer.SerializeToElement(number);
                return true;

            case AppInputType.Choice:
                AppInputOption? option = (input.Options ?? []).FirstOrDefault(o => SameScalar(o.Value, value));
                if (option is null)
                    return Fail($"“{label}” must be one of the offered choices.", out error);
                canonical = option.Value.Clone();
                return true;

            case AppInputType.Toggle:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Fail($"“{label}” must be on or off.", out error);
                canonical = value.Clone();
                return true;

            case AppInputType.Shape:
                IReadOnlyList<string> shapes = input.Choices is { Count: > 0 } c
                    ? [.. c.Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                    : AppInputTypes.Shapes;
                if (value.ValueKind != JsonValueKind.String || !shapes.Contains(value.GetString()!, StringComparer.Ordinal))
                    return Fail($"“{label}” must be one of {string.Join(", ", shapes)}.", out error);
                canonical = value.Clone();
                return true;

            case AppInputType.Model:
                if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out Guid model) || model == Guid.Empty)
                    return Fail($"“{label}” must name a model.", out error);
                if (input.Allowed is { Count: > 0 } allowed && !allowed.Contains(model))
                    return Fail(UnlistedModel(input), out error);
                canonical = JsonSerializer.SerializeToElement(model.ToString("D"));
                return true;

            default:
                return Fail($"“{label}” has an unknown type.", out error);
        }
    }

    /// <summary>
    /// A text bound to a Voiceover step's words (<c>@spokenText</c>) must split into lines the step can speak
    /// (<see cref="VoiceoverSpokenText"/>); refused as <c>APP_INPUT_INVALID</c> naming the field otherwise.
    /// </summary>
    public static bool FitsSpokenText(AppManifestInput input, JsonElement canonical, AppBindingMap map, out string? error)
    {
        error = null;
        if (input.Id is null || canonical.ValueKind != JsonValueKind.String || canonical.GetString()!.Length == 0
            || !map.UsesOf(input.Id).Any(u => u is { Kind: AppInputUseKind.Value, Field.Path: AppBindableCatalog.SpokenTextKey }))
            return true;
        if (VoiceoverSpokenText.TrySplit(canonical.GetString(), out _, out string? why))
            return true;
        error = $"“{Label(input)}” {why}";
        return false;
    }

    /// <summary>Two scalars are the same value: strings ordinal, numbers by value, booleans by value.</summary>
    public static bool SameScalar(JsonElement a, JsonElement b) => (a.ValueKind, b.ValueKind) switch
    {
        (JsonValueKind.String, JsonValueKind.String) => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
        (JsonValueKind.Number, JsonValueKind.Number) => a.GetDouble() == b.GetDouble(),
        (JsonValueKind.True, JsonValueKind.True) or (JsonValueKind.False, JsonValueKind.False) => true,
        _ => false
    };

    /// <summary>The input's label for messages (its id when it has none).</summary>
    public static string Label(AppManifestInput input) =>
        Clip(!string.IsNullOrWhiteSpace(input.Label) ? input.Label! : input.Id ?? input.Kind);

    private static bool IsEmpty(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0;

    /// <summary>The refusal of a model outside the app's <c>allowed</c> list (code <see cref="AppInputValueProblem.ModelUnavailable"/>).</summary>
    private static string UnlistedModel(AppManifestInput input) => $"“{Label(input)}” must be one of the models this app offers.";

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static string Format(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "any";

    private static string Clip(string text) => text.Length <= 40 ? text : text[..(char.IsHighSurrogate(text[39]) ? 39 : 40)];
}
