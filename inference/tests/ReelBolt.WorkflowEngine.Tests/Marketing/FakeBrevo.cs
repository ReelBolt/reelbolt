#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// A fake Brevo over HttpMessageHandler: list membership, processes and blocked contacts, recording every
/// call. Nothing here ever leaves the process.
/// </summary>
internal sealed class FakeBrevo : HttpMessageHandler
{
    internal sealed record Call(string Method, string Path, string? Body);

    private long _nextId = 100;

    public List<Call> Calls { get; } = [];
    public Dictionary<long, HashSet<string>> Lists { get; } = new() { [10] = [], [11] = [] };

    /// <summary>Contacts with emailBlacklisted set.</summary>
    public HashSet<string> Blocked { get; } = [];

    /// <summary>Contacts that unsubscribed from a list (listUnsubscribed).</summary>
    public Dictionary<string, HashSet<long>> ListUnsubscribed { get; } = [];

    /// <summary>Statistics lists per contact: any of user, admin, hard, complaint, soft.</summary>
    public Dictionary<string, HashSet<string>> Stats { get; } = [];

    /// <summary>Contacts that exist without being on any list.</summary>
    public HashSet<string> Existing { get; } = [];

    /// <summary>The FIRSTNAME / LASTNAME attributes Brevo holds per contact (an empty value removes the attribute).</summary>
    public Dictionary<string, Dictionary<string, string>> Attributes { get; } = [];

    public string ProcessStatus { get; set; } = "completed";

    /// <summary>Addresses the import silently rejects.</summary>
    public HashSet<string> DropOnImport { get; } = [];

    /// <summary>Addresses the import adds although nobody asked.</summary>
    public HashSet<string> ExtraOnImport { get; } = [];

    /// <summary>Runs first; a non-null answer replaces the normal handling (it may throw to model a transport error).</summary>
    public Func<Call, HttpResponseMessage?>? Override { get; set; }

    /// <summary>Runs after normal handling.</summary>
    public Action<Call>? After { get; set; }

    public IEnumerable<Call> Writes => Calls.Where(c => c.Method != "GET");
    public int Count(string pathEnd) => Calls.Count(c => c.Path.Split('?')[0].EndsWith(pathEnd, StringComparison.Ordinal));
    public IEnumerable<Call> Sends => Calls.Where(c => c.Path.EndsWith("/sendNow", StringComparison.Ordinal));

    public static HttpResponseMessage Status(HttpStatusCode code, string json = "{}")
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string path = request.RequestUri!.PathAndQuery;
        path = path[(path.IndexOf("/v3/", StringComparison.Ordinal) + 4)..];
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Call call = new(request.Method.Method, path, body);
        Calls.Add(call);
        if (Override?.Invoke(call) is { } injected)
        {
            return injected;
        }

        HttpResponseMessage response = Handle(call);
        After?.Invoke(call);
        return response;
    }

    /// <summary>Like Brevo: an upsert clears an attribute sent as ""; an import ignores it unless emptyContactsAttributes is true.</summary>
    private void StoreAttributes(string email, JsonElement attributes, bool clearEmpty)
    {
        if (!Attributes.TryGetValue(email, out Dictionary<string, string>? held)) Attributes[email] = held = [];
        foreach (JsonProperty a in attributes.EnumerateObject())
        {
            string v = a.Value.GetString() ?? string.Empty;
            if (v.Length == 0) { if (clearEmpty) held.Remove(a.Name); } else held[a.Name] = v;
        }
    }

    private HttpResponseMessage Handle(Call call)
    {
        Match m;
        if ((m = Regex.Match(call.Path, @"^contacts/lists/(\d+)/contacts/remove$")).Success && call.Method == "POST")
        {
            Lists[long.Parse(m.Groups[1].Value)].Clear();
            return Status(HttpStatusCode.Created, $"{{\"contacts\":{{\"processId\":{_nextId++}}}}}");
        }

        if (call.Path == "contacts/import")
        {
            using JsonDocument doc = JsonDocument.Parse(call.Body!);
            long listId = doc.RootElement.GetProperty("listIds")[0].GetInt64();
            foreach (JsonElement c in doc.RootElement.GetProperty("jsonBody").EnumerateArray())
            {
                string email = c.GetProperty("email").GetString()!;
                if (!DropOnImport.Contains(email)) Lists[listId].Add(email);
                bool clear = doc.RootElement.TryGetProperty("emptyContactsAttributes", out JsonElement ec) && ec.ValueKind == JsonValueKind.True;
                if (c.TryGetProperty("attributes", out JsonElement at)) StoreAttributes(email, at, clearEmpty: clear);
            }

            foreach (string extra in ExtraOnImport) Lists[listId].Add(extra);
            return Status(HttpStatusCode.Accepted, $"{{\"processId\":{_nextId++}}}");
        }

        if (Regex.IsMatch(call.Path, @"^processes/\d+$"))
        {
            return Status(HttpStatusCode.OK, $"{{\"status\":\"{ProcessStatus}\"}}");
        }

        if ((m = Regex.Match(call.Path, @"^contacts/lists/(\d+)/contacts\?limit=(\d+)&offset=(\d+)$")).Success)
        {
            List<string> members = Lists[long.Parse(m.Groups[1].Value)].Order(StringComparer.Ordinal).ToList();
            IEnumerable<object> page = members.Skip(int.Parse(m.Groups[3].Value)).Take(int.Parse(m.Groups[2].Value)).Select(e => new
            {
                email = e,
                emailBlacklisted = Blocked.Contains(e),
                listUnsubscribed = ListUnsubscribed.TryGetValue(e, out HashSet<long>? u) ? u.ToArray() : [],
            });
            return Status(HttpStatusCode.OK, JsonSerializer.Serialize(new { contacts = page, count = members.Count }));
        }

        if ((m = Regex.Match(call.Path, @"^contacts/lists/(\d+)$")).Success)
        {
            return Status(HttpStatusCode.OK, $"{{\"totalSubscribers\":{Lists[long.Parse(m.Groups[1].Value)].Count}}}");
        }

        if (call.Method == "GET" && (m = Regex.Match(call.Path, @"^contacts/([^/?]+)\?identifierType=email_id$")).Success)
        {
            string email = Uri.UnescapeDataString(m.Groups[1].Value);
            HashSet<string> st = Stats.TryGetValue(email, out HashSet<string>? x) ? x : [];
            bool exists = Existing.Contains(email) || Lists.Values.Any(l => l.Contains(email)) || Blocked.Contains(email) || Stats.ContainsKey(email);
            if (!exists) return Status(HttpStatusCode.NotFound, "{\"code\":\"document_not_found\"}");
            object[] one = [new { eventTime = "2026-10-31T00:00:00Z" }];
            return Status(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                email,
                emailBlacklisted = Blocked.Contains(email),
                listUnsubscribed = ListUnsubscribed.TryGetValue(email, out HashSet<long>? u) ? u.ToArray() : [],
                statistics = new
                {
                    complaints = st.Contains("complaint") ? one : [],
                    hardBounces = st.Contains("hard") ? one : [],
                    softBounces = st.Contains("soft") ? one : [],
                    unsubscriptions = new
                    {
                        userUnsubscription = st.Contains("user") ? one : [],
                        adminUnsubscription = st.Contains("admin") ? one : [],
                    },
                },
            }));
        }

        if (call.Method == "DELETE" && call.Path.StartsWith("contacts/", StringComparison.Ordinal))
        {
            string email = Uri.UnescapeDataString(call.Path["contacts/".Length..].Split('?')[0]);
            bool found = false;
            foreach (HashSet<string> l in Lists.Values) found |= l.Remove(email);
            Blocked.Remove(email);
            Stats.Remove(email);
            found |= Existing.Remove(email);
            ListUnsubscribed.Remove(email);
            return found ? Status(HttpStatusCode.NoContent) : Status(HttpStatusCode.NotFound, "{\"code\":\"document_not_found\"}");
        }

        if (call is { Method: "POST", Path: "contacts" })
        {
            using JsonDocument doc = JsonDocument.Parse(call.Body!);
            string email = doc.RootElement.GetProperty("email").GetString()!;
            bool existed = Existing.Contains(email) || Lists.Values.Any(l => l.Contains(email)) || Blocked.Contains(email);
            Existing.Add(email);
            if (doc.RootElement.TryGetProperty("listIds", out JsonElement ids))
            {
                foreach (JsonElement id in ids.EnumerateArray()) Lists[id.GetInt64()].Add(email);
            }

            if (doc.RootElement.TryGetProperty("attributes", out JsonElement at)) StoreAttributes(email, at, clearEmpty: true);
            return existed ? Status(HttpStatusCode.NoContent) : Status(HttpStatusCode.Created, $"{{\"id\":{_nextId++}}}");
        }

        if (call is { Method: "POST", Path: "emailCampaigns" })
        {
            return Status(HttpStatusCode.Created, $"{{\"id\":{_nextId++}}}");
        }

        if (Regex.IsMatch(call.Path, @"^emailCampaigns/\d+/(sendNow|sendTest)$"))
        {
            return Status(HttpStatusCode.NoContent);
        }

        if (call.Method == "GET" && call.Path.StartsWith("emailCampaigns/", StringComparison.Ordinal))
        {
            return Status(HttpStatusCode.OK, "{\"status\":\"sent\",\"statistics\":{\"globalStats\":{\"sent\":3,\"delivered\":3}}}");
        }

        if (call.Method == "DELETE" && call.Path.StartsWith("emailCampaigns/", StringComparison.Ordinal))
        {
            return Status(HttpStatusCode.NoContent);
        }

        return Status(HttpStatusCode.NotFound, "{\"code\":\"unmodelled\"}");
    }
}

internal sealed class TestProtector : ReelBolt.Shared.Inference.ISecretProtector
{
    public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public bool TryUnprotect(string? ciphertext, out string plaintext)
    {
        plaintext = string.Empty;
        if (ciphertext is null || !ciphertext.StartsWith("enc:", StringComparison.Ordinal)) return false;
        plaintext = Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[4..]));
        return true;
    }
}
