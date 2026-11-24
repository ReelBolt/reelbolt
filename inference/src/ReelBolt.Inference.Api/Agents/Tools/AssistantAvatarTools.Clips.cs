using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

// A clip that shows avatars and objects, from the assistant and MCP (docs/design/avatar-objects.md "Generating a clip with
// avatars from the editor (subject clips)"): the editor's own ISubjectClipService prices it, checks it and starts the one
// hidden run, so the tool and the editor cannot disagree. Paid.
public sealed partial class AssistantAvatarTools
{
    internal const string OpGenerateSubjectClip = "GenerateSubjectClip";

    /// <summary>What both halves of <c>GenerateSubjectClip</c> decided, and the token payload; the card reads it too.</summary>
    internal sealed record ClipPrepared(
        Project Project, IReadOnlyList<ProjectAvatar> Subjects, SubjectClipRequest Request, string? ModelName, string? SpeechPath,
        bool AllowMediaEgress, SubjectClipEstimate Estimate, string Payload);

    /// <summary>
    /// Everything both halves of <c>GenerateSubjectClip</c> decide the same way: a switch, Edit (re-read now), the subjects
    /// by name among the kinds switched on, the model by name, the speech file by path, and the service's own estimate (its
    /// switch, email, plan, subject rule, model path and settings checks). A missing media agreement is refused here
    /// unless the call says <c>allowMediaEgress: true</c>; the start asks the one rule.
    /// </summary>
    internal async Task<(ClipPrepared? Prepared, string? Error)> PrepareClipAsync(AssistantToolContext context, ClipArgs args, CancellationToken ct)
    {
        // The project first, so a foreign project reads exactly like a missing one whatever the switches say.
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, args.ProjectId, out string scopeError);
        if (project is null)
            return (null, scopeError);
        if ((!PeopleOn && !ObjectsOn) || _clips is null)
            return (null, Err(NotAvailableMessage, NotAvailableCode));


        if (args.Subjects is not { Count: > 0 } named)
            return (null, Err("subjects needs one to three names of avatars or objects of the project (as ListProjectAvatars names them).", "subjects_required"));
        if (named.Count > ProjectSubjectLimits.MaxSubjects)
            return (null, Err($"at most {ProjectSubjectLimits.MaxSubjects} avatars and objects can be shown in one clip.", "too_many_subjects"));
        (IReadOnlyList<ProjectAvatar>? subjects, string? unknown) = ProjectSubjectNames.Resolve(await AvatarsOfEnabledKindsAsync(project.Id, ct), named);
        if (subjects is null)
            return (null, Err(unknown!, "subject_not_found"));
        if (subjects.Select(a => a.Id).Distinct().Count() != subjects.Count)
            return (null, Err("an avatar or object is named twice.", "subject_named_twice"));

        ProjectFile? speech = null;
        if (!string.IsNullOrWhiteSpace(args.Speech))
        {
            (speech, string? speechError) = await FileByPathAsync(project.Id, args.Speech, ct);
            if (speech is null)
                return (null, speechError);
        }

        // A speech file makes a talking clip: its model is a LipSync one, else a VideoGeneration one.
        (Guid? capabilityId, string? modelName, string? modelError) = await VideoModelAsync(project, args.Model, lipSync: speech is not null, ct);
        if (modelError is not null)
            return (null, modelError);

        SubjectClipRequest request = new(
            [.. subjects.Select(a => a.Id)], args.Prompt, capabilityId, args.DurationSeconds, args.Ratio, args.Resolution,
            args.Takes, speech?.Id, args.SubjectMode);
        bool allow = args.AllowMediaEgress == true;
        SubjectClipOutcome<SubjectClipEstimate> estimate = await _clips.EstimateAsync(project, ClipActor(context), request, mediaAllowed: allow, ct);
        if (estimate.Refusal is { } refusal)
            return (null, FromRefusal(refusal));

        SubjectClipEstimate priced = estimate.Value!;
        if (priced.UnavailableReason is { } why)
        {
            // The project has not recorded its one-time agreement: this one call may still allow it, on the card in chat or
            // under the user's own switch over MCP. Anything else that stops the clip is said as it is.
            return (null, string.Equals(priced.UnavailableCode, SubjectClipCodes.ConsentRequired, StringComparison.Ordinal)
                ? Err("This project has not allowed sending the pictures and videos of its avatars and objects to the AI video service. " +
                      "The user can allow it once in the project's Avatars tab, or for this one clip with allowMediaEgress: true (in the app chat the card then asks them).",
                      SubjectClipCodes.ConsentRequired)
                : Err(why, priced.UnavailableCode ?? SubjectClipCodes.ModelMissing));
        }

        string payload = Payload(new
        {
            op = OpGenerateSubjectClip,
            projectId = project.Id,
            subjects = request.SubjectIds,
            prompt = request.Prompt,
            model = request.CapabilityId,
            durationSeconds = request.DurationSeconds,
            ratio = request.Ratio,
            resolution = request.Resolution,
            takes = request.Takes,
            speech = request.SpeechFileId,
            subjectMode = request.SubjectMode,
            allowMediaEgress = allow ? true : (bool?)null,
        });
        return (new ClipPrepared(project, subjects, request, modelName, speech is null ? null : PathOf(speech), allow, priced, payload), null);
    }

    private async Task<string> ProposeGenerateSubjectClipAsync(AssistantToolContext context, ClipArgs args, CancellationToken ct)
    {
        (ClipPrepared? prepared, string? error) = await PrepareClipAsync(context, args, ct);
        if (prepared is null)
            return error!;

        SubjectClipEstimate estimate = prepared.Estimate;
        return JsonSerializer.Serialize(new
        {
            operation = OpGenerateSubjectClip,
            confirmationToken = Mint(OpGenerateSubjectClip, prepared.Payload, context),
            summary = $"Make a clip in the project \"{prepared.Project.Name}\" that shows {SubjectsWords(prepared.Subjects)}. Their pictures and videos are sent to the AI video service.",
            projectName = prepared.Project.Name,
            subjects = prepared.Subjects.Select(a => new { name = a.Name, kind = KindWord(a.SubjectKind) }).ToArray(),
            model = prepared.ModelName,
            credits = estimate.BillingEnabled ? estimate.CreditsHigh ?? estimate.Credits : null,
            affordable = estimate.Affordable,
            stillIncluded = estimate.StillIncluded,
        }, Json);
    }

    private async Task<string> ApplyGenerateSubjectClipAsync(
        AssistantToolContext context, ClipArgs args, string? confirmationToken, CancellationToken ct)
    {
        (ClipPrepared? prepared, string? error) = await PrepareClipAsync(context, args, ct);
        if (prepared is null)
            return error!;
        if (!Verify(OpGenerateSubjectClip, prepared.Payload, context, confirmationToken))
            return Err(StaleTokenMessage);

        (bool mediaAllowed, string? mediaRefusal) = await MediaForThisCallAsync(
            context, prepared.Project, MediaEgressSurface.SubjectClip, args.AllowMediaEgress, ct);
        if (mediaRefusal is not null)
            return mediaRefusal;

        SubjectClipOutcome<SubjectClipStarted> started = await _clips!.StartAsync(
            prepared.Project, ClipActor(context), prepared.Request, mediaAllowed, ct);
        if (started.Refusal is { } refused)
            return FromRefusal(refused);

        return JsonSerializer.Serialize(new
        {
            started = true,
            // The run's handle, like every run-starting tool's: GetExecutionStatus and WatchExecution take it.
            executionId = started.Value!.ExecutionId,
            state = started.Value.State,
            subjects = prepared.Subjects.Select(a => new { name = a.Name, kind = KindWord(a.SubjectKind) }).ToArray(),
            credits = started.Value.Credits,
            projectName = prepared.Project.Name,
            hint = "The clip is being made; it does not wait. Check it with GetExecutionStatus using this executionId (or WatchExecution in chat). Each take is added to the project's files as a video when it is done.",
        }, Json);
    }

    private SubjectClipActor ClipActor(AssistantToolContext context) => new(
        context.UserId,
        _currentUser?.EmailVerified == true,
        context.Initiator == ExecutionInitiator.Mcp ? ExecutionInitiator.Mcp : ExecutionInitiator.Assistant);

    /// <summary>
    /// The <c>VideoGeneration</c> model (a <c>LipSync</c> one for a clip with speech) a name means: one of the workspace's or the platform's enabled models, by its
    /// display name or its model code (exactly, then without regard to case). No name = the default model (null id).
    /// </summary>
    private async Task<(Guid? Id, string? Name, string? Error)> VideoModelAsync(Project project, string? asked, bool lipSync, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asked))
            return (null, null, null);

        string wanted = asked.Trim();
        Guid organizationId = project.OrganizationId;
        var models = await _db.ModelCapabilities.AsNoTracking()
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(c => c.Capability == (lipSync ? InferenceProviderCapability.LipSync : InferenceProviderCapability.VideoGeneration) && c.IsEnabled
                        && (c.OrganizationId == null || c.OrganizationId == organizationId))
            .Select(c => new { c.Id, c.Model.DisplayName, c.Model.ModelCode })
            .ToListAsync(ct);
        static string NameOf(string? display, string code) => string.IsNullOrWhiteSpace(display) ? code : display;

        var matches = models.Where(m => string.Equals(m.DisplayName, wanted, StringComparison.Ordinal) || string.Equals(m.ModelCode, wanted, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            matches = [.. models.Where(m => string.Equals(m.DisplayName?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(m.ModelCode.Trim(), wanted, StringComparison.OrdinalIgnoreCase))];
        }

        if (matches.Count == 1)
            return (matches[0].Id, NameOf(matches[0].DisplayName, matches[0].ModelCode), null);

        string offered = models.Count == 0
            ? lipSync ? " No lip-sync model is set up for this workspace." : " No video model is set up for this workspace."
            : " Video models that can be used: " + string.Join(", ", models.Select(m => "\"" + NameOf(m.DisplayName, m.ModelCode) + "\"").Distinct().Order(StringComparer.Ordinal).Take(20)) + ".";
        return matches.Count == 0
            ? (null, null, Err($"no video model called \"{wanted}\" is available here." + offered, SubjectClipCodes.ModelNotFound))
            : (null, null, Err($"\"{wanted}\" could mean more than one video model; use the exact name." + offered, SubjectClipCodes.ModelNotFound));
    }

    internal static string SubjectsWords(IReadOnlyList<ProjectAvatar> subjects) =>
        string.Join(", ", subjects.Select(a => $"\"{a.Name}\" ({KindWord(a.SubjectKind)})"));
}
