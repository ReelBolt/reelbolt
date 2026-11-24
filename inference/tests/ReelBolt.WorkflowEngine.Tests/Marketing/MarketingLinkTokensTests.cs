#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>saas-promotions T-004: the signed confirm and unsubscribe links (plan "Signed tokens").</summary>
public sealed class MarketingLinkTokensTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemorySecretStore _secrets = new();
    private readonly MarketingLinkTokens _tokens;

    public MarketingLinkTokensTests() => _tokens = new MarketingLinkTokens(_secrets);

    [Fact]
    public void Unsubscribe_token_round_trips_and_has_the_frozen_shape()
    {
        Guid id = Guid.NewGuid();

        string token = _tokens.CreateUnsubscribeToken(id);

        token.Should().MatchRegex(@"^u1\.k1\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}$");
        MarketingTokenResult result = _tokens.VerifyUnsubscribe(token);
        result.Status.Should().Be(MarketingTokenStatus.Valid);
        result.ConsentId.Should().Be(id);
    }

    [Fact]
    public void Confirm_token_round_trips_and_has_the_frozen_shape()
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset expires = Now.AddDays(7);

        string token = _tokens.CreateConfirmToken(id, expires);

        token.Should().MatchRegex(@"^c1\.k1\.[A-Za-z0-9_-]{22}\.\d+\.[A-Za-z0-9_-]{43}$");
        token.Split('.')[3].Should().Be(expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        MarketingTokenResult result = _tokens.VerifyConfirm(token, Now);
        result.Status.Should().Be(MarketingTokenStatus.Valid);
        result.ConsentId.Should().Be(id);
    }

    [Fact]
    public void A_tampered_mac_is_invalid()
    {
        string token = _tokens.CreateUnsubscribeToken(Guid.NewGuid());
        string[] parts = token.Split('.');
        char first = parts[3][0];
        parts[3] = (first == 'A' ? 'B' : 'A') + parts[3][1..];

        _tokens.VerifyUnsubscribe(string.Join('.', parts)).Status.Should().Be(MarketingTokenStatus.Invalid);
    }

    [Fact]
    public void A_second_base64_spelling_of_the_same_mac_is_invalid()
    {
        // 32 MAC bytes take 43 base64url characters: the last one carries 4 bits and 2 spare ones.
        // Flipping a spare bit decodes to the same bytes, but it is not the token we minted.
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        string token = _tokens.CreateUnsubscribeToken(Guid.NewGuid());
        string[] parts = token.Split('.');
        int index = Alphabet.IndexOf(parts[3][^1]);
        parts[3] = parts[3][..^1] + Alphabet[index ^ 1];

        _tokens.VerifyUnsubscribe(string.Join('.', parts)).Status.Should().Be(MarketingTokenStatus.Invalid);
    }

    [Fact]
    public void A_swapped_consent_id_is_invalid()
    {
        string a = _tokens.CreateUnsubscribeToken(Guid.NewGuid());
        string b = _tokens.CreateUnsubscribeToken(Guid.NewGuid());
        string[] pa = a.Split('.');
        string[] pb = b.Split('.');
        pa[2] = pb[2];

        _tokens.VerifyUnsubscribe(string.Join('.', pa)).Status.Should().Be(MarketingTokenStatus.Invalid);
    }

    [Fact]
    public void A_confirm_token_used_as_unsubscribe_is_invalid_and_vice_versa()
    {
        Guid id = Guid.NewGuid();
        string confirm = _tokens.CreateConfirmToken(id, Now.AddDays(7));
        string unsubscribe = _tokens.CreateUnsubscribeToken(id);

        _tokens.VerifyUnsubscribe(confirm).Status.Should().Be(MarketingTokenStatus.Invalid);
        _tokens.VerifyConfirm(unsubscribe, Now).Status.Should().Be(MarketingTokenStatus.Invalid);

        // Relabelling the prefix does not help either: the purpose is inside the MAC.
        string[] parts = confirm.Split('.');
        string relabelled = string.Join('.', "u1", parts[1], parts[2], parts[4]);
        _tokens.VerifyUnsubscribe(relabelled).Status.Should().Be(MarketingTokenStatus.Invalid);
    }

    [Fact]
    public void An_expired_confirm_token_is_expired_and_a_tampered_expiry_is_invalid()
    {
        Guid id = Guid.NewGuid();
        string token = _tokens.CreateConfirmToken(id, Now.AddSeconds(-1));

        _tokens.VerifyConfirm(token, Now).Status.Should().Be(MarketingTokenStatus.Expired);

        string[] parts = token.Split('.');
        parts[3] = Now.AddDays(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        _tokens.VerifyConfirm(string.Join('.', parts), Now).Status.Should().Be(MarketingTokenStatus.Invalid);
    }

    [Fact]
    public void Unsubscribe_tokens_do_not_expire()
    {
        string token = _tokens.CreateUnsubscribeToken(Guid.NewGuid());
        _tokens.VerifyUnsubscribe(token).Status.Should().Be(MarketingTokenStatus.Valid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("u1")]
    [InlineData("u1.k1..")]
    [InlineData("u1.k1.!!!.###")]
    [InlineData("c1.k1.AAAAAAAAAAAAAAAAAAAAAA.notanumber.AAAA")]
    [InlineData("x1.k1.AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_tokens_are_invalid_without_throwing(string? token)
    {
        _tokens.VerifyUnsubscribe(token).Status.Should().Be(MarketingTokenStatus.Invalid);
        _tokens.VerifyConfirm(token, Now).Status.Should().Be(MarketingTokenStatus.Invalid);
    }

    [Fact]
    public void An_unknown_key_id_is_invalid_and_never_creates_a_secret()
    {
        string token = _tokens.CreateUnsubscribeToken(Guid.NewGuid());
        string forged = "u1.k9." + string.Join('.', token.Split('.').Skip(2));

        _tokens.VerifyUnsubscribe(forged).Status.Should().Be(MarketingTokenStatus.Invalid);
        _secrets.Requested.Should().OnlyContain(n => n == "marketing-link-token-k1");
    }

    [Fact]
    public void The_token_string_contains_no_email()
    {
        // The only input is the row id: there is no overload that takes an address.
        Guid id = Guid.NewGuid();
        string[] tokens = [_tokens.CreateUnsubscribeToken(id), _tokens.CreateConfirmToken(id, Now.AddDays(7))];

        foreach (string token in tokens)
        {
            token.Should().NotContain("@");
            token.Should().NotContainAny("alice", "example");
        }
    }

    [Fact]
    public void The_link_key_lives_in_platform_secrets_and_is_not_the_assistant_key()
    {
        MarketingLinkTokens.SecretNameFor("k1").Should().Be("marketing-link-token-k1");
        MarketingSuppressionHasher.SecretName.Should().Be("marketing-suppression-pepper");

        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        ServiceCollection services = new();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(connection));
        using ServiceProvider provider = services.BuildServiceProvider();
        ISecretProtector protector = new DataProtectionSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<DataProtectionSecretProtector>.Instance);

        byte[] assistantKey = RandomNumberGenerator.GetBytes(32);
        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Database.EnsureCreated();
            db.PlatformSecrets.Add(new PlatformSecret
            {
                Name = "assistant-confirmation-token",
                Value = protector.Protect(Convert.ToBase64String(assistantKey)),
            });
            db.SaveChanges();
        }

        PlatformSecretKeyStore store = new(
            provider.GetRequiredService<IServiceScopeFactory>(), protector, NullLogger<PlatformSecretKeyStore>.Instance);
        MarketingLinkTokens tokens = new(store);
        Guid id = Guid.NewGuid();
        string token = tokens.CreateUnsubscribeToken(id);

        byte[] linkKey = store.GetKey("marketing-link-token-k1");
        linkKey.Should().NotEqual(assistantKey);
        linkKey.Should().HaveCount(32);
        store.GetKey("marketing-suppression-pepper").Should().NotEqual(linkKey);

        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.PlatformSecrets.Select(s => s.Name).ToList()
                .Should().BeEquivalentTo("assistant-confirmation-token", "marketing-link-token-k1", "marketing-suppression-pepper");
            string stored = db.PlatformSecrets.Single(s => s.Name == "marketing-link-token-k1").Value;
            stored.Should().NotContain(Convert.ToBase64String(linkKey), "the key is stored protected, never in plaintext");
        }

        // A second store over the same database resolves the persisted key, so links survive restarts.
        PlatformSecretKeyStore restarted = new(
            provider.GetRequiredService<IServiceScopeFactory>(), protector, NullLogger<PlatformSecretKeyStore>.Instance);
        new MarketingLinkTokens(restarted).VerifyUnsubscribe(token).Status.Should().Be(MarketingTokenStatus.Valid);

        // And a MAC under the assistant key does not verify.
        MarketingLinkTokens underAssistantKey = new(new FixedStore(assistantKey));
        underAssistantKey.VerifyUnsubscribe(token).Status.Should().Be(MarketingTokenStatus.Invalid);
    }

    private sealed class FixedStore(byte[] key) : IMarketingSecretStore
    {
        public byte[] GetKey(string name) => key;
    }

    [Fact]
    public void The_mac_uses_length_prefixed_framing()
    {
        // Pins the framing so a refactor cannot silently invalidate every unsubscribe link already sent.
        byte[] key = Enumerable.Repeat((byte)7, 32).ToArray();
        MarketingLinkTokens tokens = new(new FixedStore(key));
        Guid id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        static byte[] Frame(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            byte[] length = BitConverter.GetBytes(bytes.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(length);
            return [.. length, .. bytes];
        }

        byte[] message = [.. Frame("unsubscribe"), .. Frame(id.ToString("D"))];
        byte[] mac = HMACSHA256.HashData(key, message);
        string expected = "u1.k1." + Base64Url(id.ToByteArray()) + "." + Base64Url(mac);

        tokens.CreateUnsubscribeToken(id).Should().Be(expected);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
