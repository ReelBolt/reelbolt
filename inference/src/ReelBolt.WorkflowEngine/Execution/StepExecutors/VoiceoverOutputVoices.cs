using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// The voice facts a recorded Voiceover result carries, read through the ONE description of where
/// <see cref="VoiceoverStepExecutor"/> writes them: <c>meta.voice.{projectVoiceId, remoteVoiceId,
/// source, casting.{applied, chosenVoiceId}}</c> at the root of the output (beside
/// <c>view.lines</c>). Used by <see cref="Services.Voiceover.VoiceReuseGuard"/> when a result is reused.
/// </summary>
internal sealed record VoiceoverOutputVoices(
    bool WellFormed,
    Guid? ProjectVoiceId,
    Guid? CastProjectVoiceId,
    string? RemoteVoiceId,
    string? Source)
{
    internal const string ProjectVoiceSource = "project_voice";
    internal const string CastSource = "cast";

    /// <summary>
    /// Reads <paramref name="outputJson"/>. A Completed Voiceover result always has a <c>meta</c> object, and the
    /// silent-run Skipped result is <c>{"skipped":true,...}</c>; anything else non-blank (unparseable, not an
    /// object, no <c>meta</c>) is NOT well formed, so a caller can fail closed. A well-formed result with no
    /// <c>meta.voice</c> (empty plan) names no voice.
    /// </summary>
    public static VoiceoverOutputVoices Read(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return new VoiceoverOutputVoices(true, null, null, null, null);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(VoiceoverStepExecutor.UpstreamJson(outputJson));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return NotWellFormed;

            if (Prop(root, "skipped") is { ValueKind: JsonValueKind.True })
                return new VoiceoverOutputVoices(true, null, null, null, null);

            if (Prop(root, "meta") is not { ValueKind: JsonValueKind.Object } meta)
                return NotWellFormed;
            if (Prop(meta, "voice") is not { ValueKind: JsonValueKind.Object } voice)
                return new VoiceoverOutputVoices(true, null, null, null, null);

            // CastVoiceAsync records the project voice it picked at casting.chosenVoiceId, with casting.applied true.
            Guid? cast = null;
            if (Prop(voice, "casting") is { ValueKind: JsonValueKind.Object } casting
                && Prop(casting, "applied") is { ValueKind: JsonValueKind.True })
                cast = Guid(Prop(casting, "chosenVoiceId"));

            return new VoiceoverOutputVoices(
                true, Guid(Prop(voice, "projectVoiceId")), cast, Str(Prop(voice, "remoteVoiceId")), Str(Prop(voice, "source")));
        }
        catch (JsonException)
        {
            return NotWellFormed;
        }
    }

    private static VoiceoverOutputVoices NotWellFormed => new(false, null, null, null, null);

    private static JsonElement? Prop(JsonElement obj, string name)
    {
        // Last match wins, like System.Text.Json does for a duplicate key when binding a typed value.
        JsonElement? found = null;
        foreach (JsonProperty p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                found = p.Value;
        return found;
    }

    private static string? Str(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.String } v && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static Guid? Guid(JsonElement? e) =>
        Str(e) is { } s && System.Guid.TryParse(s, out Guid g) ? g : null;
}
