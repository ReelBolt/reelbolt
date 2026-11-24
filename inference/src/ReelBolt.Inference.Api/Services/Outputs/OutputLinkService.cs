using System.Globalization;
using System.Text;
using ReelBolt.Inference.Api.Services.Assistant;

namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>What an output link can point at. Append-only: the wire name of each member is in every link minted.</summary>
public enum OutputLinkKind
{
    /// <summary>A workflow step result's render (<c>OutputStorageKey</c> under <c>projects/{p}/outputFiles/</c>).</summary>
    StepResult,

    /// <summary>A project file row.</summary>
    ProjectFile,

    /// <summary>A media derivative row (a thumbnail), or one sheet of a filmstrip density when a sheet index is given.</summary>
    Derivative,
}

/// <summary>The thing one link names: ids only, never a storage key.</summary>
public sealed record OutputLinkTarget(Guid ProjectId, OutputLinkKind Kind, Guid Id, int? SheetIndex);

/// <summary>A short-lived link and when it stops working.</summary>
public sealed record OutputLink(string Url, DateTimeOffset ExpiresAt);

/// <summary>What a verified link says: the target, the user it was minted for, and its lifetime.</summary>
public sealed record OutputLinkClaims(OutputLinkTarget Target, Guid UserId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints and verifies app-signed capability URLs for outputs (<c>GET /api/v1/output-links/{token}</c>).
/// </summary>
/// <remarks>
/// A link carries ids and an expiry, never a storage key, bucket or object-store URL. Verifying a link
/// only proves the server minted it for that user and that it has not expired; whoever serves the bytes
/// must still re-check, from the database, that the user may view the project right now.
/// </remarks>
public interface IOutputLinkService
{
    OutputLink Create(OutputLinkTarget target, Guid userId, TimeSpan? ttl);

    /// <summary>False — never a throw — for anything that is not a link this deployment minted and that is still live at <paramref name="now"/>.</summary>
    bool TryVerify(string token, DateTimeOffset now, out OutputLinkClaims claims);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Token = <c>base64url(payload) "." base64url(mac)</c>, payload
/// <c>v1|kind|id|projectId|userId|sheet|expUnix|iatUnix</c>. The MAC is
/// <see cref="IConfirmationTokenService.Compute(string, string, Guid)"/> under <see cref="Operation"/>,
/// so it shares the deployment's confirmation key and is bound to the user; verification is that
/// service's fixed-time comparison. The issue time rides along so a link minted before the user's
/// tokens were invalidated (sign out everywhere) can be refused.
/// </para>
/// <para>
/// TTL: <c>OutputLinks:DefaultTtlMinutes</c> (15) when the caller names none, never more than
/// <see cref="MaxTtl"/>. URL: <c>OutputLinks:PublicBaseUrl</c>, else the app's public origin
/// <c>CloudStorage:PublicBaseUrl</c> (compose <c>PUBLIC_BASE_URL</c>), else a path relative to this API.
/// </para>
/// </remarks>
public sealed class OutputLinkService : IOutputLinkService
{
    public const string Operation = "OutputLink.v1";
    public const string RoutePrefix = "/api/v1/output-links/";
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(60);

    /// <summary>No real token comes close; anything longer is refused before it is decoded.</summary>
    public const int MaxTokenLength = 512;

    private const string Version = "v1";

    private readonly IConfirmationTokenService _tokens;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _defaultTtl;
    private readonly string _baseUrl;

    public OutputLinkService(IConfirmationTokenService tokens, IConfiguration configuration, TimeProvider? clock = null)
    {
        _tokens = tokens;
        _clock = clock ?? TimeProvider.System;
        _defaultTtl = int.TryParse(configuration["OutputLinks:DefaultTtlMinutes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultTtl;
        _baseUrl = AbsoluteOrigin(configuration["OutputLinks:PublicBaseUrl"])
                   ?? AbsoluteOrigin(configuration["CloudStorage:PublicBaseUrl"])
                   ?? string.Empty;
    }

    public OutputLink Create(OutputLinkTarget target, Guid userId, TimeSpan? ttl)
    {
        TimeSpan lifetime = ttl is { } asked && asked > TimeSpan.Zero ? asked : _defaultTtl;
        if (lifetime > MaxTtl) lifetime = MaxTtl;

        DateTimeOffset issuedAt = TruncateToSeconds(_clock.GetUtcNow());
        DateTimeOffset expiresAt = issuedAt.Add(lifetime);
        string payload = BuildPayload(target, userId, issuedAt, expiresAt);
        byte[] mac = Convert.FromHexString(_tokens.Compute(Operation, payload, userId));
        string token = Base64UrlEncode(Encoding.UTF8.GetBytes(payload)) + "." + Base64UrlEncode(mac);
        return new OutputLink(_baseUrl + RoutePrefix + token, expiresAt);
    }

    public bool TryVerify(string token, DateTimeOffset now, out OutputLinkClaims claims)
    {
        claims = null!;
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return false;
            string[] parts = token.Split('.');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
            if (!TryBase64UrlDecode(parts[0], out byte[] payloadBytes) || !TryBase64UrlDecode(parts[1], out byte[] mac)) return false;

            string payload = Encoding.UTF8.GetString(payloadBytes);
            if (!TryParsePayload(payload, out OutputLinkClaims parsed)) return false;

            // The canonical re-serialisation must reproduce the presented payload byte for byte, so two
            // spellings of one claim set (a leading zero, an upper-case GUID) can never both verify.
            if (!string.Equals(BuildPayload(parsed.Target, parsed.UserId, parsed.IssuedAt, parsed.ExpiresAt), payload, StringComparison.Ordinal))
                return false;

            if (!_tokens.Verify(Operation, payload, parsed.UserId, Convert.ToHexString(mac).ToLowerInvariant())) return false;
            if (now >= parsed.ExpiresAt) return false;

            claims = parsed;
            return true;
        }
        catch (Exception)
        {
            // Fail closed: a verifier that can fault on a hostile input is worse than one that says no.
            return false;
        }
    }

    private static string BuildPayload(OutputLinkTarget target, Guid userId, DateTimeOffset issuedAt, DateTimeOffset expiresAt) =>
        string.Join('|',
            Version,
            KindName(target.Kind),
            target.Id.ToString("N"),
            target.ProjectId.ToString("N"),
            userId.ToString("N"),
            target.SheetIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    private static bool TryParsePayload(string payload, out OutputLinkClaims claims)
    {
        claims = null!;
        string[] f = payload.Split('|');
        if (f.Length != 8 || f[0] != Version) return false;
        if (!TryKind(f[1], out OutputLinkKind kind)) return false;
        if (!Guid.TryParseExact(f[2], "N", out Guid id) ||
            !Guid.TryParseExact(f[3], "N", out Guid projectId) ||
            !Guid.TryParseExact(f[4], "N", out Guid userId))
            return false;

        int? sheet = null;
        if (f[5].Length > 0)
        {
            if (kind != OutputLinkKind.Derivative ||
                !int.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                return false;
            sheet = index;
        }

        if (!long.TryParse(f[6], NumberStyles.None, CultureInfo.InvariantCulture, out long exp) ||
            !long.TryParse(f[7], NumberStyles.None, CultureInfo.InvariantCulture, out long iat) ||
            iat > exp)
            return false;

        claims = new OutputLinkClaims(
            new OutputLinkTarget(projectId, kind, id, sheet),
            userId,
            DateTimeOffset.FromUnixTimeSeconds(iat),
            DateTimeOffset.FromUnixTimeSeconds(exp));
        return true;
    }

    private static string KindName(OutputLinkKind kind) => kind switch
    {
        OutputLinkKind.StepResult => "step",
        OutputLinkKind.ProjectFile => "file",
        OutputLinkKind.Derivative => "deriv",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static bool TryKind(string name, out OutputLinkKind kind)
    {
        switch (name)
        {
            case "step": kind = OutputLinkKind.StepResult; return true;
            case "file": kind = OutputLinkKind.ProjectFile; return true;
            case "deriv": kind = OutputLinkKind.Derivative; return true;
            default: kind = default; return false;
        }
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds());

    private static string? AbsoluteOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? trimmed
            : null;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string text, out byte[] bytes)
    {
        bytes = [];
        foreach (char c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }

        string s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(s);

            // Canonical spelling only (QA-P0-16): the last character's padding bits are ignored by the
            // decoder, so without this one token would have several spellings that all verify.
            return string.Equals(Base64UrlEncode(bytes), text, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
