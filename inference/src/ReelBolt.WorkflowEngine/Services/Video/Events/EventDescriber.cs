using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>
/// One event to describe: the id the executor asks about (never sent to the model, never read back from it), the
/// measured words for it (first-party, from <see cref="VideoEventWords"/>), and a strip of three frames, left to
/// right: just before, at, and just after the event.
/// </summary>
/// <param name="StripBytes">The strip's JPEG bytes, or null to read <paramref name="StripPath"/> off this host.</param>
public sealed record EventDescriptionRequest(
    string EventId, string TypeWord, string? What, string? RegionWord, string StripPath, byte[]? StripBytes = null);

/// <summary>
/// A picture model's answer about one event: a few words (<see cref="EventSeenText"/>) and the kind of picture
/// (<see cref="VideoEventKinds"/>). Either may be null when the model could not tell.
/// </summary>
public sealed record EventDescription(string? Seen, string? Kind);

/// <summary>
/// Says in a few words what the picture shows at one event (layer 2 of the events analysis; layer 1 measured that
/// something happened, where and how strongly, but cannot name it).
/// </summary>
/// <remarks>
/// The answer is untrusted text about a customer's video. An implementation returns the words only through
/// <see cref="EventSeenText.Sanitize"/> and the kind only through <see cref="VideoEventKinds.Parse"/>; the binding to an
/// event id is the caller's, never the model's.
/// </remarks>
public interface IEventDescriber
{
    /// <summary>The cleaned answer, or null when the model could tell nothing. Throws when the call or its answer failed.</summary>
    Task<EventDescription?> DescribeAsync(ResolvedInferenceProvider provider, EventDescriptionRequest request, CancellationToken ct);
}

/// <summary><see cref="IEventDescriber"/> over a <c>Vision</c> chat provider: one small image and one short answer per event.</summary>
public sealed class VisionEventDescriber : IEventDescriber
{
    /// <summary>
    /// Room for the answer and for a reasoning model's own thinking before it. The one figure a worst-case price of a
    /// look is sized with, so it lives beside the look's other token figures.
    /// </summary>
    private const int MaxOutputTokens = (int)ReelBolt.Shared.Metering.StepUsageProfiles.EventLookMaxOutputTokens;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<VisionEventDescriber> _logger;

    public VisionEventDescriber(IChatClientFactory chatClientFactory, ILogger<VisionEventDescriber> logger)
    {
        _chatClientFactory = chatClientFactory;
        _logger = logger;
    }

    internal sealed record Reply(
        [property: JsonPropertyName("seen")] string? Seen,
        [property: JsonPropertyName("kind")] string? Kind = null);

    public async Task<EventDescription?> DescribeAsync(ResolvedInferenceProvider provider, EventDescriptionRequest request, CancellationToken ct)
    {
        IChatClient client = _chatClientFactory.Get(provider);
        byte[] jpeg = request.StripBytes ?? await File.ReadAllBytesAsync(request.StripPath, ct).ConfigureAwait(false);

        ChatMessage message = new(ChatRole.User, new List<AIContent>
        {
            new TextContent(BuildPrompt(request)),
            new DataContent(jpeg, "image/jpeg")
        });

        ChatResponse response = await StructuredChatFallback.GetResponseAsync(
            client, [message], ChatResponseFormat.ForJsonSchema<Reply>(), MaxOutputTokens, _logger,
            StructuredChatFallback.ProviderKey(provider), ct).ConfigureAwait(false);

        string text = RobustJsonExtractor.CollapseDuplicateLeadingBrace(response.Text ?? string.Empty);
        Reply? reply = TryParse(text)
            ?? (RobustJsonExtractor.ExtractJsonObject(text) is { } extracted ? TryParse(extracted) : null)
            ?? (RobustJsonExtractor.NormalizeQuotedStrings(RobustJsonExtractor.ExtractJsonObject(text) ?? text) is { } normalized
                ? TryParse(normalized)
                : null);

        // Prose, a refusal or a cut-off answer said nothing usable about the picture: a failed call, not "nothing there".
        if (reply is null)
            throw new InvalidOperationException(
                $"The vision provider's answer for event '{request.EventId}' could not be read ({text.Length} chars).");

        return Clean(reply);
    }

    /// <summary>The reply through both gates; null when neither words nor a kind are left.</summary>
    internal static EventDescription? Clean(Reply reply)
    {
        string? seen = EventSeenText.Sanitize(reply.Seen);
        string? kind = VideoEventKinds.Parse(reply.Kind);
        return seen is null && kind is null ? null : new EventDescription(seen, kind);
    }

    /// <summary>
    /// The question. Every word in it is first-party: the type, the measured words and the region word come from closed
    /// tables in code, so nothing of the customer's video is in the text of the prompt (only in the image).
    /// </summary>
    internal static string BuildPrompt(EventDescriptionRequest request)
    {
        string measured = string.IsNullOrWhiteSpace(request.What) ? request.TypeWord.Replace('_', ' ') : request.What!;
        string where = string.IsNullOrWhiteSpace(request.RegionWord) ? "" : $" It was measured around the {request.RegionWord!.Replace('-', ' ')} of the picture.";
        return
            $$"""
              This image is three frames of one video, side by side, left to right: just before a moment, at the
              moment, and just after it. A program measured this at the moment: {{measured}}.{{where}}

              Answer two things about what is different between the frames.

              "seen": what it is, in at most ten plain English words, as a sound editor would note it: the thing
              and what it does. Examples: "photo bubble slides in from the left", "red question mark stamps on",
              "price tag pops up", "logo fades out", "man walks across the street". Name the thing you see, not
              the people's identity. Do not write any number, time, frame, quotation or text you read in the
              picture. If you cannot tell what changes, answer "unclear".

              "kind": exactly one of these three words.
              - "graphic": a designed element or page changes (a card, bubble, icon, shape, logo, chart, screen
                recording or picture panel appears, leaves, moves or changes).
              - "text": words appear or change (a title, a caption line, a label).
              - "footage": only ordinary filmed material moves (a person walking, talking or gesturing, a
                vehicle, scenery, the camera), with nothing designed appearing or changing.

              Respond with STRICT JSON ONLY, exactly {"seen": "...", "kind": "..."}: no markdown fences, no
              explanation, nothing before the first brace or after the last.
              """;
    }

    private static Reply? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<Reply>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Which events are worth a vision call and which three frames show each. Pure.
/// </summary>
public static class EventDescriptionPlan
{
    /// <summary>Width of the three-frame strip, in pixels (each frame a third of it).</summary>
    public const int StripMaxWidth = 1152;

    /// <summary>Hard ceiling on calls per step, whatever the config says.</summary>
    public const int MaxEventsCeiling = VisionLookDefaults.MaxEventLooks;

    private const double BeforeSec = 0.12;
    private const double AtSec = 0.08;
    private const double AfterSec = 0.45;
    private const double MaxAfterSec = 1.5;
    private const double MinStepSec = 0.04;

    /// <summary>
    /// True for an event a look can add to: a type the code measures but cannot name (something appears, leaves, changes,
    /// starts to move, hits, or text comes in), or a hard cut the code read as a plain cut between two filmed shots (the
    /// pixels cannot tell a filmed shot from a photo or a document shown full frame; the picture model can). Any other
    /// cut, a flash, a camera move, a caption word and a beat need no second look.
    /// </summary>
    public static bool IsDescribable(VideoAnalysisRenderEvent e)
    {
        if (e.SourceIndex is null || e.Origin != nameof(VideoEventOrigin.Pixels))
            return false;
        return VideoEventWords.ParseType(e.Type) switch
        {
            VideoEventType.Appear or VideoEventType.Disappear or VideoEventType.Change or VideoEventType.MoveStart
                or VideoEventType.Hit or VideoEventType.TextIn => true,
            VideoEventType.Transition => VideoEventKinds.Parse(e.Kind) == VideoEventKinds.Footage,
            _ => false
        };
    }

    /// <summary>
    /// True when the look decides something: nobody knows the event's kind yet, or the code's reading is the unsure one
    /// (a cut it took for plain footage).
    /// </summary>
    public static bool NeedsLook(VideoAnalysisRenderEvent e) =>
        VideoEventKinds.Parse(e.Kind) is null
        || (VideoEventWords.ParseType(e.Type) == VideoEventType.Transition && VideoEventKinds.Parse(e.Kind) == VideoEventKinds.Footage);

    /// <summary>
    /// At most <paramref name="max"/> describable events of <paramref name="offered"/>: those a look decides something for
    /// first (<see cref="NeedsLook"/>), then the strongest (ties to the earlier one), returned in the order they were
    /// offered. A <paramref name="max"/> of 0 (looks switched off) selects none.
    /// </summary>
    public static List<VideoAnalysisRenderEvent> Select(IReadOnlyList<VideoAnalysisRenderEvent> offered, int max)
    {
        if (max <= 0)
            return [];
        int cap = Math.Min(max, MaxEventsCeiling);
        HashSet<string> keep = [.. offered
            .Select((e, i) => (Event: e, Index: i))
            .Where(t => IsDescribable(t.Event))
            .OrderBy(t => NeedsLook(t.Event) ? 0 : 1)
            .ThenByDescending(t => t.Event.Strength ?? 0)
            .ThenBy(t => t.Index)
            .Take(cap)
            .Select(t => t.Event.Id)];
        return [.. offered.Where(e => keep.Contains(e.Id))];
    }

    /// <summary>
    /// The events a strip is kept for, so an agent can ask about one later (<c>LookAtMoment</c>): describable events of
    /// <paramref name="offered"/> that got no answer in this step (<paramref name="answeredIds"/>), strongest first (ties to
    /// the earlier one), at most <paramref name="max"/> and never more than <see cref="VideoEventStrips.MaxStoredStrips"/>.
    /// </summary>
    public static List<VideoAnalysisRenderEvent> SelectForStrips(
        IReadOnlyList<VideoAnalysisRenderEvent> offered, IReadOnlySet<string> answeredIds, int max = VideoEventStrips.MaxStoredStrips) =>
        [.. offered
            .Select((e, i) => (Event: e, Index: i))
            .Where(t => IsDescribable(t.Event) && VideoEventStrips.IsEventId(t.Event.Id) && !answeredIds.Contains(t.Event.Id))
            .OrderByDescending(t => t.Event.Strength ?? 0)
            .ThenBy(t => t.Index)
            .Take(Math.Clamp(max, 0, VideoEventStrips.MaxStoredStrips))
            .Select(t => t.Event)];

    /// <summary>
    /// The question about <paramref name="e"/>: the one request both the analysis step and an agent's look send. Every word
    /// in it is first-party (the type word, the measured words and the region word come from closed tables).
    /// </summary>
    public static EventDescriptionRequest RequestFor(VideoAnalysisRenderEvent e, string stripPath, byte[]? stripBytes) =>
        new(
            e.Id,
            VideoEventWords.ParseType(e.Type) is { } type ? VideoEventWords.TypeWord(type) : e.Name,
            VideoEventWords.What(e),
            e.Region is { } region ? VideoEventWords.RegionWord(region) : null,
            stripPath,
            stripBytes);

    /// <summary>
    /// The three moments of the strip, in seconds on the event's source: just before it, just into it, and once it has
    /// settled (after a spread-out event's end). Clamped to the source and strictly increasing.
    /// </summary>
    public static double[] StripTimes(VideoAnalysisRenderEvent e, double sourceDurationSec)
    {
        double at = Math.Max(0, e.ContentSec);
        double last = sourceDurationSec > 0 ? Math.Max(0, sourceDurationSec - MinStepSec) : double.MaxValue;
        double spanSec = e.EndFrame is { } end && end > e.Frame && e.Frame > 0 && e.ContentSec > 0
            ? (end - e.Frame) * (e.ContentSec / e.Frame)
            : 0;
        double before = Math.Max(0, at - BeforeSec);
        double during = Math.Min(last, at + AtSec);
        double after = Math.Min(last, at + Math.Min(MaxAfterSec, Math.Max(AfterSec, spanSec + 0.1)));
        during = Math.Max(during, Math.Min(last, before + MinStepSec));
        after = Math.Max(after, Math.Min(last, during + MinStepSec));
        return [before, during, after];
    }
}
