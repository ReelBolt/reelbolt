using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Editing;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// One timed event inside a composition, in the composition's own frames: a stamp landing, a row
/// appearing, a door slamming. <see cref="Name"/> is a short machine word; <see cref="Label"/> is the
/// short prose a sound designer reads (sanitized, never a time).
/// </summary>
public sealed record RenderEvent(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("frame")] int Frame,
    [property: JsonPropertyName("label")] string Label);

/// <summary>
/// The event manifest an Author writes next to its render (<c>public/events.json</c>, or the
/// <c>DeclareRenderEvents</c> sandbox tool, which validates and writes the same file). On a successful
/// video render it is uploaded as the render's NON-playable sidecar under <c>agentFiles/</c>
/// (<see cref="SidecarKeyFor"/>), and a later analysis that sees the render — as a source or as pinned
/// insert content — offers each event to the sound designer as an opaque <c>e{n}</c> cue anchor (see
/// docs/video-editing.md "Event anchors inside a composition").
/// </summary>
/// <param name="Fps">The composition's frame rate, stamped by the render tool at upload; null in the file the Author writes.</param>
public sealed record RenderEventManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("fps")] double? Fps,
    [property: JsonPropertyName("events")] IReadOnlyList<RenderEvent> Events)
{
    public const int CurrentVersion = 1;

    /// <summary>Hard caps keep a model-written manifest bounded.</summary>
    public const int MaxEvents = 200;
    public const int MaxNameChars = 40;
    public const int MaxLabelChars = 60;
    public const int MaxFrame = 1_000_000;

    /// <summary>S-69: the largest manifest file read at all (256 KiB of text); 200 events fit in a small fraction.</summary>
    public const int MaxManifestChars = 256 * 1024;

    /// <summary>S-69: a raw label is cut to this many characters before any pattern runs on it.</summary>
    public const int MaxRawLabelChars = 1024;

    /// <summary>S-69: a backstop on each label pattern; the patterns are linear-time and the input is capped.</summary>
    private static readonly TimeSpan LabelPatternTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Where the Author writes the manifest in its sandbox, relative to the workspace root.</summary>
    public const string SandboxPath = "public/events.json";

    private static readonly Regex NamePattern = new("^[a-z0-9_-]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Removed from labels BEFORE the character allowlist: markup, and anything that reads as a time
    // ("00:03", "1.5s", "300 ms", "45 frames") — a label is prose, never a timestamp for the model.
    // S-69: NonBacktracking (linear in the input) plus a timeout, on a label already capped at MaxRawLabelChars.
    private static readonly Regex MarkupPattern = new("<[^>]*>", RegexOptions.NonBacktracking, LabelPatternTimeout);
    private static readonly Regex ClockPattern = new(@"\d+(?::\d+)+(?:\.\d+)?", RegexOptions.NonBacktracking, LabelPatternTimeout);
    private static readonly Regex DurationPattern = new(
        @"\d+(?:[.,]\d+)?\s*(?:ms|msec|millis(?:econds?)?|s|secs?|seconds?|frames?|fps|f)\b",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, LabelPatternTimeout);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Validates a declaration: 1..<see cref="MaxEvents"/> events, names <c>[a-z0-9_-]{1,40}</c>, frames
    /// <c>0..MaxFrame</c>. Labels are sanitized (an empty one falls back to the name). Returns the
    /// manifest ordered by frame, or null plus every problem found, so the Author fixes it in one go.
    /// </summary>
    public static (RenderEventManifest? Manifest, IReadOnlyList<string> Errors) Validate(IReadOnlyList<RenderEvent>? events)
    {
        if (events is null || events.Count == 0)
            return (null, ["Declare at least one event."]);
        if (events.Count > MaxEvents)
            return (null, [$"At most {MaxEvents} events may be declared, got {events.Count}."]);

        List<string> errors = [];
        List<RenderEvent> valid = [];
        for (int i = 0; i < events.Count; i++)
        {
            RenderEvent e = events[i];
            bool ok = true;
            if (e is null)
            {
                errors.Add($"Event {i + 1} is empty.");
                continue;
            }

            if (e.Name is null || !NamePattern.IsMatch(e.Name))
            {
                errors.Add($"Event {i + 1}: name must be 1-{MaxNameChars} characters of a-z, 0-9, '_' or '-'.");
                ok = false;
            }

            if (e.Frame is < 0 or > MaxFrame)
            {
                errors.Add($"Event {i + 1}: frame must be a composition frame number from 0 to {MaxFrame}.");
                ok = false;
            }

            if (ok)
            {
                string label = SanitizeLabel(e.Label);
                valid.Add(e with { Label = label.Length > 0 ? label : e.Name! });
            }
        }

        if (errors.Count > 0)
            return (null, errors);

        return (new RenderEventManifest(CurrentVersion, null, valid.OrderBy(e => e.Frame).ToList()), []);
    }

    /// <summary>
    /// Reads a manifest file — <c>{"events":[...]}</c> or a bare array — and validates it. Never throws.
    /// </summary>
    public static (RenderEventManifest? Manifest, IReadOnlyList<string> Errors) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, ["The event manifest is empty."]);
        if (json.Length > MaxManifestChars)
            return (null, [$"The event manifest is too large ({json.Length} characters; at most {MaxManifestChars})."]);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            JsonElement eventsElement;
            double? fps = null;
            if (root.ValueKind == JsonValueKind.Array)
            {
                eventsElement = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("events", out eventsElement) &&
                     eventsElement.ValueKind == JsonValueKind.Array)
            {
                if (root.TryGetProperty("fps", out JsonElement fpsElement) && fpsElement.ValueKind == JsonValueKind.Number &&
                    fpsElement.TryGetDouble(out double f) && f is > 0 and <= 240)
                {
                    fps = f;
                }
            }
            else
            {
                return (null, ["The event manifest must be {\"events\": [{name, frame, label}, ...]}."]);
            }

            List<RenderEvent>? events = eventsElement.Deserialize<List<RenderEvent>>(JsonOptions);
            (RenderEventManifest? manifest, IReadOnlyList<string> errors) = Validate(events);
            return (manifest is null ? null : manifest with { Fps = fps }, errors);
        }
        catch (JsonException ex)
        {
            return (null, ["The event manifest is not valid JSON: " + ex.Message]);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ["The event manifest could not be read: " + ex.Message]);
        }
    }

    /// <summary>The manifest as the file/sidecar JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// The storage key of a render's event sidecar: the render's <c>outputFiles/</c> key moved under
    /// <c>agentFiles/render-events/</c> (never in the playable outputs list), plus <c>.events.json</c>.
    /// QA-P1-12: a render COPY saved into <c>userFiles/</c> (<c>ApplySaveOutputAsFile</c>) keeps its
    /// events the same way, so a <c>userFiles/</c> key mirrors to its own sidecar too — the copy's own
    /// key, never the render's. Null for anything else — an upload has no sidecar.
    /// </summary>
    public static string? SidecarKeyFor(string? videoStorageKey)
    {
        if (string.IsNullOrWhiteSpace(videoStorageKey) || videoStorageKey.Contains("..", StringComparison.Ordinal))
            return null;

        string[] parts = videoStorageKey.Split('/', 4);
        if (parts.Length != 4 || parts[0] != "projects" || !Guid.TryParse(parts[1], out _) ||
            parts[2] is not ("outputFiles" or "userFiles") || parts[3].Length == 0 || parts[3].EndsWith('/'))
        {
            return null;
        }

        return $"projects/{parts[1]}/agentFiles/render-events/{parts[3]}.events.json";
    }

    /// <summary>
    /// Markup and anything time-shaped removed, then the overlay-text allowlist, capped at
    /// <see cref="MaxLabelChars"/>. Empty when nothing survives.
    /// </summary>
    public static string SanitizeLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        // S-69: capped BEFORE any pattern runs, so no label length reaches a regex.
        string text = raw.Length > MaxRawLabelChars ? raw[..MaxRawLabelChars] : raw;
        try
        {
            text = MarkupPattern.Replace(text, " ");
            text = ClockPattern.Replace(text, " ");
            text = DurationPattern.Replace(text, " ");
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty; // fails closed: the caller falls back to the event name
        }

        return OverlayTextSanitizer.Sanitize(text, MaxLabelChars);
    }
}
