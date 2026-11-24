using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Diff;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.Merge;
using ReelBolt.Shared.DesignSystems.ProductMap;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>DRF-05: the pure drift stages are cooperatively cancellable, so the per-item timeout abandons a slow item.</summary>
public sealed class DriftCancellationTests
{
    private static CancellationToken Cancelled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        return cts.Token;
    }

    private static JsonObject BigDocument(int tokens)
    {
        JsonObject doc = DesignDocument.New("Big");
        TokenProvenance prov = new("manual", null, null, null);
        for (int i = 0; i < tokens; i++)
            DesignDocument.SetToken(doc, $"color.group{i % 50}.t{i}", "color", new JsonObject { ["hex"] = "#112233" }, prov, 1.0);
        return doc;
    }

    [Fact]
    public void ImportAll_with_a_cancelled_token_throws_before_reading_thousands_of_files()
    {
        List<ImportFile> files = Enumerable.Range(0, 5000)
            .Select(i => new ImportFile($"app/s{i}.css", Encoding.UTF8.GetBytes(":root { --color-a: #112233; }"), "x", null))
            .ToList();

        Action act = () => DesignImportRegistry.ImportAll(files, Cancelled());

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ProductMapExtractor_with_a_cancelled_token_throws()
    {
        List<ImportFile> files = Enumerable.Range(0, 2000)
            .Select(i => new ImportFile($"app/p{i}/page.tsx", Encoding.UTF8.GetBytes("export default function P() { return null; }"), "x", null))
            .ToList();

        Action act = () => ProductMapExtractor.Extract(files, Cancelled());

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void DesignDocumentMerger_with_a_cancelled_token_throws_on_a_huge_document()
    {
        JsonObject big = BigDocument(20_000);

        Action act = () => DesignDocumentMerger.Merge(big, [big], Cancelled());

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ProductMapMerger_and_the_differs_honour_a_cancelled_token()
    {
        JsonObject big = BigDocument(20_000);
        ProductMapDocument empty = ProductMapMerger.Merge(null, null);

        ((Action)(() => ProductMapMerger.Merge(empty, empty, Cancelled()))).Should().Throw<OperationCanceledException>();
        ((Action)(() => DesignDocumentDiffer.Diff(big, big, Cancelled()))).Should().Throw<OperationCanceledException>();
        ((Action)(() => DesignDocumentDiffer.DiffProposed(big, big, big, Cancelled()))).Should().Throw<OperationCanceledException>();
        ((Action)(() => ProductMapDiffer.Diff(empty, empty, Cancelled()))).Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void A_short_deadline_returns_promptly_on_a_huge_merge()
    {
        JsonObject big = BigDocument(30_000);
        using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(1));

        DateTime started = DateTime.UtcNow;
        Action act = () =>
        {
            for (int i = 0; i < 200; i++)
                DesignDocumentMerger.Merge(big, [big, big], deadline.Token);
        };

        act.Should().Throw<OperationCanceledException>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    private sealed class SelectiveService : IDesignSystemDriftService
    {
        public List<Guid> Started { get; } = [];
        public List<Guid> Finished { get; } = [];
        public Guid Hanging { get; init; }

        public async Task<DesignDriftOutcome> CheckAsync(
            Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, CancellationToken ct)
        {
            lock (Started)
                Started.Add(projectId);
            if (projectId == Hanging)
                await Task.Delay(Timeout.Infinite, ct);

            lock (Finished)
                Finished.Add(projectId);
            return new DesignDriftOutcome("nochange", null);
        }
    }

    [Fact]
    public async Task The_worker_survives_a_timed_out_item_and_serves_the_next_one()
    {
        Guid hung = Guid.NewGuid();
        Guid next = Guid.NewGuid();
        SelectiveService drift = new() { Hanging = hung };
        ServiceCollection services = new();
        services.AddScoped<IDesignSystemDriftService>(_ => drift);
        services.AddSingleton<ITenantContext>(new TestTenant());
        await using ServiceProvider provider = services.BuildServiceProvider();

        DesignDriftQueue queue = new();
        DesignSystemDriftWorker worker = new(
            queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DesignSystemDriftWorker>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["DesignSystem:Drift:ItemTimeoutSeconds"] = "0.2" }).Build());

        queue.Enqueue(hung, DesignDriftTrigger.GitHubPush, null, Guid.NewGuid());
        queue.Enqueue(next, DesignDriftTrigger.GitHubPush, null, Guid.NewGuid());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            DateTime give = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < give)
            {
                lock (drift.Finished)
                {
                    if (drift.Finished.Contains(next))
                        break;
                }

                await Task.Delay(20);
            }

            lock (drift.Finished)
                drift.Finished.Should().Contain(next, "the loop moved on after the first item timed out");
            queue.IsQueued(hung).Should().BeFalse("the timed-out item released its slot");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
