using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// GitHub's webhook receiver: how a push reaches ReelBolt without anyone clicking anything.
///
/// <para>
/// <b>Anonymous by construction, authenticated by signature.</b> There is no session and no bearer
/// token — GitHub is the caller — so the only credential is the <c>X-Hub-Signature-256</c> HMAC over the
/// exact bytes of the body, verified in constant time against the app's webhook secret by
/// <see cref="IGitHubWebhookVerifier"/>. An unverified request is refused <b>before a single row is
/// written</b>: without that ordering this endpoint would be an unauthenticated write primitive into
/// somebody's project, which is the whole reason it is written this way.
/// </para>
///
/// <para>
/// This controller deliberately does <b>not</b> consult <c>IProjectAccessService</c>, and that is not an
/// omission: it has no caller identity to check, and the authorization it does have is stronger for its
/// purpose — a valid signature over a body that names a repository, and a repository that can only ever
/// be linked to one project. The project-scoped link API next door is where <c>IProjectAccessService</c>
/// governs, and the two meet at the link row.
/// </para>
///
/// <para>
/// <b>Responses.</b> <c>503</c> when the app is not configured (nothing can be verified, so nothing is
/// accepted), <c>401</c> for a missing or bad signature, <c>400</c> for a body that is not JSON or is
/// missing the delivery headers, <c>200</c> for a delivery that is settled and needs no work, and
/// <c>202</c> when a pull is owed and has been queued. GitHub retries any non-2xx, which is why a
/// duplicate is a <c>200</c> and not a <c>409</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/github")]
public class GitHubWebhookController : ControllerBase
{
    /// <summary>2 MB. A push payload is JSON prose about a commit; anything larger is not one.</summary>
    internal const int MaxBodyBytes = 2 * 1024 * 1024;

    private readonly InferenceApiDbContext _db;
    private readonly IGitHubWebhookVerifier _verifier;
    private readonly IGitHubDeliveryProcessor _processor;
    private readonly ITenantContext? _tenant;
    private readonly ILogger<GitHubWebhookController> _logger;

    public GitHubWebhookController(
        InferenceApiDbContext db,
        IGitHubWebhookVerifier verifier,
        IGitHubDeliveryProcessor processor,
        ILogger<GitHubWebhookController> logger,
        ITenantContext? tenant = null)
    {
        _db = db;
        _verifier = verifier;
        _processor = processor;
        _logger = logger;
        _tenant = tenant;
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        if (!_verifier.IsConfigured)
        {
            // Not "skip verification": an endpoint that cannot verify must not accept anything.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_not_configured" });
        }

        string raw;
        using (StreamReader reader = new(Request.Body, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync(ct);
        }

        string? signature = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!_verifier.TryVerify(raw, signature))
        {
            // The body itself is never logged: it is attacker-controlled text.
            _logger.LogWarning("Refused a GitHub webhook delivery with a missing or invalid X-Hub-Signature-256");
            return StatusCode(StatusCodes.Status401Unauthorized, new { error = "invalid_signature" });
        }

        string? eventName = Request.Headers["X-GitHub-Event"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(eventName))
            return BadRequest(new { error = "missing_event_header" });

        if (!IsJsonObject(raw))
            return BadRequest(new { error = "invalid_payload" });

        string deliveryId = Request.Headers["X-GitHub-Delivery"].FirstOrDefault() is { Length: > 0 } header
            ? header
            : ShortHash(raw);

        GitHubWebhookDelivery delivery = new()
        {
            Id = Guid.NewGuid(),
            DeliveryId = deliveryId,
            Event = eventName,
            RepositoryFullName = ReadRepositoryFullName(raw),
            InstallationId = ReadInstallationId(raw),
            PayloadJson = raw,
            ReceivedAt = DateTime.UtcNow
        };

        using IDisposable systemScope = _tenant.System();

        // GitHub's own idempotency key. Checked first (so the common redelivery never reaches an
        // insert) and the insert is still guarded, because two redeliveries can race.
        if (await _db.GitHubWebhookDeliveries.AnyAsync(d => d.DeliveryId == deliveryId, ct))
            return Ok(new { outcome = "duplicate" });

        _db.GitHubWebhookDeliveries.Add(delivery);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.Entry(delivery).State = EntityState.Detached;
            return Ok(new { outcome = "duplicate" });
        }

        GitHubDeliveryPlan plan = await _processor.PlanAsync(delivery, ct);
        if (plan.RequiresPull)
        {
            // The worker owns the pull: a repository can be large and GitHub gives up on a slow
            // response, so the delivery is durably queued (ProcessedAt stays null) and acknowledged.
            return Accepted(new { outcome = "queued", delivery = deliveryId });
        }

        delivery.ProcessedAt = DateTime.UtcNow;
        delivery.Outcome = plan.Outcome;
        await _db.SaveChangesAsync(ct);

        return Ok(new { outcome = plan.Outcome });
    }

    private static bool IsJsonObject(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadRepositoryFullName(string raw) => ReadNestedString(raw, "repository", "full_name");

    private static long? ReadInstallationId(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("installation", out JsonElement installation) &&
                   installation.ValueKind == JsonValueKind.Object &&
                   installation.TryGetProperty("id", out JsonElement id) &&
                   id.ValueKind == JsonValueKind.Number &&
                   id.TryGetInt64(out long value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadNestedString(string raw, string parent, string name)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty(parent, out JsonElement element) &&
                   element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out JsonElement inner) &&
                   inner.ValueKind == JsonValueKind.String
                ? inner.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A stand-in delivery id for the (never observed) case where GitHub omits its own header, so the
    /// dedupe still works: a byte-identical redelivery hashes to the same id and is a no-op.
    /// </summary>
    private static string ShortHash(string raw) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}
