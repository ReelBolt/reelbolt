using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// How a <c>ThumbnailGenerate</c> step chooses the moment in the source it turns into the image.
/// <c>Auto</c> (the default) is the whole point of the step: the moment is chosen deterministically
/// from the <c>VideoAnalyze</c> artifact's own measured facts (take quality, sharpness, stillness,
/// duplicate grouping), never by a model and never by a caller-supplied timestamp.
/// <c>AtTime</c> pins the step to one explicit moment — the "I know the frame I want" escape hatch,
/// and the only way to thumbnail a source the analyze step never looked at.
/// </summary>
public enum ThumbnailSelection
{
    Auto,
    AtTime
}

/// <summary>
/// How a source frame whose aspect ratio differs from the requested thumbnail is fitted.
/// <c>Cover</c> (the default) fills the frame and crops the overflow — what a cover image wants.
/// <c>Contain</c> fits the whole frame inside the box, padded on the excess axis, so nothing is
/// cropped.
/// </summary>
public enum ThumbnailFit
{
    Cover,
    Contain
}

/// <summary>
/// Where the optional burned-in text sits. <c>None</c> (the default) draws no text at all and is
/// byte-identical to the no-overlay path. The three bands are the same named safe zones the
/// motion-graphics overlay path already uses, so a thumbnail's text lands where the platform's own
/// overlays land.
/// </summary>
public enum ThumbnailOverlayPosition
{
    None,
    LowerThird,
    UpperThird,
    Center
}

/// <summary>
/// What <see cref="ThumbnailGenerateStepConfig.AtTimeSec"/> is measured against.
/// <c>Source</c> (the default) is the source file's own timeline, exactly as before.
/// <c>Output</c> is the timeline of the <see cref="ThumbnailGenerateStepConfig.Source"/> as the author sees
/// it: for a trimmed reference (<c>InSec</c>/<c>OutSec</c>) second 0 is the window's start, so the file
/// position is <c>InSec + AtTimeSec</c>; for an assembled film (a compiled <c>StepOutput</c>/<c>StepResult</c>)
/// the two coincide. <c>Output</c> needs an explicit <c>Source</c>.
/// </summary>
public enum ThumbnailAtTimeBasis
{
    Source,
    Output
}

/// <summary>
/// JSON configuration for a <c>StepType.ThumbnailGenerate</c> workflow step. Deserialized from
/// <c>WorkflowStep.ThumbnailGenerateConfigJson</c>. A closed, versioned, strongly-typed record — the
/// same house style as <see cref="ExtractStepConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This step calls no model.</b> It is a deterministic, non-LLM step in the same family as
/// <c>VideoCompile</c>/<c>VideoAnalyze</c>: it selects a moment from the analyze step's measured
/// facts, extracts that frame with ffmpeg, optionally burns first-party text over it, and writes the
/// result. There is deliberately no provider id on this record — adding one is the <c>I2</c>
/// work package's job (a <c>Generated</c> source mode), not this one's.
/// </para>
/// <para>
/// <b>Positional record; every new member is APPENDED.</b> Stored configs are persisted JSON, so
/// inserting a parameter in the middle silently reinterprets every config already saved. The same
/// rule the other step configs state, and the reason the version stays 1 through additive changes.
/// </para>
/// </remarks>
/// <param name="Version">Config schema version. 1 is the only version this build writes.</param>
/// <param name="AnalysisStepOrder">
/// Which <c>VideoAnalyze</c> step's full artifact supplies the candidate shots. Required: the whole
/// selection strategy is built on that artifact's measurements, and a step with nothing to select
/// from could only ever produce an arbitrary frame.
/// </param>
/// <param name="AnalysisStepResultId">
/// Cross-execution override: resolve a prior run's analysis artifact instead of this execution's,
/// exactly as <c>VideoCompileStepConfig.AnalysisStepResultId</c> does. The same project-scope
/// authorization boundary applies — an id from another project is refused, never resolved.
/// </param>
[StepCard(
    "Chooses one frame of the project's video as a cover image.",
    ["A project video, or a footage analysis"],
    "A cover image",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits,
    ReadableBy = ["The project's files"])]
public sealed record ThumbnailGenerateStepConfig(
    int Version,
    int AnalysisStepOrder,
    Guid? AnalysisStepResultId = null,
    /// <summary>See <see cref="ThumbnailSelection"/>. <c>Auto</c> is the default and the intended path.</summary>
    ThumbnailSelection Selection = ThumbnailSelection.Auto,
    /// <summary>The moment to extract, in seconds on the selected source's own timeline. Ignored unless <see cref="Selection"/> is <see cref="ThumbnailSelection.AtTime"/>.</summary>
    [property: AppBindable(AppInputType.Number, AppInputType.Duration, Label = "Moment", Min = 0, Max = 86400)]
    double AtTimeSec = 0,
    /// <summary>Which analysed source clip to read. Only meaningful with <see cref="Selection"/> = <c>AtTime</c>; the <c>Auto</c> path picks the source as part of choosing the shot.</summary>
    int SourceIndex = 0,
    /// <summary>Output width in pixels. Clamped 16..4096 at execution time.</summary>
    int Width = 1280,
    /// <summary>Output height in pixels. Clamped 16..4096 at execution time.</summary>
    int Height = 720,
    /// <summary>See <see cref="ThumbnailFit"/>.</summary>
    ThumbnailFit Fit = ThumbnailFit.Cover,
    /// <summary>JPEG quality (ffmpeg <c>-q:v</c>), clamped 2..31 by the encoder's own scale — 2 is best. Clamped 2..31 at execution time.</summary>
    int JpegQuality = 4,
    /// <summary>File name the derivative is registered under, and the last segment of its object key.</summary>
    string OutputFileName = "thumbnail.jpg",
    /// <summary>
    /// Register the image as an ordinary <c>ProjectFile</c> (category <c>outputFiles</c>) in addition
    /// to the <c>media_derivatives</c> row, so it shows up in the project's file list and can be
    /// previewed. The derivative row is written either way — it is the index the editor reads.
    /// </summary>
    bool RegisterProjectFile = true,
    /// <summary>Caps how many shot candidates the selector scores. Clamped 1..500 at execution time.</summary>
    int MaxCandidates = 24,
    /// <summary>Shots shorter than this are not considered — a sub-half-second shot is usually a flash frame.</summary>
    double MinShotSeconds = 0.4,
    /// <summary>Prefer the best take of a duplicate group over a repeated attempt. On by default; turning it off restores plain best-score selection.</summary>
    bool PreferBestTake = true,
    /// <summary>
    /// Text burned into the image, or null for none. Workflow-author-supplied config (never model
    /// output), truncated to <see cref="MaxOverlayChars"/> at execution time.
    /// </summary>
    [property: AppBindable(AppInputType.ShortText, Label = "Text on the cover", MaxLength = 90)]
    string? OverlayText = null,
    /// <summary>See <see cref="ThumbnailOverlayPosition"/>. <c>None</c> (default) draws nothing.</summary>
    ThumbnailOverlayPosition OverlayPosition = ThumbnailOverlayPosition.None,
    /// <summary>Overlay font size as a percentage of the image height. Clamped 2..25 at execution time.</summary>
    int OverlayFontSizePct = 9,
    /// <summary>drawtext font colour — allowlisted at execution time exactly like <c>VideoCompileStepConfig.OverlayFontColor</c>, because it reaches ffmpeg's filter string.</summary>
    string OverlayFontColor = "white",
    /// <summary>drawbox colour behind the text, or <c>"none"</c> for no box — allowlisted exactly like <c>VideoCompileStepConfig.OverlayBoxColor</c>.</summary>
    string OverlayBoxColor = "black@0.45",
    /// <summary>Longest overlay text accepted, in characters. Clamped 1..400 at execution time.</summary>
    int MaxOverlayChars = 90,
    /// <summary>
    /// A brand font: a <c>.ttf</c>/<c>.otf</c> file in the run's own project (at most 10 MiB, magic number checked)
    /// that the overlay text is drawn with. Null (default) uses the server's configured font. A file that is
    /// missing, in another project, too large or not a font is a coded failure, never a silent fallback.
    /// </summary>
    Guid? FontProjectFileId = null,
    /// <summary>
    /// Take the frame from this video instead of an analysed source: a project file, a step's output or a
    /// compiled film (<c>StepOutput</c>/<c>StepResult</c>). Needs <see cref="ThumbnailSelection.AtTime"/>, except
    /// that a <c>ProjectFile</c> that is one of the analysis artifact's sources may use <c>Auto</c> (ranking then
    /// considers only that source's shots). With <c>AtTime</c> and a Source the analysis step is not required.
    /// </summary>
    VideoSourceRef? Source = null,
    /// <summary>See <see cref="ThumbnailAtTimeBasis"/>.</summary>
    ThumbnailAtTimeBasis AtTimeBasis = ThumbnailAtTimeBasis.Source)
{
    /// <summary>The largest brand font file accepted, in bytes.</summary>
    public const long MaxFontBytes = 10L * 1024 * 1024;

    private static readonly HashSet<string> NamedFontColors =
        new(StringComparer.OrdinalIgnoreCase) { "white", "black", "yellow" };

    private static readonly System.Text.RegularExpressions.Regex HexColor =
        new("^#[0-9A-Fa-f]{6}\\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HashSet<string> BoxColors =
        new(StringComparer.OrdinalIgnoreCase) { "black@0.45", "black@0.6", "white@0.4", "none" };

    /// <summary>True for a font colour that may reach ffmpeg's filter string (white/black/yellow or #RRGGBB).</summary>
    public static bool IsAllowedFontColor(string? color) =>
        color is not null && (NamedFontColors.Contains(color) || HexColor.IsMatch(color));

    /// <summary>True for a box colour that may reach ffmpeg's filter string; <c>none</c> means no box.</summary>
    public static bool IsAllowedBoxColor(string? color) => color is not null && BoxColors.Contains(color);

    /// <summary>
    /// Field-level validation, in the same shape and for the same reason as
    /// <c>VideoGenerateStepConfig.Validate</c>: the SAVE path wants a precise list of what is wrong
    /// with a config before it is persisted, while the EXECUTOR still clamps whatever reaches it
    /// (a config can also arrive from a template, an import, or the assistant's apply path, none of
    /// which pass through here).
    /// </summary>
    public static List<string> Validate(ThumbnailGenerateStepConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();

        if (config.Version < 1)
            errors.Add("Version must be at least 1.");
        // AtTime on an explicit Source (a file, a step output, a compiled film) never reads the analysis.
        bool needsAnalysis = !(config.Source is not null && config.Selection == ThumbnailSelection.AtTime);
        if (needsAnalysis && config.AnalysisStepOrder < 1 && !config.AnalysisStepResultId.HasValue)
            errors.Add("AnalysisStepOrder must be the order of a VideoAnalyze step, or AnalysisStepResultId must name one.");
        if (config.Width is < 16 or > 4096)
            errors.Add("Width must be between 16 and 4096.");
        if (config.Height is < 16 or > 4096)
            errors.Add("Height must be between 16 and 4096.");
        if (config.JpegQuality is < 2 or > 31)
            errors.Add("JpegQuality must be between 2 (best) and 31 (worst).");
        if (config.MaxCandidates is < 1 or > 500)
            errors.Add("MaxCandidates must be between 1 and 500.");
        if (config.MinShotSeconds < 0)
            errors.Add("MinShotSeconds must not be negative.");
        if (config.OverlayFontSizePct is < 2 or > 25)
            errors.Add("OverlayFontSizePct must be between 2 and 25.");
        if (config.MaxOverlayChars is < 1 or > 400)
            errors.Add("MaxOverlayChars must be between 1 and 400.");
        if (config.Selection == ThumbnailSelection.AtTime &&
            (!double.IsFinite(config.AtTimeSec) || config.AtTimeSec < 0 || config.AtTimeSec > VideoSourceRef.MaxWindowSec))
            errors.Add($"AtTimeSec must be a finite number of seconds in 0..{VideoSourceRef.MaxWindowSec}.");
        if (!Enum.IsDefined(config.Selection))
            errors.Add("Selection must be Auto or AtTime.");
        if (!Enum.IsDefined(config.Fit))
            errors.Add("Fit must be Cover or Contain.");
        if (!Enum.IsDefined(config.OverlayPosition))
            errors.Add("OverlayPosition must be None, LowerThird, UpperThird or Center.");
        if (!Enum.IsDefined(config.AtTimeBasis))
            errors.Add("AtTimeBasis must be Source or Output.");
        if (config.SourceIndex < 0)
            errors.Add("SourceIndex must not be negative.");
        if (config.OverlayPosition != ThumbnailOverlayPosition.None)
        {
            if (!IsAllowedFontColor(config.OverlayFontColor))
                errors.Add("OverlayFontColor must be white, black, yellow or #RRGGBB.");
            if (!IsAllowedBoxColor(config.OverlayBoxColor))
                errors.Add("OverlayBoxColor must be black@0.45, black@0.6, white@0.4 or none (no box).");
        }
        if (config.FontProjectFileId == Guid.Empty)
            errors.Add("FontProjectFileId must be the id of a .ttf/.otf project file, or null for the default font.");
        if (config.Source is not null)
        {
            if (config.Source.Validate() is { } sourceError)
                errors.Add($"Source is malformed: {sourceError}");
            else if (config.Source.DescribeWindowProblem() is { } windowProblem)
                errors.Add($"Source.InSec/OutSec {windowProblem}");
            else if (config.Selection == ThumbnailSelection.Auto && config.Source.Kind != VideoSourceKind.ProjectFile)
                errors.Add("Source of this kind needs Selection=AtTime: Auto ranks the analysed shots, which a step output or compiled film is not part of.");
        }
        if (config.AtTimeBasis == ThumbnailAtTimeBasis.Output && config.Source is null)
            errors.Add("AtTimeBasis=Output needs a Source (the timeline AtTimeSec is measured on).");
        if (!string.IsNullOrWhiteSpace(config.OutputFileName) &&
            (config.OutputFileName.Contains('/') || config.OutputFileName.Contains('\\')))
            errors.Add("OutputFileName must be a bare file name, not a path.");

        return errors;
    }
}
