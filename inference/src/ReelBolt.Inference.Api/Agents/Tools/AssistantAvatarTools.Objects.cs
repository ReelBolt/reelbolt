using System.Text.Json;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Builds;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

// Object avatars from the assistant and MCP (docs/design/avatar-objects.md "Avatar tools for the assistant and MCP",
// "Pulling an object out of a video"): one made from project files through the ONE create path of an object avatar
// (ObjectAvatarCreator), and one pulled out of a video through the build service. Both always ask in chat.
public sealed partial class AssistantAvatarTools
{
    internal const string OpCreateObjectAvatar = "CreateObjectAvatar";
    internal const string OpExtractObjectFromVideo = "ExtractObjectFromVideo";

    /// <summary>What both halves of <c>CreateObjectAvatar</c> decided, and the token payload; the card reads it too.</summary>
    internal sealed record CreateObjectPrepared(
        Project Project, string Name, string? Description, string Locale, IReadOnlyList<ObjectFile> Files, string Payload);

    /// <summary>What both halves of <c>ExtractObjectFromVideo</c> decided, and the token payload; the card reads it too.</summary>
    internal sealed record ExtractPrepared(
        Project Project, ProjectFile Video, string VideoPath, string ObjectDescription, string Name, string? Description,
        IReadOnlyList<double>? AtSeconds, string Locale, bool AllowMediaEgress, ExtractionEstimate Estimate, string Payload);

    // ------------------------------------------------------------------ CreateObjectAvatar

    /// <summary>
    /// Everything both halves of <c>CreateObjectAvatar</c> decide the same way, in the REST create's order: the objects
    /// switch, Edit on the project (re-read now), a verified email, the plan <c>avatar_objects</c>, the name, the files by
    /// path with their roles, the statement's language. Every attach check and the person check stay the creator's.
    /// </summary>
    internal async Task<(CreateObjectPrepared? Prepared, string? Error)> PrepareCreateObjectAsync(
        AssistantToolContext context, CreateObjectArgs args, CancellationToken ct)
    {
        // The project first, so a foreign project reads exactly like a missing one whatever the switches say.
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, args.ProjectId, out string scopeError);
        if (project is null)
            return (null, scopeError);
        if (!ObjectsOn || _objectCreator is null)
            return (null, Err(ObjectsNotAvailableMessage, NotAvailableCode));

        if (EmailRefusal() is { } unverified)
            return (null, unverified);
        if (await PlanRefusalAsync(project, AvatarSubjectKind.Object, ct) is { } notEntitled)
            return (null, notEntitled);
        // The person check needs a vision model: said before an approval card is shown, not after the member approved.
        if (await _objectCreator.VisionRefusalAsync(ct) is { } noVision)
            return (null, FromRefusal(noVision));

        string name = args.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > ProjectAvatarLimits.MaxNameLength || name.Any(char.IsControl))
            return (null, Err($"Give the object a name of at most {ProjectAvatarLimits.MaxNameLength} characters.", "name_invalid"));
        string? description = string.IsNullOrWhiteSpace(args.Description) ? null : args.Description.Trim();
        if (description is { Length: > ProjectAvatarLimits.MaxDescriptionLength })
            return (null, Err($"The description can be at most {ProjectAvatarLimits.MaxDescriptionLength} characters.", "description_too_long"));

        (IReadOnlyList<ObjectFile>? files, string? fileError) = await ObjectFilesAsync(project.Id, args.Files, ct);
        if (files is null)
            return (null, fileError);
        if (!files.Any(f => f.Kind == AvatarReferenceKind.Image))
            return (null, Err("An object avatar needs at least one picture of the object.", "no_image_reference"));

        (string? locale, string? localeError) = await StatementLocaleAsync(args.Locale, context.UserId, ct);
        if (locale is null)
            return (null, localeError);

        string payload = Payload(new
        {
            op = OpCreateObjectAvatar,
            projectId = project.Id,
            name,
            description,
            locale,
            files = files.Select(f => new { id = f.File.Id, role = f.Role.ToString() }).ToArray(),
        });
        return (new CreateObjectPrepared(project, name, description, locale, files, payload), null);
    }

    private async Task<string> ProposeCreateObjectAvatarAsync(AssistantToolContext context, CreateObjectArgs args, CancellationToken ct)
    {
        (CreateObjectPrepared? prepared, string? error) = await PrepareCreateObjectAsync(context, args, ct);
        if (prepared is null)
            return error!;
        if (ProposalStatementRefusal(context, prepared.Locale, args.Statement) is { } statement)
            return statement;

        return JsonSerializer.Serialize(new
        {
            operation = OpCreateObjectAvatar,
            confirmationToken = Mint(OpCreateObjectAvatar, prepared.Payload, context),
            summary = $"Make the object avatar \"{prepared.Name}\" in the project \"{prepared.Project.Name}\" from {FilesWords(prepared.Files)}. Its pictures are checked for people first. Free.",
            projectName = prepared.Project.Name,
            name = prepared.Name,
            files = prepared.Files.Select(f => new { path = f.Path, role = f.Role.ToString() }).ToArray(),
            statement = AvatarObjectStatement.For(prepared.Locale),
            statementLocale = prepared.Locale,
            credits = "Free",
        }, Json);
    }

    private async Task<string> ApplyCreateObjectAvatarAsync(
        AssistantToolContext context, CreateObjectArgs args, string? confirmationToken, CancellationToken ct)
    {
        (CreateObjectPrepared? prepared, string? error) = await PrepareCreateObjectAsync(context, args, ct);
        if (prepared is null)
            return error!;
        if (!Verify(OpCreateObjectAvatar, prepared.Payload, context, confirmationToken))
            return Err(StaleTokenMessage);
        if (!StatementConfirmed(context, prepared.Locale, args.Statement))
            return StatementRequired(prepared.Locale, context.Initiator == ExecutionInitiator.Mcp);

        ObjectAvatarCreateOutcome created = await _objectCreator!.CreateAsync(
            prepared.Project,
            context.UserId,
            new ObjectAvatarCreateRequest(
                prepared.Name, prepared.Description, prepared.Locale, AvatarObjectStatement.For(prepared.Locale),
                [.. prepared.Files.Select(f => new AvatarReferenceAsk(f.File.Id, f.Role.ToString()))],
                Via: ViaWord(context)),
            ct);
        if (created.Refusal is { } refused)
            return FromRefusal(refused);

        return CreatedJson(created.Avatar!, prepared.Project, prepared.Files);
    }

    // ------------------------------------------------------------------ ExtractObjectFromVideo

    /// <summary>
    /// Everything both halves of <c>ExtractObjectFromVideo</c> decide the same way: the objects switch, Edit (re-read now),
    /// the video by path, the words, the moments, the statement's language and the build service's own estimate (its
    /// switch, email and plan checks, and whether the object-finding model would be used). Every other check is the
    /// build service's at the start.
    /// </summary>
    internal async Task<(ExtractPrepared? Prepared, string? Error)> PrepareExtractAsync(
        AssistantToolContext context, ExtractArgs args, CancellationToken ct)
    {
        // The project first, so a foreign project reads exactly like a missing one whatever the switches say.
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, args.ProjectId, out string scopeError);
        if (project is null)
            return (null, scopeError);
        if (!ObjectsOn || _builds is null)
            return (null, Err(ObjectsNotAvailableMessage, NotAvailableCode));


        (ProjectFile? video, string? fileError) = await FileByPathAsync(project.Id, args.Video, ct);
        if (video is null)
            return (null, fileError);
        if (!video.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return (null, Err($"\"{PathOf(video)}\" is not a video.", "not_a_video"));

        string objectDescription = args.ObjectDescription?.Trim() ?? string.Empty;
        if (objectDescription.Length == 0 || objectDescription.Length > AvatarBuildJob.MaxObjectDescriptionLength || objectDescription.Any(char.IsControl))
        {
            return (null, Err($"Say which object to take out of the video in at most {AvatarBuildJob.MaxObjectDescriptionLength} characters, for example \"the red kettle\".",
                "object_description_invalid"));
        }

        string name = args.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > ProjectAvatarLimits.MaxNameLength || name.Any(char.IsControl))
            return (null, Err($"Give the object a name of at most {ProjectAvatarLimits.MaxNameLength} characters.", "name_invalid"));
        string? description = string.IsNullOrWhiteSpace(args.Description) ? null : args.Description.Trim();

        IReadOnlyList<double>? at = args.AtSeconds is { Count: > 0 } asked ? [.. asked.Distinct().Order()] : null;
        if (at is { Count: > AvatarBuildService.MaxExtractionMoments })
            return (null, Err($"Name at most {AvatarBuildService.MaxExtractionMoments} moments of the video, in seconds from its start.", "at_seconds_invalid"));

        (string? locale, string? localeError) = await StatementLocaleAsync(args.Locale, context.UserId, ct);
        if (locale is null)
            return (null, localeError);

        bool allow = args.AllowMediaEgress == true;
        AvatarBuildOutcome<ExtractionEstimate> estimate = await _builds.EstimateExtractionAsync(
            project, Actor(context), at?.Count, mediaAllowed: allow, ct);
        if (estimate.Refusal is { } refusal)
            return (null, FromRefusal(refusal));

        string payload = Payload(new
        {
            op = OpExtractObjectFromVideo,
            projectId = project.Id,
            video = video.Id,
            objectDescription,
            name,
            description,
            at,
            locale,
            allowMediaEgress = allow ? true : (bool?)null,
        });
        return (new ExtractPrepared(project, video, PathOf(video), objectDescription, name, description, at, locale, allow, estimate.Value!, payload), null);
    }

    private async Task<string> ProposeExtractObjectFromVideoAsync(AssistantToolContext context, ExtractArgs args, CancellationToken ct)
    {
        (ExtractPrepared? prepared, string? error) = await PrepareExtractAsync(context, args, ct);
        if (prepared is null)
            return error!;
        if (ProposalStatementRefusal(context, prepared.Locale, args.Statement) is { } statement)
            return statement;

        ExtractionEstimate estimate = prepared.Estimate;
        return JsonSerializer.Serialize(new
        {
            operation = OpExtractObjectFromVideo,
            confirmationToken = Mint(OpExtractObjectFromVideo, prepared.Payload, context),
            summary = $"Take {Quoted(prepared.ObjectDescription)} out of the video \"{prepared.VideoPath}\" into the new object avatar \"{prepared.Name}\" in the project \"{prepared.Project.Name}\". " + ExtractionWords(estimate),
            projectName = prepared.Project.Name,
            name = prepared.Name,
            findsObject = estimate.Segment,
            moments = estimate.Moments,
            credits = estimate.Credits is { } c ? (object)c : "Free",
            affordable = estimate.Affordable,
            sendsFrames = estimate.Segment,
            statement = AvatarObjectStatement.For(prepared.Locale),
            statementLocale = prepared.Locale,
        }, Json);
    }

    private async Task<string> ApplyExtractObjectFromVideoAsync(
        AssistantToolContext context, ExtractArgs args, string? confirmationToken, CancellationToken ct)
    {
        (ExtractPrepared? prepared, string? error) = await PrepareExtractAsync(context, args, ct);
        if (prepared is null)
            return error!;
        if (!Verify(OpExtractObjectFromVideo, prepared.Payload, context, confirmationToken))
            return Err(StaleTokenMessage);
        if (!StatementConfirmed(context, prepared.Locale, args.Statement))
            return StatementRequired(prepared.Locale, context.Initiator == ExecutionInitiator.Mcp);

        (bool mediaAllowed, string? mediaRefusal) = await MediaForThisCallAsync(
            context, prepared.Project, MediaEgressSurface.ObjectExtraction, args.AllowMediaEgress, ct);
        if (mediaRefusal is not null)
            return mediaRefusal;

        AvatarBuildOutcome<AvatarBuildStarted> started = await _builds!.StartExtractionAsync(
            prepared.Project,
            Actor(context),
            new ObjectExtractionRequest(
                "file:" + prepared.Video.Id.ToString("D"), prepared.ObjectDescription, prepared.Name, prepared.Description,
                prepared.AtSeconds, AvatarObjectStatement.For(prepared.Locale), prepared.Locale),
            mediaAllowed,
            ct);
        if (started.Refusal is { } refused)
            return FromRefusal(refused);

        return JsonSerializer.Serialize(new
        {
            started = true,
            name = prepared.Name,
            state = started.Value!.State,
            findsObject = prepared.Estimate.Segment,
            projectName = prepared.Project.Name,
            hint = "It runs in the background and does not wait. ListProjectAvatars shows it under builds, and the object avatar appears there when it is ready.",
        }, Json);
    }

    // ------------------------------------------------------------------ shared words

    private AvatarBuildActor Actor(AssistantToolContext context) => new(context.UserId, _currentUser?.EmailVerified == true);

    /// <summary>The audit word of an object the assistant (or a connected tool) made.</summary>
    private static string ViaWord(AssistantToolContext context) =>
        context.Initiator == ExecutionInitiator.Mcp ? ObjectAvatarCreator.ViaMcp : ObjectAvatarCreator.ViaAssistant;

    internal static string FilesWords(IReadOnlyList<ObjectFile> files)
    {
        int pictures = files.Count(f => f.Kind == AvatarReferenceKind.Image);
        int videos = files.Count(f => f.Kind == AvatarReferenceKind.Video);
        string words = $"{pictures} picture{(pictures == 1 ? "" : "s")}";
        if (videos > 0) words += $" and {videos} video{(videos == 1 ? "" : "s")}";
        return words;
    }

    internal static string ExtractionWords(ExtractionEstimate estimate) => estimate.Segment
        ? $"Up to {estimate.Moments} frames are shown to the object-finding model and only the object is cut out"
          + (estimate.Credits is { } credits ? $"; about {credits:0.##} credits." : ".")
        : $"Up to {estimate.Moments} whole frames are used (no object-finding model is used, so it is free).";

    private static string Quoted(string text) => "\"" + text + "\"";

    private static string CreatedJson(ProjectAvatar avatar, Project project, IReadOnlyList<ObjectFile> files, string? variantOf = null) =>
        JsonSerializer.Serialize(new
        {
            created = true,
            name = avatar.Name,
            kind = KindWord(avatar.SubjectKind),
            variantOf,
            variantLabel = avatar.VariantLabel,
            pictures = files.Count(f => f.Kind == AvatarReferenceKind.Image),
            videos = files.Count(f => f.Kind == AvatarReferenceKind.Video),
            personCheck = "Every picture was checked: none shows a person.",
            projectName = project.Name,
        }, Json);
}
