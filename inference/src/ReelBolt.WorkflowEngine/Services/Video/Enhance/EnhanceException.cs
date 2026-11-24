namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

/// <summary>
/// Stable failure codes for the local video engines. <c>ENHANCE_INVALID_REQUEST</c>,
/// <c>ENHANCE_NOT_LARGER</c>, <c>ENHANCE_OUTPUT_TOO_LARGE</c> and <c>ENHANCE_FPS_TOO_HIGH</c> come
/// from <see cref="EnhanceFilterBuilder"/>; the rest are raised by the executors below.
/// </summary>
public static class EnhanceErrorCodes
{
    public const string DurationMismatch = "ENHANCE_DURATION_MISMATCH";
    public const string SourceTooLong = "ENHANCE_SOURCE_TOO_LONG";
    public const string SourceTooShort = "ENHANCE_SOURCE_TOO_SHORT";
    public const string ProbeFailed = "ENHANCE_PROBE_FAILED";
    public const string FfmpegFailed = "ENHANCE_FFMPEG_FAILED";
    public const string PictureInvalid = "ENHANCE_PICTURE_INVALID";
}

/// <summary>A typed enhancement failure; <see cref="Code"/> and <see cref="Field"/> are stable and persistable.</summary>
public sealed class EnhanceException : Exception
{
    public EnhanceException(string code, string field, string message)
        : base(message)
    {
        Code = code;
        Field = field;
    }

    public string Code { get; }

    /// <summary>The request field at fault, or a stage name (<c>source</c>, <c>ffmpeg</c>, <c>output</c>).</summary>
    public string Field { get; }
}
