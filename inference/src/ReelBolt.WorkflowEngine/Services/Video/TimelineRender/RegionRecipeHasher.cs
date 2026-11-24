using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Editing;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

public sealed record LocalAsset(string LocalPath, string Fingerprint, MediaProbeResult Probe);

/// <summary>
/// The content-addressed identity of a region's mezzanine: everything that can change its pixels,
/// and nothing that cannot. Keyframes are relativised to the region start so an identical curve
/// over an identical picture hashes identically wherever the region sits on the program, and an
/// edit anywhere else on the timeline leaves this region's hash — and its cached mezzanine — alone.
/// </summary>
public static class RegionRecipeHasher
{
    /// <summary>
    /// Bumped whenever the builders' output changes for the same inputs. v2: the P5 layer chain
    /// (transform, crop, fades, reverse, hold, generators, transitions) joined the recipe. v3: the P7
    /// picture depth (grades incl. colour groups and LUT fingerprints, masks, keyers, effects,
    /// stabilisation, reframe, optical-flow retime, corner pins, adjustment clips, title templates).
    /// v4: editor-v2 remediation RD-2..RD-7 — a valid rotate size, a corner pin that no longer smears,
    /// transparent fit bars under a mask/keyer/pin, transition legs that keep each clip's own depth
    /// (and hash it), and one playback-rate clamp for picture, sound and source positions.
    /// v5: R09.6/R10.5 — caption looks, 3x3 caption/title positions, multiline captions (newlines were
    /// collapsed before) wrapped to the frame.
    /// v6: T-015 — a segmentation mask is alphamerged onto the clip's leg (the recipe now carries each
    /// named track's mask content hash, invert and featherPx); a timeline without one hashes as before
    /// apart from this version token.
    /// v7: magic blur — a clip's enabled <c>regionEffects</c> render through their segmentation masks (the recipe
    /// carries each entry's effect word, strength, invert, featherPx, analysis and each track's mask content
    /// hash); a timeline without one hashes as before apart from this version token.
    /// </summary>
    public const string RendererVersion = "timeline-render-region-v7";

    public static string Hash(
        RenderRegion region,
        ResolvedRenderTarget target,
        IReadOnlyDictionary<AssetRef, LocalAsset> assets,
        string fontFingerprint,
        IReadOnlyDictionary<AssetRef, string>? hdrToneMaps = null,
        IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? segmentationMasks = null,
        bool cameraProfileZscale = true)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assets);

        StringBuilder sb = new();
        Append(sb, RendererVersion);
        Append(sb, target.Width.ToString(CultureInfo.InvariantCulture));
        Append(sb, target.Height.ToString(CultureInfo.InvariantCulture));
        Append(sb, target.RateArg);
        Append(sb, region.DurationFrames.ToString(CultureInfo.InvariantCulture));
        Append(sb, fontFingerprint);
        Append(sb, region.Layers.Count.ToString(CultureInfo.InvariantCulture));

        // A title/caption below a picture layer now draws under it (track order, as the viewer stacks it); only such a
        // region renders differently from before, so only it adds a token and every other region keeps its hash.
        bool seenText = false, textUnderPicture = false;
        foreach (RenderLayer ordered in region.Layers)
        {
            if (ordered.Item is TitleClip or Caption) seenText = true;
            else if (seenText && ordered.Item is MediaClip or Transition or InsertClip or GeneratorClip or AdjustmentClip) textUnderPicture = true;
        }

        if (textUnderPicture) Append(sb, "textorder:track");

        foreach (RenderLayer layer in region.Layers)
        {
            Item item = layer.Item;
            long offsetInRegion = region.StartFrame - item.RecordIn; // >= 0: the layer covers the region
            Append(sb, item.GetType().Name);
            switch (item)
            {
                case MediaClip clip:
                    AppendClip(sb, clip, region, assets, layer.GroupGrade, hdrToneMaps, segmentationMasks, cameraProfileZscale);
                    // Tone-map after the downscale changes the bytes of exactly these clips (v8 token).
                    if (RegionFilterGraphBuilder.ToneMapAfterScale(clip, layer.GroupGrade, target, assets, hdrToneMaps))
                        Append(sb, "tonemap-after-scale");
                    break;
                case TitleClip title:
                    Append(sb, title.Text);
                    Append(sb, Num(title.FontSize));
                    Append(sb, title.FontFamily);
                    Append(sb, title.Color);
                    if (title.Template is not null) Append(sb, "template:" + title.Template);
                    if (title.Position is not null) Append(sb, "pos:" + title.Position);
                    if (title.Transform is { } tt)
                    {
                        AppendChannel(sb, "tpx", tt.PositionX, region.StartFrame);
                        AppendChannel(sb, "tpy", tt.PositionY, region.StartFrame);
                        AppendChannel(sb, "tsc", tt.Scale, region.StartFrame);
                        if (tt.ScaleY is { } tsy && (tsy.Value != 0 || tsy.Keys is { Count: > 0 })) AppendChannel(sb, "tsy", tt.ScaleY, region.StartFrame);
                        AppendChannel(sb, "tro", tt.Rotation, region.StartFrame);
                        AppendChannel(sb, "top", tt.Opacity, region.StartFrame);
                    }
                    break;
                case AdjustmentClip adjustment:
                    AppendGrade(sb, "adj", adjustment.Grade, assets, region.StartFrame);
                    AppendEffects(sb, adjustment.Effects);
                    break;
                case Caption caption:
                    Append(sb, caption.Text);
                    Append(sb, caption.Language);
                    if (CaptionBurnIn.HashKey(caption.Look, caption.Position) is { Length: > 0 } captionStyle) Append(sb, captionStyle);
                    break;
                case InsertClip insert when insert.AssetRef is not null && insert.Quad is not null && assets.TryGetValue(insert.AssetRef, out LocalAsset? insertAsset):
                    Append(sb, insertAsset.Fingerprint);
                    Append(sb, Num(offsetInRegion));
                    AppendQuad(sb, insert.Quad, region.StartFrame);
                    if (insert.Matte is { } matte) Append(sb, $"matte:{matte.Color}:{Num(matte.Similarity)}:{Num(matte.Blend)}:{Num(matte.Spill)}");
                    break;
                case GeneratorClip generator:
                    Append(sb, generator.GeneratorType);
                    Append(sb, GeneratorColour(generator));
                    break;
                case Transition transition when layer.Transition is { } neighbours:
                    Append(sb, transition.TransitionType);
                    Append(sb, transition.Easing);
                    Append(sb, Num(offsetInRegion));
                    Append(sb, Num(transition.RecordOut - transition.RecordIn));
                    // Both legs are read over the whole transition span, so their source positions at
                    // the transition start are what identify the blend.
                    AppendLeg(sb, neighbours.From, transition.RecordIn, assets);
                    AppendLeg(sb, neighbours.To, transition.RecordIn, assets);
                    // v4 (RD-6): the legs render each clip's own depth over the span, so it is part of the blend.
                    RenderRegion span = new(transition.RecordIn, transition.RecordOut, []);
                    AppendClip(sb, neighbours.From, span, assets, neighbours.FromGroupGrade, hdrToneMaps, segmentationMasks, cameraProfileZscale);
                    AppendClip(sb, neighbours.To, span, assets, neighbours.ToGroupGrade, hdrToneMaps, segmentationMasks, cameraProfileZscale);
                    break;
                default:
                    Append(sb, "inert");
                    break;
            }
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void AppendClip(StringBuilder sb, MediaClip clip, RenderRegion region, IReadOnlyDictionary<AssetRef, LocalAsset> assets, GradeStack? groupGrade, IReadOnlyDictionary<AssetRef, string>? hdrToneMaps = null, IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? segmentationMasks = null, bool cameraProfileZscale = true)
    {
        LocalAsset asset = assets[clip.AssetRef];
        double rate = clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0;
        Append(sb, asset.Fingerprint);
        Append(sb, Num(SegmentPlanner.SourceFrameAt(clip, region.StartFrame)));
        Append(sb, Num(rate));

        // R10.7: only a tone-mapped source adds a token, so every SDR region keeps its hash (and cache).
        string? hdrKind = hdrToneMaps is not null && hdrToneMaps.TryGetValue(clip.AssetRef, out string? kind) ? kind : null;
        if (hdrKind is not null)
        {
            Append(sb, HdrToneMap.RecipeToken(hdrKind));
        }
        else if (clip.InputProfile is { } profile && HdrToneMap.KindOf(asset.Probe) is null)
        {
            // T-011: only a region with a profile clip adds the stage token (profile + the decode
            // range/matrix it selects). Every profile-less region hashes exactly as before, and an
            // HDR source ignored its profile, so it gets no token here either.
            // The zscale-vs-swscale choice changes the pixels slightly, so a host without zscale must not share this
            // region's cache entry. Only a profiled region carries it; the zscale token stays as it was.
            Append(sb, CameraProfileStage.RecipeToken(profile) + (cameraProfileZscale ? "" : ":swscale"));
        }

        // L5: only a blending clip adds a token, so every normal-mode region keeps its hash (and cache).
        if (BlendModes.IsBlending(clip.BlendMode)) Append(sb, "blend:" + clip.BlendMode);

        bool pictureDepth = RegionFilterGraphBuilder.HasPictureDepth(clip, groupGrade);
        if (TransformFilterBuilder.IsIdentity(clip) && !clip.Reverse && !clip.Hold && !pictureDepth)
        {
            return; // the P4 recipe, extended by nothing
        }

        if (pictureDepth)
        {
            AppendGrade(sb, "group", groupGrade, assets, region.StartFrame);
            AppendGrade(sb, "clip", clip.Grade, assets, region.StartFrame);
            AppendEffects(sb, clip.Effects);
            if (clip.Mask is { } seg && seg.IsSegmentationMask())
            {
                // The mask CONTENT hash of every named track, in document order: a re-cut mask re-renders the region.
                Append(sb, $"segmask:{(seg.Invert ? 1 : 0)}:{Num(seg.FeatherPx)}:{seg.AnalysisStepResultId}");
                foreach (string trackId in seg.TrackIds ?? [])
                {
                    string contentHash = seg.AnalysisStepResultId is { } analysis && segmentationMasks is not null
                        && segmentationMasks.TryGetValue(new SegmentationMaskRef(analysis, trackId), out LocalSegmentationMask? local)
                        ? local.ContentHash
                        : "unresolved";
                    Append(sb, $"segtrack:{trackId}:{contentHash}");
                }
            }
            else if (clip.Mask is { } m) Append(sb, $"mask:{m.Kind}:{Num(m.X)}:{Num(m.Y)}:{Num(m.W)}:{Num(m.H)}:{Num(m.Feather)}:{(m.Invert ? 1 : 0)}");
            foreach (RegionClipEffect re in RegionFilterGraphBuilder.EnabledRegionEffects(clip))
            {
                // Same token shape as the clip mask: the CONTENT hash of every track, in document order.
                Append(sb, $"regeffect:{re.Effect}:{Num(re.Strength)}:{(re.Mask.Invert ? 1 : 0)}:{Num(re.Mask.FeatherPx)}:{re.Mask.AnalysisStepResultId}");
                foreach (string trackId in re.Mask.TrackIds ?? [])
                {
                    string contentHash = re.Mask.AnalysisStepResultId is { } analysis && segmentationMasks is not null
                        && segmentationMasks.TryGetValue(new SegmentationMaskRef(analysis, trackId), out LocalSegmentationMask? local)
                        ? local.ContentHash
                        : "unresolved";
                    Append(sb, $"segtrack:{trackId}:{contentHash}");
                }
            }

            if (clip.Keyer is { } k) Append(sb, $"key:{k.Color}:{Num(k.Similarity)}:{Num(k.Blend)}:{Num(k.Spill)}");
            if (clip.Stabilize) Append(sb, "stabilize");
            if (clip.Reframe is { } r) Append(sb, $"reframe:{Num(r.X)}:{Num(r.Y)}");
            if (clip.RetimeQuality == "opticalFlow") Append(sb, "opticalFlow");
            if (clip.CornerPin is { } p) Append(sb, $"pin:{Num(p.TopLeft.X)}:{Num(p.TopLeft.Y)}:{Num(p.TopRight.X)}:{Num(p.TopRight.Y)}:{Num(p.BottomRight.X)}:{Num(p.BottomRight.Y)}:{Num(p.BottomLeft.X)}:{Num(p.BottomLeft.Y)}");
        }

        Append(sb, clip.Reverse ? "reverse" : clip.Hold ? "hold" : "forward");

        // Fades depend on where the region sits inside the clip, not on the source position.
        Append(sb, Num(region.StartFrame - clip.RecordIn));
        Append(sb, Num(clip.RecordOut - region.EndFrame));
        Append(sb, Num(clip.FadeInFrames));
        Append(sb, Num(clip.FadeOutFrames));

        if (TransformFilterBuilder.SourceRotationFilter(clip).Length > 0) Append(sb, $"srot:{clip.SourceRotation}");

        if (clip.Crop is { } crop)
        {
            Append(sb, $"crop:{Num(crop.Left)}:{Num(crop.Top)}:{Num(crop.Right)}:{Num(crop.Bottom)}");
        }

        if (clip.Transform is { } t)
        {
            AppendChannel(sb, "px", t.PositionX, region.StartFrame);
            AppendChannel(sb, "py", t.PositionY, region.StartFrame);
            AppendChannel(sb, "sc", t.Scale, region.StartFrame);
            if (t.ScaleY is { } sy && (sy.Value != 0 || sy.Keys is { Count: > 0 })) AppendChannel(sb, "sy", t.ScaleY, region.StartFrame);
            AppendChannel(sb, "ro", t.Rotation, region.StartFrame);
            AppendChannel(sb, "op", t.Opacity, region.StartFrame);
        }
    }

    /// <summary>P7: a grade stack with every active node's numbers and each LUT's asset fingerprint plus intensity.</summary>
    private static void AppendGrade(StringBuilder sb, string name, GradeStack? stack, IReadOnlyDictionary<AssetRef, LocalAsset> assets, long regionStart = 0)
    {
        if (!GradeFilterBuilder.IsActive(stack)) return;
        StringBuilder g = new();
        g.Append(name);
        foreach (GradeNode node in stack!.Nodes!)
        {
            if (!node.Enabled) continue;
            g.Append("|n");
            if (node.Primaries is { } p && !p.IsIdentity())
            {
                void W(string tag, GradeWheel? w)
                {
                    if (w is null || w.IsIdentity()) return;
                    g.Append(tag).Append(Num(w.R)).Append(',').Append(Num(w.G)).Append(',').Append(Num(w.B)).Append(',').Append(Num(w.Master));
                }

                W(":lift=", p.Lift);
                W(":gamma=", p.Gamma);
                W(":gain=", p.Gain);
                W(":offset=", p.Offset);
                g.Append(":c=").Append(Num(p.Contrast)).Append(":pv=").Append(Num(p.Pivot)).Append(":s=").Append(Num(p.Saturation))
                 .Append(":h=").Append(Num(p.Hue)).Append(":t=").Append(Num(p.Temperature)).Append(":ti=").Append(Num(p.Tint)).Append(":e=").Append(Num(p.Exposure));
                // tint and gamma green became one colorbalance gm; only such nodes changed pixels.
                if (GradeFilterBuilder.MergesTintIntoGammaGreen(p)) g.Append(":gm-merge1");
            }

            if (node.Curves is { } c && !c.IsIdentity())
            {
                foreach ((string tag, IReadOnlyList<CurvePoint>? points) in new[] { ("y", c.Y), ("r", c.R), ("g", c.G), ("b", c.B) })
                {
                    if (points is not { Count: > 0 }) continue;
                    g.Append(":curve").Append(tag).Append('=');
                    foreach (CurvePoint point in points) g.Append(Num(point.X)).Append('/').Append(Num(point.Y)).Append(' ');
                }
            }

            if (node.Lut is { } lut && lut.Intensity > 0)
            {
                string fingerprint = assets.TryGetValue(lut.File, out LocalAsset? lutAsset) ? lutAsset.Fingerprint : "missing";
                g.Append(":lut=").Append(fingerprint).Append('@').Append(Num(lut.Intensity));
            }

            // P9: a secondary's selection is part of what the node renders.
            if (node.Qualifier is { } q && !q.SelectsAll())
            {
                g.Append(":q=").Append(Num(q.HueCenter)).Append(',').Append(Num(q.HueWidth)).Append(',').Append(Num(q.SatMin)).Append(',').Append(Num(q.SatMax))
                 .Append(',').Append(Num(q.LumMin)).Append(',').Append(Num(q.LumMax)).Append(',').Append(Num(q.Softness)).Append(',').Append(q.Invert);
            }

            if (node.Window is { Shape: { } shape } window)
            {
                g.Append(":w=").Append(shape.Kind).Append(',').Append(Num(shape.X)).Append(',').Append(Num(shape.Y)).Append(',').Append(Num(shape.W)).Append(',').Append(Num(shape.H))
                 .Append(',').Append(Num(shape.Feather)).Append(',').Append(shape.Invert);
                foreach ((string tag, Keyframed<double>? channel) in new[] { ("ox", window.OffsetX), ("oy", window.OffsetY) })
                {
                    if (channel is null) continue;
                    g.Append(':').Append(tag).Append('=').Append(Num(channel.Value));
                    foreach (Keyframe<double> key in channel.Keys ?? []) g.Append('|').Append(Num(key.Frame - regionStart)).Append('=').Append(Num(key.Value)).Append('/').Append(key.Interp);
                }
            }
        }

        Append(sb, g.ToString());
    }

    private static void AppendEffects(StringBuilder sb, IReadOnlyList<ClipEffect>? effects)
    {
        if (effects is not { Count: > 0 }) return;
        StringBuilder e = new("fx");
        foreach (ClipEffect effect in effects)
        {
            if (!effect.Enabled || effect.Amount <= 0) continue;
            e.Append('|').Append(effect.Type).Append('=').Append(Num(effect.Amount));
        }

        Append(sb, e.ToString());
    }

    private static void AppendLeg(StringBuilder sb, MediaClip clip, long atFrame, IReadOnlyDictionary<AssetRef, LocalAsset> assets)
    {
        LocalAsset asset = assets[clip.AssetRef];
        Append(sb, asset.Fingerprint);
        Append(sb, Num(SegmentPlanner.SourceFrameAt(clip, atFrame)));
        Append(sb, Num(clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0));
        Append(sb, clip.Reverse ? "reverse" : clip.Hold ? "hold" : "forward");
    }

    /// <summary>A keyframed channel with every key's frame made relative to the region start.</summary>
    private static void AppendChannel(StringBuilder sb, string name, Keyframed<double>? channel, long regionStart)
    {
        if (channel is null) return;
        StringBuilder c = new();
        c.Append(name).Append(':').Append(Num(channel.Value));
        if (channel.Keys is { } keys)
        {
            foreach (Keyframe<double> key in keys)
            {
                c.Append('|').Append(Num(key.Frame - regionStart)).Append('=').Append(Num(key.Value)).Append('/').Append(key.Interp);
            }
        }

        Append(sb, c.ToString());
    }

    /// <summary>An insert's quad: the base corners and every keyframe (frames relative to the region start).</summary>
    private static void AppendQuad(StringBuilder sb, Keyframed<QuadCorners> quad, long regionStart)
    {
        static string Q(QuadCorners q) =>
            $"{Num(q.TopLeft.X)},{Num(q.TopLeft.Y)},{Num(q.TopRight.X)},{Num(q.TopRight.Y)},{Num(q.BottomRight.X)},{Num(q.BottomRight.Y)},{Num(q.BottomLeft.X)},{Num(q.BottomLeft.Y)}";
        StringBuilder c = new("quad:");
        if (quad.Value is { } v) c.Append(Q(v));
        foreach (Keyframe<QuadCorners> key in quad.Keys ?? [])
        {
            c.Append('|').Append(Num(key.Frame - regionStart)).Append('=').Append(Q(key.Value)).Append('/').Append(key.Interp);
        }

        Append(sb, c.ToString());
    }

    private static string GeneratorColour(GeneratorClip generator)
    {
        if (generator.GeneratorConfig is { } config && config.TryGetValue("color", out object? value))
        {
            return value switch
            {
                string s => s,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } el => el.GetString() ?? string.Empty,
                _ => string.Empty
            };
        }

        return string.Empty;
    }

    private static void Append(StringBuilder sb, string value)
    {
        sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
    }

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
