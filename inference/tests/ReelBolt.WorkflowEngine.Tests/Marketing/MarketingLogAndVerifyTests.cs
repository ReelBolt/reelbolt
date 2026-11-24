#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// saas-promotions T-024: no address, token or tokenised link in any log line of the account-mail
/// and marketing path, and the consent endpoints' verified-email gate read
/// from the database rather than the JWT claim (Security N-08).
/// </summary>
public sealed class MarketingLogAndVerifyTests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private const string AddressBearingError = "550 5.1.1 <victim@example.com>: Recipient address rejected: user unknown";
    private readonly MarketingHarness _h = new();

    public void Dispose() => _h.Dispose();

    // --- C4: account-mail and outbox logs ------------------------------------------------------

    private sealed class Handler(HttpStatusCode status, bool throwing) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throwing
                ? throw new HttpRequestException(AddressBearingError, new InvalidOperationException("inner for ada@example.com"))
                : Task.FromResult(new HttpResponseMessage(status));
    }

    private static AccountMail Mail(string template) => new(
        "ada@example.com",
        template,
        new AccountMailData { Name = "Ada", Link = "https://app.example.test/app/verify?token=secret-token-123", ExpiresIn = "7 days" });

    [Theory]
    [InlineData("export_ready", "s3cret", true)]
    [InlineData("export_ready", "s3cret", false)]
    [InlineData("export_ready", "", false)]
    [InlineData("verify_email", "s3cret", true)]
    [InlineData("marketing-confirm", "s3cret", true)]
    [InlineData("marketing-confirm", "", false)]
    public async Task A_failed_account_or_marketing_mail_logs_no_address_and_no_token(string template, string token, bool throwing)
    {
        RecordingLogger<GoAccountMailClient> logger = new();
        GoAccountMailClient client = new(
            new HttpClient(new Handler(HttpStatusCode.UnprocessableEntity, throwing)),
            Options.Create(new AccountOptions { InternalBaseUrl = "http://go-api:8080", InternalToken = token }),
            logger);

        (await client.SendAsync(Mail(template), CancellationToken.None)).Should().BeFalse();

        logger.Entries.Should().NotBeEmpty();
        logger.Entries.Should().AllSatisfy(e => e.Message.Should()
            .NotContain("@").And.NotContain("secret-token-123").And.NotContain("token="));
    }

    [Fact]
    public async Task A_failed_mail_logs_the_keyed_recipient_ref_and_the_error_class()
    {
        RecordingLogger<GoAccountMailClient> logger = new();
        GoAccountMailClient client = new(
            new HttpClient(new Handler(HttpStatusCode.Accepted, throwing: true)),
            Options.Create(new AccountOptions { InternalBaseUrl = "http://go-api:8080", InternalToken = "s3cret" }),
            logger,
            MailLogRef.ForKey("test-log-key"));

        await client.SendAsync(new AccountMail("Victim+x@Gmail.com", "export_ready", new AccountMailData()), CancellationToken.None);

        logger.Entries.Should().ContainSingle().Which.Message.Should()
            .Contain("rcpt=aa31e47adc42").And.Contain(nameof(HttpRequestException)).And.Contain("550 5.1.1");
    }

    private sealed class ThrowingClient : IAccountMailClient
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> SendAsync(AccountMail mail, CancellationToken ct)
        {
            Called.TrySetResult();
            throw new InvalidOperationException(AddressBearingError + " link https://x.test/confirm?token=tok-999");
        }
    }

    [Fact]
    public async Task The_outbox_logs_a_failed_send_without_the_address_or_token_in_the_exception()
    {
        ThrowingClient client = new();
        ServiceCollection services = new();
        services.AddSingleton<IAccountMailClient>(client);
        using ServiceProvider provider = services.BuildServiceProvider();
        RecordingLogger<MarketingMailOutbox> logger = new();
        using MarketingMailOutbox outbox = new(provider.GetRequiredService<IServiceScopeFactory>(), logger);

        outbox.Enqueue(new AccountMail("victim@example.com", MarketingConsentService.ConfirmTemplate, new AccountMailData()), MarketingMailOrigin.Anonymous);
        await outbox.StartAsync(CancellationToken.None);
        await client.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 100 && logger.Entries.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        await outbox.StopAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Message.Should().NotContain("@").And.NotContain("tok-999").And.Contain(nameof(InvalidOperationException));
    }

    // --- N-08: the consent gate re-reads EmailVerifiedAt ----------------------------------------

    /// <summary>A signed-in caller whose token carries the given emailVerified claim (null: none at all).</summary>
    private sealed class User(Guid id, string email, bool? claim) : ICurrentUser
    {
        public Guid UserId => id;
        public string Email => email;
        public bool IsAuthenticated => true;
        public bool EmailVerified => claim ?? true; // a missing claim reads as true, like ReadEmailVerified
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => false;
    }

    private MarketingConsentController Controller(InferenceApiDbContext db, ICurrentUser user)
    {
        MarketingConsentController controller = new(_h.NewService(db), _h.Wording, user);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task An_account_unverified_in_the_database_is_refused_whatever_its_token_claims(bool? claim)
    {
        Guid id = _h.AddUser("claims@example.com", verifiedAt: null);
        User user = new(id, "claims@example.com", claim);

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await Controller(db, user).GetConsent(CancellationToken.None))
                .Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        }

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await Controller(db, user).PutConsent(new MarketingConsentRequest(true, "settings", V1), CancellationToken.None))
                .Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        }
    }

    [Fact]
    public async Task An_unknown_user_id_is_refused()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        (await Controller(db, new User(Guid.NewGuid(), "ghost@example.com", null)).GetConsent(CancellationToken.None))
            .Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task An_account_verified_in_the_database_is_let_through_even_with_a_stale_false_claim()
    {
        Guid id = _h.AddUser("fresh@example.com", _h.Now);
        await using InferenceApiDbContext db = _h.NewDb();

        (await Controller(db, new User(id, "fresh@example.com", false)).GetConsent(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }
}
