#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Editor v2 Wave 2 (R09.1): analysis on demand per media file, its estimate, its credit gate and its digest cache.</summary>
public class MediaAnalysisApiTests
{
    [Fact]
    public async Task A_foreign_project_gets_404_on_every_analysis_route()
    {
        using Harness h = await Harness.CreateAsync();
        MediaAnalysisController stranger = h.Controller(userId: Guid.NewGuid());
        MediaAnalysisRequestBody body = new(["silence"], null);

        (await stranger.Start(h.ProjectId, h.MediaRef, body, default)).Should().BeOfType<NotFoundResult>();
        (await stranger.Estimate(h.ProjectId, h.MediaRef, body, default)).Should().BeOfType<NotFoundResult>();
        (await stranger.GetStates(h.ProjectId, h.MediaRef, default)).Should().BeOfType<NotFoundResult>();
        (await stranger.GetData(h.ProjectId, h.MediaRef, "silence", default)).Should().BeOfType<NotFoundResult>();
        h.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_projects_media_ref_is_404_even_for_the_owner()
    {
        using Harness h = await Harness.CreateAsync();
        string other = $"file:{Guid.NewGuid()}";
        (await h.Controller().GetStates(h.ProjectId, other, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().Start(h.ProjectId, other, new(["silence"], null), default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Free_kinds_queue_without_a_price_a_hold_or_a_charge_even_when_billing_is_on_and_the_balance_is_empty()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 0);
        MediaAnalysisStartedResponse started = Accepted(await h.Controller(billing: true).Start(
            h.ProjectId, h.MediaRef, new(["silence", "shots", "motion"], null), default));

        started.States.Keys.Should().BeEquivalentTo("silence", "shots", "motion");
        started.States.Values.Should().OnlyContain(s => s.State == "queued");
        h.Charges.Estimates.Should().BeEmpty("free kinds are never priced");
        h.Charges.Holds.Should().BeEmpty();
        h.Published.Should().ContainSingle().Which.Kinds.Should().BeEquivalentTo("silence", "shots", "motion");
    }

    [Fact]
    public async Task A_second_request_for_the_same_kind_reuses_the_queued_job()
    {
        using Harness h = await Harness.CreateAsync();
        Accepted(await h.Controller().Start(h.ProjectId, h.MediaRef, new(["silence"], null), default));
        Accepted(await h.Controller().Start(h.ProjectId, h.MediaRef, new(["silence", "shots"], null), default));

        h.Published.Should().HaveCount(2);
        h.Published[1].Kinds.Should().BeEquivalentTo(new[] { "shots" }, "silence is already queued");
    }

    [Fact]
    public async Task Bad_bodies_are_400_with_a_plain_message()
    {
        using Harness h = await Harness.CreateAsync();
        foreach (MediaAnalysisRequestBody? body in new MediaAnalysisRequestBody?[] { null, new([], null), new(["loudness"], null), new(["transcript"], "en;drop") })
        {
            BadRequestObjectResult bad = (await h.Controller().Start(h.ProjectId, h.MediaRef, body, default)).Should().BeOfType<BadRequestObjectResult>().Subject;
            JsonSerializer.Serialize(bad.Value).Should().Contain("message");
        }
    }

    [Fact]
    public async Task Estimate_prices_the_transcript_from_the_files_length_and_says_whether_it_is_affordable()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 3_000, estimateMillis: 4_000);
        MediaAnalysisEstimateResponse unaffordable = Estimate(await h.Controller(billing: true).Estimate(h.ProjectId, h.MediaRef, new(["transcript"], "en"), default));
        unaffordable.BillingEnabled.Should().BeTrue();
        unaffordable.Credits.Should().Be(4);
        unaffordable.Seconds.Should().Be(90);
        unaffordable.BalanceCredits.Should().Be(3);
        unaffordable.Affordable.Should().BeFalse();
        h.Charges.Estimates.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.AsrAudioSeconds);
        h.Charges.Estimates[0].Quantity.Should().Be(90m);

        using Harness rich = await Harness.CreateAsync(availableMillis: 10_000, estimateMillis: 4_000);
        Estimate(await rich.Controller(billing: true).Estimate(rich.ProjectId, rich.MediaRef, new(["transcript"], null), default)).Affordable.Should().BeTrue();
    }

    [Fact]
    public async Task Estimate_with_billing_off_or_only_free_kinds_costs_nothing()
    {
        using Harness h = await Harness.CreateAsync();
        MediaAnalysisEstimateResponse off = Estimate(await h.Controller(billing: false).Estimate(h.ProjectId, h.MediaRef, new(["transcript"], null), default));
        (off.BillingEnabled, off.Affordable).Should().Be((false, true));

        MediaAnalysisEstimateResponse free = Estimate(await h.Controller(billing: true).Estimate(h.ProjectId, h.MediaRef, new(["silence", "shots"], null), default));
        (free.Credits, free.Affordable).Should().Be((0d, true));
        h.Charges.Estimates.Should().BeEmpty();
    }

    [Fact]
    public async Task Transcript_without_the_credits_is_402_and_nothing_is_queued_not_even_the_free_kinds()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 1_000, estimateMillis: 4_000);
        ObjectResult refused = (await h.Controller(billing: true).Start(
            h.ProjectId, h.MediaRef, new(["silence", "transcript"], null), default)).Should().BeOfType<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(402);
        JsonSerializer.Serialize(refused.Value).Should().Contain("INSUFFICIENT_CREDITS");
        h.Published.Should().BeEmpty();
        (await h.Db.MediaAnalysisRequests.CountAsync()).Should().Be(0);
        h.Charges.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task An_affordable_transcript_takes_a_hold_and_a_successful_run_settles_it_at_what_was_metered()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 10_000, estimateMillis: 4_000);
        Accepted(await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new(["transcript"], "en"), default));
        h.Charges.Holds.Should().ContainSingle().Which.EstimateMillis.Should().Be(4_000);
        Guid requestId = h.Published.Single().RequestId;

        await h.FinishAsync(requestId, "abc", new MediaAnalysisKindOutcome(
            "transcript", true, TranscriptJson, """{"wordCount":2,"estimatedWordCount":1,"measuredWordCount":1,"language":"en"}""", null, 90, false, 3_000));

        h.Charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(3_000);
        h.Charges.Released.Should().BeEmpty();
        MediaAnalysisStatesResponse states = States(await h.Controller().GetStates(h.ProjectId, h.MediaRef, default));
        states.Kinds["transcript"].State.Should().Be("ready");
        states.Kinds["transcript"].Summary!.Value.GetProperty("wordCount").GetInt32().Should().Be(2);
        states.Kinds["silence"].State.Should().Be("none");
    }

    [Fact]
    public async Task A_second_transcript_start_while_one_is_in_flight_returns_it_and_takes_no_second_hold()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 10_000, estimateMillis: 4_000);
        MediaAnalysisStartedResponse first = Accepted(await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new(["transcript"], null), default));
        MediaAnalysisStartedResponse second = Accepted(await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new(["transcript"], null), default));

        second.States["transcript"].RequestId.Should().Be(first.States["transcript"].RequestId);
        h.Charges.Holds.Should().ContainSingle();
        h.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task A_unique_index_collision_on_finish_is_treated_as_cached_and_releases_the_hold()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 10_000, estimateMillis: 4_000);
        Accepted(await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new(["transcript"], null), default));
        Guid requestId = h.Published.Single().RequestId;
        await h.SeedResultAsync("abc");
        h.Interceptor.FailNextSave = true;
        h.Interceptor.Inner = new Npgsql.PostgresException("dup", "ERROR", "ERROR", "23505", constraintName: MediaAnalysisFinishedConsumer.ResultKeyConstraint);

        await h.FinishAsync(requestId, "abc", new MediaAnalysisKindOutcome(
            "transcript", true, TranscriptJson, "{}", null, 90, false, 3_000));

        h.Charges.Settled.Should().BeEmpty("the result was already cached by the concurrent run");
        h.Charges.Released.Should().ContainSingle();
        States(await h.Controller().GetStates(h.ProjectId, h.MediaRef, default)).Kinds["transcript"].State.Should().Be("ready");
    }

    [Fact]
    public async Task Any_other_database_error_on_finish_is_rethrown_and_the_hold_is_neither_released_nor_settled()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 10_000, estimateMillis: 4_000);
        Accepted(await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new(["transcript"], null), default));
        Guid requestId = h.Published.Single().RequestId;
        h.Interceptor.FailNextSave = true;
        h.Interceptor.Inner = new InvalidOperationException("connection reset");

        Func<Task> finish = () => h.FinishAsync(requestId, "abc", new MediaAnalysisKindOutcome(
            "transcript", true, TranscriptJson, "{}", null, 90, false, 3_000));

        await finish.Should().ThrowAsync<DbUpdateException>();
        h.Charges.Settled.Should().BeEmpty();
        h.Charges.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_transcript_releases_the_hold_and_the_reason_is_plain()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 10_000, estimateMillis: 4_000);
        Accepted(await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new(["transcript"], null), default));
        Guid requestId = h.Published.Single().RequestId;

        await h.FinishAsync(requestId, "abc", new MediaAnalysisKindOutcome(
            "transcript", false, null, null, "The transcription service couldn't process this file. You were not charged.", null, false));

        h.Charges.Settled.Should().BeEmpty();
        h.Charges.Released.Should().ContainSingle();
        MediaAnalysisKindState state = States(await h.Controller().GetStates(h.ProjectId, h.MediaRef, default)).Kinds["transcript"];
        state.State.Should().Be("failed");
        state.Reason.Should().Be("The transcription service couldn't process this file. You were not charged.");
        ObjectResult data = (await h.Controller().GetData(h.ProjectId, h.MediaRef, "transcript", default)).Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(data.Value).Should().Contain("failed");
    }

    [Fact]
    public async Task A_digest_cache_hit_is_ready_with_no_charge_and_the_same_bytes_in_another_project_share_one_result()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 10_000, estimateMillis: 4_000);
        Accepted(await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new(["transcript"], null), default));
        await h.FinishAsync(h.Published[0].RequestId, "samebytes", new MediaAnalysisKindOutcome(
            "transcript", true, TranscriptJson, "{}", null, 90, false, 2_000));

        // Same organization, a second project holding the same bytes: the engine answers from the cache.
        (Guid secondProject, string secondRef) = await h.AddSecondProjectAsync();
        Accepted(await h.Controller(billing: true).Start(secondProject, secondRef, new(["transcript"], null), default));
        h.Charges.Settled.Clear();
        h.Charges.Released.Clear();
        await h.FinishAsync(h.Published[1].RequestId, "samebytes", new MediaAnalysisKindOutcome(
            "transcript", true, TranscriptJson, "{}", null, 90, true, null), secondProject);

        (await h.Db.MediaAnalysisResults.CountAsync()).Should().Be(1, "one result per (organization, digest, kind, language)");
        h.Charges.Settled.Should().BeEmpty("a cache hit is not charged");
        h.Charges.Released.Should().ContainSingle("the up-front hold is given back");
        States(await h.Controller().GetStates(secondProject, secondRef, default)).Kinds["transcript"].State.Should().Be("ready");

        // Asking again for a ready kind answers ready at once and publishes nothing new.
        MediaAnalysisStartedResponse again = Accepted(await h.Controller(billing: true).Start(h.ProjectId, h.MediaRef, new(["transcript"], null), default));
        again.States["transcript"].State.Should().Be("ready");
        h.Published.Should().HaveCount(2);
        h.Charges.Holds.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_data_route_returns_the_stored_json_when_ready_and_409_before()
    {
        using Harness h = await Harness.CreateAsync();
        Accepted(await h.Controller().Start(h.ProjectId, h.MediaRef, new(["silence"], null), default));
        (await h.Controller().GetData(h.ProjectId, h.MediaRef, "silence", default)).Should().BeOfType<ConflictObjectResult>();
        (await h.Controller().GetData(h.ProjectId, h.MediaRef, "bogus", default)).Should().BeOfType<NotFoundResult>();

        await h.FinishAsync(h.Published[0].RequestId, "d1", new MediaAnalysisKindOutcome(
            "silence", true, """{"durationSec":90,"ranges":[{"startSec":1,"endSec":2}],"truncated":false}""", """{"rangeCount":1,"totalSeconds":1}""", null, null, false));
        ContentResult ok = (await h.Controller().GetData(h.ProjectId, h.MediaRef, "silence", default)).Should().BeOfType<ContentResult>().Subject;
        ok.ContentType.Should().Be("application/json");
        ok.Content.Should().Contain("\"ranges\"").And.NotContain("projects/");
    }

    [Fact]
    public async Task The_events_kind_is_free_queues_estimates_zero_and_serves_its_list_after_the_409_and_404_states()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 0);
        MediaAnalysisController c = h.Controller(billing: true);
        MediaAnalysisEstimateResponse est = Estimate(await c.Estimate(h.ProjectId, h.MediaRef, new(["events"], null), default));
        (est.Credits, est.Affordable).Should().Be((0d, true));
        h.Charges.Estimates.Should().BeEmpty("events is never priced");

        MediaAnalysisStartedResponse started = Accepted(await c.Start(h.ProjectId, h.MediaRef, new(["events"], null), default));
        started.States["events"].State.Should().Be("queued");
        h.Charges.Holds.Should().BeEmpty();
        (await c.GetData(h.ProjectId, h.MediaRef, "events", default)).Should().BeOfType<ConflictObjectResult>();
        (await h.Controller(userId: Guid.NewGuid()).GetData(h.ProjectId, h.MediaRef, "events", default)).Should().BeOfType<NotFoundResult>();

        await h.FinishAsync(h.Published[0].RequestId, "d-ev", new MediaAnalysisKindOutcome("events", true, EventsJson(CurrentEvents), EventsSummary(CurrentEvents), null, null, false));
        ContentResult ok = (await c.GetData(h.ProjectId, h.MediaRef, "events", default)).Should().BeOfType<ContentResult>().Subject;
        ok.Content.Should().Contain("\"events\"").And.Contain("\"timeSec\"");
    }

    private static readonly string CurrentEvents = $",\"version\":\"{MediaAnalysisJson.EventsVersion}\"";

    private static string EventsJson(string version, int frame = 12) =>
        $$"""{"fps":30,"frameCount":90,"events":[{"id":"e0","type":"appear","frame":{{frame}},"timeSec":0.4,"strength":0.8,"region":{"x":0.1,"y":0.1,"w":0.2,"h":0.2},"confidence":0.9}],"suppressed":0,"truncated":false{{version}}}""";

    private static string EventsSummary(string version) => $$"""{"eventCount":1,"byType":{"appear":1},"suppressed":0{{version}}}""";

    [Fact]
    public async Task An_events_result_of_another_detector_version_stays_readable_is_marked_outdated_and_the_next_run_replaces_it()
    {
        using Harness h = await Harness.CreateAsync();
        MediaAnalysisController c = h.Controller();

        // A file analysed before results carried a version: the Events lane still gets what it showed, and the state
        // says the result is from an earlier detector so the editor can offer to find the events again.
        Accepted(await c.Start(h.ProjectId, h.MediaRef, new(["events"], null), default));
        await h.FinishAsync(h.Published[0].RequestId, "d-old", new MediaAnalysisKindOutcome("events", true, EventsJson(""), EventsSummary(""), null, null, false));
        MediaAnalysisKindState old = States(await c.GetStates(h.ProjectId, h.MediaRef, default)).Kinds["events"];
        old.State.Should().Be("ready");
        old.Outdated.Should().BeTrue();
        (await c.GetData(h.ProjectId, h.MediaRef, "events", default)).Should().BeOfType<ContentResult>().Which.Content.Should().Contain("\"frame\":12");
        Guid storedId = (await h.Db.MediaAnalysisResults.SingleAsync(r => r.Kind == "events")).Id;

        // The next request does not reuse it: the kind is queued again.
        MediaAnalysisStartedResponse again = Accepted(await c.Start(h.ProjectId, h.MediaRef, new(["events"], null), default));
        again.States["events"].State.Should().Be("queued");
        h.Published.Should().HaveCount(2);

        // The fresh result replaces the old row in place (the cache key has no version, so there is one row).
        await h.FinishAsync(h.Published[1].RequestId, "d-old", new MediaAnalysisKindOutcome("events", true, EventsJson(CurrentEvents, frame: 15), EventsSummary(CurrentEvents), null, null, false));
        MediaAnalysisResult stored = await h.Db.MediaAnalysisResults.SingleAsync(r => r.Kind == "events");
        stored.Id.Should().Be(storedId);
        stored.ResultJson.Should().Contain("\"frame\":15").And.Contain(MediaAnalysisJson.EventsVersion);
        MediaAnalysisKindState fresh = States(await c.GetStates(h.ProjectId, h.MediaRef, default)).Kinds["events"];
        fresh.State.Should().Be("ready");
        fresh.Outdated.Should().BeNull("a current result is not marked");
        System.Text.Json.JsonSerializer.Serialize(fresh, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            .Should().NotContain("outdated", "the field is left out of the JSON unless it is set");
        (await c.GetData(h.ProjectId, h.MediaRef, "events", default)).Should().BeOfType<ContentResult>().Which.Content.Should().Contain("\"frame\":15");

        // A current result is reused.
        Accepted(await c.Start(h.ProjectId, h.MediaRef, new(["events"], null), default));
        h.Published.Should().HaveCount(2, "a current result answers the request");
    }

    private static readonly string DegradedEvents = CurrentEvents + ",\"degraded\":true";

    [Fact]
    public async Task A_degraded_events_result_is_shown_but_reads_outdated_and_the_next_run_replaces_it()
    {
        using Harness h = await Harness.CreateAsync();
        MediaAnalysisController c = h.Controller();

        // A part of the detection failed: the marks it found are served, and the state says outdated so
        // "Find events" is offered again.
        Accepted(await c.Start(h.ProjectId, h.MediaRef, new(["events"], null), default));
        await h.FinishAsync(h.Published[0].RequestId, "d-deg", new MediaAnalysisKindOutcome("events", true, EventsJson(DegradedEvents), EventsSummary(DegradedEvents), null, null, false));
        MediaAnalysisKindState degraded = States(await c.GetStates(h.ProjectId, h.MediaRef, default)).Kinds["events"];
        (degraded.State, degraded.Outdated).Should().Be(("ready", true));
        (await c.GetData(h.ProjectId, h.MediaRef, "events", default)).Should().BeOfType<ContentResult>().Which.Content.Should().Contain("\"frame\":12");
        Guid storedId = (await h.Db.MediaAnalysisResults.SingleAsync(r => r.Kind == "events")).Id;

        // It never answers a new request: the kind is queued again.
        Accepted(await c.Start(h.ProjectId, h.MediaRef, new(["events"], null), default)).States["events"].State.Should().Be("queued");
        h.Published.Should().HaveCount(2);

        // A second degraded run replaces the row and is still outdated.
        await h.FinishAsync(h.Published[1].RequestId, "d-deg", new MediaAnalysisKindOutcome("events", true, EventsJson(DegradedEvents, frame: 14), EventsSummary(DegradedEvents), null, null, false));
        MediaAnalysisResult second = await h.Db.MediaAnalysisResults.SingleAsync(r => r.Kind == "events");
        second.Id.Should().Be(storedId);
        second.ResultJson.Should().Contain("\"frame\":14");
        States(await c.GetStates(h.ProjectId, h.MediaRef, default)).Kinds["events"].Outdated.Should().BeTrue();

        // A complete run replaces it in place and is the lasting answer.
        Accepted(await c.Start(h.ProjectId, h.MediaRef, new(["events"], null), default));
        h.Published.Should().HaveCount(3);
        await h.FinishAsync(h.Published[2].RequestId, "d-deg", new MediaAnalysisKindOutcome("events", true, EventsJson(CurrentEvents, frame: 15), EventsSummary(CurrentEvents), null, null, false));
        MediaAnalysisResult stored = await h.Db.MediaAnalysisResults.SingleAsync(r => r.Kind == "events");
        stored.Id.Should().Be(storedId);
        stored.ResultJson.Should().Contain("\"frame\":15").And.NotContain("degraded");
        MediaAnalysisKindState fresh = States(await c.GetStates(h.ProjectId, h.MediaRef, default)).Kinds["events"];
        (fresh.State, fresh.Outdated).Should().Be(("ready", null));
        Accepted(await c.Start(h.ProjectId, h.MediaRef, new(["events"], null), default));
        h.Published.Should().HaveCount(3, "a complete result answers the request");

        // A degraded run never replaces a complete result.
        await h.FinishAsync(h.Published[2].RequestId, "d-deg", new MediaAnalysisKindOutcome("events", true, EventsJson(DegradedEvents, frame: 16), EventsSummary(DegradedEvents), null, null, false));
        (await h.Db.MediaAnalysisResults.SingleAsync(r => r.Kind == "events")).ResultJson.Should().Contain("\"frame\":15");
    }

    [Fact]
    public async Task A_run_the_engine_never_answered_is_reported_failed_in_plain_words()
    {
        using Harness h = await Harness.CreateAsync();
        Accepted(await h.Controller().Start(h.ProjectId, h.MediaRef, new(["shots"], null), default));
        MediaAnalysisRequest row = await h.Db.MediaAnalysisRequests.SingleAsync();
        row.UpdatedAt = DateTime.UtcNow - MediaAnalysisController.StaleAfter - TimeSpan.FromMinutes(1);
        await h.Db.SaveChangesAsync();

        MediaAnalysisKindState state = States(await h.Controller().GetStates(h.ProjectId, h.MediaRef, default)).Kinds["shots"];
        state.State.Should().Be("failed");
        state.Reason.Should().Be("The analysis took too long and was stopped. Try again.");
    }

    [Fact]
    public async Task Beats_queues_free_without_a_price_a_hold_or_a_charge_even_with_billing_on_and_an_empty_balance()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 0);
        MediaAnalysisStartedResponse started = Accepted(await h.Controller(billing: true).Start(
            h.ProjectId, h.MediaRef, new(["beats"], null), default));

        started.States["beats"].State.Should().Be("queued");
        h.Charges.Estimates.Should().BeEmpty("beats is never priced");
        h.Charges.Holds.Should().BeEmpty();
        h.Published.Should().ContainSingle().Which.Kinds.Should().Equal("beats");
    }

    [Fact]
    public async Task Beats_estimate_is_zero_and_free_even_with_billing_on()
    {
        using Harness h = await Harness.CreateAsync(availableMillis: 0);
        MediaAnalysisEstimateResponse free = Estimate(await h.Controller(billing: true).Estimate(h.ProjectId, h.MediaRef, new(["beats"], null), default));
        (free.Credits, free.Affordable).Should().Be((0d, true));
        h.Charges.Estimates.Should().BeEmpty();
    }

    [Fact]
    public async Task Beats_on_a_foreign_media_ref_is_404_on_every_route()
    {
        using Harness h = await Harness.CreateAsync();
        string other = $"file:{Guid.NewGuid()}";
        MediaAnalysisRequestBody body = new(["beats"], null);
        (await h.Controller().Start(h.ProjectId, other, body, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().Estimate(h.ProjectId, other, body, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().GetData(h.ProjectId, other, "beats", default)).Should().BeOfType<NotFoundResult>();
        h.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Beats_for_the_same_org_and_digest_reuse_the_cached_result()
    {
        using Harness h = await Harness.CreateAsync();
        const string json = """{"bpm":120.0,"beatSec":0.5,"beatPhaseSec":0.1,"barPhaseSec":0.1,"beatsPerBar":4,"beatTimesSec":[0.1,0.6],"firstDownbeatIndex":0,"durationSec":60,"analysedSec":60,"dropSec":null,"outroSec":null,"confidence":0.8,"steady":true,"tempoWord":"fast","halfTempoScore":0.1,"doubleTempoScore":0.2,"truncated":false}""";
        Accepted(await h.Controller().Start(h.ProjectId, h.MediaRef, new(["beats"], null), default));
        await h.FinishAsync(h.Published[0].RequestId, "beatbytes", new MediaAnalysisKindOutcome(
            "beats", true, json, """{"bpm":120}""", null, null, false));

        (Guid secondProject, string secondRef) = await h.AddSecondProjectAsync();
        Accepted(await h.Controller().Start(secondProject, secondRef, new(["beats"], null), default));
        await h.FinishAsync(h.Published[1].RequestId, "beatbytes", new MediaAnalysisKindOutcome(
            "beats", true, json, """{"bpm":120}""", null, null, true), secondProject);

        (await h.Db.MediaAnalysisResults.CountAsync()).Should().Be(1, "one result per (organization, digest, kind)");
        States(await h.Controller().GetStates(secondProject, secondRef, default)).Kinds["beats"].State.Should().Be("ready");
        Accepted(await h.Controller().Start(h.ProjectId, h.MediaRef, new(["beats"], null), default)).States["beats"].State.Should().Be("ready");
        h.Published.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_beats_data_route_returns_the_stored_json_verbatim_when_ready()
    {
        using Harness h = await Harness.CreateAsync();
        Accepted(await h.Controller().Start(h.ProjectId, h.MediaRef, new(["beats"], null), default));
        (await h.Controller().GetData(h.ProjectId, h.MediaRef, "beats", default)).Should().BeOfType<ConflictObjectResult>();

        const string json = """{"bpm":120.0,"beatSec":0.5,"beatPhaseSec":0.1,"barPhaseSec":0.1,"beatsPerBar":4,"beatTimesSec":[0.1,0.6],"firstDownbeatIndex":0,"durationSec":60,"analysedSec":60,"dropSec":null,"outroSec":null,"confidence":0.8,"steady":true,"tempoWord":"fast","halfTempoScore":0.1,"doubleTempoScore":0.2,"truncated":false}""";
        await h.FinishAsync(h.Published[0].RequestId, "beats1", new MediaAnalysisKindOutcome(
            "beats", true, json, """{"bpm":120,"confidence":0.8,"beatCount":2,"tempoWord":"fast"}""", null, null, false));

        ContentResult ok = (await h.Controller().GetData(h.ProjectId, h.MediaRef, "beats", default)).Should().BeOfType<ContentResult>().Subject;
        ok.ContentType.Should().Be("application/json");
        ok.Content.Should().Be(json);
        States(await h.Controller().GetStates(h.ProjectId, h.MediaRef, default)).Kinds["beats"].State.Should().Be("ready");
    }

    private const string TranscriptJson = """{"language":"en","words":[{"text":"hi","startSec":0.1,"endSec":0.4,"estimated":false},{"text":"there","startSec":0.5,"endSec":0.9,"estimated":true}],"truncated":false}""";

    private static MediaAnalysisStartedResponse Accepted(IActionResult result) =>
        result.Should().BeOfType<AcceptedResult>().Subject.Value.Should().BeOfType<MediaAnalysisStartedResponse>().Subject;

    private static MediaAnalysisEstimateResponse Estimate(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MediaAnalysisEstimateResponse>().Subject;

    private static MediaAnalysisStatesResponse States(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MediaAnalysisStatesResponse>().Subject;

    private sealed class FailOnceInterceptor : SaveChangesInterceptor
    {
        public bool FailNextSave { get; set; }
        public Exception? Inner { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!FailNextSave) return base.SavingChangesAsync(eventData, result, cancellationToken);
            FailNextSave = false;
            throw new DbUpdateException("save failed", Inner);
        }
    }

    private sealed class Harness : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OwnerId { get; init; }
        public string MediaRef { get; init; } = string.Empty;
        public required FakeNonWorkflowCharges Charges { get; init; }
        public List<MediaAnalysisRequested> Published { get; } = [];
        public required FailOnceInterceptor Interceptor { get; init; }

        public static async Task<Harness> CreateAsync(long availableMillis = 10_000, long estimateMillis = 4_000)
        {
            FailOnceInterceptor interceptor = new();
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .AddInterceptors(interceptor)
                .UseInMemoryDatabase($"analysis-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            InferenceApiDbContext db = new(options);
            Guid ownerId = Guid.NewGuid();
            Project project = NewProject(ownerId);
            db.Projects.Add(project);
            ProjectFile file = AddVideo(db, project.Id);
            await db.SaveChangesAsync();
            return new Harness
            {
                Db = db,
                Interceptor = interceptor,
                ProjectId = project.Id,
                OwnerId = ownerId,
                MediaRef = $"file:{file.Id}",
                Charges = new FakeNonWorkflowCharges { AvailableMillis = availableMillis, EstimateMillis = estimateMillis }
            };
        }

        private static Project NewProject(Guid ownerId) => new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        private static ProjectFile AddVideo(InferenceApiDbContext db, Guid projectId)
        {
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = "talk.mp4",
                MimeType = "video/mp4",
                StorageKey = $"projects/{projectId}/userFiles/talk.mp4",
                StorageBucket = "b",
                SizeBytes = 10,
                CsamStatus = CsamScanStatus.Clean
            };
            db.ProjectFiles.Add(file);
            db.MediaDerivatives.Add(new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                SourceProjectFileId = file.Id,
                SourceKind = MediaSourceKind.ProjectFile,
                SourceStorageKey = file.StorageKey,
                CacheKey = Guid.NewGuid().ToString("N"),
                Kind = MediaDerivativeKind.Thumbnail,
                StorageKey = "x",
                StorageBucket = "b",
                SourceProbeJson = new MediaSourceProbe(90, 30, 1, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
            });
            return file;
        }

        public async Task<(Guid ProjectId, string MediaRef)> AddSecondProjectAsync()
        {
            Project second = NewProject(OwnerId);
            Db.Projects.Add(second);
            ProjectFile file = AddVideo(Db, second.Id);
            await Db.SaveChangesAsync();
            return (second.Id, $"file:{file.Id}");
        }

        public MediaAnalysisController Controller(bool billing = false, Guid? userId = null)
        {
            Guid user = userId ?? OwnerId;
            Mock<ICurrentUser> current = new();
            current.SetupGet(u => u.UserId).Returns(user);
            current.SetupGet(u => u.Email).Returns("t@example.com");
            current.SetupGet(u => u.IsAuthenticated).Returns(true);
            current.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(user));
            current.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            current.SetupGet(u => u.EmailVerified).Returns(true);

            Mock<IPublishEndpoint> publish = new();
            publish.Setup(p => p.Publish(It.IsAny<MediaAnalysisRequested>(), It.IsAny<CancellationToken>()))
                .Callback<MediaAnalysisRequested, CancellationToken>((m, _) => Published.Add(m))
                .Returns(Task.CompletedTask);

            Mock<IInferenceProviderResolver> providers = new();
            providers.Setup(p => p.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedTranscriptionProvider(Guid.NewGuid(), "asr", InferenceProviderKind.OpenAICompatible, "http://x", "whisper-1", "k", 60));

            Mock<IWorkflowCostEstimator> estimator = new();
            estimator.SetupGet(e => e.BillingEnabled).Returns(billing);

            return new MediaAnalysisController(
                Db, TestTenancy.Access(Db, current.Object), publish.Object, current.Object, providers.Object,
                NullLogger<MediaAnalysisController>.Instance, Charges, estimator.Object);
        }

        public async Task FinishAsync(Guid requestId, string digest, MediaAnalysisKindOutcome outcome, Guid? projectId = null)
        {
            Mock<ConsumeContext<MediaAnalysisFinished>> context = new();
            context.SetupGet(c => c.Message).Returns(new MediaAnalysisFinished(
                requestId, projectId ?? ProjectId, TestTenancy.OrgOf(OwnerId), null, digest, [outcome]));
            context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
            await new MediaAnalysisFinishedConsumer(Db, NullLogger<MediaAnalysisFinishedConsumer>.Instance, Charges).Consume(context.Object);
        }

        public async Task SeedResultAsync(string digest)
        {
            Db.MediaAnalysisResults.Add(new MediaAnalysisResult
            {
                Id = Guid.NewGuid(),
                OrganizationId = TestTenancy.OrgOf(OwnerId),
                Digest = digest,
                Kind = "transcript",
                Language = "",
                ResultJson = TranscriptJson,
                SummaryJson = "{}",
                MeasuredSeconds = 90,
                CreatedAt = DateTime.UtcNow
            });
            await Db.SaveChangesAsync();
        }

        public void Dispose() => Db.Dispose();
    }
}
