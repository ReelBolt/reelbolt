using System.Text;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>The outcome of resolving a run's brief: the text to start the run with, or one refusal.</summary>
public sealed record BriefResolution(string? Text, string? Error, Guid? FileId = null, string? FileName = null)
{
    public static BriefResolution Ok(string? text, Guid? fileId = null, string? fileName = null) => new(text, null, fileId, fileName);

    public static BriefResolution Refused(string error) => new(null, error);
}

/// <summary>
/// Resolves the optional <c>briefFileId</c> of a run start (T-202) to the run's <c>userRequest</c>. The caller has
/// already proven access to the project; the file must belong to that same project.
/// </summary>
public interface IBriefFileResolver
{
    Task<BriefResolution> ResolveAsync(Guid projectId, Guid? briefFileId, string? userRequest, CancellationToken ct);
}

/// <inheritdoc />
public sealed class BriefFileResolver(
    InferenceApiDbContext db,
    IFileStorageService storage,
    ReelBolt.Inference.Api.Services.Moderation.PublishModeration? publishModeration = null) : IBriefFileResolver
{
    /// <summary>Largest brief file, in bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    // One message for a foreign and a missing file: the refusal must not tell a caller another project's files exist.
    internal const string NotFound = "briefFileId must be a file of this project; none was found.";
    internal const string NotText = "briefFileId must be a text file (for example .txt or .md); this file is not text.";
    internal const string NotAvailable = "The brief file is still being checked or is not available.";
    internal static readonly string TooLarge = $"The brief file is larger than the {MaxBytes / 1024} KiB cap; shorten it or split it.";

    public async Task<BriefResolution> ResolveAsync(Guid projectId, Guid? briefFileId, string? userRequest, CancellationToken ct)
    {
        if (briefFileId is not { } fileId)
            return BriefResolution.Ok(userRequest);

        var file = await db.ProjectFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.ProjectId == projectId
                        && f.CsamStatus != CsamScanStatus.Quarantined)
            .Select(f => new { f.OriginalFileName, f.MimeType, f.SizeBytes, f.StorageKey })
            .FirstOrDefaultAsync(ct);
        if (file is null)
            return BriefResolution.Refused(NotFound);
        if (file is null || await Outputs.OutputGate.IsKeyHeldAsync(db, projectId, file.StorageKey, ct))
            return BriefResolution.Refused(NotFound);
        if (!IsLikelyText(file.MimeType, file.OriginalFileName))
            return BriefResolution.Refused(NotText);
        if (file.SizeBytes > MaxBytes)
            return BriefResolution.Refused(TooLarge);

        // A brief that is a generated output's bytes goes into a run's prompt only with a current verdict.
        if (publishModeration is not null
            && await publishModeration.GateStoredFileAsync(projectId, file.StorageKey, ct) is not null)
            return BriefResolution.Refused(NotAvailable);

        await using Stream stream = await storage.DownloadAsync(projectId, file.StorageKey, ct);
        byte[] buffer = new byte[MaxBytes + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (chunk == 0)
                break;
            read += chunk;
        }

        if (read > MaxBytes)
            return BriefResolution.Refused(TooLarge);
        if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
            return BriefResolution.Refused(NotText);

        string text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
        if (text.Length == 0)
            return BriefResolution.Refused("The brief file is empty.");

        string combined = string.IsNullOrWhiteSpace(userRequest) ? text : $"{text}\n\nNotes for this run:\n{userRequest}";
        return BriefResolution.Ok(combined, fileId, file.OriginalFileName);
    }

    /// <summary>
    /// The same text predicate AssistantFileTools / ProjectFilesController use for what they will
    /// read, delegated to the shared <see cref="ProjectFileTextPolicy"/> so the four surfaces agree.
    /// </summary>
    private static bool IsLikelyText(string mimeType, string fileName) =>
        ProjectFileTextPolicy.IsLikelyText(mimeType, fileName);
}

/// <summary>The one place a host without a registered resolver still answers a briefFileId honestly.</summary>
public static class BriefResolutionExtensions
{
    public static Task<BriefResolution> ResolveAsync(IBriefFileResolver? resolver, Guid projectId, Guid? briefFileId, string? userRequest, CancellationToken ct) =>
        resolver is not null
            ? resolver.ResolveAsync(projectId, briefFileId, userRequest, ct)
            : Task.FromResult(briefFileId is null
                ? BriefResolution.Ok(userRequest)
                : BriefResolution.Refused("Brief files are not available here."));
}
