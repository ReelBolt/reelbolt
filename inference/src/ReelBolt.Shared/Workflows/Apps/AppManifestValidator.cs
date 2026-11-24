using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>An exposed input that no step of the workflow can honour (linter rule <c>WL018</c> manifest-orphan).</summary>
public sealed record AppManifestOrphan(string Code, int InputIndex, string InputLabel, string Message)
{
    public const string RuleCode = "WL018";
}

/// <summary>
/// The one manifest validator, shared by REST, the builder and the assistant (docs/design/workflow-apps.md 4.1).
/// Pure: it never reads storage. Manifest text is user-supplied and rendered into the app UI, so
/// <see cref="Sanitize"/> strips control characters and markup first and <see cref="Validate"/> caps lengths and
/// refuses links. Messages are plain sentences naming the input.
/// </summary>
public static partial class AppManifestValidator
{
    private static readonly string[] Formats = ["Original", "Vertical", "Square", "Landscape", "Portrait", "9:16", "1:1", "16:9", "4:5"];
    private static readonly string[] Resolutions = ["Auto", "720p", "1080p", "2160p", "4K"];
    private static readonly string[] DesignSystems = ["project", "none"];
    private static readonly string[] VoiceModes = ["none", "narrated"];
    private static readonly string[] OverlayCardAudioChoices = ["agent", "keep", "mute"];
    private static readonly string[] PaceChoices = ["calm", "medium", "fast"];

    [GeneratedRegex(@"^[A-Za-z]{2,8}(-[A-Za-z]{4})?(-([A-Za-z]{2}|[0-9]{3}))?$")]
    private static partial Regex LanguageTag();

    [GeneratedRegex(@"(?i)(://|\bwww\.|\bmailto:|\bjavascript:|\bdata:|\b[a-z0-9-]+\.(com|org|net|io|app|dev|co|ai|me|ly|xyz|info|biz|example|test|ru|cn|tk|gg|sh)\b)")]
    private static partial Regex Link();

    /// <summary>The refusal of a hand-written input that uses the permission field's id or its <c>permission</c> marker.</summary>
    public const string ReservedPermissionMessage =
        "The input \"" + MediaEgressPermission.InputId + "\" and the \"permission\" member belong to the app's own permission to send media. "
        + "The app adds that field itself where a step needs it; it cannot be written, bound, defaulted or renamed in an app page.";

    /// <summary>Strips control characters and markup from every text member and trims it; never throws.</summary>
    public static AppManifest Sanitize(AppManifest manifest)
    {
        // The permission field is derived from the steps on every read, so an echoed copy is dropped, never stored.
        manifest = MediaEgressPermission.Strip(manifest);
        AppManifest clean = manifest with
        {
            Title = Clean(manifest.Title),
            Tagline = Clean(manifest.Tagline),
            Inputs = manifest.Inputs?.Select(i => i with
            {
                Label = Clean(i.Label),
                Help = Clean(i.Help),
                Example = Clean(i.Example),
                OnLabel = Clean(i.OnLabel),
                OffLabel = Clean(i.OffLabel),
                Options = i.Options?.Select(o => o with { Label = Clean(o.Label), Hint = Clean(o.Hint) }).ToList()
            }).ToList()
        };

        // A manifest that carries any version-2 member is written at version 2 (docs/design/app-inputs.md 14).
        return clean.Version == AppManifestVocabulary.Version && UsesVersion2(clean)
            ? clean with { Version = AppManifestVocabulary.Version2 }
            : clean;
    }

    /// <summary>
    /// All the reasons the manifest cannot be saved against these steps; empty means valid. App inputs of kind
    /// <c>input</c> and the request template are checked against <paramref name="bindingSteps"/>; with none (a caller
    /// with no steps to check against) they are refused, so nothing is saved that no run path binds.
    /// </summary>
    public static IReadOnlyList<string> Validate(AppManifest manifest, IReadOnlyList<AppStepFacts> steps, IReadOnlyList<AppBindingStep>? bindingSteps = null)
    {
        List<string> errors = [];
        if (manifest.Version is not (AppManifestVocabulary.Version or AppManifestVocabulary.Version2))
            errors.Add($"The app manifest version must be {AppManifestVocabulary.Version} or {AppManifestVocabulary.Version2}.");

        CheckText("The title", manifest.Title, AppManifestVocabulary.MaxTitle, errors);
        CheckText("The tagline", manifest.Tagline, AppManifestVocabulary.MaxTagline, errors);

        if (manifest.Category is not null && !AppManifestVocabulary.Categories.Contains(manifest.Category))
            errors.Add($"The category must be one of: {string.Join(", ", AppManifestVocabulary.Categories)}.");
        if (manifest.Result is { } result && !AppManifestVocabulary.ResultKinds.Contains(result.Kind))
            errors.Add($"The result kind must be one of: {string.Join(", ", AppManifestVocabulary.ResultKinds)}.");
        if (manifest.SuggestedStylePreset is not null && StylePresetCatalog.Find(manifest.SuggestedStylePreset) is null)
            errors.Add($"The suggested style \"{Clip(manifest.SuggestedStylePreset)}\" is not one of the built-in styles.");

        IReadOnlyList<AppManifestInput> inputs = manifest.Inputs ?? [];
        int shown = inputs.Count(i => !AppInputPlacement.IsUnderMore(i));
        if (inputs.Count > AppManifestVocabulary.MaxInputsTotal)
            errors.Add($"An app can have at most {AppManifestVocabulary.MaxInputsTotal} inputs; this one has {inputs.Count}.");
        if (shown > AppManifestVocabulary.MaxInputs)
            errors.Add($"An app can show at most {AppManifestVocabulary.MaxInputs} inputs before \"More options\"; this one shows {shown}. Move some under \"More options\".");

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < inputs.Count; i++)
            ValidateInput(inputs[i], i, seen, errors);

        // The permission to send media is the app's own field: nobody writes, binds, defaults or renames it by hand.
        if (inputs.Any(MediaEgressPermission.IsReserved))
            errors.Add(ReservedPermissionMessage);

        foreach (AppManifestOrphan orphan in FindOrphans(manifest, steps))
            errors.Add(orphan.Message);

        ValidateAppInputs(manifest, bindingSteps, errors);
        return errors;
    }

    /// <summary>
    /// The exposed inputs no step can honour: a brief field nothing reads (it would be shown and ignored) and a file
    /// role no step takes. The pre-run linter wraps these as <c>WL018</c>; the save paths refuse them.
    /// </summary>
    public static IReadOnlyList<AppManifestOrphan> FindOrphans(AppManifest? manifest, IReadOnlyList<AppStepFacts> steps)
    {
        if (manifest?.Inputs is not { Count: > 0 } inputs)
            return [];

        IReadOnlySet<string> read = BriefFieldConsumers.FieldsReadBy(steps);
        List<AppManifestOrphan> found = [];
        for (int i = 0; i < inputs.Count; i++)
        {
            AppManifestInput input = inputs[i];
            string label = DisplayName(input);
            if (input.Kind == AppManifestVocabulary.KindBrief && input.Field is { } field
                && AppManifestVocabulary.BriefFields.Contains(field) && !read.Contains(field))
            {
                found.Add(new AppManifestOrphan(AppManifestOrphan.RuleCode, i, label,
                    $"The input \"{label}\" is not read by any step of this workflow, so it would be shown and ignored. Remove it or add a step that uses it."));
            }
            else if (input.Kind == AppManifestVocabulary.KindFiles && input.Role is { } role
                && AppManifestVocabulary.FileRoles.Contains(role) && !BriefFieldConsumers.AcceptsFileRole(role, steps))
            {
                found.Add(new AppManifestOrphan(AppManifestOrphan.RuleCode, i, label,
                    $"The input \"{label}\" takes {role} files, but no step of this workflow can use them. Remove it or add a step that takes them."));
            }
        }

        return found;
    }

    private static void ValidateInput(AppManifestInput input, int index, HashSet<string> seen, List<string> errors)
    {
        string name = DisplayName(input);
        CheckText($"The label of input {index + 1}", input.Label, AppManifestVocabulary.MaxLabel, errors);

        // Members a kind does not use must be absent: nothing unvalidated is ever stored or echoed. Inputs of kind
        // "input" are checked member by member in ValidateTypedInput.
        bool typed = input.Kind == AppManifestVocabulary.KindInput;
        if (!typed && input.Kind != AppManifestVocabulary.KindBrief && (input.Field is not null || input.Default is not null || input.Choices is not null))
            errors.Add($"The input \"{name}\" cannot have a field, default or choices; only brief inputs can.");
        if (!typed && input.Kind != AppManifestVocabulary.KindFiles && (input.Role is not null || input.Min is not null || input.Max is not null))
            errors.Add($"The input \"{name}\" cannot have a role or file limits; only files inputs can.");
        if (typed && (input.Field is not null || input.Role is not null))
            errors.Add($"The input \"{name}\" cannot have a brief field or a file role; it is an app input.");
        if (!typed && HasTypedMembers(input))
            errors.Add($"The input \"{name}\" has members only an app input (kind \"input\") can have.");

        switch (input.Kind)
        {
            case AppManifestVocabulary.KindBrief:
                if (input.Field is null || !AppManifestVocabulary.BriefFields.Contains(input.Field))
                {
                    errors.Add($"The input \"{name}\" names a brief field that does not exist. Use one of: {string.Join(", ", AppManifestVocabulary.BriefFields)}.");
                    break;
                }

                if (!seen.Add("brief:" + input.Field))
                    errors.Add($"The brief field \"{input.Field}\" is exposed twice.");
                if (input.Default is { } def && !IsValidValue(input.Field, def))
                    errors.Add($"The default of \"{name}\" is not a valid {input.Field} value.");
                if (input.Choices is { } choices)
                {
                    if (choices.Count == 0 || choices.Count > AppManifestVocabulary.MaxChoices)
                        errors.Add($"\"{name}\" can offer between 1 and {AppManifestVocabulary.MaxChoices} choices.");
                    else if (choices.Any(c => !IsValidValue(input.Field, c)))
                        errors.Add($"\"{name}\" offers a choice that is not a valid {input.Field} value.");
                }

                break;
            case AppManifestVocabulary.KindFiles:
                if (input.Role is null || !AppManifestVocabulary.FileRoles.Contains(input.Role))
                {
                    errors.Add($"The input \"{name}\" must name a file role: {string.Join(", ", AppManifestVocabulary.FileRoles)}.");
                    break;
                }

                if (!seen.Add("files:" + input.Role))
                    errors.Add($"The file role \"{input.Role}\" is exposed twice.");
                if (input.Min is < 0 || input.Max is < 1 || input.Max > AppManifestVocabulary.MaxFilesPerSlot
                    || (input.Min is { } min && input.Max is { } max && min > max)
                    || input.Min is { } wholeMin && wholeMin != Math.Floor(wholeMin)
                    || input.Max is { } wholeMax && wholeMax != Math.Floor(wholeMax))
                    errors.Add($"The file limits of \"{name}\" must satisfy 0 <= min <= max <= {AppManifestVocabulary.MaxFilesPerSlot}.");
                break;
            case AppManifestVocabulary.KindNotes:
                if (!seen.Add("notes"))
                    errors.Add("The notes box is exposed twice.");
                break;
            case AppManifestVocabulary.KindInput:
                // Checked as a whole, with the steps, by ValidateAppInputs.
                break;
            case "step":
                errors.Add($"The input \"{name}\" sets a single step option, which apps do not support. Use an app input (kind \"input\") instead.");
                break;
            default:
                errors.Add($"The input \"{name}\" has an unknown kind. Use brief, files, notes or input.");
                break;
        }
    }

    private static bool IsValidValue(string field, JsonElement value)
    {
        switch (field)
        {
            case BriefFieldConsumers.Language:
            case BriefFieldConsumers.SourceLanguage:
                return value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } tag && (tag == "auto" || LanguageTag().IsMatch(tag));
            case BriefFieldConsumers.LengthSec:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int sec) && sec is >= 5 and <= 600;
            case BriefFieldConsumers.Format:
                return IsOneOf(value, Formats);
            case BriefFieldConsumers.Resolution:
                return IsOneOf(value, Resolutions);
            case BriefFieldConsumers.StylePreset:
                return value.ValueKind == JsonValueKind.String && StylePresetCatalog.Find(value.GetString()) is not null;
            case BriefFieldConsumers.Tone:
                return value.ValueKind == JsonValueKind.String
                    && Enum.TryParse(value.GetString(), ignoreCase: true, out StyleTone tone) && Enum.IsDefined(tone);
            case BriefFieldConsumers.DesignSystem:
                return IsOneOf(value, DesignSystems);
            case BriefFieldConsumers.Voice:
                return IsOneOf(value, VoiceModes);
            case BriefFieldConsumers.OverlayCardAudio:
                return IsOneOf(value, OverlayCardAudioChoices);
            case BriefFieldConsumers.Avatar:
                // An avatar is one project's consented likeness: a default or a choice list would name another
                // project's avatar the moment the workflow is copied, so the run page always lists the project's own.
                return false;
            case BriefFieldConsumers.Pace:
                return IsOneOf(value, PaceChoices);
            default:
                return false;
        }
    }

    private static bool IsOneOf(JsonElement value, string[] allowed) =>
        value.ValueKind == JsonValueKind.String
        && allowed.Contains(value.GetString(), StringComparer.OrdinalIgnoreCase);

    private static void CheckText(string what, string? text, int max, List<string> errors)
    {
        if (text is null)
            return;
        if (text.Length > max)
            errors.Add($"{what} is longer than {max} characters.");
        if (HasHidden(text) || text.Contains('<') || text.Contains('>'))
            errors.Add($"{what} contains control characters or markup, which are not allowed.");
        if (Link().IsMatch(text))
            errors.Add($"{what} contains a link, which is not allowed.");
    }

    private static string DisplayName(AppManifestInput input) =>
        Clip(Clean(!string.IsNullOrWhiteSpace(input.Label) ? input.Label : input.Field ?? input.Role ?? input.Id ?? input.Kind) ?? string.Empty);

    // Never cut between the halves of a surrogate pair.
    private static string Clip(string text)
    {
        if (text.Length <= 40)
            return text;
        int n = char.IsHighSurrogate(text[39]) ? 39 : 40;
        return text[..n];
    }

    /// <summary>Control characters and Unicode format/separator characters (bidi overrides, zero-width): never shown.</summary>
    private static bool IsHidden(Rune r) =>
        Rune.IsControl(r) || Rune.GetUnicodeCategory(r) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned
            || IsSupplementaryVariationSelector(r);

    /// <summary>U+E0100..E01EF: an unlimited channel for hidden bytes behind one visible character.</summary>
    private static bool IsSupplementaryVariationSelector(Rune r) => r.Value is >= 0xE0100 and <= 0xE01EF;

    /// <summary>U+FE00..FE0F: one is legitimate (emoji presentation); a run of them is a smuggling channel.</summary>
    private static bool IsVariationSelector(Rune r) => r.Value is >= 0xFE00 and <= 0xFE0F;

    private static bool HasHidden(string text)
    {
        bool previousSelector = false;
        foreach (Rune r in text.EnumerateRunes())
        {
            bool selector = IsVariationSelector(r);
            if (IsHidden(r) || (selector && previousSelector))
                return true;
            previousSelector = selector;
        }

        return false;
    }

    /// <summary>Cleans a text the manifest or the app view will show, and caps it (used for the workflow-name title fallback).</summary>
    public static string SanitizeText(string? text, int max)
    {
        string clean = Clean(text) ?? string.Empty;
        if (clean.Length <= max)
            return clean;
        int n = max > 0 && char.IsHighSurrogate(clean[max - 1]) ? max - 1 : max;
        return clean[..n].TrimEnd();
    }

    private static string? Clean(string? text)
    {
        if (text is null)
            return null;
        StringBuilder sb = new(text.Length);
        // Whole code points: a tag character (U+E0000..E007F) is two surrogate halves in a string.
        bool previousSelector = false;
        foreach (Rune c in text.EnumerateRunes())
        {
            bool selector = IsVariationSelector(c);
            bool dropSelector = selector && previousSelector;
            previousSelector = selector;
            if (Rune.IsControl(c))
                sb.Append(' ');
            else if (IsHidden(c) || dropSelector)
                continue;
            else if (c.Value is not ('<' or '>' or '`'))
                sb.Append(c.ToString());
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
