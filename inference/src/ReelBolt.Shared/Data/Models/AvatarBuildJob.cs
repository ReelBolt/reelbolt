namespace ReelBolt.Shared.Data.Models;

/// <summary>What an avatar build job makes (<c>avatar_build_jobs.kind</c>, stored as these lower-case words).</summary>
public static class AvatarBuildKinds
{
    /// <summary>A variant of an object avatar, made by the picture model from the original's own pictures (and videos).</summary>
    public const string Variant = "variant";

    /// <summary>A new object avatar from frames pulled out of a video.</summary>
    public const string Extraction = "extraction";

    public static readonly IReadOnlyList<string> All = [Variant, Extraction];
}

/// <summary>
/// The states of an avatar build job (<c>avatar_build_jobs.state</c>, docs/design/avatar-objects.md "Avatar variants"
/// and "Pulling an object out of a video"), stored and served as these lower-case words. A variant goes
/// <c>queued</c>, <c>pictures</c>, <c>videos</c>, <c>creating</c>, then <c>ready</c>, <c>failed</c> or <c>cancelled</c>;
/// an extraction goes <c>queued</c>, <c>finding</c>, <c>cutting</c>, <c>creating</c>, then the same ends. While a job
/// runs its state is the stage it is in; <c>stage</c> keeps the last stage it reached after it ended, so a failed job
/// still says where it stopped.
/// </summary>
public static class AvatarBuildStates
{
    public const string Queued = "queued";
    public const string Pictures = "pictures";
    public const string Videos = "videos";
    public const string Finding = "finding";
    public const string Cutting = "cutting";
    public const string Creating = "creating";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>Every state, in the order a job can pass through them.</summary>
    public static readonly IReadOnlyList<string> All =
        [Queued, Pictures, Videos, Finding, Cutting, Creating, Ready, Failed, Cancelled];

    /// <summary>The states in which a worker is (or should be) doing something for the job.</summary>
    public static readonly IReadOnlyList<string> Working = [Pictures, Videos, Finding, Cutting, Creating];

    /// <summary>Whether the job has ended: nothing more will happen to it.</summary>
    public static bool IsTerminal(string? state) => state is Ready or Failed or Cancelled;

    /// <summary>Whether the job is waiting for a worker or being worked on.</summary>
    public static bool IsOpen(string? state) => !IsTerminal(state);
}

/// <summary>
/// One avatar build (<c>avatar_build_jobs</c>, API-owned, migration <c>AddAvatarVariants</c>): a background job that
/// makes a new object avatar, either a VARIANT of an existing one (the picture model changes the original's own
/// pictures) or an EXTRACTION (frames of one object pulled out of a video). Both end by creating the avatar through
/// the same create path as the REST route, so every attach check applies.
///
/// <para>
/// <b>No tenant query filter.</b> A job is reached only through an authorized project: every route loads the project
/// through <c>IProjectAccessService</c> first and then reads <c>WHERE project_id = @p</c>
/// (TenantBoundaryInventoryTests lists it as unfiltered). Cascade-deleted with its project.
/// </para>
/// <para>
/// <b>Restart-safe.</b> What the job has done so far lives in <see cref="ParamsJson"/> (which pictures were made, what
/// was metered), saved after each step, and <see cref="UpdatedAt"/> is its heartbeat: a job left open by a restart is
/// picked up again by the sweeper once its heartbeat is stale, and a picture already bought is never bought twice.
/// </para>
/// </summary>
public class AvatarBuildJob
{
    public const int MaxMediaRefLength = 100;
    public const int MaxObjectDescriptionLength = 300;
    public const int MaxFailureCodeLength = 64;
    public const int MaxMessageLength = 1000;

    /// <summary><see cref="AllowedBy"/>: the project's recorded media consent allowed sending the media.</summary>
    public const string AllowedByProjectConsent = "project_consent";

    /// <summary><see cref="AllowedBy"/>: the request itself allowed it for this one call (<c>allowMediaEgress</c>, under the safe rule).</summary>
    public const string AllowedByCall = "call";

    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The project's organization when the job was made (what is held and metered is attributed to it).</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The member who asked. The avatar the job creates is attested by them (they sent the object statement).</summary>
    public Guid RequestedByUserId { get; set; }

    /// <summary><see cref="AvatarBuildKinds"/>.</summary>
    public string Kind { get; set; } = AvatarBuildKinds.Variant;

    /// <summary>A variant: the original avatar. Null for an extraction, and once the original is deleted.</summary>
    public Guid? SourceAvatarId { get; set; }

    /// <summary>An extraction: the video, as a media reference (<c>file:&lt;guid&gt;</c> or <c>step_result:&lt;guid&gt;</c>).</summary>
    public string? SourceMediaRef { get; set; }

    /// <summary>A variant: what to change, as the member typed it (untrusted text; sent to a model only sanitized and quoted).</summary>
    public string? Instruction { get; set; }

    /// <summary>An extraction: which object to pull out, as the member typed it (untrusted text).</summary>
    public string? ObjectDescription { get; set; }

    /// <summary>The name of the avatar the job makes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A variant: its label ("Red").</summary>
    public string? VariantLabel { get; set; }

    /// <summary>The request's other fields and the job's own progress notes, as JSON (jsonb). Never a storage key of another project.</summary>
    public string? ParamsJson { get; set; }

    /// <summary><see cref="AvatarBuildStates"/>.</summary>
    public string State { get; set; } = AvatarBuildStates.Queued;

    /// <summary>The stage the job is in, or reached last (<c>queued</c> until a worker takes it, then one of <see cref="AvatarBuildStates.Working"/>).</summary>
    public string? Stage { get; set; }

    /// <summary>How many items of the current stage are finished (pictures made, moments cut).</summary>
    public int Done { get; set; }

    /// <summary>How many items the job works through in all.</summary>
    public int Total { get; set; }

    /// <summary>The hidden workflow run that grabs frames or makes the videos, once one was queued (no FK: the engine owns runs).</summary>
    public Guid? ExecutionId { get; set; }

    /// <summary>The avatar the job created, once <see cref="AvatarBuildStates.Ready"/>. Set null when that avatar is deleted.</summary>
    public Guid? AvatarId { get; set; }

    /// <summary>The language the object statement was sent in (<c>en</c> or <c>it</c>); the created avatar stores that statement.</summary>
    public string StatementLocale { get; set; } = string.Empty;

    /// <summary>
    /// What allowed sending media out of the host for this job: <see cref="AllowedByProjectConsent"/> or
    /// <see cref="AllowedByCall"/>; null when nothing is sent (an extraction that uses whole frames).
    /// </summary>
    public string? AllowedBy { get; set; }

    /// <summary>Why the job failed, as a SCREAMING_SNAKE code; null otherwise.</summary>
    public string? FailureCode { get; set; }

    /// <summary>A plain sentence for the member: why it failed, or a note on how it was made.</summary>
    public string? Message { get; set; }

    /// <summary>The credit reservation taken up front for the job's picture work; null when nothing is held.</summary>
    public Guid? HoldId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The last change and the worker's heartbeat (a job in a working state with a stale one is picked up again).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token, mapped to PostgreSQL's <c>xmin</c> system column (no real column), so two workers
    /// (two API replicas during a deploy) cannot both claim one job, and a cancel is never overwritten by a worker's save.
    /// </summary>
    public uint RowVersion { get; set; }
}
