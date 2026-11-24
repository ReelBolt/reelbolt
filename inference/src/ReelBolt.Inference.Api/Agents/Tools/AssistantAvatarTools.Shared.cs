using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>One project file an avatar is made from: its path in the project, and optionally what it shows.</summary>
/// <param name="Path">The file's path in the project (as ListProjectFiles gives it), or its name when that is unique.</param>
/// <param name="Role">
/// For an object: <c>Front</c>, <c>Angle</c> or <c>Detail</c> for a picture, <c>Motion</c> for a video. Left out, the
/// first picture is <c>Front</c>, every other picture <c>Angle</c> and a video <c>Motion</c>.
/// </param>
public sealed record AvatarFileArg(string? Path, string? Role = null);

// The shared half of the avatar tools: the argument shapes (one record per tool, parsed the same way for the tool and for
// its approval card), the switches, the plan, the statement, the media rule, and files and avatars by name.
public sealed partial class AssistantAvatarTools
{
    // ------------------------------------------------------------------ the words every description shares

    internal const string StatementNote =
        " The object statement (\"these files show an object, a product or a place and no identifiable person, and I may use them\") is confirmed by the user: in the app chat the approval card shows it and allowing the card is the confirmation; over MCP pass statement (the statement verbatim as the user read and accepted it; a call without it is answered with the text to show) and locale (en or it). locale is optional (default the user's language).";

    internal const string MediaNote =
        " allowMediaEgress is optional: true allows sending the avatars' pictures and videos to the AI service for this one call when the project has not recorded its one-time agreement (ListProjectAvatars mediaConsent); in the app chat the approval card then always asks, over MCP it counts only while the user has let connected tools send their media (Settings > Assistant). It never records the project's agreement.";

    internal const string CreateObjectShape =
        " name is the new avatar's name (at most 200 characters), description is optional. files is 1 to 8 pictures and at most 3 short videos of the project, each {path, role?}: path as ListProjectFiles gives it, role Front, Angle or Detail for a picture and Motion for a video (left out: the first picture Front, the other pictures Angle, a video Motion). No recordings. Making a person's avatar is only possible in the app's Avatars tab.";

    internal const string ExtractShape =
        " video is the path of a project video; objectDescription says which object (at most 300 characters, for example \"the red kettle\"); name is the new avatar's name and description is optional. atSeconds is up to 6 moments of the video in seconds (left out: evenly spaced). With an object-finding model and the permission to send the frames, each frame is shown to it and only the object is cut out; without, whole frames are used for free.";

    internal const string VariantShape =
        " original is the name of an OBJECT avatar of the project (a person's variants are made only in the app's Avatars tab); label names the variant (at most 60 characters, for example \"Red\"; it is shown as \"Car · Red\"); description says what sets it apart (at most 500 characters). Then EITHER files (other project files, each {path, role?} as for ProposeCreateObjectAvatar; free) OR instruction (at most 1000 characters: the picture model changes the original's pictures as asked; paid), with includeVideos true to also make its videos again from their changed first frame.";

    internal const string ClipShape =
        " subjects is the NAMES of one to three avatars or objects of the project (as ListProjectAvatars names them; at most one person). prompt says what happens in the clip (at most 2000 characters). model is optional: the name of a video model of the workspace (default the workspace's default video model). durationSeconds (default 6), ratio (16:9, 9:16, 1:1...; default 16:9), resolution (the model's size word, such as 768P) and takes (1 or 2, default 1) are optional. speech is optional: the path of a project WAV or MP3 the one chosen person says (a talking clip; one person and nothing else). subjectMode is optional: Auto, References or FirstFrame (how the avatars reach the model). The avatars' pictures and videos are sent to the video service.";

    // ------------------------------------------------------------------ the argument shapes

    /// <summary>The arguments of <c>ProposeCreateObjectAvatar</c> / <c>ApplyCreateObjectAvatar</c>.</summary>
    internal sealed record CreateObjectArgs(string? Name, IReadOnlyList<AvatarFileArg>? Files, string? Description, string? Locale, string? Statement, Guid? ProjectId)
    {
        public static CreateObjectArgs From(AIFunctionArguments args) => new(
            ToolArgs.GetString(args, "name"), FilesOf(args, "files"), ToolArgs.GetString(args, "description"),
            ToolArgs.GetString(args, "locale"), ToolArgs.GetString(args, "statement"), ToolArgs.GetGuid(args, "projectId"));
    }

    /// <summary>The arguments of <c>ProposeExtractObjectFromVideo</c> / <c>ApplyExtractObjectFromVideo</c>.</summary>
    internal sealed record ExtractArgs(
        string? Video, string? ObjectDescription, string? Name, string? Description, IReadOnlyList<double>? AtSeconds,
        string? Locale, string? Statement, bool? AllowMediaEgress, Guid? ProjectId)
    {
        public static ExtractArgs From(AIFunctionArguments args) => new(
            ToolArgs.GetString(args, "video"), ToolArgs.GetString(args, "objectDescription"), ToolArgs.GetString(args, "name"),
            ToolArgs.GetString(args, "description"), Read<List<double>>(args, "atSeconds"), ToolArgs.GetString(args, "locale"),
            ToolArgs.GetString(args, "statement"), ToolArgs.GetBool(args, "allowMediaEgress"), ToolArgs.GetGuid(args, "projectId"));
    }

    /// <summary>The arguments of <c>ProposeCreateAvatarVariant</c> / <c>ApplyCreateAvatarVariant</c>.</summary>
    internal sealed record VariantArgs(
        string? Original, string? Label, string? Description, IReadOnlyList<AvatarFileArg>? Files, string? Instruction,
        bool? IncludeVideos, string? Locale, string? Statement, bool? AllowMediaEgress, Guid? ProjectId)
    {
        public static VariantArgs From(AIFunctionArguments args) => new(
            ToolArgs.GetString(args, "original"), ToolArgs.GetString(args, "label"), ToolArgs.GetString(args, "description"),
            FilesOf(args, "files"), ToolArgs.GetString(args, "instruction"), ToolArgs.GetBool(args, "includeVideos"),
            ToolArgs.GetString(args, "locale"), ToolArgs.GetString(args, "statement"), ToolArgs.GetBool(args, "allowMediaEgress"),
            ToolArgs.GetGuid(args, "projectId"));
    }

    /// <summary>The arguments of <c>ProposeGenerateSubjectClip</c> / <c>ApplyGenerateSubjectClip</c>.</summary>
    internal sealed record ClipArgs(
        IReadOnlyList<string>? Subjects, string? Prompt, string? Model, int? DurationSeconds, string? Ratio, string? Resolution,
        int? Takes, string? Speech, string? SubjectMode, bool? AllowMediaEgress, Guid? ProjectId)
    {
        public static ClipArgs From(AIFunctionArguments args) => new(
            ToolArgs.GetStrings(args, "subjects"), ToolArgs.GetString(args, "prompt"), ToolArgs.GetString(args, "model"),
            Int(args, "durationSeconds"), ToolArgs.GetString(args, "ratio"), ToolArgs.GetString(args, "resolution"),
            Int(args, "takes"), ToolArgs.GetString(args, "speech"), ToolArgs.GetString(args, "subjectMode"),
            ToolArgs.GetBool(args, "allowMediaEgress"), ToolArgs.GetGuid(args, "projectId"));
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static IReadOnlyList<AvatarFileArg>? FilesOf(AIFunctionArguments args, string name) => Read<List<AvatarFileArg>>(args, name);

    private static T? Read<T>(AIFunctionArguments args, string name) where T : class
    {
        if (ToolArgs.Get(args, name) is not { } element)
            return null;
        try
        {
            return element.Deserialize<T>(WebJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Int(AIFunctionArguments args, string name) =>
        ToolArgs.Get(args, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out int value) ? value : null;

    // ------------------------------------------------------------------ switches, answers, refusals

    private bool PeopleOn => ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(_configuration, EnabledKey);

    private bool ObjectsOn => ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(_configuration, ObjectsEnabledKey);

    private bool KindOn(AvatarSubjectKind kind) => kind == AvatarSubjectKind.Object ? ObjectsOn : PeopleOn;

    /// <summary>The kind as the tools say it: <c>person</c> or <c>object</c>.</summary>
    internal static string KindWord(AvatarSubjectKind kind) => kind == AvatarSubjectKind.Object ? "object" : "person";

    private static string KindWord(string kind) =>
        string.Equals(kind, nameof(AvatarSubjectKind.Object), StringComparison.OrdinalIgnoreCase) ? "object" : "person";

    internal const string NotAvailableCode = "AVATARS_NOT_AVAILABLE";

    internal const string ObjectsNotAvailableMessage = "Object avatars are not available in this workspace.";

    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";

    internal const string PersonInAppMessage =
        "A person's avatar, and a variant of one, is made only by the user in the project's Avatars tab, with the person's consent. It cannot be done from here.";

    private static string Err(string message, string? code = null)
    {
        JsonObject body = new() { ["error"] = message };
        if (code is not null) body["code"] = code;
        return body.ToJsonString();
    }

    /// <summary>
    /// A typed refusal of a shared service as a tool answer: its plain sentence and code, plus only the extra members that
    /// carry words (a statement to show, a model's name, a plan feature), never one that could carry an id.
    /// </summary>
    internal static string FromRefusal(AvatarRefusal refusal)
    {
        if (refusal.StatusCode == StatusCodes.Status404NotFound && refusal.Code == "not_found")
            return Err(NotAvailableMessage, NotAvailableCode);

        JsonObject body = new() { ["error"] = refusal.Message, ["code"] = refusal.Code };
        if (refusal.Extra is { } extra)
        {
            foreach (string key in new[] { "requiredStatement", "statementVersion", "locale", "statements", "sibling", "feature", "missing", "retryAfterSeconds", "models", "errors", "reason", "field", "required", "limit", "capability", "canConfigure" })
            {
                if (extra.TryGetValue(key, out object? value) && value is not null)
                    body[key] = JsonSerializer.SerializeToNode(value, value.GetType(), Json);
            }
        }

        return body.ToJsonString();
    }

    private string? EmailRefusal() =>
        _currentUser?.EmailVerified == true ? null : Err(EmailVerificationGate.Message, EmailVerificationGate.ErrorCode);

    /// <summary>The plan feature of <paramref name="kind"/> (<c>avatars</c> / <c>avatar_objects</c>), re-read on this call.</summary>
    private async Task<string?> PlanRefusalAsync(Project project, AvatarSubjectKind kind, CancellationToken ct)
    {
        if (_entitlements is null)
            return null;
        OrganizationEntitlementSet set = await _entitlements.GetAsync(project.OrganizationId, ct);
        PlanGateViolation? violation = kind == AvatarSubjectKind.Object ? PlanGate.CheckAvatarObjects(set) : PlanGate.CheckAvatars(set);
        return violation is null
            ? null
            : new JsonObject { ["error"] = violation.Message, ["code"] = violation.Code, ["feature"] = violation.Feature }.ToJsonString();
    }

    // ------------------------------------------------------------------ the object statement

    /// <summary>
    /// The language the object statement is shown in: the one asked for (<c>en</c> or <c>it</c>), else the user's saved
    /// language when the statement exists in it, else English. An unknown locale is refused.
    /// </summary>
    private async Task<(string? Locale, string? Error)> StatementLocaleAsync(string? asked, Guid userId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(asked))
        {
            string locale = asked.Trim().ToLowerInvariant();
            return AvatarObjectStatement.For(locale) is null
                ? (null, Err($"locale must be one of: {string.Join(", ", AvatarObjectStatement.Locales)}.", "locale_invalid"))
                : (locale, null);
        }

        string? saved = await _db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Locale)
            .FirstOrDefaultAsync(ct);
        return (saved is not null && AvatarObjectStatement.For(saved) is not null ? saved : "en", null);
    }

    /// <summary>What a call that did not confirm the object statement reads: the text to show, in its language and the others.</summary>
    private static string StatementRequired(string locale, bool mcp) => new JsonObject
    {
        ["error"] = mcp
            ? "Making an object avatar needs the user's object statement. Show them the text in requiredStatement and, once they accept it, pass it verbatim as statement with its locale."
            : "The object statement was not confirmed on the approval card, so nothing was made.",
        ["code"] = "object_statement_required",
        ["requiredStatement"] = AvatarObjectStatement.For(locale) ?? AvatarObjectStatement.English,
        ["statementVersion"] = AvatarObjectStatement.Version,
        ["locale"] = AvatarObjectStatement.For(locale) is null ? "en" : locale,
        ["statements"] = new JsonObject { ["en"] = AvatarObjectStatement.English, ["it"] = AvatarObjectStatement.Italian },
    }.ToJsonString();

    /// <summary>
    /// Whether the member confirmed the object statement of <paramref name="locale"/> for THIS call: over MCP the caller
    /// sent it verbatim; in the app chat the approval card showed it and the person allowed the card
    /// (<see cref="AssistantStatementApproval"/>, which only the gate sets). Anything else is no confirmation.
    /// </summary>
    private static bool StatementConfirmed(AssistantToolContext context, string locale, string? statement)
    {
        if (context.Initiator == ExecutionInitiator.Mcp)
            return AvatarObjectStatement.Matches(locale, statement);

        return context.ChatApproved
               && AssistantStatementApproval.Approved is { } shown
               && string.Equals(shown.Version, AvatarObjectStatement.Version, StringComparison.Ordinal)
               && string.Equals(shown.Locale, locale, StringComparison.Ordinal)
               && AvatarObjectStatement.Matches(locale, shown.Text);
    }

    /// <summary>
    /// Over MCP a proposal already needs the statement (the collapsed call runs both halves at once); in chat the card
    /// asks for it, so a proposal does not.
    /// </summary>
    private static string? ProposalStatementRefusal(AssistantToolContext context, string locale, string? statement) =>
        context.Initiator == ExecutionInitiator.Mcp && !AvatarObjectStatement.Matches(locale, statement)
            ? StatementRequired(locale, mcp: true)
            : null;

    /// <summary>The object statement of <paramref name="locale"/> as a card shows it.</summary>
    internal static ApprovalStatement CardStatement(string locale) =>
        new(AvatarObjectStatement.Version, locale, AvatarObjectStatement.For(locale) ?? AvatarObjectStatement.English);

    // ------------------------------------------------------------------ the media rule

    internal const string MediaNotAllowedCode = "media_egress_not_allowed";

    /// <summary>
    /// Whether THIS call may send the avatars' media although the project's agreement is not recorded, by the one rule
    /// (<see cref="IMediaEgressCallPermission"/>): only a call that says <c>allowMediaEgress: true</c> asks it; in chat
    /// an always-asked card that said so, over MCP the user's own switch. A call that asked and may not is refused.
    /// The project's recorded agreement is read by each service itself.
    /// </summary>
    private async Task<(bool MediaAllowed, string? Refusal)> MediaForThisCallAsync(
        AssistantToolContext context, Project project, MediaEgressSurface surface, bool? allowMediaEgress, CancellationToken ct)
    {
        if (allowMediaEgress != true || project.SubjectMediaConsentAt is not null)
            return (false, null);

        string? grantor = await _mediaEgress.GrantorAsync(MediaEgressCall.FromTool(surface, context, project.Id), ct);
        return grantor is not null ? (true, null) : (false, MediaNotAllowedJson(context));
    }

    private static string MediaNotAllowedJson(AssistantToolContext context) => Err(
        context.Initiator == ExecutionInitiator.Mcp
            ? AssistantToolProvider.MediaEgressMcpMessage
            : "Sending the media for this one call was not approved on the card, so nothing was started. " + MediaEgressPermission.NotAllowedHereMessage,
        MediaNotAllowedCode);

    /// <summary>The card asks for the media permission: the call says so and the project has not recorded its agreement.</summary>
    internal static bool AsksForMedia(Project project, bool? allowMediaEgress) =>
        allowMediaEgress == true && project.SubjectMediaConsentAt is null;

    // ------------------------------------------------------------------ files and avatars by name

    /// <summary>One project file an avatar is made from, resolved.</summary>
    internal sealed record ObjectFile(ProjectFile File, string Path, AvatarReferenceKind Kind, AvatarReferenceRole Role);

    /// <summary>
    /// The project's non-quarantined file named by <paramref name="path"/>: its stored path exactly, else its file name
    /// when exactly one file has it. The answer names files by path, never by id.
    /// </summary>
    private async Task<(ProjectFile? File, string? Error)> FileByPathAsync(Guid projectId, string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, Err("a file path is empty: pass the path ListProjectFiles gives.", "file_path_required"));

        string normalized;
        try
        {
            normalized = ProjectFilePath.NormalizeRelativePath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return (null, Err($"\"{path}\" is not a file path of this project.", "file_not_found"));
        }

        List<ProjectFile> candidates = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId && f.CsamStatus != CsamScanStatus.Quarantined
                        && f.ModerationStatus != FileModerationStatus.Quarantined
                        && (f.OriginalPath == normalized || f.OriginalFileName == normalized))
            .ToListAsync(ct);
        List<ProjectFile> exact = [.. candidates.Where(f => f.OriginalPath == normalized)];
        if (exact.Count > 0)
            return (exact.OrderByDescending(f => f.UploadedAt).First(), null);
        if (candidates.Count > 1)
        {
            return (null, Err("several files are called \"" + normalized + "\"; give the full path: " +
                string.Join(", ", candidates.Select(f => f.OriginalPath ?? f.OriginalFileName).Order(StringComparer.Ordinal)), "file_ambiguous"));
        }

        return candidates.Count == 1
            ? (candidates[0], null)
            : (null, Err($"no file \"{normalized}\" is in this project.", "file_not_found"));
    }

    private static string PathOf(ProjectFile file) => file.OriginalPath ?? ProjectFilePath.CombineDirectoryAndFileName(file.DirectoryPath, file.OriginalFileName);

    /// <summary>
    /// The files an OBJECT avatar is made from, in the order given: each a picture or a video of the project, its role
    /// (asked for, or the default), named once. The counts and every attach check are the shared creator's.
    /// </summary>
    private async Task<(IReadOnlyList<ObjectFile>? Files, string? Error)> ObjectFilesAsync(
        Guid projectId, IReadOnlyList<AvatarFileArg>? files, CancellationToken ct)
    {
        if (files is not { Count: > 0 })
            return (null, Err("files needs at least one picture of the object: each {path, role?}.", "files_required"));
        int most = ProjectAvatarLimits.MaxObjectImages + ProjectAvatarLimits.MaxObjectVideos;
        if (files.Count > most)
            return (null, Err($"an object avatar takes at most {ProjectAvatarLimits.MaxObjectImages} pictures and {ProjectAvatarLimits.MaxObjectVideos} videos.", "too_many_files"));

        List<ObjectFile> resolved = [];
        bool front = false;
        foreach (AvatarFileArg asked in files)
        {
            (ProjectFile? file, string? error) = await FileByPathAsync(projectId, asked?.Path, ct);
            if (file is null)
                return (null, error);
            if (resolved.Any(r => r.File.Id == file.Id))
                return (null, Err($"\"{PathOf(file)}\" is named twice.", "file_named_twice"));

            AvatarReferenceKind kind = file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? AvatarReferenceKind.Image
                : file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? AvatarReferenceKind.Video
                : AvatarReferenceKind.Audio;
            if (kind == AvatarReferenceKind.Audio)
                return (null, Err($"\"{PathOf(file)}\" is not a picture or a video; an object avatar takes pictures and short videos only.", "reference_kind_not_allowed"));

            AvatarReferenceRole role;
            if (!string.IsNullOrWhiteSpace(asked!.Role))
            {
                if (!Enum.TryParse(asked.Role.Trim(), ignoreCase: true, out role) || !Enum.IsDefined(role)
                    || !ProjectAvatarLimits.RoleFits(AvatarSubjectKind.Object, kind, role))
                {
                    return (null, Err(kind == AvatarReferenceKind.Image
                        ? $"the role of the picture \"{PathOf(file)}\" must be Front, Angle or Detail."
                        : $"the role of the video \"{PathOf(file)}\" must be Motion.", "role_invalid"));
                }
            }
            else
            {
                role = kind == AvatarReferenceKind.Video ? AvatarReferenceRole.Motion : front ? AvatarReferenceRole.Angle : AvatarReferenceRole.Front;
            }

            front |= kind == AvatarReferenceKind.Image && role == AvatarReferenceRole.Front;
            resolved.Add(new ObjectFile(file, PathOf(file), kind, role));
        }

        return (resolved, null);
    }

    /// <summary>The avatars of the project of the kinds whose switch is on: the set names are matched in.</summary>
    private async Task<List<ProjectAvatar>> AvatarsOfEnabledKindsAsync(Guid projectId, CancellationToken ct)
    {
        bool people = PeopleOn;
        bool objects = ObjectsOn;
        return await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == projectId
                        && ((people && a.SubjectKind == AvatarSubjectKind.Person) || (objects && a.SubjectKind == AvatarSubjectKind.Object)))
            .ToListAsync(ct);
    }

    /// <summary>The canonical payload a token is bound to: the operation and its resolved targets, never the model's words alone.</summary>
    private static string Payload(object canonical) => JsonSerializer.Serialize(canonical, Json);

    private string Mint(string operation, string payload, AssistantToolContext context) =>
        _tokens.Compute(operation, payload, context.UserId, _currentUser?.OrganizationId);

    private bool Verify(string operation, string payload, AssistantToolContext context, string? presented) =>
        _tokens.Verify(operation, payload, context.UserId, _currentUser?.OrganizationId, presented ?? string.Empty);
}
