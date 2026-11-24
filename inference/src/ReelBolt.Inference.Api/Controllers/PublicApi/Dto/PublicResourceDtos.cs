namespace ReelBolt.Inference.Api.Controllers.PublicApi.Dto;

// The public v1 resource shapes. Their OWN types, never the dashboard DTOs, so the dashboard can change
// freely. Ids are strings, timestamps ISO UTC, enums camelCase strings; never a storage key or bucket.

public sealed record PublicProjectView(string Id, string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record PublicCreateProjectRequest(string? Name, string? Description);

public sealed record PublicFileView(
    string Id, string Name, string Path, string MimeType, long SizeBytes, DateTime CreatedAt, string Status);

public sealed record PublicCreateUploadRequest(string? FileName, string? DirectoryPath, long? SizeBytes, string? MimeType);

public sealed record PublicUploadPartView(int PartNumber, string Url, DateTime ExpiresAt);

/// <summary>
/// <c>mode</c> is <c>single</c> (one PUT of the whole file to <c>parts[0].url</c>, sending
/// <c>headers</c>) or <c>multipart</c> (PUT each <c>partSizeBytes</c> slice to its part url and keep the ETag).
/// </summary>
public sealed record PublicUploadView(
    string UploadId, string Mode, IReadOnlyList<PublicUploadPartView> Parts, long PartSizeBytes,
    IReadOnlyDictionary<string, string> Headers);

public sealed record PublicCompletedPart(int PartNumber, string? Etag);

public sealed record PublicCompleteUploadRequest(IReadOnlyList<PublicCompletedPart>? Parts);

public sealed record PublicImportFileRequest(string? Url, string? FileName, string? DirectoryPath);

public sealed record PublicDownloadLinkView(string Url, DateTimeOffset ExpiresAt);

public sealed record PublicTemplateView(string Key, string Name, string Description, string CostClass, bool RequiresUserInput);

public sealed record PublicWorkflowStepView(int Order, string Type, string Name);

/// <summary>One option of a choice input: the value to send in <c>brief.inputs</c> and its label.</summary>
public sealed record PublicAppInputOptionView(System.Text.Json.JsonElement Value, string? Label);

/// <summary>
/// One field of the workflow's page. A typed app input (<c>kind</c> "input") has <c>id</c> and <c>type</c>: send its value as
/// <c>brief.inputs[id]</c> when starting a run.
/// </summary>
public sealed record PublicAppInputView(
    string Kind, string? Field, string? Role, string? Label, double? Min, double? Max,
    string? Id = null, string? Type = null, bool? Required = null,
    IReadOnlyList<PublicAppInputOptionView>? Options = null, string? Unit = null,
    System.Text.Json.JsonElement? Default = null, string? Help = null, string? Example = null,
    int? MaxLength = null, double? Step = null);

public sealed record PublicWorkflowView(
    string Id, string Name, string? Description, string? TemplateKey,
    IReadOnlyList<PublicWorkflowStepView> Steps, IReadOnlyList<PublicAppInputView> AppInputs,
    string CostClass, DateTime CreatedAt, DateTime UpdatedAt,
    System.Text.Json.JsonElement? InputSchema = null);

public sealed record PublicCreateWorkflowRequest(string? TemplateKey, string? Name);
