using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>
/// product-events-funnel T-002: the ingest endpoint now persists <c>product_events</c> rows as well as
/// moving the OTLP counter. Three rules are pinned here:
/// <list type="bullet">
/// <item>a client funnel name goes through <c>ClaimAndAppendAsync</c> with <c>source = 'client'</c>, and
/// the counter moves only for the call that newly claimed the event;</item>
/// <item>a consent-decision name goes through <c>AppendAsync</c> with <c>source = 'consent'</c>, is
/// recorded whether granted or declined, and moves no counter;</item>
/// <item>validation stays all-or-nothing: one unknown name writes nothing at all (Review Focus 5).</item>
/// </list>
/// </summary>
public class TelemetryControllerTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Org = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>A log that keeps what it was asked to write, so the controller's routing is observable.</summary>
    private sealed class FakeLog : IProductEventLog
    {
        private readonly HashSet<(Guid UserId, string Event)> _claimed = [];

        public List<ProductEvent> Appended { get; } = [];
        public List<(Guid UserId, Guid? OrganizationId, string Event, string Source)> ClaimCalls { get; } = [];

        public Task AppendAsync(ProductEvent entry, CancellationToken ct)
        {
            Appended.Add(entry);
            return Task.CompletedTask;
        }

        public Task<bool> ClaimAndAppendAsync(Guid userId, Guid? organizationId, string eventName, string source, CancellationToken ct)
        {
            ClaimCalls.Add((userId, organizationId, eventName, source));
            if (!_claimed.Add((userId, eventName))) return Task.FromResult(false);
            Appended.Add(new ProductEvent
            {
                UserId = userId,
                OrganizationId = organizationId,
                Event = eventName,
                OccurredAt = DateTimeOffset.UtcNow,
                Source = source,
            });
            return Task.FromResult(true);
        }

        public Task<bool> ClaimAndAppendWithDetailAsync(
            Guid userId, Guid? organizationId, string eventName, string claimKey, string? detail, string source, CancellationToken ct)
        {
            ClaimCalls.Add((userId, organizationId, claimKey, source));
            if (!_claimed.Add((userId, claimKey))) return Task.FromResult(false);
            Appended.Add(new ProductEvent
            {
                UserId = userId,
                OrganizationId = organizationId,
                Event = eventName,
                OccurredAt = DateTimeOffset.UtcNow,
                Source = source,
                Detail = detail,
            });
            return Task.FromResult(true);
        }

        // The client ingest never makes the payment step's organization-activation claim: this double
        // does not model it, and returns false rather than pretending to have written a row.
        public Task<bool> ClaimAndAppendFirstPaymentAsync(Guid organizationId, Guid? userId, CancellationToken ct)
            => Task.FromResult(false);
    }

    private sealed class FakeUser(Guid userId, Guid? organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => OrganizationRole.Member;
    }

    private static TelemetryController Controller(IProductEventLog log, bool enabled = true) => new(
        new FakeUser(User, Org),
        Options.Create(new AlphaTelemetryOptions { ClientIngestEnabled = enabled }),
        // Only the errors action reads configuration, and these tests exercise the events action.
        new ConfigurationBuilder().Build(),
        log);

    private static ClientEventsDto Batch(params string[] names) =>
        new() { Events = names.Select(n => new ClientFunnelEventDto { Name = n }).ToList() };

    /// <summary>Counts the funnel counter's measurements while <paramref name="action"/> runs.</summary>
    private static long CountFunnelEvents(Action action)
    {
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AlphaFunnelDiagnostics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => total += measurement);
        listener.Start();
        action();
        return total;
    }

    [Fact]
    public async Task A_client_batch_writes_one_client_row_per_event()
    {
        var log = new FakeLog();

        IActionResult result = await Controller(log).Events(
            Batch("alpha.first_project", "alpha.first_run"), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        log.Appended.Should().HaveCount(2);
        log.Appended.Should().OnlyContain(e => e.Source == ProductEventSources.Client);
        log.Appended.Select(e => e.Event).Should().BeEquivalentTo("alpha.first_project", "alpha.first_run");
        log.Appended.Should().OnlyContain(e => e.UserId == User && e.OrganizationId == Org,
            "organization_id comes from ICurrentUser");
    }

    [Fact]
    public async Task A_repeat_client_batch_writes_no_second_row()
    {
        var log = new FakeLog();
        TelemetryController controller = Controller(log);

        await controller.Events(Batch("alpha.first_project"), CancellationToken.None);
        await controller.Events(Batch("alpha.first_project"), CancellationToken.None);

        log.Appended.Should().HaveCount(1, "the durable claim, not the caller, decides what is written");
    }

    [Fact]
    public void The_counter_moves_only_for_a_newly_won_claim()
    {
        var log = new FakeLog();
        TelemetryController controller = Controller(log);

        long counted = CountFunnelEvents(() =>
        {
            controller.Events(Batch("alpha.first_project"), CancellationToken.None).GetAwaiter().GetResult();
            controller.Events(Batch("alpha.first_project"), CancellationToken.None).GetAwaiter().GetResult();
            controller.Events(Batch("alpha.first_project"), CancellationToken.None).GetAwaiter().GetResult();
        });

        counted.Should().Be(1);
    }

    [Fact]
    public async Task A_consent_decline_writes_one_consent_row_and_no_client_rows()
    {
        var log = new FakeLog();

        IActionResult result = await Controller(log).Events(
            Batch("telemetry.consent.declined"), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        log.ClaimCalls.Should().BeEmpty("a consent decision is not a funnel claim and moves no counter");
        log.Appended.Should().ContainSingle();
        ProductEvent row = log.Appended[0];
        row.Source.Should().Be(ProductEventSources.Consent);
        row.Event.Should().Be("telemetry.consent.declined");
        row.UserId.Should().Be(User, "the choice is a fact about the person");
        row.OrganizationId.Should().BeNull("a consent row is per-person by design, not an organizational fact");
    }

    [Fact]
    public async Task A_consent_grant_writes_one_consent_row()
    {
        var log = new FakeLog();

        await Controller(log).Events(Batch("telemetry.consent.granted"), CancellationToken.None);

        log.Appended.Should().ContainSingle();
        log.Appended[0].Source.Should().Be(ProductEventSources.Consent);
        log.Appended[0].Event.Should().Be("telemetry.consent.granted");
        log.Appended[0].UserId.Should().Be(User);
        log.Appended[0].OrganizationId.Should().BeNull("a consent row is per-person by design");
        log.ClaimCalls.Should().BeEmpty();
    }

    [Fact]
    public void A_consent_decision_moves_no_counter()
    {
        var log = new FakeLog();

        long counted = CountFunnelEvents(() =>
            Controller(log).Events(Batch("telemetry.consent.declined"), CancellationToken.None).GetAwaiter().GetResult());

        counted.Should().Be(0, "recording the choice is not a funnel step");
    }

    [Fact]
    public async Task A_batch_with_an_unknown_name_writes_nothing_at_all()
    {
        var log = new FakeLog();

        IActionResult result = await Controller(log).Events(
            Batch("alpha.first_project", "alpha.made_up"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        log.Appended.Should().BeEmpty("one bad entry must not half-write the batch");
        log.ClaimCalls.Should().BeEmpty();
    }

    private static ClientEventsDto Detailed(string name, string? detail) =>
        new() { Events = [new ClientFunnelEventDto { Name = name, Detail = detail }] };

    [Fact]
    public async Task Run_events_are_accepted_and_run_blocked_is_deduped_per_detail()
    {
        var log = new FakeLog();
        var c = Controller(log);
        (await c.Events(Batch("alpha.run_page_opened", "alpha.run_clicked"), CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
        foreach (string d in AlphaFunnelDiagnostics.RunBlockedDetails)
            (await c.Events(Detailed("alpha.run_blocked", d), CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await c.Events(Detailed("alpha.run_blocked", "credits"), CancellationToken.None)).Should().BeOfType<NoContentResult>();

        log.Appended.Should().HaveCount(2 + AlphaFunnelDiagnostics.RunBlockedDetails.Length);
        log.Appended.Single(e => e.Detail == "credits").Source.Should().Be("client");
        log.Appended.Where(e => e.Event != "alpha.run_blocked").Should().OnlyContain(e => e.Detail == null);
    }

    [Theory]
    [InlineData("alpha.run_blocked", null)]
    [InlineData("alpha.run_blocked", "free text")]
    [InlineData("alpha.run_blocked", "")]
    [InlineData("alpha.run_clicked", "credits")]
    [InlineData("alpha.first_run", "other")]
    [InlineData("telemetry.consent.granted", "other")]
    public async Task Invalid_detail_combinations_are_refused_and_write_nothing(string name, string? detail)
    {
        var log = new FakeLog();
        (await Controller(log).Events(Detailed(name, detail), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        log.Appended.Should().BeEmpty();
    }

    [Fact]
    public async Task The_renamed_export_name_is_accepted_and_the_old_one_is_refused()
    {
        var renamed = new FakeLog();
        (await Controller(renamed).Events(Batch("alpha.first_export_initiated"), CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
        renamed.Appended.Should().ContainSingle(e => e.Event == "alpha.first_export_initiated");

        var old = new FakeLog();
        (await Controller(old).Events(Batch("alpha.first_export"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        old.Appended.Should().BeEmpty();
    }

    [Fact]
    public async Task Alpha_signup_cannot_come_from_the_browser()
    {
        // The signup endpoint answers a uniform 202 and the Go API observes the transition server-side,
        // so the browser ingest refuses the name rather than minting a `client` row for a server event.
        var log = new FakeLog();

        IActionResult result = await Controller(log).Events(Batch("alpha.signup"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        log.Appended.Should().BeEmpty();
        log.ClaimCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_endpoints_answer_404_until_the_deployment_opts_in()
    {
        var log = new FakeLog();

        (await Controller(log, enabled: false).Events(Batch("alpha.first_project"), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        log.Appended.Should().BeEmpty();
        log.ClaimCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("billing.upgrade_prompt_shown", "insufficient_credits_alert")]
    [InlineData("billing.upgrade_clicked", "plan_gate")]
    [InlineData("billing.upgrade_prompt_shown", "insufficient_credits_toast")]
    [InlineData("billing.upgrade_clicked", "pricing")]
    public async Task An_upgrade_event_with_a_closed_surface_is_recorded_as_a_client_row(string name, string surface)
    {
        var log = new RecordingProductEventLog();
        IActionResult result = await Controller(log).Events(
            new ClientEventsDto { Events = [new ClientFunnelEventDto { Name = name, Detail = surface }] }, CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
        ProductEvent row = log.Rows.Should().ContainSingle().Subject;
        row.Event.Should().Be(name);
        row.Detail.Should().Be(surface);
        row.Source.Should().Be(ProductEventSources.Client);
    }

    [Theory]
    [InlineData("billing.upgrade_clicked", "somewhere_else")]
    [InlineData("billing.upgrade_prompt_shown", null)]
    [InlineData("alpha.first_project", "pricing")]
    public async Task An_unknown_surface_or_a_detail_on_another_event_writes_nothing(string name, string? detail)
    {
        var log = new RecordingProductEventLog();
        IActionResult result = await Controller(log).Events(
            new ClientEventsDto { Events = [new ClientFunnelEventDto { Name = name, Detail = detail }] }, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
        log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_upgrade_event_repeats_once_per_surface_only()
    {
        var log = new RecordingProductEventLog();
        var dto = new ClientEventsDto
        {
            Events =
            [
                new ClientFunnelEventDto { Name = "billing.upgrade_clicked", Detail = "pricing" },
                new ClientFunnelEventDto { Name = "billing.upgrade_clicked", Detail = "pricing" },
                new ClientFunnelEventDto { Name = "billing.upgrade_clicked", Detail = "plan_gate" },
            ],
        };
        await Controller(log).Events(dto, CancellationToken.None);
        log.Rows.Select(r => r.Detail).Should().BeEquivalentTo(["pricing", "plan_gate"]);
    }
}
