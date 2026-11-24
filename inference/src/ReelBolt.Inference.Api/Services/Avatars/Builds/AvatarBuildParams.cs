using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>
/// What <c>avatar_build_jobs.params_json</c> holds: the parts of the request that have no column of their own, and the
/// job's own notes on what it has done (which pictures were made, what was metered), saved after every step so a job
/// picked up again after a restart goes on where it stopped and buys nothing twice.
/// </summary>
public sealed class AvatarBuildParams
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---- The request ----

    /// <summary>The description of the avatar the job creates.</summary>
    public string? Description { get; set; }

    /// <summary>A variant: what sets it apart from its original.</summary>
    public string? VariantDescription { get; set; }

    /// <summary>A variant: the original's picture references to change, in the original's order.</summary>
    public List<Guid> PictureReferenceIds { get; set; } = [];

    /// <summary>A variant with videos: the original's video references to make again.</summary>
    public List<Guid> VideoReferenceIds { get; set; } = [];

    /// <summary>A variant: also make the chosen videos again.</summary>
    public bool IncludeVideos { get; set; }

    /// <summary>The member's instruction passed the prompt screen when the job was requested (so the worker does not ask again).</summary>
    public bool InstructionScreened { get; set; }

    /// <summary>An extraction: the moments the member asked for (seconds), when they named any.</summary>
    public List<double>? AtSeconds { get; set; }

    // ---- Money ----

    /// <summary>What was held up front for the job's picture (and find-the-object) work, in millicredits.</summary>
    public long HoldMillis { get; set; }

    /// <summary>What one picture was held at, in millicredits (the floor of a picture whose metering failed).</summary>
    public long PerPictureMillis { get; set; }

    /// <summary>The picture model prices by the size of what it delivers, so the hold settles with no ceiling.</summary>
    public bool SizePriced { get; set; }

    /// <summary>What the job has metered so far, in millicredits.</summary>
    public long MeteredMillis { get; set; }

    /// <summary>The hold was settled or released; nothing more is charged for this job.</summary>
    public bool HoldClosed { get; set; }

    /// <summary>A variant with videos: the most the videos may cost, in credits (the hidden run's own spend cap).</summary>
    public double? VideoMaxSpendCredits { get; set; }

    /// <summary>A variant with videos: the video model chosen when the request was checked, and each video's take.</summary>
    public AvatarBuildVideoModel? VideoModel { get; set; }

    /// <summary>An extraction: the object is looked for with the segmentation model (a provider and the media permission were there).</summary>
    public bool Segment { get; set; }

    /// <summary>An extraction: what one still sent to the segmentation model was held at, in millicredits.</summary>
    public long PerStillMillis { get; set; }

    /// <summary>An extraction: how many moments were held for (the stills the segmentation model may be shown).</summary>
    public int HeldStills { get; set; }

    /// <summary>An extraction: when the stills of the video were asked for (they are asked for once, then waited for).</summary>
    public DateTime? StillsRequestedAt { get; set; }

    /// <summary>An extraction: the source video's size in pixels, when it is known (the crops are grown to a minimum size in it).</summary>
    public int? SourceWidth { get; set; }

    public int? SourceHeight { get; set; }

    // ---- Hidden runs ----

    /// <summary>The hidden <c>AvatarBuild</c> runs the job queued, oldest first (a run of the items a failed run left is queued after it).</summary>
    public List<AvatarBuildRun> Runs { get; set; } = [];

    /// <summary>What each item of a hidden run came to: keyed by item (<c>grab:</c>, <c>take:</c>, <c>cut:</c>).</summary>
    public Dictionary<string, AvatarBuildRunOutcome> RunOutcomes { get; set; } = [];

    // ---- Work done ----

    /// <summary>One entry per picture, video or frame the job has finished with (made or failed).</summary>
    public List<AvatarBuildItem> Items { get; set; } = [];

    /// <summary>An extraction: the moments chosen, with the box found in each (when one was looked for).</summary>
    public List<AvatarBuildMoment>? Moments { get; set; }

    /// <summary>When the job first tried to create the avatar (it waits for the new files' safety checks, up to a limit).</summary>
    public DateTime? CreatingSince { get; set; }

    /// <summary>When the job began waiting for something outside it (stills, a hidden run).</summary>
    public DateTime? WaitingSince { get; set; }

    /// <summary>A note for the member that is kept when the job ends well (for example "whole frames were used").</summary>
    public string? Note { get; set; }

    // ---- Who is working on it ----

    /// <summary>
    /// A worker holds the job until this instant and moves it forward with every step it saves. A job in a working
    /// state whose lease has passed was left by a worker that stopped, and the sweeper hands it to another.
    /// </summary>
    public DateTime? LeaseUntil { get; set; }

    /// <summary>The job waits for something outside it (a safety check, a hidden run) and is looked at again from this instant.</summary>
    public DateTime? RetryAt { get; set; }

    /// <summary>How many times a worker stopped on the job for a reason of ours; after a few the job fails instead of trying again.</summary>
    public int Faults { get; set; }

    /// <summary>Whether a worker should take the job now: nobody holds it, and it is not waiting for a later instant.</summary>
    public bool IsDue(DateTime utcNow) =>
        (LeaseUntil is not { } lease || lease <= utcNow) && (RetryAt is not { } retry || retry <= utcNow);

    public static AvatarBuildParams Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AvatarBuildParams();
        try
        {
            return JsonSerializer.Deserialize<AvatarBuildParams>(json, Json) ?? new AvatarBuildParams();
        }
        catch (JsonException)
        {
            return new AvatarBuildParams();
        }
    }

    public string Write() => JsonSerializer.Serialize(this, Json);

    /// <summary>The finished entry of <paramref name="key"/>, when the job already dealt with it.</summary>
    public AvatarBuildItem? Find(string key) => Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// The id of the project file a job makes from one source: derived from the job and the source alone, so a job
    /// that is run again finds the file it already made (and the object it already stored) instead of buying it again.
    /// </summary>
    public static Guid FileIdFor(Guid jobId, string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("avatar-build:" + jobId.ToString("N") + ":" + key));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>What became of one source of a build job.</summary>
public sealed class AvatarBuildItem
{
    public const string Made = "made";
    public const string Failed = "failed";

    /// <summary>What the entry is about: <c>pic:{referenceId}</c>, <c>frame:{referenceId}</c>, <c>vid:{referenceId}</c> or <c>moment:{index}</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary><see cref="Made"/> or <see cref="Failed"/>.</summary>
    public string Outcome { get; set; } = Failed;

    /// <summary>The project file that was made (only with <see cref="Made"/>).</summary>
    public Guid? FileId { get; set; }

    /// <summary>The role the new reference takes (copied from the picture it came from).</summary>
    public string? Role { get; set; }

    /// <summary>The order of the source among the original's references.</summary>
    public int Ordinal { get; set; }

    public static string PictureKey(Guid referenceId) => "pic:" + referenceId.ToString("N");

    public static string FrameKey(Guid referenceId) => "frame:" + referenceId.ToString("N");

    public static string VideoKey(Guid referenceId) => "vid:" + referenceId.ToString("N");

    public static string MomentKey(int index) => "moment:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A hidden run's item: the first frame of a variant's video.</summary>
    public static string GrabKey(Guid referenceId) => "grab:" + referenceId.ToString("N");

    /// <summary>A hidden run's item: a variant's video made again from its changed first frame.</summary>
    public static string TakeKey(Guid referenceId) => "take:" + referenceId.ToString("N");

    /// <summary>A hidden run's item: one moment of an extraction, cut at full size.</summary>
    public static string CutKey(int index) => "cut:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The video model a variant's videos are made with, fixed when the request was checked.</summary>
public sealed class AvatarBuildVideoModel
{
    /// <summary>The default <c>VideoGeneration</c> capability (its id is what a step names).</summary>
    public Guid? ProviderId { get; set; }

    public string Model { get; set; } = string.Empty;

    public string Resolution { get; set; } = string.Empty;

    /// <summary>One entry per video, in the original's order.</summary>
    public List<AvatarBuildVideoTake> Takes { get; set; } = [];
}

/// <summary>One video of a variant made again: its length and frame shape at the model, and the most it may cost.</summary>
public sealed class AvatarBuildVideoTake
{
    public Guid ReferenceId { get; set; }

    public int DurationSeconds { get; set; }

    public string Ratio { get; set; } = string.Empty;

    /// <summary>The take's own spend cap, in credits (what the estimate priced it at, rounded up).</summary>
    public decimal MaxSpendCredits { get; set; }
}

/// <summary>One hidden <c>AvatarBuild</c> run of the job: a workflow of one step per item, and which item each step is.</summary>
public sealed class AvatarBuildRun
{
    /// <summary>What the run does: <see cref="Grabs"/>, <see cref="Takes"/> or <see cref="Cuts"/>.</summary>
    public string Purpose { get; set; } = Grabs;

    public const string Grabs = "grabs";
    public const string Takes = "takes";
    public const string Cuts = "cuts";

    /// <summary>The n-th run of this purpose (0-based); the ids below are derived from it, so a restart finds them again.</summary>
    public int Index { get; set; }

    public Guid WorkflowId { get; set; }

    public Guid ExecutionId { get; set; }

    /// <summary>The item of each step, in step order (step 1 is the first).</summary>
    public List<string> Items { get; set; } = [];

    /// <summary>The run was handed to the engine.</summary>
    public bool Queued { get; set; }

    public DateTime? QueuedAt { get; set; }

    /// <summary>What the run made was read into <see cref="AvatarBuildParams.RunOutcomes"/> (it ended).</summary>
    public bool Read { get; set; }
}

/// <summary>What one item of a hidden run came to.</summary>
public sealed class AvatarBuildRunOutcome
{
    public bool Ok { get; set; }

    /// <summary>The step result that holds what the item made (its stored object), when it was made.</summary>
    public Guid? StepResultId { get; set; }

    /// <summary>Why the item was not made, as the step's code, when it is known.</summary>
    public string? FailureCode { get; set; }
}

/// <summary>One moment of a video an extraction looks at.</summary>
public sealed class AvatarBuildMoment
{
    /// <summary>Where in the video, in seconds.</summary>
    public double AtSeconds { get; set; }

    /// <summary>Whether the object was looked for here, and found (null = not looked for: the whole frame is used).</summary>
    public bool? Found { get; set; }

    /// <summary>The still of this moment was dealt with (looked at, or kept whole).</summary>
    public bool Looked { get; set; }

    /// <summary>The crop as fractions of the frame (the found box with its margin), when there is one; null = the whole frame.</summary>
    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }
}

/// <summary>The failure codes a build job ends with (<c>avatar_build_jobs.failure_code</c>).</summary>
public static class AvatarBuildCodes
{
    /// <summary>The original is gone, no longer usable, or one of its pictures changed since it was attested.</summary>
    public const string AvatarNotUsable = "AVATAR_NOT_USABLE";

    /// <summary>No picture could be made.</summary>
    public const string VariantFailed = "VARIANT_FAILED";

    /// <summary>No default picture-edit model on fal.ai is set up.</summary>
    public const string VariantProviderMissing = "VARIANT_PROVIDER_MISSING";

    /// <summary>The default picture-edit model cannot take several pictures.</summary>
    public const string VariantModelUnsupported = "VARIANT_MODEL_UNSUPPORTED";

    /// <summary>No default video model that starts from a picture is set up.</summary>
    public const string VariantVideoModelMissing = "VARIANT_VIDEO_MODEL_MISSING";

    /// <summary>
    /// The default video model starts from a picture but its takes cannot be priced in credits: no price is set up for it
    /// while billing is on, or it is budgeted in GPU minutes (a self-hosted ComfyUI server), which a variant's take cannot
    /// carry. Refused before anything is held, so the videos are never silently left out.
    /// </summary>
    public const string VariantVideoModelUnpriced = "VARIANT_VIDEO_MODEL_UNPRICED";

    /// <summary>A model-made variant is for objects only.</summary>
    public const string VariantNotForPeople = "VARIANT_NOT_FOR_PEOPLE";

    /// <summary>The media permission is missing.</summary>
    public const string ConsentRequired = "CONSENT_REQUIRED";

    /// <summary>The member's instruction was refused by the content check.</summary>
    public const string ContentBlocked = "CONTENT_BLOCKED";

    /// <summary>The workspace's storage is full.</summary>
    public const string StorageQuotaExceeded = "STORAGE_QUOTA_EXCEEDED";

    /// <summary>The new files did not pass their safety checks in time.</summary>
    public const string ChecksTimedOut = "AVATAR_CHECKS_TIMED_OUT";

    /// <summary>No usable frame of the object came out of the video.</summary>
    public const string ObjectNotFoundInVideo = "OBJECT_NOT_FOUND_IN_VIDEO";

    /// <summary>The video cannot be used (not a video of this project, held, or its length is not known yet).</summary>
    public const string SourceUnavailable = "EXTRACTION_SOURCE_UNAVAILABLE";

    /// <summary>The job could not go on for a reason of ours (refunded).</summary>
    public const string BuildFailed = "AVATAR_BUILD_FAILED";

    /// <summary>The member cancelled it.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>A hidden run of the job could not be started, or reached none of its steps (an item outcome, never a job's code).</summary>
    public const string RunNotStarted = "RUN_NOT_STARTED";

    /// <summary>A hidden run of the job did not end in time and was stopped (an item outcome, never a job's code).</summary>
    public const string RunTimedOut = "RUN_TIMED_OUT";
}
