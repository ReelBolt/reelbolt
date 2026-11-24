using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Inference.Api.Services.Auth;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Reads the decision-calibration report from the WorkflowEngine, which owns <c>decision_observations</c>
/// and the aggregation. Exists so the assistant reuses that one implementation instead of
/// re-deriving escalation/acceptance from a second read-only mapping of the table.
/// </summary>
public interface IDecisionCalibrationClient
{
    /// <summary>Returns the engine's report JSON (provider bucket arrays trimmed), or an error message.</summary>
    Task<(string? Json, string? Error)> GetAsync(string? site, double? acceptAt, double? minMargin, CancellationToken ct);
}

/// <summary>
/// HTTP implementation. The credential comes from <see cref="IEngineCallCredential"/>: the caller's own
/// bearer token when no engine key is configured, otherwise a 60-second admin token minted for that caller
/// whose <c>platformAdmin</c> is database truth. Either way the engine's admin check is evaluated against
/// the identity the assistant is acting for and this service never widens authority. Read-only: a single GET.
/// </summary>
public sealed class DecisionCalibrationClient : IDecisionCalibrationClient
{
    /// <summary>Route of the WorkflowEngine calibration read; drift-tested against McpScopeRoutes in the engine.</summary>
    public const string CalibrationPath = "/api/v1/workflow-engine/decision-calibration";

    public const string HttpClientName = "workflow-engine-calibration";

    private readonly IHttpClientFactory _factory;
    private readonly IEngineCallCredential _credential;

    public DecisionCalibrationClient(IHttpClientFactory factory, IEngineCallCredential credential)
    {
        _factory = factory;
        _credential = credential;
    }

    public async Task<(string? Json, string? Error)> GetAsync(
        string? site, double? acceptAt, double? minMargin, CancellationToken ct)
    {
        AuthenticationHeaderValue? header = await _credential.ForCurrentCallerAsync(ct);
        if (header is null)
            return (null, "no caller credential available to read calibration data");

        var query = new List<string>();
        if (acceptAt.HasValue) query.Add($"acceptAt={acceptAt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (minMargin.HasValue) query.Add($"minMargin={minMargin.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(site)) query.Add($"site={Uri.EscapeDataString(site)}");
        string path = CalibrationPath + (query.Count > 0 ? "?" + string.Join('&', query) : "");

        try
        {
            HttpClient client = _factory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = header;
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return (null, "admin privileges required");
            if (!response.IsSuccessStatusCode)
                return (null, $"the workflow engine answered HTTP {(int)response.StatusCode}");

            string body = await response.Content.ReadAsStringAsync(ct);
            JsonNode? root = JsonNode.Parse(body);
            // Per-provider bucket arrays are small-sample noise for a model reading the result; the site
            // entries keep theirs.
            if (root?["providers"] is JsonArray providers)
                foreach (JsonNode? p in providers)
                    (p as JsonObject)?.Remove("reliabilityBuckets");
            return (root?.ToJsonString(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return (null, "the workflow engine could not be reached");
        }
    }
}
