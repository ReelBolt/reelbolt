using System.Text.Json;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Builds;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

// Variants of an OBJECT avatar from the assistant and MCP (docs/design/avatar-objects.md "Avatar variants", AO-8): from
// other project files through the one create path (ObjectAvatarCreator, free), or made by the picture model through the
// build service (paid). A person's variants are made only in the app, with the person's consent.
public sealed partial class AssistantAvatarTools
{
    internal const string OpCreateAvatarVariant = "CreateAvatarVariant";

    /// <summary>What both halves of <c>CreateAvatarVariant</c> decided, and the token payload; the card reads it too.</summary>
    /// <param name="Original">The original (never itself a variant: a variant of a variant attaches to the original).</param>
    /// <param name="Files">The other files, when the variant is made from them; null when the picture model makes it.</param>
    /// <param name="Estimate">The build service's price, when the picture model makes it; null otherwise.</param>
    internal sealed record VariantPrepared(
        Project Project, ProjectAvatar Original, string Label, string Description, IReadOnlyList<ObjectFile>? Files,
        string? Instruction, bool IncludeVideos, string Locale, bool AllowMediaEgress, VariantEstimate? Estimate, string Payload)
    {
        public string Name => ProjectAvatar.VariantDisplayName(Original.Name, Label);

        public bool ByModel => Files is null;
    }

    /// <summary>
    /// Everything both halves of <c>CreateAvatarVariant</c> decide the same way: the objects switch, Edit (re-read now), a
    /// verified email, the plan <c>avatar_objects</c>, the original by name (an object; a person is refused here), the
    /// label and description, exactly one of files or an instruction, the statement's language, and for an instruction the
    /// build service's own estimate. Every other check is the shared creator's or the build service's.
    /// </summary>
    internal async Task<(VariantPrepared? Prepared, string? Error)> PrepareVariantAsync(
        AssistantToolContext context, VariantArgs args, CancellationToken ct)
    {
        // The project first, so a foreign project reads exactly like a missing one whatever the switches say.
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, args.ProjectId, out string scopeError);
        if (project is null)
            return (null, scopeError);
        if (!ObjectsOn || _objectCreator is null || _builds is null)
            return (null, Err(ObjectsNotAvailableMessage, NotAvailableCode));

        if (EmailRefusal() is { } unverified)
            return (null, unverified);

        // The original by name among the kinds switched on: a person's name is answered with where to do it.
        List<ProjectAvatar> avatars = await AvatarsOfEnabledKindsAsync(project.Id, ct);
        (IReadOnlyList<ProjectAvatar>? named, string? unknown) = ProjectSubjectNames.Resolve(avatars, [args.Original ?? string.Empty]);
        if (named is null)
            return (null, Err(unknown!, "avatar_not_found"));
        ProjectAvatar original = named[0];
        if (original.SubjectKind == AvatarSubjectKind.Person)
            return (null, Err(PersonInAppMessage, "VARIANT_OF_PERSON_IN_APP"));
        if (original.VariantOfAvatarId is { } parentId && avatars.FirstOrDefault(a => a.Id == parentId) is { } parent)
            original = parent;

        if (await PlanRefusalAsync(project, AvatarSubjectKind.Object, ct) is { } notEntitled)
            return (null, notEntitled);
        if (await _objectCreator.VisionRefusalAsync(ct) is { } noVision)
            return (null, FromRefusal(noVision));

        string label = args.Label?.Trim() ?? string.Empty;
        if (label.Length == 0 || label.Length > ProjectAvatarLimits.MaxVariantLabelLength || label.Any(char.IsControl))
            return (null, Err($"label names the variant in at most {ProjectAvatarLimits.MaxVariantLabelLength} characters, for example \"Red\".", "variant_label_invalid"));
        string description = args.Description?.Trim() ?? string.Empty;
        if (description.Length == 0 || description.Length > ProjectAvatarLimits.MaxVariantDescriptionLength || description.Any(char.IsControl))
        {
            return (null, Err($"description says what sets the variant apart, in at most {ProjectAvatarLimits.MaxVariantDescriptionLength} characters, for example \"matte red paint, black rims\".",
                "variant_description_invalid"));
        }

        bool hasFiles = args.Files is { Count: > 0 };
        string? instruction = string.IsNullOrWhiteSpace(args.Instruction) ? null : args.Instruction.Trim();
        if (hasFiles == (instruction is not null))
            return (null, Err("Pass EITHER files (other project files of the variant) OR instruction (what the picture model should change), not both and not neither.", "variant_source_invalid"));
        if (instruction is { Length: > ProjectAvatarLimits.MaxVariantInstructionLength })
            return (null, Err($"instruction can be at most {ProjectAvatarLimits.MaxVariantInstructionLength} characters.", "variant_instruction_invalid"));
        if (hasFiles && args.IncludeVideos == true)
            return (null, Err("includeVideos goes with instruction only; a variant made from files takes the videos in files.", "variant_source_invalid"));

        IReadOnlyList<ObjectFile>? files = null;
        if (hasFiles)
        {
            (files, string? fileError) = await ObjectFilesAsync(project.Id, args.Files, ct);
            if (files is null)
                return (null, fileError);
            if (!files.Any(f => f.Kind == AvatarReferenceKind.Image))
                return (null, Err("A variant needs at least one picture of the object.", "no_image_reference"));
        }

        (string? locale, string? localeError) = await StatementLocaleAsync(args.Locale, context.UserId, ct);
        if (locale is null)
            return (null, localeError);

        bool includeVideos = args.IncludeVideos == true;
        bool allow = args.AllowMediaEgress == true;
        VariantEstimate? estimate = null;
        if (instruction is not null)
        {
            AvatarBuildOutcome<VariantEstimate> priced = await _builds.EstimateVariantAsync(
                project, Actor(context), original.Id,
                new VariantBuildRequest(instruction, label, description, IncludeVideos: includeVideos), ct);
            if (priced.Refusal is { } refusal)
                return (null, FromRefusal(refusal));
            estimate = priced.Value!;
            if (estimate.UnavailableReason is { } why)
                return (null, Err(why, "VARIANT_UNAVAILABLE"));
            if (project.SubjectMediaConsentAt is null && !allow)
            {
                return (null, Err("This project has not allowed sending the pictures of its avatars and objects to the AI picture service. " +
                                  "The user can allow it once in the project's Avatars tab, or for this one variant with allowMediaEgress: true (in the app chat the card then asks them).",
                    AvatarBuildCodes.ConsentRequired));
            }
        }

        string payload = Payload(new
        {
            op = OpCreateAvatarVariant,
            projectId = project.Id,
            original = original.Id,
            label,
            description,
            files = files?.Select(f => new { id = f.File.Id, role = f.Role.ToString() }).ToArray(),
            instruction,
            includeVideos = includeVideos ? true : (bool?)null,
            locale,
            allowMediaEgress = allow && instruction is not null ? true : (bool?)null,
        });
        return (new VariantPrepared(project, original, label, description, files, instruction, includeVideos, locale, allow, estimate, payload), null);
    }

    private async Task<string> ProposeCreateAvatarVariantAsync(AssistantToolContext context, VariantArgs args, CancellationToken ct)
    {
        (VariantPrepared? prepared, string? error) = await PrepareVariantAsync(context, args, ct);
        if (prepared is null)
            return error!;
        if (ProposalStatementRefusal(context, prepared.Locale, args.Statement) is { } statement)
            return statement;

        VariantEstimate? estimate = prepared.Estimate;
        return JsonSerializer.Serialize(new
        {
            operation = OpCreateAvatarVariant,
            confirmationToken = Mint(OpCreateAvatarVariant, prepared.Payload, context),
            summary = VariantSummary(prepared),
            projectName = prepared.Project.Name,
            name = prepared.Name,
            original = prepared.Original.Name,
            label = prepared.Label,
            madeBy = prepared.ByModel ? "pictureModel" : "files",
            pictures = estimate?.Pictures,
            videos = estimate?.Videos,
            credits = prepared.ByModel ? (estimate is { BillingEnabled: true } ? estimate.CreditsHigh ?? estimate.Credits : null) : (object)"Free",
            affordable = estimate?.Affordable ?? true,
            statement = AvatarObjectStatement.For(prepared.Locale),
            statementLocale = prepared.Locale,
        }, Json);
    }

    private async Task<string> ApplyCreateAvatarVariantAsync(
        AssistantToolContext context, VariantArgs args, string? confirmationToken, CancellationToken ct)
    {
        (VariantPrepared? prepared, string? error) = await PrepareVariantAsync(context, args, ct);
        if (prepared is null)
            return error!;
        if (!Verify(OpCreateAvatarVariant, prepared.Payload, context, confirmationToken))
            return Err(StaleTokenMessage);
        if (!StatementConfirmed(context, prepared.Locale, args.Statement))
            return StatementRequired(prepared.Locale, context.Initiator == ExecutionInitiator.Mcp);

        string statement = AvatarObjectStatement.For(prepared.Locale)!;
        if (prepared.Files is { } files)
        {
            ObjectAvatarCreateOutcome created = await _objectCreator!.CreateAsync(
                prepared.Project,
                context.UserId,
                new ObjectAvatarCreateRequest(
                    null, null, prepared.Locale, statement,
                    [.. files.Select(f => new AvatarReferenceAsk(f.File.Id, f.Role.ToString()))],
                    new AvatarVariantAsk(prepared.Original.Id, prepared.Label, prepared.Description),
                    ViaWord(context)),
                ct);
            if (created.Refusal is { } refused)
                return FromRefusal(refused);
            return CreatedJson(created.Avatar!, prepared.Project, files, prepared.Original.Name);
        }

        (bool mediaAllowed, string? mediaRefusal) = await MediaForThisCallAsync(
            context, prepared.Project, MediaEgressSurface.AvatarVariant, args.AllowMediaEgress, ct);
        if (mediaRefusal is not null)
            return mediaRefusal;

        AvatarBuildOutcome<AvatarBuildStarted> started = await _builds!.StartVariantAsync(
            prepared.Project,
            Actor(context),
            prepared.Original.Id,
            new VariantBuildRequest(
                prepared.Instruction, prepared.Label, prepared.Description,
                IncludeVideos: prepared.IncludeVideos, ConsentStatement: statement, ConsentLocale: prepared.Locale),
            mediaAllowed,
            ct);
        if (started.Refusal is { } refusedBuild)
            return FromRefusal(refusedBuild);

        return JsonSerializer.Serialize(new
        {
            started = true,
            name = prepared.Name,
            original = prepared.Original.Name,
            label = prepared.Label,
            state = started.Value!.State,
            projectName = prepared.Project.Name,
            hint = "The picture model is making the variant in the background; it does not wait. ListProjectAvatars shows it under builds, and the variant appears under its original when it is ready.",
        }, Json);
    }

    internal static string VariantSummary(VariantPrepared prepared) => prepared.ByModel
        ? $"Make the variant \"{prepared.Name}\" of the object \"{prepared.Original.Name}\" in the project \"{prepared.Project.Name}\": the picture model changes its pictures as asked" +
          (prepared.IncludeVideos ? " and makes its videos again from their changed first frame (the movement can differ)." : ".") +
          " Paid."
        : $"Make the variant \"{prepared.Name}\" of the object \"{prepared.Original.Name}\" in the project \"{prepared.Project.Name}\" from {FilesWords(prepared.Files!)}. Its pictures are checked for people first. Free.";
}
