using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>Moves a matched file's bytes to the project's quarantine prefix. Evidence, never served.</summary>
public interface ICsamQuarantineService
{
    /// <summary>
    /// Move the object under <c>projects/{projectId}/quarantine/{fileId}/{safeName}</c> and point the
    /// row at it. Returns the destination key. An already-quarantined row is a no-op (a re-scan must
    /// not move the same object twice). Throws when storage refuses; the caller records that.
    /// </summary>
    Task<string> MoveAsync(ProjectFile file, CancellationToken ct);
}

public sealed class CsamQuarantineService : ICsamQuarantineService
{
    private readonly IFileStorageService _storage;
    private readonly IProjectHistoryEraser? _historyEraser;
    private readonly TimeProvider _clock;
    private readonly ILogger<CsamQuarantineService> _logger;

    public CsamQuarantineService(
        IFileStorageService storage,
        TimeProvider? clock = null,
        IProjectHistoryEraser? historyEraser = null,
        ILogger<CsamQuarantineService>? logger = null)
    {
        _storage = storage;
        _historyEraser = historyEraser;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CsamQuarantineService>.Instance;
    }

    public async Task<string> MoveAsync(ProjectFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        // A second scan of an already-quarantined row must not move the object again: quarantine is
        // final until a human reviews the evidence. If a prior move was partial, the recorded
        // quarantine key and the actual storage key diverge — then move the ACTUAL object to the
        // recorded key and repair the row, rather than trusting the stale key.
        if (!string.IsNullOrWhiteSpace(file.CsamQuarantineKey))
        {
            if (!string.Equals(file.CsamQuarantineKey, file.StorageKey, StringComparison.Ordinal))
            {
                await MoveObjectAsync(file, file.CsamQuarantineKey, ct);

                // The row remembers the evidence before anything else can fail: an eraser throw must
                // not leave the move unreported (a retry would otherwise move from an empty key).
                file.StorageKey = file.CsamQuarantineKey;
                file.CsamQuarantinedAt ??= _clock.GetUtcNow().UtcDateTime;
                await EraseHistoryAsync(file, ct);
            }

            return file.CsamQuarantineKey;
        }

        string safeName = SafeFileName(file.OriginalFileName);
        string destination = $"projects/{file.ProjectId}/quarantine/{file.Id}/{safeName}";

        await MoveObjectAsync(file, destination, ct);

        // Point the row at the evidence BEFORE the eraser runs. If the eraser throws, the caller
        // still persists a row whose StorageKey/CsamQuarantineKey name the moved object.
        file.StorageKey = destination;
        file.CsamQuarantineKey = destination;
        file.CsamQuarantinedAt = _clock.GetUtcNow().UtcDateTime;

        await EraseHistoryAsync(file, ct);
        return destination;
    }

    /// <summary>
    /// Moves the bytes outside the copy-on-change protection: they must never be copied into the
    /// project's history.
    /// </summary>
    private async Task MoveObjectAsync(ProjectFile file, string destination, CancellationToken ct)
    {
        using (ProjectHistoryProtection.Suppress())
            await _storage.MoveAsync(file.ProjectId, file.StorageKey, destination, ct);
    }

    /// <summary>
    /// Erases the file from every version so quarantined bytes are never retained or restored
    /// (project versioning, contract section 6). The move already succeeded and the row already
    /// names the evidence, so a failure here is logged, never turned into a failed move: the
    /// retention GC retries it.
    /// </summary>
    private async Task EraseHistoryAsync(ProjectFile file, CancellationToken ct)
    {
        if (_historyEraser is null)
            return;

        try
        {
            await _historyEraser.EraseFileAsync(file.ProjectId, file.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CSAM quarantine of file {FileId} moved the object but could not erase its history copies; the retention GC retries",
                file.Id);
        }
    }

    /// <summary>
    /// Basename restricted to <c>[A-Za-z0-9._-]</c>; anything else becomes <c>_</c>. A name that
    /// yields nothing usable becomes <c>file</c>, so the key is always a legal object name.
    /// </summary>
    internal static string SafeFileName(string? originalFileName)
    {
        string name = (originalFileName ?? string.Empty).Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        StringBuilder builder = new(name.Length);
        foreach (char c in name)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');

        string safe = builder.ToString().Trim('.');
        if (safe.Length > 200) safe = safe[..200].TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "file" : safe;
    }
}
