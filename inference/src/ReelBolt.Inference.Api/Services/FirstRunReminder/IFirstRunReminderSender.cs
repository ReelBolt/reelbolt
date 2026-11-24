using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Marketing;

namespace ReelBolt.Inference.Api.Services.FirstRunReminder;

public enum FirstRunReminderSendOutcome
{
    /// <summary>Nothing was sent (the no-op sender).</summary>
    NotSent,

    Sent,

    /// <summary>The provider refused; a stable code is in <see cref="FirstRunReminderSendResult.ErrorCode"/>.</summary>
    Failed,
}

public sealed record FirstRunReminderSendResult(FirstRunReminderSendOutcome Outcome, string? ErrorCode = null);

/// <summary>Sends one reminder. The default implementation sends nothing.</summary>
public interface IFirstRunReminderSender
{
    /// <summary>Null when the sender can send; else a stable code (for example <c>brevo_key_missing</c>). Checked before any claim.</summary>
    string? NotReadyReason { get; }

    Task<FirstRunReminderSendResult> SendAsync(FirstRunReminderMessage message, CancellationToken ct);
}

/// <summary>The default: sends nothing and logs nothing per user (the sweeper logs counts).</summary>
public sealed class NoOpFirstRunReminderSender : IFirstRunReminderSender
{
    public string? NotReadyReason => null;

    public Task<FirstRunReminderSendResult> SendAsync(FirstRunReminderMessage message, CancellationToken ct)
        => Task.FromResult(new FirstRunReminderSendResult(FirstRunReminderSendOutcome.NotSent));
}

/// <summary>
/// Brevo transactional email (<c>POST /v3/smtp/email</c>) over a plain HttpClient. Registered and resolved only with
/// <c>Enabled=true</c> and <c>DryRun=false</c>. The key (<c>BREVO_TRANSACTIONAL_API_KEY</c>) is never logged; a failure is
/// logged as a status code and the recipient's <c>rcpt=</c> ref only. Not retried: a reminder is at most once.
/// </summary>
public sealed class BrevoTransactionalFirstRunReminderSender(
    HttpClient http,
    IOptions<FirstRunReminderOptions> options,
    IOptions<MarketingCampaignOptions> campaign,
    ILogger<BrevoTransactionalFirstRunReminderSender> logger) : IFirstRunReminderSender
{
    public const string Endpoint = "https://api.brevo.com/v3/smtp/email";

    public string? NotReadyReason => string.IsNullOrWhiteSpace(ResolveKey()) ? "brevo_key_missing" : null;

    private string? ResolveKey()
        => string.IsNullOrWhiteSpace(options.Value.BrevoTransactionalApiKey)
            ? Environment.GetEnvironmentVariable("BREVO_TRANSACTIONAL_API_KEY")
            : options.Value.BrevoTransactionalApiKey;

    public async Task<FirstRunReminderSendResult> SendAsync(FirstRunReminderMessage message, CancellationToken ct)
    {
        string? key = ResolveKey();

        if (string.IsNullOrWhiteSpace(key))
        {
            return new FirstRunReminderSendResult(FirstRunReminderSendOutcome.Failed, "brevo_key_missing");
        }

        string from = string.IsNullOrWhiteSpace(options.Value.FromAddress) ? campaign.Value.FromAddress : options.Value.FromAddress!;
        Dictionary<string, object> body = new()
        {
            ["sender"] = new { name = options.Value.FromName, email = from },
            ["to"] = new[] { new { email = message.ToAddress } },
            ["subject"] = message.Subject,
            ["htmlContent"] = message.Html,
            ["textContent"] = message.Text,
        };
        if (message.UnsubscribeUrl is not null)
        {
            body["headers"] = new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{message.UnsubscribeUrl}>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
            };
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
            request.Headers.Add("api-key", key);
            request.Headers.Add("accept", "application/json");
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return new FirstRunReminderSendResult(FirstRunReminderSendOutcome.Sent);
            }

            logger.LogWarning("First-run reminder refused by Brevo status={Status} {Rcpt}", (int)response.StatusCode, message.RcptRef);
            return new FirstRunReminderSendResult(FirstRunReminderSendOutcome.Failed, $"brevo_http_{(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("First-run reminder send failed {Rcpt}: {Error}", message.RcptRef, MailLogRef.DescribeError(ex));
            return new FirstRunReminderSendResult(FirstRunReminderSendOutcome.Failed, "brevo_unreachable");
        }
    }
}
