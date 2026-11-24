namespace ReelBolt.WorkflowEngine.Services.Video.Cleanup;

public static class CleanupErrorCodes
{
    public const string DurationMismatch = "AUDIO_DURATION_MISMATCH";
    public const string ModelChecksumMismatch = "RNNOISE_MODEL_CHECKSUM_MISMATCH";
    public const string ModelMissing = "RNNOISE_MODEL_MISSING";
    public const string ProbeFailed = "AUDIO_CLEANUP_PROBE_FAILED";
    public const string FfmpegFailed = "AUDIO_CLEANUP_FFMPEG_FAILED";
}

/// <summary>A typed cleanup failure; <see cref="Code"/> is stable and safe to persist.</summary>
public sealed class CleanupException : Exception
{
    public CleanupException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}
