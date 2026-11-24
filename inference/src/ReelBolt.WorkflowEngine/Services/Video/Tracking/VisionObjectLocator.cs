using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>An axis-aligned box in 0..1 of the image it was found in.</summary>
public readonly record struct LocatedBox(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public bool IsUsable => Width > 0.002 && Height > 0.002 && X0 >= -0.01 && Y0 >= -0.01 && X1 <= 1.01 && Y1 <= 1.01;
}

/// <summary>Finds instances of a named object in a still image.</summary>
public interface IObjectLocator
{
    /// <summary>
    /// Every visible instance of <paramref name="label"/> in the JPEG at <paramref name="imagePath"/>,
    /// as boxes in 0..1 of that image. Empty when there is none.
    /// </summary>
    Task<IReadOnlyList<LocatedBox>> LocateAsync(
        ResolvedInferenceProvider provider, string imagePath, string label, CancellationToken ct);

    /// <summary>
    /// The same lookup over JPEG <b>bytes</b> the caller already holds — the arm a media workspace
    /// feeds, because a keyframe produced by a remote session (D11) exists as bytes on this host and
    /// as nothing else. It is the arm <see cref="IObjectLocator"/>'s subject-location call site uses;
    /// object tracking still passes a path, because it forces a local workspace.
    /// </summary>
    Task<IReadOnlyList<LocatedBox>> LocateAsync(
        ResolvedInferenceProvider provider, byte[] jpegBytes, string label, CancellationToken ct);
}

/// <summary>
/// <see cref="IObjectLocator"/> over a <c>Vision</c> chat provider. Coordinates are requested as
/// integers on a 0..1000 grid, which vision models report far more reliably than fractions, and
/// the reply is parsed with the same robust JSON repair the shot captioner uses. A model can only
/// ever contribute boxes: they become tracker SEEDS, every frame after the seed is measured by the
/// tracker, and the label is the workflow author's text, never the model's.
/// </summary>
public sealed class VisionObjectLocator : IObjectLocator
{
    private const int MaxObjects = 16;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<VisionObjectLocator> _logger;

    public VisionObjectLocator(IChatClientFactory chatClientFactory, ILogger<VisionObjectLocator> logger)
    {
        _chatClientFactory = chatClientFactory;
        _logger = logger;
    }

    internal sealed record LocatorReply([property: JsonPropertyName("objects")] List<LocatorBox>? Objects);

    internal sealed record LocatorBox(
        [property: JsonPropertyName("x0")] double X0,
        [property: JsonPropertyName("y0")] double Y0,
        [property: JsonPropertyName("x1")] double X1,
        [property: JsonPropertyName("y1")] double Y1);

    public async Task<IReadOnlyList<LocatedBox>> LocateAsync(
        ResolvedInferenceProvider provider, string imagePath, string label, CancellationToken ct)
    {
        byte[] jpeg = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
        return await LocateAsync(provider, jpeg, label, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LocatedBox>> LocateAsync(
        ResolvedInferenceProvider provider, byte[] jpegBytes, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jpegBytes);

        IChatClient client = _chatClientFactory.Get(provider);
        byte[] jpeg = jpegBytes;
        string safeLabel = Sanitize(label);

        string prompt =
            $"""
             Find every visible instance of: {safeLabel}

             Report each one as a tight bounding box around exactly that object (not the thing it
             is attached to), in integer coordinates on a 0..1000 grid over this image: x0,y0 is
             the top-left corner and x1,y1 the bottom-right, where (0,0) is the image's top-left
             and (1000,1000) its bottom-right. Include partly hidden or small instances. If there
             is none, return an empty list.

             Respond with STRICT JSON ONLY, exactly {"{"}"objects": [{"{"}"x0": 0, "y0": 0, "x1": 0, "y1": 0{"}"}]{"}"} —
             no markdown fences, no explanation, nothing before the first brace or after the last.
             """;

        ChatMessage message = new(ChatRole.User, new List<AIContent>
        {
            new TextContent(prompt),
            new DataContent(jpeg, "image/jpeg")
        });

        ChatResponse response = await StructuredChatFallback.GetResponseAsync(
            client, [message], ChatResponseFormat.ForJsonSchema<LocatorReply>(), 1500, _logger,
            StructuredChatFallback.ProviderKey(provider), ct).ConfigureAwait(false);
        string text = RobustJsonExtractor.CollapseDuplicateLeadingBrace(response.Text ?? string.Empty);
        LocatorReply? reply = TryParse(text)
            ?? (RobustJsonExtractor.ExtractJsonObject(text) is { } extracted ? TryParse(extracted) : null)
            ?? (RobustJsonExtractor.NormalizeQuotedStrings(RobustJsonExtractor.ExtractJsonObject(text) ?? text) is { } normalized
                ? TryParse(normalized)
                : null);

        // An unreadable reply is a FAILED lookup, never "nothing here": a refusal, prose or a
        // truncated answer said nothing about the frame, and treating it as empty would let a
        // censor run publish whatever was in it.
        if (reply is null)
            throw new InvalidOperationException(
                $"The vision provider's reply for '{safeLabel}' could not be read ({text.Length} chars).");

        if ((reply.Objects?.Count ?? 0) > MaxObjects)
            throw new InvalidOperationException(
                $"The vision provider reported {reply.Objects!.Count} instances of '{safeLabel}', more than the {MaxObjects} one lookup can track.");

        return ToBoxes(reply);
    }

    /// <summary>The reply's boxes on the 0..1000 grid, normalized, reordered so x0 &lt; x1 and y0 &lt; y1, unusable ones dropped.</summary>
    internal static IReadOnlyList<LocatedBox> ToBoxes(LocatorReply reply) =>
        (reply.Objects ?? [])
            .Take(MaxObjects)
            .Select(b => new LocatedBox(
                Math.Clamp(Math.Min(b.X0, b.X1) / 1000.0, 0, 1),
                Math.Clamp(Math.Min(b.Y0, b.Y1) / 1000.0, 0, 1),
                Math.Clamp(Math.Max(b.X0, b.X1) / 1000.0, 0, 1),
                Math.Clamp(Math.Max(b.Y0, b.Y1) / 1000.0, 0, 1)))
            .Where(b => b.IsUsable)
            .ToList();

    private static LocatorReply? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<LocatorReply>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The workflow author's label, kept to one short printable line before it enters a prompt.</summary>
    internal static string Sanitize(string label)
    {
        string oneLine = new string((label ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return oneLine.Length > 120 ? oneLine[..120] : oneLine;
    }
}
