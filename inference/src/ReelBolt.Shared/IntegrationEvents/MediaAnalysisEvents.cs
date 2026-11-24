using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.IntegrationEvents;

/// <summary>
/// API to engine (R09.1): analyse one media file. The engine downloads it, computes its digest, reuses a cached result
/// for the same bytes when the organization has one, and otherwise runs the deterministic detectors (and the
/// transcription when asked). Answered by <see cref="MediaAnalysisFinished"/>.
/// </summary>
public record MediaAnalysisRequested(
    Guid RequestId,
    Guid ProjectId,
    Guid OrganizationId,
    Guid? SourceProjectFileId,
    string SourceStorageKey,
    IReadOnlyList<string> Kinds,
    string? Language,
    Guid? UserId = null,
    double? HeldSeconds = null);

/// <summary>Engine to API: the run began (the requested kinds move from queued to running).</summary>
public record MediaAnalysisStarted(Guid RequestId, Guid ProjectId);

/// <summary>One kind's outcome. <see cref="FromCache"/> true: the result came from the digest cache and nothing was run or billed.</summary>
public record MediaAnalysisKindOutcome(
    string Kind,
    bool Ok,
    string? ResultJson,
    string? SummaryJson,
    string? Reason,
    double? MeasuredSeconds,
    bool FromCache,
    long? ActualMillis = null,
    bool UnpricedUsage = false);

/// <summary>Engine to API: the run is over; every requested kind has an outcome.</summary>
public record MediaAnalysisFinished(
    Guid RequestId,
    Guid ProjectId,
    Guid OrganizationId,
    Guid? SourceProjectFileId,
    string? Digest,
    IReadOnlyList<MediaAnalysisKindOutcome> Outcomes);

/// <summary>The result and summary shapes both services read and write (camelCase JSON).</summary>
public static class MediaAnalysisJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Response bounds: a result larger than this is cut and marked <c>truncated</c>.</summary>
    public const int MaxItems = 20000;
    public const int MaxWords = 100000;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// The version of the per-file <c>events</c> result. Change it whenever the detector gives other events for the same
    /// file, or the stored shape gains a field old rows lack. The cache key has no version, so this is how an old row is told apart.
    /// </summary>
    public const string EventsVersion = "edit-moments-1";

    /// <summary>
    /// True when a stored <c>events</c> result (or its summary) is the lasting answer for its file: its top-level
    /// <c>version</c> equals <see cref="EventsVersion"/> and it is not marked <c>degraded</c>. A row with another version,
    /// no version, a <c>degraded: true</c> mark or unreadable JSON is not current. Such a row is still served, marked
    /// outdated; it is never a cache hit, and the next request for the kind computes it again and replaces it. The editor's
    /// helpers read moments from a row of this version even when it is marked degraded, never from another version.
    /// </summary>
    public static bool EventsAreCurrent(string? json) => ReadEventsMarks(json) is (true, false);

    /// <summary>
    /// True when a stored <c>events</c> result (or its summary) was written by this version of the detector, complete or
    /// not. Only for choosing which of two rows to keep; whether a row is the lasting answer is <see cref="EventsAreCurrent"/>.
    /// </summary>
    public static bool EventsAreThisVersion(string? json) => ReadEventsMarks(json).ThisVersion;

    /// <summary>The two top-level marks of a stored events result or summary. Only top-level fields count.</summary>
    private static (bool ThisVersion, bool Degraded) ReadEventsMarks(string? json)
    {
        if (string.IsNullOrEmpty(json)) return (false, false);
        try
        {
            bool thisVersion = false, degraded = false;
            Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return (false, false);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isVersion = reader.ValueTextEquals("version");
                bool isDegraded = !isVersion && reader.ValueTextEquals("degraded");
                if (!reader.Read()) return (false, false);
                if (isVersion) thisVersion = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(EventsVersion);
                else if (isDegraded) degraded = reader.TokenType != JsonTokenType.False;
                // A value that is an object or an array is stepped over whole: only top-level fields are read.
                reader.Skip();
            }

            return (thisVersion, degraded);
        }
        catch (JsonException)
        {
            return (false, false);
        }
    }
}

public sealed record AnalysisRange(double StartSec, double EndSec);

public sealed record AnalysisSilenceResult(double? DurationSec, IReadOnlyList<AnalysisRange> Ranges, bool Truncated);

public sealed record AnalysisShotsResult(IReadOnlyList<AnalysisRange> Shots, bool Truncated);

public sealed record AnalysisMotionWindow(double StartSec, double EndSec, double Mean, string MotionClass);

public sealed record AnalysisMotionResult(IReadOnlyList<AnalysisMotionWindow> Windows, bool Truncated);

/// <summary>
/// One detected event; <c>Type</c> is the snake_case type word (appear, move_start, speech_end...). The last four
/// members are what the detector measured beside the frame: the last frame of a spread-out event, the direction and
/// speed words of a move, and the kind of picture (<c>graphic</c>, <c>text</c> or <c>footage</c>). Each is null, and
/// left out of the JSON, when the detector gave none; a row stored before they existed reads them as null.
/// The plain words of an event are never stored: they are worked out when the result is read.
/// </summary>
public sealed record AnalysisEvent(
    string Id, string Type, int Frame, double TimeSec, double Strength, AnalysisEventRegion? Region, double Confidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndFrame = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Direction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Speed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);

public sealed record AnalysisEventRegion(double X, double Y, double W, double H);

/// <summary>
/// <see cref="Version"/> is <see cref="MediaAnalysisJson.EventsVersion"/> at the time the result was made; null on a row stored before versions.
/// <see cref="Degraded"/> is true when a part of the detection failed, so events are missing (left out of the JSON when false).
/// A degraded result is shown, but it is never the lasting answer for the file: see <see cref="MediaAnalysisJson.EventsAreCurrent"/>.
/// </summary>
public sealed record AnalysisEventsResult(
    double Fps, int FrameCount, IReadOnlyList<AnalysisEvent> Events, int Suppressed, bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Degraded = false);

/// <summary>
/// The tracked beat grid of one music file (the editor reads <see cref="BeatTimesSec"/>; the steady fields are its
/// fallback). <see cref="DurationSec"/> is the file's probed length, <see cref="AnalysedSec"/> what was actually
/// analysed (the first 1200 s at most).
/// </summary>
public sealed record AnalysisBeatsResult(
    double Bpm,
    double BeatSec,
    double BeatPhaseSec,
    double BarPhaseSec,
    int BeatsPerBar,
    IReadOnlyList<double> BeatTimesSec,
    int FirstDownbeatIndex,
    double DurationSec,
    double AnalysedSec,
    double? DropSec,
    double? OutroSec,
    double Confidence,
    bool Steady,
    string TempoWord,
    double HalfTempoScore,
    double DoubleTempoScore,
    bool Truncated);

public sealed record AnalysisWord(string Text, double StartSec, double EndSec, bool Estimated);

public sealed record AnalysisTranscriptResult(string? Language, IReadOnlyList<AnalysisWord> Words, bool Truncated);
