using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing.Interchange;

/// <summary>
/// Interchange writers and the OTIO reader for an editor v2 document (P6): CMX3600 EDL, SRT and
/// WebVTT sidecars, and OpenTimelineIO JSON in both directions. Pure functions over the document —
/// nothing here touches storage; the controller resolves assets and names before and after.
/// </summary>
/// <remarks>
/// <para>
/// <b>EDL.</b> One event per media clip of the FIRST video track (the classic single-track CMX3600),
/// non-drop-frame timecode at the document rate, reel <c>AX</c> for every clip (reel names are
/// eight characters and carry no project meaning here), the asset id in a <c>* SOURCE FILE</c>
/// comment and the clip name in <c>* FROM CLIP NAME</c>. A transition between two clips becomes a
/// <c>D nnn</c> event (dissolve of the transition's length) whatever its <c>xfade</c> word, since
/// CMX3600 knows only cut, dissolve and wipe codes. Source timecode is the clip's source range at
/// its own rate; a reversed or held clip exports its source range as read forwards.
/// </para>
/// <para>
/// <b>OTIO.</b> <c>Timeline.1 → Stack.1 → Track.1 (Video|Audio) → Gap.1 | Clip.1 | Transition.1</c>
/// with <c>ExternalReference.1</c> media references whose <c>target_url</c> is a
/// <c>reelbolt://project-file/{guid}</c> or <c>reelbolt://step-result/{guid}</c> URL — a stable name
/// for THIS deployment's media, never a storage key. The writer also stores the whole document under
/// <c>metadata.reelbolt.document</c>, so a file ReelBolt wrote re-imports losslessly (every P5 field
/// included) and a file another tool wrote still imports as cuts: each <c>Clip.1</c> whose
/// <c>target_url</c> is a reelbolt URL, or whose URL/filename matches a project file name the
/// controller supplies, becomes a <c>MediaClip</c>; a clip that resolves to nothing is reported, and
/// the import is refused with the list rather than guessed.
/// </para>
/// </remarks>
public static class TimelineInterchange
{
    public const string OtioTimelineSchema = "Timeline.1";
    public const string ReelBoltUrlScheme = "reelbolt://";

    // ── EDL ──────────────────────────────────────────────────────────────────

    public static string WriteEdl(EditTimeline document) => WriteEdl(document, null);

    /// <summary>EDL with the hand-off media naming option: <c>* SOURCE FILE:</c> carries the package-relative <c>media/&lt;name&gt;</c> path.</summary>
    public static string WriteEdl(EditTimeline document, HandOffMediaNames? names)
    {
        ArgumentNullException.ThrowIfNull(document);
        double fps = Fps(document.Settings.Rate);
        StringBuilder sb = new();
        sb.Append("TITLE: ").Append(SanitizeTitle(document.Name)).Append("\r\n");
        sb.Append("FCM: NON-DROP FRAME\r\n\r\n");

        Track? video = document.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
        if (video is null) return sb.ToString();

        int eventNumber = 0;
        IReadOnlyList<Item> items = video.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not MediaClip clip) continue;
            eventNumber++;
            Transition? incoming = i >= 2 && items[i - 1] is Transition t && items[i - 2] is MediaClip ? t : null;
            string code = incoming is null ? "C   " : $"D {(incoming.RecordOut - incoming.RecordIn).ToString("000", CultureInfo.InvariantCulture)}";
            long recordIn = incoming?.RecordIn ?? clip.RecordIn;
            long recordOut = clip.RecordOut;
            double rate = clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0;
            long sourceIn = incoming is null ? clip.SourceIn : clip.SourceIn - (long)Math.Round((incoming.RecordOut - incoming.RecordIn) * rate);
            long sourceOut = clip.SourceIn + (long)Math.Round((clip.RecordOut - clip.RecordIn) * rate);
            sb.Append(eventNumber.ToString("000", CultureInfo.InvariantCulture))
              .Append("  AX       V     ").Append(code).Append("     ")
              .Append(Timecode(Math.Max(0, sourceIn), fps)).Append(' ').Append(Timecode(sourceOut, fps)).Append(' ')
              .Append(Timecode(recordIn, fps)).Append(' ').Append(Timecode(recordOut, fps)).Append("\r\n");
            sb.Append("* FROM CLIP NAME: ").Append(SanitizeTitle(clip.Name)).Append("\r\n");
            sb.Append("* SOURCE FILE: ").Append(names?.RelativePath(clip.AssetRef) ?? AssetUrl(clip.AssetRef)).Append("\r\n");
            if (incoming is not null)
            {
                sb.Append("* EFFECT NAME: ").Append(incoming.TransitionType.ToUpperInvariant()).Append("\r\n");
            }

            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    // ── Subtitles ────────────────────────────────────────────────────────────

    public static string WriteSrt(EditTimeline document)
    {
        ArgumentNullException.ThrowIfNull(document);
        double fps = Fps(document.Settings.Rate);
        StringBuilder sb = new();
        int n = 0;
        foreach (Caption caption in Captions(document))
        {
            n++;
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(Clock(caption.RecordIn, fps, ',')).Append(" --> ").Append(Clock(caption.RecordOut, fps, ',')).Append('\n');
            sb.Append(caption.Text.Trim()).Append("\n\n");
        }

        return sb.ToString();
    }

    public static string WriteVtt(EditTimeline document)
    {
        ArgumentNullException.ThrowIfNull(document);
        double fps = Fps(document.Settings.Rate);
        StringBuilder sb = new("WEBVTT\n\n");
        foreach (Caption caption in Captions(document))
        {
            sb.Append(Clock(caption.RecordIn, fps, '.')).Append(" --> ").Append(Clock(caption.RecordOut, fps, '.')).Append('\n');
            sb.Append(caption.Text.Trim()).Append("\n\n");
        }

        return sb.ToString();
    }

    private static IEnumerable<Caption> Captions(EditTimeline document) =>
        document.Tracks.SelectMany(t => t.Items).OfType<Caption>().Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Text)).OrderBy(c => c.RecordIn).ThenBy(c => c.RecordOut);

    /// <summary>True when the transition at <paramref name="index"/> sits between two abutting media clips.</summary>
    private static bool HasClipNeighbours(IReadOnlyList<Item> items, int index) =>
        index > 0 && index + 1 < items.Count
        && items[index - 1] is MediaClip outgoing && items[index + 1] is MediaClip incoming
        && outgoing.RecordOut == items[index].RecordIn && incoming.RecordIn == items[index].RecordOut
        && items[index].RecordOut > items[index].RecordIn;

    /// <summary>
    /// How far the clip at <paramref name="index"/> extends in OTIO beyond its own v2 span: its tail
    /// over the first half of a following transition, its head over the second half of a preceding one.
    /// </summary>
    private static (long Tail, long Head) TransitionNeighbourExtents(IReadOnlyList<Item> items, int index)
    {
        if (items[index] is not MediaClip) return (0, 0);
        long tail = 0, head = 0;
        if (index + 1 < items.Count && items[index + 1] is Transition next && HasClipNeighbours(items, index + 1))
            tail = (next.RecordOut - next.RecordIn) / 2;
        if (index > 0 && items[index - 1] is Transition previous && HasClipNeighbours(items, index - 1))
        {
            long span = previous.RecordOut - previous.RecordIn;
            head = span - span / 2;
        }
        return (tail, head);
    }

    // ── OTIO write ───────────────────────────────────────────────────────────

    public static string WriteOtio(EditTimeline document) => WriteOtio(document, null);

    /// <summary>
    /// OTIO with the hand-off media naming option: <c>target_url</c> is the package-relative <c>media/&lt;name&gt;</c>, the
    /// reference gains <c>available_range</c> when the duration is known, and the <c>reelbolt://</c> URL (plus the stored
    /// size) moves into the reference's <c>metadata.reelbolt</c>. Without the option the output is unchanged.
    /// </summary>
    public static string WriteOtio(EditTimeline document, HandOffMediaNames? names)
    {
        ArgumentNullException.ThrowIfNull(document);
        double fps = Fps(document.Settings.Rate);
        JsonArray tracks = [];
        foreach (Track track in document.Tracks)
        {
            JsonArray children = [];
            long cursor = 0;
            IReadOnlyList<Item> items = track.Items;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item.RecordIn > cursor)
                {
                    children.Add(new JsonObject
                    {
                        ["OTIO_SCHEMA"] = "Gap.1",
                        ["name"] = "",
                        ["source_range"] = TimeRange(0, item.RecordIn - cursor, fps)
                    });
                }

                // OTIO's transition OVERLAPS the two clips around a cut (in_offset into the outgoing
                // clip, out_offset into the incoming one), where the v2 model gives it a span of its
                // own. The two agree once the outgoing clip is extended by in_offset and the incoming
                // clip starts out_offset earlier — the inverse of what ReadOtio trims back off.
                (long extendTail, long extendHead) = TransitionNeighbourExtents(items, i);
                switch (item)
                {
                    case MediaClip clip:
                        {
                            double rate = clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0;
                            long recordFrames = clip.RecordOut - clip.RecordIn + extendTail + extendHead;
                            long sourceDuration = (long)Math.Round(recordFrames * rate);
                            long sourceStart = Math.Max(0, clip.SourceIn - (long)Math.Round(extendHead * rate));
                            children.Add(new JsonObject
                            {
                                ["OTIO_SCHEMA"] = "Clip.1",
                                ["name"] = clip.Name,
                                ["source_range"] = TimeRange(sourceStart, sourceDuration, fps),
                                ["media_reference"] = MediaReference(clip, names, fps),
                                ["metadata"] = new JsonObject { ["reelbolt"] = new JsonObject { ["itemId"] = clip.Id.ToString("D") } }
                            });
                            break;
                        }
                    case Transition transition when HasClipNeighbours(items, i):
                        {
                            long span = transition.RecordOut - transition.RecordIn;
                            long before = span / 2;
                            children.Add(new JsonObject
                            {
                                ["OTIO_SCHEMA"] = "Transition.1",
                                ["name"] = transition.Name,
                                ["transition_type"] = "SMPTE_Dissolve",
                                ["in_offset"] = RationalTime(before, fps),
                                ["out_offset"] = RationalTime(span - before, fps),
                                ["metadata"] = new JsonObject { ["reelbolt"] = new JsonObject { ["transitionType"] = transition.TransitionType, ["easing"] = transition.Easing } }
                            });
                            break;
                        }
                    default:
                        // Titles, captions, generators, inserts and a transition without two media
                        // neighbours have no OTIO clip; they ride in the reelbolt document block and
                        // show as a gap to other tools.
                        children.Add(new JsonObject
                        {
                            ["OTIO_SCHEMA"] = "Gap.1",
                            ["name"] = item.Name,
                            ["source_range"] = TimeRange(0, item.RecordOut - item.RecordIn, fps),
                            ["metadata"] = new JsonObject { ["reelbolt"] = new JsonObject { ["itemType"] = item.GetType().Name } }
                        });
                        break;
                }

                cursor = item.RecordOut;
            }

            tracks.Add(new JsonObject
            {
                ["OTIO_SCHEMA"] = "Track.1",
                ["name"] = track.Name,
                ["kind"] = track.Kind == TrackKind.Video ? "Video" : "Audio",
                ["children"] = children,
                ["metadata"] = new JsonObject { ["reelbolt"] = new JsonObject { ["trackId"] = track.Id.ToString("D") } }
            });
        }

        JsonObject timeline = new()
        {
            ["OTIO_SCHEMA"] = OtioTimelineSchema,
            ["name"] = document.Name,
            ["global_start_time"] = RationalTime(document.Settings.StartTimecodeFrames, fps),
            ["tracks"] = new JsonObject { ["OTIO_SCHEMA"] = "Stack.1", ["name"] = "tracks", ["children"] = tracks },
            ["metadata"] = new JsonObject
            {
                ["reelbolt"] = new JsonObject
                {
                    ["schemaVersion"] = document.SchemaVersion,
                    ["document"] = JsonNode.Parse(JsonSerializer.Serialize(document, DocumentJson))
                }
            }
        };

        return timeline.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject MediaReference(MediaClip clip, HandOffMediaNames? names, double fps)
    {
        HandOffMediaEntry? entry = names?.Find(clip.AssetRef);
        if (entry is null)
        {
            return new JsonObject
            {
                ["OTIO_SCHEMA"] = "ExternalReference.1",
                ["target_url"] = AssetUrl(clip.AssetRef),
                ["name"] = clip.Name
            };
        }

        JsonObject reelbolt = new() { ["assetUrl"] = AssetUrl(clip.AssetRef) };
        if (entry.SizeBytes is { } size) reelbolt["sizeBytes"] = size;
        JsonObject reference = new()
        {
            ["OTIO_SCHEMA"] = "ExternalReference.1",
            ["target_url"] = names!.RelativeUrl(clip.AssetRef),
            ["name"] = entry.SafeName
        };
        if (entry.DurationSec is { } seconds && seconds > 0)
            reference["available_range"] = TimeRange(0, Math.Max(1, (long)Math.Round(seconds * fps)), fps);
        reference["metadata"] = new JsonObject { ["reelbolt"] = reelbolt };
        return reference;
    }

    // ── OTIO read ────────────────────────────────────────────────────────────

    /// <summary>The outcome of an OTIO read: a document, or the reasons it could not be one.</summary>
    public sealed record OtioImportResult(EditTimeline? Document, IReadOnlyList<string> Errors, IReadOnlyList<string> UnresolvedUrls, bool FromReelBoltMetadata, IReadOnlyList<OfflineClip>? Offline = null, IReadOnlyList<MatchedMedia>? Matched = null, IReadOnlyList<AmbiguousMedia>? Ambiguous = null)
    {
        /// <summary>R12.3: what matched and how (one entry per distinct file).</summary>
        public IReadOnlyList<MatchedMedia> MatchedMedia => Matched ?? [];

        /// <summary>R12.3: names several project files share, left offline rather than guessed.</summary>
        public IReadOnlyList<AmbiguousMedia> AmbiguousMedia => Ambiguous ?? [];

        /// <summary>The clips whose media is missing here (lenient import only): left as a hole with a timeline marker.</summary>
        public IReadOnlyList<OfflineClip> OfflineClips => Offline ?? [];

        public bool Succeeded => Document is not null && Errors.Count == 0 && UnresolvedUrls.Count == 0;
    }

    /// <summary>
    /// A clip an import could not resolve: <see cref="ItemId"/> is the id of the timeline marker left at its position;
    /// <see cref="Reason"/> is <c>notFound</c>, <c>ambiguous</c> or <c>durationMismatch</c>, <see cref="Message"/> its plain sentence.
    /// </summary>
    public sealed record OfflineClip(Guid ItemId, string Name, string Reason, string? Message = null);

    /// <summary>
    /// Reads an OTIO timeline. <paramref name="resolveUrl"/> maps a <c>target_url</c> (any form) to
    /// an <see cref="AssetRef"/> inside the importing project, or null. The returned document is
    /// NOT yet validated — the caller runs <see cref="EditTimelineValidator"/> as it does for every
    /// other write.
    /// </summary>
    public static OtioImportResult ReadOtio(JsonElement root, Guid projectId, Guid timelineId, string name, Func<string, AssetRef?> resolveUrl, bool allowOffline = false)
    {
        ArgumentNullException.ThrowIfNull(resolveUrl);
        return ReadOtio(root, projectId, timelineId, name, query =>
        {
            AssetRef? found = query.Url is null ? null : resolveUrl(query.Url);
            return found is null ? MediaMatchResult.Offline("notFound") : MediaMatchResult.Matched(found, "name", query.ClipName);
        }, allowOffline);
    }

    /// <summary>
    /// R12.3: reads an OTIO timeline resolving each media reference through <paramref name="resolveMedia"/>, which sees the
    /// reference's URL, stated duration and size and answers with the project asset and how it matched, or why it did not.
    /// </summary>
    public static OtioImportResult ReadOtio(JsonElement root, Guid projectId, Guid timelineId, string name, Func<MediaMatchQuery, MediaMatchResult> resolveMedia, bool allowOffline = false)
    {
        ArgumentNullException.ThrowIfNull(resolveMedia);
        List<MatchedMedia> matched = [];
        List<AmbiguousMedia> ambiguous = [];
        List<string> errors = [];
        List<OfflineClip> offline = [];
        List<Marker> offlineMarkers = [];
        List<string> unresolved = [];

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new OtioImportResult(null, ["the OTIO file is not a JSON object"], unresolved, false);
        }

        if (!root.TryGetProperty("OTIO_SCHEMA", out JsonElement schema) || schema.ValueKind != JsonValueKind.String || !schema.GetString()!.StartsWith("Timeline.", StringComparison.Ordinal))
        {
            errors.Add("the root OTIO_SCHEMA is not a Timeline");
            return new OtioImportResult(null, errors, unresolved, false);
        }

        // A file ReelBolt wrote carries the whole document: restore it, re-stamped to this project.
        if (root.TryGetProperty("metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("reelbolt", out JsonElement reelbolt) && reelbolt.ValueKind == JsonValueKind.Object &&
            reelbolt.TryGetProperty("document", out JsonElement embedded) && embedded.ValueKind == JsonValueKind.Object)
        {
            try
            {
                EditTimeline? stored = JsonSerializer.Deserialize<EditTimeline>(embedded.GetRawText(), DocumentJson);
                if (stored is not null)
                {
                    // Every asset the embedded document names still has to resolve HERE: a file
                    // exported from another project (or deployment) names media this project lacks.
                    // An id that does not resolve gets a second chance by the file name the OTIO track clip
                    // carries (a hand-off package imported into a project holding the same files).
                    double embeddedFps = Fps(stored.Settings.Rate);
                    Dictionary<Guid, JsonElement> otioClips = IndexOtioClips(root);
                    Dictionary<AssetRef, MediaMatchResult> results = [];
                    foreach (IGrouping<AssetRef, MediaClip> group in stored.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().GroupBy(c => c.AssetRef))
                    {
                        MediaClip first = group.First();
                        MediaMatchResult result = resolveMedia(new MediaMatchQuery(AssetUrl(group.Key), first.Name, FrameSeconds: 1.0 / embeddedFps));
                        if (result.Asset is null)
                        {
                            foreach (MediaClip user in group)
                            {
                                if (!otioClips.TryGetValue(user.Id, out JsonElement otioClip)) continue;
                                MediaMatchResult byName = resolveMedia(QueryFor(otioClip, user.Name, embeddedFps));
                                if (byName.Asset is not null || byName.Reason is "ambiguous" or "durationMismatch") { result = byName; break; }
                            }
                        }

                        results[group.Key] = result;
                        if (result.Asset is { } resolvedAsset) AddMatched(matched, result, first.Name, resolvedAsset);
                        else if (result.Reason == "ambiguous" && result.Candidates is { Count: > 0 }) ambiguous.Add(new AmbiguousMedia(first.Name, result.Candidates));
                    }

                    HashSet<AssetRef> missing = results.Where(r => r.Value.Asset is null).Select(r => r.Key).ToHashSet();
                    Dictionary<AssetRef, AssetRef> remap = results.Where(r => r.Value.Asset is not null && r.Value.Asset != r.Key).ToDictionary(r => r.Key, r => r.Value.Asset!);

                    EditTimeline restored = stored with { Id = timelineId, ProjectId = projectId, Name = name };
                    if (remap.Count > 0)
                    {
                        restored = RemapAssets(restored, remap);
                    }

                    if (missing.Count > 0 && allowOffline)
                    {
                        // The document has no offline clip: drop each unresolvable clip (its time stays a hole) and mark it.
                        List<Marker> markers = [.. restored.Markers];
                        List<Track> kept = [];
                        foreach (Track track in restored.Tracks)
                        {
                            List<Item> items = [];
                            foreach (Item item in track.Items)
                            {
                                if (item is MediaClip gone && missing.Contains(gone.AssetRef))
                                {
                                    string label = string.IsNullOrWhiteSpace(gone.Name) ? "Clip" : gone.Name;
                                    string reason = results[gone.AssetRef].Reason ?? "notFound";
                                    offline.Add(new OfflineClip(gone.Id, label, reason, MediaMatchResult.MessageFor(reason)));
                                    markers.Add(new Marker(gone.Id, gone.RecordIn, "Offline: " + label, "red", "Media missing. Place the file here to relink.", Math.Max(1, gone.RecordOut - gone.RecordIn)));
                                }
                                else items.Add(item);
                            }

                            kept.Add(track with { Items = items });
                        }

                        restored = restored with { Tracks = kept, Markers = markers };
                    }
                    else
                    {
                        foreach (AssetRef reference in missing) unresolved.Add(AssetUrl(reference));
                    }

                    return new OtioImportResult(unresolved.Count == 0 ? restored : null, errors, unresolved.Distinct().ToList(), true, offline, matched, ambiguous);
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"the embedded reelbolt document could not be read: {ex.Message}");
                return new OtioImportResult(null, errors, unresolved, true);
            }
        }

        double fps = ReadRate(root);
        List<Track> tracks = [];
        if (!root.TryGetProperty("tracks", out JsonElement stack) || stack.ValueKind != JsonValueKind.Object ||
            !stack.TryGetProperty("children", out JsonElement trackList) || trackList.ValueKind != JsonValueKind.Array)
        {
            errors.Add("the timeline has no tracks stack");
            return new OtioImportResult(null, errors, unresolved, false);
        }

        int trackIndex = 0;
        foreach (JsonElement trackEl in trackList.EnumerateArray())
        {
            if (trackEl.ValueKind != JsonValueKind.Object) continue;
            string kindWord = trackEl.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "Video";
            TrackKind kind = kindWord.Equals("Audio", StringComparison.OrdinalIgnoreCase) ? TrackKind.Audio : TrackKind.Video;
            string trackName = trackEl.TryGetProperty("name", out JsonElement tn) && tn.ValueKind == JsonValueKind.String && tn.GetString()!.Length > 0 ? tn.GetString()! : (kind == TrackKind.Video ? $"V{trackIndex + 1}" : $"A{trackIndex + 1}");
            List<Item> items = [];
            long cursor = 0;
            MediaClip? previousClip = null;
            long pendingTransition = 0, pendingIn = 0, pendingOut = 0;
            if (trackEl.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    if (child.ValueKind != JsonValueKind.Object) continue;
                    string childSchema = child.TryGetProperty("OTIO_SCHEMA", out JsonElement cs) && cs.ValueKind == JsonValueKind.String ? cs.GetString()! : "";
                    if (childSchema.StartsWith("Gap.", StringComparison.Ordinal))
                    {
                        cursor += ReadDurationFrames(child, fps);
                        previousClip = null;
                        pendingTransition = 0;
                        continue;
                    }

                    if (childSchema.StartsWith("Transition.", StringComparison.Ordinal))
                    {
                        pendingIn = Math.Max(0, child.TryGetProperty("in_offset", out JsonElement io) ? ReadRationalFrames(io, fps) : 0);
                        pendingOut = Math.Max(0, child.TryGetProperty("out_offset", out JsonElement oo) ? ReadRationalFrames(oo, fps) : 0);
                        pendingTransition = pendingIn + pendingOut;
                        continue;
                    }

                    if (!childSchema.StartsWith("Clip.", StringComparison.Ordinal)) continue;

                    long duration = ReadDurationFrames(child, fps);
                    long sourceIn = child.TryGetProperty("source_range", out JsonElement sr) && sr.ValueKind == JsonValueKind.Object && sr.TryGetProperty("start_time", out JsonElement st) ? ReadRationalFrames(st, fps) : 0;
                    string clipName = child.TryGetProperty("name", out JsonElement cn) && cn.ValueKind == JsonValueKind.String ? cn.GetString()! : "Clip";
                    string? url = ReadTargetUrl(child);
                    MediaMatchResult match = url is null ? MediaMatchResult.Offline("notFound") : resolveMedia(QueryFor(child, clipName, fps));
                    AssetRef? asset = match.Asset;
                    if (asset is null && allowOffline)
                    {
                        Guid offlineId = DeterministicId(timelineId, trackIndex, items.Count + offline.Count + 1000, "offline");
                        string reason = match.Reason ?? "notFound";
                        offline.Add(new OfflineClip(offlineId, clipName, reason, url is null ? "This clip has no media reference." : MediaMatchResult.MessageFor(reason)));
                        if (reason == "ambiguous" && match.Candidates is { Count: > 0 } && ambiguous.All(a => !string.Equals(a.Name, ImportMediaMatcher.LeafName(url!), StringComparison.OrdinalIgnoreCase)))
                            ambiguous.Add(new AmbiguousMedia(ImportMediaMatcher.LeafName(url!), match.Candidates));
                        offlineMarkers.Add(new Marker(offlineId, cursor, "Offline: " + clipName, "red", "Media missing. Place the file here to relink.", Math.Max(1, duration)));
                        cursor += duration;
                        previousClip = null;
                        pendingTransition = 0;
                        continue;
                    }

                    if (asset is null)
                    {
                        unresolved.Add(url ?? $"(clip '{clipName}' has no media reference)");
                        cursor += duration;
                        previousClip = null;
                        pendingTransition = 0;
                        continue;
                    }

                    AddMatched(matched, match, clipName, asset);

                    if (duration <= 0) duration = 1;

                    // An OTIO transition sits between two clips and overlaps both (in_offset into the
                    // outgoing clip, out_offset into the incoming one); the v2 model gives it its own
                    // span by trimming exactly those offsets back off the two clips.
                    if (pendingTransition > 0 && previousClip is not null && items.Count > 0 && previousClip.RecordOut == cursor)
                    {
                        long before = Math.Min(pendingIn, previousClip.RecordOut - previousClip.RecordIn - 1);
                        long after = Math.Min(pendingOut, Math.Max(0, duration - 1));
                        if (before + after > 0)
                        {
                            MediaClip trimmed = new(previousClip.Id, previousClip.Name, previousClip.RecordIn, cursor - before, previousClip.AssetRef, previousClip.SourceIn, previousClip.PlaybackRate, previousClip.Enabled, previousClip.Provenance);
                            items[^1] = trimmed;
                            items.Add(new Transition(DeterministicId(timelineId, trackIndex, items.Count, "transition"), "Dissolve", cursor - before, cursor + after, "dissolve"));
                            MediaClip incoming = new(DeterministicId(timelineId, trackIndex, items.Count, "clip"), clipName, cursor + after, cursor + duration, asset, Math.Max(0, sourceIn + after), 1.0, true, new Provenance(ProvenanceOrigin.Imported));
                            items.Add(incoming);
                            previousClip = incoming;
                            cursor += duration;
                            pendingTransition = 0;
                            continue;
                        }
                    }

                    MediaClip imported = new(DeterministicId(timelineId, trackIndex, items.Count, "clip"), clipName, cursor, cursor + duration, asset, Math.Max(0, sourceIn), 1.0, true, new Provenance(ProvenanceOrigin.Imported));
                    items.Add(imported);
                    previousClip = imported;
                    cursor += duration;
                    pendingTransition = 0;
                }
            }

            tracks.Add(new Track(DeterministicId(timelineId, trackIndex, -1, "track"), kind, trackName, null, kind == TrackKind.Video ? 100 : 64, false, true, false, false, null, items));
            trackIndex++;
        }

        if (tracks.Count == 0)
        {
            errors.Add("the timeline has no Video or Audio tracks");
        }

        Rational rate = RateOf(fps);
        EditTimeline document = new(
            id: timelineId,
            projectId: projectId,
            name: name,
            settings: new EditTimelineSettings(1920, 1080, rate, 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks: tracks,
            markers: offlineMarkers);

        return new OtioImportResult(errors.Count == 0 && unresolved.Count == 0 ? document : null, errors, unresolved.Distinct().ToList(), false, offline, matched, ambiguous);
    }

    /// <summary>The reelbolt URL of an asset reference — the one spelling both directions agree on.</summary>
    public static string AssetUrl(AssetRef reference) => reference.Kind switch
    {
        AssetRefKind.ProjectFile => $"{ReelBoltUrlScheme}project-file/{reference.ProjectFileId:D}",
        _ => $"{ReelBoltUrlScheme}step-result/{reference.StepResultId:D}"
    };

    /// <summary>Parses a reelbolt URL back to an asset reference (null for any other URL).</summary>
    public static AssetRef? TryParseAssetUrl(string? url)
    {
        if (url is null || !url.StartsWith(ReelBoltUrlScheme, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = url[ReelBoltUrlScheme.Length..];
        if (rest.StartsWith("project-file/", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(rest["project-file/".Length..], out Guid fileId))
            return new AssetRef(AssetRefKind.ProjectFile, fileId);
        if (rest.StartsWith("step-result/", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(rest["step-result/".Length..], out Guid stepId))
            return new AssetRef(AssetRefKind.StepResult, StepResultId: stepId);
        return null;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions DocumentJson = EditTimelineJson.Options;

    private static double Fps(Rational rate) => rate.Den == 0 ? 30.0 : rate.Num / (double)rate.Den;

    private static Rational RateOf(double fps)
    {
        if (Math.Abs(fps - 29.97) < 0.01) return new Rational(30000, 1001);
        if (Math.Abs(fps - 23.976) < 0.01) return new Rational(24000, 1001);
        if (Math.Abs(fps - 59.94) < 0.01) return new Rational(60000, 1001);
        int whole = (int)Math.Round(fps);
        return new Rational(Math.Max(1, whole), 1);
    }

    private static string Timecode(long frames, double fps)
    {
        int rate = Math.Max(1, (int)Math.Round(fps));
        long f = frames % rate;
        long totalSeconds = frames / rate;
        return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}:{f:00}";
    }

    private static string Clock(long frames, double fps, char separator)
    {
        double seconds = frames / fps;
        long ms = (long)Math.Round(seconds * 1000);
        long h = ms / 3_600_000;
        long m = ms / 60_000 % 60;
        long s = ms / 1000 % 60;
        long rem = ms % 1000;
        return $"{h:00}:{m:00}:{s:00}{separator}{rem:000}";
    }

    private static string SanitizeTitle(string? text)
    {
        StringBuilder sb = new();
        foreach (char c in text ?? string.Empty)
        {
            if (c is '\r' or '\n') continue;
            sb.Append(c);
        }

        string value = sb.ToString().Trim();
        return value.Length == 0 ? "Untitled" : value;
    }

    private static JsonObject RationalTime(long value, double fps) => new()
    {
        ["OTIO_SCHEMA"] = "RationalTime.1",
        ["rate"] = fps,
        ["value"] = value
    };

    private static JsonObject TimeRange(long start, long duration, double fps) => new()
    {
        ["OTIO_SCHEMA"] = "TimeRange.1",
        ["start_time"] = RationalTime(start, fps),
        ["duration"] = RationalTime(duration, fps)
    };

    private static double ReadRate(JsonElement root)
    {
        if (root.TryGetProperty("global_start_time", out JsonElement gst) && gst.ValueKind == JsonValueKind.Object && gst.TryGetProperty("rate", out JsonElement r) && r.ValueKind == JsonValueKind.Number)
            return r.GetDouble() > 0 ? r.GetDouble() : 30.0;
        // Fall back to the first clip's rate.
        if (root.TryGetProperty("tracks", out JsonElement stack) && stack.TryGetProperty("children", out JsonElement tracks) && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement track in tracks.EnumerateArray())
            {
                if (!track.TryGetProperty("children", out JsonElement children) || children.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement child in children.EnumerateArray())
                {
                    if (child.TryGetProperty("source_range", out JsonElement sr) && sr.TryGetProperty("duration", out JsonElement d) && d.TryGetProperty("rate", out JsonElement dr) && dr.ValueKind == JsonValueKind.Number && dr.GetDouble() > 0)
                        return dr.GetDouble();
                }
            }
        }

        return 30.0;
    }

    private static long ReadDurationFrames(JsonElement item, double fps)
    {
        if (item.TryGetProperty("source_range", out JsonElement sr) && sr.ValueKind == JsonValueKind.Object && sr.TryGetProperty("duration", out JsonElement d))
            return ReadRationalFrames(d, fps);
        return 0;
    }

    /// <summary>A RationalTime converted to the document's rate (an OTIO value may be at another rate).</summary>
    private static long ReadRationalFrames(JsonElement rational, double fps)
    {
        if (rational.ValueKind != JsonValueKind.Object) return 0;
        double value = rational.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        double rate = rational.TryGetProperty("rate", out JsonElement r) && r.ValueKind == JsonValueKind.Number && r.GetDouble() > 0 ? r.GetDouble() : fps;
        return (long)Math.Round(value * fps / rate);
    }

    /// <summary>Rewrites every asset reference in the document that is a key of <paramref name="remap"/> (through the document JSON: the reference type is immutable).</summary>
    private static EditTimeline RemapAssets(EditTimeline document, Dictionary<AssetRef, AssetRef> remap)
    {
        JsonNode root = JsonSerializer.SerializeToNode(document, DocumentJson)!;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o.ContainsKey("kind") && (o.ContainsKey("projectFileId") || o.ContainsKey("stepResultId")))
                    {
                        AssetRef? current = null;
                        try { current = o.Deserialize<AssetRef>(DocumentJson); } catch (JsonException) { /* not an asset reference */ }
                        if (current is not null && remap.TryGetValue(current, out AssetRef? target))
                        {
                            JsonObject fresh = JsonSerializer.SerializeToNode(target, DocumentJson)!.AsObject();
                            o.Clear();
                            foreach (KeyValuePair<string, JsonNode?> pair in fresh.ToList())
                            {
                                fresh.Remove(pair.Key);
                                o[pair.Key] = pair.Value;
                            }
                        }

                        return;
                    }

                    foreach (KeyValuePair<string, JsonNode?> pair in o.ToList()) Walk(pair.Value);
                    break;
                case JsonArray a:
                    foreach (JsonNode? child in a.ToList()) Walk(child);
                    break;
            }
        }

        Walk(root);
        return JsonSerializer.Deserialize<EditTimeline>(root.ToJsonString(), DocumentJson) ?? document;
    }

    private static void AddMatched(List<MatchedMedia> matched, MediaMatchResult result, string fallbackName, AssetRef asset)
    {
        if (matched.Any(m => m.FileId == asset.ProjectFileId && m.StepResultId == asset.StepResultId)) return;
        matched.Add(new MatchedMedia(result.Name ?? fallbackName, asset.ProjectFileId, asset.StepResultId, result.How ?? "name"));
    }

    /// <summary>The OTIO track clips by the ReelBolt item id a hand-off wrote into their metadata.</summary>
    private static Dictionary<Guid, JsonElement> IndexOtioClips(JsonElement root)
    {
        Dictionary<Guid, JsonElement> byId = [];
        if (!root.TryGetProperty("tracks", out JsonElement stack) || stack.ValueKind != JsonValueKind.Object ||
            !stack.TryGetProperty("children", out JsonElement tracks) || tracks.ValueKind != JsonValueKind.Array) return byId;
        foreach (JsonElement track in tracks.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object || !track.TryGetProperty("children", out JsonElement children) || children.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement child in children.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object
                    && child.TryGetProperty("metadata", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("reelbolt", out JsonElement rb) && rb.ValueKind == JsonValueKind.Object
                    && rb.TryGetProperty("itemId", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idEl.GetString(), out Guid itemId))
                    byId.TryAdd(itemId, child);
            }
        }

        return byId;
    }

    /// <summary>What a clip's media reference tells the matcher: URL, the hand-off's asset URL and size, and the stated duration.</summary>
    private static MediaMatchQuery QueryFor(JsonElement clip, string clipName, double fps)
    {
        string? assetUrl = null;
        long? size = null;
        double? available = null;
        double frameSeconds = 1.0 / Math.Max(1.0, fps);
        if (clip.TryGetProperty("media_reference", out JsonElement reference) && reference.ValueKind == JsonValueKind.Object)
        {
            if (reference.TryGetProperty("metadata", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("reelbolt", out JsonElement rb) && rb.ValueKind == JsonValueKind.Object)
            {
                if (rb.TryGetProperty("assetUrl", out JsonElement au) && au.ValueKind == JsonValueKind.String) assetUrl = au.GetString();
                if (rb.TryGetProperty("sizeBytes", out JsonElement sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out long bytes)) size = bytes;
            }

            if (reference.TryGetProperty("available_range", out JsonElement range) && range.ValueKind == JsonValueKind.Object
                && range.TryGetProperty("duration", out JsonElement dur) && dur.ValueKind == JsonValueKind.Object
                && dur.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.Number)
            {
                double rate = dur.TryGetProperty("rate", out JsonElement r) && r.ValueKind == JsonValueKind.Number && r.GetDouble() > 0 ? r.GetDouble() : fps;
                double seconds = value.GetDouble() / rate;
                if (seconds > 0) { available = seconds; frameSeconds = 1.0 / rate; }
            }
        }

        return new MediaMatchQuery(ReadTargetUrl(clip), clipName, assetUrl, available, size, frameSeconds);
    }

    private static string? ReadTargetUrl(JsonElement clip)
    {
        if (clip.TryGetProperty("media_reference", out JsonElement reference) && reference.ValueKind == JsonValueKind.Object)
        {
            if (reference.TryGetProperty("target_url", out JsonElement url) && url.ValueKind == JsonValueKind.String) return url.GetString();
            if (reference.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String) return n.GetString();
        }

        return null;
    }

    private static Guid DeterministicId(Guid timelineId, int trackIndex, int itemIndex, string kind)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{timelineId:N}|{trackIndex}|{itemIndex}|{kind}"));
        byte[] guid = hash[..16];
        guid[7] = (byte)((guid[7] & 0x0F) | 0x40);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid);
    }
}
