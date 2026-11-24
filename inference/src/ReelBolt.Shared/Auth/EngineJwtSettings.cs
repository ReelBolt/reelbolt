using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace ReelBolt.Shared.Auth;

/// <summary>
/// The optional engine signing key (<c>EngineJwt:SigningKey</c>, env <c>ENGINE_JWT_SIGNING_KEY</c>). When it
/// is set, the WorkflowEngine no longer holds the session signing key (<c>Jwt:SigningKey</c>): it signs its
/// on-behalf-of tokens with the engine key under its own issuer and audience, and it accepts inbound only
/// the short-lived admin tokens the Inference API mints with the same key. A stolen engine key therefore
/// cannot forge a user session. When the key is not set, nothing changes.
/// </summary>
public sealed class EngineJwtSettings
{
    public const string SigningKeyKey = "EngineJwt:SigningKey";
    public const string IssuerKey = "EngineJwt:Issuer";
    public const string AudienceKey = "EngineJwt:Audience";

    /// <summary>Default issuer of the engine's on-behalf-of tokens.</summary>
    public const string DefaultIssuer = "reelbolt-engine";

    /// <summary>Default audience of the engine's on-behalf-of tokens.</summary>
    public const string DefaultAudience = "reelbolt-engine-obo";

    /// <summary>Issuer of the admin tokens the Inference API mints for the engine's inbound routes.</summary>
    public const string AdminIssuer = "reelbolt-control";

    /// <summary>Audience of the admin tokens the Inference API mints for the engine's inbound routes.</summary>
    public const string AdminAudience = "reelbolt-engine-admin";

    /// <summary>Lifetime of an admin token: one proxied request.</summary>
    public static readonly TimeSpan AdminLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Cut-over switch for the Inference API (env <c>ENGINE_JWT_ACCEPT_LEGACY_OBO</c>, default false). The
    /// control host deploys before the engine host, so for a while the engine still signs its on-behalf-of
    /// tokens with the session key. True keeps accepting those, on the engine's allowlisted routes only.
    /// </summary>
    public const string AcceptLegacyOnBehalfOfKey = "EngineJwt:AcceptLegacyOnBehalfOf";

    public const int MinKeyBytes = 32;

    private EngineJwtSettings(byte[] key, string issuer, string audience, bool acceptLegacyOnBehalfOf)
    {
        Key = key;
        Issuer = issuer;
        Audience = audience;
        AcceptLegacyOnBehalfOf = acceptLegacyOnBehalfOf;
    }

    /// <summary>See <see cref="AcceptLegacyOnBehalfOfKey"/>. Has no effect unless the engine key is set.</summary>
    public bool AcceptLegacyOnBehalfOf { get; }

    public byte[] Key { get; }

    /// <summary>Issuer of on-behalf-of tokens (engine to Inference API).</summary>
    public string Issuer { get; }

    /// <summary>Audience of on-behalf-of tokens (engine to Inference API).</summary>
    public string Audience { get; }

    public SymmetricSecurityKey SecurityKey => new(Key);

    /// <summary>
    /// Startup check, called first thing in each service's <c>Program.cs</c>. Compose passes
    /// <c>JWT_SIGNING_KEY</c> through unguarded, so each service refuses a missing or short key itself.
    /// The Inference API always needs the session key (<paramref name="engineKeyReplacesSessionKey"/> false).
    /// The WorkflowEngine needs it only while it has no engine key (true). A bad engine key setting throws too.
    /// </summary>
    public static void RequireSigningKeys(IConfiguration configuration, bool engineKeyReplacesSessionKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EngineJwtSettings? engine = Read(configuration);
        if (engine is not null && engineKeyReplacesSessionKey)
            return;
        if (Encoding.UTF8.GetByteCount(configuration["Jwt:SigningKey"] ?? string.Empty) < MinKeyBytes)
            throw new InvalidOperationException(
                $"Jwt:SigningKey (JWT_SIGNING_KEY) must be set to a random value of at least {MinKeyBytes} bytes; generate one with: openssl rand -hex 32");
    }

    /// <summary>
    /// Reads the engine key settings. Null when <c>EngineJwt:SigningKey</c> is blank (the feature is off).
    /// Throws, so the process refuses to start, when the key is shorter than 32 bytes, when it equals the
    /// session signing key, or when the on-behalf-of issuer or audience collides with the admin or session ones.
    /// </summary>
    public static EngineJwtSettings? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? raw = configuration[SigningKeyKey];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        byte[] key = Encoding.UTF8.GetBytes(raw);
        if (key.Length < MinKeyBytes)
            throw new InvalidOperationException($"{SigningKeyKey} must be at least {MinKeyBytes} bytes.");

        byte[] session = Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"] ?? string.Empty);
        if (session.Length > 0 && CryptographicOperations.FixedTimeEquals(key, session))
            throw new InvalidOperationException(
                $"{SigningKeyKey} must not equal Jwt:SigningKey: the engine key exists so the engine never holds the session key.");

        string issuer = Blank(configuration[IssuerKey]) ?? DefaultIssuer;
        string audience = Blank(configuration[AudienceKey]) ?? DefaultAudience;
        if (string.Equals(issuer, AdminIssuer, StringComparison.Ordinal)
            || string.Equals(audience, AdminAudience, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{IssuerKey} and {AudienceKey} must differ from the admin token's '{AdminIssuer}' and '{AdminAudience}'.");
        // The Inference API tells an engine token from a session token by its issuer before it validates
        // either, so the two issuers must differ.
        if (string.Equals(issuer, configuration["Jwt:Issuer"], StringComparison.Ordinal))
            throw new InvalidOperationException($"{IssuerKey} must differ from the session token's Jwt:Issuer.");

        string? legacy = Blank(configuration[AcceptLegacyOnBehalfOfKey]);
        bool acceptLegacy = false;
        if (legacy is not null && !bool.TryParse(legacy, out acceptLegacy))
            throw new InvalidOperationException($"{AcceptLegacyOnBehalfOfKey} must be true or false.");

        return new EngineJwtSettings(key, issuer, audience, acceptLegacy);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
