namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// The lifecycle of one design version. Persisted by name, so the member order is
/// append-only: never reorder, rename or remove a member.
/// </summary>
public enum DesignVersionState
{
    Draft,
    Approved,
    Superseded,
    Rejected,
}

/// <summary>
/// How a design version came to exist. Persisted by name and append-only.
/// </summary>
public enum DesignVersionOrigin
{
    Import,
    StaticExtract,
    LlmExtract,
    Manual,
    Merge,
    Restore,
}

/// <summary>
/// Where a design source is read from. Persisted by name and append-only.
/// </summary>
public enum DesignSourceKind
{
    ProjectFile,
    GitHubPath,
    Upload,
    Url,
    Figma,
    Manual,
}

/// <summary>
/// Whether a design source still matches what was read. Persisted by name and append-only.
/// </summary>
public enum DesignSourceStatus
{
    Current,
    Changed,
    Missing,
}

/// <summary>
/// The lifecycle of a design proposal. Persisted by name and append-only.
/// </summary>
public enum DesignProposalStatus
{
    Pending,
    Approved,
    Rejected,
    Superseded,
    Expired,
}

/// <summary>
/// What caused a design proposal to be raised. Persisted by name and append-only.
/// </summary>
public enum DesignProposalTrigger
{
    SourceChanged,
    Manual,
    Scheduled,
    Import,
}
