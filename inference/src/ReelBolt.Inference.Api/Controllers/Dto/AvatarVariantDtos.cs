namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// A variant of an object avatar made by the picture model (<c>POST .../avatars/{id}/variants</c>,
/// docs/design/avatar-objects.md "Avatar variants"). The estimate route takes the same body and reads only
/// <paramref name="ReferenceIds"/> and <paramref name="IncludeVideos"/>.
/// </summary>
/// <param name="Instruction">What to change (1..1000), for example "paint it matte red".</param>
/// <param name="VariantLabel">The variant's short label (1..60), for example "Red".</param>
/// <param name="VariantDescription">What sets it apart (1..500). Left out, the instruction is used.</param>
/// <param name="Name">The new avatar's name. Left out, "original · label".</param>
/// <param name="Description">The new avatar's own description. Left out, the original's.</param>
/// <param name="ReferenceIds">Which of the original's pictures and videos to change. Left out, all its pictures.</param>
/// <param name="IncludeVideos">Also make the chosen videos again from their changed first frame (default false).</param>
/// <param name="ConsentStatement">The OBJECT statement, verbatim, as served in <c>objectStatements</c>.</param>
/// <param name="ConsentLocale">The language that statement was shown in (<c>en</c> or <c>it</c>).</param>
/// <param name="AllowMediaEgress">
/// Allows sending the avatar's pictures to the AI provider for THIS request when the project's media consent is not
/// recorded. Counts only for a caller the safe rule lets allow it (a person signed in to the app); it never records
/// the project consent.
/// </param>
public sealed record CreateAvatarVariantRequest(
    string? Instruction = null,
    string? VariantLabel = null,
    string? VariantDescription = null,
    string? Name = null,
    string? Description = null,
    IReadOnlyList<Guid>? ReferenceIds = null,
    bool IncludeVideos = false,
    string? ConsentStatement = null,
    string? ConsentLocale = null,
    bool? AllowMediaEgress = null);

/// <summary>
/// An object to pull out of a video into a new object avatar (<c>POST .../avatars/extract-object</c>,
/// docs/design/avatar-objects.md "Pulling an object out of a video").
/// </summary>
/// <param name="MediaRef">The video: <c>file:&lt;guid&gt;</c> or <c>step_result:&lt;guid&gt;</c>.</param>
/// <param name="ObjectDescription">Which object (1..300), for example "the red kettle".</param>
/// <param name="Name">The new avatar's name.</param>
/// <param name="Description">The new avatar's description. Left out, the object description.</param>
/// <param name="AtSeconds">Up to 6 moments of the video to take the object from. Left out, evenly spaced ones.</param>
/// <param name="ConsentStatement">The OBJECT statement, verbatim.</param>
/// <param name="ConsentLocale">The language that statement was shown in.</param>
/// <param name="AllowMediaEgress">Allows sending frames to the segmentation provider for THIS request (see <see cref="CreateAvatarVariantRequest"/>). Without the permission whole frames are used.</param>
public sealed record ExtractObjectRequest(
    string? MediaRef = null,
    string? ObjectDescription = null,
    string? Name = null,
    string? Description = null,
    IReadOnlyList<double>? AtSeconds = null,
    string? ConsentStatement = null,
    string? ConsentLocale = null,
    bool? AllowMediaEgress = null);

/// <summary>What a variant would cost (<c>POST .../avatars/{id}/variants/estimate</c>); nothing is written.</summary>
/// <param name="BillingEnabled">False when this deployment charges no credits; the figures are then null.</param>
/// <param name="Credits">What the pictures are held at.</param>
/// <param name="CreditsHigh">The most the build can cost: the pictures plus, with videos, the videos ("up to").</param>
/// <param name="BalanceCredits">What the workspace can spend now.</param>
/// <param name="Affordable">Whether the workspace can pay <paramref name="CreditsHigh"/> now.</param>
/// <param name="Pictures">How many pictures would be changed.</param>
/// <param name="Videos">How many videos would be made again.</param>
/// <param name="UnavailableReason">Why a figure could not be formed, in plain words.</param>
public sealed record AvatarVariantEstimateResponse(
    bool BillingEnabled,
    double? Credits,
    double? CreditsHigh,
    double? BalanceCredits,
    bool Affordable,
    int Pictures,
    int Videos,
    string? UnavailableReason = null);

/// <summary>A build that was accepted (<c>202</c>).</summary>
public sealed record AvatarBuildStartedResponse(Guid JobId, string State);

/// <summary>How far a build is.</summary>
public sealed record AvatarBuildProgressResponse(int Done, int Total);

/// <summary>
/// One avatar build (<c>GET .../avatars/builds</c>, <c>GET .../avatars/builds/{jobId}</c>).
/// </summary>
/// <param name="Kind"><c>variant</c> or <c>extraction</c>.</param>
/// <param name="State">
/// <c>queued</c>, then the stage it is in (<c>pictures</c>, <c>videos</c>, <c>finding</c>, <c>cutting</c>, <c>creating</c>),
/// then <c>ready</c>, <c>failed</c> or <c>cancelled</c>.
/// </param>
/// <param name="Stage">The stage the build is in, or reached last (kept after it ended): <c>queued</c>, <c>pictures</c>, <c>videos</c>, <c>finding</c>, <c>cutting</c> or <c>creating</c>.</param>
/// <param name="Name">The name of the avatar being made.</param>
/// <param name="AvatarId">The avatar that was created, once <c>ready</c>.</param>
/// <param name="FailureCode">Why it failed, as a code.</param>
/// <param name="Message">A plain sentence: why it failed, or a note on how it was made.</param>
/// <param name="Credits">What the build's picture work was held at; null when nothing is charged.</param>
public sealed record AvatarBuildResponse(
    Guid JobId,
    string Kind,
    string State,
    string? Stage,
    AvatarBuildProgressResponse Progress,
    string Name,
    Guid? AvatarId,
    string? FailureCode,
    string? Message,
    double? Credits,
    DateTime CreatedAt,
    DateTime UpdatedAt);
