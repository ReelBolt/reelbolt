using MassTransit;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// The one "a media upload just committed" request: a <see cref="MediaDerivativesRequested"/> with
/// <c>requestedBy:"upload"</c> for a <c>video/*</c> or <c>audio/*</c> <c>project_files</c> row, so the
/// engine makes its proxy, filmstrip, peaks and keyframe index — and stores its probe — before
/// anyone opens it (R22, P2-LAZY-04). Every upload path that commits a media row publishes through
/// here, so they can never drift into publishing different messages.
/// </summary>
/// <remarks>
/// <b>Best effort, after the commit.</b> The row already exists, so a broker outage that propagated
/// would answer an upload that fully succeeded with an error and invite a duplicate retry. The event is
/// a hint, not durable state; the editor's explicit <c>POST /media/derivatives/request</c> is the
/// recovery path. Cancellation still propagates: a cancelled request is never reported as a success.
/// The ETag is read best-effort too and may be empty, which the engine reads as "hash the content".
/// <para>
/// <b>Analysis at upload.</b> A <c>video/*</c> file also asks the engine to keep what its analysis passes find
/// (<see cref="MediaDerivativesRequested.AnalyzeForWorkflows"/>) when the project opted in and the global switch
/// is on. That is read HERE, once, through <see cref="IUploadAnalysisSetting"/>
/// (<see cref="AnalyzeForWorkflowsAsync"/>): callers pass the service and never read the setting themselves. No
/// service, an audio file or a failed read publishes the request as before. The two publishers that do not go
/// through <see cref="PublishAsync"/> (the coverage sweep and the editor's own request) ask the same method.
/// </para>
/// </remarks>
public static class UploadDerivativeRequest
{
    public static bool IsMediaMimeType(string? mimeType) =>
        !string.IsNullOrEmpty(mimeType) &&
        (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Only a video is analysed for workflows at upload; an audio file never is.</summary>
    public static bool IsVideoMimeType(string? mimeType) =>
        !string.IsNullOrEmpty(mimeType) && mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    public static async Task PublishAsync(
        IPublishEndpoint publishEndpoint,
        IFileStorageService storage,
        IUploadAnalysisSetting? analysis,
        ProjectFile file,
        string mimeType,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            string? etag = await storage.GetObjectETagAsync(file.ProjectId, file.StorageKey, ct);
            bool analyze = await AnalyzeForWorkflowsAsync(analysis, file.ProjectId, file.Id, mimeType, logger, ct);

            await publishEndpoint.Publish(new MediaDerivativesRequested(
                file.ProjectId,
                new MediaSourceRef(MediaSourceKind.ProjectFile, file.Id),
                file.Id,
                file.StorageKey,
                etag ?? string.Empty,
                mimeType,
                MediaDerivativeRequests.DefaultKinds,
                MediaDerivativeRequests.RequestedByUpload,
                DateTime.UtcNow,
                AnalyzeForWorkflows: analyze), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Uploaded media file {ProjectFileId} in project {ProjectId} but could not request its "
                + "derivative set; the editor's explicit derivative request is the recovery path",
                file.Id,
                file.ProjectId);
        }
    }

    /// <summary>
    /// The value of <see cref="MediaDerivativesRequested.AnalyzeForWorkflows"/> for a request about one project
    /// file: true for a <c>video/*</c> file of a project that opted in, while the operator's switch is on. Every
    /// publisher of a derivative request for a project file asks here (the upload paths through
    /// <see cref="PublishAsync"/>, the coverage sweep that serves synced folders, repository sync and lost
    /// requests, and the editor's own request), so the project's switch covers every way a video arrives.
    /// A setting that throws is read as "off": the derivative request must still go out, exactly as it did
    /// before the setting existed. No service or another kind of file is "off" too. Cancellation propagates.
    /// </summary>
    public static async Task<bool> AnalyzeForWorkflowsAsync(
        IUploadAnalysisSetting? analysis,
        Guid projectId,
        Guid fileId,
        string? mimeType,
        ILogger? logger,
        CancellationToken ct)
    {
        if (analysis is null || !IsVideoMimeType(mimeType)) return false;

        try
        {
            return await analysis.IsOnAsync(projectId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Could not read the upload-analysis setting for file {ProjectFileId} in project {ProjectId} "
                + "({ExceptionType}); its derivatives are requested without the analysis",
                fileId,
                projectId,
                ex.GetType().Name);
            return false;
        }
    }
}
