using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// Opens an image as a picture document in the thumbnail studio, through a propose→apply pair
/// (<c>ProposeCreatePictureDocument</c> / <c>ApplyCreatePictureDocument</c>): the image becomes the base layer, and
/// the answer carries <c>{documentId, name, width, height}</c> so the picture tools (<c>ProposeDesignGraphic</c>,
/// <c>ProposeAddTextLayer</c>) can follow with that <c>documentId</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only images.</b> The source is an image project file (<c>fileId</c>) or an <c>outputId</c> that resolves to one: a
/// <c>ThumbnailGenerate</c>/<c>ExtractFrame</c> step result (whose output names the project file it registered) or a
/// project file id. A video render or video file is refused with a pointer to <c>ExtractFrame</c>.
/// </para>
/// <para>
/// <b>Same door as the editor.</b> The document is made by <see cref="IImageDocumentCommands.CreateDocumentAsync"/>, i.e.
/// the editor's own create action, never raw SQL. The project is resolved through <see cref="IProjectAccessService"/>
/// exactly once per call (edit permission) and the file is looked up inside that project only, so another project's
/// file reads as "image not found". No storage key, bucket or URL appears in any result.
/// </para>
/// <para>
/// Not registered in the tool provider by this class: the integration ticket adds it, its approval-catalog entry and
/// its card.
/// </para>
/// </remarks>
public sealed class AssistantPictureDocumentTools(
    InferenceApiDbContext db,
    IConfirmationTokenService tokens,
    IProjectAccessService projectAccess,
    ICurrentUser currentUser,
    IImageDocumentCommands commands,
    IFileStorageService storage)
{
    internal const string OpCreateDocument = "PictureCreateDocument";
    internal const string ImageNotFoundMessage = "image not found";
    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";
    internal const string NeedOneSourceMessage = "pass exactly one of outputId or fileId";
    internal const string VideoMessage =
        "that is a video, not a picture: add an ExtractFrame step to pull a still frame from it, then open the frame's image";
    internal const int MaxNameLength = 120;

    /// <summary>How much of a stored image is read to find its pixel size (headers sit at the start).</summary>
    private const int HeaderReadBytes = 512 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Progress captions, in <c>AssistantToolProgress</c>'s voice (no tool names, ids or jargon), for the integration ticket.</summary>
    public static readonly IReadOnlyDictionary<string, string> Captions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ProposeCreatePictureDocument"] = "Preparing to open the image in the thumbnail studio",
        ["ApplyCreatePictureDocument"] = "Opening the image in the thumbnail studio",
    };

    private const string SourceNote =
        "Exactly one of outputId (a ThumbnailGenerate or ExtractFrame step result, or a project file id) or fileId (an image project file) is supplied. A video is refused; an ExtractFrame step is required first. ";

    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (Guid? outputId = null, Guid? fileId = null, string? name = null, Guid? projectId = null, CancellationToken ct = default) =>
                    ProposeCreate(context, outputId, fileId, name, projectId, ct),
                name: "ProposeCreatePictureDocument",
                description: "Proposes opening an image in the thumbnail studio as a new picture document (the image becomes its base layer), WITHOUT writing anything, and returns a confirmationToken and a summary. Free. " + SourceNote + "name is optional. " + AssistantProjectScope.ProjectIdNote + " The proposal's confirmationToken is accepted by ApplyCreatePictureDocument."),
            AIFunctionFactory.Create(
                (string confirmationToken, Guid? outputId = null, Guid? fileId = null, string? name = null, Guid? projectId = null, CancellationToken ct = default) =>
                    ApplyCreate(context, outputId, fileId, name, projectId, confirmationToken, ct),
                name: "ApplyCreatePictureDocument",
                description: "Creates the picture document a prior ProposeCreatePictureDocument proposed and returns { documentId, name, width, height }; the returned documentId is accepted by the picture tools (ProposeDesignGraphic, ProposeAddTextLayer, ...) for further work. Every argument and confirmationToken is verified against the proposal; a fabricated or stale token, or a changed argument, is refused and nothing is written. " + AssistantProjectScope.ProjectIdNote),
        };
    }

    private async Task<string> ProposeCreate(
        AssistantToolContext context, Guid? outputId, Guid? fileId, string? name, Guid? projectId, CancellationToken ct)
    {
        Project? project = AssistantProjectScope.Resolve(projectAccess, context, ProjectPermission.Edit, projectId, out string error);
        if (project is null)
            return error;

        (ProjectFile? file, string? problem) = await ResolveImageAsync(project.Id, outputId, fileId, ct);
        if (file is null)
            return Error(problem!);

        string? cleanName = CleanName(name);
        (int? w, int? h) = await ReadSizeAsync(project.Id, file.StorageKey, ct);
        string size = w is { } && h is { } ? $" ({w} x {h})" : string.Empty;
        string named = cleanName is null ? string.Empty : $" named \"{cleanName}\"";

        return JsonSerializer.Serialize(new
        {
            operation = OpCreateDocument,
            confirmationToken = tokens.Compute(OpCreateDocument, Payload(project.Id, file.Id, cleanName), currentUser.UserId),
            summary = $"Open the image \"{file.OriginalFileName}\"{size} in the thumbnail studio as a new picture{named}.",
            projectName = project.Name,
            imageName = file.OriginalFileName,
            paid = false
        }, Json);
    }

    private async Task<string> ApplyCreate(
        AssistantToolContext context, Guid? outputId, Guid? fileId, string? name, Guid? projectId, string confirmationToken, CancellationToken ct)
    {
        Project? project = AssistantProjectScope.Resolve(projectAccess, context, ProjectPermission.Edit, projectId, out string error);
        if (project is null)
            return error;

        (ProjectFile? file, string? problem) = await ResolveImageAsync(project.Id, outputId, fileId, ct);
        if (file is null)
            return Error(problem!);

        string? cleanName = CleanName(name);
        if (!tokens.Verify(OpCreateDocument, Payload(project.Id, file.Id, cleanName), currentUser.UserId, confirmationToken))
            return Error(StaleTokenMessage);

        ImageCommandOutcome outcome = await commands.CreateDocumentAsync(
            project.Id, new CreateEditImageDocumentRequest(file.Id, cleanName), ct);
        if (!outcome.Ok)
        {
            return JsonSerializer.Serialize(new
            {
                error = outcome.StatusCode == 404
                    ? ImageNotFoundMessage
                    : outcome.Message ?? $"the editor refused to open the image (status {outcome.StatusCode})",
                code = outcome.Code
            }, Json);
        }

        EditImageDocumentDto? created = outcome.Read<EditImageDocumentDto>();
        if (created is null)
            return Error("the editor did not return the new picture");

        int? width = created.CanvasWidth;
        int? height = created.CanvasHeight;
        if (width is null || height is null)
            (width, height) = await ReadSizeAsync(project.Id, file.StorageKey, ct);

        return JsonSerializer.Serialize(new
        {
            documentId = created.Id,
            name = created.Name ?? file.OriginalFileName,
            width,
            height,
            projectName = project.Name
        }, Json);
    }

    /// <summary>
    /// The image project file the arguments name, inside <paramref name="projectId"/> only. A step result resolves to
    /// the file its output names (<c>projectFileId</c>); a video is refused with the ExtractFrame pointer.
    /// </summary>
    private async Task<(ProjectFile? File, string? Problem)> ResolveImageAsync(
        Guid projectId, Guid? outputId, Guid? fileId, CancellationToken ct)
    {
        bool hasOutput = outputId is { } o && o != Guid.Empty;
        bool hasFile = fileId is { } f && f != Guid.Empty;
        if (hasOutput == hasFile)
            return (null, NeedOneSourceMessage);

        Guid? resolvedFileId = fileId;
        if (hasOutput)
        {
            var result = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == outputId!.Value && r.WorkflowExecution.ProjectId == projectId)
                .Select(r => new { r.Output, r.OutputStorageKey })
                .FirstOrDefaultAsync(ct);

            if (result is null)
            {
                resolvedFileId = outputId; // not a step result: the id of a project file
            }
            else if (await Services.Outputs.OutputGate.IsResultHeldAsync(db, outputId!.Value, ct))
            {
                return (null, ReelBolt.Shared.Storage.OutputHoldResponse.Message);
            }
            else if (TryReadProjectFileId(result.Output) is { } registered)
            {
                resolvedFileId = registered;
            }
            else
            {
                return (null, string.IsNullOrEmpty(result.OutputStorageKey)
                    ? "that output has no image to open (only ThumbnailGenerate and ExtractFrame outputs, and image files, can be opened)"
                    : VideoMessage);
            }
        }

        ProjectFile? file = await db.ProjectFiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == resolvedFileId!.Value && x.ProjectId == projectId
                                      && x.CsamStatus != CsamScanStatus.Quarantined, ct);
        if (file is null || !file.StorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            return (null, ImageNotFoundMessage);
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(db, file, ct))
            return (null, ReelBolt.Shared.Storage.OutputHoldResponse.Message);

        if (file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return (null, VideoMessage);
        if (!file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return (null, $"\"{file.OriginalFileName}\" is {file.MimeType}, not an image, so it cannot be opened as a picture");

        return (file, null);
    }

    private static Guid? TryReadProjectFileId(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("projectFileId", out JsonElement id)
                   && id.ValueKind == JsonValueKind.String
                   && Guid.TryParse(id.GetString(), out Guid parsed)
                   && parsed != Guid.Empty
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The stored image's pixel size from its header; null when it cannot be read (the answer then omits it).</summary>
    private async Task<(int? Width, int? Height)> ReadSizeAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await storage.DownloadAsync(projectId, storageKey, ct);
            byte[] head = new byte[HeaderReadBytes];
            int total = 0;
            int read;
            while (total < head.Length && (read = await stream.ReadAsync(head.AsMemory(total), ct)) > 0)
                total += read;
            return ImageHeader.TryReadSize(head.AsSpan(0, total), out int w, out int h) && w > 0 && h > 0 ? (w, h) : (null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, null);
        }
    }

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string cleaned = new(name.Where(c => !char.IsControl(c)).ToArray());
        cleaned = cleaned.Trim();
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] : cleaned;
    }

    private static string Payload(Guid projectId, Guid fileId, string? name) =>
        JsonSerializer.Serialize(new { op = OpCreateDocument, projectId, fileId, name }, Json);

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);
}
