#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.FolderSync;

/// <summary>
/// The desktop app proves itself with an Ed25519 signature on every <c>/api/v1/device/**</c> request
/// (docs/local-folder-sync.md "How the desktop app authenticates"). These are the security properties
/// the whole device surface rests on: the signature covers the method, the path WITH its query and the
/// body's hash; the clock window is 120 s; a nonce is single-use for 240 s and is recorded ONLY after
/// the signature verified (so an unauthenticated flood cannot burn a device's nonces or fill the cache);
/// and a device that is unknown, revoked, a cloud pool worker or has no organization is refused — always
/// with the same <c>401 device_unauthorized</c> and no detail, so nothing leaks which check failed.
/// </summary>
public class DeviceRequestAuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_correctly_signed_request_from_a_paired_desktop_device_authenticates()
    {
        Fixture f = new();

        DeviceAuthResult result = await f.Authenticate();

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().Be(DeviceAuthFailure.None);
        result.Device!.DeviceId.Should().Be(f.DeviceId);
        result.Device.OrganizationId.Should().Be(f.OrganizationId);
        result.Device.PairingUserId.Should().Be(f.PairingUserId);
        // First sighting of this device stamps last_seen_at, which the dashboard reads as "online".
        (await f.Reload()).LastSeenAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task A_body_tampered_after_signing_is_refused()
    {
        Fixture f = new();
        // Signed over "[]", but different bytes arrive on the wire.
        byte[] signedBytes = Encoding.UTF8.GetBytes("[]");
        string timestamp = f.Timestamp();
        string nonce = f.Nonce();
        string signature = f.Sign(timestamp, nonce, signedBytes);

        DeviceAuthResult result = await f.Send(
            "POST", Fixture.Path, Encoding.UTF8.GetBytes("""[{"path":"a.txt"}]"""), timestamp, nonce, signature);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(DeviceAuthFailure.BadSignature);
    }

    [Fact]
    public async Task A_path_or_method_changed_after_signing_is_refused()
    {
        Fixture f = new();
        byte[] body = Encoding.UTF8.GetBytes("{}");
        string timestamp = f.Timestamp();
        string nonce = f.Nonce();
        string signed = DeviceSignature.SignedString(
            f.DeviceId.ToString(), timestamp, nonce, "GET", "/api/v1/device/folder-links?a=1", body);

        // Same signature presented on another method and on another path: both must fail.
        (await f.Send("POST", "/api/v1/device/folder-links?a=1", body, timestamp, nonce, signed))
            .Failure.Should().Be(DeviceAuthFailure.BadSignature);
        (await f.Send("GET", "/api/v1/device/folder-links?a=2", body, timestamp, nonce, signed))
            .Failure.Should().Be(DeviceAuthFailure.BadSignature);
    }

    [Theory]
    [InlineData(-121)]
    [InlineData(121)]
    public async Task A_timestamp_outside_the_window_is_refused(int offsetSeconds)
    {
        Fixture f = new();
        // Signed with a timestamp the server's clock disagrees with, then presented.
        DeviceAuthResult result = await f.Authenticate(timestampOverride: Now.AddSeconds(offsetSeconds));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(DeviceAuthFailure.ClockSkew);
    }

    [Fact]
    public async Task The_same_nonce_twice_is_refused_as_a_replay()
    {
        Fixture f = new();
        // One request, byte for byte: same timestamp, same nonce, same signature.
        byte[] body = [];
        string timestamp = f.Timestamp();
        string nonce = f.Nonce();
        string signature = f.Sign(timestamp, nonce, body);

        (await f.Send("POST", Fixture.Path, body, timestamp, nonce, signature)).Succeeded.Should().BeTrue();

        // The identical message replayed (a captured request) — the nonce is spent.
        DeviceAuthResult replay = await f.Send("POST", Fixture.Path, body, timestamp, nonce, signature);
        replay.Succeeded.Should().BeFalse();
        replay.Failure.Should().Be(DeviceAuthFailure.ReplayedNonce);
    }

    [Fact]
    public async Task A_nonce_is_not_spent_when_the_signature_is_bad()
    {
        Fixture f = new();
        string timestamp = f.Timestamp();
        string nonce = f.Nonce();
        byte[] body = Encoding.UTF8.GetBytes("{}");

        // A forged request that CLAIMS this nonce must not be able to poison it for the real device.
        (await f.Send("POST", Fixture.Path, body, timestamp, nonce, signature: "AAAA")).Succeeded.Should().BeFalse();
        (await f.Send("POST", Fixture.Path, body, timestamp, nonce, f.Sign(timestamp, nonce, body))).Succeeded
            .Should().BeTrue("the nonce was never recorded, because the signature never verified");
    }

    [Fact]
    public async Task A_nonce_shorter_than_16_bytes_is_refused()
    {
        Fixture f = new();
        byte[] body = Encoding.UTF8.GetBytes("{}");
        string timestamp = f.Timestamp();
        string shortNonce = Base64Url(new byte[8]);

        DeviceAuthResult result = await f.Send(
            "POST", Fixture.Path, body, timestamp, shortNonce, f.Sign(timestamp, shortNonce, body));

        result.Failure.Should().Be(DeviceAuthFailure.BadNonce);
    }

    [Fact]
    public async Task A_request_with_no_headers_is_refused()
    {
        Fixture f = new();

        DeviceAuthResult result = await f.Authenticate(signed: false);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(DeviceAuthFailure.MissingHeaders);
    }

    [Fact]
    public async Task A_revoked_device_is_refused()
    {
        Fixture f = new();
        f.Device.RevokedAt = Now.AddMinutes(-1).UtcDateTime;
        await f.Db.SaveChangesAsync();

        DeviceAuthResult result = await f.Authenticate();

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(DeviceAuthFailure.DeviceNotUsable,
            "revoking a device stops folder sync immediately — the dashboard's Revoke must be enough");
    }

    [Fact]
    public async Task A_cloud_pool_device_is_refused()
    {
        Fixture f = new();
        f.Device.Kind = RunnerKind.CloudPool;
        await f.Db.SaveChangesAsync();

        (await f.Authenticate()).Failure.Should().Be(DeviceAuthFailure.DeviceNotUsable);
    }

    [Fact]
    public async Task A_device_with_no_organization_is_refused()
    {
        Fixture f = new();
        f.Device.OrganizationId = null;
        await f.Db.SaveChangesAsync();

        (await f.Authenticate()).Failure.Should().Be(DeviceAuthFailure.DeviceNotUsable);
    }

    [Fact]
    public async Task A_public_key_that_does_not_match_the_signature_is_refused()
    {
        Fixture f = new();
        // Another machine's key on the row (a swapped key) must not authenticate this signature.
        f.Device.PublicKey = Convert.ToBase64String(new KeyPair().Public);
        await f.Db.SaveChangesAsync();

        (await f.Authenticate()).Failure.Should().Be(DeviceAuthFailure.BadSignature);
    }

    [Fact]
    public async Task An_unknown_device_is_refused()
    {
        Fixture f = new();
        f.Db.RunnerDevices.RemoveRange(f.Db.RunnerDevices);
        await f.Db.SaveChangesAsync();

        (await f.Authenticate()).Failure.Should().Be(DeviceAuthFailure.UnknownDevice);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_read_whole()
    {
        Fixture f = new();

        DeviceAuthResult result = await f.Authenticate(sendBody: new string('x', DeviceRequestAuthenticator.MaxBodyBytes + 1));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(DeviceAuthFailure.BodyTooLarge);
    }

    [Fact]
    public async Task Last_seen_is_not_rewritten_on_every_request()
    {
        Fixture f = new();
        (await f.Authenticate()).Succeeded.Should().BeTrue();
        (await f.Reload()).LastSeenAt.Should().Be(Now.UtcDateTime);

        // A second request a few seconds later must not touch the row again (LastSeenResolution = 1 min):
        // a syncing computer sends many requests a second, and none of them should write.
        f.Advance(TimeSpan.FromSeconds(10));
        (await f.Authenticate()).Succeeded.Should().BeTrue();
        f.Db.ChangeTracker.Clear();
        (await f.Reload()).LastSeenAt.Should().Be(Now.UtcDateTime, "the stamp is resolved to once a minute");
    }

    // ------------------------------------------------------------------ fixture

    private static readonly byte[] EmptyBody = [];

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>An Ed25519 pair, as the desktop app generates it at pairing.</summary>
    private sealed class KeyPair
    {
        public KeyPair()
        {
            Ed25519KeyPairGenerator generator = new();
            generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
            Private = (Ed25519PrivateKeyParameters)pair.Private;
            Public = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        }

        public Ed25519PrivateKeyParameters Private { get; }

        public byte[] Public { get; }

        public string Sign(string message)
        {
            Ed25519Signer signer = new();
            signer.Init(true, Private);
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            signer.BlockUpdate(bytes, 0, bytes.Length);
            return Base64Url(signer.GenerateSignature());
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class Fixture
    {
        public const string Path = "/api/v1/device/folder-links/1/manifest";

        private readonly Clock _clock = new(Now);
        private readonly KeyPair _keys = new();

        public Fixture()
        {
            Db = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);

            DeviceId = Guid.NewGuid();
            OrganizationId = Guid.NewGuid();
            PairingUserId = Guid.NewGuid();

            Device = new RunnerDevice
            {
                Id = DeviceId,
                Kind = RunnerKind.Desktop,
                Name = "Studio PC",
                OrganizationId = OrganizationId,
                PairedByUserId = PairingUserId,
                PublicKey = Convert.ToBase64String(_keys.Public),
                CreatedAt = Now.UtcDateTime
            };

            Db.RunnerDevices.Add(Device);
            Db.SaveChanges();

            Authenticator = new DeviceRequestAuthenticator(
                Db, new DeviceNonceCache(), NullLogger<DeviceRequestAuthenticator>.Instance, _clock);
        }

        public InferenceApiDbContext Db { get; }

        public RunnerDevice Device { get; }

        public DeviceRequestAuthenticator Authenticator { get; }

        public Guid DeviceId { get; }

        public Guid OrganizationId { get; }

        public Guid PairingUserId { get; }

        private string NonceSeed { get; set; } = Guid.NewGuid().ToString("N");

        public void Advance(TimeSpan by) => _clock.Advance(by);

        public async Task<RunnerDevice> Reload() =>
            await Db.RunnerDevices.AsNoTracking().SingleAsync(d => d.Id == DeviceId);

        public string Timestamp() => Timestamp(Now);

        public string Timestamp(DateTimeOffset at) =>
            at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

        public string Nonce()
        {
            NonceSeed = Guid.NewGuid().ToString("N");
            return Base64Url(Encoding.UTF8.GetBytes(NonceSeed));
        }

        public string Sign(string timestamp, string nonce, byte[] body, string method = "POST", string path = Path) =>
            _keys.Sign(DeviceSignature.SignedString(DeviceId.ToString(), timestamp, nonce, method, path, body));

        /// <summary>Builds and sends a request; <paramref name="body"/> is what the server receives.</summary>
        public async Task<DeviceAuthResult> Send(
            string method,
            string path,
            byte[] body,
            string timestamp,
            string nonce,
            string signature)
        {
            DefaultHttpContext context = new();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Body = new MemoryStream(body);
            context.Request.Headers[DeviceSignature.DeviceHeader] = DeviceId.ToString();
            context.Request.Headers[DeviceSignature.TimestampHeader] = timestamp;
            context.Request.Headers[DeviceSignature.NonceHeader] = nonce;
            context.Request.Headers[DeviceSignature.SignatureHeader] = signature;
            return await Authenticator.AuthenticateAsync(context.Request, CancellationToken.None);
        }

        /// <summary>
        /// A correctly signed request, unless asked otherwise. <paramref name="signBody"/> is what was
        /// signed, <paramref name="sendBody"/> what is sent (they differ only in the tamper test).
        /// </summary>
        public async Task<DeviceAuthResult> Authenticate(
            string? sendBody = null,
            string? signBody = null,
            DateTimeOffset? timestampOverride = null,
            bool signed = true)
        {
            byte[] sent = sendBody is null ? EmptyBody : Encoding.UTF8.GetBytes(sendBody);
            byte[] signedBytes = signBody is null ? EmptyBody : Encoding.UTF8.GetBytes(signBody);
            byte[] effective = signBody is null ? sent : signedBytes;

            string timestamp = timestampOverride is { } at ? Timestamp(at) : Timestamp();
            string nonce = Nonce();
            string signature = signed ? Sign(timestamp, nonce, effective) : string.Empty;

            return await Send("POST", Path, sent, timestamp, nonce, signature);
        }
    }
}
