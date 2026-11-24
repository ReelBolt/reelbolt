using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Brevo's marketing webhook. Anonymous, guarded by a static header token (Brevo signs nothing). A blank
/// configured token answers 404 before anything else; a wrong or missing one 401 with no detail. The webhook
/// can only reduce sending: <c>unsubscribe</c> and <c>spam</c> withdraw consent, <c>hard_bounce</c> marks the
/// address undeliverable, everything else is a 200 no-op. An unknown address answers 200 and writes nothing,
/// so the endpoint is no oracle for list membership. Nothing from the payload or the token is logged.
/// </summary>
[ApiController]
[Route("api/v1/marketing/webhooks/brevo")]
[AllowAnonymous]
public sealed class MarketingBrevoWebhookController(
    MarketingConsentService consents,
    IOptions<MarketingCampaignOptions> options,
    ILogger<MarketingBrevoWebhookController> logger) : ControllerBase
{
    public const string TokenHeader = "X-ReelBolt-Webhook-Token";

    /// <summary>
    /// The only bound on a delivery: every event in a body within it is processed (none is dropped). nginx
    /// <c>client_max_body_size</c> for this location matches it.
    /// </summary>
    public const int MaxBodyBytes = 1024 * 1024;

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        string? configured = options.Value.WebhookToken;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return NotFound();
        }

        if (!TokenMatches(configured, Request.Headers[TokenHeader].ToString()))
        {
            return Unauthorized();
        }

        if (Request.ContentLength is > MaxBodyBytes)
        {
            logger.LogWarning("Brevo webhook body over the {Limit} byte limit was refused (declared length {Length})", MaxBodyBytes, Request.ContentLength);
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        byte[]? body = await ReadBoundedAsync(ct);
        if (body is null)
        {
            logger.LogWarning("Brevo webhook body over the {Limit} byte limit was refused", MaxBodyBytes);
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        List<(string Event, string Email)> events;
        try
        {
            events = Parse(body);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        int failed = 0;
        foreach ((string ev, string email) in events)
        {
            try
            {
                switch (ev)
                {
                    case "unsubscribe":
                    case "unsubscribed":
                        await consents.WithdrawFromProviderAsync(email, MarketingConsentSource.BrevoUnsubscribe, ct);
                        break;
                    case "spam":
                        await consents.WithdrawFromProviderAsync(email, MarketingConsentSource.EmailComplaint, ct);
                        break;
                    case "hard_bounce":
                    case "hardbounce":
                        await consents.MarkUndeliverableAsync(email, "brevo_hard_bounce", ct);
                        break;
                    default:
                        break; // soft bounces and everything unknown change nothing
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failing event must not abandon the rest of the array: process the others, then fail the request so
                // Brevo may retry. The log line carries no address.
                failed++;
                logger.LogError("Brevo webhook event could not be applied: {Error}", MailLogRef.DescribeError(ex));
            }
        }

        if (failed > 0)
        {
            logger.LogError("Brevo webhook: {Failed} of {Total} events could not be applied; answering 500 so Brevo may retry", failed, events.Count);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Ok();
    }

    private static bool TokenMatches(string configured, string? supplied)
    {
        // Hash both so the comparison is over equal-length values.
        byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(a, b) && !string.IsNullOrEmpty(supplied);
    }

    private async Task<byte[]?> ReadBoundedAsync(CancellationToken ct)
    {
        using MemoryStream ms = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxBodyBytes)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static List<(string Event, string Email)> Parse(byte[] body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        List<(string, string)> events = [];
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.EnumerateArray(),
            JsonValueKind.Object => [doc.RootElement],
            _ => [],
        };
        foreach (JsonElement item in items)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("event", out JsonElement ev) || ev.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("email", out JsonElement em) || em.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string email = MarketingEmail.Normalize(em.GetString());
            if (email.Length is 0 or > MarketingEmail.MaxLength)
            {
                continue;
            }

            events.Add(((ev.GetString() ?? string.Empty).Trim().ToLowerInvariant(), email));
        }

        return events;
    }
}
