using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Save-time avatar check (docs/design/avatars.md "Consent and safety", "Revoke": step saves naming a revoked avatar
/// are refused; docs/design/avatar-objects.md "Subjects in a video generation step"). Every avatar a
/// <c>VideoGenerate</c> config names, through the legacy <c>AvatarId</c> or through <c>Subjects</c>, must be an avatar of
/// THIS project, of a kind that is switched on (a row of a kind whose switch is off is treated as absent), and with its
/// consent or attestation on record, not withdrawn and not past its keep-until date; at most one of a step's subjects
/// may be a person; and a talking head (<c>AvatarAudio</c>) is one person. Refused with the same wording style as the
/// voice check (<see cref="VoiceoverVoiceConsentSaveValidator"/>). Only the consent half of the one rule
/// (<see cref="ProjectAvatar.UnselectableReason"/>) is judged here: the reference bytes are hashed by the engine at run
/// time, which stays authoritative (an avatar can be revoked or its files changed after the save). Another project's
/// avatar is "not found", never named.
/// <para>
/// Decision AO-3 (docs/design/avatar-objects.md): a step that shows an avatar or an object on a model that takes no
/// pictures at all (a text-only route) is refused here too, naming the image-capable model of the same family
/// (<see cref="VideoSubjectSiblings"/>). Judged only when the step names its own provider (a visible
/// <c>VideoGeneration</c> capability of the project's organization or of the platform) and the model is one this
/// platform has a declaration for; a step on the default provider is judged by the linter (WL034) and by the engine
/// before anything is charged.
/// </para>
/// </summary>
public static class VideoGenerateAvatarSaveValidator
{
    private sealed record Named(CreateWorkflowStepRequest Step, IReadOnlyList<Guid> AvatarIds, bool TalkingHead)
    {
        /// <summary>The step's own provider (a <c>VideoGeneration</c> capability id), or null for the default.</summary>
        public Guid? ProviderId { get; init; }

        /// <summary>The step's model identity.</summary>
        public string? Model { get; init; }
    }

    public static async Task<IReadOnlyList<string>> ValidateAsync(
        InferenceApiDbContext db, Guid projectId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        List<Named> named = Extract(steps);
        if (named.Count == 0)
            return [];

        Guid[] ids = [.. named.SelectMany(n => n.AvatarIds).Distinct()];
        List<ProjectAvatar> rows = await db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == projectId && ids.Contains(a.Id))
            .ToListAsync(ct);

        Dictionary<Guid, InferenceProviderKind> kinds = [];
        Guid[] providers = ProvidersNamed(named);
        if (providers.Length > 0)
        {
            Guid? organizationId = await ProjectOrganization(db, projectId).FirstOrDefaultAsync(ct);
            kinds = await ProviderKinds(db, organizationId, providers).ToDictionaryAsync(c => c.Id, c => c.Kind, ct);
        }

        return Errors(named, rows, kinds, await ReelBolt.Shared.Avatars.AvatarVariantOriginals.LoadAsync(db, projectId, rows, ct));
    }

    public static IReadOnlyList<string> Validate(InferenceApiDbContext db, Guid projectId, IEnumerable<CreateWorkflowStepRequest> steps)
    {
        List<Named> named = Extract(steps);
        if (named.Count == 0)
            return [];

        Guid[] ids = [.. named.SelectMany(n => n.AvatarIds).Distinct()];
        List<ProjectAvatar> rows = [.. db.ProjectAvatars.AsNoTracking().Where(a => a.ProjectId == projectId && ids.Contains(a.Id))];

        Dictionary<Guid, InferenceProviderKind> kinds = [];
        Guid[] providers = ProvidersNamed(named);
        if (providers.Length > 0)
        {
            Guid? organizationId = ProjectOrganization(db, projectId).FirstOrDefault();
            kinds = ProviderKinds(db, organizationId, providers).ToDictionary(c => c.Id, c => c.Kind);
        }

        // A variant of a person is only as usable as its original (read the originals, as the async twin does).
        Guid[] originalIds = [.. rows.Where(r => r.SubjectKind == AvatarSubjectKind.Person && r.VariantOfAvatarId is not null).Select(r => r.VariantOfAvatarId!.Value).Distinct()];
        Dictionary<Guid, ProjectAvatar> originals = originalIds.Length == 0
            ? []
            : db.ProjectAvatars.AsNoTracking().Where(a => a.ProjectId == projectId && originalIds.Contains(a.Id)).ToDictionary(a => a.Id);
        return Errors(named, rows, kinds, originals);
    }

    private static Guid[] ProvidersNamed(List<Named> named) =>
        [.. named.Select(n => n.ProviderId).OfType<Guid>().Where(id => id != Guid.Empty).Distinct()];

    private static IQueryable<Guid?> ProjectOrganization(InferenceApiDbContext db, Guid projectId) =>
        db.Projects.AsNoTracking()
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrganizationId);

    /// <summary>
    /// The provider kind of each named <c>VideoGeneration</c> capability the project's organization can see (its own or
    /// a platform row). Another organization's row is simply not found, so this check never says anything about it
    /// (the ownership check refuses it under its own one message, decision D5).
    /// </summary>
    private static IQueryable<ProviderKindRow> ProviderKinds(InferenceApiDbContext db, Guid? organizationId, Guid[] capabilityIds) =>
        db.ModelCapabilities.AsNoTracking()
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(c => capabilityIds.Contains(c.Id)
                && c.Capability == InferenceProviderCapability.VideoGeneration
                && (c.OrganizationId == null || c.OrganizationId == organizationId))
            .Select(c => new ProviderKindRow(c.Id, c.Model.Provider.Kind));

    private sealed record ProviderKindRow(Guid Id, InferenceProviderKind Kind);

    private static List<Named> Extract(IEnumerable<CreateWorkflowStepRequest> steps)
    {
        List<Named> result = [];
        foreach (CreateWorkflowStepRequest step in steps)
        {
            if (!StepConfigSaveValidator.TryParseStepType(step.StepType, out StepType type) || type != StepType.VideoGenerate
                || string.IsNullOrWhiteSpace(step.VideoGenerateConfigJson))
                continue;

            VideoGenerateStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(step.VideoGenerateConfigJson, VideoGenerateStepConfig.JsonOptions);
            }
            catch (JsonException)
            {
                continue; // the config's own shape check reports this
            }

            // The legacy AvatarId and Subjects alike (a config that names both is refused by its own shape check; every
            // id it names is still judged here, so no avatar of another project slips through on a malformed step).
            Guid[] ids = config is null
                ? []
                : [.. new[] { config.AvatarId ?? Guid.Empty }
                    .Concat(config.Subjects?.Where(s => s is not null).Select(s => s.AvatarId) ?? [])
                    .Where(id => id != Guid.Empty)
                    .Distinct()];
            if (ids.Length > 0)
                result.Add(new Named(step, ids, config!.HasAvatarAudio) { ProviderId = config.ProviderId, Model = config.Model });
        }

        return result;
    }

    private static List<string> Errors(List<Named> named, List<ProjectAvatar> rows, IReadOnlyDictionary<Guid, InferenceProviderKind> kinds,
        IReadOnlyDictionary<Guid, ProjectAvatar> originals)
    {
        DateTime now = DateTime.UtcNow;
        List<string> errors = [];
        foreach (Named n in named)
        {
            string prefix = $"Step {n.Step.StepOrder} (VideoGenerate): ";
            int people = 0, objects = 0;
            foreach (Guid avatarId in n.AvatarIds)
            {
                ProjectAvatar? row = rows.FirstOrDefault(r => r.Id == avatarId);
                // A row of a kind whose switch is off is treated as absent, as every avatar route treats it.
                if (row is null || !AppStepFacts.AvatarKindEnabled(row.SubjectKind))
                {
                    errors.Add(prefix + $"avatar {avatarId} was not found in this project.");
                    continue;
                }

                if (row.SubjectKind == AvatarSubjectKind.Object)
                    objects++;
                else
                    people++;

                // A stand-in usable picture makes the reference checks pass, so the answer is the consent half only.
                string? reason = row.UnselectableReason(now, [new AvatarReferenceState(AvatarReferenceKind.Image, true, false)], originals);
                if (reason is not null)
                    errors.Add(prefix + $"avatar {avatarId} cannot be used: {reason}.");
            }

            if (people > 1)
                errors.Add(prefix + "a clip shows at most one person, and this step names more than one avatar of a person. Keep one of them (objects are not limited to one).");

            if (n.TalkingHead && (objects > 0 || people + objects > 1))
                errors.Add(prefix + "a talking head (avatarAudio) is one person saying words: the step must show exactly one avatar, of a person.");

            // AO-3: a text-only route takes no pictures, so it can show no avatar and no object. Only a model this
            // platform has a declaration for is judged; an unknown one is left to the engine.
            if (n.ProviderId is Guid providerId
                && kinds.TryGetValue(providerId, out InferenceProviderKind kind)
                && VideoSubjectModels.TryDeclared(kind, n.Model, out VideoReferenceInputSupport references, out VideoImageInputSupport images)
                && !references.AcceptsAvatar
                && images == VideoImageInputSupport.None)
            {
                errors.Add(prefix + VideoSubjectPaths.TakesNoPictures(n.Model, VideoSubjectSiblings.For(kind, n.Model)));
            }
        }

        return errors;
    }
}
