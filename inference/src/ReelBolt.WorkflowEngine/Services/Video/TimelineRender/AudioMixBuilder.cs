using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>Measured program loudness, as <c>loudnorm</c> reports it.</summary>
public sealed record LoudnessMeasurement(
    double InputIntegratedLufs,
    double InputTruePeakDbtp,
    double InputLoudnessRange,
    double InputThreshold,
    double TargetOffset);

/// <summary>
/// Builds the single sample-accurate audio mix pass and the optional two-pass <c>loudnorm</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every placed clip is trimmed from its own source at the source position the plan computed,
/// resampled to the target rate, retimed with <c>atempo</c> when its playback rate is not 1,
/// reversed when the clip is (P5), gained by its (possibly keyframed) <c>gainDb</c> times its fade
/// ramps — the keyframe curve goes through <see cref="KeyframeExpressionBuilder"/> as a balanced
/// <c>if()</c> tree — delayed to its program position with <c>adelay</c>, and the clips are summed
/// with <c>amix normalize=0</c> so levels are exactly what the document says rather than
/// auto-attenuated. Every clip is padded to the program length BEFORE the sum: ffmpeg 8.0.1's
/// <c>amix</c> ends at the last sound and an <c>apad</c> after it only sometimes recovers, so the
/// mix's length must never depend on <c>amix</c>'s end. The result is then trimmed to the exact
/// program duration. No clips at all
/// means digital silence of the program length, never a missing audio stream.
/// </para>
/// <para>
/// <b>Track processing (P5).</b> When ANY track carries a fader level, a pan or an EQ, the clips are
/// first summed per track, each track bus then takes <c>volume</c> (static or automated),
/// <c>pan</c> (constant-power balance), <c>highpass</c>/<c>lowpass</c>/<c>equalizer</c>, and the
/// tracks are summed. When no track does, the graph is the flat P4 one, byte for byte — so a
/// document that never touched a fader mixes exactly as before.
/// </para>
/// </remarks>
public static class AudioMixBuilder
{
    public const string MixFileName = "program-mix.wav";

    private static readonly Regex LoudnormJson = new(@"\{[^{}]*""input_i""[^{}]*\}", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Builds the mix argv. <paramref name="outputPath"/> receives 16-bit PCM WAV.</summary>
    public static IReadOnlyList<string> BuildMixArgs(
        RenderPlan plan,
        ResolvedRenderTarget target,
        IReadOnlyDictionary<AssetRef, LocalAsset> assets,
        string outputPath,
        int threads,
        IReadOnlyList<Track>? tracks = null,
        IReadOnlyList<AudioBus>? buses = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assets);

        double fps = target.Fps;
        double programSec = plan.DurationFrames / fps;
        List<string> args = ["-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", Num(threads)];
        List<string> graph = [];

        MixRouting routing = Route(plan, assets, tracks, buses);
        List<AudioClipPlan> clips = routing.Clips;
        Dictionary<int, Track> processedTracks = routing.ProcessedTracks;
        Dictionary<Guid, AudioBus> busById = routing.BusById;
        Dictionary<int, Guid> routedTracks = routing.RoutedTracks;
        bool useBuses = routing.UseBuses;
        EnsureWithinKeyframedGainCeiling(KeyframedGainCount(routing, tracks));

        for (int i = 0; i < clips.Count; i++)
        {
            AudioClipPlan clip = clips[i];
            LocalAsset asset = assets[clip.Clip.AssetRef];
            double rate = SegmentPlanner.EffectiveRate(clip.Clip);
            double spanFrames = clip.EndFrame - clip.StartFrame;
            double sourceStartSec = Math.Max(0, clip.SourceInFrames) / fps;
            double sourceSpanSec = spanFrames * rate / fps;
            long delayMs = (long)Math.Round((clip.StartFrame - plan.RangeStartFrame) * 1000.0 / fps);

            args.AddRange([.. UserMediaInputFormats.InputOptions(), "-ss", Num(sourceStartSec), "-t", Num(sourceSpanSec + 0.5), "-i", asset.LocalPath]);

            string reverse = clip.Clip.Reverse ? "areverse," : string.Empty;
            string retime = Atempo(rate);
            string gain = GainFilter(clip, fps);
            string channels = ChannelMapFilter(clip.Clip.ChannelMap);
            graph.Add(
                $"[{i}:a]atrim=duration={Num(sourceSpanSec)},asetpts=PTS-STARTPTS,{reverse}aresample={target.SampleRate}," +
                $"aformat=channel_layouts=stereo,{channels}{retime}{gain}adelay={delayMs}|{delayMs},apad=whole_dur={Num(programSec)}[a{i}]");
        }

        if (clips.Count == 0)
        {
            graph.Add($"anullsrc=r={target.SampleRate}:cl=stereo,atrim=duration={Num(programSec)}[aout]");
        }
        else if (!useBuses)
        {
            string inputs = string.Concat(Enumerable.Range(0, clips.Count).Select(i => $"[a{i}]"));
            string mix = clips.Count == 1 ? $"{inputs}anull" : $"{inputs}amix=inputs={clips.Count}:normalize=0:duration=longest";
            graph.Add($"{mix},apad=whole_dur={Num(programSec)},atrim=duration={Num(programSec)},asetpts=PTS-STARTPTS[aout]");
        }
        else
        {
            // One bus per track (in document order), each processed, then summed.
            List<string> busLabels = [];
            Dictionary<Guid, List<string>> routedLabels = [];
            foreach (int trackIndex in clips.Select(c => c.TrackIndex).Distinct().OrderBy(i => i))
            {
                int[] members = clips.Select((c, i) => (c, i)).Where(p => p.c.TrackIndex == trackIndex).Select(p => p.i).ToArray();
                string inputs = string.Concat(members.Select(i => $"[a{i}]"));
                string sum = members.Length == 1 ? $"{inputs}anull" : $"{inputs}amix=inputs={members.Length}:normalize=0:duration=longest";
                string processing = processedTracks.TryGetValue(trackIndex, out Track? track) ? TrackChain(track, plan.RangeStartFrame, fps) : string.Empty;
                string label = $"[bus{trackIndex}]";
                graph.Add($"{sum},{processing}apad=whole_dur={Num(programSec)},atrim=duration={Num(programSec)}{label}");
                if (routedTracks.TryGetValue(trackIndex, out Guid busId))
                {
                    if (!routedLabels.TryGetValue(busId, out List<string>? list)) routedLabels[busId] = list = [];
                    list.Add(label);
                }
                else
                {
                    busLabels.Add(label);
                }
            }

            if (routedLabels.Count > 0)
            {
                // P8: build every bus that receives something, children before the bus they feed,
                // then hand the root buses to the master sum beside the unrouted tracks.
                HashSet<Guid> emitted = [];
                int mixIndex = 0;
                foreach (AudioBus bus in busById.Values)
                {
                    if (!IsRootBus(bus, busById)) continue;
                    string? label = EmitBus(bus, busById, routedLabels, emitted, [], graph, plan.RangeStartFrame, fps, programSec, ref mixIndex);
                    if (label is not null) busLabels.Add(label);
                }
            }

            string busInputs = string.Concat(busLabels);
            string busMix = busLabels.Count == 1 ? $"{busInputs}anull" : $"{busInputs}amix=inputs={busLabels.Count}:normalize=0:duration=longest";
            graph.Add($"{busMix},atrim=duration={Num(programSec)},asetpts=PTS-STARTPTS[aout]");
        }

        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[aout]", "-c:a", "pcm_s16le", "-ar", Num(target.SampleRate), "-ac", "2", outputPath]);
        return args;
    }

    /// <summary>
    /// <c>atempo</c> for a playback rate, as a chain of stages each within [0.5, 2] (the range every
    /// ffmpeg build accepts), so the sound follows the picture over the whole
    /// <see cref="SegmentPlanner.EffectiveRate"/> range: 4 is <c>atempo=2,atempo=2</c>, 0.25 is
    /// <c>atempo=0.5,atempo=0.5</c>. Empty at (or within 1e-9 of) 1; ends with a comma otherwise.
    /// </summary>
    public static string Atempo(double rate)
    {
        if (rate == 1.0) return string.Empty;
        List<string> stages = [];
        double rest = rate;
        while (rest > 2.0)
        {
            stages.Add("atempo=2");
            rest /= 2.0;
        }

        while (rest < 0.5)
        {
            stages.Add("atempo=0.5");
            rest /= 0.5;
        }

        if (Math.Abs(rest - 1.0) > 1e-9) stages.Add($"atempo={Num(Math.Round(rest, 9))}");
        // A rate within 1e-9 of 1 leaves no stage: emit nothing, never a lone ',' (',,' in the graph).
        return stages.Count == 0 ? string.Empty : string.Join(',', stages) + ",";
    }

    /// <summary>Which clips the mix places and how they are routed (shared by the mix and its preflight).</summary>
    private sealed record MixRouting(
        List<AudioClipPlan> Clips,
        Dictionary<int, Track> ProcessedTracks,
        Dictionary<Guid, AudioBus> BusById,
        Dictionary<int, Guid> RoutedTracks)
    {
        public bool UseBuses => ProcessedTracks.Count > 0 || RoutedTracks.Count > 0;
    }

    private static MixRouting Route(
        RenderPlan plan, IReadOnlyDictionary<AssetRef, LocalAsset> assets, IReadOnlyList<Track>? tracks, IReadOnlyList<AudioBus>? buses)
    {
        List<AudioClipPlan> clips = plan.AudioClips
            .Where(c => assets.TryGetValue(c.Clip.AssetRef, out LocalAsset? a) && a.Probe.AudioCodec is not null)
            .ToList();

        // Track buses are only built when some track asks for processing; otherwise the P4 graph.
        Dictionary<int, Track> processedTracks = [];
        if (tracks is not null)
        {
            foreach (int trackIndex in clips.Select(c => c.TrackIndex).Distinct())
            {
                if (trackIndex >= 0 && trackIndex < tracks.Count && HasProcessing(tracks[trackIndex]))
                    processedTracks[trackIndex] = tracks[trackIndex];
            }
        }

        // P8: submix buses. A track whose busId names a bus of the document is routed through it;
        // a bus with an outputBusId feeds that bus, the rest feed the master. Only buses that
        // actually receive something are built, so an unused bus list changes nothing.
        Dictionary<Guid, AudioBus> busById = [];
        foreach (AudioBus? bus in buses ?? [])
        {
            if (bus is not null) busById.TryAdd(bus.Id, bus);
        }

        Dictionary<int, Guid> routedTracks = [];
        if (tracks is not null && busById.Count > 0)
        {
            foreach (int trackIndex in clips.Select(c => c.TrackIndex).Distinct())
            {
                if (trackIndex >= 0 && trackIndex < tracks.Count && tracks[trackIndex].BusId is { } busId && busById.ContainsKey(busId))
                    routedTracks[trackIndex] = busId;
            }
        }

        return new MixRouting(clips, processedTracks, busById, routedTracks);
    }

    private static int KeyframedGainCount(MixRouting routing, IReadOnlyList<Track>? tracks) =>
        routing.Clips.Count(c => c.Clip.GainDb?.Keys is { Count: > 1 })
        + (routing.UseBuses ? routing.Clips.Select(c => c.TrackIndex).Distinct().Count(i => tracks![i].GainDb?.Keys is { Count: > 1 }) : 0)
        + (routing.RoutedTracks.Count > 0 ? routing.BusById.Values.Count(b => b.GainDb?.Keys is { Count: > 1 }) : 0);

    /// <summary>
    /// Preflight for the mix (RD-8): refuses, before any region is rendered, a plan whose mix would
    /// carry more keyframed gain curves than <see cref="KeyframeExpressionBuilder.MaxKeyframedParametersPerGraph"/>
    /// (beyond it ffmpeg fails at execve with an empty stderr). Same count the mix itself makes.
    /// </summary>
    /// <exception cref="TimelineRenderLimitException">With a plain sentence for the user.</exception>
    public static void EnsureWithinKeyframedGainCeiling(
        RenderPlan plan, IReadOnlyDictionary<AssetRef, LocalAsset> assets, IReadOnlyList<Track>? tracks, IReadOnlyList<AudioBus>? buses)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assets);
        EnsureWithinKeyframedGainCeiling(KeyframedGainCount(Route(plan, assets, tracks, buses), tracks));
    }

    private static void EnsureWithinKeyframedGainCeiling(int keyframedCount)
    {
        int max = KeyframeExpressionBuilder.MaxKeyframedParametersPerGraph;
        if (keyframedCount <= max) return;
        throw new TimelineRenderLimitException(
            $"This render automates the volume of {keyframedCount} clips, tracks or buses; a render can automate at most {max}. " +
            "Remove the volume keyframes from some of them, or render a shorter range.");
    }

    /// <summary>True when the track asks for fader, pan, EQ (P5) or dynamics/denoise/leveler (P8) processing.</summary>
    public static bool HasProcessing(Track track)
    {
        if (track.Pan != 0) return true;
        if (track.Eq is { } eq && (eq.LowCutHz is not null || eq.HighCutHz is not null || eq.Bands is { Count: > 0 })) return true;
        if (track.GainDb is { } gain && (gain.Value != 0 || gain.Keys is { Count: > 0 })) return true;
        if (HasChannelProcessing(track.Dynamics, track.Denoise, track.Leveler)) return true;
        return false;
    }

    /// <summary>P8: whether any dynamics stage, the denoiser or the leveler is present.</summary>
    public static bool HasChannelProcessing(AudioDynamics? dynamics, AudioDenoise? denoise, AudioLeveler? leveler) =>
        denoise is not null || leveler is not null ||
        (dynamics is not null && (dynamics.Compressor is not null || dynamics.Limiter is not null || dynamics.Gate is not null || dynamics.Deesser is not null));

    /// <summary>
    /// The track bus chain (P5): automated/static <c>volume</c>, constant-power <c>pan</c>, low cut,
    /// high cut and the parametric bands — every filter present only when the track asks for it —
    /// followed (P8) by the processing chain. Ends with a trailing comma so the caller can append the
    /// padding filters.
    /// </summary>
    public static string TrackChain(Track track, long rangeStartFrame, double fps) =>
        ChannelChain(track.GainDb, track.Pan, track.Eq, track.Dynamics, track.Denoise, track.Leveler, rangeStartFrame, fps);

    /// <summary>P8: the chain of a track or a bus — one definition for both.</summary>
    public static string ChannelChain(Keyframed<double>? gainDb, double pan, TrackEq? eq, AudioDynamics? dynamics, AudioDenoise? denoise, AudioLeveler? leveler, long rangeStartFrame, double fps)
    {
        List<string> parts = [];

        if (gainDb is { } gain)
        {
            if (gain.Keys is { Count: > 0 } keys)
            {
                // The bus runs on the program timeline (every clip is already adelay'ed), so t = 0 is
                // the range start and the automation keys (timeline frames) are relative to it.
                string db = KeyframeExpressionBuilder.Build(keys, gain.Value, rangeStartFrame, fps);
                parts.Add($"volume='{KeyframeExpressionBuilder.DbToLinear(db)}':eval=frame");
            }
            else if (gain.Value != 0)
            {
                parts.Add($"volume={Num(Math.Clamp(gain.Value, -60.0, 20.0))}dB");
            }
        }

        if (pan != 0)
        {
            double p = Math.Clamp(pan, -1, 1);
            double angle = (p + 1) * Math.PI / 4; // 0 = hard left, π/4 = centre, π/2 = hard right
            double left = Math.Cos(angle) * Math.Sqrt(2);
            double right = Math.Sin(angle) * Math.Sqrt(2);
            parts.Add($"pan=stereo|c0={Num(Math.Round(left, 6))}*c0|c1={Num(Math.Round(right, 6))}*c1");
        }

        if (eq is not null)
        {
            if (eq.LowCutHz is { } low) parts.Add($"highpass=f={Num(low)}");
            if (eq.HighCutHz is { } high) parts.Add($"lowpass=f={Num(high)}");
            if (eq.Bands is { } bands)
            {
                foreach (EqBand band in bands.Take(EditTimelineValidator.MaxEqBands))
                {
                    if (band.GainDb == 0) continue;
                    parts.Add($"equalizer=f={Num(band.FrequencyHz)}:width_type=q:w={Num(band.Q)}:g={Num(band.GainDb)}");
                }
            }
        }

        parts.AddRange(ProcessingChain(dynamics, denoise, leveler));

        return parts.Count == 0 ? string.Empty : string.Join(',', parts) + ",";
    }

    /// <summary>
    /// P8: the processing filters in signal order — denoise, gate, de-esser, compressor, leveler,
    /// limiter — each present only when asked for. dB values become ffmpeg's linear amplitudes here;
    /// the document carries only dB.
    /// </summary>
    public static IReadOnlyList<string> ProcessingChain(AudioDynamics? dynamics, AudioDenoise? denoise, AudioLeveler? leveler)
    {
        List<string> parts = [];
        if (denoise is { } dn)
        {
            double strength = Math.Clamp(dn.Strength, 0, 1);
            parts.Add($"afftdn=nr={Num(Math.Round(6 + 30 * strength, 3))}:nf=-50");
        }

        if (dynamics?.Gate is { } gate)
        {
            parts.Add($"agate=threshold={Lin(gate.ThresholdDb)}:ratio={Num(Math.Clamp(gate.Ratio, 1, 20))}:attack={Num(Math.Clamp(gate.AttackMs, 1, 2000))}:release={Num(Math.Clamp(gate.ReleaseMs, 1, 2000))}");
        }

        if (dynamics?.Deesser is { } de)
        {
            parts.Add($"deesser=i={Num(Math.Round(Math.Clamp(de.Intensity, 0, 1), 4))}");
        }

        if (dynamics?.Compressor is { } comp)
        {
            parts.Add($"acompressor=threshold={Lin(comp.ThresholdDb)}:ratio={Num(Math.Clamp(comp.Ratio, 1, 20))}:attack={Num(Math.Clamp(comp.AttackMs, 1, 2000))}:release={Num(Math.Clamp(comp.ReleaseMs, 1, 2000))}:makeup={Lin(Math.Clamp(comp.MakeupDb, 0, 24))}");
        }

        if (leveler is { } lv)
        {
            parts.Add(lv.Kind == "dynamic"
                ? $"dynaudnorm=p={Lin(lv.TargetDb)}:f=500:g=31"
                : $"speechnorm=p={Lin(lv.TargetDb)}:e=3");
        }

        if (dynamics?.Limiter is { } lim)
        {
            parts.Add($"alimiter=limit={Lin(Math.Clamp(lim.CeilingDb, -24, 0))}:level=0");
        }

        return parts;
    }

    /// <summary>P8: the <c>pan</c> filter for a clip's channel map, or empty for <c>stereo</c>.</summary>
    public static string ChannelMapFilter(string? channelMap) => channelMap switch
    {
        "left" => "pan=stereo|c0=c0|c1=c0,",
        "right" => "pan=stereo|c0=c1|c1=c1,",
        "mono" => "pan=stereo|c0=0.5*c0+0.5*c1|c1=0.5*c0+0.5*c1,",
        "swap" => "pan=stereo|c0=c1|c1=c0,",
        _ => string.Empty
    };

    private static string Lin(double db) => Num(Math.Round(Math.Pow(10, Math.Clamp(db, -90, 24) / 20), 6));

    /// <summary>A bus that feeds the master: no output, an output that does not exist, or one that would loop.</summary>
    private static bool IsRootBus(AudioBus bus, Dictionary<Guid, AudioBus> buses)
    {
        if (bus.OutputBusId is not { } output || output == bus.Id || !buses.ContainsKey(output)) return true;
        // Walk the chain; a loop back to this bus makes it a root (the validator rejects loops, this only keeps the graph finite).
        HashSet<Guid> seen = [bus.Id];
        Guid? next = output;
        while (next is { } id && buses.TryGetValue(id, out AudioBus? target))
        {
            if (!seen.Add(id)) return true;
            next = target.OutputBusId;
        }

        return false;
    }

    /// <summary>Emits the bus's graph line (its routed tracks and child buses summed, then its chain) and returns its label, or null when nothing feeds it.</summary>
    private static string? EmitBus(
        AudioBus bus,
        Dictionary<Guid, AudioBus> buses,
        Dictionary<Guid, List<string>> routedLabels,
        HashSet<Guid> emitted,
        HashSet<Guid> onPath,
        List<string> graph,
        long rangeStartFrame,
        double fps,
        double programSec,
        ref int mixIndex)
    {
        if (!emitted.Add(bus.Id) || !onPath.Add(bus.Id)) return null;
        List<string> inputs = [];
        if (routedLabels.TryGetValue(bus.Id, out List<string>? tracks)) inputs.AddRange(tracks);
        foreach (AudioBus child in buses.Values)
        {
            if (child.Id == bus.Id || child.OutputBusId != bus.Id) continue;
            string? label = EmitBus(child, buses, routedLabels, emitted, onPath, graph, rangeStartFrame, fps, programSec, ref mixIndex);
            if (label is not null) inputs.Add(label);
        }

        onPath.Remove(bus.Id);
        if (inputs.Count == 0) return null;

        string joined = string.Concat(inputs);
        string sum = inputs.Count == 1 ? $"{joined}anull" : $"{joined}amix=inputs={inputs.Count}:normalize=0:duration=longest";
        string processing = ChannelChain(bus.GainDb, bus.Pan, bus.Eq, bus.Dynamics, bus.Denoise, bus.Leveler, rangeStartFrame, fps);
        string outLabel = $"[mix{mixIndex++}]";
        graph.Add($"{sum},{processing}apad=whole_dur={Num(programSec)},atrim=duration={Num(programSec)}{outLabel}");
        return outLabel;
    }

    /// <summary>
    /// The <c>volume</c> filter for a clip's static or keyframed gain times its fade ramps (P5), or
    /// empty at unity with no fades.
    /// </summary>
    public static string GainFilter(AudioClipPlan clip, double fps)
    {
        Keyframed<double>? gain = clip.Clip.GainDb;
        string? fades = FadeExpression(clip, fps);

        if (gain is null && fades is null) return string.Empty;

        if (gain?.Keys is { Count: > 0 } keys)
        {
            // t = 0 is the clip's own first sample (after atrim/asetpts), which is timeline frame
            // clip.StartFrame; keyframes are authored in timeline frames.
            string db = KeyframeExpressionBuilder.Build(keys, gain.Value, clip.StartFrame, fps);
            string linear = KeyframeExpressionBuilder.DbToLinear(db);
            return fades is null ? $"volume='{linear}':eval=frame," : $"volume='{linear}*{fades}':eval=frame,";
        }

        double value = gain is null ? 0.0 : Math.Clamp(gain.Value, -60.0, 20.0);
        if (fades is null)
        {
            return value == 0.0 ? string.Empty : $"volume={Num(value)}dB,";
        }

        string linearStatic = value == 0.0 ? "1" : KeyframeExpressionBuilder.DbToLinear(Num(value));
        return $"volume='{linearStatic}*{fades}':eval=frame,";
    }

    /// <summary>The fade-in × fade-out ramps of a planned clip as a clip-local expression in <c>t</c>, or null when there are none.</summary>
    private static string? FadeExpression(AudioClipPlan clip, double fps)
    {
        List<string> factors = [];
        if (clip.FadeIn is { } fi && fi.EndFrame > fi.StartFrame)
        {
            double t0 = (fi.StartFrame - clip.StartFrame) / fps;
            double d = (fi.EndFrame - fi.StartFrame) / fps;
            factors.Add($"clip((t-({Num(t0)}))/{Num(d)},0,1)");
        }

        if (clip.FadeOut is { } fo && fo.EndFrame > fo.StartFrame)
        {
            double t1 = (fo.EndFrame - clip.StartFrame) / fps;
            double d = (fo.EndFrame - fo.StartFrame) / fps;
            factors.Add($"clip(({Num(t1)}-t)/{Num(d)},0,1)");
        }

        return factors.Count == 0 ? null : string.Join("*", factors);
    }

    /// <summary>First <c>loudnorm</c> pass: measure only (writes nothing).</summary>
    public static IReadOnlyList<string> BuildMeasureArgs(string mixPath, TimelineRenderLoudness targets) =>
    [
        "-y", "-nostdin", "-hide_banner", "-loglevel", "info", "-i", mixPath,
        "-af", $"loudnorm=I={Num(targets.TargetLufs)}:TP={Num(targets.TruePeakDbtp)}:LRA={Num(targets.LoudnessRange)}:print_format=json",
        "-f", "null", "-"
    ];

    /// <summary>Longest stretches of silence kept from the silence pass (bounded so a stuttering mix cannot flood the output).</summary>
    public const int MaxSilences = 50;

    /// <summary>A separate, audio-only pass that lists stretches of at least 2 s below -50 dB (never writes a file).</summary>
    public static IReadOnlyList<string> BuildSilenceArgs(string mixPath) =>
    [
        "-y", "-nostdin", "-hide_banner", "-loglevel", "info", "-vn", "-i", mixPath,
        "-af", "silencedetect=noise=-50dB:d=2",
        "-f", "null", "-"
    ];

    /// <summary>Parses <c>silence_start</c>/<c>silence_end</c> lines from stderr; an unclosed stretch runs to <paramref name="durationSec"/>.</summary>
    public static IReadOnlyList<(double Start, double End)> ParseSilences(string? stderr, double durationSec)
    {
        List<(double Start, double End)> list = [];
        if (string.IsNullOrEmpty(stderr)) return list;
        double? open = null;
        foreach (Match m in SilenceLine.Matches(stderr))
        {
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) continue;
            if (m.Groups[1].Value == "start")
            {
                open = Math.Max(0, v);
            }
            else if (open is { } s)
            {
                if (v > s) list.Add((s, v));
                open = null;
            }

            if (list.Count >= MaxSilences) return list;
        }

        if (open is { } os && double.IsFinite(durationSec) && durationSec > os) list.Add((os, durationSec));
        return list;
    }

    private static readonly Regex SilenceLine = new(@"silence_(start|end):\s*(-?[0-9.]+(?:e[-+]?[0-9]+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Second pass: apply, linear, with the measured values.</summary>
    public static IReadOnlyList<string> BuildNormalizeArgs(
        string mixPath, string outputPath, TimelineRenderLoudness targets, LoudnessMeasurement measured, int sampleRate) =>
    [
        "-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-i", mixPath,
        "-af",
        $"loudnorm=I={Num(targets.TargetLufs)}:TP={Num(targets.TruePeakDbtp)}:LRA={Num(targets.LoudnessRange)}:" +
        $"measured_I={Num(measured.InputIntegratedLufs)}:measured_TP={Num(measured.InputTruePeakDbtp)}:" +
        $"measured_LRA={Num(measured.InputLoudnessRange)}:measured_thresh={Num(measured.InputThreshold)}:" +
        $"offset={Num(measured.TargetOffset)}:linear=true:print_format=summary",
        "-ar", Num(sampleRate), "-c:a", "pcm_s16le", outputPath
    ];

    /// <summary>Parses the JSON block <c>loudnorm print_format=json</c> writes to stderr.</summary>
    public static LoudnessMeasurement? ParseLoudnorm(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;
        Match match = LoudnormJson.Match(stderr);
        if (!match.Success) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(match.Value);
            JsonElement root = doc.RootElement;
            return new LoudnessMeasurement(
                Read(root, "input_i"),
                Read(root, "input_tp"),
                Read(root, "input_lra"),
                Read(root, "input_thresh"),
                Read(root, "target_offset"));
        }
        catch (JsonException)
        {
            return null;
        }

        static double Read(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement element)) return 0;
            string? text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            if (text is null) return 0;
            if (text.Contains("inf", StringComparison.OrdinalIgnoreCase)) return -99.0;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }
    }

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
