namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The propose-then-apply confirmation contract, generalized out of <c>WorkflowEditDiffService</c>
/// so every mutating assistant tool can share it: a token is never *trusted* as presented, it is
/// **recomputed** server-side from the operation, the canonical payload and the acting user, and
/// only then compared. A fabricated or stale token therefore fails <see cref="Verify"/> instead of
/// writing anything.
/// </summary>
public interface IConfirmationTokenService
{
    /// <summary>
    /// Mints the confirmation token for one (operation, canonical payload, user) triple.
    /// Deterministic: the same inputs always produce the same token within a deployment.
    /// </summary>
    string Compute(string operation, string canonicalPayload, Guid userId);

    /// <summary>
    /// Recomputes the expected token and compares it against <paramref name="presented"/> in fixed
    /// time. Returns <c>false</c> — never throws — for a null, empty, garbage, truncated or
    /// wrong-length token, and for a token minted for a different operation, payload or user.
    /// </summary>
    bool Verify(string operation, string canonicalPayload, Guid userId, string presented);

    /// <summary>
    /// Mints the confirmation token for an operation that acts on ONE ORGANIZATION's resource,
    /// binding <paramref name="organizationId"/> into the MAC as well (A11). A token a caller minted
    /// while their active organization was A therefore stops verifying while it is B — same user,
    /// same operation, same payload — because the organization is re-derived from the live request
    /// on every call rather than remembered from the turn that proposed.
    /// <para>
    /// A <paramref name="organizationId"/> of <c>null</c> is the deployment-wide form and reproduces
    /// the token the three-argument overload mints, byte for byte, so every pre-existing operation is
    /// unaffected by this overload's arrival. Pass an organization only where an organization is what
    /// the operation acts on.
    /// </para>
    /// </summary>
    string Compute(string operation, string canonicalPayload, Guid userId, Guid? organizationId);

    /// <summary>
    /// The organization-bound half of <see cref="Verify(string, string, Guid, string)"/>: a token
    /// minted under a different organization fails to verify, exactly as one minted for a different
    /// user, operation or payload does.
    /// </summary>
    bool Verify(string operation, string canonicalPayload, Guid userId, Guid? organizationId, string presented);
}
