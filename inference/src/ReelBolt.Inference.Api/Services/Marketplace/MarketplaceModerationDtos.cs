namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>Body of a user report. <see cref="Reason"/> is one of illegal, sexual, copyright, spam, misleading, other.</summary>
public sealed record ReportRecipeRequest(string? Reason, string? Note);

/// <summary>Answer to a report. It never says whether this report hid the recipe.</summary>
public sealed record ReportRecipeResponse(bool Reported);

/// <summary>Body of an admin approve. <see cref="VersionId"/> is the version the admin looked at; <see cref="Digest"/> its content hash.</summary>
public sealed record AdminApproveRequest(Guid? VersionId, string? Digest, bool? ClearHidden);

/// <summary>Body of an admin reject. <see cref="Reason"/> is content, unsafe, copyright, low_quality or other.</summary>
public sealed record AdminRejectRequest(Guid? VersionId, string? Digest, string? Reason, string? Note);

/// <summary>Body of an admin remove.</summary>
public sealed record AdminRemoveRequest(string? Reason, string? Note);

public sealed record AdminChecksDto(string? Text, string? Preview, IReadOnlyList<string> LintCodes);

public sealed record AdminPromptDto(int StepOrder, string Field, string Text);

/// <summary>One recipe (or one pending version of a recipe) as the admin queue and lists show it. Never an email or a user id.</summary>
public sealed record AdminRecipeRow(
    Guid Id,
    Guid? VersionId,
    string? Digest,
    string Name,
    string? Description,
    string AuthorLabel,
    string? OrgName,
    int Version,
    string Status,
    bool HiddenByReports,
    AdminChecksDto? Checks,
    IReadOnlyList<AdminPromptDto>? Prompts,
    string? PreviewStatus = null,
    string? PreviewSha256 = null);

public sealed record AdminRecipeRows(IReadOnlyList<AdminRecipeRow> Items, string? NextCursor = null);

/// <summary>One open report. <see cref="ReporterAgeDays"/> and <see cref="ReporterVerified"/> tell an admin how much weight it has; the reporter's identity is never shown.</summary>
public sealed record AdminReportRow(
    Guid Id, Guid RecipeId, string RecipeName, string Reason, string? Note, int Count, DateTime CreatedAt, int? ReporterAgeDays = null, bool ReporterVerified = false);

public sealed record AdminReportRows(IReadOnlyList<AdminReportRow> Items);

public sealed record AdminVersionDto(
    Guid Id, int N, string Digest, string PreviewStatus, string? Decision, string? DecisionReason, DateTime CreatedAt, DateTime? ConsentAt);

public sealed record AdminRecipeDetail(
    Guid Id,
    string Name,
    string Description,
    string AuthorLabel,
    string? OrgName,
    string Status,
    bool HiddenByReports,
    string? RemovedReason,
    string Category,
    string? Language,
    string CostClass,
    int? CurrentVersion,
    int CopyCount,
    int OpenReports,
    IReadOnlyList<AdminVersionDto> Versions,
    IReadOnlyList<AdminPromptDto> Prompts,
    string? CheckJson,
    DateTime? ListedAt,
    DateTime CreatedAt);

/// <summary>A copying organization, by id only (legal cases). The wire name is orgId; the property is named so the private-column scan stays meaningful.</summary>
public sealed record AdminCopyDto([property: System.Text.Json.Serialization.JsonPropertyName("orgId")] Guid Copier, Guid VersionId, DateTime FirstCopiedAt);

public sealed record AdminCopies(IReadOnlyList<AdminCopyDto> Items);

public sealed record AdminAuditRow(
    Guid Id, Guid? RecipeId, Guid? VersionId, string Action, string ActorKind, Guid? AdminId, string? Reason, string? Note, DateTime CreatedAt);

public sealed record AdminAuditRows(IReadOnlyList<AdminAuditRow> Items);

/// <summary>The wire shape the admin actions answer with.</summary>
public sealed record AdminActionResponse(Guid Id, string Status, bool HiddenByReports);

/// <summary>Where the preview or poster of a version under review lives; never leaves the server.</summary>
public sealed record AdminMediaRef(Guid RecipeId, Guid VersionId, string Key, string? Sha256);
