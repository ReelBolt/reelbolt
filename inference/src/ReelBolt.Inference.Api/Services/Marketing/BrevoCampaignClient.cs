using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>A Brevo failure. <see cref="Code"/> is stable and safe to log; it never carries an address, a body or the key.</summary>
public sealed class BrevoCampaignException(string code, int? status = null, bool outcomeUnknown = false) : Exception(code)
{
    public string Code => code;

    /// <summary>The HTTP status, when Brevo answered.</summary>
    public int? Status => status;

    /// <summary>True for a transport error, a timeout or a 5xx: the call may have been applied.</summary>
    public bool OutcomeUnknown => outcomeUnknown;
}

public sealed record BrevoListContact(string Email, bool EmailBlacklisted, IReadOnlyList<long> ListUnsubscribed);

/// <summary>One contact as <c>GET /contacts/{email}</c> reports it (statistics reduced to what classification needs).</summary>
public sealed record BrevoContactInfo(
    bool EmailBlacklisted, IReadOnlyList<long> ListUnsubscribed, bool Complaints, bool Unsubscribed, bool HardBounces,
    DateTime? ComplaintAt = null, DateTime? UnsubscribedAt = null);

/// <summary>A contact to push: the normalized address and the names Brevo should hold (null or empty clears the attribute).</summary>
public sealed record BrevoContactInput(string Email, string? FirstName = null, string? LastName = null);

public sealed record BrevoCampaignDraft(string Name, string Subject, string Html, string SenderEmail, string SenderName, IReadOnlyList<long> ListIds);

public sealed record BrevoCampaignInfo(string Status, string? StatsJson);

/// <summary>The Brevo calls the campaign pipeline needs, and nothing else.</summary>
public interface IBrevoCampaignClient
{
    /// <summary>Starts removing every contact from the list; returns the process id.</summary>
    Task<long> RemoveAllFromListAsync(long listId, CancellationToken ct);

    /// <summary>Imports the contacts (FIRSTNAME and LASTNAME always sent, empty when there is no name) into the list; returns the process id. Never blocklists.</summary>
    Task<long> ImportAsync(long listId, IReadOnlyList<BrevoContactInput> contacts, CancellationToken ct);

    /// <summary>
    /// Creates or updates one contact (POST contacts, updateEnabled). A null or empty name is sent as an empty string so a
    /// cleared name is removed at Brevo. <paramref name="listId"/> is sent only for a first add. Never sends a blocklist field.
    /// </summary>
    Task UpsertContactAsync(BrevoContactInput contact, long? listId, CancellationToken ct);

    /// <summary>The process status: queued, in_process, processing, completed, failed, cancelled.</summary>
    Task<string> GetProcessStatusAsync(long processId, CancellationToken ct);

    Task<int> GetListSubscriberCountAsync(long listId, CancellationToken ct);

    /// <summary>One page; a page shorter than <paramref name="limit"/> is the last.</summary>
    Task<IReadOnlyList<BrevoListContact>> GetListContactsAsync(long listId, int offset, int limit, CancellationToken ct);

    /// <summary>Reads one contact; null when Brevo has none (404).</summary>
    Task<BrevoContactInfo?> GetContactAsync(string email, CancellationToken ct);

    /// <summary>Deletes a contact; an absent one (404) counts as success.</summary>
    Task DeleteContactAsync(string email, CancellationToken ct);

    Task<long> CreateCampaignAsync(BrevoCampaignDraft draft, CancellationToken ct);
    Task SendNowAsync(long campaignId, CancellationToken ct);

    /// <summary>Refuses an empty list: an empty <c>emailTo</c> mails the account's whole test list.</summary>
    Task SendTestAsync(long campaignId, IReadOnlyList<string> emailTo, CancellationToken ct);

    Task<BrevoCampaignInfo> GetCampaignAsync(long campaignId, CancellationToken ct);
    Task DeleteCampaignAsync(long campaignId, CancellationToken ct);
}

/// <summary>
/// Brevo campaign API over a plain HttpClient. Logs only operation names, status codes and <c>rcpt=</c>
/// refs. No call is retried here: whether to retry is the pipeline's decision (and sendNow is never retried).
/// </summary>
public sealed partial class BrevoCampaignClient(
    HttpClient http,
    IOptions<MarketingCampaignOptions> options,
    MailLogRef logRef,
    ILogger<BrevoCampaignClient> logger) : IBrevoCampaignClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _nextCallAt = DateTime.MinValue;

    public async Task<long> RemoveAllFromListAsync(long listId, CancellationToken ct)
    {
        using JsonDocument doc = await SendAsync(HttpMethod.Post, $"contacts/lists/{listId}/contacts/remove", new { all = true }, "remove_all", ct);
        return doc.RootElement.TryGetProperty("contacts", out JsonElement c) && c.TryGetProperty("processId", out JsonElement p) && p.TryGetInt64(out long id)
            ? id
            : throw new BrevoCampaignException("brevo_bad_response");
    }

    public async Task UpsertContactAsync(BrevoContactInput contact, long? listId, CancellationToken ct)
    {
        // emailBlacklisted / emailBlacklist are deliberately never sent.
        Dictionary<string, object> body = new()
        {
            ["email"] = contact.Email,
            ["attributes"] = new Dictionary<string, string> { ["FIRSTNAME"] = contact.FirstName ?? string.Empty, ["LASTNAME"] = contact.LastName ?? string.Empty },
            ["updateEnabled"] = true,
        };
        if (listId is { } id)
        {
            body["listIds"] = new[] { id };
        }

        using JsonDocument _ = await SendAsync(HttpMethod.Post, "contacts", body, "upsert_contact", ct, logRcpt: contact.Email);
    }

    public async Task<long> ImportAsync(long listId, IReadOnlyList<BrevoContactInput> contacts, CancellationToken ct)
    {
        // emailBlacklist is deliberately never sent: it would blocklist everyone in the import.
        object body = new
        {
            jsonBody = contacts.Select(c => ImportRow(c)).ToArray(),
            listIds = new[] { listId },
            updateExistingContacts = true,
            // Without it an empty attribute value is ignored and a cleared name stays at Brevo (checked live).
            emptyContactsAttributes = true,
            disableNotification = true,
        };
        using JsonDocument doc = await SendAsync(HttpMethod.Post, "contacts/import", body, "import", ct);
        return doc.RootElement.TryGetProperty("processId", out JsonElement p) && p.TryGetInt64(out long id)
            ? id
            : throw new BrevoCampaignException("brevo_bad_response");
    }

    private static Dictionary<string, object> ImportRow(BrevoContactInput c)
        => new()
        {
            ["email"] = c.Email,
            // Always both: an empty string removes the attribute at Brevo, so a cleared name does not linger.
            ["attributes"] = new Dictionary<string, string> { ["FIRSTNAME"] = c.FirstName ?? string.Empty, ["LASTNAME"] = c.LastName ?? string.Empty },
        };

    public async Task<string> GetProcessStatusAsync(long processId, CancellationToken ct)
    {
        using JsonDocument doc = await SendAsync(HttpMethod.Get, $"processes/{processId}", null, "process", ct, strict: true);
        return doc.RootElement.TryGetProperty("status", out JsonElement s) && s.GetString() is { } status
            ? status
            : throw new BrevoCampaignException("brevo_bad_response");
    }

    public async Task<int> GetListSubscriberCountAsync(long listId, CancellationToken ct)
    {
        using JsonDocument doc = await SendAsync(HttpMethod.Get, $"contacts/lists/{listId}", null, "list", ct, strict: true);
        return doc.RootElement.TryGetProperty("totalSubscribers", out JsonElement t) && t.TryGetInt32(out int n)
            ? n
            : throw new BrevoCampaignException("brevo_bad_response");
    }

    public async Task<IReadOnlyList<BrevoListContact>> GetListContactsAsync(long listId, int offset, int limit, CancellationToken ct)
    {
        using JsonDocument doc = await SendAsync(HttpMethod.Get, $"contacts/lists/{listId}/contacts?limit={limit}&offset={offset}", null, "list_contacts", ct, strict: true);
        List<BrevoListContact> result = [];
        if (!doc.RootElement.TryGetProperty("contacts", out JsonElement contacts) || contacts.ValueKind != JsonValueKind.Array)
        {
            // A page without a contacts array is malformed, never an empty list.
            throw new BrevoCampaignException("brevo_bad_response");
        }

        foreach (JsonElement c in contacts.EnumerateArray())
        {
            string? email = c.TryGetProperty("email", out JsonElement e) ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            bool black = c.TryGetProperty("emailBlacklisted", out JsonElement b) && b.ValueKind == JsonValueKind.True;
            List<long> unsub = [];
            if (c.TryGetProperty("listUnsubscribed", out JsonElement u) && u.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in u.EnumerateArray())
                {
                    if (id.TryGetInt64(out long v))
                    {
                        unsub.Add(v);
                    }
                }
            }

            result.Add(new BrevoListContact(email.Trim().ToLowerInvariant(), black, unsub));
        }

        return result;
    }

    public async Task<BrevoContactInfo?> GetContactAsync(string email, CancellationToken ct)
    {
        string path = $"contacts/{Uri.EscapeDataString(email)}?identifierType=email_id";
        using JsonDocument doc = await SendAsync(HttpMethod.Get, path, null, "get_contact", ct, okOn404: true, logRcpt: email, strict: true);
        JsonElement r = doc.RootElement;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty(NotFoundMarker, out _))
        {
            return null;
        }

        if (!r.TryGetProperty("email", out _) && !r.TryGetProperty("id", out _))
        {
            // Not a contact document: never read it as a clean contact.
            throw new BrevoCampaignException("brevo_bad_response");
        }

        bool black = r.TryGetProperty("emailBlacklisted", out JsonElement b) && b.ValueKind == JsonValueKind.True;
        List<long> unsub = [];
        if (r.TryGetProperty("listUnsubscribed", out JsonElement u) && u.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement id in u.EnumerateArray())
            {
                if (id.TryGetInt64(out long v))
                {
                    unsub.Add(v);
                }
            }
        }

        bool complaints = false, unsubscribed = false, hard = false;
        DateTime? complaintAt = null, unsubscribedAt = null;
        if (r.TryGetProperty("statistics", out JsonElement st) && st.ValueKind == JsonValueKind.Object)
        {
            complaints = NonEmpty(st, "complaints");
            complaintAt = LatestEvent(st, "complaints");
            hard = NonEmpty(st, "hardBounces");
            if (st.TryGetProperty("unsubscriptions", out JsonElement un) && un.ValueKind == JsonValueKind.Object)
            {
                unsubscribed = NonEmpty(un, "userUnsubscription") || NonEmpty(un, "adminUnsubscription");
                DateTime? user = LatestEvent(un, "userUnsubscription"), admin = LatestEvent(un, "adminUnsubscription");
                bool userHas = NonEmpty(un, "userUnsubscription"), adminHas = NonEmpty(un, "adminUnsubscription");
                // Unknown time on either non-empty list: the time cannot be compared.
                unsubscribedAt = (userHas && user is null) || (adminHas && admin is null)
                    ? null
                    : (user, admin) switch
                    {
                        ({ } ua, { } ub) => ua > ub ? ua : ub,
                        ({ } ua, null) => ua,
                        (null, { } ub) => ub,
                        _ => null,
                    };
            }
        }

        return new BrevoContactInfo(black, unsub, complaints, unsubscribed, hard, complaintAt, unsubscribedAt);
    }

    /// <summary>
    /// The newest <c>eventTime</c> (UTC) in a statistics array; null when the array is empty or any entry has no
    /// parseable time (the caller then cannot compare and takes the protective reading).
    /// </summary>
    private static DateTime? LatestEvent(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() == 0)
        {
            return null;
        }

        DateTime? latest = null;
        foreach (JsonElement e in a.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("eventTime", out JsonElement t) || t.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset when))
            {
                return null;
            }

            DateTime utc = when.UtcDateTime;
            if (latest is null || utc > latest)
            {
                latest = utc;
            }
        }

        return latest;
    }

    private const string NotFoundMarker = "__notFound";

    private static bool NonEmpty(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0;

    public async Task DeleteContactAsync(string email, CancellationToken ct)
    {
        string path = $"contacts/{Uri.EscapeDataString(email)}?identifierType=email_id";
        using JsonDocument _ = await SendAsync(HttpMethod.Delete, path, null, "delete_contact", ct, okOn404: true, logRcpt: email);
    }

    public async Task<long> CreateCampaignAsync(BrevoCampaignDraft draft, CancellationToken ct)
    {
        Dictionary<string, object> body = new()
        {
            ["name"] = draft.Name,
            ["subject"] = draft.Subject,
            ["sender"] = new { email = draft.SenderEmail, name = draft.SenderName },
            ["htmlContent"] = draft.Html,
        };
        if (draft.ListIds.Count > 0)
        {
            body["recipients"] = new { listIds = draft.ListIds };
        }

        using JsonDocument doc = await SendAsync(HttpMethod.Post, "emailCampaigns", body, "create_campaign", ct);
        return doc.RootElement.TryGetProperty("id", out JsonElement p) && p.TryGetInt64(out long id)
            ? id
            : throw new BrevoCampaignException("brevo_bad_response");
    }

    public async Task SendNowAsync(long campaignId, CancellationToken ct)
    {
        using JsonDocument _ = await SendAsync(HttpMethod.Post, $"emailCampaigns/{campaignId}/sendNow", null, "send_now", ct, sendNow: true);
    }

    public async Task SendTestAsync(long campaignId, IReadOnlyList<string> emailTo, CancellationToken ct)
    {
        if (emailTo.Count == 0 || emailTo.Any(string.IsNullOrWhiteSpace))
        {
            throw new BrevoCampaignException("brevo_empty_recipients");
        }

        using JsonDocument _ = await SendAsync(HttpMethod.Post, $"emailCampaigns/{campaignId}/sendTest", new { emailTo }, "send_test", ct);
    }

    public async Task<BrevoCampaignInfo> GetCampaignAsync(long campaignId, CancellationToken ct)
    {
        using JsonDocument doc = await SendAsync(HttpMethod.Get, $"emailCampaigns/{campaignId}?statistics=globalStats", null, "get_campaign", ct, okOn404: true);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(NotFoundMarker, out _))
        {
            // Brevo has no such campaign (its own not-found document): reported as a status so the caller can settle the row.
            return new BrevoCampaignInfo("not_found", null);
        }

        string status = doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("status", out JsonElement s) && s.ValueKind == JsonValueKind.String && s.GetString() is { } v
            ? v
            : throw new BrevoCampaignException("brevo_bad_response");
        string? stats = doc.RootElement.TryGetProperty("statistics", out JsonElement st) && st.ValueKind == JsonValueKind.Object && st.TryGetProperty("globalStats", out JsonElement g)
            ? g.GetRawText()
            : null;
        return new BrevoCampaignInfo(status, stats);
    }

    public async Task DeleteCampaignAsync(long campaignId, CancellationToken ct)
    {
        using JsonDocument _ = await SendAsync(HttpMethod.Delete, $"emailCampaigns/{campaignId}", null, "delete_campaign", ct, okOn404: true);
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, object? body, string op, CancellationToken ct, bool okOn404 = false, string? logRcpt = null, bool strict = false, bool sendNow = false)
    {
        MarketingCampaignOptions o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BrevoCampaignApiKey))
        {
            throw new BrevoCampaignException("brevo_key_missing");
        }

        await ThrottleAsync(o.BrevoMinCallIntervalMs, ct);
        string rcpt = logRcpt is null ? string.Empty : " " + logRef.For(logRcpt);
        using HttpRequestMessage req = new(method, $"{o.BrevoApiBase.TrimEnd('/')}/{path}");
        req.Headers.TryAddWithoutValidation("api-key", o.BrevoCampaignApiKey);
        req.Headers.TryAddWithoutValidation("accept", "application/json");
        // Paths carry contact addresses (contacts/{email}): reduce the exported client span's url.full to the origin.
        req.Options.Set(ReelBolt.Shared.Observability.ReelBoltTelemetry.OriginOnlyUrl, new Uri(o.BrevoApiBase).GetLeftPart(UriPartial.Authority));
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Brevo {Op} timed out{Rcpt}", op, rcpt);
            throw new BrevoCampaignException("brevo_timeout", null, outcomeUnknown: true);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Brevo {Op} failed{Rcpt} {Error}", op, rcpt, MailLogRef.DescribeError(ex));
            throw new BrevoCampaignException("brevo_transport_error", null, outcomeUnknown: true);
        }

        using (res)
        {
            int status = (int)res.StatusCode;
            string text = res.Content is null ? string.Empty : await res.Content.ReadAsStringAsync(ct);
            // A 404 is "absent" only when the body is Brevo's own not-found document. A 404 from anything else (a proxy,
            // a wrong base URL) is an error: the row stays and the call is retried, never counted as erased.
            if (res.IsSuccessStatusCode || (okOn404 && res.StatusCode == HttpStatusCode.NotFound && IsBrevoNotFound(text)))
            {
                logger.LogInformation("Brevo {Op} status={Status}{Rcpt}", op, status, rcpt);
                if (!res.IsSuccessStatusCode)
                {
                    return JsonDocument.Parse("{\"" + NotFoundMarker + "\":true}");
                }

                if (strict)
                {
                    // A read that must answer with data: an empty, non-JSON or non-object 2xx is a failure, never "clean" or "empty".
                    return ParseObjectOrThrow(text);
                }

                return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : ParseOrEmpty(text);
            }

            // Brevo's own error code (a short [a-z_] word, never the body or an address) so a refusal is diagnosable from the log.
            string brevoCode = ErrorCode(status, text);
            logger.LogWarning("Brevo {Op} rejected status={Status} code={Code}{Rcpt}", op, status, brevoCode, rcpt);
            // sendNow is the one call that is never retried: only a body that proves Brevo refused it is a refusal.
            bool unknown = sendNow ? !IsDefiniteSendNowRefusal(status, text) : status >= 500;
            throw new BrevoCampaignException(unknown ? $"brevo_http_{status}" : brevoCode, status, unknown);
        }
    }

    /// <summary>
    /// Brevo answers a missing document with <c>{"code":"document_not_found",...}</c>. The exact code comes from Brevo's
    /// documentation and has not been verified against a live account.
    /// </summary>
    internal static bool IsBrevoNotFound(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String
                && c.GetString() is "document_not_found";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A sendNow refusal that is certain: 400, 402 or 404 with a parsable JSON object body that is not one of Brevo's
    /// "already sent / being sent / request already processed" answers. Anything else (408, 429, 3xx, other statuses, an
    /// unparsable body) may have been applied and is an unknown outcome.
    /// </summary>
    internal static bool IsDefiniteSendNowRefusal(int status, string body)
    {
        if (status is not (400 or 402 or 404)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (string name in new[] { "code", "message" })
            {
                if (doc.RootElement.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } t
                    && (t.Equals("campaign_sent", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("campaign_processing", StringComparison.OrdinalIgnoreCase)
                        || t.Contains("Request already processed", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonDocument ParseObjectOrThrow(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new BrevoCampaignException("brevo_bad_response");
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new BrevoCampaignException("brevo_bad_response");
        }

        return doc;
    }

    private static JsonDocument ParseOrEmpty(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    /// <summary>Brevo's <c>code</c> field when it is a plain token, else the status. Never the message.</summary>
    private static string ErrorCode(int status, string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String
                && c.GetString() is { } code && CodePattern().IsMatch(code))
            {
                return "brevo_" + code;
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return $"brevo_http_{status}";
    }

    private static async Task ThrottleAsync(int intervalMs, CancellationToken ct)
    {
        if (intervalMs <= 0)
        {
            return;
        }

        await Gate.WaitAsync(ct);
        try
        {
            TimeSpan wait = _nextCallAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            _nextCallAt = DateTime.UtcNow.AddMilliseconds(intervalMs);
        }
        finally
        {
            Gate.Release();
        }
    }

    [GeneratedRegex("^[a-z_]{1,40}$")]
    private static partial Regex CodePattern();
}
