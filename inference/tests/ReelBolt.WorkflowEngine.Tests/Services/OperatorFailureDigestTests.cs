#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Activity;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// The daily operator digest: which failed steps it reports (the refund mapping, not a hand list), what the mail
/// may carry (redacted, cut, no personal data), when it runs (off = no query; once a day across replicas) and how it
/// fails (never crashes, retries).
/// </summary>
public class OperatorFailureDigestTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Slot = new(2026, 11, 16, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid StepId = Guid.NewGuid();

    private static FailureDigestOptions Enabled() => new() { Enabled = true, RecipientEmail = "ops@example.test" };

    [Fact]
    public void It_is_off_by_default_and_enabled_without_a_recipient_fails_startup()
    {
        new FailureDigestOptions().Enabled.Should().BeFalse();
        var validator = new FailureDigestOptionsValidator();

        validator.Validate(null, new FailureDigestOptions()).Succeeded.Should().BeTrue("disabled and unset is inert");
        ValidateOptionsResult noRecipient = validator.Validate(null, new FailureDigestOptions { Enabled = true });
        noRecipient.Failed.Should().BeTrue();
        noRecipient.FailureMessage.Should().Contain("RecipientEmail");
        validator.Validate(null, new FailureDigestOptions { Enabled = true, RecipientEmail = "Ops <ops@example.test>" }).Failed.Should().BeTrue();
        validator.Validate(null, new FailureDigestOptions { Enabled = true, RecipientEmail = "ops@example.test", HourUtc = 24 }).Failed.Should().BeTrue();
        validator.Validate(null, Enabled()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(true, "ops@example.test", false)]
    public void The_bound_configuration_fails_when_enabled_without_a_recipient(bool enabled, string? email, bool throws)
    {
        var values = new Dictionary<string, string?> { ["Operations:FailureDigest:Enabled"] = enabled.ToString() };
        if (email is not null)
        {
            values["Operations:FailureDigest:RecipientEmail"] = email;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddOptions<FailureDigestOptions>().Bind(configuration.GetSection(FailureDigestOptions.SectionName));
        services.AddSingleton<IValidateOptions<FailureDigestOptions>, FailureDigestOptionsValidator>();
        using ServiceProvider provider = services.BuildServiceProvider();

        Func<FailureDigestOptions> resolve = () => provider.GetRequiredService<IOptions<FailureDigestOptions>>().Value;
        if (throws)
        {
            resolve.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("OPERATOR_DIGEST_EMAIL");
        }
        else
        {
            resolve.Should().NotThrow();
        }
    }

    [Fact]
    public async Task Disabled_runs_no_query_and_sends_nothing()
    {
        var mail = new FakeMail();
        using ServiceProvider provider = Build(mail);
        var service = Service(provider, new FailureDigestOptions());

        (await service.CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.Disabled);

        mail.Sent.Should().BeEmpty();
        provider.GetRequiredService<FakeCounters>().Calls.Should().Be(0, "the claim is the first thing touched, and it is not touched");
    }

    [Fact]
    public async Task Only_failures_the_platform_bears_are_reported_and_they_are_grouped()
    {
        var mail = new FakeMail();
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.AddRange(
                Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, minutesAgo: 30, "boom one"),
                Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgB, minutesAgo: 20, "boom two"),
                Failed(FailureClass.PlatformProviderError, "SEGMENTATION_PROVIDER_ERROR", OrgA, minutesAgo: 10, "provider down"),
                // Not the platform's: user input, user stop, own runner, BYO provider, credits, entitlement.
                Failed(FailureClass.UserInput, "CONFIG_INVALID", OrgA, 10, "bad config"),
                Failed(FailureClass.UserCancelled, "USER_CANCELLED", OrgA, 10, "stopped"),
                Failed(FailureClass.Runner, "RUNNER_X", OrgA, 10, "runner"),
                Failed(FailureClass.ByoProviderError, "BYO", OrgA, 10, "byo"),
                Failed(FailureClass.InsufficientCredits, "NO_CREDITS", OrgA, 10, "credits"),
                Failed(FailureClass.EntitlementDenied, "DENIED", OrgA, 10, "entitlement"),
                // Outside the window, and a completed step.
                Failed(FailureClass.PlatformInternal, "OLD", OrgA, minutesAgo: 60 * 30, "old"),
                Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, 5, "fine", status: StepStatus.Completed));
        });

        OperatorDigestData digest = await OperatorFailureDigestService.BuildAsync(
            Db(provider), Now.UtcDateTime.AddHours(-24), Now.UtcDateTime, 50, CancellationToken.None);

        digest.Groups.Select(g => g.Code).Should().Equal("STEP_INTERNAL_ERROR", "SEGMENTATION_PROVIDER_ERROR");
        digest.Groups[0].Count.Should().Be(2);
        digest.Groups[0].Organizations.Should().Be(2);
        digest.Groups[0].StepType.Should().Be(nameof(StepType.VideoCompile));
        digest.Failures.Should().HaveCount(3);
        digest.Failures[0].Error.Should().Be("provider down", "newest first");
        digest.Failures.All(f => Guid.TryParse(f.RunId, out _)).Should().BeTrue();
        digest.FailuresOmitted.Should().Be(0);
        digest.Runs.Should().ContainSingle(r => r.Label == "Failed" && r.Count == 10, "every in-window failed step above sits in its own failed run; the 30h-old one is outside");

        // The classification is the shared refund rule, over every enum member.
        foreach (FailureClass c in Enum.GetValues<FailureClass>())
        {
            ActivityFailureRefund.IsRefunded(c).Should().Be(
                c is FailureClass.None or FailureClass.PlatformInternal or FailureClass.PlatformProviderError, $"{c}: the digest relies on the refund rule");
        }
    }

    [Fact]
    public async Task A_failed_step_with_no_assigned_class_is_refunded_so_it_is_reported()
    {
        using ServiceProvider provider = Build(new FakeMail(), db =>
        {
            Seed(db);
            db.WorkflowStepResults.Add(Failed(FailureClass.None, "NONE_CLASS", OrgA, 10, "unclassified"));
        });

        OperatorDigestData digest = await OperatorFailureDigestService.BuildAsync(
            Db(provider), Now.UtcDateTime.AddHours(-24), Now.UtcDateTime, 50, CancellationToken.None);

        digest.Groups.Should().ContainSingle(g => g.Code == "NONE_CLASS" && g.Count == 1);
        digest.Failures.Should().ContainSingle(f => f.Code == "NONE_CLASS" && f.Error == string.Empty, "an unclassified failure is an agent's own report and may quote model output: no text");
    }

    [Fact]
    public async Task MaxRows_caps_the_lines_and_reports_how_many_were_left_out()
    {
        using ServiceProvider provider = Build(new FakeMail(), db =>
        {
            Seed(db);
            for (int i = 0; i < 5; i++)
            {
                db.WorkflowStepResults.Add(Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, minutesAgo: i + 1, $"e{i}"));
            }
        });

        OperatorDigestData digest = await OperatorFailureDigestService.BuildAsync(
            Db(provider), Now.UtcDateTime.AddHours(-24), Now.UtcDateTime, 2, CancellationToken.None);

        digest.Failures.Should().HaveCount(2);
        digest.FailuresOmitted.Should().Be(3);
        digest.Groups.Single().Count.Should().Be(5);
    }

    [Fact]
    public void Error_text_is_redacted_before_it_is_cut_and_cut_to_160_characters()
    {
        string secret = "sk-ant-api03-" + new string('A', 60);
        string text = "failed calling provider with key " + secret + "\n\n   and more\ttext " + new string('x', 400);

        string excerpt = OperatorFailureDigestService.Excerpt(text);

        excerpt.Should().NotContain(secret).And.NotContain("sk-ant");
        excerpt.Should().Contain("[redacted]");
        excerpt.Length.Should().BeLessThanOrEqualTo(160);
        excerpt.Should().NotContain("\n").And.NotContain("\t");

        // A secret that straddles the 160-character boundary is still redacted: redaction runs first.
        string straddle = new string('y', 150) + " " + secret;
        OperatorFailureDigestService.Excerpt(straddle).Should().NotContain("sk-ant");
        OperatorFailureDigestService.Excerpt(null).Should().BeEmpty();
    }

    [Fact]
    public async Task Feedback_in_the_window_is_counted_listed_redacted_and_cut()
    {
        string secret = "sk-ant-api03-" + new string('B', 60);
        using ServiceProvider provider = Build(new FakeMail(), db =>
        {
            db.FeedbackReports.AddRange(
                new FeedbackReport { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OrganizationId = OrgA, Kind = FeedbackReport.BugKind, Message = "it broke " + secret + new string('z', 300), CreatedAt = Now.UtcDateTime.AddHours(-2) },
                new FeedbackReport { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OrganizationId = OrgA, Kind = FeedbackReport.FeedbackKind, Message = "nice", CreatedAt = Now.UtcDateTime.AddHours(-1) },
                new FeedbackReport { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OrganizationId = OrgA, Kind = FeedbackReport.BugKind, Message = "yesterday", CreatedAt = Now.UtcDateTime.AddHours(-40) });
        });

        OperatorDigestData digest = await OperatorFailureDigestService.BuildAsync(
            Db(provider), Now.UtcDateTime.AddHours(-24), Now.UtcDateTime, 50, CancellationToken.None);

        digest.FeedbackCount.Should().Be(2);
        digest.Feedback.Select(f => f.Kind).Should().Equal("feedback", "bug");
        digest.Feedback[1].Text.Length.Should().BeLessThanOrEqualTo(160);
        digest.Feedback[1].Text.Should().NotContain("sk-ant");
    }

    [Fact]
    public async Task The_mail_carries_no_address_name_or_user_id_and_goes_to_the_recipient_only()
    {
        var mail = new FakeMail();
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.Add(Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, 10, "render failed"));
        });

        (await Service(provider, Enabled()).CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.Sent);

        AccountMail sent = mail.Sent.Single();
        sent.To.Should().Be("ops@example.test");
        sent.Template.Should().Be("operator_digest");
        sent.Locale.Should().Be("en");
        string json = JsonSerializer.Serialize(sent.Data);
        json.Should().NotContain("@").And.NotContain(OrgA.ToString()).And.NotContain("\"name\":\"");
        json.Should().Contain("STEP_INTERNAL_ERROR").And.Contain("\"runId\"");
        GoAccountMailClient.AllowedTemplates.Should().Contain("operator_digest");
    }

    [Fact]
    public async Task An_empty_window_sends_nothing_unless_SendWhenEmpty_and_keeps_the_days_claim()
    {
        var mail = new FakeMail();
        using ServiceProvider provider = Build(mail);

        (await Service(provider, Enabled()).CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.Empty);
        mail.Sent.Should().BeEmpty();
        (await Service(provider, Enabled()).CheckOnceAsync(Now.AddHours(1))).Should().Be(FailureDigestOutcome.AlreadyClaimed);

        var mail2 = new FakeMail();
        using ServiceProvider provider2 = Build(mail2);
        var withEmpty = new FailureDigestOptions { Enabled = true, RecipientEmail = "ops@example.test", SendWhenEmpty = true };
        (await Service(provider2, withEmpty).CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.Sent);
        mail2.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task It_is_not_due_before_the_hour_and_sends_once_per_day_across_replicas()
    {
        var mail = new FakeMail();
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.Add(Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, 10, "x"));
        });
        FailureDigestOptions options = Enabled();

        (await Service(provider, options).CheckOnceAsync(new DateTimeOffset(2026, 11, 16, 5, 59, 0, TimeSpan.Zero)))
            .Should().Be(FailureDigestOutcome.NotDue);

        // Two "replicas" (two services over the same store) racing for the same day.
        FailureDigestOutcome[] outcomes = await Task.WhenAll(
            Service(provider, options).CheckOnceAsync(Now),
            Service(provider, options).CheckOnceAsync(Now),
            Service(provider, options).CheckOnceAsync(Now));

        outcomes.Count(o => o == FailureDigestOutcome.Sent).Should().Be(1);
        outcomes.Count(o => o == FailureDigestOutcome.AlreadyClaimed).Should().Be(2);
        mail.Sent.Should().ContainSingle();

        // The next day is a new claim (its own window is empty here, so ask for the empty mail).
        (await Service(provider, options with { SendWhenEmpty = true }).CheckOnceAsync(Now.AddDays(1))).Should().Be(FailureDigestOutcome.Sent);
    }

    [Fact]
    public async Task A_failed_send_is_not_thrown_and_releases_the_claim_so_the_next_check_retries()
    {
        var mail = new FakeMail { Succeed = false };
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.Add(Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, 10, "x"));
        });
        var service = Service(provider, Enabled());

        (await service.CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.SendFailed);

        mail.Succeed = true;
        (await service.CheckOnceAsync(Now.AddMinutes(15))).Should().Be(FailureDigestOutcome.Sent);
        mail.Sent.Should().HaveCount(2, "the first attempt was recorded as made, the second was accepted");
    }

    [Fact]
    public async Task A_relay_4xx_is_one_attempt_and_no_more_queries_or_mails_that_day()
    {
        var mail = new FakeMail { Succeed = false, Status = 403 };
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.Add(Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, 10, "x"));
        });
        var service = Service(provider, Enabled());

        (await service.CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.Refused);
        (await service.CheckOnceAsync(Now.AddMinutes(15))).Should().Be(FailureDigestOutcome.AlreadyClaimed);
        (await service.CheckOnceAsync(Now.AddMinutes(30))).Should().Be(FailureDigestOutcome.AlreadyClaimed);

        mail.Sent.Should().ContainSingle("a refusal is not retried");
    }

    [Fact]
    public async Task Transient_failures_stop_after_three_attempts_a_day()
    {
        var mail = new FakeMail { Succeed = false, Status = 503 };
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.Add(Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, 10, "x"));
        });
        var service = Service(provider, Enabled());

        (await service.CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.SendFailed);
        (await service.CheckOnceAsync(Now.AddMinutes(15))).Should().Be(FailureDigestOutcome.SendFailed);
        (await service.CheckOnceAsync(Now.AddMinutes(30))).Should().Be(FailureDigestOutcome.SendFailed);
        (await service.CheckOnceAsync(Now.AddMinutes(45))).Should().Be(FailureDigestOutcome.AlreadyClaimed, "the third attempt kept the claim");
        (await service.CheckOnceAsync(Now.AddMinutes(60))).Should().Be(FailureDigestOutcome.AlreadyClaimed);

        mail.Sent.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("download https://bucket.s3.eu.example.com/projects/0f8fad5b-d9cb-469f-a165-70867728950e/outputs/My%20Film.mp4?X-Amz-Signature=abc123&X-Amz-Credential=zzz failed", new[] { "My%20Film", "abc123", "zzz", "0f8fad5b" }, "[link]")]
    [InlineData("connection failed: Host=db;Password=x;Database=app", new[] { "Password=x" }, "Password=[redacted]")]
    [InlineData("user ada.lovelace@example.org said it broke", new[] { "ada.lovelace", "example.org" }, "[email]")]
    [InlineData("call https://api.vendor.test/v1/run?key=0f8fad5b-d9cb-469f-a165-70867728950e:deadbeefcafe1234 returned 500", new[] { "deadbeefcafe", "0f8fad5b", "/v1/run" }, "[link]")]
    [InlineData("missing object projects/0f8fad5b-d9cb-469f-a165-70867728950e/agentFiles/clip.mp4 in store", new[] { "clip.mp4", "0f8fad5b" }, "projects/[redacted]")]
    [InlineData("retry with api_key=sk_live_" + "1234567890 and token=abc", new[] { "sk_live", "abc" }, "api_key=[redacted]")]
    public void The_digest_scrub_removes_urls_paths_params_keys_and_addresses(string text, string[] gone, string kept)
    {
        string excerpt = OperatorFailureDigestService.Excerpt(text);

        foreach (string g in gone)
        {
            excerpt.Should().NotContain(g);
        }

        excerpt.Should().Contain(kept);
        excerpt.Should().NotContain("://").And.NotContain("%3A%2F%2F");
    }

    [Fact]
    public void A_secret_straddling_the_old_4000_character_boundary_is_still_redacted()
    {
        string secret = "sk-ant-api03-" + new string('C', 60);

        // Far past the kept 160 characters: only the "redaction runs on the whole text before the cut" rule would hide it
        // from a naive implementation that cut first, so assert on what the excerpt keeps, not on what it never reaches.
        string deep = new string('y', 3990) + " " + secret + " tail";
        OperatorFailureDigestService.Excerpt(deep).Should().NotContain("sk-ant");

        // A secret that starts inside the kept 160 and ends past it: without redaction the prefix would be in the excerpt.
        string straddling = new string('z', 150) + " " + secret;
        string excerpt = OperatorFailureDigestService.Excerpt(straddling);
        excerpt.Should().NotContain("sk-ant").And.NotContain("api03").And.NotContain("CCCC");
        excerpt.Should().StartWith(new string('z', 150) + " [redact");

        // A secret fully inside the kept text.
        OperatorFailureDigestService.Excerpt("failed with " + secret).Should().NotContain("sk-ant").And.Contain("[redacted]");
    }

    [Theory]
    [InlineData("see https%3A%2F%2Fbucket.s3.example.com%2Fprojects%2Fabc%2Fout%2Ffile.mp4%3FX-Amz-Signature%3Dsig999%26X-Amz-Credential%3Dcred777 now", new[] { "sig999", "cred777", "file.mp4", "/out" }, "[link]")]
    [InlineData("cache down redis://:hunter2pw@cache.internal:6379/0 timeout", new[] { "hunter2pw", "/0" }, "[link]")]
    [InlineData("db postgres://app:s3cr3tpw@db.internal/app refused", new[] { "s3cr3tpw", "app:", "db.internal/app", "/app" }, "[link]")]
    [InlineData("fetch https://u:p4ss@bucket.s3.amazonaws.com/some/clip.mp4?X-Amz-Signature=sig123 failed", new[] { "p4ss", "u:", "/some", "clip.mp4", "sig123", "@" }, "[link]")]
    [InlineData("fetch https%3A%2F%2Fu%3Ap4ss%40bucket.s3.amazonaws.com%2Fsome%2Fclip.mp4%3Fsig%3D123 failed", new[] { "p4ss", "/some", "clip.mp4", "sig%3D", "%40" }, "[link]")]
    [InlineData("config password='hunter two' loaded", new[] { "hunter", "two" }, "password=[redacted] loaded")]
    [InlineData("config password=\"hunter two\" loaded", new[] { "hunter", "two" }, "password=[redacted] loaded")]
    [InlineData("config password: \n  nextline-secret rest", new[] { "nextline-secret" }, "password: [redacted]")]
    [InlineData("config password='unterminated tail", new[] { "unterminated" }, "password=[redacted]")]
    [InlineData("body {\"api_key\": \"zq81-secretvalue\", \"x\": 1}", new[] { "secretvalue" }, "[redacted]")]
    [InlineData("body {\"password\":\"hunter two\"}", new[] { "hunter", "two" }, "[redacted]")]
    [InlineData("body {\"token\" : \"tok_abcdef123456\"}", new[] { "tok_abcdef" }, "[redacted]")]
    [InlineData("login failed Password: swordfish1 for service", new[] { "swordfish1" }, "Password: [redacted]")]
    [InlineData("sent x-api-key: kk_live_77aa88bb to vendor", new[] { "kk_live", "77aa88bb" }, "[redacted]")]
    [InlineData("sent xi-api-key: xi_9f8e7d6c5b to vendor", new[] { "xi_9f8e", "9f8e7d6c5b" }, "[redacted]")]
    [InlineData("header Authorization: Key keyid77:keysecret88 rejected", new[] { "keyid77", "keysecret88" }, "Authorization: [redacted]")]
    [InlineData("header Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sigpart rejected", new[] { "eyJhbGci", "sigpart" }, "Authorization: [redacted]")]
    [InlineData("got Bearer abcdef0123456789 back", new[] { "abcdef0123" }, "Bearer [redacted]")]
    [InlineData("key sk-" + "proj-abcdEFGH12345678 invalid", new[] { "sk-" + "proj", "abcdEFGH" }, "[redacted]")]
    [InlineData("key sk_test_" + "4eC39HqLyjWDarjtT1zdp7dc invalid", new[] { "sk_test", "4eC39H" }, "[redacted]")]
    [InlineData("missing s3://tenant-bucket/projects/a/b/clip.mp4 in store", new[] { "clip.mp4", "projects/a" }, "[link]")]
    [InlineData("user josé.garcía@exämple.es wrote", new[] { "josé", "garcía", "exämple" }, "[email]")]
    [InlineData("user 山田太郎@例え.日本 wrote", new[] { "山田", "例え" }, "[email]")]
    [InlineData("user ada\uFF20example.org wrote", new[] { "ada", "example.org" }, "[email]")]
    public void The_digest_scrub_also_removes_encoded_urls_credentials_headers_and_international_addresses(string text, string[] gone, string kept)
    {
        string excerpt = OperatorFailureDigestService.Excerpt(text);

        foreach (string g in gone)
        {
            excerpt.Should().NotContain(g);
        }

        excerpt.Should().Contain(kept);
        excerpt.Should().NotContain("://").And.NotContain("%3A%2F%2F");
    }

    [Theory]
    [InlineData("fail https://user:p@ss@host.example/path?x=1 end", new[] { "p@ss", "ss@", "user", "/path", "x=1" })]
    [InlineData("fail postgres://app:pa:ss@db.internal/app end", new[] { "pa:ss", "app:", "ss@", "/app" })]
    [InlineData("fail https://user:p%40ss%3Aw0rd@bucket.s3.amazonaws.com/k/clip.mp4?X-Amz-Signature=abc end", new[] { "p%40ss", "w0rd", "user", "/k", "clip.mp4", "abc" })]
    [InlineData("fail redis://:p/a?s#s@cache:6379/0 end", new[] { ":p/a", "s#s", "6379", "/0" })]
    [InlineData("fail amqp://guest:gu!est$&'()*+,;=@rabbit/vhost end", new[] { "gu!est", "guest", "$&", "vhost" })]
    [InlineData("fail https://:pw0rdonly@host.example/x end", new[] { "pw0rdonly", "/x" })]
    [InlineData("fail https://useronly@host.example/x end", new[] { "useronly", "/x" })]
    [InlineData("fail https://u:p@[::1]:8443/x end", new[] { "u:p", "8443", "/x" })]
    [InlineData("fail https://cb.example/r?next=https%3A%2F%2Fu%3Ap%40ss%3Aw%40rd%40inner.example%2Fk%2Fclip.mp4%3Fsig%3D123 end", new[] { "p%40ss", "w%40rd", "inner.example", "clip.mp4", "sig", "123", "/r", "next" })]
    [InlineData("fail https%3A%2F%2Fu%3Ap%40ss%40bucket.example%2Fk%2Fclip.mp4%3Fsig%3D123 end", new[] { "p%40ss", "p@ss", "u:", "clip.mp4", "sig", "123", "/k" })]
    public void A_url_with_awkward_credentials_becomes_a_bare_link_through_the_digest_path(string text, string[] gone)
    {
        string excerpt = OperatorFailureDigestService.Excerpt(text);

        foreach (string g in gone)
        {
            excerpt.Should().NotContain(g);
        }

        excerpt.Should().Be("fail [link] end", "the whole URL goes, whatever the password or the host contains");
        OperatorFailureDigestService.Scrub(text).Should().NotContain(gone[0], "Scrub alone also removes it");
    }

    [Theory]
    [InlineData("see https://api.vendor.com/v1/x, contact bob@corp.example", new[] { "api.vendor.com", "/v1/x", "bob", "corp.example" })]
    [InlineData("go https://host.example/path?next=a@b.c now", new[] { "host.example", "/path", "next", "a@b.c", "b.c" })]
    [InlineData("go https://host.example/p#frag@evil now", new[] { "host.example", "/p", "frag", "evil" })]
    [InlineData("(https://u:p@host.example/x) then token=abc", new[] { "u:p", "host.example", "/x", "abc" })]
    [InlineData("\"https://host.example/a\",\"key\":\"s3cr3t@x\"", new[] { "host.example", "/a", "s3cr3t", "@x" })]
    [InlineData("https://host.example/x] user@y.example", new[] { "host.example", "/x", "user", "y.example" })]
    [InlineData("see https://host.example/q, password: hunter2@home", new[] { "host.example", "/q", "hunter2" })]
    public void Text_around_a_url_is_not_read_as_part_of_it_and_nothing_leaks(string text, string[] gone)
    {
        string excerpt = OperatorFailureDigestService.Excerpt(text);

        foreach (string g in gone)
        {
            excerpt.Should().NotContain(g);
        }

        excerpt.Should().Contain("[link]");
    }

    [Theory]
    [InlineData("connect reelbolt:hunter2pass@postgres:5432/reelbolt now", new[] { "hunter2pass", "postgres", "5432", "@" }, "[redacted]")]
    [InlineData("connect reelbolt:hunter2pass@db.example.com:5432/reelbolt now", new[] { "hunter2pass", "db.example.com", "5432", "[email]", "@" }, "[redacted]")]
    [InlineData("connect app:s3cr3t@[::1]:5432/app now", new[] { "s3cr3t", "::1", "5432", "@" }, "[redacted]")]
    [InlineData("fail https://user:pa ss@host.example/ tail", new[] { "pa ss", "ss@", "host.example", "user" }, "[link]")]
    [InlineData("fail https://bob:my pass@h.example.com/x done", new[] { "my pass", "pass@", "h.example.com", "[email]", "/x" }, "[link]")]
    [InlineData("fail https://bob:pa\"ss@host.example/p end", new[] { "pa\"ss", "ss@", "host.example", "/p" }, "[link]")]
    [InlineData("fail https://bob:pa<ss>x@host.example/p end", new[] { "pa<ss", "ss>", "x@", "host.example", "/p" }, "[link]")]
    [InlineData("get https%253A%252F%252Fbucket.s3.amazonaws.com%252Fk%252Fclip.mp4%253FX-Amz-Signature%253Dsig999 failed", new[] { "bucket", "clip.mp4", "sig999", "X-Amz", "%25" }, "[link]")]
    [InlineData("get https%3A//bucket.example/k/clip.mp4?X-Amz-Signature=sig999 failed", new[] { "bucket", "clip.mp4", "sig999", "X-Amz" }, "[link]")]
    [InlineData("body {\"u\":\"https:\\/\\/bucket.example\\/k\\/clip.mp4?X-Amz-Signature=sig999\"}", new[] { "bucket", "clip.mp4", "sig999", "X-Amz" }, "[link]")]
    [InlineData("sent Cookie: sid=abc123; theme=dark to vendor", new[] { "abc123", "theme", "dark" }, "Cookie: [redacted]")]
    [InlineData("got Set-Cookie: sess=zzz9; Path=/; HttpOnly", new[] { "zzz9", "HttpOnly" }, "Set-Cookie: [redacted]")]
    [InlineData("oauth ya" + "29.a0AfH6SMBx1234567890abcdef failed", new[] { "ya29", "a0AfH6", "SMBx" }, "[redacted]")]
    public void The_scrub_also_covers_scheme_less_credentials_split_urls_encoded_forms_cookies_and_google_tokens(string text, string[] gone, string kept)
    {
        string excerpt = OperatorFailureDigestService.Excerpt(text);

        foreach (string g in gone)
        {
            excerpt.Should().NotContain(g);
        }

        excerpt.Should().Contain(kept);
    }

    [Fact]
    public async Task A_row_whose_scrub_times_out_becomes_unreadable_and_the_rest_of_the_digest_still_goes_out()
    {
        var mail = new FakeMail();
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.AddRange(
                Failed(FailureClass.PlatformInternal, "FINE_ONE", OrgA, 10, "render failed cleanly"),
                Failed(FailureClass.PlatformInternal, "SLOW_ROW", OrgA, 20, "TIMEOUT-MARKER password=hunter2slow https://host.example/p?sig=zzz"),
                Failed(FailureClass.PlatformInternal, "FINE_TWO", OrgA, 30, "disk full"));
        });

        OperatorFailureDigestService.ScrubProbe = text =>
        {
            if (text.Contains("TIMEOUT-MARKER", StringComparison.Ordinal))
            {
                throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
            }
        };
        try
        {
            (await Service(provider, Enabled()).CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.Sent);
        }
        finally
        {
            OperatorFailureDigestService.ScrubProbe = null;
        }

        OperatorDigestData digest = mail.Sent.Single().Data.Digest!;
        digest.Failures.Single(f => f.Code == "SLOW_ROW").Error.Should().Be(OperatorFailureDigestService.UnreadableExcerpt);
        digest.Failures.Single(f => f.Code == "FINE_ONE").Error.Should().Be("render failed cleanly");
        digest.Failures.Single(f => f.Code == "FINE_TWO").Error.Should().Be("disk full");
        string json = System.Text.Json.JsonSerializer.Serialize(digest, OperatorFailureDigestService.WireOptions);
        json.Should().NotContain("hunter2slow").And.NotContain("host.example").And.NotContain("TIMEOUT-MARKER");
    }

    [Fact]
    public async Task The_window_filter_keeps_exactly_the_rows_the_coalesce_form_kept()
    {
        DateTime from = Slot.AddHours(-24);
        DateTime to = Slot;
        WorkflowStepResult Row(string code, DateTime? completed, DateTime executed)
        {
            WorkflowStepResult r = Failed(FailureClass.PlatformInternal, code, OrgA, 1, "x");
            r.CompletedAt = completed;
            r.ExecutedAt = executed;
            return r;
        }

        WorkflowStepResult[] rows =
        [
            Row("COMPLETED_IN", Slot.AddHours(-3), Slot.AddHours(-4)),
            Row("COMPLETED_AT_FROM", from, from.AddHours(-1)),
            Row("COMPLETED_AT_TO", to, to.AddHours(-1)),
            Row("COMPLETED_JUST_BEFORE_TO", to.AddTicks(-1), to.AddHours(-1)),
            Row("COMPLETED_BEFORE", from.AddTicks(-1), Slot.AddHours(-2)),
            Row("COMPLETED_OUT_EXECUTED_IN", to.AddHours(1), Slot.AddHours(-2)),
            Row("NEVER_COMPLETED_IN", null, Slot.AddHours(-5)),
            Row("NEVER_COMPLETED_AT_FROM", null, from),
            Row("NEVER_COMPLETED_AT_TO", null, to),
            Row("NEVER_COMPLETED_BEFORE", null, from.AddTicks(-1))
        ];
        using ServiceProvider provider = Build(new FakeMail(), db =>
        {
            Seed(db);
            db.WorkflowStepResults.AddRange(rows);
        });

        OperatorDigestData digest = await OperatorFailureDigestService.BuildAsync(Db(provider), from, to, 50, CancellationToken.None);

        string[] expected = rows
            .Where(r => (r.CompletedAt ?? r.ExecutedAt) >= from && (r.CompletedAt ?? r.ExecutedAt) < to)
            .Select(r => r.FailureCode!).Order().ToArray();
        digest.Groups.Select(g => g.Code).Order().Should().Equal(expected, "the same rows as COALESCE(completed_at, executed_at) in [from, to)");
        expected.Should().BeEquivalentTo("COMPLETED_IN", "COMPLETED_AT_FROM", "COMPLETED_JUST_BEFORE_TO", "NEVER_COMPLETED_IN", "NEVER_COMPLETED_AT_FROM");
    }

    [Fact]
    public async Task The_attempt_count_lives_in_the_store_so_a_restart_or_a_second_replica_does_not_reset_it_and_a_timeout_is_retried()
    {
        // No status at all: the relay was never reached (timeout, connection refused). Transient, so retried.
        var mail = new FakeMail { Succeed = false, Status = null };
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.Add(Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, 10, "x"));
        });

        // A brand-new service (a restart, or another replica) for every check: only the database row carries the count.
        (await Service(provider, Enabled()).CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.SendFailed);
        (await Service(provider, Enabled()).CheckOnceAsync(Now.AddMinutes(15))).Should().Be(FailureDigestOutcome.SendFailed);
        (await Service(provider, Enabled()).CheckOnceAsync(Now.AddMinutes(30))).Should().Be(FailureDigestOutcome.SendFailed);
        (await Service(provider, Enabled()).CheckOnceAsync(Now.AddMinutes(45))).Should().Be(FailureDigestOutcome.AlreadyClaimed);
        (await Service(provider, Enabled()).CheckOnceAsync(Now.AddHours(5))).Should().Be(FailureDigestOutcome.AlreadyClaimed);

        mail.Sent.Should().HaveCount(OperatorFailureDigestService.MaxAttemptsPerDay);

        using IServiceScope scope = provider.CreateScope();
        InferenceApiDbContext db2 = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        db2.MailRateCounters.Single(c => c.Bucket == OperatorFailureDigestService.AttemptBucket).Count.Should().Be(OperatorFailureDigestService.MaxAttemptsPerDay);
    }

    [Fact]
    public void The_agent_output_preview_is_cut_off_before_the_excerpt()
    {
        OperatorFailureDigestService.CutOutputPreview("Agent X reported failure. Output preview: the model said hello").Should().Be("Agent X reported failure. ");
        OperatorFailureDigestService.CutOutputPreview("agent y failed. OUTPUT PREVIEW: more").Should().Be("agent y failed. ");
        OperatorFailureDigestService.CutOutputPreview("plain").Should().Be("plain");
        OperatorFailureDigestService.CutOutputPreview(null).Should().BeNull();
    }

    [Fact]
    public async Task A_classified_failure_loses_the_output_preview_and_an_unclassified_one_loses_all_text()
    {
        var mail = new FakeMail();
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.AddRange(
                Failed(FailureClass.PlatformInternal, "AGENT_FAILED", OrgA, 10, "Agent X reported failure. Output preview: PRIVATE-MODEL-WORDS"),
                Failed(FailureClass.None, "AGENT_NONE", OrgA, 20, "Agent X reported failure. Output preview: OTHER-MODEL-WORDS"));
        });

        (await Service(provider, Enabled()).CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.Sent);

        OperatorDigestData digest = mail.Sent.Single().Data!.Digest!;
        digest.Failures.Single(f => f.Code == "AGENT_FAILED").Error.Should().Be("Agent X reported failure.");
        digest.Failures.Single(f => f.Code == "AGENT_NONE").Error.Should().BeEmpty();
        digest.Groups.Should().Contain(g => g.Code == "AGENT_NONE" && g.Count == 1, "the counts stay");
        System.Text.Json.JsonSerializer.Serialize(digest).Should().NotContain("MODEL-WORDS");
    }

    [Fact]
    public async Task Two_hundred_non_latin_rows_still_make_a_body_the_relay_accepts_and_the_rest_is_counted()
    {
        var mail = new FakeMail();
        string cjk = new string('文', 400);
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            for (int i = 0; i < 200; i++)
            {
                db.WorkflowStepResults.Add(Failed(FailureClass.PlatformInternal, "STEP_INTERNAL_ERROR", OrgA, i + 1, cjk));
            }

            for (int i = 0; i < 200; i++)
            {
                db.FeedbackReports.Add(new FeedbackReport { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OrganizationId = OrgA, Kind = FeedbackReport.BugKind, Message = cjk, CreatedAt = Slot.AddMinutes(-i - 1) });
            }
        });
        var service = Service(provider, Enabled() with { MaxRows = 200 });

        (await service.CheckOnceAsync(Now)).Should().Be(FailureDigestOutcome.Sent);

        OperatorDigestData digest = mail.Sent.Single().Data!.Digest!;
        byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { to = "x", template = "operator_digest", data = new { digest }, locale = "en" }, OperatorFailureDigestService.WireOptions);
        body.Length.Should().BeLessThan(64 * 1024, "the relay reads at most 64 KB");
        digest.Failures.Count.Should().BeLessThanOrEqualTo(OperatorFailureDigestService.RelayMaxRows).And.BeGreaterThan(0);
        (digest.Failures.Count + digest.FailuresOmitted).Should().Be(200, "every failure is either listed or counted as left out");
        digest.Feedback.Count.Should().BeLessThanOrEqualTo(OperatorFailureDigestService.RelayMaxRows);
        digest.FeedbackCount.Should().Be(200);
    }

    [Fact]
    public async Task The_window_runs_from_the_previous_slot_to_this_slot_whenever_the_check_happens()
    {
        var mail = new FakeMail();
        using ServiceProvider provider = Build(mail, db =>
        {
            Seed(db);
            db.WorkflowStepResults.AddRange(
                Failed(FailureClass.PlatformInternal, "BEFORE_SLOT", OrgA, 1, "in"),
                Failed(FailureClass.PlatformInternal, "FIRST_MINUTE", OrgA, 24 * 60, "in: exactly the window start"),
                Failed(FailureClass.PlatformInternal, "TOO_OLD", OrgA, 24 * 60 + 1, "out"),
                Failed(FailureClass.PlatformInternal, "AFTER_SLOT", OrgA, -30, "out: belongs to tomorrow's digest"));
        });

        // A late retry at 13:45 reads the same span as a check at 06:05 would.
        (await Service(provider, Enabled()).CheckOnceAsync(Now.AddHours(6).AddMinutes(45))).Should().Be(FailureDigestOutcome.Sent);

        mail.Sent.Single().Data!.Digest!.Groups.Select(g => g.Code).Should().BeEquivalentTo("BEFORE_SLOT", "FIRST_MINUTE");
        mail.Sent.Single().Data!.Digest!.WindowHours.Should().Be(24);
    }

    // ---- fixtures ----

    private static void Seed(InferenceApiDbContext db)
    {
        db.WorkflowSteps.Add(new WorkflowStep { Id = StepId, StepType = StepType.VideoCompile });
    }

    private static WorkflowStepResult Failed(FailureClass cls, string code, Guid org, int minutesAgo, string error, StepStatus status = StepStatus.Failed)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkflowStepId = StepId,
            WorkflowExecutionId = Guid.NewGuid(),
            WorkflowExecution = new WorkflowExecution { Id = Guid.NewGuid(), OrganizationId = org, Status = ExecutionStatus.Failed, CompletedAt = Slot.AddMinutes(-minutesAgo) },
            Status = status,
            FailureClass = cls,
            FailureCode = code,
            ErrorDetails = error,
            ExecutedAt = Slot.AddMinutes(-minutesAgo),
            CompletedAt = Slot.AddMinutes(-minutesAgo)
        };

    private static readonly System.Collections.Concurrent.ConcurrentBag<IServiceScope> LiveScopes = [];

    /// <summary>A context from a scope that stays referenced (and is disposed with its provider), so it cannot be collected mid-query.</summary>
    private static InferenceApiDbContext Db(ServiceProvider provider)
    {
        IServiceScope scope = provider.CreateScope();
        LiveScopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
    }

    private static ServiceProvider Build(FakeMail mail, Action<InferenceApiDbContext>? seed = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        string dbName = Guid.NewGuid().ToString();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(mail);
        services.AddSingleton<IAccountMailClient>(mail);
        services.AddSingleton<FakeCounters>();
        services.AddScoped<IMailRateCounterStore>(sp => new DbBackedMarkers(sp.GetRequiredService<InferenceApiDbContext>(), sp.GetRequiredService<FakeCounters>()));
        ServiceProvider provider = services.BuildServiceProvider();
        if (seed is not null)
        {
            using IServiceScope seedScope = provider.CreateScope();
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            seed(db);
            db.SaveChanges();
        }

        return provider;
    }

    private static OperatorFailureDigestService Service(ServiceProvider provider, FailureDigestOptions options)
        => new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<OperatorFailureDigestService>.Instance);

    private sealed class FakeMail : IAccountMailClient
    {
        public List<AccountMail> Sent { get; } = [];

        public bool Succeed { get; set; } = true;

        public int? Status { get; set; }

        public Task<AccountMailResult> SendWithStatusAsync(AccountMail mail, CancellationToken ct)
            => SendAsync(mail, ct).ContinueWith(t => new AccountMailResult(t.Result, Status), ct);

        public Task<bool> SendAsync(AccountMail mail, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(mail);
            }

            return Task.FromResult(Succeed);
        }
    }

    private sealed class FakeCounters
    {
        public int Calls;
    }

    /// <summary>The "insert if absent" marker semantics of <c>TryMarkOnceAsync</c>, over the same database the service releases it from.</summary>
    private sealed class DbBackedMarkers(InferenceApiDbContext db, FakeCounters counters) : IMailRateCounterStore
    {
        private static readonly object Gate = new();

        public Task<bool> TryMarkOnceAsync(string bucket, DateTime windowStart, CancellationToken ct)
        {
            Interlocked.Increment(ref counters.Calls);
            lock (Gate)
            {
                DateTime utc = DateTime.SpecifyKind(windowStart, DateTimeKind.Utc);
                if (db.MailRateCounters.Any(c => c.Bucket == bucket && c.WindowStart == utc))
                {
                    return Task.FromResult(false);
                }

                db.MailRateCounters.Add(new MailRateCounter { Bucket = bucket, WindowStart = utc, Count = 1, UpdatedAt = DateTime.UtcNow });
                db.SaveChanges();
                return Task.FromResult(true);
            }
        }

        public Task<int> IncrementAsync(string bucket, DateTime windowStart, CancellationToken ct)
        {
            lock (Gate)
            {
                DateTime utc = DateTime.SpecifyKind(windowStart, DateTimeKind.Utc);
                MailRateCounter? row = db.MailRateCounters.FirstOrDefault(c => c.Bucket == bucket && c.WindowStart == utc);
                if (row is null)
                {
                    row = new MailRateCounter { Bucket = bucket, WindowStart = utc, Count = 0, UpdatedAt = DateTime.UtcNow };
                    db.MailRateCounters.Add(row);
                }

                row.Count++;
                db.SaveChanges();
                return Task.FromResult(row.Count);
            }
        }

        public Task<MailRateDecision> DecideAsync(
            IReadOnlyList<MailRateLimit> limits, DateTimeOffset now, Func<IReadOnlyList<MailRateEstimate>, bool> allow, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
