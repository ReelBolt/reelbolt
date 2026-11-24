using System.Linq.Expressions;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// THE decision for a vendor voice id (<c>DefaultVoiceId</c>, a line's <c>VoiceId</c>, a cast or recorded voice id)
/// that may belong to a cloned voice (docs/voiceover.md "Voices and consent"). The engine's run-time check, the
/// reuse guard and the Inference API's save-time check all call it, so they cannot disagree. Callers pass EVERY
/// <c>project_voices</c> row carrying the id, from any project (one query).
/// <list type="number">
/// <item>A row of THIS project matches and is unusable: refused.</item>
/// <item>A usable <see cref="ProjectVoiceSource.Cloned"/> row of THIS project matches: allowed. (Another project's row
/// with the same id is then irrelevant: this project's own consent decides.)</item>
/// <item>Otherwise a <see cref="ProjectVoiceSource.Cloned"/> row of ANOTHER project matches (and is in
/// <see cref="Scope"/>): refused, even when this project has a <see cref="ProjectVoiceSource.Library"/> row with the
/// same id. A clone's consent belongs to the project that attested it, and a Library row attests none.</item>
/// <item>Otherwise (no match, or only <see cref="ProjectVoiceSource.Library"/> rows elsewhere): allowed — a raw vendor
/// or library id. Residual: a deleted clone's vendor id is indistinguishable from a raw one.</item>
/// </list>
/// A refusal carries no detail about which project owns the voice.
/// </summary>
public static class VendorVoiceIdRule
{
    /// <param name="projectId">The project the step runs / is saved in.</param>
    /// <param name="rowsWithThisVendorId">Every row, any project, whose <c>RemoteVoiceId</c> is the id being checked.</param>
    /// <param name="isUsable">The usability projection of the caller (engine: resolver projection; API: <c>IsSelectable</c>).</param>
    public static bool IsRefused(Guid projectId, IEnumerable<ProjectVoice> rowsWithThisVendorId, Func<ProjectVoice, bool> isUsable)
    {
        List<ProjectVoice> rows = [.. rowsWithThisVendorId];
        List<ProjectVoice> own = [.. rows.Where(r => r.ProjectId == projectId)];
        if (own.Any(r => !isUsable(r)))
            return true;
        // This project's own usable clone carries its own attested consent, so it decides. A Library row carries none:
        // it must not stand in for the consent of another project's clone that happens to share the vendor id.
        if (own.Any(r => r.Source == ProjectVoiceSource.Cloned))
            return false;
        return rows.Any(r => r.ProjectId != projectId && r.Source == ProjectVoiceSource.Cloned);
    }

    /// <summary>The one wording for a refused vendor id, identical for an unusable clone here and a clone of another project.</summary>
    public static string Message(string vendorId) =>
        $"voice id '{vendorId}' cannot be used: a cloned voice is usable only in a project where its consent is attested, not revoked and within retention.";

    /// <summary>
    /// Which rows carrying one of <paramref name="remoteVoiceIds"/> the rule may consult (one shared predicate for the
    /// engine, the reuse guard and the save check, so they cannot drift). In scope: this project's own rows, plus Cloned
    /// rows of OTHER projects that are
    /// <list type="bullet">
    /// <item>in the SAME organization as this project (read from the project row; <see cref="Guid.Empty"/> = unresolved) —
    /// UNCONDITIONALLY, whatever the provider lookup returns (null, missing, unresolvable): the organization's own
    /// projects share its consent regime;</item>
    /// <item>or on a PLATFORM-owned account (<c>OrganizationId</c> null or <c>IsSystem</c>), which every tenant can speak through;</item>
    /// <item>or, when this project's organization is unresolved, on any account that can be resolved (refuse conservatively).</item>
    /// </list>
    /// <c>ProjectVoice.InferenceProviderId</c> is a model CAPABILITY id (<c>model_capabilities.Id</c>, the old flat provider
    /// row ids having become capability ids); its account is capability -> model -> provider, joined here.
    /// DELIBERATE: a foreign-organization clone whose capability is null or unresolvable (its account deleted, FK SET NULL)
    /// stays OUT of scope. Refusing it would let any organization mint a clone with a popular vendor id and then delete its
    /// own provider to make that id refuse for every other tenant. Likewise a foreign clone on another organization's
    /// private account is ignored: nobody else can speak through it.
    /// </summary>
    public static Expression<Func<ProjectVoice, bool>> Scope(
        IQueryable<Project> projects, IQueryable<ModelCapability> capabilities, Guid projectId, string[] remoteVoiceIds) =>
        v => remoteVoiceIds.Contains(v.RemoteVoiceId) &&
             (v.ProjectId == projectId ||
              (v.Source == ProjectVoiceSource.Cloned &&
               ((projects.Where(q => q.Id == projectId).Select(q => q.OrganizationId).FirstOrDefault() != Guid.Empty &&
                 projects.Any(p => p.Id == v.ProjectId &&
                     p.OrganizationId == projects.Where(q => q.Id == projectId).Select(q => q.OrganizationId).FirstOrDefault())) ||
                capabilities.Any(c => c.Id == v.InferenceProviderId &&
                    (c.Model.Provider.OrganizationId == null || c.Model.Provider.IsSystem)) ||
                (projects.Where(q => q.Id == projectId).Select(q => q.OrganizationId).FirstOrDefault() == Guid.Empty &&
                 capabilities.Any(c => c.Id == v.InferenceProviderId)))));
}
