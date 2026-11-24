using System.Globalization;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Quality;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>R10.7: a source that was converted from HDR to SDR before grading. The name is the clip's own name, never an id or path.</summary>
public sealed record HdrToneMapped(string Name, string Kind);

/// <summary>A note about the render that is not about one item: a stable code and a plain message.</summary>
public sealed record RenderWarning(string Code, string Message);

/// <summary>One region's outcome in a finished render.</summary>
public sealed record RenderedRegion(long StartFrame, long EndFrame, string RecipeHash, bool FromCache, int LayerCount);

/// <summary>The complete outcome of <see cref="TimelineRenderer.RenderAsync"/>.</summary>
public sealed record TimelineRenderResult(
    string OutputPath,
    ResolvedRenderTarget Target,
    RenderPlan Plan,
    IReadOnlyList<RenderedRegion> Regions,
    LoudnessMeasurement? MeasuredLoudness,
    bool LoudnessNormalised,
    IReadOnlyList<IReadOnlyList<string>> Invocations,
    IReadOnlyList<HdrToneMapped>? HdrToneMapped = null,
    IReadOnlyList<RenderWarning>? Warnings = null,
    IReadOnlyList<QualityFinding>? LoudnessChecks = null)
{
    public int CachedRegionCount => Regions.Count(r => r.FromCache);
}

/// <summary>Everything a render needs besides the document: where to work, how to reach ffmpeg, which font.</summary>
public sealed record TimelineRenderEnvironment(
    IVideoToolRunner Runner,
    string ScratchDirectory,
    string FontFilePath,
    TimeSpan Timeout,
    int Threads,
    IRegionCache Cache,
    Guid ProjectId,
    Func<string, int, Task>? Progress = null,
    /// <summary>
    /// T-006: who a generated-text moderation call is attributed to, and the surface it came from.
    /// Null (every pre-moderation caller) means no attribution is available, so no call is made even
    /// when a gate is wired.
    /// </summary>
    ModerationAttribution? Moderation = null,
    /// <summary>R10.7: whether ffmpeg can tone-map HDR to SDR. Null means probe this host's ffmpeg once per process.</summary>
    IHdrToneMapCapability? ToneMapCapability = null,
    /// <summary>T-015: the verified local mask videos of segmentation-masked clips, by (analysis, track). Null for an unmasked timeline.</summary>
    IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? SegmentationMasks = null,
    /// <summary>T-011: whether ffmpeg can decode log footage explicitly (zscale). Null means probe this host's ffmpeg once per process.</summary>
    ICameraProfileCapability? CameraProfileCapability = null);

/// <summary>
/// The renderer proper: plan → per-region mezzanine (cache or ffmpeg) → concat → audio mix →
/// optional loudnorm → final encode. Works on LOCAL files only; the step executor owns storage,
/// the database and the step contract, and the golden-parity harness drives this same class
/// through its own asset resolver — one code path, measured once.
/// </summary>
public sealed class TimelineRenderer
{
    private readonly ILogger _logger;
    private readonly IContentModerationGate? _moderation;
    private readonly int _moderationMaxChars;

    public TimelineRenderer(
        ILogger logger,
        IContentModerationGate? moderation = null,
        int moderationMaxTextChars = 8000)
    {
        _logger = logger;
        _moderation = moderation;
        _moderationMaxChars = moderationMaxTextChars;
    }

    public async Task<TimelineRenderResult> RenderAsync(
        EditTimeline document,
        TimelineRenderStepConfig config,
        IReadOnlyDictionary<AssetRef, LocalAsset> assets,
        string outputPath,
        TimelineRenderEnvironment env,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(env);

        // P8: compound and multicam clips are flattened into plain tracks and media clips before
        // planning, so the planner, the region builder, the mix and the recipe hash never see them.
        document = TimelineFlattener.Flatten(document);

        // T-006: title/caption text is generated content, screened before any region pass or ffmpeg
        // call. Unavailable fails open; Block throws and the step records CONTENT_BLOCKED.
        await ScreenGeneratedTextAsync(document, env, ct);

        // R09.7: sidecar-only or no-captions deliveries leave the captions out of the picture. The text
        // was screened above either way.
        if (!config.BurnInCaptions)
            document = document with { Tracks = document.Tracks.Select(t => t with { Items = t.Items.Where(i => i is not Caption).ToList() }).ToList() };

        ResolvedRenderTarget target = TimelineRenderPresets.Resolve(config, document.Settings);
        RenderPlan plan = SegmentPlanner.Plan(document, config.Range);
        RequireAssets(plan, assets);
        // RD-8: a mix the ceiling would refuse fails now, before the expensive region pass.
        AudioMixBuilder.EnsureWithinKeyframedGainCeiling(plan, assets, document.Tracks, document.Buses);
        foreach (AssetRef lut in TimelineAssetReferences.LutFiles(document).Distinct())
        {
            if (!assets.ContainsKey(lut))
                throw new InvalidOperationException($"LUT {lut.Kind} {lut.ProjectFileId ?? lut.StepResultId} was not resolved before rendering.");
        }

        // Every LUT the document references is parsed ONCE, here, before any ffmpeg runs: a file we cannot
        // read fails the render immediately and names itself, instead of after the first region has rendered.
        Dictionary<AssetRef, CubeLut> luts = [];
        foreach (AssetRef lutRef in TimelineAssetReferences.LutFiles(document).Distinct())
        {
            try
            {
                luts[lutRef] = await CubeLut.LoadAsync(assets[lutRef].LocalPath, ct).ConfigureAwait(false);
            }
            catch (CubeLutException ex)
            {
                throw new RenderLutException(lutRef, ex);
            }
        }

        Directory.CreateDirectory(env.ScratchDirectory);
        List<IReadOnlyList<string>> invocations = [];
        List<RenderedRegion> rendered = [];
        string fontFingerprint = FontFingerprint(env.FontFilePath);

        // R10.7: HDR sources the plan actually plays are converted to SDR first, when this ffmpeg can.
        List<HdrToneMapped> toneMapped = [];
        List<RenderWarning> warnings = [];
        IReadOnlyDictionary<AssetRef, string>? hdrToneMaps = null;
        if (target.VideoEnabled)
        {
            IReadOnlyDictionary<AssetRef, string> hdrAssets = HdrToneMap.Plan(assets);
            Dictionary<AssetRef, string> used = [];
            if (hdrAssets.Count > 0)
            {
                foreach (RenderLayer layer in plan.Regions.SelectMany(r => r.Layers))
                {
                    IEnumerable<MediaClip> clips = layer.Item switch
                    {
                        MediaClip c => [c],
                        Transition when layer.Transition is { } n => [n.From, n.To],
                        _ => []
                    };
                    foreach (MediaClip clip in clips)
                    {
                        if (hdrAssets.TryGetValue(clip.AssetRef, out string? kind) && used.TryAdd(clip.AssetRef, kind))
                            toneMapped.Add(new HdrToneMapped(string.IsNullOrWhiteSpace(clip.Name) ? "Clip" : clip.Name, kind));
                    }
                }
            }

            if (used.Count > 0)
            {
                IHdrToneMapCapability capability = env.ToneMapCapability ?? FfmpegHdrToneMapCapability.Shared;
                if (await capability.IsAvailableAsync(env.Runner, ct).ConfigureAwait(false))
                {
                    hdrToneMaps = used;
                }
                else
                {
                    toneMapped.Clear();
                    warnings.Add(new RenderWarning(HdrToneMap.UnavailableWarning, HdrToneMap.UnavailableMessage));
                }
            }
        }

        // T-011: a clip that names a camera profile gets the log to Rec.709 stage at the start of its
        // leg. HDR wins: an HDR source that also names a profile is tone-mapped only and the render
        // says so once. The stage's decode prefix wants zscale; without it the swscale fallback is used.
        bool anyCameraProfile = false;
        if (target.VideoEnabled)
        {
            bool profileIgnoredHdr = false;
            foreach (RenderLayer layer in plan.Regions.SelectMany(r => r.Layers))
            {
                IEnumerable<MediaClip> clips = layer.Item switch
                {
                    MediaClip c => [c],
                    Transition when layer.Transition is { } n => [n.From, n.To],
                    _ => []
                };
                foreach (MediaClip clip in clips)
                {
                    if (string.IsNullOrEmpty(clip.InputProfile)) continue;
                    if (HdrToneMap.KindOf(assets[clip.AssetRef].Probe) is not null) profileIgnoredHdr = true;
                    else anyCameraProfile = true;
                }
            }

            if (profileIgnoredHdr)
                warnings.Add(new RenderWarning(CameraProfileStage.IgnoredHdrWarning, CameraProfileStage.IgnoredHdrMessage));
        }

        bool cameraProfileZscale = true;
        if (anyCameraProfile && target.VideoEnabled)
        {
            ICameraProfileCapability capability = env.CameraProfileCapability ?? FfmpegCameraProfileCapability.Shared;
            cameraProfileZscale = await capability.IsAvailableAsync(env.Runner, ct).ConfigureAwait(false);
        }

        string? programVideoPath = null;
        if (target.VideoEnabled)
        {
            // ---- Regions ----
            List<string> regionPaths = new(plan.Regions.Count);
            for (int i = 0; i < plan.Regions.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                RenderRegion region = plan.Regions[i];
                string hash = RegionRecipeHasher.Hash(region, target, assets, fontFingerprint, hdrToneMaps, env.SegmentationMasks, cameraProfileZscale);
                string regionPath = Path.Combine(env.ScratchDirectory, $"region-{i:D4}-{hash[..16]}.mp4");
                regionPaths.Add(regionPath);

                await Report(env, $"Rendering region {i + 1}/{plan.Regions.Count}", 5 + (int)(60.0 * i / Math.Max(1, plan.Regions.Count))).ConfigureAwait(false);

                bool hit = false;
                try
                {
                    hit = await env.Cache.TryFetchAsync(env.ProjectId, hash, regionPath, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "TimelineRender: region cache lookup failed for {Hash}; rendering instead.", hash);
                }

                if (!hit)
                {
                    RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
                        region, target, assets, regionPath, env.FontFilePath,
                        n => Path.Combine(env.ScratchDirectory, n < 0 ? $"region-{i:D4}-graph.txt" : $"region-{i:D4}-text-{n}.txt"), env.Threads,
                        lutFilePathFor: n => Path.Combine(env.ScratchDirectory, $"region-{i:D4}-lut-{n}.cube"),
                        stabilizePathFor: n => Path.Combine(env.ScratchDirectory, $"region-{i:D4}-stab-{n}.trf"),
                        hdrToneMaps: hdrToneMaps,
                        segmentationMasks: env.SegmentationMasks,
                        cameraProfilePathFor: n => Path.Combine(env.ScratchDirectory, $"region-{i:D4}-profile-{n}.cube"),
                        cameraProfileZscale: cameraProfileZscale,
                        luts: luts);
                    foreach (RegionFilterGraphBuilder.TextFile text in argv.TextFiles)
                        await File.WriteAllTextAsync(text.Path, text.Contents, ct).ConfigureAwait(false);
                    if (argv.GraphFile is { } graphFile)
                        await File.WriteAllTextAsync(graphFile.Path, graphFile.Contents, ct).ConfigureAwait(false);

                    // T-011: each distinct camera profile of the region is generated here as its own
                    // cube — ffmpeg never opens a user file.
                    foreach (RegionFilterGraphBuilder.CameraProfileRequest request in argv.CameraProfileFiles ?? [])
                        await File.WriteAllTextAsync(request.Path, CameraProfileCube.Write(request.ProfileId), ct).ConfigureAwait(false);

                    // P7: every LUT the region reads is parsed, clamped, blended to its intensity and
                    // REWRITTEN here — ffmpeg never opens the user's file.
                    foreach (GradeFilterBuilder.LutRequest request in argv.LutFiles ?? [])
                    {
                        if (!assets.ContainsKey(request.Lut.File))
                            throw new InvalidOperationException($"LUT {request.Lut.File.Kind}:{request.Lut.File.ProjectFileId ?? request.Lut.File.StepResultId} was not staged.");
                        CubeLut atIntensity = luts[request.Lut.File].WithIntensity(request.Lut.Intensity);
                        if (request.CubePath is not null)
                        {
                            await File.WriteAllTextAsync(request.Path, atIntensity.ToShaperCubeText(), ct).ConfigureAwait(false);
                            await File.WriteAllTextAsync(request.CubePath, atIntensity.ToCubeText(), ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await File.WriteAllTextAsync(request.Path, atIntensity.ToCubeText(), ct).ConfigureAwait(false);
                        }
                    }

                    // P7: stabilisation detection passes write their transforms before the region reads them.
                    foreach (RegionFilterGraphBuilder.PrePass prePass in argv.PrePasses ?? [])
                    {
                        await RunAsync(env, prePass.Args, $"region {i} stabilisation pass", ct).ConfigureAwait(false);
                        invocations.Add(prePass.Args);
                    }

                    await RunAsync(env, argv.Args, $"region {i} ({region.StartFrame}-{region.EndFrame})", ct).ConfigureAwait(false);
                    invocations.Add(argv.Args);

                    try
                    {
                        await env.Cache.StoreAsync(env.ProjectId, hash, regionPath, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "TimelineRender: region cache store failed for {Hash}; continuing without caching it.", hash);
                    }
                }

                rendered.Add(new RenderedRegion(region.StartFrame, region.EndFrame, hash, hit, region.Layers.Count));
            }

            // ---- Concat (stream copy) ----
            await Report(env, "Joining regions", 68).ConfigureAwait(false);
            string listPath = Path.Combine(env.ScratchDirectory, "regions.txt");
            await File.WriteAllLinesAsync(listPath, regionPaths.Select(p => $"file '{p.Replace("'", @"'\''")}'"), ct).ConfigureAwait(false);
            programVideoPath = Path.Combine(env.ScratchDirectory, "program-video.mp4");
            List<string> concat = ["-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", "-movflags", "+faststart", programVideoPath];
            await RunAsync(env, concat, "region concat", ct).ConfigureAwait(false);
            invocations.Add(concat);
        }

        // ---- Audio: one mix pass ----
        await Report(env, "Mixing audio", 74).ConfigureAwait(false);
        string mixPath = Path.Combine(env.ScratchDirectory, AudioMixBuilder.MixFileName);
        IReadOnlyList<string> mixArgs = AudioMixBuilder.BuildMixArgs(plan, target, assets, mixPath, env.Threads, document.Tracks, document.Buses);
        await RunAsync(env, mixArgs, "audio mix", ct).ConfigureAwait(false);
        invocations.Add(mixArgs);

        // ---- Loudness: measure always, normalise when asked ----
        LoudnessMeasurement? measured = null;
        bool normalised = false;
        TimelineRenderLoudness measureTargets = config.Loudness ?? new TimelineRenderLoudness();
        IReadOnlyList<string> measureArgs = AudioMixBuilder.BuildMeasureArgs(mixPath, measureTargets);
        VideoToolResult measureResult = await env.Runner.RunFfmpegAsync(measureArgs, env.Timeout, ct).ConfigureAwait(false);
        invocations.Add(measureArgs);
        if (measureResult.Succeeded)
        {
            measured = AudioMixBuilder.ParseLoudnorm(measureResult.StdErr);
        }
        else
        {
            _logger.LogWarning("TimelineRender: loudness measurement failed (exit {Exit}); the render continues unmeasured.", measureResult.ExitCode);
        }

        // Silence pass: a separate invocation so the measure/normalise args stay byte-identical. Its failure only means no silence facts.
        IReadOnlyList<(double Start, double End)> silences = [];
        double programDurationSec = plan.DurationFrames / target.Fps;
        try
        {
            IReadOnlyList<string> silenceArgs = AudioMixBuilder.BuildSilenceArgs(mixPath);
            VideoToolResult silenceResult = await env.Runner.RunFfmpegAsync(silenceArgs, env.Timeout, ct).ConfigureAwait(false);
            invocations.Add(silenceArgs);
            if (silenceResult.Succeeded) silences = AudioMixBuilder.ParseSilences(silenceResult.StdErr, programDurationSec);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TimelineRender: silence pass failed; the render continues without silence facts.");
        }

        string finalAudioPath = mixPath;
        if (config.Loudness is { } loudness && measured is not null)
        {
            await Report(env, "Normalising loudness", 80).ConfigureAwait(false);
            string normalisedPath = Path.Combine(env.ScratchDirectory, "program-mix-normalised.wav");
            IReadOnlyList<string> normArgs = AudioMixBuilder.BuildNormalizeArgs(mixPath, normalisedPath, loudness, measured, target.SampleRate);
            await RunAsync(env, normArgs, "loudnorm (pass 2)", ct).ConfigureAwait(false);
            invocations.Add(normArgs);
            finalAudioPath = normalisedPath;
            normalised = true;
        }

        // ---- Final encode / mux ----
        await Report(env, "Encoding delivery", 86).ConfigureAwait(false);
        List<string> final = ["-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", env.Threads.ToString(CultureInfo.InvariantCulture)];
        if (programVideoPath is not null)
        {
            final.AddRange(["-i", programVideoPath, "-i", finalAudioPath, "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", target.Encoder, "-preset", target.EncoderPreset]);
            final.AddRange(RateControlArgs(target));
            final.AddRange(["-pix_fmt", "yuv420p", "-r", target.RateArg]);
            if (target.Encoder == "libx265")
                final.AddRange(["-tag:v", "hvc1"]);
        }
        else
        {
            final.AddRange(["-i", finalAudioPath, "-vn"]);
        }

        // The program length is known from the validated timeline: it bounds both streams, so the
        // shorter stream never decides how much of the other is delivered (no -shortest).
        double programSec = plan.DurationFrames / target.Fps;
        final.AddRange(["-c:a", "aac", "-b:a", $"{target.AudioBitrateKbps}k", "-ar", target.SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2",
            "-t", programSec.ToString("R", CultureInfo.InvariantCulture), "-movflags", "+faststart", outputPath]);
        await RunAsync(env, final, "final encode", ct).ConfigureAwait(false);
        invocations.Add(final);

        await Report(env, "Render complete", 95).ConfigureAwait(false);
        IReadOnlyList<QualityFinding> loudnessChecks = [];
        if (measured is not null)
        {
            TimelineRenderLoudness t = config.Loudness ?? new TimelineRenderLoudness();
            loudnessChecks = LoudnessAssessor.Assess(
                new LoudnessFacts(measured.InputIntegratedLufs, measured.InputTruePeakDbtp, measured.InputLoudnessRange, silences,
                    programDurationSec, plan.AudioClips.Count > 0, normalised),
                new LoudnessTarget(t.TargetLufs, t.TruePeakDbtp, t.LoudnessRange, 2));
        }

        return new TimelineRenderResult(outputPath, target, plan, rendered, measured, normalised, invocations, toneMapped, warnings, loudnessChecks);
    }

    /// <summary>
    /// The delivery rate-control arguments. Quality is exactly the historical <c>-crf n</c>; Capped adds
    /// a maximum to the CRF encode; Average is a single-pass target with no CRF. Numbers only, from
    /// validated integers.
    /// </summary>
    internal static IReadOnlyList<string> RateControlArgs(ResolvedRenderTarget target)
    {
        string crf = target.Crf.ToString(CultureInfo.InvariantCulture);
        if (target.VideoBitrateKbps is not { } kbps)
            return ["-crf", crf];

        long buf = (long)kbps * 2;
        string k(long v) => v.ToString(CultureInfo.InvariantCulture) + "k";
        return target.RateControl switch
        {
            TimelineRenderRateControl.Capped => ["-crf", crf, "-maxrate", k(kbps), "-bufsize", k(buf)],
            TimelineRenderRateControl.Average => ["-b:v", k(kbps), "-maxrate", k((long)kbps * 3 / 2), "-bufsize", k(buf)],
            _ => ["-crf", crf]
        };
    }

    /// <summary>Every asset the plan touches must have been resolved by the caller — refused up front, never a mid-render NRE.</summary>
    private static void RequireAssets(RenderPlan plan, IReadOnlyDictionary<AssetRef, LocalAsset> assets)
    {
        IEnumerable<AssetRef> refs = plan.Regions.SelectMany(r => r.Layers).Select(l => l.Item).OfType<MediaClip>().Select(c => c.AssetRef)
            .Concat(plan.Regions.SelectMany(r => r.Layers).Select(l => l.Item).OfType<InsertClip>().Where(i => i.AssetRef is not null).Select(i => i.AssetRef!))
            .Concat(plan.Regions.SelectMany(r => r.Layers).Where(l => l.Transition is not null).SelectMany(l => new[] { l.Transition!.From.AssetRef, l.Transition!.To.AssetRef }))
            .Concat(plan.AudioClips.Select(a => a.Clip.AssetRef));
        foreach (AssetRef reference in refs.Distinct())
        {
            if (!assets.ContainsKey(reference))
                throw new InvalidOperationException($"Asset {reference.Kind} {reference.ProjectFileId ?? reference.StepResultId} was not resolved before rendering.");
        }
    }

    private async Task RunAsync(TimelineRenderEnvironment env, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        VideoToolResult result = await env.Runner.RunFfmpegAsync(args, env.Timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            string stderr = result.StdErr.Length > 4000 ? result.StdErr[^4000..] : result.StdErr;
            throw new TimelineRenderException(
                $"ffmpeg failed during {what} (exit {result.ExitCode}, timedOut={result.TimedOut}): {stderr.Trim()}");
        }
    }

    private static Task Report(TimelineRenderEnvironment env, string stage, int percent) =>
        env.Progress is null ? Task.CompletedTask : env.Progress(stage, percent);

    /// <summary>Size + mtime of the font file: a font swap changes glyphs, so it changes the recipe.</summary>
    private static string FontFingerprint(string fontFilePath)
    {
        try
        {
            FileInfo info = new(fontFilePath);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Screens a timeline document's generated title and caption text once, before the region pass.
    /// A Block throws <see cref="TimelineContentBlockedException"/>; an unavailable call logs and
    /// proceeds (generated text fails open). No gate or no attribution means no call.
    /// </summary>
    private async Task ScreenGeneratedTextAsync(
        EditTimeline document, TimelineRenderEnvironment env, CancellationToken ct)
    {
        if (_moderation is null || env.Moderation is not { } attribution) return;

        List<string> texts = [];
        foreach (Track track in document.Tracks)
        {
            foreach (Item item in track.Items)
            {
                switch (item)
                {
                    case TitleClip title when !string.IsNullOrWhiteSpace(title.Text):
                        texts.Add(title.Text);
                        break;
                    case Caption caption when !string.IsNullOrWhiteSpace(caption.Text):
                        texts.Add(caption.Text);
                        break;
                }
            }
        }

        if (texts.Count == 0) return;

        // The whole document's text is rendered, so every bounded chunk is classified. More chunks
        // than the bound refuses the render rather than leaving a tail unchecked.
        if (!ModerationTextChunker.TryChunk(string.Join('\n', texts), _moderationMaxChars, out IReadOnlyList<string> chunks))
            throw new TimelineContentBlockedException(
                "The timeline text is too long to check without leaving part of it unclassified.");

        foreach (string chunk in chunks)
        {
            ModerationDecision decision = await _moderation.ModerateAsync(
                new ModerationInput(chunk, [], ModerationContentKind.Text), attribution, ct);

            if (decision.Action == ModerationAction.Block)
                throw new TimelineContentBlockedException(decision.UserMessage);

            if (!decision.Available)
            {
                _logger.LogWarning(
                    "Content moderation was unavailable for timeline-render text; proceeding because generated text fails open");
            }
        }
    }
}

/// <summary>A render step failed in ffmpeg. The message carries the tail of stderr.</summary>
public sealed class TimelineRenderException(string message) : Exception(message);

/// <summary>
/// The timeline's generated title/caption text was blocked by content moderation. The message is the
/// plain user reason; the step executor records it as <c>CONTENT_BLOCKED</c> and no ffmpeg runs.
/// </summary>
public sealed class TimelineContentBlockedException(string message) : Exception(message);

/// <summary>
/// The document asks for more than one render can carry (e.g. too many keyframed gains in one
/// mix). Thrown before any ffmpeg call; the message is a plain sentence for the user.
/// </summary>
public sealed class TimelineRenderLimitException(string message) : Exception(message);
