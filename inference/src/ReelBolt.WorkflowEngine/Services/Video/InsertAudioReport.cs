using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Copies the insert-audio mix report onto each applied insert as <c>audio {mixed, gainDb, reason?}</c>,
/// so a caller reading <c>inserts.appliedInserts[]</c> sees whether a pinned screen's own sound
/// reached the programme. Only called when <c>MixInsertAudio</c> is on. <b>Pure.</b>
/// </summary>
public static class InsertAudioReport
{
    public static void Annotate(JsonObject insertsNode, JsonObject insertAudioNode)
    {
        if (insertsNode["appliedInserts"] is not JsonArray applied)
            return;

        bool unavailable = insertAudioNode["unavailable"] is JsonValue u && u.TryGetValue(out bool un) && un;
        JsonNode? gain = insertAudioNode["gainDb"];
        JsonArray regions = insertAudioNode["regions"] as JsonArray ?? [];

        foreach (JsonNode? item in applied)
        {
            if (item is not JsonObject insert)
                continue;

            string? regionId = insert["regionId"] is JsonValue r && r.TryGetValue(out string? id) ? id : null;
            JsonObject? region = regions.OfType<JsonObject>().FirstOrDefault(
                x => x["regionId"] is JsonValue v && v.TryGetValue(out string? rid) && rid == regionId);

            bool mixed = !unavailable && region?["mixed"] is JsonValue m && m.TryGetValue(out bool mx) && mx;
            var audio = new JsonObject { ["mixed"] = mixed, ["gainDb"] = gain?.DeepClone() };
            if (!mixed)
            {
                audio["reason"] = unavailable
                    ? "unavailable"
                    : region?["reason"] is JsonValue why && why.TryGetValue(out string? text) ? text : "not_reported";
            }

            insert["audio"] = audio;
        }
    }
}
