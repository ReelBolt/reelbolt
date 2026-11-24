using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.ApiKeys;

public enum ApiKeyVerifyStatus
{
    Ok,
    Invalid,
    Revoked,
    Expired
}

public sealed record ApiKeyVerifyResult(ApiKeyVerifyStatus Status, ApiKey? Key)
{
    public static readonly ApiKeyVerifyResult Invalid = new(ApiKeyVerifyStatus.Invalid, null);
}

public sealed record CreateApiKeyCommand(
    Guid OrganizationId,
    Guid CreatedByUserId,
    string Name,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<Guid>? ProjectIds,
    DateTime? ExpiresAt,
    long? DailyCapMillis,
    long? WeeklyCapMillis,
    long? MonthlyCapMillis,
    DateTime? DailyCapWaivedAt = null);

/// <summary>The stored key and the plaintext token, which exists only in this result.</summary>
public sealed record CreatedApiKey(ApiKey Key, string Token);

public interface IApiKeyService
{
    /// <summary>Creates and saves a key. The plaintext token is returned once and never stored.</summary>
    Task<CreatedApiKey> CreateAsync(CreateApiKeyCommand command, CancellationToken ct);

    /// <summary>Verifies a presented token. Never throws for a bad token.</summary>
    Task<ApiKeyVerifyResult> VerifyAsync(string? token, CancellationToken ct);
}

public sealed class ApiKeyService(InferenceApiDbContext db, ILogger<ApiKeyService> logger, TimeProvider? time = null) : IApiKeyService
{
    // Hashed when the id is unknown so a miss costs the same as a wrong secret.
    private static readonly byte[] DummyHash = Encoding.ASCII.GetBytes(ApiKeyToken.Hash("rbk_decoy"));

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<CreatedApiKey> CreateAsync(CreateApiKeyCommand c, CancellationToken ct)
    {
        // L18: fail closed in the service, not only in the controller that normally validates scopes. A key
        // carrying a scope the guard cannot reason about would silently hold nothing.
        string? unknownScope = c.Scopes.FirstOrDefault(s => !ApiScopes.IsKnown(s));
        if (unknownScope is not null)
            throw new ArgumentException($"Unknown API key scope '{unknownScope}'.", nameof(c));

        ApiKeyToken.Generated generated = ApiKeyToken.Generate();
        DateTime now = _time.GetUtcNow().UtcDateTime;
        Guid keyRowId = Guid.NewGuid();
        Guid serviceUserId = Guid.NewGuid();

        // The key acts as its OWN hidden service account (a plain Member of this one org), so it survives its creator
        // leaving. No Personal organization is provisioned. One SaveChanges = one transaction for all three rows.
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = serviceUserId,
            Email = $"apikey+{generated.KeyId.ToLowerInvariant()}@service.invalid",
            DisplayName = c.Name,
            PasswordHash = null,
            IsAdmin = false,
            IsServiceAccount = true,
            MustChangePassword = false,
            EmailVerifiedAt = now,
            Locale = null,
            CreatedAt = now
        });
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            OrganizationId = c.OrganizationId,
            UserId = serviceUserId,
            Role = OrganizationRole.Member,
            CreatedAt = now
        });

        ApiKey key = new()
        {
            Id = keyRowId,
            ServiceUserId = serviceUserId,
            OrganizationId = c.OrganizationId,
            CreatedByUserId = c.CreatedByUserId,
            Name = c.Name,
            KeyId = generated.KeyId,
            SecretHash = ApiKeyToken.Hash(generated.Token),
            LastFour = generated.LastFour,
            Scopes = c.Scopes.Distinct(StringComparer.Ordinal).ToArray(),
            ProjectIds = c.ProjectIds?.Distinct().ToArray(),
            ExpiresAt = c.ExpiresAt,
            DailyCapMillis = c.DailyCapMillis,
            DailyCapWaivedAt = c.DailyCapWaivedAt,
            WeeklyCapMillis = c.WeeklyCapMillis,
            MonthlyCapMillis = c.MonthlyCapMillis,
            CreatedAt = now
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("API key {KeyId} created for organization {OrgId}", key.KeyId, key.OrganizationId);
        return new CreatedApiKey(key, generated.Token);
    }

    public async Task<ApiKeyVerifyResult> VerifyAsync(string? token, CancellationToken ct)
    {
        if (!ApiKeyToken.TryParse(token, out string keyId))
        {
            // Malformed: still do one hash so the cost does not reveal a format failure.
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ApiKeyToken.Hash(token ?? string.Empty)), DummyHash);
            return ApiKeyVerifyResult.Invalid;
        }

        // No tenant yet: the key is what establishes the organization.
        ApiKey? key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.KeyId == keyId, ct);
        byte[] presented = Encoding.ASCII.GetBytes(ApiKeyToken.Hash(token!));
        byte[] stored = key is null ? DummyHash : Encoding.ASCII.GetBytes(key.SecretHash);
        bool match = CryptographicOperations.FixedTimeEquals(presented, stored);
        if (key is null || !match)
        {
            logger.LogInformation("API key {KeyId} rejected: invalid", keyId);
            return ApiKeyVerifyResult.Invalid;
        }

        if (key.RevokedAt is not null)
        {
            logger.LogInformation("API key {KeyId} rejected: revoked", keyId);
            return new ApiKeyVerifyResult(ApiKeyVerifyStatus.Revoked, key);
        }

        if (key.ExpiresAt is { } expiresAt && DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc) <= _time.GetUtcNow().UtcDateTime)
        {
            logger.LogInformation("API key {KeyId} rejected: expired", keyId);
            return new ApiKeyVerifyResult(ApiKeyVerifyStatus.Expired, key);
        }

        return new ApiKeyVerifyResult(ApiKeyVerifyStatus.Ok, key);
    }
}
