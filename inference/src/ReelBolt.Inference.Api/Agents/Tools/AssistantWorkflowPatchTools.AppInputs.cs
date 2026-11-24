using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The four app-page operations of <c>ProposePatchWorkflow</c>/<c>ApplyPatchWorkflow</c> (docs/design/app-inputs.md 13.2,
/// lane L8): <c>setAppInput</c> (add or replace one field of the app page, by id), <c>removeAppInput</c> (remove it and
/// every use of it), <c>bindAppInput</c> (make one step's setting read a field) and <c>setAppPage</c> (title, tagline,
/// field order, the request template). They only edit the stored page and the steps' configs; whether the result can be
/// saved is decided by the same validators as every other save (<c>StepConfigSaveValidator</c>, the manifest validator),
/// so nothing a field may reach is decided here: the allowlist of bindable settings is the only vocabulary.
/// </summary>
public sealed partial class AssistantWorkflowPatchTools
{
    internal const string OpSetAppInput = "setappinput";
    internal const string OpRemoveAppInput = "removeappinput";
    internal const string OpBindAppInput = "bindappinput";
    internal const string OpSetAppPage = "setapppage";

    /// <summary>The app page while a patch is applied: the page it started from and whether an operation changed it.</summary>
    private sealed class AppPageDraft(Func<AppManifest> start)
    {
        private AppManifest? _manifest;

        /// <summary>The page as the operations left it (the stored page, or the derived one, until an operation edits it).</summary>
        public AppManifest Manifest
        {
            get => _manifest ??= start();
            set => _manifest = value;
        }

        /// <summary>True once an operation edited the page.</summary>
        public bool Touched { get; set; }
    }

    private static string AppOpsNote() =>
        " App page (the fields a person fills in to run the app): {op:\"setAppInput\", input:{id, type shortText|longText|number|duration|choice|toggle|shape|model, label, help, example, required, default, min, max, unit, options:[{value,label}], more, showWhen}} adds or replaces a field by id; "
        + "{op:\"removeAppInput\", id} removes it and every use of it; "
        + "{op:\"bindAppInput\", stepOrder, field, inputId, mode:\"placeholder\"|\"value\", template?, value?} makes a step's setting read the field (field from GetStepCatalog's bindableFields, an item named with its index such as regionEffects[0].effect; placeholder mode writes template, which must contain {{input.<inputId>}}, into a text setting; value mode sets the whole setting: @model for the step's model, @stillModel for a talking head's picture model, @spokenText for a Voiceover step's exact words, one paragraph per spoken line; for a switch or a choice, value:{\"true\":800,\"false\":0} lists what each of its values writes, so one switch can set several settings and a value left out keeps the step's own); "
        + "{op:\"setAppPage\", title?, tagline?, order?:[input ids], requestTemplate?} (requestTemplate is text added to each run's request with {{input.id}} placeholders; \"\" removes it). Consent, sending media to an outside service, spend caps and providers can never read a field.";

    /// <summary>True for an operation that edits the app page and names no step of its own.</summary>
    private static bool IsPageOperation(string op) => op is OpSetAppInput or OpRemoveAppInput or OpSetAppPage;

    private static string? ApplyPageOperation(string op, WorkflowPatchOperation operation, List<CreateWorkflowStepRequest> steps, AppPageDraft page)
    {
        string? error = op switch
        {
            OpSetAppInput => SetAppInput(operation, page),
            OpRemoveAppInput => RemoveAppInput(operation, steps, page),
            _ => SetAppPage(operation, page)
        };
        if (error is null)
            page.Touched = true;
        return error;
    }

    private static string? SetAppInput(WorkflowPatchOperation operation, AppPageDraft page)
    {
        if (operation.Input is not { } raw)
            return "setAppInput needs an input (a JSON object: id, type, label …)";
        JsonObject obj;
        try
        {
            JsonNode? node = raw.ValueKind == JsonValueKind.String ? JsonNode.Parse(raw.GetString() ?? string.Empty) : JsonNode.Parse(raw.GetRawText());
            if (node is not JsonObject parsed)
                return "setAppInput's input must be a JSON object";
            obj = parsed;
        }
        catch (JsonException)
        {
            return "setAppInput's input is not valid JSON";
        }

        if (!obj.Any(p => string.Equals(p.Key, "kind", StringComparison.OrdinalIgnoreCase)))
            obj["kind"] = AppManifestVocabulary.KindInput;

        AppManifestInput? input;
        try
        {
            input = obj.Deserialize<AppManifestInput>(AppManifestJson.Options);
        }
        catch (JsonException ex)
        {
            return $"setAppInput's input is not a valid field: {ex.Message}";
        }

        if (input is null || input.Kind != AppManifestVocabulary.KindInput)
            return "setAppInput adds an app input: its kind is \"input\" (brief fields, file slots and notes are not set here)";
        if (string.IsNullOrWhiteSpace(input.Id) || !AppInputPlaceholders.IdPattern().IsMatch(input.Id))
            return "setAppInput needs input.id: a lower-case letter then letters and digits, at most 32 characters";

        List<AppManifestInput> inputs = [.. page.Manifest.Inputs ?? []];
        int at = inputs.FindIndex(i => i.Kind == AppManifestVocabulary.KindInput && string.Equals(i.Id, input.Id, StringComparison.Ordinal));
        if (at >= 0)
            inputs[at] = input;
        else
            inputs.Add(input);
        page.Manifest = page.Manifest with { Version = AppManifestVocabulary.Version2, Inputs = inputs };
        return null;
    }

    private static string? RemoveAppInput(WorkflowPatchOperation operation, List<CreateWorkflowStepRequest> steps, AppPageDraft page)
    {
        string? id = operation.Id ?? operation.InputId;
        if (string.IsNullOrWhiteSpace(id))
            return "removeAppInput needs the id of the field";
        List<AppManifestInput> inputs = [.. page.Manifest.Inputs ?? []];
        AppManifestInput? removed = inputs.FirstOrDefault(i => i.Kind == AppManifestVocabulary.KindInput && string.Equals(i.Id, id, StringComparison.Ordinal));
        if (removed is null)
            return $"the app page has no field '{id}'";

        // A branch decided by the field cannot be unbound without changing what the workflow does: say so instead.
        if (steps.FirstOrDefault(s => StaticBranchResolver.InputIds(s.ConditionExpression).Contains(id)) is { } branch)
            return $"step {branch.StepOrder} chooses its path from this field; change its condition first";

        inputs.Remove(removed);
        // Another field shown only when this one has a value is shown always.
        inputs = [.. inputs.Select(i => i.ShowWhen is { } when && string.Equals(when.Input, id, StringComparison.Ordinal) ? i with { ShowWhen = null } : i)];
        string? template = page.Manifest.RequestTemplate is { } requestTemplate
            ? string.Join("\n", requestTemplate.Split('\n').Where(line => !AppInputPlaceholders.Ids(line).Contains(id)))
            : null;
        page.Manifest = page.Manifest with { Inputs = inputs, RequestTemplate = string.IsNullOrWhiteSpace(template) ? null : template };

        // Every use in a step goes: an appInputs entry naming it, and each {{input.id}} (replaced by the field's default
        // text, or nothing), so the setting keeps a plain value.
        string fill = removed.Default is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? string.Empty
            : removed.Default is { ValueKind: JsonValueKind.Number } number ? number.GetRawText()
            : string.Empty;
        for (int i = 0; i < steps.Count; i++)
        {
            CreateWorkflowStepRequest step = steps[i];
            if (!Enum.TryParse(step.StepType ?? nameof(StepType.Agent), out StepType type) || ConfigOf(step, type) is not { Json: { } json })
                continue;
            if (!json.Contains(AppInputPlaceholders.Opening, StringComparison.Ordinal) && !json.Contains(AppInputPlaceholders.AppInputsKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (JsonNode.Parse(json) is not JsonObject config)
                continue;
            if (AppBindingAnalysis.FindAppInputs(config) is (string key, JsonObject map))
            {
                foreach (string setting in map.Where(p => AppValueBinding.TryRead(p.Value, out AppValueBinding? b, out _) && b!.InputId == id).Select(p => p.Key).ToList())
                    map.Remove(setting);
                if (map.Count == 0)
                    config.Remove(key);
            }

            ReplacePlaceholders(config, id, fill);
            steps[i] = WithConfig(step, type, config.ToJsonString());
        }

        return null;
    }

    private static string? SetAppPage(WorkflowPatchOperation operation, AppPageDraft page)
    {
        if (operation.Title is null && operation.Tagline is null && operation.Order is null && operation.RequestTemplate is null)
            return "setAppPage needs at least one of title, tagline, order or requestTemplate";
        AppManifest manifest = page.Manifest;
        if (operation.Title is not null)
            manifest = manifest with { Title = string.IsNullOrWhiteSpace(operation.Title) ? null : operation.Title };
        if (operation.Tagline is not null)
            manifest = manifest with { Tagline = string.IsNullOrWhiteSpace(operation.Tagline) ? null : operation.Tagline };
        if (operation.RequestTemplate is not null)
            manifest = manifest with { Version = AppManifestVocabulary.Version2, RequestTemplate = string.IsNullOrWhiteSpace(operation.RequestTemplate) ? null : operation.RequestTemplate };
        if (operation.Order is { } order)
        {
            List<AppManifestInput> inputs = [.. manifest.Inputs ?? []];
            List<AppManifestInput> fields = [.. inputs.Where(i => i.Kind == AppManifestVocabulary.KindInput)];
            List<AppManifestInput> ordered = [];
            foreach (string id in order)
            {
                AppManifestInput? field = fields.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
                if (field is null)
                    return $"order names '{id}', which is not a field of the app page";
                if (!ordered.Contains(field))
                    ordered.Add(field);
            }

            ordered.AddRange(fields.Where(f => !ordered.Contains(f)));
            // The fields take the places the fields had; brief fields, file slots and notes keep theirs.
            int next = 0;
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].Kind == AppManifestVocabulary.KindInput)
                    inputs[i] = ordered[next++];
            }

            manifest = manifest with { Inputs = inputs };
        }

        page.Manifest = manifest;
        return null;
    }

    private static string? BindAppInput(List<CreateWorkflowStepRequest> steps, int index, WorkflowPatchOperation operation, AppPageDraft page)
    {
        CreateWorkflowStepRequest step = steps[index];
        if (string.IsNullOrWhiteSpace(operation.InputId) || !AppInputPlaceholders.IdPattern().IsMatch(operation.InputId))
            return "bindAppInput needs inputId, the id of a field of the app page";
        if (string.IsNullOrWhiteSpace(operation.Field))
            return "bindAppInput needs field, a setting from GetStepCatalog's bindableFields";
        if (!Enum.TryParse(step.StepType ?? nameof(StepType.Agent), out StepType type) || ConfigOf(step, type) is not { } current)
            return $"a {step.StepType ?? nameof(StepType.Agent)} step has no setting a field can fill";
        if (!(page.Manifest.Inputs ?? []).Any(i => i.Kind == AppManifestVocabulary.KindInput && string.Equals(i.Id, operation.InputId, StringComparison.Ordinal)))
            return $"the app page has no field '{operation.InputId}' (add it with setAppInput first)";

        string field = operation.Field.Trim();
        AppBindableField? bindable = AppBindableCatalog.Find(type, AppBindingAnalysis.PatternOf(field));
        if (bindable is null)
        {
            string allowed = string.Join(", ", AppBindableCatalog.For(type).Select(f => f.Path));
            return allowed.Length == 0
                ? $"no setting of a {type} step can read a field"
                : $"'{field}' cannot read a field; a {type} step's bindable settings are: {allowed}";
        }

        string mode = (operation.Mode ?? (bindable.Mode == AppBindingMode.Value ? "value" : "placeholder")).Trim().ToLowerInvariant();
        JsonObject config;
        try
        {
            config = string.IsNullOrWhiteSpace(current.Json) ? new JsonObject() : JsonNode.Parse(current.Json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return "the step's settings are not valid JSON; fix them first";
        }

        switch (mode)
        {
            case "value":
                if (bindable.Mode != AppBindingMode.Value)
                    return $"'{field}' is text: bind it with mode \"placeholder\"";
                (string? key, JsonNode? node) = AppBindingAnalysis.FindAppInputs(config) is (string k, var n) ? (k, n) : (null, null);
                JsonObject map = node as JsonObject ?? new JsonObject();
                JsonNode entry = JsonValue.Create(operation.InputId)!;
                if (operation.Value is { ValueKind: JsonValueKind.Object } listed)
                {
                    // A mapped binding: what each of the field's values writes (docs/design/app-inputs.md 27).
                    entry = new JsonObject { [AppValueBinding.InputMember] = operation.InputId, [AppValueBinding.ValuesMember] = JsonNode.Parse(listed.GetRawText()) };
                    if (!AppValueBinding.TryRead(entry, out _, out string? entryError))
                        return $"value {entryError}";
                }
                else if (operation.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                {
                    return "value must list what each of the field's values writes, for example {\"true\": 800, \"false\": 0}";
                }

                map[bindable.IsNested ? field : bindable.Path] = entry;
                config[key ?? AppInputPlaceholders.AppInputsKey] = map;
                break;
            case "placeholder":
                if (bindable.Mode != AppBindingMode.Placeholder)
                    return $"'{field}' is set as a whole: bind it with mode \"value\"";
                string placeholder = AppInputPlaceholders.Opening + operation.InputId + "}}";
                string text = operation.Template ?? placeholder;
                if (!text.Contains(placeholder, StringComparison.Ordinal))
                    return $"template must contain {placeholder}";
                if (SetPath(config, field, text) is { } pathError)
                    return pathError;
                break;
            default:
                return "mode must be \"placeholder\" or \"value\"";
        }

        steps[index] = WithConfig(step, type, config.ToJsonString());
        return null;
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(\[(?<index>\d+)\])?$")]
    private static partial Regex PathSegment();

    /// <summary>Sets a dotted path (<c>a.b[0].c</c>) to text, creating objects on the way; an index must name an existing item.</summary>
    private static string? SetPath(JsonObject root, string path, string value)
    {
        string[] segments = path.Split('.');
        JsonNode current = root;
        for (int i = 0; i < segments.Length; i++)
        {
            Match match = PathSegment().Match(segments[i]);
            if (!match.Success || current is not JsonObject obj)
                return $"'{path}' is not a setting path (use names and [index], for example trackObjects[0].label)";
            string name = match.Groups["name"].Value;
            string key = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            bool last = i == segments.Length - 1;
            if (match.Groups["index"].Success)
            {
                int index = int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (obj[key] is not JsonArray array || index >= array.Count)
                    return $"'{path}': the step has no item {index} in {name}";
                if (last)
                {
                    array[index] = value;
                    return null;
                }

                current = array[index] ?? (array[index] = new JsonObject());
                continue;
            }

            if (last)
            {
                obj[key] = value;
                return null;
            }

            current = obj[key] ?? (obj[key] = new JsonObject());
        }

        return null;
    }

    /// <summary>Replaces every <c>{{input.id}}</c> in the config's text (not the appInputs map) with <paramref name="fill"/>.</summary>
    private static void ReplacePlaceholders(JsonNode? node, string id, string fill)
    {
        string token = AppInputPlaceholders.Opening + id + "}}";
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? child) in obj.ToList())
                {
                    if (child is JsonValue v && v.TryGetValue(out string? text) && text.Contains(token, StringComparison.Ordinal))
                        obj[key] = text.Replace(token, fill, StringComparison.Ordinal);
                    else
                        ReplacePlaceholders(child, id, fill);
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue(out string? text) && text.Contains(token, StringComparison.Ordinal))
                        array[i] = text.Replace(token, fill, StringComparison.Ordinal);
                    else
                        ReplacePlaceholders(array[i], id, fill);
                }

                break;
        }
    }
}
