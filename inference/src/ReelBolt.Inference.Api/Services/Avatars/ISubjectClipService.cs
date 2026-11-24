using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>Who asks for a subject clip: the member, whether their email is verified, and how the run is recorded as started.</summary>
/// <param name="UserId">The member the run is started for (its initiator).</param>
/// <param name="EmailVerified">An unverified address cannot start a run (nor price one).</param>
/// <param name="Initiator">How the run is recorded: <see cref="ExecutionInitiator.Human"/> from the editor, the assistant's own otherwise.</param>
public sealed record SubjectClipActor(Guid UserId, bool EmailVerified, ExecutionInitiator Initiator = ExecutionInitiator.Human);

/// <summary>
/// A clip that shows up to three of the project's avatars and objects (docs/design/avatar-objects.md "Generating a clip
/// with avatars from the editor (subject clips)"). The same request prices the clip and starts it.
/// </summary>
/// <param name="SubjectIds">1..3 avatars of the project, at most one of them a person, in the order chosen.</param>
/// <param name="Prompt">What happens in the clip (1..2000 characters), in the member's words.</param>
/// <param name="CapabilityId">A <c>VideoGeneration</c> model capability of the workspace or the platform; null = the default one.</param>
/// <param name="DurationSeconds">The clip's length; null = 6. A talking clip is as long as its speech.</param>
/// <param name="Ratio">The frame shape (<c>16:9</c>, <c>9:16</c>, ...); null = 16:9.</param>
/// <param name="Resolution">The model's size word (<c>768P</c>, ...); null = 768P when offered, else the model's cheapest.</param>
/// <param name="Takes">1..2 clips; null = 1.</param>
/// <param name="SpeechFileId">A project WAV or MP3 the one chosen person says: a talking clip. One person and nothing else.</param>
/// <param name="SubjectMode"><c>Auto</c> (null), <c>References</c> or <c>FirstFrame</c>: how the subjects reach the model.</param>
public sealed record SubjectClipRequest(
    IReadOnlyList<Guid>? SubjectIds,
    string? Prompt,
    Guid? CapabilityId = null,
    int? DurationSeconds = null,
    string? Ratio = null,
    string? Resolution = null,
    int? Takes = null,
    Guid? SpeechFileId = null,
    string? SubjectMode = null);

/// <summary>What a subject clip would cost, before anything is written (credits, not millicredits).</summary>
/// <param name="BillingEnabled">False when this deployment charges no credits; the figures are then null.</param>
/// <param name="Credits">What the run is held at: exactly the hold the engine reserves for the step.</param>
/// <param name="CreditsHigh">The most the run can cost (the step's fixed "up to" price; equal to <paramref name="Credits"/> today).</param>
/// <param name="BalanceCredits">What the workspace can spend now; null when it cannot be read.</param>
/// <param name="Affordable">Whether the workspace can pay the hold now (true when billing is off).</param>
/// <param name="Path">How the subjects reach the model: <c>References</c>, <c>FirstFrame</c> or <c>TalkingHead</c>; null when not decided.</param>
/// <param name="StillIncluded">One still picture of the subjects is made first and is part of the price (<c>FirstFrame</c>, <c>TalkingHead</c>).</param>
/// <param name="UnavailableReason">Why it cannot run (or what must happen first), in plain words; null = it can.</param>
/// <param name="UnavailableCode">The code beside <paramref name="UnavailableReason"/> (<c>CONSENT_REQUIRED</c> = the one-time consent is asked at start).</param>
public sealed record SubjectClipEstimate(
    bool BillingEnabled,
    double? Credits,
    double? CreditsHigh,
    double? BalanceCredits,
    bool Affordable,
    string? Path,
    bool StillIncluded,
    string? UnavailableReason = null,
    string? UnavailableCode = null);

/// <summary>A clip that was accepted: the hidden run's id, its state (<c>queued</c>) and what it is held at.</summary>
public sealed record SubjectClipStarted(Guid ExecutionId, string State, double? Credits);

/// <summary>A subject a clip shows, by name and kind (<c>Person</c> or <c>Object</c>); never an id.</summary>
public sealed record SubjectClipSubjectName(string Name, string Kind);

/// <summary>One subject clip as the editor and the assistant read it. No storage key, no URL.</summary>
/// <param name="ExecutionId">The hidden run.</param>
/// <param name="State"><c>queued</c>, <c>running</c>, <c>ready</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="OutputFileIds">The new project video files, one per delivered take; filled only when <c>ready</c>.</param>
/// <param name="Message">Why it failed, as one sentence; null otherwise.</param>
/// <param name="FailureCode">The failure code (SCREAMING_SNAKE); null otherwise.</param>
/// <param name="Subjects">What it shows, in the order chosen (an avatar deleted since is left out).</param>
public sealed record SubjectClipStatus(
    Guid ExecutionId,
    string State,
    IReadOnlyList<Guid> OutputFileIds,
    string? Message,
    string? FailureCode,
    IReadOnlyList<SubjectClipSubjectName> Subjects);

/// <summary>A stop that was asked for: the run and <c>stopping</c>.</summary>
public sealed record SubjectClipStopped(Guid ExecutionId, string State);

/// <summary>A typed answer of the subject clip service: the value, or the refusal (never both, never neither).</summary>
public sealed record SubjectClipOutcome<T>(T? Value, AvatarRefusal? Refusal) where T : class
{
    public bool Ok => Refusal is null;

    public static SubjectClipOutcome<T> Of(T value) => new(value, null);

    public static SubjectClipOutcome<T> Refused(AvatarRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Subject clips (docs/design/avatar-objects.md "Generating a clip with avatars from the editor (subject clips)"): a clip
/// that shows up to three of the project's avatars and objects, made outside any workflow by one hidden
/// <see cref="WorkflowDefinitionKind.SubjectClip"/> run of one inline <c>VideoGenerate</c> step, its takes registered as
/// ordinary project video files. One service behind the editor's REST routes and the assistant's tool, so they cannot
/// disagree on a check, a price or a limit.
/// <para>
/// <b>What the caller does first:</b> project access (Edit to price, start or stop; View to read) and, for a call that
/// carries <c>allowMediaEgress: true</c>, the per-call safe rule (<see cref="IMediaEgressCallPermission"/>, surface
/// <see cref="MediaEgressSurface.SubjectClip"/>): the result is the plain <c>mediaAllowed</c> argument. Everything else is
/// checked here, in the design's order: the switch of each kind (404), a verified email, who may generate with a person
/// (<c>Avatars:Generation</c>, the engine's own rule: <c>409 AVATAR_GENERATION_DISABLED</c>, an estimate answers it as
/// <c>unavailableCode</c>), the plan
/// (<c>video_generation</c>, plus <c>avatars</c> / <c>avatar_objects</c> per kind), the media consent
/// (<c>409 CONSENT_REQUIRED</c>: the project's recorded consent, or <c>mediaAllowed</c>), the subjects (the one rule), the
/// model path (<c>409 AVATAR_NOT_SUPPORTED_BY_MODEL</c> with <c>sibling</c>), the settings (<c>400 {message, errors}</c>),
/// credits (<c>402 INSUFFICIENT_CREDITS</c>), the paid run rate (<c>429</c>). Nothing is written before the last passed.
/// </para>
/// <para>
/// The run's step is configured by the server only: <c>AllowSourceMediaEgress</c> is set because the media permission
/// was established, <c>MaxSpendCredits</c> is the estimate's high figure, the step's output is registered as project
/// files and the step is never served from the step cache (a new clip is asked for). Answers are typed, never HTTP
/// results.
/// </para>
/// </summary>
public interface ISubjectClipService
{
    /// <summary>
    /// What the clip would cost and how the subjects would reach the model. Writes nothing. A missing media consent is
    /// not a refusal here: it is answered as <c>unavailableCode: CONSENT_REQUIRED</c> beside the price, and a start asks for it.
    /// </summary>
    /// <param name="mediaAllowed">The caller established the per-call media permission (see the interface remarks).</param>
    Task<SubjectClipOutcome<SubjectClipEstimate>> EstimateAsync(
        Project project, SubjectClipActor actor, SubjectClipRequest request, bool mediaAllowed, CancellationToken ct);

    /// <summary>Queues the clip: one hidden <see cref="WorkflowDefinitionKind.SubjectClip"/> run (answered <c>queued</c>).</summary>
    /// <param name="mediaAllowed">The caller established the per-call media permission (see the interface remarks).</param>
    Task<SubjectClipOutcome<SubjectClipStarted>> StartAsync(
        Project project, SubjectClipActor actor, SubjectClipRequest request, bool mediaAllowed, CancellationToken ct);

    /// <summary>One subject clip of the project, or null (another project's run, another kind's and a missing one alike).</summary>
    Task<SubjectClipStatus?> GetAsync(Guid projectId, Guid executionId, CancellationToken ct);

    /// <summary>The project's subject clips, newest first (at most 20); <paramref name="activeOnly"/> keeps the queued and running ones.</summary>
    Task<IReadOnlyList<SubjectClipStatus>> ListAsync(Guid projectId, bool activeOnly, CancellationToken ct);

    /// <summary>
    /// Asks the engine to stop a clip that has not ended (the workflow stop event, so the hold is released the usual way).
    /// <c>404</c> when the project has no such clip; <c>409 SUBJECT_CLIP_ALREADY_FINISHED</c> when it ended first.
    /// </summary>
    Task<SubjectClipOutcome<SubjectClipStopped>> CancelAsync(Guid projectId, Guid executionId, Guid userId, CancellationToken ct);
}
