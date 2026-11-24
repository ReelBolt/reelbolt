#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Services.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// brevo-marketing-campaigns production fix: the campaign create body carries no <c>tag</c> (the account's plan refuses
/// it with 405 method_not_allowed), and a Brevo rejection logs Brevo's own error code, never the body or an address.
/// Fake Brevo only.
/// </summary>
public sealed class BrevoCampaignCreateTests
{
    private readonly FakeBrevo _brevo = new();
    private readonly RecordingLogger<BrevoCampaignClient> _log = new();

    private BrevoCampaignClient Client() => new(
        new HttpClient(_brevo),
        Microsoft.Extensions.Options.Options.Create(new MarketingCampaignOptions
        {
            Enabled = true,
            BrevoCampaignApiKey = "secret-key",
            BrevoApiBase = "https://brevo.example.test/v3",
            BrevoMinCallIntervalMs = 0,
        }),
        MailLogRef.Ephemeral(),
        _log);

    private static BrevoCampaignDraft Draft(params long[] lists)
        => new("reelbolt-test", "[TEST] Subject", "<p>hi</p>", "from@example.test", "Sender", lists);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_campaign_never_sends_a_tag(bool withList)
    {
        long id = await Client().CreateCampaignAsync(withList ? Draft(10) : Draft(), CancellationToken.None);

        id.Should().BeGreaterThan(0);
        FakeBrevo.Call call = _brevo.Calls.Single(c => c is { Method: "POST", Path: "emailCampaigns" });
        using JsonDocument body = JsonDocument.Parse(call.Body!);
        body.RootElement.TryGetProperty("tag", out _).Should().BeFalse();
        body.RootElement.GetProperty("name").GetString().Should().Be("reelbolt-test");
        body.RootElement.GetProperty("htmlContent").GetString().Should().Be("<p>hi</p>");
        body.RootElement.TryGetProperty("recipients", out _).Should().Be(withList);
    }

    [Fact]
    public async Task A_rejection_logs_brevos_error_code_and_nothing_from_the_body()
    {
        _brevo.Override = c => c is { Method: "POST", Path: "emailCampaigns" }
            ? FakeBrevo.Status(HttpStatusCode.MethodNotAllowed,
                "{\"code\":\"method_not_allowed\",\"message\":\"You are not allowed to avail tag option for ada@example.test\"}")
            : null;

        BrevoCampaignException ex = (await Assert.ThrowsAsync<BrevoCampaignException>(
            () => Client().CreateCampaignAsync(Draft(10), CancellationToken.None)));

        ex.Code.Should().Be("brevo_method_not_allowed");
        string warning = _log.Entries.Single(e => e.Level == LogLevel.Warning).Message;
        warning.Should().Contain("status=405").And.Contain("code=brevo_method_not_allowed");
        warning.Should().NotContain("ada@example.test").And.NotContain("not allowed to avail").And.NotContain("secret-key");
    }

    [Fact]
    public async Task A_rejection_without_a_usable_code_logs_the_status_code_word()
    {
        _brevo.Override = c => c is { Method: "POST", Path: "emailCampaigns" }
            ? FakeBrevo.Status(HttpStatusCode.BadRequest, "<html>proxy ada@example.test</html>")
            : null;

        await Assert.ThrowsAsync<BrevoCampaignException>(() => Client().CreateCampaignAsync(Draft(10), CancellationToken.None));

        string warning = _log.Entries.Single(e => e.Level == LogLevel.Warning).Message;
        warning.Should().Contain("code=brevo_http_400").And.NotContain("ada@example.test");
    }
}
