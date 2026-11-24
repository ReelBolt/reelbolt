using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Progressive disclosure for a view that did not fit: instead of a monolithic list that stops part-way through the film, the
/// editor gets the speech outline of the WHOLE timeline (sections, gist, cut-point ids) plus the detail of the first section,
/// and reads the rest of the sections through the footage tools. Every id of the analysis is then a valid keep id, because
/// every one of them is reachable through those tools (see <see cref="Apply"/>).
/// </summary>
internal static class VideoSpeechOutlineView
{
    /// <summary>The share of the view's budget kept free for what the outline adds: the sections and the whole-film phrase list.</summary>
    internal static int ReservedChars(VideoAnalysisArtifact artifact)
    {
        if (VideoSpeechOutline.Build(artifact) is not { } outline)
            return 0;
        return outline.Nodes.ToJsonString().Length + (outline.Sources?.ToJsonString().Length ?? 0)
            + VideoSpeechOutline.PhraseList(artifact).ToJsonString().Length + ColourRows(artifact).ToJsonString().Length
            + OutlineViewIds.AllIdRanges(artifact).ToJsonString().Length + 200;
    }

    /// <summary>One line per shot that has a measured colour reading: <c>s12 temp=warm tone=mid sat=low</c>. Words only.</summary>
    internal static JsonArray ColourRows(VideoAnalysisArtifact artifact) =>
        new([.. artifact.Shots
            .Where(s => s.Visual is { ColorTemperatureClass: not null })
            .Select(s => (JsonNode)JsonValue.Create($"{s.Id} temp={s.Visual!.ColorTemperatureClass} tone={s.Visual.ToneClass ?? "-"} sat={s.Visual.SaturationClass ?? "-"}")!)]);

    /// <summary>The colour readings with consecutive shots of the same reading folded into one line: <c>s3-s9 temp=warm tone=mid sat=low</c>.</summary>
    internal static JsonArray ColourRuns(VideoAnalysisArtifact artifact)
    {
        JsonArray runs = [];
        string? first = null, last = null, key = null;
        void Flush()
        {
            if (first is not null)
                runs.Add(JsonValue.Create((first == last ? first : first + "-" + last) + " " + key));
        }

        foreach (VideoAnalysisShot s in artifact.Shots.Where(s => s.Visual is { ColorTemperatureClass: not null }))
        {
            string reading = $"temp={s.Visual!.ColorTemperatureClass} tone={s.Visual.ToneClass ?? "-"} sat={s.Visual.SaturationClass ?? "-"}";
            if (reading == key)
            {
                last = s.Id;
                continue;
            }

            Flush();
            first = last = s.Id;
            key = reading;
        }

        Flush();
        return runs;
    }

    /// <summary>
    /// Lays the outline over <paramref name="view"/> when <c>Outline</c> is on and the per-item view left items out.
    /// Returns the ids a keep may name (all shots, silences and transcript ids of the artifact, in offered order), or null
    /// when nothing changed. The artifact must carry the merged events, if any, so sections match the events outline.
    /// </summary>
    internal static List<string>? Apply(JsonObject view, JsonObject meta, VideoAnalysisArtifact artifact, VideoAnalyzeStepConfig config)
    {
        if (!config.Outline || (meta["droppedItems"]?.GetValue<int>() ?? 0) <= 0)
            return null;
        if (VideoSpeechOutline.Build(artifact) is not { } outline)
            return null;

        // One section id space: lay the speech fields over the events outline's section of the same id, else carry our own.
        if (view["outline"] is JsonObject existing && existing["sections"] is JsonArray existingSections)
        {
            foreach (JsonNode? node in outline.Nodes)
            {
                if (node is not JsonObject speech)
                    continue;
                JsonObject? target = existingSections.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.GetValue<string>() == speech["id"]?.GetValue<string>());
                if (target is null)
                    continue;
                foreach (KeyValuePair<string, JsonNode?> kv in speech.Where(kv => kv.Key != "id"))
                    target[kv.Key] = kv.Value?.DeepClone();
            }

            existing["speechNote"] = VideoEventReader.UntrustedNote;
            if (outline.Sources is not null)
                existing["sources"] = outline.Sources.DeepClone();
        }
        else
        {
            view["outline"] = new JsonObject
            {
                ["sections"] = outline.Nodes.DeepClone(),
                ["speechNote"] = VideoEventReader.UntrustedNote
            };
            if (outline.Sources is not null)
                view["outline"]!["sources"] = outline.Sources.DeepClone();
        }

        // Rows past the first section are removed first, so the size checks below measure the view as it will be sent.
        Dictionary<int, double> offsets = VideoSpeechOutline.Offsets(artifact, out double total);
        double firstEnd = outline.Sections[0].EndSec;
        Dictionary<string, double> startById = new(StringComparer.Ordinal);
        foreach (VideoAnalysisShot s in artifact.Shots) startById[s.Id] = offsets.GetValueOrDefault(s.SourceIndex) + s.StartSec;
        foreach (VideoAnalysisSilenceSpan s in artifact.SilenceSpans) startById[s.Id] = offsets.GetValueOrDefault(s.SourceIndex) + s.StartSec;
        foreach (VideoAnalysisSegment s in artifact.Segments) startById[s.Id] = offsets.GetValueOrDefault(s.SourceIndex) + s.StartSec;

        int kept = 0;
        foreach (string array in new[] { "shots", "silences", "segments" })
        {
            if (view[array] is not JsonArray rows)
                continue;
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                string? id = rows[i]?["id"]?.GetValue<string>();
                if (id is not null && startById.TryGetValue(id, out double at) && at >= firstEnd)
                    rows.RemoveAt(i);
                else
                    kept++;
            }
        }

        // Shot ids that are no longer rows (past the first section) are valid keeps too: listed as ranges, tiny.
        JsonObject outlineNode = (JsonObject)view["outline"]!;
        outlineNode["shots"] = OutlineViewIds.ShotRanges(artifact);

        // The whole film's phrases and a colour reading for every shot (the rows past the first section are removed, so the
        // colour room would grade them blind). The COMBINED view must fit the effective budget; degrade in order: phrase text
        // shrinks, then the colour rows are summarised into runs, then the phrase list is left out, then the colour too.
        int limit = meta["effectiveMaxOutputChars"]?.GetValue<int>() ?? Math.Clamp(config.MaxOutputChars, 256, 200_000);
        bool hasColour = ColourRows(artifact).Count > 0;
        bool Fits(JsonArray? colourNode, JsonArray? phrasesNode)
        {
            outlineNode.Remove("colour");
            outlineNode.Remove("phrases");
            outlineNode.Remove("phrasesNote");
            if (colourNode is not null)
                outlineNode["colour"] = colourNode;
            if (phrasesNode is not null)
            {
                outlineNode["phrases"] = phrasesNode;
                outlineNode["phrasesNote"] = "Every phrase of the whole film is listed here, in order. A keep may start or end on any listed id. Do not repeat a read call.";
            }

            return view.ToJsonString().Length <= limit;
        }

        int[] phraseSizes = [VideoSpeechOutline.PhraseTextChars, 40, 20, 0];
        bool placed = false;
        string? colourNote = null;
        foreach (bool summarised in hasColour ? new[] { false, true } : new[] { false })
        {
            foreach (int chars in phraseSizes)
            {
                if (Fits(hasColour ? (summarised ? ColourRuns(artifact) : ColourRows(artifact)) : null, VideoSpeechOutline.PhraseList(artifact, chars)))
                {
                    placed = true;
                    break;
                }
            }

            if (placed)
            {
                if (summarised)
                    colourNote = "Per-shot colour readings are summarised into runs of shots with the same reading, to fit.";
                break;
            }
        }

        if (!placed)
        {
            // A very long transcript: even the bare ids would pass the limit. The sections (gist, cut points) stay, the phrases
            // are left to the read tools, and the step says so.
            meta["outline_summarised"] = "The film is very long, so the phrase list was left out of the view; the outline's sections and cut points cover the whole film, and ReadVideoSection or ReadVideoTranscript return the phrases of any part.";
            if (!Fits(hasColour ? ColourRuns(artifact) : null, null))
            {
                Fits(null, null);
                if (hasColour)
                    colourNote = "Per-shot colour readings were left out of the outline for size; the first section's shots carry theirs.";
            }
            else if (hasColour)
            {
                colourNote = "Per-shot colour readings are summarised into runs of shots with the same reading, to fit.";
            }
        }

        if (colourNote is not null)
            meta["outline_summarised"] = (meta["outline_summarised"]?.GetValue<string>() is { } e ? e + " " : "") + colourNote;

        // Every id of the analysis is a valid keep id (the phrase list names the speech, the tools return the rest).
        meta["offeredIdRanges"] = OutlineViewIds.AllIdRanges(artifact);
        meta["offeredIdCount"] = artifact.Shots.Count + artifact.SilenceSpans.Count + artifact.Segments.Count;
        meta["viewItemCount"] = kept;
        meta["offeredCoverage"] = new JsonObject
        {
            ["firstSec"] = 0.0,
            ["lastSec"] = Math.Round(outline.LastSec, 2),
            ["sourceDurationSec"] = Math.Round(total, 2),
            ["mode"] = "outline"
        };
        meta["speechOutline"] = new JsonObject
        {
            ["sections"] = outline.Sections.Count,
            ["tokensEstimate"] = outline.TokensEstimate,
            ["digest"] = outline.Digest
        };
        // Said out loud when the budget forced the sections of several clips to be summarised: every clip still has its
        // own line in outline.sources, and everything is reachable through the read tools.
        if (outline.Summarised)
            meta["outline_summarised"] = (meta["outline_summarised"]?.GetValue<string>() is { } earlier ? earlier + " " : "") + "Sections of several source clips were summarised to fit; outline.sources has one line per clip and ReadVideoSection/ReadVideoTranscript return the full speech.";

        return
        [
            .. artifact.Shots.Select(s => s.Id),
            .. artifact.SilenceSpans.Select(s => s.Id),
            .. artifact.Segments.Select(s => s.Id)
        ];
    }
}
