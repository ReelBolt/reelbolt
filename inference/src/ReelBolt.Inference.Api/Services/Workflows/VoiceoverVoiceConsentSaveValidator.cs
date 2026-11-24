using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Save-time voice consent check (docs/voiceover.md "Voices and consent"): a Voiceover config whose
/// <c>ProjectVoiceId</c>, <c>DefaultVoiceId</c> or an inline line's <c>VoiceId</c> names a project voice that cannot be
/// spoken right now (consent missing, revoked, retention expired, or no such voice in the project) is refused at save
/// with the same wording style as the other save checks. A vendor id that matches no voice of the project is a raw
/// vendor/library id and passes. This is an early warning only: the engine's check at run time stays authoritative,
/// since a voice can be revoked after the save. Uses <see cref="ProjectVoice.IsSelectable"/>, the one rule.
/// </summary>
public static class VoiceoverVoiceConsentSaveValidator
{
    private sealed record Named(CreateWorkflowStepRequest Step, Guid? ProjectVoiceId, IReadOnlyList<string> VendorIds);

    public static async Task<IReadOnlyList<string>> ValidateAsync(
        InferenceApiDbContext db, Guid projectId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        List<Named> named = Extract(steps);
        if (named.Count == 0)
            return [];

        (Guid[] ids, string[] vendor) = Keys(named);
        List<ProjectVoice> rows = await db.ProjectVoices.AsNoTracking()
            .Where(v => v.ProjectId == projectId && ids.Contains(v.Id))
            .Union(VendorRows(db, projectId, vendor))
            .ToListAsync(ct);
        return Errors(projectId, named, rows);
    }

    public static IReadOnlyList<string> Validate(InferenceApiDbContext db, Guid projectId, IEnumerable<CreateWorkflowStepRequest> steps)
    {
        List<Named> named = Extract(steps);
        if (named.Count == 0)
            return [];

        (Guid[] ids, string[] vendor) = Keys(named);
        List<ProjectVoice> rows = db.ProjectVoices.AsNoTracking()
            .Where(v => v.ProjectId == projectId && ids.Contains(v.Id))
            .Union(VendorRows(db, projectId, vendor))
            .ToList();
        return Errors(projectId, named, rows);
    }

    /// <summary>
    /// The rows a vendor id is judged against: <see cref="VendorVoiceIdRule.Scope"/> (this project's own, plus other
    /// projects' clones in this organization or on platform accounts). Capabilities are read with the tenant filter
    /// off so another organization's private account is seen for what it is and ignored, not mistaken for missing.
    /// </summary>
    internal static IQueryable<ProjectVoice> VendorRows(InferenceApiDbContext db, Guid projectId, string[] vendor) =>
        db.ProjectVoices.AsNoTracking().Where(VendorVoiceIdRule.Scope(
            db.Projects, db.ModelCapabilities.IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName]), projectId, vendor));

    private static (Guid[] Ids, string[] Vendor) Keys(List<Named> named) => (
        [.. named.Where(n => n.ProjectVoiceId is not null).Select(n => n.ProjectVoiceId!.Value).Distinct()],
        [.. named.SelectMany(n => n.VendorIds).Distinct()]);

    private static List<Named> Extract(IEnumerable<CreateWorkflowStepRequest> steps)
    {
        List<Named> result = [];
        foreach (CreateWorkflowStepRequest step in steps)
        {
            // The step type is parsed exactly as the save path stores it (case-insensitive, S-03), so "voiceover" is checked.
            if (!StepConfigSaveValidator.TryParseStepType(step.StepType, out StepType type) || type != StepType.Voiceover
                || string.IsNullOrWhiteSpace(step.VoiceoverConfigJson))
                continue;

            VoiceoverStepConfig? config;
            try
            {
                config = VoiceoverStepConfig.Parse(step.VoiceoverConfigJson);
            }
            catch (JsonException)
            {
                continue; // the config's own shape check reports this
            }

            if (config is null)
                continue;

            List<string> vendor = [];
            if (!string.IsNullOrWhiteSpace(config.DefaultVoiceId))
                vendor.Add(config.DefaultVoiceId);
            vendor.AddRange((config.Source?.Lines ?? []).Select(l => l.VoiceId).Where(v => !string.IsNullOrWhiteSpace(v))!);
            if (config.ProjectVoiceId is not null || vendor.Count > 0)
                result.Add(new Named(step, config.ProjectVoiceId, vendor));
        }

        return result;
    }

    private static List<string> Errors(Guid projectId, List<Named> named, List<ProjectVoice> rows)
    {
        DateTime now = DateTime.UtcNow;
        List<string> errors = [];
        foreach (Named n in named)
        {
            string prefix = $"Step {n.Step.StepOrder} (Voiceover): ";
            if (n.ProjectVoiceId is Guid id)
            {
                ProjectVoice? row = rows.FirstOrDefault(r => r.Id == id);
                if (row is null)
                    errors.Add(prefix + $"project voice {id} was not found in this project.");
                else if (row.UnselectableReason(now) is { } reason)
                    errors.Add(prefix + $"project voice {id} cannot be used: {reason}.");
            }

            // A vendor id: this project's own row decides; otherwise a clone of ANOTHER project is refused. The
            // message is the same either way and names neither the owner project nor the voice.
            foreach (string vendorId in n.VendorIds.Distinct())
            {
                if (VendorVoiceIdRule.IsRefused(projectId, rows.Where(r => r.RemoteVoiceId == vendorId), r => r.IsSelectable(now)))
                    errors.Add(prefix + VendorVoiceIdRule.Message(vendorId));
            }
        }

        return errors;
    }
}
