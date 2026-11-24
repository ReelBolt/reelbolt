namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// The one rule for what a read-only plan (a Trial plan: no API access) may still do through the public API.
/// A read is GET or HEAD, plus the POST <c>.../estimate</c> route, which only computes an answer. Output links are NOT reads: a trial key must not mint them.
/// Everything else (runs, stop/resume/retry, uploads, import, deletes, webhooks, run-request cancel) is a write.
/// </summary>
public static class PublicApiReadRule
{
    public static bool IsRead(string method, string? routeTemplate)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) return true;
        if (!HttpMethods.IsPost(method) || string.IsNullOrEmpty(routeTemplate)) return false;

        return routeTemplate.TrimEnd('/').EndsWith("/estimate", StringComparison.OrdinalIgnoreCase);
    }
}
