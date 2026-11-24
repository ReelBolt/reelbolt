using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows.ChangeRequests;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Which clips of a finished VideoGenerate step a change request redoes, and the overrides each carries
/// (docs/change-requests.md "VideoGenerate"). Pure: built from the request and the previous manifest's clips.
/// A clip is "in scope" when the request names it (<c>v2</c> names the shot and so all of its takes,
/// <c>v2t3</c> names that take only), when a <c>*</c> override is present, or when the request names nothing at all
/// (the whole step). Everything else is carried over from the previous manifest.
/// </summary>
internal sealed class VideoClipChangePlan
{
    public const string PromptKey = "prompt";
    public const string RetakeKey = "retake";

    private readonly HashSet<string> _explicit = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, JsonElement>? _star;
    private readonly Dictionary<string, IReadOnlyDictionary<string, JsonElement>> _perItem = new(StringComparer.Ordinal);
    private readonly bool _wholeStep;

    public VideoClipChangePlan(ChangeRequestPayload payload, IReadOnlyList<JsonObject> previousClips)
    {
        foreach (string id in payload.ItemIds ?? [])
            _explicit.Add(id);
        foreach ((string key, IReadOnlyDictionary<string, JsonElement> keys) in payload.Overrides
            ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>())
        {
            if (key == ChangeRequestValidator.AllItems)
            {
                _star = keys;
            }
            else
            {
                _perItem[key] = keys;
                _explicit.Add(key);
            }
        }

        _wholeStep = _explicit.Count == 0 && _star is not { Count: > 0 };
        PreviousClips = previousClips;
        Matched = previousClips.Count(c => ClipId(c) is string id && InScope(id));
    }

    public IReadOnlyList<JsonObject> PreviousClips { get; }

    /// <summary>How many of the previous clips the request matched (0 means <c>CHANGE_NOT_APPLIED</c>).</summary>
    public int Matched { get; }

    public bool InScope(string clipId) =>
        _wholeStep || _star is { Count: > 0 }
        || _explicit.Contains(clipId) || _explicit.Contains(StepChangeCapabilities.ShotIdOf(clipId));

    public JsonObject? Previous(string clipId) =>
        PreviousClips.FirstOrDefault(c => string.Equals(ClipId(c), clipId, StringComparison.Ordinal));

    /// <summary>The prompt override of a clip (its own, else its shot's, else <c>*</c>), or null.</summary>
    public string? PromptFor(string clipId) =>
        Merged(clipId, PromptKey) is { ValueKind: JsonValueKind.String } v && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    public bool RetakeFor(string clipId) => Merged(clipId, RetakeKey) is { ValueKind: JsonValueKind.True };

    private JsonElement? Merged(string clipId, string key)
    {
        JsonElement? found = null;
        if (_star is not null && _star.TryGetValue(key, out JsonElement s))
            found = s;
        string shotId = StepChangeCapabilities.ShotIdOf(clipId);
        if (shotId != clipId && _perItem.TryGetValue(shotId, out IReadOnlyDictionary<string, JsonElement>? shot) && shot.TryGetValue(key, out JsonElement sh))
            found = sh;
        if (_perItem.TryGetValue(clipId, out IReadOnlyDictionary<string, JsonElement>? own) && own.TryGetValue(key, out JsonElement o))
            found = o;
        return found;
    }

    public static string? ClipId(JsonObject clip) =>
        clip["clipId"] is JsonValue v && v.TryGetValue(out string? id) ? id : null;

    /// <summary>True when the clip reached a ledger row in the original run (so it held a hash ordinal).</summary>
    public static bool WasDispatched(JsonObject clip)
    {
        string? status = clip["status"] is JsonValue s && s.TryGetValue(out string? st) ? st : null;
        if (status == "skipped")
            return false;
        string? reason = clip["failureReason"] is JsonValue r && r.TryGetValue(out string? rs) ? rs : null;
        return !(status == "failed" && reason == "INVALID_PROMPT");
    }

    /// <summary>The number in <c>v{n}</c> / <c>v{n}t{k}</c>, else null.</summary>
    public static int? ShotNumber(string clipId)
    {
        string shot = StepChangeCapabilities.ShotIdOf(clipId);
        return shot.Length > 1 && shot[0] == 'v'
            && int.TryParse(shot.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }
}
