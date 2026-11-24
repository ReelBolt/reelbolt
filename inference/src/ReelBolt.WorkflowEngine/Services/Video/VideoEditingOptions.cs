namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Options for the ffmpeg/ffprobe-backed video editing pipeline (plan §2). Bound from the
/// <c>VideoEditing</c> configuration section (<c>VideoEditing__*</c> env vars in compose).
/// </summary>
public sealed class VideoEditingOptions
{
    public const string SectionName = "VideoEditing";

    /// <summary>
    /// Root directory for per-execution/per-step scratch working files (extracted audio,
    /// intermediate segment files). Must be a volume pre-owned by the container's non-root
    /// <c>$APP_UID</c> (R12) — see the WorkflowEngine Dockerfile.
    /// </summary>
    public string ScratchPath { get; set; } = "/var/tmp/reelbolt-video";

    /// <summary>Executable name or path for ffmpeg. Resolved via PATH by default.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Executable name or path for ffprobe. Resolved via PATH by default.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>
    /// Maximum number of ffmpeg/ffprobe invocations allowed to run concurrently across the whole
    /// process (R13). Enforced by a single process-wide semaphore in <see cref="IVideoToolRunner"/>.
    /// Default 1: video encoding is CPU-heavy and competes with WorkflowEngine:MaxConcurrency.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>
    /// Additional cap on <see cref="VideoToolPriority.Low"/> invocations — the editor's media
    /// derivatives (proxy, filmstrip, waveform peaks, keyframe index). Derivatives are already
    /// scheduled behind every <see cref="VideoToolPriority.Normal"/> waiter, but that rule alone
    /// only protects a compile that has ALREADY been queued: with nothing else running, a burst of
    /// derivatives would still take the entire <see cref="MaxConcurrentJobs"/> budget and a
    /// compile arriving a moment later would wait behind them. This cap keeps derivative
    /// concurrency to a fraction of the budget permanently. Default 1 — derivatives are
    /// background convenience work and every one of them is a full video decode.
    /// </summary>
    public int DerivativeMaxConcurrentJobs { get; set; } = 1;

    /// <summary>
    /// <c>-threads</c> passed to the ffmpeg passes that do a real full video decode (shot
    /// detection, Phase 1 visual-analysis grid sampling) — NOT applied to the audio-only passes
    /// (silence detection, audio extraction), which don't decode video at all. Left unbounded
    /// (ffmpeg's own "auto" thread count), a single invocation can saturate every core on the
    /// host decoding a large/high-framerate source, starving this same process's own RabbitMQ
    /// and Postgres connections of CPU time long enough for their heartbeats/keepalives to lapse
    /// — surfacing as a misleadingly-labeled "Cancelled by user request" mid-step, discovered
    /// analyzing real 4K120 source footage. Default 4 is conservative for a shared/small host;
    /// raise it (VideoEditing__FfmpegThreads) on a host with cores to spare.
    /// </summary>
    public int FfmpegThreads { get; set; } = 4;

    /// <summary>
    /// Threads for the analysis copy (decoder and encoder). 0 (default) = every core; a positive value is that many,
    /// bounded to the core count. Only one heavy job runs at a time (the heavy lane), so on 8 cores the worst case is
    /// the copy on 8 threads, one normal job on <see cref="FfmpegThreads"/>, and one niced derivative on half its threads.
    /// </summary>
    public int AnalysisProxyThreads { get; set; }

    /// <summary>The thread count the analysis copy uses on this host.</summary>
    public int ResolveAnalysisProxyThreads() =>
        AnalysisProxyThreads > 0 ? Math.Min(AnalysisProxyThreads, Environment.ProcessorCount) : Environment.ProcessorCount;

    /// <summary>Hard wall-clock timeout, in seconds, for a single VideoAnalyze step's tool invocations.</summary>
    public int AnalyzeTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// A source at least this long (seconds) whose short side is above 1080 px is analysed through a 540p copy
    /// (see <see cref="AnalysisProxy"/>) when the step needs no full-resolution pixels. 0 turns the copy off.
    /// </summary>
    public int AnalysisProxyMinDurationSeconds { get; set; } = 120;

    /// <summary>Hard wall-clock timeout, in seconds, for a single VideoCompile step's tool invocations.</summary>
    public int CompileTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Font file path passed to drawtext's <c>fontfile=</c> for Phase 3 motion-graphics overlays
    /// (see docs/video-editing.md "Motion graphics (Phase 3)"). Must be a font actually installed
    /// in the image — the WorkflowEngine Dockerfile installs Alpine's <c>font-dejavu</c> package
    /// alongside ffmpeg specifically so this default resolves.
    /// </summary>
    public string FontFilePath { get; set; } = "/usr/share/fonts/dejavu/DejaVuSans.ttf";

    /// <summary>
    /// Directory of the bundled caption fonts, passed to the <c>ass</c> filter's <c>fontsdir=</c>
    /// when burned captions are rendered through libass (see docs/voiceover.md "Captions"). The
    /// WorkflowEngine Dockerfile copies <c>inference/fonts/captions</c> here. When the directory
    /// does not exist the captions fall back to DejaVu Sans and say so in the step output.
    /// </summary>
    public string CaptionFontsDir { get; set; } = "/usr/share/fonts/reelbolt-captions";

    /// <summary>
    /// Local path to the ReelBolt watermark RASTER the B8b output policy composites onto a
    /// watermarked (trial) render. ffmpeg's <c>overlay</c> needs a BITMAP and <c>brand/</c> ships
    /// SVG only, so the raster is generated from <c>brand/mark-mono.svg</c> by
    /// <c>brand/scripts/render-watermark.mjs</c> into <c>inference/assets/watermark.png</c>;
    /// the engine image COPYs it to the default path below (shipped appsettings.json).
    /// <para>
    /// Still empty-but-armed, deliberately: an EMPTY value, or a path that does not exist in the
    /// container, makes a watermarked plan cap the output and record + log that the mark was
    /// skipped for want of an asset, rather than failing the encode. A self-host deployment that
    /// does not ship the asset therefore still compiles; it just has no mark until
    /// <c>VideoEditing__WatermarkAssetPath</c> names one that exists.
    /// </para>
    /// </summary>
    public string WatermarkAssetPath { get; set; } = string.Empty;

    /// <summary>
    /// Thresholds of the voiceover source-speech guard (<c>VideoEditing:VoiceoverGuard:*</c>, env
    /// <c>VideoEditing__VoiceoverGuard__*</c>); see docs/voiceover.md "Source speech guard".
    /// </summary>
    public VoiceoverGuardOptions VoiceoverGuard { get; set; } = new();

    /// <summary>
    /// The per-source pass cache of VideoAnalyze (<c>VideoEditing:SourcePassCache:*</c>, env
    /// <c>VideoEditing__SourcePassCache__*</c>); see <see cref="Caching.SourcePassCache"/>.
    /// </summary>
    public SourcePassCacheOptions SourcePassCache { get; set; } = new();
}

/// <summary>Switch of the per-source pass cache.</summary>
public sealed class SourcePassCacheOptions
{
    /// <summary>
    /// When true (the default) the silence, shot, frame grid and event passes of a stored source are kept and reused.
    /// False runs every pass every time, exactly as before the cache existed.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true (the default) a derivative run that was asked to (<c>AnalyzeForWorkflows</c> on its request) also
    /// fills the cache for its video source, after the derivatives, at the derivatives' low priority. False ignores
    /// the ask. Nothing is filled while <see cref="Enabled"/> is false.
    /// </summary>
    public bool WarmOnUpload { get; set; } = true;
}

/// <summary>Uncalibrated thresholds for <see cref="VoiceoverSourceSpeechGuard"/>. Values are clamped to 0..1 when read.</summary>
public sealed class VoiceoverGuardOptions
{
    /// <summary>
    /// Share (0..1) of the kept program time over which a rendered film's own sound carries speech
    /// before that sound is dropped under the voiceover. Default 0.60.
    /// </summary>
    public double NarratedFilmSpeechCoverage { get; set; } = 0.60;

    /// <summary>
    /// Share (0..1) of the voiceover lines' total time over which other sources carry speech
    /// before the compile warns. Default 0.50.
    /// </summary>
    public double SpeechUnderLinesCoverage { get; set; } = 0.50;
}
