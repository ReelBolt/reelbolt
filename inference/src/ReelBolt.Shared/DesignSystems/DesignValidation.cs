namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Everything the validator needs from the host: whether a <c>project_files</c> row exists in the
/// same project, the closed set of bundled font names and, when the document may reference a brand
/// library's assets, whether a <c>design_system_library_assets</c> row is visible to the caller (its
/// own library, or the library its project descends from). All of it is supplied by the caller so the
/// validator stays pure and never touches the database.
/// </summary>
public sealed record DesignValidationContext(
    Func<Guid, bool> ProjectFileExists,
    IReadOnlySet<string> BundledFontNames,
    Func<Guid, bool>? LibraryAssetExists = null);

/// <summary>
/// One rule breach found while validating a design document. <see cref="Code"/> is one of
/// <c>DS_V01</c>..<c>DS_V22</c>, <see cref="Path"/> is the JSON path the breach was found at and
/// <see cref="Message"/> is a human-readable explanation.
/// </summary>
public sealed record DesignViolation(string Code, string Path, string Message);

/// <summary>
/// The full outcome of validating one design document. A document is valid only when no violation
/// was recorded.
/// </summary>
public sealed record DesignValidationResult(IReadOnlyList<DesignViolation> Violations)
{
    /// <summary>True when the document broke no rule.</summary>
    public bool IsValid => Violations.Count == 0;
}
