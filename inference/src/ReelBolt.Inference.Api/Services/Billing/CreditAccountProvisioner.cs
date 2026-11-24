using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>Outcome of <see cref="ICreditAccountProvisioner.EnsureTrialGrantAsync"/>.</summary>
public enum TrialProvisioningOutcome
{
    /// <summary>Billing is off (self-host): nothing is ever granted or blocked.</summary>
    Disabled,

    /// <summary>The caller is not an eligible verified Owner of a Personal workspace.</summary>
    NotEligible,

    /// <summary>This call granted the trial credits.</summary>
    Granted,

    /// <summary>The trial was already granted (to this org earlier, or to this email for another org).</summary>
    AlreadyClaimed,

    /// <summary>Provisioning failed internally; nothing is granted and the next call retries.</summary>
    Failed
}

/// <summary>Lazy, idempotent provisioning of a workspace's credit account (C6).</summary>
public interface ICreditAccountProvisioner
{
    /// <summary>
    /// Grants the one-time trial credits to <paramref name="organizationId"/> when the user is its
    /// verified Owner and the normalised email never claimed a trial before. Safe to call on every
    /// request; never throws (except on caller cancellation).
    /// </summary>
    Task<TrialProvisioningOutcome> EnsureTrialGrantAsync(Guid userId, Guid? organizationId, CancellationToken ct = default);
}

/// <summary>
/// C6: gives a verified Owner's Personal workspace the trial grant exactly once per normalised
/// email, forever. The <see cref="TrialClaim"/> insert and the ledger grant share one transaction on
/// a dedicated context, so a unique violation on the claim means "somebody already got it" and
/// nothing is granted. A claim that already points at this same org replays the idempotent grant
/// (a no-op once applied).
/// </summary>
public sealed class CreditAccountProvisioner : ICreditAccountProvisioner
{
    /// <summary>Config key: master switch. Off (default) is self-host: never grants, never blocks.</summary>
    public const string EnabledKey = "Billing:Enabled";

    /// <summary>Config key: trial size in whole credits; defaults to the <c>trial</c> plan's one-time credits (100).</summary>
    public const string TrialCreditsKey = "Billing:Trial:Credits";

    /// <summary>Config key: optional trial expiry in days; unset means the grant never expires.</summary>
    public const string TrialExpiryDaysKey = "Billing:Trial:ExpiryDays";

    /// <summary>Config key: kill switch for the one-time first-video grant (default on).</summary>
    public const string FirstVideoEnabledKey = "Billing:FirstVideoGrant:Enabled";

    /// <summary>Config key: first-video grant size in whole credits (default 750).</summary>
    public const string FirstVideoCreditsKey = "Billing:FirstVideoGrant:Credits";

    /// <summary>Config key: first-video grant expiry in days (default 30).</summary>
    public const string FirstVideoExpiryDaysKey = "Billing:FirstVideoGrant:ExpiryDays";

    /// <summary>Ledger note that marks the first-video grant (the billing summary finds it by this).</summary>
    public const string FirstVideoNote = "first-video";

    /// <summary>Idempotency key of an organization's first-video grant.</summary>
    public static string FirstVideoKey(Guid orgId) => $"firstvideo:{orgId:N}";

    private const long FallbackTrialMillis = 100_000;
    private const long DefaultFirstVideoCredits = 750;
    private const int DefaultFirstVideoExpiryDays = 30;

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<CreditAccountProvisioner> _logger;
    private readonly IFirstVideoEligibility? _eligibility;

    public CreditAccountProvisioner(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        ILogger<CreditAccountProvisioner> logger,
        TimeProvider? time = null,
        IFirstVideoEligibility? eligibility = null)
    {
        _eligibility = eligibility;
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Lowercase, trim, strip <c>+tag</c>; strip dots for Gmail and fold googlemail.com into gmail.com.</summary>
    public static string NormalizeEmail(string email)
    {
        string value = (email ?? string.Empty).Trim().ToLowerInvariant();
        int at = value.LastIndexOf('@');
        if (at <= 0)
        {
            return value;
        }

        string local = value[..at];
        string domain = value[(at + 1)..];
        int plus = local.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            local = local[..plus];
        }

        if (domain is "gmail.com" or "googlemail.com")
        {
            domain = "gmail.com";
            local = local.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        return $"{local}@{domain}";
    }

    /// <summary>Lowercase hex SHA-256 of the normalised email (the <c>trial_claims</c> key).</summary>
    public static string HashEmail(string email)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeEmail(email))));

    public async Task<TrialProvisioningOutcome> EnsureTrialGrantAsync(Guid userId, Guid? organizationId, CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>(EnabledKey))
        {
            return TrialProvisioningOutcome.Disabled;
        }

        if (organizationId is not Guid orgId)
        {
            return TrialProvisioningOutcome.NotEligible;
        }

        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            return await ProvisionAsync(db, userId, orgId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never block a request over a trial grant; the next call retries.
            _logger.LogWarning(ex, "Trial provisioning failed for organization {OrganizationId}", orgId);
            return TrialProvisioningOutcome.Failed;
        }
    }

    private async Task<TrialProvisioningOutcome> ProvisionAsync(InferenceApiDbContext db, Guid userId, Guid orgId, CancellationToken ct)
    {
        bool eligible = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(o => o.Id == orgId && o.Kind == OrganizationKind.Personal && o.DeletedAt == null, ct).ConfigureAwait(false)
            && await db.OrganizationMemberships.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(m => m.OrganizationId == orgId && m.UserId == userId && m.Role == OrganizationRole.Owner, ct).ConfigureAwait(false);
        if (!eligible)
        {
            return TrialProvisioningOutcome.NotEligible;
        }

        var user = await db.ApplicationUsers.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.EmailVerifiedAt, u.SignupIp })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (user is null || user.EmailVerifiedAt is null || string.IsNullOrWhiteSpace(user.Email))
        {
            return TrialProvisioningOutcome.NotEligible;
        }

        string hash = HashEmail(user.Email);
        long millis = await TrialMillisAsync(db, ct).ConfigureAwait(false);
        DateTime now = _time.GetUtcNow().UtcDateTime;
        DateTime? expiresAt = _configuration.GetValue<int?>(TrialExpiryDaysKey) is int days and > 0 ? now.AddDays(days) : null;

        // Decided before the transaction (DNS and HTTP must not hold it open) and only for a claim that is still free.
        // The sign-up IP is read here and never logged or stored; only the reason is.
        string? deniedReason = null;
        if (_eligibility is not null && _configuration.GetValue(FirstVideoEnabledKey, true) && await HolderAsync(db, hash, ct).ConfigureAwait(false) is null)
        {
            deniedReason = await _eligibility.DeniedReasonAsync(user.Email, user.SignupIp, ct).ConfigureAwait(false);
        }

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Check before inserting: every billing summary call lands here, and a rejected duplicate insert is logged as a
        // database error by EF even though it is expected. The unique index still settles a concurrent race.
        if (await HolderAsync(db, hash, ct).ConfigureAwait(false) is { } existing)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await ReplayAsync(existing).ConfigureAwait(false);
        }

        db.TrialClaims.Add(new TrialClaim { Id = Guid.NewGuid(), EmailSha256 = hash, OrganizationId = orgId, ClaimedAt = now, FirstVideoDeniedReason = deniedReason });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return await ReplayAsync(await HolderAsync(db, hash, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        CreditLedgerResult result = await GrantAsync(db, orgId, millis, expiresAt, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning("Trial grant refused for organization {OrganizationId}: {Error}", orgId, result.Error);
            return TrialProvisioningOutcome.Failed;
        }

        // The claim is NEW here (an existing holder returned above), so the one-time first-video grant rides in the
        // same transaction: both land or neither does.
        if (deniedReason is not null)
        {
            _logger.LogInformation("First-video grant refused for organization {OrganizationId}: {Reason}", orgId, deniedReason);
        }
        else if (_configuration.GetValue(FirstVideoEnabledKey, true))
        {
            long credits = _configuration.GetValue<long?>(FirstVideoCreditsKey) is long c and > 0 ? c : DefaultFirstVideoCredits;
            int fvDays = _configuration.GetValue<int?>(FirstVideoExpiryDaysKey) is int d and > 0 ? d : DefaultFirstVideoExpiryDays;
            CreditLedgerResult bonus = await new CreditLedger(db, _time).GrantAsync(
                orgId, CreditGrantSource.Promo, credits * 1000, now.AddDays(fvDays), FirstVideoKey(orgId),
                context: new CreditLedgerContext(Note: FirstVideoNote), ct: ct).ConfigureAwait(false);
            if (!bonus.Ok)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogWarning("First-video grant refused for organization {OrganizationId}: {Error}", orgId, bonus.Error);
                return TrialProvisioningOutcome.Failed;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result.Replayed ? TrialProvisioningOutcome.AlreadyClaimed : TrialProvisioningOutcome.Granted;

        async Task<TrialProvisioningOutcome> ReplayAsync(Guid? holder)
        {
            if (holder != orgId)
            {
                return TrialProvisioningOutcome.AlreadyClaimed;
            }

            // This org holds the claim: replay the idempotent grant (a no-op once applied).
            CreditLedgerResult replay = await GrantAsync(db, orgId, millis, expiresAt, ct).ConfigureAwait(false);
            return replay.Ok ? (replay.Replayed ? TrialProvisioningOutcome.AlreadyClaimed : TrialProvisioningOutcome.Granted) : TrialProvisioningOutcome.Failed;
        }
    }

    private static Task<Guid?> HolderAsync(InferenceApiDbContext db, string hash, CancellationToken ct)
        => db.TrialClaims.AsNoTracking().Where(c => c.EmailSha256 == hash)
            .Select(c => (Guid?)c.OrganizationId).FirstOrDefaultAsync(ct);

    private Task<CreditLedgerResult> GrantAsync(InferenceApiDbContext db, Guid orgId, long millis, DateTime? expiresAt, CancellationToken ct)
        => new CreditLedger(db, _time).GrantAsync(
            orgId, CreditGrantSource.Trial, millis, expiresAt, $"trial:{orgId:N}",
            context: new CreditLedgerContext(Note: "trial"), ct: ct);

    private async Task<long> TrialMillisAsync(InferenceApiDbContext db, CancellationToken ct)
    {
        if (_configuration.GetValue<long?>(TrialCreditsKey) is long credits and > 0)
        {
            return credits * 1000;
        }

        long fromPlan = await db.Plans.AsNoTracking().Where(p => p.Key == "trial")
            .Select(p => p.OneTimeCreditsMillis).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return fromPlan > 0 ? fromPlan : FallbackTrialMillis;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || (ex.InnerException?.GetType().Name == "SqliteException"
                && ex.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));
}
