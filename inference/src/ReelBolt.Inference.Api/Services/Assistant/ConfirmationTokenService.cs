using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.DataProtection;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// HMAC-SHA256 confirmation tokens, keyed from a secret resolved through <see cref="ISecretProtector"/>.
///
/// <para><b>The key.</b> Read from <c>Assistant:ConfirmationTokenKey</c> (a Data-Protection-protected,
/// base64 key) when an operator sets one. When it is absent the key is generated once, protected and
/// persisted to <c>Assistant:ConfirmationTokenKeyFile</c> — defaulting to
/// <c>&lt;DataProtection:KeysPath&gt;/assistant-confirmation-token</c>, i.e. the shared <c>dpkeys</c>
/// volume both services already mount — so every replica and every restart of this service derives
/// the same key. The key is never hardcoded, never logged, and never returned; only its role in the
/// MAC is observable. Everything here is lazy, so constructing the service (at DI validation time,
/// or in a test) never touches the filesystem.</para>
///
/// <para><b>The one unkeyed operation.</b> <see cref="ApplyWorkflowOperation"/> predates this service
/// and its token is a plain SHA-256 of the canonical payload — no key, no user. It must stay
/// byte-identical to the digest <c>Services/Workflows/WorkflowEditDiffService.cs</c> has always
/// produced, because a token proposed before a deploy has to stay acceptable after it. What pins
/// that byte value is
/// <c>tests/ReelBolt.WorkflowEngine.Tests/Services/ConfirmationTokenServiceTests.cs</c>, whose
/// <c>Compute_for_ApplyWorkflow_reproduces_the_legacy_keyless_digest_byte_for_byte</c> asserts the
/// literal digest and whose <c>Verify_accepts_the_token_the_real_diff_service_mints</c> asserts that
/// the token <c>WorkflowEditDiffService</c> mints equals what this service computes for the same
/// legacy payload. No decision record states it. So this
/// service deliberately reproduces that exact digest for that one operation name, and
/// nothing else: every other operation is HMAC-SHA256 over a keyed, user-bound message. The carve-out
/// is a compatibility obligation, not a template — new mutating tools must not copy it.</para>
///
/// <para><b>The organization binding (A11).</b> The keyed overloads take the acting organization as a
/// fourth input, for operations that act on one organization's own resource (a custom agent's skill
/// assignment). It is read from the live <c>ICurrentUser</c> at BOTH ends — when the proposal is
/// minted and when it is verified — so a caller who switched organizations between the two halves
/// cannot apply a proposal that was made in the other one. A <c>null</c> organization frames exactly
/// as the three-argument overload always has, so no pre-existing token changes value.</para>
/// </summary>
public sealed class ConfirmationTokenService : IConfirmationTokenService
{
    /// <summary>
    /// The operation name whose token must stay byte-identical to the keyless SHA-256 digest
    /// <c>WorkflowEditDiffService</c> has always produced. See the class remarks.
    /// </summary>
    public const string ApplyWorkflowOperation = "ApplyWorkflow";

    private const string KeyConfigPath = "Assistant:ConfirmationTokenKey";
    private const string KeyFileConfigPath = "Assistant:ConfirmationTokenKeyFile";
    private const string DataProtectionKeysPathConfigPath = "DataProtection:KeysPath";
    private const string DefaultKeysPath = "/keys";
    private const string DefaultKeyFileName = "assistant-confirmation-token";
    private const string DatabaseSecretName = "assistant-confirmation-token";
    private const int KeySizeBytes = 32;

    private readonly ISecretProtector? _protector;
    private readonly IConfiguration? _configuration;
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger<ConfirmationTokenService> _logger;
    private readonly object _gate = new();

    private byte[]? _key;

    public ConfirmationTokenService(
        ISecretProtector protector,
        IConfiguration configuration,
        ILogger<ConfirmationTokenService> logger,
        IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? NullLogger<ConfirmationTokenService>.Instance;
    }

    private ConfirmationTokenService(byte[] key)
    {
        _key = key;
        _logger = NullLogger<ConfirmationTokenService>.Instance;
    }

    /// <summary>
    /// A process-local instance with a random key, for call sites that are not built by the DI
    /// container (existing unit tests constructing <c>WorkflowEditDiffService</c> with a DbContext).
    /// It mints and verifies consistently within the process, but shares no key with the container's
    /// singleton — which is exactly why production must inject the registered one.
    /// </summary>
    public static ConfirmationTokenService CreateEphemeral() => new(RandomNumberGenerator.GetBytes(KeySizeBytes));

    public string Compute(string operation, string canonicalPayload, Guid userId) =>
        Compute(operation, canonicalPayload, userId, organizationId: null);

    public string Compute(string operation, string canonicalPayload, Guid userId, Guid? organizationId)
    {
        operation ??= string.Empty;
        canonicalPayload ??= string.Empty;

        // The one unkeyed carve-out, and it deliberately ignores BOTH the key and the organization:
        // its digest is a compatibility obligation (see the class remarks), so nothing may be added
        // to it — not even for an organization-bound caller.
        if (operation == ApplyWorkflowOperation)
        {
            return ToToken(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)));
        }

        byte[] mac = HMACSHA256.HashData(GetKey(), BuildMessage(operation, canonicalPayload, userId, organizationId));
        return ToToken(mac);
    }

    public bool Verify(string operation, string canonicalPayload, Guid userId, string presented) =>
        Verify(operation, canonicalPayload, userId, organizationId: null, presented);

    public bool Verify(string operation, string canonicalPayload, Guid userId, Guid? organizationId, string presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Encoding.UTF8.GetBytes(Compute(operation, canonicalPayload, userId, organizationId));
        }
        catch (Exception)
        {
            // Fail closed. Nothing in Compute is expected to throw, but a verification path that can
            // 500 on a hostile input is worse than one that says "not confirmed".
            return false;
        }

        // FixedTimeEquals — not string == — so a byte-by-byte probe of the token learns nothing from
        // response timing. It returns false (never throws) when the lengths differ, which covers a
        // truncated or padded token.
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(presented));
    }

    private static string ToToken(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>
    /// Length-prefixed framing of the inputs. Without the explicit lengths, the operation name and
    /// the payload could be re-split at their separator and a token minted for one pair would verify
    /// against another.
    /// </summary>
    /// <remarks>
    /// An <paramref name="organizationId"/> of <c>null</c> appends nothing, so the framed message —
    /// and therefore every token minted before this overload existed — is unchanged. A non-null one
    /// is appended after a newline; that stays unambiguous because the user id before it is a fixed
    /// 36-character "D" GUID and the payload before that is length-prefixed, so no two distinct
    /// (operation, payload, user, organization) tuples can frame to the same string.
    /// </remarks>
    private static byte[] BuildMessage(string operation, string canonicalPayload, Guid userId, Guid? organizationId)
    {
        string framed = string.Concat(
            operation.Length.ToString(CultureInfo.InvariantCulture), ":", operation, "\n",
            canonicalPayload.Length.ToString(CultureInfo.InvariantCulture), ":", canonicalPayload, "\n",
            userId.ToString("D"),
            organizationId is { } org ? "\n" + org.ToString("D") : string.Empty);

        return Encoding.UTF8.GetBytes(framed);
    }

    private byte[] GetKey()
    {
        byte[]? cached = Volatile.Read(ref _key);
        if (cached is not null)
        {
            return cached;
        }

        lock (_gate)
        {
            return _key ??= ResolveKey();
        }
    }

    private byte[] ResolveKey()
    {
        // 1. An operator-supplied key wins, so a deployment can pin one deliberately.
        string? configured = _configuration![KeyConfigPath];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (TryDecodeKey(configured, out byte[] configuredKey))
            {
                return configuredKey;
            }

            // Fail closed rather than silently keeping a second key: tokens minted before this
            // service could read the configured key are rejected, which is recoverable (re-propose),
            // whereas a silent divergence would be invisible.
            _logger.LogWarning(
                "{ConfigPath} is set but could not be decrypted with the current Data Protection key ring; "
                + "falling back to the persisted key. Tokens minted under the configured key will be rejected.",
                KeyConfigPath);
        }

        // 2a. DataProtection:Store=Database: the key lives in platform_secrets, shared by every replica
        // with no /keys volume. An existing key file is imported on first use so tokens issued before
        // the switch keep verifying.
        string keyFilePath = ResolveKeyFilePath();
        if (_scopes is not null && DataProtectionRegistration.IsDatabaseStore(_configuration))
        {
            byte[]? fromDatabase = TryResolveDatabaseKey(keyFilePath);
            if (fromDatabase is not null)
            {
                return fromDatabase;
            }
        }

        // 2b. Otherwise the key persisted with this service (first run generated it).
        try
        {
            if (File.Exists(keyFilePath)
                && TryDecodeKey(File.ReadAllText(keyFilePath).Trim(), out byte[] persistedKey))
            {
                return persistedKey;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning(ex, "Could not read the persisted assistant confirmation-token key at {KeyFilePath}.", keyFilePath);
        }

        // 3. First run: generate once and persist, so a restart reuses the same key.
        byte[] generated = RandomNumberGenerator.GetBytes(KeySizeBytes);
        PersistKey(keyFilePath, generated);
        return generated;
    }

    private byte[]? TryResolveDatabaseKey(string keyFilePath)
    {
        try
        {
            using IServiceScope scope = _scopes!.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

            if (TryReadDatabaseKey(db, out byte[] existing))
            {
                return existing;
            }

            // Prefer the key a file-based deployment already issued tokens under; else a fresh one.
            byte[]? candidate = null;
            if (File.Exists(keyFilePath) && TryDecodeKey(File.ReadAllText(keyFilePath).Trim(), out byte[] fileKey))
            {
                candidate = fileKey;
                _logger.LogInformation("Importing the assistant confirmation-token key from {KeyFilePath} into the database.", keyFilePath);
            }

            candidate ??= RandomNumberGenerator.GetBytes(KeySizeBytes);
            string protectedKey = _protector!.Protect(Convert.ToBase64String(candidate));

            // Replicas may race here: the first insert wins and everyone re-reads the winner.
            db.Database.ExecuteSqlInterpolated(
                $"INSERT INTO platform_secrets (name, value, created_at) VALUES ({DatabaseSecretName}, {protectedKey}, {DateTime.UtcNow}) ON CONFLICT (name) DO NOTHING");
            return TryReadDatabaseKey(db, out byte[] winner) ? winner : null;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or IOException
            or UnauthorizedAccessException or CryptographicException or Npgsql.NpgsqlException)
        {
            _logger.LogWarning(ex, "Could not resolve the assistant confirmation-token key from the database; falling back to the key file.");
            return null;
        }
    }

    private bool TryReadDatabaseKey(InferenceApiDbContext db, out byte[] key)
    {
        string? stored = db.PlatformSecrets.AsNoTracking()
            .Where(x => x.Name == DatabaseSecretName)
            .Select(x => x.Value)
            .FirstOrDefault();
        return TryDecodeKey(stored, out key);
    }

    private string ResolveKeyFilePath()
    {
        string? configured = _configuration![KeyFileConfigPath];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured!;
        }

        string keysPath = _configuration[DataProtectionKeysPathConfigPath] ?? DefaultKeysPath;
        return Path.Combine(keysPath, DefaultKeyFileName);
    }

    private void PersistKey(string keyFilePath, byte[] key)
    {
        try
        {
            string? directory = Path.GetDirectoryName(keyFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-rename so a crash mid-write cannot leave a half-written key that would
            // decrypt to nothing and silently change the key on the next start.
            string protectedKey = _protector!.Protect(Convert.ToBase64String(key));
            string temporaryPath = $"{keyFilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, protectedKey);
            File.Move(temporaryPath, keyFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or NotSupportedException)
        {
            // Degrade to a process-local key rather than refusing to start: the only consequence is
            // that a token proposed before a restart is rejected after it (re-propose, which the
            // stale-token path already tells the caller to do).
            _logger.LogWarning(ex, "Could not persist the assistant confirmation-token key at {KeyFilePath}.", keyFilePath);
        }
    }

    private bool TryDecodeKey(string? protectedValue, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        if (!_protector!.TryUnprotect(protectedValue, out string base64) || string.IsNullOrWhiteSpace(base64))
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
