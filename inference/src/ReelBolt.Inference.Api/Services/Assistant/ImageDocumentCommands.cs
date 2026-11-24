using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>What one picture-document command answered: the HTTP status the editor would have seen, and its body.</summary>
public sealed record ImageCommandOutcome(int StatusCode, JsonElement? Body)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public bool Ok => StatusCode is >= 200 and < 300;

    /// <summary>The body's <c>message</c> (or <c>error</c>), the plain sentence every refusal of these routes carries.</summary>
    public string? Message =>
        Body is { ValueKind: JsonValueKind.Object } body
            ? (body.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()
                : body.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null)
            : null;

    /// <summary>The body's <c>code</c>, when it has one.</summary>
    public string? Code =>
        Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

    public T? Read<T>() => Body is { } body ? body.Deserialize<T>(Web) : default;
}

/// <summary>
/// The picture-document commands the platform assistant may perform, routed through the SAME controller
/// actions the editor calls — so validation, the revision bump, retention marks, vendor dispatch,
/// transparency carry-over, metering and the project access check (which each action performs itself)
/// are exactly the editor's, not a second copy that could drift. See <see cref="ControllerImageDocumentCommands"/>.
/// </summary>
public interface IImageDocumentCommands
{
    /// <summary>Create a picture document from one of the project's images, with that image as its base layer.</summary>
    Task<ImageCommandOutcome> CreateDocumentAsync(Guid projectId, CreateEditImageDocumentRequest request, CancellationToken ct);

    Task<ImageCommandOutcome> CreateTextLayerAsync(Guid projectId, Guid documentId, CreateTextLayerRequest request, CancellationToken ct);

    Task<ImageCommandOutcome> UpdateLayerAsync(Guid projectId, Guid documentId, Guid layerId, UpdateEditImageLayerRequest request, CancellationToken ct);

    Task<ImageCommandOutcome> DeleteLayerAsync(Guid projectId, Guid documentId, Guid layerId, CancellationToken ct);

    Task<ImageCommandOutcome> UpdateDocumentAsync(Guid projectId, Guid documentId, UpdateEditImageDocumentRequest request, CancellationToken ct);

    Task<ImageCommandOutcome> RunOperationAsync(Guid projectId, Guid documentId, EditImageOperationRequest request, CancellationToken ct);

    /// <summary>
    /// The same operation run for a tool that established, under the one media rule
    /// (<see cref="ReelBolt.Inference.Api.Services.Avatars.IMediaEgressCallPermission"/>), that THIS call may send the
    /// subjects' pictures (<paramref name="mediaEgressGrantedBy"/>, a <c>MediaEgressPermission.By*</c> word) although the
    /// project's consent is not recorded. The default ignores the grant (fail closed: the operation then needs the
    /// project's consent as before); the real adapter hands it to the controller for this one call.
    /// </summary>
    Task<ImageCommandOutcome> RunOperationAsync(
        Guid projectId, Guid documentId, EditImageOperationRequest request, string mediaEgressGrantedBy, CancellationToken ct) =>
        RunOperationAsync(projectId, documentId, request, ct);

    Task<ImageCommandOutcome> SegmentSubjectAsync(Guid projectId, Guid documentId, SegmentSubjectRequest request, CancellationToken ct);

    /// <summary>
    /// Store a mask PNG the server drew (the assistant's "erase this area") at a key shaped exactly like one
    /// <c>assets/upload-url</c> issues for the document, with the same 24-hour retention mark, and return the key.
    /// </summary>
    Task<string> StoreMaskAsync(Guid projectId, Guid organizationId, Guid documentId, byte[] png, CancellationToken ct);
}

/// <summary>
/// <see cref="IImageDocumentCommands"/> over the real controllers, instantiated in the current request's
/// scope with <see cref="ActivatorUtilities"/>: the same constructor the router would use, the same
/// <see cref="Microsoft.AspNetCore.Http.HttpContext"/> (so usage attribution and the caller are the
/// request's own). Deliberately an adapter rather than a refactor of the three controllers into services:
/// it reuses every rule they enforce without moving any of them.
/// </summary>
public sealed class ControllerImageDocumentCommands : IImageDocumentCommands
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _services;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? _httpContext;

    public ControllerImageDocumentCommands(IServiceProvider services, Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContext = null)
    {
        _services = services;
        _httpContext = httpContext;
    }

    public async Task<ImageCommandOutcome> CreateDocumentAsync(Guid projectId, CreateEditImageDocumentRequest request, CancellationToken ct) =>
        From(await Controller<EditImageDocumentsController>().Create(projectId, request, ct));

    public async Task<ImageCommandOutcome> CreateTextLayerAsync(Guid projectId, Guid documentId, CreateTextLayerRequest request, CancellationToken ct) =>
        From(await Controller<EditImageDocumentsController>().CreateTextLayer(projectId, documentId, request, ct));

    public async Task<ImageCommandOutcome> UpdateLayerAsync(Guid projectId, Guid documentId, Guid layerId, UpdateEditImageLayerRequest request, CancellationToken ct) =>
        From(await Controller<EditImageDocumentsController>().UpdateLayer(projectId, documentId, layerId, request, ct));

    public async Task<ImageCommandOutcome> DeleteLayerAsync(Guid projectId, Guid documentId, Guid layerId, CancellationToken ct) =>
        From(await Controller<EditImageDocumentsController>().DeleteLayer(projectId, documentId, layerId, ct));

    public async Task<ImageCommandOutcome> UpdateDocumentAsync(Guid projectId, Guid documentId, UpdateEditImageDocumentRequest request, CancellationToken ct) =>
        From(await Controller<EditImageDocumentsController>().UpdateDocument(projectId, documentId, request, ct));

    public async Task<ImageCommandOutcome> RunOperationAsync(Guid projectId, Guid documentId, EditImageOperationRequest request, CancellationToken ct) =>
        From(await Controller<EditImageOperationsController>().RunOperation(projectId, documentId, request, ct));

    public async Task<ImageCommandOutcome> RunOperationAsync(
        Guid projectId, Guid documentId, EditImageOperationRequest request, string mediaEgressGrantedBy, CancellationToken ct)
    {
        EditImageOperationsController controller = Controller<EditImageOperationsController>();
        // For this one controller instance and this one call only: the instance is discarded after it.
        controller.InProcessMediaEgressGrant = mediaEgressGrantedBy;
        return From(await controller.RunOperation(projectId, documentId, request, ct));
    }

    public async Task<ImageCommandOutcome> SegmentSubjectAsync(Guid projectId, Guid documentId, SegmentSubjectRequest request, CancellationToken ct) =>
        From(await Controller<EditImageDocumentsController>().SegmentSubject(projectId, documentId, request, ct));

    public async Task<string> StoreMaskAsync(Guid projectId, Guid organizationId, Guid documentId, byte[] png, CancellationToken ct)
    {
        IAmazonS3 s3 = _services.GetRequiredService<IAmazonS3>();
        IConfiguration configuration = _services.GetRequiredService<IConfiguration>();
        InferenceApiDbContext db = _services.GetRequiredService<InferenceApiDbContext>();

        string key = $"{EditImageDocumentSupport.EditorPrefix(projectId, documentId)}mask-{Guid.NewGuid()}.png";
        using (MemoryStream body = new(png, writable: false))
        {
            PutObjectRequest put = new()
            {
                BucketName = configuration["MinIO:BucketName"] ?? "reelforge",
                Key = key,
                InputStream = body,
                ContentType = "image/png",
                AutoCloseStream = false
            };
            put.Metadata["category"] = "agentFiles";
            await s3.PutObjectAsync(put, ct);
        }

        // The same horizon an editor-issued upload gets: ripe 24 hours from now.
        int graceDays = Math.Clamp(configuration.GetValue("Billing:StorageRetention:GraceDays", StorageRetentionService.DefaultGraceDays), 0, 365);
        db.StorageRetentionMarks.Add(new StorageRetentionMark
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ProjectId = projectId,
            StorageKey = key,
            Category = "agentFiles",
            MarkedAt = DateTime.UtcNow.AddHours(24).AddDays(-graceDays)
        });
        await db.SaveChangesAsync(ct);
        return key;
    }

    private T Controller<T>() where T : ControllerBase
    {
        T controller = ActivatorUtilities.CreateInstance<T>(_services);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = _httpContext?.HttpContext ?? new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = _services }
        };
        return controller;
    }

    internal static ImageCommandOutcome From<T>(ActionResult<T> result) =>
        result.Result is { } inner ? From(inner) : new ImageCommandOutcome(200, Serialize(result.Value));

    internal static ImageCommandOutcome From(IActionResult result) => result switch
    {
        ObjectResult o => new ImageCommandOutcome(o.StatusCode ?? 200, Serialize(o.Value)),
        StatusCodeResult s => new ImageCommandOutcome(s.StatusCode, null),
        ForbidResult => new ImageCommandOutcome(403, null),
        ChallengeResult => new ImageCommandOutcome(401, null),
        _ => new ImageCommandOutcome(200, null)
    };

    private static JsonElement? Serialize(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), Web);
}
