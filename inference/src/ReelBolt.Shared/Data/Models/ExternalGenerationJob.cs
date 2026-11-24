namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Status of an external video generation job.
/// </summary>
public enum ExternalGenerationJobStatus
{
    Reserved,
    Submitted,
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>
    /// A take an EARLIER attempt of the run (a failed or superseded step result) left <see cref="Reserved"/>
    /// with no <see cref="ExternalGenerationJob.RemoteJobId"/>, given up when a manual Resume or a Retry
    /// re-bought the take under a new row (a product decision). It may still have reached the
    /// vendor, so it is NOT released: it keeps counting at its reserved amount toward the step's
    /// MaxSpend cap and, for its UTC day, the project, organization and global daily caps, as possibly spent.
    /// It is only excluded from the user's charge: never billed. A take is abandoned at most
    /// <c>VideoGeneration:MaxAbandonsPerTake</c> times per execution. Appended; persisted by name.
    /// </summary>
    Abandoned
}

/// <summary>
/// A record of a video generation job submitted to an external provider (e.g., MiniMax).
/// Owned by the WorkflowEngine (table <c>external_generation_jobs</c>).
///
/// <para>
/// <b>Deliberately no foreign key relationships.</b>
/// Generation jobs are indexed by denormalized <see cref="ExecutionId"/>, <see cref="StepResultId"/>,
/// and <see cref="ProjectId"/> rather than enforced entity relationships, so jobs survive
/// execution or step-result deletion and can be queried/analyzed independently. This mirrors the
/// design rationale of <see cref="DecisionObservation"/>.
/// </para>
/// </summary>
public class ExternalGenerationJob
{
    /// <summary>
    /// Unique identifier for this generation job record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The workflow execution this job belongs to. Not a real FK — see the class doc comment.
    /// </summary>
    public Guid ExecutionId { get; set; }

    /// <summary>
    /// The step result of the attempt that reserved the row; a later retry reusing the row keeps it.
    /// Not a real FK — see the class doc comment.
    /// </summary>
    public Guid StepResultId { get; set; }

    /// <summary>
    /// The project that owns this generation job. Not a real FK — see the class doc comment.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// The organization that pays for this job (denormalized; null only when unknown). Not a real
    /// FK — see the class doc comment.
    /// </summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>
    /// The clip ID associated with this generation job (if applicable).
    /// </summary>
    public string ClipId { get; set; } = "";

    /// <summary>
    /// SHA-256 hash of the generation request parameters, for deduplication and caching.
    /// </summary>
    public string RequestHash { get; set; } = "";

    /// <summary>
    /// The inference provider ID that was used to submit this job.
    /// </summary>
    public Guid ProviderId { get; set; }

    /// <summary>
    /// The remote job ID assigned by the external provider (e.g., MiniMax job ID).
    /// </summary>
    public string? RemoteJobId { get; set; }

    /// <summary>
    /// The current status of this generation job. A concurrency token: every update is conditional on the status
    /// the writer read (<c>WHERE status = @original</c>), so an attempt that lost its lease and kept running can never
    /// overwrite a row a newer attempt changed (for example <see cref="ExternalGenerationJobStatus.Abandoned"/>).
    /// </summary>
    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public ExternalGenerationJobStatus Status { get; set; } = ExternalGenerationJobStatus.Reserved;

    /// <summary>
    /// The estimated cost of this generation job, denominated in <see cref="CostUnit"/> — credits for
    /// every priced vendor, GPU-seconds for a self-hosted ComfyUI provider. Unit-neutral on purpose:
    /// one column carries both quantities and only <see cref="CostUnit"/> says which one it is.
    /// </summary>
    public decimal CostEstimate { get; set; }

    /// <summary>
    /// The actual cost incurred by this generation job (populated once complete), in
    /// <see cref="CostUnit"/>. See <see cref="CostEstimate"/> for why the name is unit-neutral.
    /// </summary>
    public decimal? ActualCost { get; set; }

    /// <summary>
    /// The unit <see cref="CostEstimate"/>/<see cref="ActualCost"/> are denominated in:
    /// <c>"credits"</c> (every priced vendor — 1 credit is <c>$0.006</c> of vendor cost, and the
    /// engine migration <c>ExternalGenerationJobsCostCredits</c> converted the rows that used to
    /// hold dollars, this being the column default since) or <c>"gpuseconds"</c> (a self-hosted
    /// ComfyUI provider, whose cost is GPU time). Budget queries filter on it so the two are never
    /// summed together.
    /// </summary>
    public string CostUnit { get; set; } = CreditsUnit;

    /// <summary>The persisted <see cref="CostUnit"/> for credit-denominated rows.</summary>
    public const string CreditsUnit = "credits";

    /// <summary>The persisted <see cref="CostUnit"/> for GPU-seconds-denominated rows.</summary>
    public const string GpuSecondsUnit = "gpuseconds";

    /// <summary>
    /// The storage key where the generated media artifact is stored (if succeeded).
    /// </summary>
    public string? StorageKey { get; set; }

    /// <summary>
    /// The <see cref="ProjectAvatar"/> this take showed, when it used one (docs/design/avatars.md "Engine column").
    /// No FK, like every other id here: the engine does not own avatars, and a usage row must survive the avatar's
    /// deletion. Added by the engine migration <c>AddExternalGenerationJobAvatar</c>; written from phase 2 on each
    /// take that used an avatar; read by <c>GET .../avatars/{id}/usage</c>. Null on every other row.
    /// </summary>
    public Guid? AvatarId { get; set; }

    /// <summary>
    /// Every project avatar this take showed, people and objects, as a JSON array of id strings
    /// (<c>subject_ids_json</c>, jsonb; docs/design/avatar-objects.md "Avatar subject kind (data model)"). Written for a
    /// step that names several subjects; <see cref="AvatarId"/> stays and holds the person when there is one, else the
    /// first object. No FK, like every other id here. Added by the engine migration
    /// <c>AddExternalGenerationJobSubjects</c>; read by <c>GET .../avatars/{id}/usage</c>. Null on every other row.
    /// </summary>
    public string? SubjectIdsJson { get; set; }

    /// <summary>The ids <see cref="SubjectIdsJson"/> holds; empty when it is null or unreadable.</summary>
    public static IReadOnlyList<Guid> ReadSubjectIds(string? subjectIdsJson)
    {
        if (string.IsNullOrWhiteSpace(subjectIdsJson))
            return [];
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(subjectIdsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];
            var ids = new List<Guid>();
            foreach (System.Text.Json.JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(item.GetString(), out Guid id))
                    ids.Add(id);
            }

            return ids;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>The <see cref="SubjectIdsJson"/> value of <paramref name="subjectIds"/>; null when there is none.</summary>
    public static string? WriteSubjectIds(IReadOnlyList<Guid>? subjectIds) =>
        subjectIds is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(subjectIds.Select(id => id.ToString("D")))
            : null;

    /// <summary>
    /// When this job record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this job record was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
