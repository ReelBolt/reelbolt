using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing;

public sealed record CampaignContent(string? Subject, string? Html);

public sealed record CampaignDryRunResult(
    int RecipientCount, int SuppressedCount, IReadOnlyList<string> SampleMasked, bool FooterOk, IReadOnlyList<string> Errors, bool ListSendEnabled);

public sealed record CampaignSettings(bool FooterOk, bool ListSendEnabled, string? TestRecipientMasked, bool BrevoConfigured);

/// <summary>
/// Campaign drafts, dry-run, test send and the request to send. The send itself is the pipeline's job
/// (<see cref="MarketingCampaignPipeline"/>): this class only validates and queues.
/// </summary>
public sealed class MarketingCampaignService(
    InferenceApiDbContext db,
    MarketingConsentService consents,
    IBrevoCampaignClient brevo,
    IOptions<MarketingOptions> marketing,
    IOptions<CloudStorageOptions> cloudStorage,
    IOptions<MarketingCampaignOptions> options,
    ILogger<MarketingCampaignService> logger,
    TimeProvider? clock = null,
    ReelBolt.Shared.Inference.ISecretProtector? protector = null)
{
    /// <summary>
    /// The footer appended to every campaign, in one place. <c>{{ unsubscribe }}</c> is Brevo's own
    /// placeholder, replaced per recipient. The %TOKENS% are filled from configuration (HTML-encoded).
    /// </summary>
    public const string FooterHtmlTemplate =
        "<hr><p style=\"font-size:12px;color:#666\">%CONTROLLER%<br>%VATLINE%%ADDRESS%<br>%WHY%<br>"
        + "<a href=\"{{ unsubscribe }}\">Unsubscribe</a> | <a href=\"%PRIVACY%\">Privacy</a></p>";

    private const int MaxSamples = 3;
    private const int MaxHtmlBytes = 1_000_000;
    private const int MinHtmlChars = 10;

    // The per-admin hourly test-send limit lives in this process: with several API replicas each keeps its
    // own count, so the effective limit is TestSendsPerHour per replica. Brevo's own 50 tests/day still holds.
    private static readonly Dictionary<Guid, List<DateTimeOffset>> TestSends = [];
    private static readonly object Gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private MarketingCampaignOptions Opt => options.Value;

    public bool Enabled => Opt.Enabled;

    private string? BaseUrl => marketing.Value.ResolveBaseUrl(cloudStorage.Value);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ----- content -----

    public static IReadOnlyList<string> SubjectErrors(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return ["subject_missing"];
        if (subject.Length > 200) return ["subject_too_long"];
        if (subject.Any(char.IsControl)) return ["subject_invalid_characters"];
        return [];
    }

    public IReadOnlyList<string> Validate(CampaignContent c)
    {
        List<string> errors = [.. SubjectErrors(c.Subject)];
        if (string.IsNullOrWhiteSpace(c.Html)) errors.Add("html_missing");
        else if (c.Html.Trim().Length < MinHtmlChars) errors.Add("html_too_short");
        else if (Encoding.UTF8.GetByteCount(c.Html) + 2000 > MaxHtmlBytes) errors.Add("html_too_large");
        else
        {
            string? structure = StructureError(c.Html);
            if (structure is not null) errors.Add(structure);
        }
        if (string.IsNullOrWhiteSpace(Opt.FooterControllerName)) errors.Add("footer_controller_name_missing");
        if (string.IsNullOrWhiteSpace(Opt.FooterAddress)) errors.Add("footer_address_missing");
        if (BaseUrl is null) errors.Add("public_base_url_missing");
        return errors;
    }

    /// <summary>The refusal code when the HTML cannot carry the footer (too deep, no body), else null. See <see cref="CampaignFooterPlacer"/>.</summary>
    public static string? StructureError(string html)
    {
        try
        {
            CampaignFooterPlacer.Check(html);
            return null;
        }
        catch (CampaignRefusal r)
        {
            return r.Code;
        }
    }

    /// <summary>The body with the mandatory footer. Throws <see cref="CampaignRefusal"/> when the footer identity is missing.</summary>
    public string BuildHtml(CampaignContent content)
    {
        IReadOnlyList<string> errors = Validate(content);
        if (errors.Count > 0) throw new CampaignRefusal(errors[0]);

        string name = Opt.FooterControllerName!.Trim();
        string address = Opt.FooterAddress!.Trim();
        string vat = string.IsNullOrWhiteSpace(Opt.FooterVatNumber) ? string.Empty : "P.IVA " + Opt.FooterVatNumber.Trim();
        string footer = FooterHtmlTemplate
            .Replace("%CONTROLLER%", WebUtility.HtmlEncode($"ReelBolt is operated by {name}"), StringComparison.Ordinal)
            .Replace("%VATLINE%", vat.Length > 0 ? WebUtility.HtmlEncode(vat) + "<br>" : string.Empty, StringComparison.Ordinal)
            .Replace("%ADDRESS%", WebUtility.HtmlEncode(address), StringComparison.Ordinal)
            .Replace("%WHY%", WebUtility.HtmlEncode("You receive this because you opted in to ReelBolt news and offers."), StringComparison.Ordinal)
            .Replace("%PRIVACY%", WebUtility.HtmlEncode(BaseUrl + "/legal/privacy"), StringComparison.Ordinal);
        return CampaignFooterPlacer.Place(content.Html!, footer, [name, address]);
    }

    /// <summary>
    /// The exact HTML that would go to Brevo (body plus footer), or null with the first error code when it cannot be built
    /// (the same codes <see cref="BuildHtml"/> refuses with).
    /// </summary>
    public string? TryBuildHtml(CampaignContent content, out string? error)
    {
        IReadOnlyList<string> errors = Validate(content);
        if (errors.Count > 0)
        {
            error = errors[0];
            return null;
        }

        error = null;
        return BuildHtml(content);
    }

    public static string ContentHash(CampaignContent c)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((c.Subject ?? string.Empty).Trim() + "\n" + (c.Html ?? string.Empty))));

    public static CampaignContent ContentOf(MarketingCampaign c) => new(c.Subject, c.Html);

    // ----- drafts -----

    public async Task<IReadOnlyList<MarketingCampaign>> ListAsync(int limit, CancellationToken ct)
        => await db.MarketingCampaigns.AsNoTracking().OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);

    public Task<MarketingCampaign?> GetAsync(Guid id, CancellationToken ct)
        => db.MarketingCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>Creates a Draft. Throws <see cref="CampaignRefusal"/> with the first content problem that is about the request itself.</summary>
    public async Task<MarketingCampaign> CreateAsync(CampaignContent content, Guid adminUserId, CancellationToken ct)
    {
        RequireDraftable(content);
        DateTime now = Now;
        MarketingCampaign row = new()
        {
            Id = Guid.NewGuid(),
            Subject = content.Subject!.Trim(),
            Html = content.Html!,
            Status = MarketingCampaignStatus.Draft,
            CreatedBy = adminUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MarketingCampaigns.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>Edits a Draft and clears its test record. Null: no such campaign. Throws <c>not_draft</c> otherwise.</summary>
    public async Task<MarketingCampaign?> UpdateAsync(Guid id, CampaignContent content, CancellationToken ct)
    {
        RequireDraftable(content);
        string subject = content.Subject!.Trim();
        string html = content.Html!;
        DateTime now = Now;
        int rows = await db.MarketingCampaigns.Where(c => c.Id == id && c.Status == MarketingCampaignStatus.Draft)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Subject, subject)
                .SetProperty(c => c.Html, html)
                .SetProperty(c => c.TestSentAt, (DateTime?)null)
                .SetProperty(c => c.TestContentHash, (string?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);
        if (rows == 0)
        {
            if (!await db.MarketingCampaigns.AnyAsync(c => c.Id == id, ct)) return null;
            throw new CampaignRefusal("not_draft");
        }

        return await GetAsync(id, ct);
    }

    /// <summary>Deletes a Draft. False: no such campaign. Throws <c>not_draft</c> otherwise.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        int rows = await db.MarketingCampaigns.Where(c => c.Id == id && c.Status == MarketingCampaignStatus.Draft).ExecuteDeleteAsync(ct);
        if (rows > 0) return true;
        if (!await db.MarketingCampaigns.AnyAsync(c => c.Id == id, ct)) return false;
        throw new CampaignRefusal("not_draft");
    }

    private static void RequireDraftable(CampaignContent content)
    {
        IReadOnlyList<string> subject = SubjectErrors(content.Subject);
        if (subject.Count > 0) throw new CampaignRefusal(subject[0]);
        if (string.IsNullOrWhiteSpace(content.Html)) throw new CampaignRefusal("html_missing");
        if (Encoding.UTF8.GetByteCount(content.Html) > MaxHtmlBytes) throw new CampaignRefusal("html_too_large");
        if (StructureError(content.Html) is { } structure) throw new CampaignRefusal(structure);
    }

    // ----- dry run, settings -----

    /// <summary>Counts and masked samples for the sendable set. Sends nothing and never calls Brevo.</summary>
    public async Task<CampaignDryRunResult?> DryRunAsync(Guid id, Guid adminUserId, CancellationToken ct)
    {
        MarketingCampaign? c = await GetAsync(id, ct);
        if (c is null) return null;
        List<string> errors = [.. Validate(ContentOf(c))];
        bool footerOk = !errors.Any(e => e.StartsWith("footer_", StringComparison.Ordinal)) && BaseUrl is not null;
        (IReadOnlyList<(Guid ConsentId, string Email)> recipients, int suppressed) = await consents.SendableRecipientsAsync(ct);
        string[] samples = recipients.Take(MaxSamples).Select(r => Mask(r.Email)).ToArray();
        logger.LogInformation(
            "Marketing campaign dry-run by {UserId}: campaign={CampaignId} recipients={Recipients} suppressed={Suppressed} errors={Errors}",
            adminUserId, id, recipients.Count, suppressed, errors.Count);
        return new CampaignDryRunResult(recipients.Count, suppressed, samples, footerOk, errors, Opt.ListSendEnabled);
    }

    public CampaignSettings Settings()
    {
        bool footerOk = !string.IsNullOrWhiteSpace(Opt.FooterControllerName) && !string.IsNullOrWhiteSpace(Opt.FooterAddress) && BaseUrl is not null;
        return new CampaignSettings(
            footerOk,
            Opt.ListSendEnabled,
            string.IsNullOrWhiteSpace(Opt.TestRecipient) ? null : Mask(Opt.TestRecipient.Trim()),
            !string.IsNullOrWhiteSpace(Opt.BrevoCampaignApiKey) && Opt.BrevoListId > 0 && Opt.BrevoTestListId > 0);
    }

    // ----- test send -----

    /// <summary>
    /// A campaign with no recipients, the configured test recipient imported into the test list, one
    /// sendTest to that single address, then the campaign is deleted. The recipient never comes from a request.
    /// Null: no such campaign. Records the content hash on success.
    /// </summary>
    public async Task<bool> TestSendAsync(Guid id, Guid adminUserId, CancellationToken ct)
    {
        MarketingCampaign? c = await GetAsync(id, ct);
        if (c is null) return false;
        if (c.Status != MarketingCampaignStatus.Draft) throw new CampaignRefusal("not_draft");
        if (string.IsNullOrWhiteSpace(Opt.TestRecipient)) throw new CampaignRefusal("test_recipient_missing");
        CampaignContent content = ContentOf(c);
        IReadOnlyList<string> errors = Validate(content);
        if (errors.Count > 0) throw new CampaignRefusal(errors[0]);
        RequireBrevoConfigured(listNeeded: false);
        if (Opt.BrevoTestListId <= 0) throw new CampaignRefusal("brevo_test_list_missing");
        ReserveTestSend(adminUserId);

        string recipient = MarketingEmail.Normalize(Opt.TestRecipient);

        // The test recipient is imported into Brevo, so it falls under the same write-ahead rule as the send list: if a
        // consent row exists for this address its sync flag is set BEFORE the import, so a purge stages the erasure and
        // the contact cannot outlive the person. If no row exists the address is the operator's own configured value
        // (MARKETING_TEST_RECIPIENT), not personal data held on a consent list, and there is nothing to erase.
        await db.MarketingConsents.Where(x => x.Email == recipient && x.BrevoSyncedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BrevoSyncedAt, Now), ct);
        bool hadConsentRow = await db.MarketingConsents.AsNoTracking().AnyAsync(x => x.Email == recipient, ct);

        long? campaignId = null;
        try
        {
            long importProcess = await brevo.ImportAsync(Opt.BrevoTestListId, [new BrevoContactInput(recipient)], ct);
            await MarketingCampaignPipeline.WaitForProcessAsync(brevo, importProcess, Opt, ct);
            campaignId = await brevo.CreateCampaignAsync(
                new BrevoCampaignDraft($"reelbolt-test-{c.Id:N}", "[TEST] " + c.Subject.Trim(), BuildHtml(content), Opt.FromAddress, Opt.FromName, []), ct);
            await brevo.SendTestAsync(campaignId.Value, [recipient], ct);
        }
        finally
        {
            if (campaignId is { } created)
            {
                try
                {
                    await brevo.DeleteCampaignAsync(created, CancellationToken.None);
                }
                catch (BrevoCampaignException)
                {
                    // best effort
                }
            }

            // The person was purged while the test recipient was at Brevo: the purge may have read the row before the flag
            // and staged nothing, so stage the erasure here with the address this call holds.
            if (hadConsentRow && protector is not null)
            {
                try
                {
                    if (!await db.MarketingConsents.AsNoTracking().AnyAsync(x => x.Email == recipient, CancellationToken.None))
                    {
                        await MarketingCampaignPipeline.StageMissingAsync(db, protector, [recipient], Now, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("Marketing test send could not re-stage the test recipient's erasure: {Error}", MailLogRef.DescribeError(ex));
                }
            }
        }

        string hash = ContentHash(content);
        DateTime now = Now;
        await db.MarketingCampaigns.Where(x => x.Id == id && x.Status == MarketingCampaignStatus.Draft && x.Subject == c.Subject && x.Html == c.Html)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TestSentAt, now).SetProperty(x => x.TestContentHash, hash).SetProperty(x => x.UpdatedAt, now), ct);
        logger.LogInformation("Marketing campaign test send by {UserId}: campaign={CampaignId}", adminUserId, id);
        return true;
    }

    // ----- send request -----

    /// <summary>
    /// Queues a Draft for the worker. Refusal order: list_send_disabled, not_draft, content errors,
    /// brevo_key_missing, brevo_list_missing, test_send_required, recipient_count_changed, no_recipients, unresolved_send, campaign_active. Null: no such campaign.
    /// </summary>
    public async Task<MarketingCampaign?> RequestSendAsync(Guid id, int confirmRecipientCount, Guid adminUserId, CancellationToken ct)
    {
        if (!Opt.ListSendEnabled) throw new CampaignRefusal("list_send_disabled");
        MarketingCampaign? c = await GetAsync(id, ct);
        if (c is null) return null;
        if (c.Status != MarketingCampaignStatus.Draft) throw new CampaignRefusal("not_draft");
        CampaignContent content = ContentOf(c);
        IReadOnlyList<string> errors = Validate(content);
        if (errors.Count > 0) throw new CampaignRefusal(errors[0]);
        RequireBrevoConfigured(listNeeded: true);
        if (c.TestSentAt is null || c.TestContentHash != ContentHash(content)) throw new CampaignRefusal("test_send_required");
        (IReadOnlyList<(Guid ConsentId, string Email)> recipients, _) = await consents.SendableRecipientsAsync(ct);
        if (recipients.Count != confirmRecipientCount) throw new CampaignRefusal("recipient_count_changed");
        if (recipients.Count == 0) throw new CampaignRefusal("no_recipients");
        // Both gates are also part of the conditional update below (NOT EXISTS), so they are one atomic step; these reads
        // only pick the stable refusal code.
        if (await db.MarketingCampaigns.AnyAsync(x => x.Status == MarketingCampaignStatus.SendUnknown, ct)) throw new CampaignRefusal("unresolved_send");
        if (await HasActiveAsync(ct)) throw new CampaignRefusal("campaign_active");

        DateTime now = Now;
        int count = recipients.Count;
        int rows;
        try
        {
            rows = await db.MarketingCampaigns
                .Where(x => x.Id == id && x.Status == MarketingCampaignStatus.Draft
                    && x.Subject == c.Subject && x.Html == c.Html && x.TestContentHash == c.TestContentHash)
                .Where(x => !db.MarketingCampaigns.Any(o => o.Id != id && (o.Status == MarketingCampaignStatus.SendUnknown
                    || o.Status == MarketingCampaignStatus.Queued || o.Status == MarketingCampaignStatus.Syncing || o.Status == MarketingCampaignStatus.Sending)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, MarketingCampaignStatus.Queued)
                    .SetProperty(x => x.ConfirmedRecipientCount, count)
                    .SetProperty(x => x.FailureCode, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now), ct);
        }
        catch (Exception e) when (IsActiveIndexViolation(e))
        {
            // the partial unique index: another campaign became active between the check and the update
            throw new CampaignRefusal("campaign_active");
        }

        // One conditional update on status, subject, html and the tested hash: an edit (which clears the test record)
        // or a second request landing after the checks above matches no row.
        if (rows == 0)
        {
            MarketingCampaign? now2 = await GetAsync(id, ct);
            if (now2 is null) return null;
            if (await db.MarketingCampaigns.AnyAsync(x => x.Status == MarketingCampaignStatus.SendUnknown, ct)) throw new CampaignRefusal("unresolved_send");
            if (await HasActiveAsync(ct)) throw new CampaignRefusal("campaign_active");
            throw new CampaignRefusal(now2.Status != MarketingCampaignStatus.Draft ? "not_draft" : "test_send_required");
        }

        logger.LogInformation("Marketing campaign send queued by {UserId}: campaign={CampaignId} confirmed={Recipients}", adminUserId, id, count);
        return await GetAsync(id, ct);
    }

    /// <summary>The partial unique index that allows one active campaign (see the AddMarketingCampaigns migration).</summary>
    internal const string ActiveIndexName = "ix_marketing_campaigns_one_active";

    /// <summary>
    /// A unique violation (SQLSTATE 23505, possibly wrapped) on exactly the one-active-campaign index. Any other database
    /// failure, including a <see cref="DbUpdateException"/> for something else, is not a "campaign is active" answer.
    /// </summary>
    internal static bool IsActiveIndexViolation(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is Npgsql.PostgresException { SqlState: "23505" } pg && pg.ConstraintName == ActiveIndexName) return true;
        }

        return false;
    }

    private Task<bool> HasActiveAsync(CancellationToken ct)
        => db.MarketingCampaigns.AnyAsync(c => c.Status == MarketingCampaignStatus.Queued
            || c.Status == MarketingCampaignStatus.Syncing || c.Status == MarketingCampaignStatus.Sending, ct);

    // ----- resolve, duplicate -----

    /// <summary>
    /// A platform admin's decision on a SendUnknown campaign (or a Sending one older than <c>SendingResolveAfterHours</c>) after checking Brevo by hand: <c>sent</c> or <c>notSent</c>.
    /// Calls Brevo nothing. Null: no such campaign. Throws <c>not_send_unknown</c> from any other status, <c>outcome_invalid</c> for any other outcome.
    /// </summary>
    public async Task<MarketingCampaign?> ResolveAsync(Guid id, string? outcome, Guid adminUserId, CancellationToken ct)
    {
        bool sent = outcome switch
        {
            "sent" => true,
            "notSent" => false,
            _ => throw new CampaignRefusal("outcome_invalid"),
        };
        DateTime now = Now;
        // A Sending row Brevo keeps in review (or reports with a status nobody maps) would stay active for ever: after a
        // configurable age an admin may settle it too. Younger than that it is a 409 as before.
        DateTime sendingBefore = now.AddHours(-Math.Max(1, Opt.SendingResolveAfterHours));
        int rows = sent
            ? await db.MarketingCampaigns.Where(x => x.Id == id && (x.Status == MarketingCampaignStatus.SendUnknown
                    || (x.Status == MarketingCampaignStatus.Sending && x.UpdatedAt <= sendingBefore)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MarketingCampaignStatus.Sent)
                    .SetProperty(x => x.FailureCode, (string?)null).SetProperty(x => x.UpdatedAt, now), ct)
            : await db.MarketingCampaigns.Where(x => x.Id == id && (x.Status == MarketingCampaignStatus.SendUnknown
                    || (x.Status == MarketingCampaignStatus.Sending && x.UpdatedAt <= sendingBefore)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MarketingCampaignStatus.Failed)
                    .SetProperty(x => x.FailureCode, "not_sent").SetProperty(x => x.UpdatedAt, now), ct);
        if (rows == 0)
        {
            if (!await db.MarketingCampaigns.AnyAsync(x => x.Id == id, ct)) return null;
            throw new CampaignRefusal("not_send_unknown");
        }

        logger.LogWarning("Marketing campaign send resolved by {UserId}: campaign={CampaignId} outcome={Outcome}", adminUserId, id, outcome);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// A new Draft with the same subject and HTML and no test record, from a Draft, Failed or Sent campaign (the statuses the
    /// page offers it for). Null: no such campaign. A Queued, Syncing or Sending campaign answers <c>campaign_active</c> and a
    /// SendUnknown one <c>unresolved_send</c>: copying a campaign that may be going out invites a second send.
    /// </summary>
    public async Task<MarketingCampaign?> DuplicateAsync(Guid id, Guid adminUserId, CancellationToken ct)
    {
        MarketingCampaign? source = await GetAsync(id, ct);
        if (source is null) return null;
        if (source.Status is MarketingCampaignStatus.Queued or MarketingCampaignStatus.Syncing or MarketingCampaignStatus.Sending) throw new CampaignRefusal("campaign_active");
        if (source.Status == MarketingCampaignStatus.SendUnknown) throw new CampaignRefusal("unresolved_send");
        DateTime now = Now;
        MarketingCampaign copy = new()
        {
            Id = Guid.NewGuid(),
            Subject = source.Subject,
            Html = source.Html,
            Status = MarketingCampaignStatus.Draft,
            CreatedBy = adminUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MarketingCampaigns.Add(copy);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Marketing campaign duplicated by {UserId}: source={SourceId} copy={CampaignId}", adminUserId, id, copy.Id);
        return copy;
    }

    // ----- results -----

    /// <summary>Reads Brevo's status and statistics; <c>sent</c> moves a Sending row to Sent. Null: no such campaign.</summary>
    public async Task<MarketingCampaign?> RefreshResultsAsync(Guid id, CancellationToken ct)
    {
        MarketingCampaign? c = await GetAsync(id, ct);
        if (c is null) return null;
        if (c.BrevoCampaignId is not { } brevoId || c.Status is not (MarketingCampaignStatus.Sending or MarketingCampaignStatus.Sent or MarketingCampaignStatus.SendUnknown))
        {
            throw new CampaignRefusal("nothing_to_refresh");
        }

        BrevoCampaignInfo info = await brevo.GetCampaignAsync(brevoId, ct);
        DateTime now = Now;
        string? stats = info.StatsJson;
        if (stats is not null)
        {
            await db.MarketingCampaigns.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.StatsJson, stats).SetProperty(x => x.StatsAt, now), ct);
        }
        if (info.Status == "sent")
        {
            await db.MarketingCampaigns.Where(x => x.Id == id && (x.Status == MarketingCampaignStatus.Sending || x.Status == MarketingCampaignStatus.SendUnknown))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MarketingCampaignStatus.Sent).SetProperty(x => x.FailureCode, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now), ct);
        }
        else if (c.Status == MarketingCampaignStatus.SendUnknown)
        {
            // Only "sent" settles without a human. A Brevo "draft" settles nothing here (it may still be processing a
            // sendNow); the only way to Failed not_sent is POST {id}/resolve. A campaign Brevo reports as running goes back to
            // Sending, which stays active.
            if (info.Status is "in_process" or "queued")
            {
                try
                {
                    await db.MarketingCampaigns.Where(x => x.Id == id && x.Status == MarketingCampaignStatus.SendUnknown)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MarketingCampaignStatus.Sending)
                            .SetProperty(x => x.FailureCode, (string?)null).SetProperty(x => x.UpdatedAt, now), ct);
                }
                catch (Exception e) when (IsActiveIndexViolation(e))
                {
                    // another campaign became active meanwhile: a stable 409, not a 500
                    throw new CampaignRefusal("campaign_active");
                }
            }
        }
        else if (c.Status == MarketingCampaignStatus.Sending)
        {
            // A Sending row must not stay active forever, but nobody can tell what was delivered when Brevo says draft,
            // stopped, archived or no longer knows it: it becomes SendUnknown and a human decides.
            if (info.Status is "draft" or "suspended" or "cancelled" or "cancelling" or "archive" or "not_found")
            {
                await db.MarketingCampaigns.Where(x => x.Id == id && x.Status == MarketingCampaignStatus.Sending)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MarketingCampaignStatus.SendUnknown)
                        .SetProperty(x => x.FailureCode, "send_unknown").SetProperty(x => x.UpdatedAt, now), ct);
            }
        }

        return await GetAsync(id, ct);
    }

    /// <summary>Refreshes every Sending row; one failure does not stop the others.</summary>
    public async Task RefreshAllSendingAsync(CancellationToken ct)
    {
        List<Guid> ids = await db.MarketingCampaigns.AsNoTracking().Where(c => c.Status == MarketingCampaignStatus.Sending && c.BrevoCampaignId != null)
            .Select(c => c.Id).ToListAsync(ct);
        foreach (Guid id in ids)
        {
            try
            {
                await RefreshResultsAsync(id, ct);
            }
            catch (Exception ex) when (ex is BrevoCampaignException or CampaignRefusal)
            {
                logger.LogWarning("Campaign results refresh failed: campaign={CampaignId} code={Code}", id, ex.Message);
            }
        }
    }

    public static JsonElement? ParseStats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ----- helpers -----

    private void RequireBrevoConfigured(bool listNeeded)
    {
        if (string.IsNullOrWhiteSpace(Opt.BrevoCampaignApiKey)) throw new CampaignRefusal("brevo_key_missing");
        if (listNeeded && Opt.BrevoListId <= 0) throw new CampaignRefusal("brevo_list_missing");
    }

    public static string Mask(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return "***";
        int dot = email.LastIndexOf('.');
        string tld = dot > at ? email[dot..] : string.Empty;
        return $"{email[0]}***@{email[at + 1]}***{tld}";
    }

    private void ReserveTestSend(Guid adminUserId)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        lock (Gate)
        {
            if (!TestSends.TryGetValue(adminUserId, out List<DateTimeOffset>? list)) TestSends[adminUserId] = list = [];
            list.RemoveAll(t => t <= now.AddHours(-1));
            if (list.Count >= Math.Max(1, Opt.TestSendsPerHour)) throw new CampaignRefusal("rate_limited");
            list.Add(now);
        }
    }
}

/// <summary>A refusal with a stable code (never carries an address).</summary>
public sealed class CampaignRefusal(string code) : Exception(code)
{
    public string Code => code;
}
