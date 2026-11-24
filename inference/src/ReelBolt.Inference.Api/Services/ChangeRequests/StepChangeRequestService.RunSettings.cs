using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.ChangeRequests;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>
/// Run settings in a change request (docs/design/app-inputs.md 29): the brief fields of a finished run (language, length,
/// voice, files, notes, ...) changed as a NEW LINKED RUN through the same replay as an app-input change. The seeds are the
/// steps on the run's path that READ a changed setting (<see cref="BriefFieldConsumers"/>, the request for the notes, the
/// file roles a step takes); the replay runs with the new brief, so every re-made step reads it through the engine's own
/// brief overrides. Shape, size, style and tone change the whole film and are refused
/// (<see cref="AppInputChangeCodes.BriefNotChangeable"/>).
/// </summary>
public sealed partial class StepChangeRequestService
{
    /// <summary>The longest notes a change may set (the request text's own column holds more; this is the form's cap).</summary>
    internal const int MaxChangeNotesChars = 8000;

    private const string NotesKey = "notes";

    private static readonly JsonSerializerOptions StepConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static bool UsesBrief(CreateStepChangeRequest? body) => body?.Brief is not null;

    /// <summary>The prepared run-settings part: the new stored brief (no inputs), the notes, the steps that read it and the lines.</summary>
    private sealed record PreparedBrief(
        StoredRunBrief? Stored,
        string? Notes,
        bool NotesChanged,
        IReadOnlySet<int> Seeds,
        IReadOnlyList<BriefChangeLine> Lines);

    /// <summary>The brief fields a change can honour, in the page's usual order.</summary>
    internal static readonly string[] ChangeableBriefFields =
    [
        BriefFieldConsumers.Language, BriefFieldConsumers.SourceLanguage, BriefFieldConsumers.LengthSec, BriefFieldConsumers.Voice,
        BriefFieldConsumers.DesignSystem, BriefFieldConsumers.OverlayCardAudio, BriefFieldConsumers.Avatar
    ];

    /// <summary>The brief fields that change the whole film: refused, with the reason a person reads.</summary>
    internal static readonly string[] FixedBriefFields =
    [
        BriefFieldConsumers.Format, BriefFieldConsumers.Resolution, BriefFieldConsumers.StylePreset, "styleId", "style", BriefFieldConsumers.Tone
    ];

    private static bool IsAuto(string? language) =>
        language is not null && (language.Trim().Length == 0 || string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>The line key of a brief field, the notes or a files role.</summary>
    internal static string BriefKeyOf(string field) => "brief:" + field;

    internal static string FilesKeyOf(RunBriefFileRole role) => "brief:files:" + role;

    private static string? FixedFieldOf(RunBrief change) =>
        change.Format is not null ? BriefFieldConsumers.Format
        : change.Resolution is not null ? BriefFieldConsumers.Resolution
        : change.StylePreset is not null ? BriefFieldConsumers.StylePreset
        : change.StyleId is not null ? "styleId"
        : change.Style is not null ? "style"
        : change.Tone is not null ? BriefFieldConsumers.Tone
        : null;

    private static StepChangeRequestResult BriefRefusal(int status, string code, string message, string field) =>
        StepChangeRequestResult.Refuse(status, code, message, new Dictionary<string, object?> { ["field"] = field });

    /// <summary>
    /// The run-settings part of a change: validated as a run start validates its brief, merged over the run's own, and the
    /// steps on <paramref name="reachable"/> that read each changed setting. A setting nothing on the path reads is refused
    /// (<see cref="AppInputChangeCodes.BriefNotUsed"/>). <paramref name="notes"/> are the run's own notes (recovered from its
    /// request); <paramref name="notesKnown"/> false means they could not be recovered (then they cannot be changed).
    /// </summary>
    private async Task<(PreparedBrief?, StepChangeRequestResult?)> PrepareBriefAsync(
        Project project, StepChangeSource source, AppManifest? page, RunBrief change, IReadOnlySet<int> reachable,
        string? notes, bool notesKnown, CancellationToken ct)
    {
        if (FixedFieldOf(change) is { } fixedField)
        {
            return (null, BriefRefusal(400, AppInputChangeCodes.BriefNotChangeable,
                $"'{fixedField}' changes the whole video, so it cannot be changed here. Run the app again instead.", fixedField));
        }

        if (change.Inputs is not null)
        {
            return (null, BriefRefusal(400, AppInputChangeCodes.BriefNotChangeable,
                "Send new values for the app's own fields under 'inputs', not inside 'brief'.", "inputs"));
        }

        bool clearLanguage = IsAuto(change.Language);
        bool clearSourceLanguage = IsAuto(change.SourceLanguage);
        RunBrief checkedBrief = change with
        {
            Language = clearLanguage ? null : change.Language,
            SourceLanguage = clearSourceLanguage ? null : change.SourceLanguage,
            Notes = null
        };
        if (RunBriefValidator.Validate(checkedBrief) is { } problem)
            return (null, Invalid([new("brief", problem)]));
        if (change.Notes is { Length: > MaxChangeNotesChars })
            return (null, Invalid([new("brief.notes", $"The notes can be at most {MaxChangeNotesChars} characters.")]));
        if (await RunBriefSplitter.ProjectProblemAsync(_db, project.Id, checkedBrief, ct) is { } projectProblem)
            return (null, Invalid([new("brief", projectProblem)]));

        StoredRunBrief old = (RunBriefJson.TryParse(source.Execution.RunBriefJson) ?? new StoredRunBrief()) with { Inputs = null };
        StoredRunBrief normalized = RunBriefSplitter.ToStored(checkedBrief) ?? new StoredRunBrief();
        StoredRunBrief next = old;
        if (change.Language is not null)
            next = next with { Language = clearLanguage ? null : normalized.Language };
        if (change.SourceLanguage is not null)
            next = next with { SourceLanguage = clearSourceLanguage ? null : normalized.SourceLanguage };
        if (change.LengthSec is not null)
            next = next with { LengthSec = normalized.LengthSec };
        if (change.DesignSystem is not null)
            next = next with { DesignSystem = normalized.DesignSystem };
        if (change.Voice is not null)
            next = next with { Voice = normalized.Voice };
        if (change.OverlayCardAudio is not null)
            next = next with { OverlayCardAudio = normalized.OverlayCardAudio == RunBrief.OverlayCardAudioAgent ? null : normalized.OverlayCardAudio };
        if (change.Avatar is not null)
            next = next with { Avatar = normalized.Avatar };
        if (change.SourceFiles is { Count: > 0 } groups)
        {
            HashSet<RunBriefFileRole> replaced = [.. groups.Select(g => g.Role!.Value)];
            List<RunBriefSourceFiles> files = [.. (old.SourceFiles ?? []).Where(g => g.Role is not { } r || !replaced.Contains(r))];
            files.AddRange(normalized.SourceFiles ?? []);
            next = next with { SourceFiles = files.Count > 0 ? [.. files.OrderBy(g => g.Role)] : null };
        }

        IReadOnlyList<WorkflowStep> steps = source.Steps;
        HashSet<int> seeds = [];
        List<BriefChangeLine> lines = [];

        // Each brief field whose value moved: the steps that read it.
        foreach (string field in ChangeableBriefFields)
        {
            JsonElement before = BriefFieldValue(old, field);
            JsonElement after = BriefFieldValue(next, field);
            if (string.Equals(before.GetRawText(), after.GetRawText(), StringComparison.Ordinal))
                continue;
            IReadOnlySet<int> readers = BriefConsumers(steps, reachable, field, null);
            if (readers.Count == 0)
            {
                return (null, BriefRefusal(409, AppInputChangeCodes.BriefNotUsed,
                    $"Nothing in this run uses “{BriefLabel(page, field, null)}”, so there is nothing to redo.", field));
            }

            seeds.UnionWith(readers);
            lines.Add(new BriefChangeLine(BriefKeyOf(field), BriefLabel(page, field, null),
                await BriefWordsAsync(project.Id, field, before, ct), await BriefWordsAsync(project.Id, field, after, ct)));
        }

        // Each files role whose files moved: the steps that take that role.
        foreach (RunBriefFileRole role in (change.SourceFiles ?? []).Select(g => g.Role!.Value).Distinct().Order())
        {
            List<Guid> before = FilesOf(old, role);
            List<Guid> after = FilesOf(next, role);
            if (before.SequenceEqual(after))
                continue;
            IReadOnlySet<int> readers = BriefConsumers(steps, reachable, "files", role);
            if (readers.Count == 0)
            {
                return (null, BriefRefusal(409, AppInputChangeCodes.BriefNotUsed,
                    $"Nothing in this run uses “{BriefLabel(page, "files", role)}”, so there is nothing to redo.", "sourceFiles"));
            }

            seeds.UnionWith(readers);
            lines.Add(new BriefChangeLine(FilesKeyOf(role), BriefLabel(page, "files", role),
                await FileWordsAsync(project.Id, before, ct), await FileWordsAsync(project.Id, after, ct)));
        }

        // The notes: every step that reads the request.
        bool notesChanged = change.Notes is not null
            && !string.Equals(change.Notes.Trim(), (notes ?? string.Empty).Trim(), StringComparison.Ordinal);
        string? newNotes = notes;
        if (notesChanged)
        {
            if (!notesKnown)
            {
                return (null, BriefRefusal(400, AppInputChangeCodes.BriefNotChangeable,
                    "This run's request was not written on its app page, so its notes cannot be changed here. Run it again instead.", NotesKey));
            }

            IReadOnlySet<int> readers = BriefConsumers(steps, reachable, NotesKey, null);
            if (readers.Count == 0)
            {
                return (null, BriefRefusal(409, AppInputChangeCodes.BriefNotUsed,
                    $"Nothing in this run uses “{BriefLabel(page, NotesKey, null)}”, so there is nothing to redo.", NotesKey));
            }

            seeds.UnionWith(readers);
            newNotes = change.Notes!.Trim().Length == 0 ? null : change.Notes.Trim();
            lines.Add(new BriefChangeLine(BriefKeyOf(NotesKey), BriefLabel(page, NotesKey, null), NotesWords(notes), NotesWords(newNotes)));
        }

        return (new PreparedBrief(next.IsEmpty ? null : next, newNotes, notesChanged, seeds, lines), null);
    }

    /// <summary>
    /// The steps on <paramref name="reachable"/> that read a run setting: a brief field (<see cref="BriefFieldConsumers"/>;
    /// the avatar only where it is honoured), the notes (every step that reads the request) or the files of
    /// <paramref name="role"/> (a step that takes that role, or a footage source that reads it). Never a Conditional.
    /// </summary>
    internal static IReadOnlySet<int> BriefConsumers(IEnumerable<WorkflowStep> steps, IReadOnlySet<int> reachable, string field, RunBriefFileRole? role)
    {
        HashSet<int> readers = [];
        foreach (WorkflowStep step in steps)
        {
            if (step.StepType == StepType.Conditional || !reachable.Contains(step.StepOrder))
                continue;
            AppStepFacts facts = AppStepFacts.From(step.StepType, AgentTypeOf(step), step.VideoAnalyzeConfigJson, step.VideoGenerateConfigJson, null, step.ImageGenerateConfigJson);
            bool reads = field switch
            {
                NotesKey => AppBindingAnalysis.ReadsRequest(step.StepType),
                "files" => role is { } r && ReadsFiles(step, facts, r),
                BriefFieldConsumers.Avatar => facts.TakesBriefAvatar,
                _ => BriefFieldConsumers.FieldsReadBy(facts).Contains(field)
            };
            if (reads)
                readers.Add(step.StepOrder);
        }

        return readers;
    }

    private static bool ReadsFiles(WorkflowStep step, AppStepFacts facts, RunBriefFileRole role)
    {
        string pageRole = role == RunBriefFileRole.InsertContent ? "insertContent" : role.ToString().ToLowerInvariant();
        if (BriefFieldConsumers.AcceptsFileRole(pageRole, [facts]))
            return true;
        if (step.StepType != StepType.VideoAnalyze || string.IsNullOrWhiteSpace(step.VideoAnalyzeConfigJson))
            return false;
        try
        {
            VideoAnalyzeStepConfig? config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(step.VideoAnalyzeConfigJson, StepConfigOptions);
            if (config is null)
                return false;
            IEnumerable<VideoSourceRef> sources = config.Sources is { Count: > 0 } many ? many : [config.Source];
            return sources.Any(s => s is { Kind: VideoSourceKind.BriefFiles } && s.Role == role);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>One brief field of a stored brief in the body's own shape (null when unset; <c>overlayCardAudio</c> unset reads as <c>agent</c>).</summary>
    internal static JsonElement BriefFieldValue(StoredRunBrief? brief, string field)
    {
        object? value = field switch
        {
            BriefFieldConsumers.Language => brief?.Language,
            BriefFieldConsumers.SourceLanguage => brief?.SourceLanguage,
            BriefFieldConsumers.LengthSec => brief?.LengthSec,
            BriefFieldConsumers.Voice => brief?.Voice,
            BriefFieldConsumers.DesignSystem => brief?.DesignSystem ?? RunBrief.DesignSystemProject,
            BriefFieldConsumers.OverlayCardAudio => brief?.OverlayCardAudio ?? RunBrief.OverlayCardAudioAgent,
            BriefFieldConsumers.Avatar => brief?.Avatar,
            _ => null
        };
        return JsonSerializer.SerializeToElement(value, RunBriefJson.Options);
    }

    private static List<Guid> FilesOf(StoredRunBrief? brief, RunBriefFileRole role) =>
        [.. (brief?.SourceFiles ?? []).Where(g => g.Role == role).SelectMany(g => g.FileIds ?? [])];

    /// <summary>The page's own label of a run setting, else a plain default.</summary>
    internal static string BriefLabel(AppManifest? page, string field, RunBriefFileRole? role)
    {
        AppManifestInput? input = (page?.Inputs ?? []).FirstOrDefault(i => field switch
        {
            NotesKey => i.Kind == AppManifestVocabulary.KindNotes,
            "files" => i.Kind == AppManifestVocabulary.KindFiles && role is { } r && string.Equals(i.Role, r.ToString(), StringComparison.OrdinalIgnoreCase),
            _ => i.Kind == AppManifestVocabulary.KindBrief && i.Field == field
        });
        if (!string.IsNullOrWhiteSpace(input?.Label))
            return input.Label.Trim();
        return field switch
        {
            BriefFieldConsumers.Language => "Language",
            BriefFieldConsumers.SourceLanguage => "Spoken language",
            BriefFieldConsumers.LengthSec => "Length",
            BriefFieldConsumers.Voice => "Narration",
            BriefFieldConsumers.DesignSystem => "Design",
            BriefFieldConsumers.OverlayCardAudio => "Sound under full-screen pictures",
            BriefFieldConsumers.Avatar => "Avatar",
            NotesKey => "Notes",
            "files" => role switch
            {
                RunBriefFileRole.Video => "Your clips",
                RunBriefFileRole.Music => "Music",
                RunBriefFileRole.Sfx => "Sound effects",
                RunBriefFileRole.Image => "Pictures",
                RunBriefFileRole.InsertContent => "What goes on the screen",
                RunBriefFileRole.Overlay => "Pictures over the film",
                _ => "Files"
            },
            _ => field
        };
    }

    private async Task<string> BriefWordsAsync(Guid projectId, string field, JsonElement value, CancellationToken ct)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return field switch
            {
                BriefFieldConsumers.Language or BriefFieldConsumers.SourceLanguage => "(automatic)",
                BriefFieldConsumers.Avatar => "(none)",
                _ => "(not set)"
            };
        }

        switch (field)
        {
            case BriefFieldConsumers.LengthSec:
                return value.TryGetInt32(out int seconds) ? $"{seconds} seconds" : value.GetRawText();
            case BriefFieldConsumers.DesignSystem:
                return value.GetString() == RunBrief.DesignSystemNone ? "No design system" : "The project's design";
            case BriefFieldConsumers.OverlayCardAudio:
                return value.GetString() switch
                {
                    RunBrief.OverlayCardAudioKeep => "Keep the film's sound",
                    RunBrief.OverlayCardAudioMute => "Mute the film's sound",
                    _ => "Decided per picture"
                };
            case BriefFieldConsumers.Voice:
                {
                    RunBriefVoice? voice = value.Deserialize<RunBriefVoice>(RunBriefJson.Options);
                    if (voice?.Mode == RunBriefVoiceMode.None)
                        return "No narration";
                    if (voice?.ProjectVoiceId is { } voiceId)
                    {
                        string? name = await _db.ProjectVoices.AsNoTracking()
                            .Where(v => v.Id == voiceId && v.ProjectId == projectId)
                            .Select(v => v.Name)
                            .FirstOrDefaultAsync(ct);
                        return string.IsNullOrWhiteSpace(name) ? "Narrated with a voice of this project" : $"Narrated by {name}";
                    }

                    return voice?.LibraryVoiceId is not null ? "Narrated with a library voice" : "Narrated with the default voice";
                }
            case BriefFieldConsumers.Avatar:
                return "An avatar of this project";
            default:
                return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        }
    }

    private async Task<string> FileWordsAsync(Guid projectId, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return "(none)";
        List<string> names = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId && ids.Contains(f.Id))
            .Select(f => f.OriginalFileName)
            .ToListAsync(ct);
        string words = names.Count == 0 ? $"{ids.Count} file(s)" : string.Join(", ", names.Order(StringComparer.Ordinal));
        return words.Length > 200 ? words[..200] + "…" : words;
    }

    private static string NotesWords(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return "(empty)";
        string text = notes.Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    /// <summary>
    /// The run settings a finished app run's page shows (docs/design/app-inputs.md 29), with the run's value and whether a
    /// change request can change each: a fixed setting (shape, size, style, tone) is not supported, nor one nothing on the
    /// run's path reads. Null when the page shows none.
    /// </summary>
    private async Task<IReadOnlyList<StepChangeBriefFieldResponse>?> BriefCapabilitiesAsync(
        Project project, StepChangeSource source, AppManifest page, IReadOnlyDictionary<string, JsonElement> values,
        bool finished, CancellationToken ct)
    {
        IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders([.. source.Steps.Select(AppBindingStep.From)], values);
        StoredRunBrief? brief = RunBriefJson.TryParse(source.Execution.RunBriefJson);
        bool notesKnown = TryRecoverNotes(page, values, source.Execution.UserRequest, out string? notes);
        Dictionary<int, WorkflowStep> byOrder = source.Steps.ToDictionary(s => s.StepOrder);
        List<StepChangeBriefFieldResponse> fields = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (AppManifestInput input in page.Inputs ?? [])
        {
            string key;
            string? field = null;
            RunBriefFileRole? role = null;
            JsonElement? value;
            string valueText;
            bool fixedSetting = false;
            switch (input.Kind)
            {
                case AppManifestVocabulary.KindBrief when input.Field is { } f:
                    field = f;
                    key = BriefKeyOf(f);
                    fixedSetting = !ChangeableBriefFields.Contains(f);
                    value = fixedSetting ? null : BriefFieldValue(brief, f);
                    valueText = fixedSetting ? string.Empty : await BriefWordsAsync(project.Id, f, value!.Value, ct);
                    break;
                case AppManifestVocabulary.KindNotes:
                    key = BriefKeyOf(NotesKey);
                    value = JsonSerializer.SerializeToElement(notesKnown ? notes ?? string.Empty : string.Empty);
                    valueText = NotesWords(notes);
                    break;
                case AppManifestVocabulary.KindFiles when Enum.TryParse(input.Role, ignoreCase: true, out RunBriefFileRole parsed):
                    role = parsed;
                    key = FilesKeyOf(parsed);
                    List<Guid> ids = FilesOf(brief, parsed);
                    value = JsonSerializer.SerializeToElement(ids);
                    valueText = await FileWordsAsync(project.Id, ids, ct);
                    break;
                default:
                    continue;
            }

            if (!seen.Add(key))
                continue;

            string readsKey = input.Kind == AppManifestVocabulary.KindNotes ? NotesKey : role is not null ? "files" : field!;
            IReadOnlySet<int> readers = fixedSetting ? new HashSet<int>() : BriefConsumers(source.Steps, reachable, readsKey, role);
            List<string> usedBy = [.. readers.Order().Select(o => byOrder.TryGetValue(o, out WorkflowStep? s) ? LabelOf(s) : $"Step {o}")];

            (bool supported, string? reason, string? code) = (true, null, null);
            if (!finished)
                (supported, reason, code) = (false, "the run has not finished", "RUN_NOT_FINISHED");
            else if (fixedSetting)
                (supported, reason, code) = (false, "it changes the whole video, so run it again instead", AppInputChangeCodes.BriefNotChangeable);
            else if (input.Kind == AppManifestVocabulary.KindNotes && !notesKnown)
                (supported, reason, code) = (false, "this run's request was not written on its app page", AppInputChangeCodes.BriefNotChangeable);
            else if (usedBy.Count == 0)
                (supported, reason, code) = (false, "nothing in this run uses it", AppInputChangeCodes.BriefNotUsed);

            fields.Add(new StepChangeBriefFieldResponse(
                key, input.Kind, field, role?.ToString(), BriefLabel(page, readsKey, role), value, valueText,
                supported, reason, code, usedBy, input));
        }

        return fields.Count == 0 ? null : fields;
    }
}
