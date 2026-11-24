using System.Text.Json;
using ReelBolt.Shared.Workflows.ChangeRequests;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>
/// "Changing a change": a request made on a run that is itself a change-request run re-makes its items from the
/// snapshot's unchanged config, so the earlier request's overrides must travel with it. The prior row already
/// stores an EFFECTIVE payload, so one hop is enough. Only items in the NEW request's scope gain overrides.
/// </summary>
internal static class ChangeRequestCarryForward
{
    public static ChangeRequestPayload Apply(ChangeRequestPayload requested, string? priorOverridesJson, IReadOnlySet<string> allowedKeys)
    {
        var prior = ParseOverrides(priorOverridesJson);
        if (prior.Count == 0)
            return requested;

        var merged = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        if (requested.Overrides is not null)
            foreach ((string item, IReadOnlyDictionary<string, JsonElement> keys) in requested.Overrides)
                merged[item] = new Dictionary<string, JsonElement>(keys ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);

        prior.TryGetValue(ChangeRequestValidator.AllItems, out Dictionary<string, JsonElement>? priorAll);
        merged.TryGetValue(ChangeRequestValidator.AllItems, out Dictionary<string, JsonElement>? newAll);

        // The new scope: named items and items it overrides; a "*" request re-makes every item the prior one touched.
        List<string> scope = [];
        foreach (string id in requested.ItemIds ?? [])
            if (!scope.Contains(id, StringComparer.Ordinal))
                scope.Add(id);
        foreach (string id in (requested.Overrides ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>()).Keys.Order(StringComparer.Ordinal))
            if (id != ChangeRequestValidator.AllItems && !scope.Contains(id, StringComparer.Ordinal))
                scope.Add(id);
        if (newAll is not null)
            foreach (string id in prior.Keys.Order(StringComparer.Ordinal))
                if (id != ChangeRequestValidator.AllItems && !scope.Contains(id, StringComparer.Ordinal))
                    scope.Add(id);

        foreach (string id in scope)
        {
            if (!merged.ContainsKey(id) && merged.Count >= StepChangeCapabilities.MaxItems)
                continue; // over the item limit: the oldest carried keys are dropped, never the request
            Dictionary<string, JsonElement> carried = [];
            if (priorAll is not null)
                foreach ((string k, JsonElement v) in priorAll)
                    carried[k] = v;
            if (prior.TryGetValue(id, out Dictionary<string, JsonElement>? own))
                foreach ((string k, JsonElement v) in own)
                    carried[k] = v;
            if (!merged.TryGetValue(id, out Dictionary<string, JsonElement>? target))
                merged[id] = target = [];
            foreach ((string k, JsonElement v) in carried)
                if (allowedKeys.Contains(k) && !target.ContainsKey(k) && newAll?.ContainsKey(k) != true)
                    target[k] = v;
            if (target.Count == 0)
                merged.Remove(id);
        }

        if (newAll is not null && priorAll is not null)
            foreach ((string k, JsonElement v) in priorAll)
                if (allowedKeys.Contains(k) && !newAll.ContainsKey(k))
                    newAll[k] = v;

        return requested with
        {
            Overrides = merged.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, JsonElement>)p.Value, StringComparer.Ordinal)
        };
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> ParseOverrides(string? json)
    {
        Dictionary<string, Dictionary<string, JsonElement>> result = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return result;
            foreach (JsonProperty item in doc.RootElement.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object)
                    continue;
                Dictionary<string, JsonElement> keys = new(StringComparer.Ordinal);
                foreach (JsonProperty k in item.Value.EnumerateObject())
                    keys[k.Name] = k.Value.Clone();
                result[item.Name] = keys;
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
