using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Observability;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>A named 32-byte key, generated once and shared by every replica.</summary>
public interface IMarketingSecretStore
{
    /// <summary>The key stored under <paramref name="name"/>; generated and persisted on first use.</summary>
    /// <exception cref="InvalidOperationException">The key could not be read or persisted.</exception>
    byte[] GetKey(string name);
}

/// <summary>
/// Keys in <c>platform_secrets</c>, with the resolve-and-persist pattern of
/// <c>ConfirmationTokenService</c>: read the row; when absent, generate 32 random bytes, protect them
/// with <see cref="ISecretProtector"/> and <c>INSERT … ON CONFLICT (name) DO NOTHING</c> (replicas may
/// race; the first insert wins and everyone re-reads the winner). The key is never logged or returned
/// over the wire.
///
/// <para><b>Fail closed, never a process-local key.</b> The marketing keys sign unsubscribe links
/// that never expire and pepper the suppression list of erased accounts. A key that silently differs
/// per process would mint dead unsubscribe links or let an erased address back onto the list, so a
/// failure here throws (and is retried on the next call) instead of degrading.</para>
/// </summary>
public sealed class PlatformSecretKeyStore : IMarketingSecretStore
{
    private const int KeySizeBytes = 32;

    private readonly IServiceScopeFactory _scopes;
    private readonly ISecretProtector _protector;
    private readonly ILogger<PlatformSecretKeyStore> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public PlatformSecretKeyStore(IServiceScopeFactory scopes, ISecretProtector protector, ILogger<PlatformSecretKeyStore> logger)
    {
        _scopes = scopes;
        _protector = protector;
        _logger = logger;
    }

    public byte[] GetKey(string name)
    {
        if (_cache.TryGetValue(name, out byte[]? cached))
        {
            return cached;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(name, out cached))
            {
                return cached;
            }

            byte[] key = Resolve(name);
            _cache[name] = key;
            return key;
        }
    }

    private byte[] Resolve(string name)
    {
        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

            if (TryRead(db, name, out byte[] existing, out bool rowExists))
            {
                return existing;
            }

            if (!rowExists)
            {
                string protectedKey = _protector.Protect(Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeBytes)));
                db.Database.ExecuteSqlInterpolated(
                    $"INSERT INTO platform_secrets (name, value, created_at) VALUES ({name}, {protectedKey}, {DateTime.UtcNow}) ON CONFLICT (name) DO NOTHING");
                if (TryRead(db, name, out byte[] winner, out _))
                {
                    _logger.LogInformation("Generated the platform secret {SecretName}.", name.ForLog());
                    return winner;
                }
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or CryptographicException
            or System.Data.Common.DbException)
        {
            throw new InvalidOperationException($"The platform secret '{name}' could not be resolved.", ex);
        }

        // A row exists but this Data Protection key ring cannot decrypt it. Never overwrite it: that
        // would invalidate every link already sent. An operator restores the key ring.
        throw new InvalidOperationException(
            $"The platform secret '{name}' exists but cannot be decrypted with the current Data Protection key ring.");
    }

    private bool TryRead(InferenceApiDbContext db, string name, out byte[] key, out bool rowExists)
    {
        key = [];
        string? stored = db.PlatformSecrets.AsNoTracking()
            .Where(x => x.Name == name)
            .Select(x => x.Value)
            .FirstOrDefault();
        rowExists = stored is not null;
        if (string.IsNullOrWhiteSpace(stored) || !_protector.TryUnprotect(stored, out string base64))
        {
            return false;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(base64);
            if (decoded.Length == 0)
            {
                return false;
            }

            key = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
