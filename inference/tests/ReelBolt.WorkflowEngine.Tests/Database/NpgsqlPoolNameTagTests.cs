#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Database;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Database;

/// <summary>
/// Security S-1: Npgsql's meter tags every pool instrument with <c>db.client.connection.pool.name</c>,
/// which defaults to the connection string minus <c>Password</c> (so <c>SSL Password</c>, host, user
/// and database would reach the OTLP collector). Every data source the services create carries a
/// fixed name instead. The connection attempts below target a refused loopback port; opening is only
/// there so Npgsql creates (and registers the metrics of) the data source, it never connects.
/// </summary>
public class NpgsqlPoolNameTagTests
{
    private const string PoolNameTag = "db.client.connection.pool.name";
    private const string Secret = "sekret";
    private const string Host = "127.0.0.2";

    // Unique per test, so a tag left behind by another test's data source can never match.
    private static (string ConnectionString, string Database, string User) LeakyConnectionString()
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        string database = $"s1db{id}";
        string user = $"s1user{id}";
        return ($"Host={Host};Port=1;Database={database};Username={user};Password=pw;SSL Password={Secret};Timeout=2",
            database, user);
    }

    /// <summary>Every tag value the Npgsql meter currently publishes, as (tag key, value) pairs.</summary>
    private static List<KeyValuePair<string, string>> NpgsqlTags()
    {
        var tags = new List<KeyValuePair<string, string>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Npgsql") l.EnableMeasurementEvents(instrument);
        };
        void Collect(ReadOnlySpan<KeyValuePair<string, object?>> span)
        {
            lock (tags)
            {
                foreach (KeyValuePair<string, object?> kv in span)
                    tags.Add(new KeyValuePair<string, string>(kv.Key, kv.Value?.ToString() ?? string.Empty));
            }
        }
        listener.SetMeasurementEventCallback<int>((_, _, span, _) => Collect(span));
        listener.SetMeasurementEventCallback<long>((_, _, span, _) => Collect(span));
        listener.SetMeasurementEventCallback<double>((_, _, span, _) => Collect(span));
        listener.Start();
        listener.RecordObservableInstruments();
        return tags;
    }

    private static void AssertNoLeak(string database, string user, string expectedPoolName)
    {
        List<KeyValuePair<string, string>> tags = NpgsqlTags();

        tags.Where(t => t.Key == PoolNameTag).Select(t => t.Value).Should().Contain(expectedPoolName);
        tags.Select(t => t.Value).Should().NotContain(v =>
            v.Contains(Secret, StringComparison.Ordinal)
            || v.Contains(Host, StringComparison.Ordinal)
            || v.Contains(database, StringComparison.Ordinal)
            || v.Contains(user, StringComparison.Ordinal));
    }

    private static void OpenAndIgnoreTheRefusal(DbContext db)
    {
        try
        {
            db.Database.OpenConnection();
        }
        catch (Exception)
        {
            // refused: expected, the data source exists now
        }
    }

    [Fact]
    public void The_inference_api_data_source_has_a_fixed_pool_name()
    {
        (string cs, string database, string user) = LeakyConnectionString();
        using var root = CompositionRootHarness.Build(
            "s1-inference-api",
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs });
        using IServiceScope scope = root.Services.CreateScope();

        OpenAndIgnoreTheRefusal(scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>());

        AssertNoLeak(database, user, "reelbolt-inference-api");
    }

    [Fact]
    public void The_workflow_engine_data_source_has_a_fixed_pool_name()
    {
        (string cs, string database, string user) = LeakyConnectionString();
        using var root = CompositionRootHarness.Build(
            "s1-workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration),
            new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs });
        using IServiceScope scope = root.Services.CreateScope();

        OpenAndIgnoreTheRefusal(scope.ServiceProvider.GetRequiredService<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>());

        AssertNoLeak(database, user, "reelbolt-workflow-engine");
    }

    [Fact]
    public async Task The_billing_change_lock_data_source_has_a_fixed_pool_name()
    {
        (string cs, string database, string user) = LeakyConnectionString();
        var billingLock = new PostgresBillingChangeLock(cs);

        Func<Task> act = () => billingLock.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);
        await act.Should().ThrowAsync<Exception>("nothing listens on the loopback port");

        AssertNoLeak(database, user, "reelbolt-inference-api-billing-lock");
    }

    [Fact]
    public async Task The_connection_sampler_data_source_has_a_fixed_pool_name()
    {
        (string cs, string database, string user) = LeakyConnectionString();
        await using Npgsql.NpgsqlDataSource dataSource = PostgresConnectionSampler.CreateDataSource(cs);

        Func<Task> act = async () => { await using var _ = await dataSource.OpenConnectionAsync(); };
        await act.Should().ThrowAsync<Exception>("nothing listens on the loopback port");

        AssertNoLeak(database, user, "reelbolt-inference-api-sampler");
    }
}
