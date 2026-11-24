using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.FolderSync;

/// <summary>
/// The paired desktop computer a signed request came from: who it is, which organization it belongs
/// to, and the person it acts as (the user who paired it).
/// </summary>
public sealed record DeviceContext(Guid DeviceId, string DeviceName, Guid OrganizationId, Guid PairingUserId);

/// <summary>Why a device request was refused. Every one is answered the same <c>401 device_unauthorized</c>.</summary>
public enum DeviceAuthFailure
{
    None,
    MissingHeaders,
    BadTimestamp,
    ClockSkew,
    BadNonce,
    BodyTooLarge,
    UnknownDevice,
    DeviceNotUsable,
    BadSignature,
    ReplayedNonce
}

public sealed record DeviceAuthResult(DeviceContext? Device, DeviceAuthFailure Failure)
{
    public bool Succeeded => Device is not null;
}

/// <summary>
/// The signed-request format of the desktop app's folder sync (docs/design/local-folder-sync-and-storage.md,
/// "Device authentication"). Pure functions, shared by the server and the tests' golden vectors.
/// </summary>
public static class DeviceSignature
{
    public const string Version = "reelbolt-device-request-v1";

    public const string DeviceHeader = "X-ReelBolt-Device";
    public const string TimestampHeader = "X-ReelBolt-Timestamp";
    public const string NonceHeader = "X-ReelBolt-Nonce";
    public const string SignatureHeader = "X-ReelBolt-Signature";

    /// <summary>
    /// <c>reelbolt-device-request-v1|deviceId|timestamp|nonce|METHOD|pathAndQuery|sha256hex(body)</c>,
    /// every component exactly as the request carried it.
    /// </summary>
    public static string SignedString(string deviceId, string timestamp, string nonce, string method, string pathAndQuery, ReadOnlySpan<byte> body) =>
        string.Join('|', Version, deviceId, timestamp, nonce, method.ToUpperInvariant(), pathAndQuery, BodyHash(body));

    /// <summary>Lowercase hex SHA-256 of the body bytes (an empty body hashes the empty string).</summary>
    public static string BodyHash(ReadOnlySpan<byte> body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    /// <summary>
    /// Verifies an Ed25519 signature. <paramref name="publicKeyBase64"/> is <c>runner_devices.public_key</c>:
    /// standard base64 of the raw 32-byte key, as the Go pairing handler stores it.
    /// <paramref name="signatureBase64Url"/> is base64url of the 64-byte signature, padding optional.
    /// Never throws.
    /// </summary>
    public static bool Verify(string? publicKeyBase64, string signedString, string? signatureBase64Url)
    {
        if (publicKeyBase64 is null || signatureBase64Url is null) return false;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[]? signature = DecodeBase64Url(signatureBase64Url);
        if (key.Length != 32 || signature is not { Length: 64 }) return false;

        try
        {
            Ed25519Signer verifier = new();
            verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            byte[] message = Encoding.UTF8.GetBytes(signedString);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Base64url (padding optional) to bytes; null when it is not valid base64url.</summary>
    public static byte[]? DecodeBase64Url(string value)
    {
        string trimmed = value.TrimEnd('=');
        if (trimmed.Length == 0 || trimmed.Length > 512) return null;
        foreach (char c in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return null;
        }

        string standard = trimmed.Replace('-', '+').Replace('_', '/');
        standard = standard.PadRight(standard.Length + ((4 - (standard.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The <c>(device, nonce)</c> pairs seen in the last <see cref="Window"/>, so a captured request cannot be
/// replayed. In memory and per API process: a second replica has its own cache, which the 120-second
/// timestamp window bounds (a replay must reach a different replica within two minutes).
/// </summary>
public sealed class DeviceNonceCache
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(240);

    private const int PruneThreshold = 50_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>Records the nonce; false when it was already seen inside the window (a replay).</summary>
    public bool TryRecord(Guid deviceId, string nonce, DateTimeOffset now)
    {
        if (_seen.Count > PruneThreshold) Prune(now);

        string key = deviceId.ToString("N") + "|" + nonce;
        while (true)
        {
            if (_seen.TryAdd(key, now)) return true;
            if (!_seen.TryGetValue(key, out DateTimeOffset seenAt)) continue;
            if (now - seenAt < Window) return false;
            if (_seen.TryUpdate(key, now, seenAt)) return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, DateTimeOffset> entry in _seen)
        {
            if (now - entry.Value >= Window) _seen.TryRemove(entry.Key, out _);
        }
    }
}

/// <summary>
/// Authenticates a <c>/api/v1/device/**</c> request signed with the device's Ed25519 key — no bearer
/// token, no cookie (docs/design/local-folder-sync-and-storage.md, "Device authentication"). Refuses
/// when the timestamp is more than <see cref="MaxSkew"/> away, the nonce was seen in the last
/// <see cref="DeviceNonceCache.Window"/>, the device is unknown, revoked, not a desktop or without an
/// organization or pairing user, or the signature does not verify against <c>runner_devices.public_key</c>.
/// The caller answers every refusal with the same <c>401 device_unauthorized</c> and no detail.
/// Updates <c>last_seen_at</c> at most once a minute.
/// </summary>
public sealed class DeviceRequestAuthenticator
{
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(120);

    public static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    public const int MaxBodyBytes = 4 * 1024 * 1024;

    public const string ItemKey = "reelbolt.device";

    private readonly InferenceApiDbContext _db;
    private readonly DeviceNonceCache _nonces;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeviceRequestAuthenticator> _logger;

    public DeviceRequestAuthenticator(
        InferenceApiDbContext db,
        DeviceNonceCache nonces,
        ILogger<DeviceRequestAuthenticator> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _nonces = nonces;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<DeviceAuthResult> AuthenticateAsync(HttpRequest request, CancellationToken ct)
    {
        string? deviceHeader = request.Headers[DeviceSignature.DeviceHeader];
        string? timestamp = request.Headers[DeviceSignature.TimestampHeader];
        string? nonce = request.Headers[DeviceSignature.NonceHeader];
        string? signature = request.Headers[DeviceSignature.SignatureHeader];

        if (string.IsNullOrEmpty(deviceHeader) || string.IsNullOrEmpty(timestamp) ||
            string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(signature) ||
            !Guid.TryParse(deviceHeader, out Guid deviceId))
        {
            return Fail(DeviceAuthFailure.MissingHeaders);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        if (!TryParseTimestamp(timestamp, out DateTimeOffset signedAt)) return Fail(DeviceAuthFailure.BadTimestamp);
        if ((now - signedAt).Duration() > MaxSkew) return Fail(DeviceAuthFailure.ClockSkew);

        if (DeviceSignature.DecodeBase64Url(nonce) is not { Length: >= 16 }) return Fail(DeviceAuthFailure.BadNonce);

        byte[]? body = await ReadBodyAsync(request, ct);
        if (body is null) return Fail(DeviceAuthFailure.BodyTooLarge);

        RunnerDevice? device = await _db.RunnerDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return Fail(DeviceAuthFailure.UnknownDevice);

        Guid? pairingUser = device.PairedByUserId ?? device.CreatedByUserId;
        if (device.Kind != RunnerKind.Desktop || device.RevokedAt is not null ||
            device.OrganizationId is not { } organizationId || pairingUser is not { } userId ||
            string.IsNullOrEmpty(device.PublicKey))
        {
            return Fail(DeviceAuthFailure.DeviceNotUsable);
        }

        string signed = DeviceSignature.SignedString(deviceHeader, timestamp, nonce, request.Method, PathAndQuery(request), body);
        if (!DeviceSignature.Verify(device.PublicKey, signed, signature)) return Fail(DeviceAuthFailure.BadSignature);

        // Recorded only after the signature verified, so an unauthenticated flood cannot fill the cache
        // or burn a legitimate device's nonces.
        if (!_nonces.TryRecord(deviceId, nonce, now)) return Fail(DeviceAuthFailure.ReplayedNonce);

        if (device.LastSeenAt is not { } lastSeen || now.UtcDateTime - lastSeen >= LastSeenResolution)
        {
            device.LastSeenAt = now.UtcDateTime;
            await _db.SaveChangesAsync(ct);
        }

        return new DeviceAuthResult(new DeviceContext(device.Id, device.Name, organizationId, userId), DeviceAuthFailure.None);
    }

    /// <summary>
    /// The path and query exactly as the client sent them: the raw request target when the server
    /// recorded it (Kestrel does), else the escaped path base, path and query string.
    /// </summary>
    public static string PathAndQuery(HttpRequest request)
    {
        string? raw = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw) && raw.StartsWith('/')) return raw;
        return request.PathBase.ToUriComponent() + request.Path.ToUriComponent() + request.QueryString.ToUriComponent();
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        if (value.Length > 40 || !value.Contains('T')) return false;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
    }

    /// <summary>Reads the whole body (at most <see cref="MaxBodyBytes"/>) and rewinds it for model binding.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > MaxBodyBytes) return null;

        request.EnableBuffering(MaxBodyBytes, MaxBodyBytes + 1L);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await request.Body.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        request.Body.Position = 0;
        return buffer.ToArray();
    }

    private DeviceAuthResult Fail(DeviceAuthFailure failure)
    {
        // The reason is for the operator's log only; the device always sees the same 401.
        _logger.LogInformation("Refused a device request: {Reason}", failure);
        return new DeviceAuthResult(null, failure);
    }
}
