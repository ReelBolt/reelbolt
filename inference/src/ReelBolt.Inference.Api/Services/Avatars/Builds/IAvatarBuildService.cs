using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>The member a build is asked by: who they are and whether their email is verified.</summary>
public sealed record AvatarBuildActor(Guid UserId, bool EmailVerified);

/// <summary>
/// A variant to be made by the picture model (<c>POST .../avatars/{id}/variants</c> and its estimate). The estimate
/// reads only <see cref="ReferenceIds"/> and <see cref="IncludeVideos"/>; a start needs the rest.
/// </summary>
/// <param name="Instruction">What to change (1..1000), in the member's words.</param>
/// <param name="VariantLabel">The variant's label (1..60).</param>
/// <param name="VariantDescription">What sets it apart (1..500); left out, the instruction is used (cut to 500).</param>
/// <param name="Name">The new avatar's name; left out, "original · label".</param>
/// <param name="Description">The new avatar's own description; left out, the original's.</param>
/// <param name="ReferenceIds">Which of the original's pictures and videos to change; left out, all its pictures.</param>
/// <param name="IncludeVideos">Also make the chosen videos again from their changed first frame.</param>
/// <param name="ConsentStatement">The OBJECT statement, verbatim, for the avatar the job will create.</param>
/// <param name="ConsentLocale">The language that statement was shown in.</param>
public sealed record VariantBuildRequest(
    string? Instruction,
    string? VariantLabel,
    string? VariantDescription = null,
    string? Name = null,
    string? Description = null,
    IReadOnlyList<Guid>? ReferenceIds = null,
    bool IncludeVideos = false,
    string? ConsentStatement = null,
    string? ConsentLocale = null);

/// <summary>An object to pull out of a video into a new object avatar (<c>POST .../avatars/extract-object</c>).</summary>
/// <param name="MediaRef">The video: <c>file:&lt;guid&gt;</c> or <c>step_result:&lt;guid&gt;</c>.</param>
/// <param name="ObjectDescription">Which object (1..300), in the member's words.</param>
/// <param name="Name">The new avatar's name.</param>
/// <param name="Description">The new avatar's description; left out, the object description.</param>
/// <param name="AtSeconds">Up to 6 moments of the video; left out, evenly spaced ones.</param>
/// <param name="ConsentStatement">The OBJECT statement, verbatim.</param>
/// <param name="ConsentLocale">The language that statement was shown in.</param>
public sealed record ObjectExtractionRequest(
    string? MediaRef,
    string? ObjectDescription,
    string? Name,
    string? Description = null,
    IReadOnlyList<double>? AtSeconds = null,
    string? ConsentStatement = null,
    string? ConsentLocale = null);

/// <summary>What a variant would cost, before anything is written (credits, not millicredits).</summary>
/// <param name="BillingEnabled">False when this deployment does not charge credits; the figures are then null.</param>
/// <param name="Credits">What the pictures are held at.</param>
/// <param name="CreditsHigh">The most the job can cost: the pictures plus, with videos, the videos' ceiling ("up to").</param>
/// <param name="BalanceCredits">What the workspace can spend now; null when it cannot be read.</param>
/// <param name="Affordable">Whether the workspace can pay <paramref name="CreditsHigh"/> now (true when billing is off).</param>
/// <param name="Pictures">How many pictures would be changed.</param>
/// <param name="Videos">How many videos would be made again.</param>
/// <param name="UnavailableReason">Why no figure could be formed, in plain words; null otherwise.</param>
public sealed record VariantEstimate(
    bool BillingEnabled,
    double? Credits,
    double? CreditsHigh,
    double? BalanceCredits,
    bool Affordable,
    int Pictures,
    int Videos,
    string? UnavailableReason = null);

/// <summary>
/// What pulling an object out of a video would cost, before anything is written (credits). Finding the object with the
/// segmentation model is the only paid part; without a segmentation model or the media permission whole frames are used,
/// for free.
/// </summary>
/// <param name="BillingEnabled">False when this deployment does not charge credits; the figures are then null.</param>
/// <param name="Segment">The object would be looked for with the segmentation model (else whole frames are cut).</param>
/// <param name="Moments">How many moments of the video are used.</param>
/// <param name="Credits">What finding the object is held at; null when nothing is charged.</param>
/// <param name="BalanceCredits">What the workspace can spend now; null when it cannot be read or nothing is charged.</param>
/// <param name="Affordable">Whether the workspace can pay <paramref name="Credits"/> now (true when nothing is charged).</param>
public sealed record ExtractionEstimate(
    bool BillingEnabled,
    bool Segment,
    int Moments,
    double? Credits,
    double? BalanceCredits,
    bool Affordable);

/// <summary>A job that was accepted: its id and state (<c>queued</c>).</summary>
public sealed record AvatarBuildStarted(Guid JobId, string State);

/// <summary>One build as the routes, the web and the assistant read it. No storage key, no file id.</summary>
/// <param name="Kind"><c>variant</c> or <c>extraction</c>.</param>
/// <param name="State">One of <see cref="AvatarBuildStates.All"/>.</param>
/// <param name="Stage">The stage the job is in or reached last (<c>queued</c> until a worker takes it).</param>
/// <param name="Name">The name of the avatar the job makes.</param>
/// <param name="AvatarId">The created avatar, once ready.</param>
/// <param name="Credits">What the job's picture work was held at (credits); null when nothing is charged.</param>
public sealed record AvatarBuildStatus(
    Guid JobId,
    string Kind,
    string State,
    string? Stage,
    int Done,
    int Total,
    string Name,
    Guid? AvatarId,
    string? FailureCode,
    string? Message,
    double? Credits,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>A typed answer of the build service: the value, or the refusal (never both, never neither).</summary>
public sealed record AvatarBuildOutcome<T>(T? Value, AvatarRefusal? Refusal) where T : class
{
    public bool Ok => Refusal is null;

    public static AvatarBuildOutcome<T> Of(T value) => new(value, null);

    public static AvatarBuildOutcome<T> Refused(AvatarRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Avatar build jobs (docs/design/avatar-objects.md "Avatar variants", "Pulling an object out of a video"): a variant
/// of an object avatar made by the picture model, and an object pulled out of a video. One service behind the REST
/// routes and, later, the assistant and MCP tools, so they cannot disagree on a check, a price or a limit.
/// <para>
/// <b>What the caller does first:</b> project access (Edit to start or cancel, View to read) and, for a call that
/// carries <c>allowMediaEgress: true</c>, the safe rule (<see cref="IMediaEgressCallPermission"/>): the result is the
/// plain <c>mediaAllowed</c> argument. Everything else is checked here, in the order of the design: the objects switch,
/// a verified email, the plan, the original, the object statement, the media permission (the project's recorded
/// consent, or <c>mediaAllowed</c>), the model, the limits, credits, the paid run-rate bucket.
/// </para>
/// <para>Answers are typed (<see cref="AvatarBuildOutcome{T}"/> with an <see cref="AvatarRefusal"/>), never HTTP results.</para>
/// </summary>
public interface IAvatarBuildService
{
    /// <summary>What making the variant would cost. Writes nothing; needs neither the statement nor the media permission.</summary>
    Task<AvatarBuildOutcome<VariantEstimate>> EstimateVariantAsync(
        Project project, AvatarBuildActor actor, Guid avatarId, VariantBuildRequest request, CancellationToken ct);

    /// <summary>Queues a variant build. The hold for all pictures is taken here; the job runs in the background.</summary>
    /// <param name="mediaAllowed">The caller established the per-call media permission (see the interface remarks).</param>
    Task<AvatarBuildOutcome<AvatarBuildStarted>> StartVariantAsync(
        Project project, AvatarBuildActor actor, Guid avatarId, VariantBuildRequest request, bool mediaAllowed, CancellationToken ct);

    /// <summary>
    /// What an extraction of <paramref name="moments"/> moments (null: the default count) would cost and whether the
    /// object would be looked for with the segmentation model. Writes nothing; checks the switch, a verified email and the
    /// plan, like a start. The assistant's approval card prices the call with it.
    /// </summary>
    /// <param name="mediaAllowed">The caller established the per-call media permission (else the project's consent decides).</param>
    Task<AvatarBuildOutcome<ExtractionEstimate>> EstimateExtractionAsync(
        Project project, AvatarBuildActor actor, int? moments, bool mediaAllowed, CancellationToken ct);

    /// <summary>Queues an extraction: an object pulled out of a video into a new object avatar.</summary>
    /// <param name="mediaAllowed">The caller established the per-call media permission; without it (and without the project consent) whole frames are used.</param>
    Task<AvatarBuildOutcome<AvatarBuildStarted>> StartExtractionAsync(
        Project project, AvatarBuildActor actor, ObjectExtractionRequest request, bool mediaAllowed, CancellationToken ct);

    /// <summary>The project's builds, newest first (at most 50).</summary>
    Task<IReadOnlyList<AvatarBuildStatus>> ListBuildsAsync(Guid projectId, CancellationToken ct);

    /// <summary>One build of the project, or null.</summary>
    Task<AvatarBuildStatus?> GetBuildAsync(Guid projectId, Guid jobId, CancellationToken ct);

    /// <summary>
    /// Cancels a build that has not ended. The worker stops between two pictures; what was already bought is settled.
    /// Null when the project has no such job; an ended job is answered as it is (idempotent).
    /// </summary>
    Task<AvatarBuildStatus?> CancelBuildAsync(Guid projectId, Guid jobId, CancellationToken ct);
}
