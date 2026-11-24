using System.Globalization;
using System.Text;
using System.Xml;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing.Interchange;

/// <summary>
/// Editor v2, P9: Final Cut Pro XML (FCPXML 1.10) export of an edit timeline.
/// </summary>
/// <remarks>
/// <para>
/// What it writes: one <c>format</c> (the timeline's frame duration and size), one <c>asset</c> per
/// distinct media source (its <c>media-rep src</c> is the same <c>reelbolt://</c> URL the OTIO and EDL
/// exports use — never a storage key), and one <c>project</c> whose <c>sequence</c> spine is the FIRST
/// video track: <c>asset-clip</c> per media clip, <c>gap</c> per hole, and a <c>transition</c> per
/// transition item. Every other enabled media clip (higher video tracks, audio tracks) is a CONNECTED
/// clip on the spine element it starts over, on lane +n for the n-th further video track and −n for
/// the n-th audio track. Timeline markers become <c>marker</c>s on the spine element they fall in.
/// </para>
/// <para>
/// Times are exact rationals of the timeline rate — <c>frames × den / num s</c> — so nothing is
/// rounded through seconds. Asset durations are the furthest source frame any clip reads (the real
/// file length is not part of the document). Titles, captions, generators, inserts, adjustment clips,
/// grades, keyframes and region effects (blur, pixelate, ... through a segmentation mask) are NOT exported (OTIO carries the lossless ReelBolt document); compound and
/// multicam clips are exported flattened, as the renderer plays them. The output has not been opened
/// in Final Cut Pro from this host; its structure is pinned by tests against the FCPXML 1.10 element
/// rules (spine contiguity, connected-clip offsets in the parent's time base).
/// </para>
/// </remarks>
/// <summary>
/// Export flags. <see cref="IncludeAudio"/>: write the audio-track connected clips (and the assets' audio flag).
/// <see cref="Roles"/>: write the <c>audioRole</c> attribute on connected audio clips. The FCPXML version is fixed at 1.10.
/// </summary>
public sealed record FcpxmlOptions(bool IncludeAudio = true, bool Roles = true, HandOffMediaNames? HandOff = null);

public static class FcpxmlWriter
{
    public static string Write(EditTimeline document) => Write(document, new FcpxmlOptions());

    public static string Write(EditTimeline document, FcpxmlOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        EditTimeline flat = TimelineFlattener.Flatten(document);
        Rational rate = flat.Settings.Rate.Num > 0 && flat.Settings.Rate.Den > 0 ? flat.Settings.Rate : new Rational(30, 1);

        List<Track> videoTracks = flat.Tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        List<Track> audioTracks = flat.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        Track? primary = videoTracks.FirstOrDefault();

        // Assets: one per distinct source, duration = the furthest source frame read.
        Dictionary<string, (string Id, string Name, long Duration)> assets = new(StringComparer.Ordinal);
        foreach (MediaClip clip in flat.Tracks.SelectMany(t => t.Items).OfType<MediaClip>())
        {
            string url = TimelineInterchange.AssetUrl(clip.AssetRef);
            long extent = clip.SourceIn + SourceLength(clip);
            if (assets.TryGetValue(url, out var existing)) assets[url] = existing with { Duration = Math.Max(existing.Duration, extent) };
            else assets[url] = ($"r{assets.Count + 2}", string.IsNullOrWhiteSpace(clip.Name) ? "Clip" : clip.Name, extent);
        }

        // Hand-off naming: the media-rep src is the package-relative media/<name> (percent-encoded), keyed by the same url.
        Dictionary<string, string> handOffSources = new(StringComparer.Ordinal);
        if (options.HandOff is { } handOff)
        {
            foreach (MediaClip clip in flat.Tracks.SelectMany(t => t.Items).OfType<MediaClip>())
            {
                if (handOff.RelativeUrl(clip.AssetRef) is { } relativeUrl) handOffSources[TimelineInterchange.AssetUrl(clip.AssetRef)] = relativeUrl;
            }
        }

        long end = flat.Tracks.SelectMany(t => t.Items).Select(i => i.RecordOut).DefaultIfEmpty(0).Max();

        StringBuilder sb = new();
        XmlWriterSettings settings = new() { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true, NewLineChars = "\n" };
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE fcpxml>\n\n");
        using (XmlWriter xml = XmlWriter.Create(sb, settings))
        {
            xml.WriteStartElement("fcpxml");
            xml.WriteAttributeString("version", "1.10");

            xml.WriteStartElement("resources");
            xml.WriteStartElement("format");
            xml.WriteAttributeString("id", "r1");
            xml.WriteAttributeString("frameDuration", Time(1, rate));
            xml.WriteAttributeString("width", flat.Settings.Width.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("height", flat.Settings.Height.ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();
            foreach ((string url, (string id, string name, long duration)) in assets)
            {
                xml.WriteStartElement("asset");
                xml.WriteAttributeString("id", id);
                xml.WriteAttributeString("name", name);
                xml.WriteAttributeString("start", "0s");
                xml.WriteAttributeString("duration", Time(Math.Max(1, duration), rate));
                xml.WriteAttributeString("hasVideo", "1");
                xml.WriteAttributeString("hasAudio", options.IncludeAudio ? "1" : "0");
                xml.WriteAttributeString("format", "r1");
                xml.WriteStartElement("media-rep");
                xml.WriteAttributeString("kind", "original-media");
                xml.WriteAttributeString("src", handOffSources.TryGetValue(url, out string? relative) ? relative : url);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }

            xml.WriteEndElement(); // resources

            xml.WriteStartElement("library");
            xml.WriteStartElement("event");
            xml.WriteAttributeString("name", "ReelBolt");
            xml.WriteStartElement("project");
            xml.WriteAttributeString("name", string.IsNullOrWhiteSpace(flat.Name) ? "Timeline" : flat.Name);
            xml.WriteStartElement("sequence");
            xml.WriteAttributeString("format", "r1");
            xml.WriteAttributeString("duration", Time(end, rate));
            xml.WriteAttributeString("tcStart", Time(flat.Settings.StartTimecodeFrames, rate));
            xml.WriteAttributeString("tcFormat", "NDF");
            xml.WriteAttributeString("audioLayout", "stereo");
            xml.WriteAttributeString("audioRate", flat.Settings.SampleRate >= 48000 ? "48k" : "44.1k");
            xml.WriteStartElement("spine");

            // The spine: contiguous elements covering [0, end).
            List<SpineElement> spine = BuildSpine(primary, end);
            List<(MediaClip Clip, int Lane)> connected = [];
            for (int v = 1; v < videoTracks.Count; v++)
            {
                if (!videoTracks[v].Enabled) continue;
                connected.AddRange(videoTracks[v].Items.OfType<MediaClip>().Where(c => c.Enabled).Select(c => (c, v)));
            }

            for (int a = 0; a < audioTracks.Count && options.IncludeAudio; a++)
            {
                if (!audioTracks[a].Enabled || audioTracks[a].Mute) continue;
                connected.AddRange(audioTracks[a].Items.OfType<MediaClip>().Where(c => c.Enabled).Select(c => (c, -(a + 1))));
            }

            foreach (SpineElement element in spine)
            {
                switch (element.Item)
                {
                    case MediaClip clip:
                        xml.WriteStartElement("asset-clip");
                        xml.WriteAttributeString("ref", assets[TimelineInterchange.AssetUrl(clip.AssetRef)].Id);
                        xml.WriteAttributeString("name", string.IsNullOrWhiteSpace(clip.Name) ? "Clip" : clip.Name);
                        xml.WriteAttributeString("offset", Time(element.Start, rate));
                        xml.WriteAttributeString("start", Time(clip.SourceIn, rate));
                        xml.WriteAttributeString("duration", Time(element.End - element.Start, rate));
                        if (!clip.Enabled) xml.WriteAttributeString("enabled", "0");
                        break;
                    case Transition transition:
                        xml.WriteStartElement("transition");
                        xml.WriteAttributeString("name", TransitionName(transition.TransitionType));
                        xml.WriteAttributeString("offset", Time(element.Start, rate));
                        xml.WriteAttributeString("duration", Time(element.End - element.Start, rate));
                        break;
                    default:
                        xml.WriteStartElement("gap");
                        xml.WriteAttributeString("name", "Gap");
                        xml.WriteAttributeString("offset", Time(element.Start, rate));
                        xml.WriteAttributeString("start", "0s");
                        xml.WriteAttributeString("duration", Time(element.End - element.Start, rate));
                        break;
                }

                if (element.Item is not Transition)
                {
                    // The parent's local time base: a clip's source start, a gap's 0.
                    long parentBase = element.Item is MediaClip parentClip ? parentClip.SourceIn : 0;
                    foreach (Marker marker in flat.Markers.Where(m => m.Frame >= element.Start && m.Frame < element.End))
                    {
                        xml.WriteStartElement("marker");
                        xml.WriteAttributeString("start", Time(parentBase + (marker.Frame - element.Start), rate));
                        xml.WriteAttributeString("duration", Time(Math.Max(1, marker.DurationFrames ?? 1), rate));
                        xml.WriteAttributeString("value", marker.Name);
                        if (!string.IsNullOrWhiteSpace(marker.Note)) xml.WriteAttributeString("note", marker.Note);
                        xml.WriteEndElement();
                    }

                    foreach ((MediaClip clip, int lane) in connected.Where(c => c.Clip.RecordIn >= element.Start && c.Clip.RecordIn < element.End).OrderBy(c => c.Clip.RecordIn).ThenBy(c => c.Lane))
                    {
                        xml.WriteStartElement("asset-clip");
                        xml.WriteAttributeString("ref", assets[TimelineInterchange.AssetUrl(clip.AssetRef)].Id);
                        xml.WriteAttributeString("lane", lane.ToString(CultureInfo.InvariantCulture));
                        xml.WriteAttributeString("name", string.IsNullOrWhiteSpace(clip.Name) ? "Clip" : clip.Name);
                        xml.WriteAttributeString("offset", Time(parentBase + (clip.RecordIn - element.Start), rate));
                        xml.WriteAttributeString("start", Time(clip.SourceIn, rate));
                        xml.WriteAttributeString("duration", Time(clip.RecordOut - clip.RecordIn, rate));
                        if (lane < 0 && options.Roles) xml.WriteAttributeString("audioRole", "dialogue");
                        xml.WriteEndElement();
                    }
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement(); // spine
            xml.WriteEndElement(); // sequence
            xml.WriteEndElement(); // project
            xml.WriteEndElement(); // event
            xml.WriteEndElement(); // library
            xml.WriteEndElement(); // fcpxml
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>An exact rational time of <paramref name="frames"/> at <paramref name="rate"/>: <c>frames·den/num s</c>, reduced.</summary>
    public static string Time(long frames, Rational rate)
    {
        if (frames == 0) return "0s";
        long numerator = frames * rate.Den;
        long denominator = rate.Num;
        long g = Gcd(Math.Abs(numerator), denominator);
        numerator /= g;
        denominator /= g;
        return denominator == 1
            ? numerator.ToString(CultureInfo.InvariantCulture) + "s"
            : numerator.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture) + "s";
    }

    private sealed record SpineElement(long Start, long End, Item? Item);

    private static List<SpineElement> BuildSpine(Track? primary, long end)
    {
        List<SpineElement> spine = [];
        long cursor = 0;
        foreach (Item item in primary?.Items ?? [])
        {
            if (item is not (MediaClip or Transition)) continue;
            if (item.RecordIn > cursor) spine.Add(new SpineElement(cursor, item.RecordIn, null));
            if (item.RecordIn < cursor) continue; // overlap cannot occur in a valid document
            spine.Add(new SpineElement(item.RecordIn, item.RecordOut, item));
            cursor = item.RecordOut;
        }

        if (cursor < end) spine.Add(new SpineElement(cursor, end, null));
        return spine;
    }

    private static long SourceLength(MediaClip clip)
    {
        double rate = clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0;
        return clip.Hold ? 1 : (long)Math.Ceiling((clip.RecordOut - clip.RecordIn) * rate);
    }

    private static string TransitionName(string type) => type switch
    {
        "fade" or "dissolve" => "Cross Dissolve",
        "fadeblack" => "Fade To Color",
        _ => "Cross Dissolve"
    };

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
