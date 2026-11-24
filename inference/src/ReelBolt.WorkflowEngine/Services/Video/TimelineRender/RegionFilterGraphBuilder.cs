using System.Globalization;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// Builds the ONE ffmpeg invocation that renders a region to its mezzanine.
/// </summary>
/// <remarks>
/// <para>
/// Shape: one <c>-ss/-t/-i</c> per media leg (seek before the input for speed, then an exact
/// <c>trim</c>/<c>setpts</c> inside the graph for frame accuracy), a black <c>color</c> base at the
/// target size and rate, each layer fitted into the canvas (<c>scale</c> with
/// <c>force_original_aspect_ratio=decrease</c> + <c>pad</c>, i.e. the document's <c>fit</c> policy),
/// overlaid bottom-to-top, then titles and captions as <c>drawtext</c> reading a sanitised
/// <c>textfile=</c> with <c>expansion=none</c>, and the mezzanine encoder with an exact
/// <c>-frames:v</c> cap so every region file is precisely its planned length.
/// </para>
/// <para>
/// <b>P5 layers.</b> A media clip that uses a transform, a crop or a fade takes the
/// <see cref="TransformFilterBuilder"/> chain instead of the fit+pad chain and is overlaid at its
/// position; a reversed clip is read forwards and <c>reverse</c>d; a held clip is one frame
/// <c>loop</c>ed; a generator is a <c>color</c>/<c>smptehdbars</c>/<c>testsrc2</c> source; a
/// transition is two legs — the outgoing clip continued past its end and the incoming clip pulled
/// back, each padded with its last frame if the source runs out (<c>tpad</c>), blended by
/// <c>xfade</c> over the WHOLE transition span and then <c>trim</c>med to the region. A clip that
/// uses none of this renders through the byte-identical P4 chain.
/// </para>
/// <para>
/// <b>Trust boundary.</b> Nothing from the document is interpolated into the filter string except
/// numbers this builder formats itself (every one range-checked by <c>EditTimelineValidator</c>),
/// a colour that matched <c>#RRGGBB</c>, and a transition/generator word that matched the closed
/// catalogue; text goes into a file, the font is the host's configured file, and paths are the
/// renderer's own scratch paths. A playback rate is clamped to [0.1, 10].
/// </para>
/// <para>
/// <b>Graph length.</b> A keyframed channel expands to an expression; a region with many of them can
/// exceed what a single argv string may carry (the P0-M1 spike measured ffmpeg dying at execve with an
/// empty stderr past a few hundred KB). Above <see cref="MaxInlineGraphChars"/> the graph is therefore
/// written to a file and passed as <c>-filter_complex_script</c>; below it the argv is unchanged.
/// </para>
/// </remarks>
public static class RegionFilterGraphBuilder
{
    public const int MaxTitleChars = 200;
    public const int MaxCaptionChars = 300;
    public const double MinPlaybackRate = 0.1;
    public const double MaxPlaybackRate = 10.0;
    public const int MaxInlineGraphChars = 60_000;

    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}\\z", RegexOptions.Compiled);

    /// <summary>What the caller must write to disk before running the argv.</summary>
    public sealed record TextFile(string Path, string Contents);

    /// <summary>P7: a stabilisation detection pass the caller runs BEFORE the region's argv; it writes <paramref name="ResultPath"/>.</summary>
    public sealed record PrePass(IReadOnlyList<string> Args, string ResultPath);

    /// <summary>T-011: a camera-profile cube the caller must materialise at <paramref name="Path"/> before ffmpeg runs.</summary>
    public sealed record CameraProfileRequest(string ProfileId, string Path);

    /// <summary>The argv plus the files it reads. <paramref name="GraphFile"/> is set when the graph went to a script file. P7 adds the LUT files to materialise and the pre-passes to run first; T-011 adds the camera-profile cubes.</summary>
    public sealed record RegionArgv(
        IReadOnlyList<string> Args,
        IReadOnlyList<TextFile> TextFiles,
        TextFile? GraphFile = null,
        IReadOnlyList<GradeFilterBuilder.LutRequest>? LutFiles = null,
        IReadOnlyList<PrePass>? PrePasses = null,
        IReadOnlyList<CameraProfileRequest>? CameraProfileFiles = null);

    /// <summary>P7: true when the clip uses a picture feature beyond the P5 chain (grade, mask, keyer, effects, stabilise, reframe, optical-flow retime, corner pin).</summary>
    public static bool HasPictureDepth(MediaClip clip, GradeStack? groupGrade = null) =>
        GradeFilterBuilder.IsActive(clip.Grade) || GradeFilterBuilder.IsActive(groupGrade) || clip.Mask is not null || clip.Keyer is not null
        || (clip.Effects is { Count: > 0 } && clip.Effects.Any(e => e.Enabled && e.Amount > 0)) || clip.Stabilize || clip.Reframe is not null
        || (clip.RetimeQuality == "opticalFlow" && Rate(clip) < 1.0) || clip.CornerPin is not null
        || EnabledRegionEffects(clip).Count > 0;

    /// <summary>
    /// True when a tone-mapped clip is DOWNSCALED to the canvas and nothing in its chain needs source pixels
    /// (no crop/transform/fades/grade/mask/keyer/effects/stabilise/reframe/corner pin, no pixel-altering
    /// autorotate): the leg then scales to its fitted size FIRST and tone-maps the small frame (the float
    /// zscale chain costs several times the rest at 4K). Shared with <see cref="RegionRecipeHasher"/> so the
    /// recipe changes for exactly the affected regions.
    /// </summary>
    internal static bool ToneMapAfterScale(
        MediaClip clip, GradeStack? groupGrade, ResolvedRenderTarget target,
        IReadOnlyDictionary<AssetRef, LocalAsset> assets, IReadOnlyDictionary<AssetRef, string>? hdrToneMaps)
    {
        if (hdrToneMaps is null || !hdrToneMaps.ContainsKey(clip.AssetRef) || !assets.TryGetValue(clip.AssetRef, out LocalAsset? asset))
            return false;
        MediaProbeResult? probe = asset.Probe;
        if (probe is null || probe.AltersPixelsOnDecode || probe.Width <= 0 || probe.Height <= 0)
            return false;
        if (clip.Transform is not null || clip.Crop is not null || clip.FadeInFrames > 0 || clip.FadeOutFrames > 0 || HasPictureDepth(clip, groupGrade))
            return false;
        return (long)target.Width * probe.Height < (long)probe.Width * target.Height
            ? target.Width < probe.Width
            : target.Height < probe.Height;
    }

    /// <summary>The clip's enabled prompted region effects, in document order (the order they are applied in).</summary>
    internal static IReadOnlyList<RegionClipEffect> EnabledRegionEffects(MediaClip clip) =>
        clip.RegionEffects is { Count: > 0 } all ? all.Where(e => e is { Enabled: true }).ToList() : [];

    /// <summary>
    /// The ONE mapping of a region effect's wire word (<c>EditTimelineValidator.RegionEffectWords</c>) to the
    /// workflow compile's <see cref="RegionEffect"/> enum. An unknown word is a programming error (validated upstream).
    /// </summary>
    internal static RegionEffect ToRegionEffect(string word) => word switch
    {
        "blur" => RegionEffect.Blur,
        "pixelate" => RegionEffect.Pixelate,
        "highlight" => RegionEffect.Highlight,
        "spotlight" => RegionEffect.Spotlight,
        "colorpop" => RegionEffect.ColorPop,
        "outline" => RegionEffect.Outline,
        _ => throw new InvalidOperationException($"Unknown region effect '{word}'.")
    };

    /// <summary>
    /// Builds the region's argv.
    /// </summary>
    /// <param name="region">The region.</param>
    /// <param name="target">The resolved target (size/rate).</param>
    /// <param name="assets">Local assets by ref.</param>
    /// <param name="outputPath">Mezzanine path.</param>
    /// <param name="fontFilePath">The host's font file for drawtext.</param>
    /// <param name="textFilePathFor">Scratch path for the n-th text file of this region (the graph file, when needed, is index -1).</param>
    /// <param name="threads">ffmpeg thread count (fixed, so encodes are reproducible).</param>
    /// <param name="lutFilePathFor">P7: scratch path for the n-th LUT file of this region (defaults to <c>textFilePathFor(-2 - n)</c>).</param>
    /// <param name="hdrToneMaps">R10.7: assets (by ref) whose decode chain starts with the HDR to SDR tone-map; null for none.</param>
    /// <param name="stabilizePathFor">P7: scratch path for the n-th stabilisation transforms file (defaults to <c>textFilePathFor(-100 - n)</c>).</param>
    /// <param name="cameraProfilePathFor">T-011: scratch path for the n-th camera-profile cube of this region (defaults to <c>textFilePathFor(-3 - n)</c>).</param>
    /// <param name="luts">The parsed LUTs by asset (parsed once before the region loop); null renders every LUT as a 3D cube, as before 1D support.</param>
    /// <param name="cameraProfileZscale">T-011: false when this host's ffmpeg has no <c>zscale</c>; the swscale fallback prefix is used instead.</param>
    public static RegionArgv Build(
        RenderRegion region,
        ResolvedRenderTarget target,
        IReadOnlyDictionary<AssetRef, LocalAsset> assets,
        string outputPath,
        string fontFilePath,
        Func<int, string> textFilePathFor,
        int threads,
        Func<int, string>? lutFilePathFor = null,
        Func<int, string>? stabilizePathFor = null,
        IReadOnlyDictionary<AssetRef, string>? hdrToneMaps = null,
        IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? segmentationMasks = null,
        Func<int, string>? cameraProfilePathFor = null,
        bool cameraProfileZscale = true,
        IReadOnlyDictionary<AssetRef, CubeLut>? luts = null)
    {
        // R10.7: a tone-mapped source gets the SDR conversion as the FIRST filters of its leg, before
        // trim/crop/transform/grade. Null/empty (every SDR render) leaves every leg byte-identical.
        string ToneMap(MediaClip c) => hdrToneMaps is not null && hdrToneMaps.TryGetValue(c.AssetRef, out string? kind)
            ? HdrToneMap.Filter(kind) + ","
            : string.Empty;
        // T-011: the camera-profile stage is the FIRST filter of an SDR leg that names a profile, before
        // trim/crop/transform/grade. HDR wins: a clip that also tone-maps never gets it. One cube per
        // distinct profile in the region, reported so the renderer materialises it.
        cameraProfilePathFor ??= n => textFilePathFor(-3 - n);
        Dictionary<string, int> cameraProfileIndex = [];
        List<CameraProfileRequest> cameraProfileRequests = [];
        string Profile(MediaClip c)
        {
            if (string.IsNullOrEmpty(c.InputProfile) || HdrToneMap.KindOf(assets[c.AssetRef].Probe) is not null)
                return string.Empty;
            if (!cameraProfileIndex.TryGetValue(c.InputProfile, out int index))
            {
                index = cameraProfileRequests.Count;
                cameraProfileIndex[c.InputProfile] = index;
                cameraProfileRequests.Add(new CameraProfileRequest(c.InputProfile, cameraProfilePathFor(index)));
            }

            return CameraProfileStage.Filter(c.InputProfile, cameraProfileRequests[index].Path, cameraProfileZscale) + ",";
        }

        lutFilePathFor ??= n => textFilePathFor(-2 - n);
        stabilizePathFor ??= n => textFilePathFor(-100 - n);
        List<GradeFilterBuilder.LutRequest> lutRequests = [];
        List<PrePass> prePasses = [];
        int lutIndex = 0;
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        double fps = target.Fps;
        double durationSec = region.DurationFrames / fps;
        List<string> args = ["-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", Num(threads)];
        List<string> graph = [];
        List<TextFile> textFiles = [];
        TransformFilterBuilder.LayerContext context = new(target, region.StartFrame, region.EndFrame);

        // Inputs: one per media leg, in stacking order (a transition contributes two).
        int inputIndex = 0;
        List<(RenderLayer Layer, int InputA, int InputB)> pictureLayers = [];
        // T-015: the mask videos of segmentation-masked clips, by picture-layer ordinal and side
        // ('m' = the layer's clip, 'a'/'b' = a transition's legs). Empty for every unmasked timeline.
        Dictionary<string, List<int>> maskInputs = [];
        // Magic blur: the mask videos of each enabled regionEffects entry, same keys; one list per entry, in
        // document order. They are separate extra inputs appended AFTER the clip's own mask inputs, so the
        // input indexing is deterministic and a clip with both a `mask` and regionEffects cannot clash.
        Dictionary<string, List<List<int>>> effectMaskInputs = [];
        List<int> AddTrackInputs(MediaClip masked, ShapeMask m, long start, long stop, bool continuePastEnd, bool pullBack)
        {
            List<int> indexes = [];
            foreach (LocalSegmentationMask local in ResolveMasks(m, segmentationMasks))
            {
                args.AddRange(InputArgsAt(local.LocalPath, masked, start, stop, fps, continuePastEnd, pullBack));
                indexes.Add(inputIndex++);
            }

            return indexes;
        }

        void AddMaskInputs(MediaClip masked, string key, long start, long stop, bool continuePastEnd, bool pullBack)
        {
            if (masked.Mask is { } m && m.IsSegmentationMask())
                maskInputs[key] = AddTrackInputs(masked, m, start, stop, continuePastEnd, pullBack);

            IReadOnlyList<RegionClipEffect> effects = EnabledRegionEffects(masked);
            if (effects.Count == 0)
                return;
            List<List<int>> perEntry = [];
            foreach (RegionClipEffect effect in effects)
            {
                List<int> indexes = AddTrackInputs(masked, effect.Mask, start, stop, continuePastEnd, pullBack);
                if (indexes.Count == 0)
                    throw new InvalidOperationException("A region effect has no segmentation mask track to render through.");
                perEntry.Add(indexes);
            }

            effectMaskInputs[key] = perEntry;
        }
        int effectSlot = 0;

        foreach (RenderLayer layer in region.Layers)
        {
            switch (layer.Item)
            {
                case MediaClip clip:
                    args.AddRange(InputArgs(clip, assets, region.StartFrame, region.EndFrame, fps));
                    pictureLayers.Add((layer, inputIndex, -1));
                    inputIndex++;
                    AddMaskInputs(clip, $"{pictureLayers.Count - 1}m", region.StartFrame, region.EndFrame, false, false);
                    break;
                case Transition when layer.Transition is { } neighbours:
                    // Both legs cover the WHOLE transition span, whatever part of it this region is.
                    args.AddRange(InputArgs(neighbours.From, assets, layer.Item.RecordIn, layer.Item.RecordOut, fps, continuePastEnd: true));
                    args.AddRange(InputArgs(neighbours.To, assets, layer.Item.RecordIn, layer.Item.RecordOut, fps, pullBack: true));
                    pictureLayers.Add((layer, inputIndex, inputIndex + 1));
                    inputIndex += 2;
                    AddMaskInputs(neighbours.From, $"{pictureLayers.Count - 1}a", layer.Item.RecordIn, layer.Item.RecordOut, true, false);
                    AddMaskInputs(neighbours.To, $"{pictureLayers.Count - 1}b", layer.Item.RecordIn, layer.Item.RecordOut, false, true);
                    break;
                case InsertClip insert when InsertLayerBuilder.IsRenderable(insert):
                    args.AddRange(InsertLayerBuilder.InputArgs(insert, assets[insert.AssetRef!], region, fps));
                    pictureLayers.Add((layer, inputIndex, -1));
                    inputIndex++;
                    break;
                case GeneratorClip:
                case AdjustmentClip:
                case TitleClip:
                case Caption:
                    pictureLayers.Add((layer, -1, -1));
                    break;
                default:
                    break;
            }
        }

        graph.Add($"color=c=black:s={target.Width}x{target.Height}:r={target.RateArg}:d={Num(durationSec)}[base]");

        string current = "[base]";
        int layerNumber = 0;

        // A title or caption is drawn at its own track's place in the stack (as the viewer stacks it): over
        // every picture layer below it, under every picture layer above it.
        int textIndex = 0;
        void DrawText(RenderLayer layer)
        {
            string? text = null;
            int fontSize;
            string color;
            string placement;
            TitleClip? transformedTitle = null;
            switch (layer.Item)
            {
                case TitleClip title:
                    {
                        if (title.Transform is not null) transformedTitle = title;
                        text = OverlayTextSanitizer.Sanitize(title.Text, MaxTitleChars);
                        fontSize = ClampFontSize(title.FontSize, target.Height);
                        color = HexColor.IsMatch(title.Color ?? string.Empty) ? title.Color! : "#FFFFFF";
                        // P7: the template decides decoration and position; "plain"/null is the P4 bytes.
                        (string position, string decoration) = GradeFilterBuilder.TitleTemplate(title.Template, target.Height);
                        if (title.Position is not null && title.Template is not ("lowerThird" or "card"))
                            position = CaptionBurnIn.Placement(title.Position, target.Width, target.Height, multiline: false);
                        placement = $"{decoration}:{position}";
                        break;
                    }
                case Caption caption:
                    if (CaptionBurnIn.IsLegacy(caption.Look, caption.Position) && !HasLineBreak(caption.Text))
                    {
                        text = OverlayTextSanitizer.Sanitize(caption.Text, MaxCaptionChars);
                        fontSize = ClampFontSize(target.Height * 0.045, target.Height);
                        color = "#FFFFFF";
                        placement = "borderw=2:bordercolor=black@0.6:x=(w-text_w)/2:y=h-text_h-h/12";
                    }
                    else
                    {
                        // R09.6: a look, a 3x3 position and/or lines; sized from the short side, wrapped to the frame.
                        (fontSize, color, string decoration) = CaptionBurnIn.Look(caption.Look, target.Width, target.Height);
                        IReadOnlyList<string> lines = CaptionBurnIn.Wrap(
                            CaptionBurnIn.Lines(caption.Text, MaxCaptionChars), CaptionBurnIn.CharsPerLine(target.Width, fontSize));
                        text = string.Join('\n', lines);
                        placement = $"{decoration}:{CaptionBurnIn.Placement(caption.Position, target.Width, target.Height, lines.Count > 1)}";
                    }

                    break;
                default:
                    return;
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            string textPath = textFilePathFor(textIndex);
            textFiles.Add(new TextFile(textPath, text));
            string drawtext =
                $"drawtext=fontfile='{EscapeFilterPath(fontFilePath)}':textfile='{EscapeFilterPath(textPath)}':expansion=none:" +
                $"fontsize={Num(fontSize)}:fontcolor={color}:{placement}";
            if (transformedTitle?.Transform is { } titleTransform)
            {
                // A transformed title is drawn at its anchor on its own transparent canvas-size layer, scaled and
                // rotated there, then overlaid with its opacity and offset (the same channels a picture layer has).
                TransformFilterBuilder.LayerChain titleChain = TransformFilterBuilder.BuildTitle(
                    titleTransform, transformedTitle.RecordIn, transformedTitle.RecordOut,
                    new TransformFilterBuilder.LayerContext(target, region.StartFrame, region.EndFrame));
                graph.Add($"color=c=black@0:s={target.Width}x{target.Height}:r={target.RateArg}:d={Num(region.DurationFrames / fps)},format=rgba,{drawtext}[t{textIndex}s]");
                graph.Add($"[t{textIndex}s]{(titleChain.Chain.Length > 0 ? titleChain.Chain : "null")}[t{textIndex}l]");
                string titleOverlay = titleChain.OverlayOptions.Length == 0 ? "overlay=eof_action=pass" : $"overlay={titleChain.OverlayOptions}:eof_action=pass";
                graph.Add($"{current}[t{textIndex}l]{titleOverlay}[t{textIndex}]");
            }
            else
            {
                graph.Add($"{current}{drawtext}[t{textIndex}]");
            }

            current = $"[t{textIndex}]";
            textIndex++;
        }

        for (int pictureOrdinal = 0; pictureOrdinal < pictureLayers.Count; pictureOrdinal++)
        {
            (RenderLayer layer, int inputA, int inputB) = pictureLayers[pictureOrdinal];
            if (layer.Item is TitleClip or Caption)
            {
                DrawText(layer);
                continue;
            }
            string label = $"l{layerNumber}";
            string overlayOptions;
            switch (layer.Item)
            {
                case MediaClip clip:
                    {
                        TransformFilterBuilder.LayerChain chain = TransformFilterBuilder.Build(clip, context);
                        string preScale = ToneMapAfterScale(clip, layer.GroupGrade, target, assets, hdrToneMaps)
                            ? $"scale={target.Width}:{target.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,"
                            : string.Empty;
                        string leg = preScale + ToneMap(clip) + Profile(clip) + LegChain(clip, region.StartFrame, region.EndFrame, target);
                        string source = $"[{inputA}:v]";
                        if (effectMaskInputs.TryGetValue($"{pictureOrdinal}m", out List<List<int>>? layerEffects))
                        {
                            source = RegionEffectStages(graph, clip, assets, layerEffects, source, label, leg,
                                LegChain(clip, region.StartFrame, region.EndFrame, target, allowOpticalFlow: false), target, durationSec, ref effectSlot);
                            leg = "null";
                        }

                        if (maskInputs.TryGetValue($"{pictureOrdinal}m", out List<int>? layerMasks))
                        {
                            source = SegmentationMatte(graph, clip, source, layerMasks, label, leg, LegChain(clip, region.StartFrame, region.EndFrame, target, allowOpticalFlow: false));
                            leg = "null";
                        }

                        string body = ClipLayer(
                            clip, layer.GroupGrade, chain, leg, label, target, region.StartFrame,
                            lutFilePathFor, lutRequests, ref lutIndex, luts, stabilizePathFor, prePasses,
                            () => InputArgs(clip, assets, region.StartFrame, region.EndFrame, fps), threads, out overlayOptions);
                        graph.Add($"{source}{body}[{label}]");
                        break;
                    }
                case AdjustmentClip adjustment:
                    {
                        // P7: an adjustment grades what is already composited below it; no input, no overlay.
                        List<string> parts = [];
                        Add(parts, GradeFilterBuilder.Build(adjustment.Grade, lutFilePathFor, lutRequests, ref lutIndex, $"adj{layerNumber}q", region.StartFrame, fps, luts));
                        Add(parts, GradeFilterBuilder.Effects(adjustment.Effects));
                        if (parts.Count == 0) continue;
                        graph.Add($"{current}{string.Join(',', parts)},format=yuv420p[v{layerNumber}]");
                        current = $"[v{layerNumber}]";
                        layerNumber++;
                        continue;
                    }
                case InsertClip insert:
                    {
                        // The media stretched to the canvas, pinned into the (keyframed) quad on a transparent layer.
                        graph.Add($"[{inputA}:v]{InsertLayerBuilder.Chain(insert, region, target)}[{label}]");
                        overlayOptions = string.Empty;
                        break;
                    }
                case GeneratorClip generator:
                    {
                        TransformFilterBuilder.LayerChain chain = TransformFilterBuilder.BuildGenerator(context);
                        graph.Add($"{GeneratorSource(generator, target, durationSec)},{chain.Chain}[{label}]");
                        overlayOptions = string.Empty;
                        break;
                    }
                case Transition transition when layer.Transition is { } neighbours:
                    {
                        double spanSec = (transition.RecordOut - transition.RecordIn) / fps;
                        double skipSec = (region.StartFrame - transition.RecordIn) / fps;
                        string kind = EditTimelineValidator.TransitionTypes.Contains(transition.TransitionType ?? string.Empty) ? transition.TransitionType! : "fade";
                        string legA = ToneMap(neighbours.From) + Profile(neighbours.From) + LegChain(neighbours.From, transition.RecordIn, transition.RecordOut, target, padToEnd: true);
                        string legB = ToneMap(neighbours.To) + Profile(neighbours.To) + LegChain(neighbours.To, transition.RecordIn, transition.RecordOut, target, padToEnd: true);

                        // The outgoing clip keeps its own fade-in and the incoming its fade-out, each only
                        // when it reaches into the span; the side the transition blends drops its fade
                        // (as the audio crossfade does).
                        MediaClip from = neighbours.From, to = neighbours.To;
                        long fromFadeIn = from.FadeInFrames > 0 && from.RecordIn + from.FadeInFrames > transition.RecordIn ? from.FadeInFrames : 0;
                        long toFadeOut = to.FadeOutFrames > 0 && to.RecordOut - to.FadeOutFrames < transition.RecordOut ? to.FadeOutFrames : 0;
                        bool plainLegs =
                            from.Transform is null && from.Crop is null && TransformFilterBuilder.SourceRotationFilter(from).Length == 0 && fromFadeIn == 0 && !HasPictureDepth(from, neighbours.FromGroupGrade) &&
                            to.Transform is null && to.Crop is null && TransformFilterBuilder.SourceRotationFilter(to).Length == 0 && toFadeOut == 0 && !HasPictureDepth(to, neighbours.ToGroupGrade);

                        if (plainLegs)
                        {
                            // The P5 bytes, untouched: two opaque fitted legs.
                            string fit = $"scale={target.Width}:{target.Height}:force_original_aspect_ratio=decrease,pad={target.Width}:{target.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p";
                            graph.Add($"[{inputA}:v]{legA},{fit}[x{layerNumber}a]");
                            graph.Add($"[{inputB}:v]{legB},{fit}[x{layerNumber}b]");
                            graph.Add(
                                $"[x{layerNumber}a][x{layerNumber}b]xfade=transition={kind}:duration={Num(spanSec)}:offset=0," +
                                $"trim=start={Num(skipSec)}:duration={Num(durationSec)},setpts=PTS-STARTPTS,format=yuv420p[{label}]");
                            overlayOptions = string.Empty;
                            break;
                        }

                        // RD-6: each leg renders through its clip's own chain (transform, crop, fades,
                        // grade, mask, keyer, effects, group grade) in transition-local time, is placed on
                        // a transparent canvas, and the two canvases are blended — so nothing pops to
                        // plain for the span, and what is around a moved/scaled picture stays see-through.
                        TransformFilterBuilder.LayerContext legContext = new(target, transition.RecordIn, transition.RecordOut);
                        foreach ((MediaClip clip, GradeStack? group, int input, string leg, char side, long fadeIn, long fadeOut, bool pullBack) in new[]
                        {
                            (from, neighbours.FromGroupGrade, inputA, legA, 'a', fromFadeIn, 0L, false),
                            (to, neighbours.ToGroupGrade, inputB, legB, 'b', 0L, toFadeOut, true),
                        })
                        {
                            string legLabel = $"x{layerNumber}{side}";
                            TransformFilterBuilder.LayerChain chain = TransformFilterBuilder.Build(clip, legContext, fadeIn, fadeOut);
                            string legSource = $"[{input}:v]";
                            string legPicture = leg;
                            if (effectMaskInputs.TryGetValue($"{pictureOrdinal}{side}", out List<List<int>>? legEffects))
                            {
                                legSource = RegionEffectStages(graph, clip, assets, legEffects, legSource, legLabel, leg,
                                    LegChain(clip, transition.RecordIn, transition.RecordOut, target, padToEnd: true, allowOpticalFlow: false), target, spanSec, ref effectSlot);
                                legPicture = "null";
                            }

                            if (maskInputs.TryGetValue($"{pictureOrdinal}{side}", out List<int>? legMasks))
                            {
                                legSource = SegmentationMatte(graph, clip, legSource, legMasks, legLabel, legPicture,
                                    LegChain(clip, transition.RecordIn, transition.RecordOut, target, padToEnd: true, allowOpticalFlow: false));
                                legPicture = "null";
                            }

                            string body = ClipLayer(
                                clip, group, chain, legPicture, legLabel, target, transition.RecordIn,
                                lutFilePathFor, lutRequests, ref lutIndex, luts, stabilizePathFor, prePasses,
                                () => InputArgs(clip, assets, transition.RecordIn, transition.RecordOut, fps, continuePastEnd: !pullBack, pullBack: pullBack), threads,
                                out string legOverlay);
                            graph.Add($"{legSource}{body}[{legLabel}l]");
                            graph.Add($"color=c=black@0:s={target.Width}x{target.Height}:r={target.RateArg}:d={Num(spanSec)},format=rgba[{legLabel}c]");
                            string place = legOverlay.Length == 0 ? "overlay=eof_action=pass" : $"overlay={legOverlay}:eof_action=pass";
                            graph.Add($"[{legLabel}c][{legLabel}l]{place},format=rgba[{legLabel}]");
                        }

                        graph.Add(
                            $"[x{layerNumber}a][x{layerNumber}b]xfade=transition={kind}:duration={Num(spanSec)}:offset=0," +
                            $"trim=start={Num(skipSec)}:duration={Num(durationSec)},setpts=PTS-STARTPTS,format=rgba[{label}]");
                        overlayOptions = string.Empty;
                        break;
                    }
                default:
                    continue;
            }

            if (layer.Item is MediaClip { } blended && BlendModes.FfmpegOption(blended.BlendMode) is { } blendOption)
            {
                // L5: blend in RGB (never YUV), gamma-encoded. The layer is first placed on a transparent canvas-size
                // frame with the SAME overlay geometry the plain path uses, so its alpha (opacity x fades x mask x
                // bars) rides along; blend() then runs backdrop (first input) x layer (second), and the result is
                // mixed back by the layer's alpha so transparent areas keep the backdrop.
                string n = $"b{layerNumber}";
                string place = overlayOptions.Length == 0 ? "overlay=format=rgb:eof_action=pass" : $"overlay={WithoutOverlayFormat(overlayOptions)}:format=rgb:eof_action=pass";
                graph.Add($"color=c=black@0:s={target.Width}x{target.Height}:r={target.RateArg}:d={Num(durationSec)},format=rgba[{n}c]");
                graph.Add($"[{n}c][{label}]{place},format=rgba,split=2[{n}f][{n}a]");
                graph.Add($"[{n}f]format=gbrp[{n}rgb]");
                graph.Add($"[{n}a]alphaextract[{n}al]");
                graph.Add($"{current}split=2[{n}k][{n}u]");
                graph.Add($"[{n}k]format=gbrp[{n}bk]");
                graph.Add($"[{n}bk][{n}rgb]blend={blendOption}[{n}m]");
                graph.Add($"[{n}m][{n}al]alphamerge,format=rgba[{n}x]");
                graph.Add($"[{n}u][{n}x]overlay=eof_action=pass[v{layerNumber}]");
                current = $"[v{layerNumber}]";
                layerNumber++;
                continue;
            }

            string overlay = overlayOptions.Length == 0 ? "overlay=eof_action=pass" : $"overlay={overlayOptions}:eof_action=pass";
            graph.Add($"{current}[{label}]{overlay}[v{layerNumber}]");
            current = $"[v{layerNumber}]";
            layerNumber++;
        }


        graph.Add($"{current}format=yuv420p[vout]");

        string graphText = string.Join(';', graph);
        TextFile? graphFile = null;
        if (graphText.Length > MaxInlineGraphChars)
        {
            graphFile = new TextFile(textFilePathFor(-1), graphText);
            args.AddRange(["-filter_complex_script", graphFile.Path]);
        }
        else
        {
            args.AddRange(["-filter_complex", graphText]);
        }

        args.AddRange(["-map", "[vout]", "-an"]);
        args.AddRange(TimelineRenderPresets.MezzanineVideoArgs);
        args.AddRange(["-r", target.RateArg, "-frames:v", Num(region.DurationFrames), "-movflags", "+faststart", outputPath]);

        return new RegionArgv(
            args, textFiles, graphFile,
            lutRequests.Count == 0 ? null : lutRequests,
            prePasses.Count == 0 ? null : prePasses,
            cameraProfileRequests.Count == 0 ? null : cameraProfileRequests);
    }

    /// <summary>
    /// The filter chain of one media clip's picture, from its leg (<c>trim … fps</c>) to the layer the
    /// overlay places: the P4/P5 chain unchanged when the clip has no P7 depth, else stabilise, reframe,
    /// keyer, the fitted picture, group grade, clip grade, effects, mask and corner pin. Used for an
    /// ordinary layer and for each leg of a transition (RD-6), with <paramref name="origin"/> the time
    /// origin of its stream (region start, or transition start for a leg).
    /// </summary>
    private static string ClipLayer(
        MediaClip clip,
        GradeStack? groupGrade,
        TransformFilterBuilder.LayerChain chain,
        string leg,
        string label,
        ResolvedRenderTarget target,
        long origin,
        Func<int, string> lutFilePathFor,
        List<GradeFilterBuilder.LutRequest> lutRequests,
        ref int lutIndex,
        IReadOnlyDictionary<AssetRef, CubeLut>? luts,
        Func<int, string> stabilizePathFor,
        List<PrePass> prePasses,
        Func<IEnumerable<string>> prePassInput,
        int threads,
        out string overlayOptions)
    {
        double fps = target.Fps;
        if (!HasPictureDepth(clip, groupGrade))
        {
            // The P4/P5 bytes, untouched.
            overlayOptions = chain.OverlayOptions;
            string plainRotation = TransformFilterBuilder.SourceRotationFilter(clip);
            return plainRotation.Length == 0 ? $"{leg},{chain.Chain}" : $"{leg},{plainRotation},{chain.Chain}";
        }

        List<string> parts = [leg];
        if (clip.Stabilize)
        {
            string trf = stabilizePathFor(prePasses.Count);
            List<string> pass = ["-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", Num(threads)];
            pass.AddRange(prePassInput());
            pass.AddRange(["-vf", $"{leg},{StabilizeFilter.Detect(trf)}", "-f", "null", "-"]);
            prePasses.Add(new PrePass(pass, trf));
            parts.Add(StabilizeFilter.Transform(trf));
        }

        Add(parts, TransformFilterBuilder.SourceRotationFilter(clip));
        Add(parts, GradeFilterBuilder.Reframe(clip.Reframe, target.Width, target.Height));
        Add(parts, GradeFilterBuilder.Keyer(clip.Keyer));
        // Alpha from a keyer, a mask or a corner pin must survive to the overlay: drop the
        // chain's closing yuv420p and end in rgba instead.
        bool alpha = clip.Keyer is not null || clip.Mask is not null || clip.CornerPin is not null;
        string picture = alpha && chain.Chain.EndsWith(",format=yuv420p", StringComparison.Ordinal) ? chain.Chain[..^",format=yuv420p".Length] : chain.Chain;
        // RD-4: the P4 fit pads with opaque black; with alpha the bars must be transparent
        // (still padded to the canvas, so mask fractions stay canvas fractions).
        if (alpha && chain.UsesP4Fit) picture = TransparentFit(target);
        Add(parts, picture);
        Add(parts, GradeFilterBuilder.Build(groupGrade, lutFilePathFor, lutRequests, ref lutIndex, $"{label}g", origin, fps, luts));
        Add(parts, GradeFilterBuilder.Build(clip.Grade, lutFilePathFor, lutRequests, ref lutIndex, $"{label}c", origin, fps, luts));
        Add(parts, GradeFilterBuilder.Effects(clip.Effects));
        Add(parts, GradeFilterBuilder.Mask(clip.Mask));
        if (clip.CornerPin is not null)
        {
            // The pin wants the whole canvas as its source: pad the fitted picture first.
            Add(parts, $"pad={target.Width}:{target.Height}:(ow-iw)/2:(oh-ih)/2:color=0x00000000");
            Add(parts, GradeFilterBuilder.CornerPin(clip.CornerPin, target.Width, target.Height));
        }

        if (alpha) parts.Add("format=rgba");
        overlayOptions = clip.CornerPin is not null ? string.Empty : chain.OverlayOptions;
        return string.Join(',', parts);
    }

    /// <summary>The mask tracks a segmentation mask names, in the order the document lists them; a missing one is a programming error (the step executor verifies them first).</summary>
    private static IEnumerable<LocalSegmentationMask> ResolveMasks(ShapeMask mask, IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? resolved)
    {
        foreach (string trackId in mask.TrackIds ?? [])
        {
            if (mask.AnalysisStepResultId is not { } analysis || resolved is null || !resolved.TryGetValue(new SegmentationMaskRef(analysis, trackId), out LocalSegmentationMask? local))
                throw new InvalidOperationException("A segmentation mask track was not verified and staged before rendering.");
            yield return local;
        }
    }

    /// <summary>
    /// T-015: the segmentation matte as graph stages. Each named track's FFV1 gray mask is read on the clip's
    /// own source clock (same seek, trim, reverse and rate as its picture), the tracks are unioned
    /// (<c>blend=lighten</c>), inverted and feathered (<see cref="RegionEffectFilterBuilder.BuildMaskHelperChain"/>:
    /// invert, then <c>gblur</c> on the MASK), and <c>alphamerge</c>d onto the picture at SOURCE geometry — before
    /// the clip's own crop/transform, so the matte follows the picture it was cut from. Returns the label the
    /// remaining chain reads.
    /// </summary>
    private static string SegmentationMatte(
        List<string> graph, MediaClip clip, string pictureSource, IReadOnlyList<int> maskInputs, string label, string pictureLeg, string maskLeg)
    {
        if (clip.Stabilize)
            throw new InvalidOperationException("A segmentation mask cannot be combined with stabilisation.");
        ShapeMask mask = clip.Mask!;
        for (int k = 0; k < maskInputs.Count; k++)
            graph.Add($"[{maskInputs[k]}:v]{maskLeg},format=gray[{label}mk{k}]");

        string union = $"[{label}mk0]";
        for (int k = 1; k < maskInputs.Count; k++)
        {
            string next = $"[{label}mu{k}]";
            graph.Add($"{union}[{label}mk{k}]blend=all_mode=lighten{next}");
            union = next;
        }

        graph.Add(RegionEffectFilterBuilder.BuildMaskHelperChain(union, $"[{label}mh]", mask.Invert, mask.FeatherPx, 1.0));
        graph.Add($"{pictureSource}{pictureLeg},format=rgba[{label}pic]");
        graph.Add($"[{label}pic][{label}mh]alphamerge[{label}sm]");
        return $"[{label}sm]";
    }

    /// <summary>
    /// Magic blur: each enabled <c>regionEffects</c> entry, in order, as graph stages. The picture leg is decoded
    /// (trim/rate/fps) and the effect is applied through the entry's segmentation matte AT SOURCE GEOMETRY — before
    /// the clip's own crop/transform/grade/effects (exactly where the segmentation matte is cut), so the effect
    /// follows the object through every later transform. The mask streams are read on the clip's source clock
    /// like <see cref="SegmentationMatte"/>'s (union of the entry's tracks, invert + feather via
    /// <see cref="RegionEffectFilterBuilder.BuildMaskHelperChain"/>). Known limits: the effect chain composites in
    /// yuv420p, so a source's own alpha is dropped; a render-only feature (the preview cannot show it).
    /// Returns the label the remaining chain reads (it has already consumed <paramref name="pictureLeg"/>).
    /// </summary>
    private static string RegionEffectStages(
        List<string> graph, MediaClip clip, IReadOnlyDictionary<AssetRef, LocalAsset> assets, IReadOnlyList<List<int>> entryInputs,
        string pictureSource, string label, string pictureLeg, string maskLeg, ResolvedRenderTarget target, double legDurationSec, ref int effectSlot)
    {
        if (clip.Stabilize)
            throw new InvalidOperationException("A region effect cannot be combined with stabilisation.");
        MediaProbeResult probe = assets[clip.AssetRef].Probe;
        IReadOnlyList<RegionClipEffect> effects = EnabledRegionEffects(clip);
        string current = $"[{label}rp]";
        graph.Add($"{pictureSource}{pictureLeg}{current}");
        for (int e = 0; e < effects.Count; e++)
        {
            RegionClipEffect effect = effects[e];
            int slot = effectSlot++;
            string prefix = $"{label}re{e}";
            IReadOnlyList<int> inputs = entryInputs[e];
            for (int k = 0; k < inputs.Count; k++)
                graph.Add($"[{inputs[k]}:v]{maskLeg},format=gray[{prefix}mk{k}]");
            string union = $"[{prefix}mk0]";
            for (int k = 1; k < inputs.Count; k++)
            {
                string next = $"[{prefix}mu{k}]";
                graph.Add($"{union}[{prefix}mk{k}]blend=all_mode=lighten{next}");
                union = next;
            }

            graph.Add(RegionEffectFilterBuilder.BuildMaskHelperChain(union, $"[{prefix}mh]", effect.Mask.Invert, effect.Mask.FeatherPx, 1.0));
            string output = $"[{prefix}o]";
            graph.Add(RegionEffectFilterBuilder.BuildEffectChain(
                current, $"[{prefix}mh]", output, slot, ToRegionEffect(effect.Effect), probe.Width, probe.Height, effect.Strength, target.RateArg, legDurationSec));
            current = output;
        }

        return current;
    }

    /// <summary>The overlay options without their <c>format=</c> option: the blend overlay sets its own (<c>format=rgb</c>), and a second one is a duplicate option.</summary>
    private static string WithoutOverlayFormat(string options) =>
        string.Join(':', options.Split(':').Where(o => !o.StartsWith("format=", StringComparison.Ordinal)));

    /// <summary>The P4 fit (aspect kept, centred on the canvas) with transparent bars instead of black ones.</summary>
    private static string TransparentFit(ResolvedRenderTarget target) =>
        $"scale={target.Width}:{target.Height}:force_original_aspect_ratio=decrease,format=rgba,pad={target.Width}:{target.Height}:(ow-iw)/2:(oh-ih)/2:color=0x00000000,setsar=1";

    private static void Add(List<string> parts, string filter)
    {
        if (!string.IsNullOrEmpty(filter)) parts.Add(filter);
    }

    /// <summary>
    /// The <c>-ss/-t/-i</c> for one leg of a clip over timeline <c>[start, stop)</c>: a coarse seek before
    /// the input (fast), with an exact trim inside the graph (accurate). The extra 0.5 s read keeps the
    /// decoder from starving at the trim's end on long-GOP sources. A reversed clip reads its span from
    /// the lowest source frame; a held clip reads one frame at <c>sourceIn</c>.
    /// </summary>
    private static IEnumerable<string> InputArgs(
        MediaClip clip, IReadOnlyDictionary<AssetRef, LocalAsset> assets, long start, long stop, double fps,
        bool continuePastEnd = false, bool pullBack = false) =>
        InputArgsAt(assets[clip.AssetRef].LocalPath, clip, start, stop, fps, continuePastEnd, pullBack);

    /// <summary>
    /// <see cref="InputArgs"/> for an arbitrary local file read on the clip's SOURCE clock: a segmentation
    /// mask video is source-time aligned, so it takes exactly the seek its clip's picture takes.
    /// </summary>
    private static IEnumerable<string> InputArgsAt(
        string localPath, MediaClip clip, long start, long stop, double fps, bool continuePastEnd, bool pullBack)
    {
        double rate = Rate(clip);
        double spanSec = (stop - start) * rate / fps;

        if (clip.Hold)
        {
            return [.. UserMediaInputFormats.InputOptions(), "-ss", Num(clip.SourceIn / fps), "-t", Num(Math.Max(0.5, 2.0 / fps)), "-i", localPath];
        }

        double sourceStartFrames;
        if (clip.Reverse)
        {
            // Lowest source frame of the span: counted back from the clip's end (pullBack/continue
            // simply extend the span the planner already expressed through start/stop).
            sourceStartFrames = clip.SourceIn + (clip.RecordOut - stop) * rate;
        }
        else if (pullBack)
        {
            // The incoming leg of a transition: `start` lies BEFORE the clip's recordIn.
            sourceStartFrames = clip.SourceIn - (clip.RecordIn - start) * rate;
        }
        else
        {
            // Ordinary leg, or the outgoing leg of a transition continuing past its recordOut.
            sourceStartFrames = clip.SourceIn + (start - clip.RecordIn) * rate;
        }

        _ = continuePastEnd;
        double sourceStartSec = Math.Max(0, sourceStartFrames) / fps;
        return [.. UserMediaInputFormats.InputOptions(), "-ss", Num(sourceStartSec), "-t", Num(spanSec + 0.5), "-i", localPath];
    }

    /// <summary>
    /// The per-leg chain that turns an input into a region-length stream at the target rate:
    /// <c>trim,setpts[,reverse][,setpts=PTS/rate],fps</c> — or, held, one frame looped. With
    /// <paramref name="padToEnd"/> the leg is padded with its last frame to the span (a transition
    /// leg whose source runs out must not shorten the <c>xfade</c>).
    /// </summary>
    private static string LegChain(MediaClip clip, long start, long stop, ResolvedRenderTarget target, bool padToEnd = false, bool allowOpticalFlow = true)
    {
        double fps = target.Fps;
        double spanSec = (stop - start) / fps;
        double rate = Rate(clip);

        if (clip.Hold)
        {
            return $"trim=end_frame=1,setpts=PTS-STARTPTS,loop=loop=-1:size=1:start=0,trim=duration={Num(spanSec)},setpts=N/({target.RateArg})/TB,fps={target.RateArg}";
        }

        // P7: a slowed clip asking for optical flow interpolates the new frames instead of repeating them.
        string retime = rate == 1.0
            ? string.Empty
            : allowOpticalFlow && clip.RetimeQuality == "opticalFlow" && rate < 1.0
                ? $"setpts=PTS/{Num(rate)},minterpolate=fps={target.RateArg}:mi_mode=mci:mc_mode=aobmc:vsbmc=1,"
                : $"setpts=PTS/{Num(rate)},";
        string reverse = clip.Reverse ? "reverse,setpts=PTS-STARTPTS," : string.Empty;
        string pad = padToEnd ? $"tpad=stop=-1:stop_mode=clone,trim=duration={Num(spanSec)}," : string.Empty;
        return $"trim=duration={Num(spanSec * rate)},setpts=PTS-STARTPTS,{reverse}{retime}fps={target.RateArg}{(pad.Length == 0 ? string.Empty : "," + pad.TrimEnd(','))}";
    }

    /// <summary>A generator as a filter source of the region's length at the canvas size and rate.</summary>
    private static string GeneratorSource(GeneratorClip generator, ResolvedRenderTarget target, double durationSec)
    {
        string size = $"s={target.Width}x{target.Height}:r={target.RateArg}:d={Num(durationSec)}";
        switch (generator.GeneratorType)
        {
            case "bars":
                return $"smptehdbars={size}";
            case "testsrc":
                return $"testsrc2={size}";
            default:
                {
                    string color = "#000000";
                    if (generator.GeneratorConfig is { } config && config.TryGetValue("color", out object? value))
                    {
                        string? text = value switch
                        {
                            string s => s,
                            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } el => el.GetString(),
                            _ => null
                        };
                        if (text is not null && HexColor.IsMatch(text)) color = text;
                    }

                    return $"color=c={color}:{size}";
                }
        }
    }

    private static bool HasLineBreak(string? text) => text is not null && text.AsSpan().IndexOfAny('\n', '\r') >= 0;

    private static double Rate(MediaClip clip) => SegmentPlanner.EffectiveRate(clip);

    private static int ClampFontSize(double requested, int canvasHeight)
    {
        if (!double.IsFinite(requested) || requested <= 0) requested = canvasHeight * 0.06;
        return (int)Math.Round(Math.Clamp(requested, 8, Math.Max(8, canvasHeight * 0.5)));
    }

    /// <summary>
    /// Escapes a path for use inside a single-quoted filter option. Paths are the renderer's own
    /// scratch paths, so this is belt-and-braces, not the boundary.
    /// </summary>
    private static string EscapeFilterPath(string path) =>
        path.Replace("\\", "\\\\").Replace("'", "\\'").Replace(":", "\\:");

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
