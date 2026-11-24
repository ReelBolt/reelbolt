#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Outputs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// Output links are app-signed capability URLs: ids and expiry only, HMAC'd under op
/// <c>OutputLink.v1</c>, 15 minutes by default and never more than 60.
/// </summary>
public class OutputLinkServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _user = Guid.NewGuid();
    private readonly OutputLinkTarget _target = new(Guid.NewGuid(), OutputLinkKind.StepResult, Guid.NewGuid(), null);

    internal static OutputLinkService Build(
        IConfirmationTokenService? tokens = null,
        FixedClock? clock = null,
        Dictionary<string, string?>? settings = null)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build();
        return new OutputLinkService(tokens ?? ConfirmationTokenService.CreateEphemeral(), config, clock ?? new FixedClock(Now));
    }

    private static string TokenOf(OutputLink link) => link.Url[(link.Url.LastIndexOf('/') + 1)..];

    [Fact]
    public void A_minted_link_round_trips_to_the_same_target_and_user()
    {
        OutputLinkService service = Build();
        OutputLink link = service.Create(_target, _user, null);

        service.TryVerify(TokenOf(link), Now.AddMinutes(1), out OutputLinkClaims claims).Should().BeTrue();
        claims.Target.Should().Be(_target);
        claims.UserId.Should().Be(_user);
        claims.ExpiresAt.Should().Be(link.ExpiresAt);
        claims.IssuedAt.Should().Be(Now);
    }

    [Fact]
    public void A_derivative_sheet_link_carries_its_sheet_index()
    {
        OutputLinkService service = Build();
        OutputLinkTarget sheet = new(Guid.NewGuid(), OutputLinkKind.Derivative, Guid.NewGuid(), 3);

        service.TryVerify(TokenOf(service.Create(sheet, _user, null)), Now, out OutputLinkClaims claims).Should().BeTrue();
        claims.Target.SheetIndex.Should().Be(3);
    }

    [Fact]
    public void The_default_ttl_is_fifteen_minutes()
    {
        Build().Create(_target, _user, null).ExpiresAt.Should().Be(Now.AddMinutes(15));
    }

    [Fact]
    public void The_default_ttl_is_configurable()
    {
        OutputLinkService service = Build(settings: new() { ["OutputLinks:DefaultTtlMinutes"] = "5" });
        service.Create(_target, _user, null).ExpiresAt.Should().Be(Now.AddMinutes(5));
    }

    [Theory]
    [InlineData(600)]
    [InlineData(61)]
    public void A_ttl_over_an_hour_is_clamped_to_an_hour(int minutes)
    {
        Build().Create(_target, _user, TimeSpan.FromMinutes(minutes)).ExpiresAt.Should().Be(Now.AddMinutes(60));
    }

    [Fact]
    public void A_configured_default_over_an_hour_is_clamped_too()
    {
        OutputLinkService service = Build(settings: new() { ["OutputLinks:DefaultTtlMinutes"] = "1440" });
        service.Create(_target, _user, null).ExpiresAt.Should().Be(Now.AddMinutes(60));
    }

    [Fact]
    public void An_expired_link_does_not_verify()
    {
        OutputLinkService service = Build();
        OutputLink link = service.Create(_target, _user, null);

        service.TryVerify(TokenOf(link), link.ExpiresAt, out _).Should().BeFalse();
        service.TryVerify(TokenOf(link), link.ExpiresAt.AddSeconds(1), out _).Should().BeFalse();
    }

    [Fact]
    public void A_tampered_payload_does_not_verify()
    {
        OutputLinkService service = Build();
        string token = TokenOf(service.Create(_target, _user, null));
        string[] parts = token.Split('.');
        string payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));

        // Point the same signature at another output.
        string forged = payload.Replace(_target.Id.ToString("N"), Guid.NewGuid().ToString("N"));
        forged.Should().NotBe(payload);
        string tampered = Base64UrlEncode(Encoding.UTF8.GetBytes(forged)) + "." + parts[1];

        service.TryVerify(tampered, Now, out _).Should().BeFalse();
    }

    [Fact]
    public void A_payload_with_a_later_expiry_does_not_verify()
    {
        OutputLinkService service = Build();
        OutputLink link = service.Create(_target, _user, null);
        string[] parts = TokenOf(link).Split('.');
        string payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
        string forged = payload.Replace(
            link.ExpiresAt.ToUnixTimeSeconds().ToString(),
            link.ExpiresAt.AddDays(30).ToUnixTimeSeconds().ToString());

        service.TryVerify(Base64UrlEncode(Encoding.UTF8.GetBytes(forged)) + "." + parts[1], Now, out _).Should().BeFalse();
    }

    [Fact]
    public void A_tampered_signature_does_not_verify()
    {
        OutputLinkService service = Build();
        string token = TokenOf(service.Create(_target, _user, null));
        // The first signature character carries six real bits (the last one carries padding bits a
        // decoder ignores, so flipping it would not change the MAC at all).
        int at = token.IndexOf('.') + 1;
        string tampered = token[..at] + (token[at] == 'A' ? 'B' : 'A') + token[(at + 1)..];

        service.TryVerify(tampered, Now, out _).Should().BeFalse();
    }

    /// <summary>
    /// QA-P0-16: the last character of an unpadded base64url part carries padding bits a lenient decoder
    /// ignores, so one valid token has several spellings. Only the canonical spelling verifies.
    /// </summary>
    [Fact]
    public void A_non_canonical_base64url_spelling_of_a_valid_link_does_not_verify()
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        OutputLinkService service = Build();
        string token = TokenOf(service.Create(_target, _user, null));
        int last = Alphabet.IndexOf(token[^1]);
        string variant = token[..^1] + Alphabet[last ^ 1];

        static byte[] Decode(string part)
        {
            string b64 = part.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
        }

        Decode(variant[(variant.IndexOf('.') + 1)..]).Should().Equal(Decode(token[(token.IndexOf('.') + 1)..]),
            "the variant differs only in ignored padding bits, so a lenient decoder reads the same MAC");
        service.TryVerify(token, Now, out _).Should().BeTrue();
        service.TryVerify(variant, Now, out _).Should().BeFalse();
    }

    [Fact]
    public void A_signature_minted_for_another_operation_does_not_verify()
    {
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        OutputLinkService service = Build(tokens);
        string token = TokenOf(service.Create(_target, _user, null));
        string payloadPart = token.Split('.')[0];
        string payload = Encoding.UTF8.GetString(Base64UrlDecode(payloadPart));

        // Same key, same payload, same user, different operation.
        string otherOpSig = tokens.Compute("DeleteWorkflow", payload, _user);
        string forged = payloadPart + "." + Base64UrlEncode(Convert.FromHexString(otherOpSig));

        service.TryVerify(forged, Now, out _).Should().BeFalse();
    }

    [Fact]
    public void A_link_from_another_deployment_key_does_not_verify()
    {
        string token = TokenOf(Build().Create(_target, _user, null));
        Build().TryVerify(token, Now, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("abc")]
    [InlineData("abc.def")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    public void Garbage_never_verifies_and_never_throws(string token)
    {
        Build().TryVerify(token, Now, out _).Should().BeFalse();
    }

    [Fact]
    public void The_url_is_absolute_under_the_configured_public_base()
    {
        OutputLinkService service = Build(settings: new() { ["OutputLinks:PublicBaseUrl"] = "https://app.example.com/" });
        service.Create(_target, _user, null).Url.Should().StartWith("https://app.example.com/api/v1/output-links/");
    }

    [Fact]
    public void Without_its_own_setting_the_url_reuses_the_app_public_base()
    {
        OutputLinkService service = Build(settings: new() { ["CloudStorage:PublicBaseUrl"] = "https://reelbolt.example" });
        service.Create(_target, _user, null).Url.Should().StartWith("https://reelbolt.example/api/v1/output-links/");
    }

    [Fact]
    public void Without_any_public_base_the_url_is_relative()
    {
        Build().Create(_target, _user, null).Url.Should().StartWith("/api/v1/output-links/");
    }

    [Fact]
    public void The_url_carries_ids_only_never_a_storage_key()
    {
        OutputLink link = Build().Create(_target, _user, null);
        string payload = Encoding.UTF8.GetString(Base64UrlDecode(TokenOf(link).Split('.')[0]));
        payload.Should().StartWith("v1|");
        payload.Should().NotContain("projects/");
        payload.Should().NotContain("outputFiles");
    }

    internal static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Base64UrlDecode(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}
