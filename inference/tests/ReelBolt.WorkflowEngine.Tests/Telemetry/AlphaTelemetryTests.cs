using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text.Json;
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

namespace ReelBolt.WorkflowEngine.Tests.Telemetry;

/// <summary>
/// The server half of the alpha funnel and the client-error surface (alpha-launch T-018, security
/// follow-up). Three things are pinned here:
/// <list type="bullet">
/// <item>the route redactor matches the corpus the browser also reads, so the two cannot drift;</item>
/// <item>the funnel counter moves only when the durable claim is NEW, so one authenticated caller
/// cannot drive it without bound;</item>
/// <item>an oversized batch and an unknown event are refused before anything is claimed.</item>
/// </list>
/// </summary>
public class AlphaTelemetryTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- the shared route-redaction contract -------------------------------------------------

    private sealed record Corpus(IReadOnlyList<string> StaticSegments, IReadOnlyList<CorpusCase> Cases);

    private sealed record CorpusCase(string Input, string Expected);

    private static Corpus LoadCorpus()
    {
        string path = Path.Combine(
            RepositoryRoot(),
            "inference", "src", "ReelBolt.Inference.Api", "Services", "Telemetry", "route-redaction.corpus.json");
        File.Exists(path).Should().BeTrue($"expected the shared corpus at {path}");
        Corpus? corpus = JsonSerializer.Deserialize<Corpus>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        corpus.Should().NotBeNull();
        return corpus!;
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(AlphaTelemetryTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln"))) return dir;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void The_redactor_and_the_corpus_agree_on_the_static_segments()
    {
        Corpus corpus = LoadCorpus();
        RouteRedactor.StaticSegments.Should().BeEquivalentTo(corpus.StaticSegments);
    }

    [Fact]
    public void The_redactor_matches_every_corpus_case()
    {
        Corpus corpus = LoadCorpus();
        foreach (CorpusCase testCase in corpus.Cases)
        {
            RouteRedactor.Redact(testCase.Input).Should().Be(testCase.Expected, $"route {testCase.Input}");
        }
    }

    // ---- the durable claim gates the counter -------------------------------------------------

    private sealed class FakeLog : IProductEventLog
    {
        private readonly HashSet<(Guid, string)> _claimed = [];
        public List<(Guid UserId, string Event)> Calls { get; } = [];

        public Task AppendAsync(ProductEvent entry, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> ClaimAndAppendAsync(Guid userId, Guid? organizationId, string eventName, string source, CancellationToken ct)
        {
            Calls.Add((userId, eventName));
            return Task.FromResult(_claimed.Add((userId, eventName)));
        }

        // The client ingest never makes the payment step's organization-activation claim: this double
        // does not model it, and returns false rather than pretending to have written a row.
        public Task<bool> ClaimAndAppendFirstPaymentAsync(Guid organizationId, Guid? userId, CancellationToken ct)
            => Task.FromResult(false);
    }

    private sealed class FakeUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
    }

    private static TelemetryController Controller(IProductEventLog log) => new(
        new FakeUser(User),
        Options.Create(new AlphaTelemetryOptions { ClientIngestEnabled = true }),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Test" }).Build(),
        log);

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

    private static ClientEventsDto Batch(params string[] names) =>
        new() { Events = names.Select(n => new ClientFunnelEventDto { Name = n }).ToList() };

    [Fact]
    public async Task An_event_is_counted_only_when_the_claim_is_new()
    {
        var log = new FakeLog();
        TelemetryController controller = Controller(log);

        IActionResult first = await controller.Events(Batch("alpha.first_project"), CancellationToken.None);
        IActionResult second = await controller.Events(Batch("alpha.first_project"), CancellationToken.None);

        first.Should().BeOfType<NoContentResult>();
        second.Should().BeOfType<NoContentResult>();
        log.Calls.Should().HaveCount(2, "both calls reached the claim store");
    }

    [Fact]
    public void The_counter_moves_once_even_when_the_caller_ignores_once_per_user()
    {
        var log = new FakeLog();
        TelemetryController controller = Controller(log);

        long counted = CountFunnelEvents(() =>
        {
            controller.Events(Batch("alpha.first_project"), CancellationToken.None).GetAwaiter().GetResult();
            controller.Events(Batch("alpha.first_project"), CancellationToken.None).GetAwaiter().GetResult();
            controller.Events(Batch("alpha.first_project"), CancellationToken.None).GetAwaiter().GetResult();
        });

        counted.Should().Be(1, "the durable claim, not the caller, decides how often the event counts");
    }

    [Fact]
    public async Task An_oversized_batch_is_refused_before_anything_is_claimed()
    {
        var log = new FakeLog();
        string[] tooMany = Enumerable.Repeat("alpha.signup", TelemetryController.MaxEventsPerBatch + 1).ToArray();

        IActionResult result = await Controller(log).Events(Batch(tooMany), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        log.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_event_is_refused_before_anything_is_claimed()
    {
        var log = new FakeLog();

        IActionResult result = await Controller(log).Events(Batch("alpha.made_up"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        log.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_endpoints_answer_404_until_the_deployment_opts_in()
    {
        var log = new FakeLog();
        var controller = new TelemetryController(
            new FakeUser(User),
            Options.Create(new AlphaTelemetryOptions { ClientIngestEnabled = false }),
            new ConfigurationBuilder().Build(),
            log);

        (await controller.Events(Batch("alpha.signup"), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        controller.Errors(new ClientErrorDto { Name = "TypeError", Route = "/app/projects/p1" }).Should().BeOfType<NotFoundResult>();
        log.Calls.Should().BeEmpty();
    }
}
