using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Archives;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The assistant's BINARY I/O surface (web chat and MCP share it): put audio, video, images or any other
/// file of a normal size into a project without a browser session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Upload</b> reuses <see cref="IDirectUploadService"/> exactly as <c>ProjectFilesController</c>'s
/// upload-url/upload-complete do: the same ticket MAC bound to the caller (CallerKey = the user id), the
/// same server-chosen key under <c>userFiles</c>, the plan's quota on the claimed size at the proposal
/// and on the stored size at completion, the same multipart machinery. The answer carries an S3
/// presigned PUT (the one sanctioned exception to "no presigned URL leaves the server": write-only,
/// 15 minutes (parts 30), for a key the server chose, and the object is moved to a never-presigned key
/// on ApplyUploadFile, S-05) but never the key itself; the opaque
/// <c>uploadToken</c> packs the ticket's MAC'd fields, and the key is re-derived from them.
/// </para>
/// <para>
/// <b>Import from URL</b> validates the URL's shape at the proposal (no network) and binds a confirmation
/// token to the organization, the project, the URL and the target path; the apply runs
/// <see cref="IUrlImportService"/> (SSRF-guarded, capped, quota-checked).
/// </para>
/// <para>
/// Every tool here writes or prepares a write, so each re-reads the caller's authority on the call:
/// a verified email, and Edit on the project (<see cref="AssistantProjectScope"/>). Refusals write nothing.
/// </para>
/// </remarks>
public sealed class AssistantUploadTools
{
    internal const string OpImportFileFromUrl = "ImportFileFromUrl.v1";

    /// <summary>The most part URLs one proposal presigns; a file needing more is refused (6.4 GiB at 64 MiB parts).</summary>
    public const int MaxPresignedParts = DirectUploadService.MaxPartUrlsPerCall;

    internal const string UploadTokenPrefix = "rbu1.";

    private static readonly JsonSerializerOptions OmitNulls = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly string InvalidUploadTokenJson = JsonSerializer.Serialize(new
    {
        error = "uploadToken is not one this server issued to you for this project; propose the upload again",
        code = "UPLOAD_TICKET_INVALID"
    });

    private static readonly string StaleTokenJson = JsonSerializer.Serialize(new
    {
        error = "stale confirmationToken, re-propose and try again"
    });

    private static readonly string DirectUploadDisabledJson = JsonSerializer.Serialize(new
    {
        error = "This deployment cannot accept direct uploads.",
        code = "DIRECT_UPLOAD_DISABLED"
    });

    /// <summary>The same generic answer the HTTP upload routes give a quarantined file: no detail.</summary>
    private static readonly string FileUnavailableJson = JsonSerializer.Serialize(new
    {
        error = "This file cannot be used.",
        code = "FILE_UNAVAILABLE"
    });

    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;
    private readonly IConfirmationTokenService _tokens;
    private readonly IDirectUploadService? _directUploads;
    private readonly IUrlImportService _urlImport;
    private readonly IProjectFileAddedEffects _fileAddedEffects;
    private readonly InferenceApiDbContext _db;
    private readonly Services.Telemetry.IProductEventEmitter? _productEvents;

    public AssistantUploadTools(
        IProjectAccessService projectAccess,
        ICurrentUser currentUser,
        IConfirmationTokenService tokens,
        IDirectUploadService? directUploads,
        IUrlImportService urlImport,
        IProjectFileAddedEffects fileAddedEffects,
        InferenceApiDbContext db,
        Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
        _tokens = tokens;
        _directUploads = directUploads;
        _urlImport = urlImport;
        _fileAddedEffects = fileAddedEffects;
        _db = db;
    }

    /// <summary>A part the client PUT, with the ETag storage answered for it.</summary>
    public sealed record UploadedPart(int PartNumber, [property: JsonPropertyName("etag")] string ETag);

    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (string fileName, long sizeBytes, string? contentType = null, string? directoryPath = null, Guid? projectId = null, CancellationToken ct = default) =>
                    ProposeUploadFile(context, fileName, sizeBytes, contentType, directoryPath, projectId, ct),
                name: "ProposeUploadFile",
                description: "Prepares an upload of one file of any type (audio, video, images, documents) into a project and returns where to PUT its bytes, WITHOUT recording anything yet. sizeBytes is the exact size of the file. Returns { projectName, uploadToken, method, url (one PUT) or parts [{ partNumber, url }] with partSizeBytes (large files: PUT each part and keep the ETag response header of each), partCount, headers (send them with every PUT), expiresAt, maxBytes, instructions }. The plan's storage quota is checked now and again on completion; a file needing more than 100 parts is refused. " + AssistantProjectScope.ProjectIdNote + " ApplyUploadFile records the file from that uploadToken after the bytes are uploaded."),
            AIFunctionFactory.Create(
                (string uploadToken, IReadOnlyList<UploadedPart>? parts = null, Guid? projectId = null, CancellationToken ct = default) =>
                    ApplyUploadFile(context, uploadToken, parts, projectId, ct),
                name: "ApplyUploadFile",
                description: "Records a file whose bytes were PUT to the url(s) a prior ProposeUploadFile returned. uploadToken is verified against the proposal; a multi-part upload supplies parts as [{ partNumber, etag }] for every part. Storage's real size must equal the proposed sizeBytes, and the quota is re-checked on it; otherwise the upload is discarded and nothing is recorded. Repeating the call returns the same file. Returns { fileId, fileName, directoryPath, sizeBytes, mimeType }. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (string url, string? fileName = null, string? directoryPath = null, Guid? projectId = null) =>
                    ProposeImportFileFromUrl(context, url, fileName, directoryPath, projectId),
                name: "ProposeImportFileFromUrl",
                description: "Proposes importing one file from a public https URL into a project, and returns a confirmationToken, WITHOUT downloading anything. The URL must be https, carry no user:password, and point at a public host (private, local and internal addresses are refused). fileName defaults to the URL's last path segment; directoryPath to the project root. " + AssistantProjectScope.ProjectIdNote + " The proposal's confirmationToken is accepted by ApplyImportFileFromUrl."),
            AIFunctionFactory.Create(
                (string url, string confirmationToken, string? fileName = null, string? directoryPath = null, Guid? projectId = null, CancellationToken ct = default) =>
                    ApplyImportFileFromUrl(context, url, fileName, directoryPath, confirmationToken, projectId, ct),
                name: "ApplyImportFileFromUrl",
                description: "Downloads the file a prior ProposeImportFileFromUrl proposed and adds it to the project. confirmationToken is verified against the proposal's url, fileName, directoryPath and project; otherwise nothing is downloaded. Redirects are followed at most 3 times, each re-checked; the file is capped in size and time and counts against the plan's storage quota. Returns { fileId, fileName, directoryPath, sizeBytes, mimeType }, or { error, code } with code IMPORT_URL_REFUSED, IMPORT_TOO_LARGE, IMPORT_FAILED (with upstreamStatus), IMPORT_TIMEOUT, IMPORT_BUSY (too many imports are already running) or STORAGE_QUOTA_EXCEEDED. " + AssistantProjectScope.ProjectIdNote)
        };
    }

    // ================================================================= upload

    private async Task<string> ProposeUploadFile(
        AssistantToolContext context, string fileName, long sizeBytes, string? contentType, string? directoryPath, Guid? projectId, CancellationToken ct)
    {
        if (UnverifiedEmail() is { } unverified) return unverified;
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string error);
        if (project is null) return error;
        if (_directUploads is null) return DirectUploadDisabledJson;

        if (!TryUploadPath(fileName, directoryPath, out string name, out string? relativePath))
            return Error("invalid path: fileName must be a plain file name and directoryPath relative, without '..'", "INVALID_PATH");
        if (sizeBytes < 0)
            return Error("sizeBytes must be the file's exact size in bytes.", "INVALID_SIZE");

        // Judged BEFORE the service opens a multipart upload, so a refusal leaves nothing behind in storage.
        if (sizeBytes > DirectUploadLimits.SinglePutMaxBytes)
        {
            long partSize = DirectUploadLimits.PartSizeFor(sizeBytes);
            long needed = (sizeBytes + partSize - 1) / partSize;
            if (needed > MaxPresignedParts)
            {
                return Error(
                    $"This file would need {needed} parts of {partSize} bytes; at most {MaxPresignedParts} parts ({MaxPresignedParts * partSize} bytes) can be uploaded this way. Upload it from the web app instead.",
                    "TOO_MANY_PARTS");
            }
        }

        DirectUploadTicket ticket;
        try
        {
            ticket = await _directUploads.CreateAsync(
                new DirectUploadRequest(
                    project.Id, project.OrganizationId, CallerKey(context), name, relativePath, sizeBytes, contentType, DirectUploadService.Category,
                    // S-05 / D5 exception: the URL lands in a chat transcript, so it is short-lived and the
                    // object it writes is moved to a never-presigned key on ApplyUploadFile.
                    RelocateOnComplete: true),
                ct);
        }
        catch (DirectUploadRefusedException refused)
        {
            return Refusal(refused);
        }

        IReadOnlyList<object>? parts = null;
        long expiresAtUnix;
        if (ticket.Mode == DirectUploadMode.Multipart)
        {
            IReadOnlyList<DirectUploadPartUrl> urls;
            try
            {
                urls = await _directUploads.PresignPartsAsync(ticket, Enumerable.Range(1, ticket.PartCount).ToList(), ct);
            }
            catch (DirectUploadRefusedException refused)
            {
                await _directUploads.AbortAsync(ticket, ct);
                return Refusal(refused);
            }

            parts = urls.Select(u => (object)new { partNumber = u.PartNumber, url = u.Url }).ToList();
            expiresAtUnix = urls.Count == 0 ? ticket.ExpiresAtUnix : urls.Min(u => u.ExpiresAtUnix);
        }
        else
        {
            // The PUT URL's own window (the ticket adds a grace period for the completion call only).
            expiresAtUnix = ticket.ExpiresAtUnix - (long)DirectUploadService.CompletionGrace.TotalSeconds;
        }

        bool multipart = ticket.Mode == DirectUploadMode.Multipart;
        return JsonSerializer.Serialize(new
        {
            projectName = project.Name,
            uploadToken = PackUploadToken(ticket),
            method = "PUT",
            url = multipart ? null : ticket.Url,
            parts,
            partSizeBytes = multipart ? ticket.PartSizeBytes : (long?)null,
            partCount = multipart ? ticket.PartCount : 1,
            headers = ticket.Headers,
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix).ToString("O", CultureInfo.InvariantCulture),
            maxBytes = ticket.SizeBytes,
            instructions = multipart
                ? $"PUT bytes [(n-1)*{ticket.PartSizeBytes}, n*{ticket.PartSizeBytes}) of the file to each part n's url with the given headers, keep each response's ETag header, then call ApplyUploadFile with the uploadToken and parts [{{ partNumber, etag }}] for all {ticket.PartCount} parts."
                : $"PUT the file's exact {ticket.SizeBytes} bytes to url with the given headers before expiresAt, then call ApplyUploadFile with the uploadToken."
        }, OmitNulls);
    }

    private async Task<string> ApplyUploadFile(
        AssistantToolContext context, string uploadToken, IReadOnlyList<UploadedPart>? parts, Guid? projectId, CancellationToken ct)
    {
        if (UnverifiedEmail() is { } unverified) return unverified;

        // A project the call names is judged first, exactly as every other tool does, so a foreign or missing one
        // reads "project not found" whatever the token looks like; the ticket's own project is checked below.
        if (AssistantProjectScope.IsExplicit(projectId)
            && AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string namedError) is null)
        {
            return namedError;
        }

        if (!TryUnpackUploadToken(uploadToken, out DirectUploadTicketEcho? echo))
            return InvalidUploadTokenJson;

        // The ticket names its project; a call that names ANOTHER project is refused rather than
        // re-pointed, and the caller's Edit on the ticket's project is re-checked now.
        if (AssistantProjectScope.IsExplicit(projectId) && projectId != echo!.ProjectId)
            return InvalidUploadTokenJson;
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, echo!.ProjectId, out string error);
        if (project is null) return error;
        if (_directUploads is null) return DirectUploadDisabledJson;

        DirectUploadTicket? ticket = _directUploads.Verify(echo, CallerKey(context));
        if (ticket is null || ticket.ProjectId != project.Id)
            return InvalidUploadTokenJson;

        bool alreadyRecorded = await _db.ProjectFiles.AnyAsync(f => f.Id == ticket.FileId && f.ProjectId == project.Id, ct);

        ProjectFile file;
        try
        {
            IReadOnlyList<DirectUploadCompletedPart> completed = (parts ?? [])
                .Where(p => p is not null)
                .Select(p => new DirectUploadCompletedPart(p.PartNumber, p.ETag ?? string.Empty))
                .ToList();
            file = await _directUploads.CompleteAsync(ticket, new DirectUploadCompletion(completed), ct);
        }
        catch (DirectUploadRefusedException refused)
        {
            return Refusal(refused);
        }

        if (!alreadyRecorded)
            await _fileAddedEffects.RunAsync(file, context.UserId, ct);

        if (file.CsamStatus == CsamScanStatus.Quarantined) return FileUnavailableJson;

        // Only the completion that wrote the row: a replay is the same upload again.
        if (!alreadyRecorded) await RecordFirstUploadAsync(context, project, ct);

        return FileAnswer(project, file);
    }

    // ================================================================= import from URL

    private string ProposeImportFileFromUrl(AssistantToolContext context, string url, string? fileName, string? directoryPath, Guid? projectId)
    {
        if (UnverifiedEmail() is { } unverified) return unverified;
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string error);
        if (project is null) return error;

        if (UrlImportTarget.ValidateUrl(url, out Uri? uri) is { } badUrl) return ImportRefusal(badUrl);
        if (UrlImportTarget.ResolveTarget(uri!, fileName, directoryPath, out UrlImportPath? target) is { } badPath) return ImportRefusal(badPath);

        string token = _tokens.Compute(OpImportFileFromUrl, ImportPayload(project.Id, uri!, target!), context.UserId, project.OrganizationId);
        string source = UrlImportTarget.Origin(uri!);

        return JsonSerializer.Serialize(new
        {
            operation = OpImportFileFromUrl,
            confirmationToken = token,
            summary = $"Download a file from {source} into \"{target!.OriginalPath}\" in project \"{project.Name}\".",
            projectName = project.Name,
            source,
            fileName = target.FileName,
            directoryPath = target.DirectoryPath,
            targetPath = target.OriginalPath
        }, OmitNulls);
    }

    private async Task<string> ApplyImportFileFromUrl(
        AssistantToolContext context, string url, string? fileName, string? directoryPath, string confirmationToken, Guid? projectId, CancellationToken ct)
    {
        if (UnverifiedEmail() is { } unverified) return unverified;
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string error);
        if (project is null) return error;

        if (UrlImportTarget.ValidateUrl(url, out Uri? uri) is { } badUrl) return ImportRefusal(badUrl);
        if (UrlImportTarget.ResolveTarget(uri!, fileName, directoryPath, out UrlImportPath? target) is { } badPath) return ImportRefusal(badPath);

        // Recomputed from THIS call's arguments and the caller's live organization: a different URL,
        // name, folder, project, user or organization than the one confirmed stops verifying.
        if (!_tokens.Verify(OpImportFileFromUrl, ImportPayload(project.Id, uri!, target!), context.UserId, project.OrganizationId, confirmationToken ?? string.Empty))
            return StaleTokenJson;

        UrlImportResult result = await _urlImport.ImportAsync(project, context.UserId, uri!.AbsoluteUri, fileName, directoryPath, ct);
        if (result.Refusal is { } refusal) return ImportRefusal(refusal);
        if (result.File!.CsamStatus == CsamScanStatus.Quarantined) return FileUnavailableJson;
        await RecordFirstUploadAsync(context, project, ct);
        return FileAnswer(project, result.File!);
    }

    /// <summary>
    /// product-analytics: the caller's first upload (<c>activation.file_uploaded</c>, once per user), the same
    /// event the dashboard's upload routes write. Only after the file is usable; best effort.
    /// </summary>
    private Task RecordFirstUploadAsync(AssistantToolContext context, Project project, CancellationToken ct) =>
        _productEvents is null
            ? Task.CompletedTask
            : _productEvents.FirstAsync(
                context.UserId, project.OrganizationId, Services.Telemetry.ProductEventNames.FileUploaded, ct);

    /// <summary>The canonical payload an import confirmation covers: project, the normalised URL and the target path.</summary>
    internal static string ImportPayload(Guid projectId, Uri url, UrlImportPath target) =>
        JsonSerializer.Serialize(new { op = OpImportFileFromUrl, projectId, url = url.AbsoluteUri, targetPath = target.OriginalPath });

    // ================================================================= upload token

    /// <summary>
    /// The opaque token handed to the client: every MAC'd ticket field EXCEPT the storage key (re-derived
    /// on the way back), base64url-encoded behind a version prefix. Not a secret and not a capability by
    /// itself — <see cref="IDirectUploadService.Verify"/> checks the MAC against the caller — just compact.
    /// </summary>
    internal static string PackUploadToken(DirectUploadTicket ticket)
    {
        PackedTicket packed = new(
            ticket.UploadToken, ticket.Mode == DirectUploadMode.Multipart ? "m" : "s", ticket.ProjectId, ticket.FileId, ticket.FileName,
            ticket.RelativePath, ticket.ContentType, ticket.SizeBytes, ticket.ExpiresAtUnix, ticket.UploadId, ticket.PartSizeBytes, ticket.PartCount,
            ticket.RelocateOnComplete ? true : null);
        string json = JsonSerializer.Serialize(packed, OmitNulls);
        return UploadTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryUnpackUploadToken(string? token, out DirectUploadTicketEcho? echo)
    {
        echo = null;
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(UploadTokenPrefix, StringComparison.Ordinal) || token.Length > 8192)
            return false;

        try
        {
            string b64 = token[UploadTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
            PackedTicket? packed = JsonSerializer.Deserialize<PackedTicket>(Convert.FromBase64String(b64));
            if (packed is null || string.IsNullOrWhiteSpace(packed.Mac) || packed.ProjectId == Guid.Empty || packed.FileId == Guid.Empty
                || string.IsNullOrWhiteSpace(packed.FileName) || packed.Mode is not ("s" or "m"))
            {
                return false;
            }

            string? key = DirectUploadService.DeriveKey(packed.ProjectId, packed.FileId, packed.FileName, packed.RelativePath);
            if (key is null) return false;

            echo = new DirectUploadTicketEcho(
                packed.Mac, packed.Mode == "m" ? DirectUploadMode.Multipart : DirectUploadMode.SinglePut, packed.ProjectId, packed.FileId, key,
                packed.FileName, packed.RelativePath, packed.ContentType ?? string.Empty, packed.SizeBytes, packed.ExpiresAtUnix,
                packed.UploadId, packed.PartSizeBytes, packed.PartCount, null, null, null, null)
            { RelocateOnComplete = packed.Relocate == true };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }

    private sealed record PackedTicket(
        [property: JsonPropertyName("t")] string Mac,
        [property: JsonPropertyName("m")] string Mode,
        [property: JsonPropertyName("p")] Guid ProjectId,
        [property: JsonPropertyName("f")] Guid FileId,
        [property: JsonPropertyName("n")] string FileName,
        [property: JsonPropertyName("r")] string? RelativePath,
        [property: JsonPropertyName("c")] string? ContentType,
        [property: JsonPropertyName("s")] long SizeBytes,
        [property: JsonPropertyName("e")] long ExpiresAtUnix,
        [property: JsonPropertyName("u")] string? UploadId,
        [property: JsonPropertyName("ps")] long PartSizeBytes,
        [property: JsonPropertyName("pc")] int PartCount,
        [property: JsonPropertyName("rl")] bool? Relocate);

    // ================================================================= helpers

    /// <summary>The caller a ticket's MAC is bound to: the user id, exactly as the browser routes bind it.</summary>
    private static string CallerKey(AssistantToolContext context) => context.UserId.ToString("D");

    private string? UnverifiedEmail() =>
        _currentUser.EmailVerified
            ? null
            : JsonSerializer.Serialize(new { error = EmailVerificationGate.Message, code = EmailVerificationGate.ErrorCode });

    private static bool TryUploadPath(string? fileName, string? directoryPath, out string name, out string? relativePath)
    {
        name = string.Empty;
        relativePath = null;
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        try
        {
            string normalized = ProjectFilePath.NormalizeRelativePath(fileName);
            if (normalized.Contains('/') || normalized.Any(char.IsControl)) return false;
            string? directory = ProjectFilePath.NormalizeDirectoryPath(directoryPath);
            name = normalized;
            relativePath = directory is null ? null : ProjectFilePath.CombineDirectoryAndFileName(directory, normalized);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string FileAnswer(Project project, ProjectFile file)
    {
        // A zip is unpacked in the background: say so, so the assistant does not treat the zip as the project's code.
        if (ArchiveUnpackPaths.IsZipFile(file))
        {
            string folder = ArchiveUnpackPaths.CombineDirectory(file.DirectoryPath, ArchiveUnpackPaths.SanitizeFolderName(file.OriginalFileName));
            return JsonSerializer.Serialize(new
            {
                fileId = file.Id,
                fileName = file.OriginalFileName,
                directoryPath = file.DirectoryPath,
                sizeBytes = file.SizeBytes,
                mimeType = file.MimeType,
                projectName = project.Name,
                unpacking = true,
                note = $"The zip is being unpacked into the folder \"{folder}\" (a number is added if that name is taken); its files appear in the project in a moment and the zip is removed once that succeeds."
            });
        }

        return JsonSerializer.Serialize(new
        {
            fileId = file.Id,
            fileName = file.OriginalFileName,
            directoryPath = file.DirectoryPath,
            sizeBytes = file.SizeBytes,
            mimeType = file.MimeType,
            projectName = project.Name
        });
    }

    private static string Error(string message, string code) =>
        JsonSerializer.Serialize(new { error = message, code });

    private static string Refusal(DirectUploadRefusedException refused) =>
        JsonSerializer.Serialize(new
        {
            error = refused.Message,
            code = refused.Code,
            usedBytes = refused.Code == PlanGate.StorageCode ? refused.UsedBytes : null,
            limitBytes = refused.Code == PlanGate.StorageCode ? refused.LimitBytes : null
        }, OmitNulls);

    private static string ImportRefusal(UrlImportRefusal refusal) =>
        JsonSerializer.Serialize(new
        {
            error = refusal.Message,
            code = refusal.Code,
            upstreamStatus = refusal.UpstreamStatus,
            limitBytes = refusal.LimitBytes,
            usedBytes = refusal.UsedBytes
        }, OmitNulls);
}
