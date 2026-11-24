using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>
/// An already-authorized caller. Authorization stays at each entry (the controller asks <c>IProjectAccessService</c> once; the
/// assistant goes through its project scope): the service trusts that <see cref="Project"/> is one this user may edit.
/// </summary>
public sealed record ObjectAnalysisCaller(Guid UserId, Guid OrganizationId, Project Project, bool EmailVerified);

/// <summary>The timeline items the finished analysis is to be applied to by the finalizer.</summary>
public sealed record ObjectAnalysisIntent(Guid TimelineId, IReadOnlyList<Guid> ItemIds);

/// <summary>A refusal in transport-neutral form: the status the entry should answer with and the exact body (null for a bare 404).</summary>
public sealed record ObjectAnalysisRefusal(int StatusCode, object? Body);

/// <summary>A value or a refusal. <see cref="SuccessStatus"/> is 200, or 202 when a run was queued or is still in flight.</summary>
public sealed record ObjectAnalysisResult<T>(T? Value, ObjectAnalysisRefusal? Refusal, int SuccessStatus = 200)
{
    public bool Succeeded => Refusal is null;

    public static ObjectAnalysisResult<T> Ok(T value, int status = 200) => new(value, null, status);

    public static ObjectAnalysisResult<T> Refuse(int status, object? body = null) => new(default, new ObjectAnalysisRefusal(status, body));

    public static ObjectAnalysisResult<T> Refuse(ObjectAnalysisRefusal refusal) => new(default, refusal);
}

/// <summary>
/// "Find this object in my video", for any entry: the plan gate, estimate, consent, content reuse, credit hold, rate limit, the hidden
/// run and the status derived from it. Behaviour is the object-analysis controller's, moved here so the assistant shares it.
/// </summary>
public interface IObjectAnalysisService
{
    Task<ObjectAnalysisResult<ObjectAnalysisEstimate>> EstimateAsync(
        ObjectAnalysisCaller caller, string mediaRef, ObjectAnalysisEstimateRequest? body, CancellationToken ct);

    /// <param name="intent">Stored on the row when given; the finalizer then writes <c>regionEffects</c> onto those items when the run passes.</param>
    Task<ObjectAnalysisResult<ObjectAnalysisQueuedResponse>> RequestAsync(
        ObjectAnalysisCaller caller, string mediaRef, ObjectAnalysisRequestBody? body, ObjectAnalysisIntent? intent, CancellationToken ct);

    /// <summary>
    /// Read-only look at the hourly limit a NEW paid analysis by this user in this project would hit: the refusal it would get, or null.
    /// Creates nothing. A reused analysis is never limited, so callers skip it when the estimate says <c>cached</c>.
    /// </summary>
    Task<ObjectAnalysisRefusal?> PeekRunRateAsync(ObjectAnalysisCaller caller, CancellationToken ct);

    Task<ObjectAnalysisResult<ObjectAnalysisResponse>> GetAsync(Guid projectId, string mediaRef, Guid requestId, CancellationToken ct);

    Task<ObjectAnalysisResult<IReadOnlyList<ObjectAnalysisResponse>>> ListAsync(Guid projectId, string mediaRef, CancellationToken ct);

    /// <summary>
    /// The row's status from its run. A row still <c>queued</c> whose run is terminal is moved on (idempotent). A succeeded run
    /// re-reads its tracks from the artifact.
    /// </summary>
    Task<ObjectAnalysisDerived> DeriveAsync(ObjectEffectRequest row, CancellationToken ct);
}
