#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Database;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Database;

/// <summary>
/// The server-connection sampler must never get in the way: disabled at 0 it touches nothing, and on
/// a provider that is not Npgsql (InMemory here, as in every non-Postgres test host) it stops quietly
/// without publishing a value. The Postgres path runs in the gated saturation test at the end.
/// The gauges read one static sample, so the class joins the Postgres collection: the gated test's
/// reading can never overlap another test that asserts nothing is published.
/// </summary>
[Collection("Postgres")]
public class PostgresConnectionSamplerTests
{
    private static IConfiguration Config(string? seconds)
    {
        var values = new Dictionary<string, string?>();
        if (seconds is not null) values["Database:ServerConnectionSampleSeconds"] = seconds;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static List<string> ObservedInstruments() => Observed().ConvertAll(m => m.Instrument);

    private static List<(string Instrument, int Value)> Observed()
    {
        var seen = new List<(string, int)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PostgresConnectionSampler.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => seen.Add((instrument.Name, value)));
        listener.Start();
        listener.RecordObservableInstruments();
        return seen;
    }

    // QA-3: the sampler's own source, so a saturated API pool cannot blind it. Builds the data source
    // without opening it, so this test runs in the default (non-Postgres) suite.
    [Fact]
    public void The_sampler_data_source_holds_exactly_one_connection_whatever_the_string_says()
    {
        const string cs = "Host=127.0.0.2;Port=1;Database=d;Username=u;Password=pw;Maximum Pool Size=50;Minimum Pool Size=5";

        using NpgsqlDataSource dataSource = PostgresConnectionSampler.CreateDataSource(cs);
        NpgsqlConnectionStringBuilder csb = new(dataSource.ConnectionString);

        PostgresConnectionSampler.MaxPoolSize.Should().Be(1);
        csb.MaxPoolSize.Should().Be(1);
        csb.MinPoolSize.Should().Be(0);
        csb.Host.Should().Be("127.0.0.2");
        csb.Database.Should().Be("d");
        PostgresConnectionSampler.DataSourceName.Should().Be("reelbolt-inference-api-sampler");
    }

    // Security T-041 F3: every pool-size spelling PostgresPoolSizing accepts, including the SqlClient
    // "Max Pool Size" Npgsql itself rejects, still yields the one-connection sampler source.
    [Theory]
    [InlineData("Max Pool Size")]
    [InlineData("Maximum Pool Size")]
    [InlineData("MaxPoolSize")]
    [InlineData("max pool size")]
    public void The_sampler_data_source_accepts_every_pool_size_spelling(string key)
    {
        using NpgsqlDataSource dataSource = PostgresConnectionSampler.CreateDataSource($"Host=127.0.0.1;Port=1;Username=u;Password=p;{key}=20");

        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize.Should().Be(1);
    }

    // Security S-21 / QA-10: Npgsql rejects the SqlClient spelling "Min Pool Size", which used to
    // throw here and leave the gauges off; every Minimum spelling is now accepted and dropped to 0.
    [Theory]
    [InlineData("Min Pool Size")]
    [InlineData("MinPoolSize")]
    [InlineData("min pool size")]
    [InlineData("Minimum Pool Size")]
    public void The_sampler_data_source_accepts_every_minimum_pool_size_spelling(string key)
    {
        using NpgsqlDataSource dataSource = PostgresConnectionSampler.CreateDataSource($"Host=127.0.0.1;Port=1;Username=u;Password=p;{key}=2");

        var csb = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        csb.MaxPoolSize.Should().Be(1);
        csb.MinPoolSize.Should().Be(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task Disabled_does_not_touch_the_database_and_completes(string seconds)
    {
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var sampler = new PostgresConnectionSampler(scopes.Object, Config(seconds), NullLogger<PostgresConnectionSampler>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        await sampler.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        sampler.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        scopes.Verify(s => s.CreateScope(), Times.Never);
        await sampler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_non_npgsql_provider_stops_quietly_and_publishes_nothing()
    {
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        ServiceProvider sp = services.BuildServiceProvider();
        var sampler = new PostgresConnectionSampler(
            sp.GetRequiredService<IServiceScopeFactory>(), Config("1"), NullLogger<PostgresConnectionSampler>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        await sampler.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        sampler.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        ObservedInstruments().Should().BeEmpty();
        await sampler.StopAsync(CancellationToken.None);
    }

    // Security S-5: an interval PeriodicTimer cannot take (or an absurd one) must not throw out of
    // ExecuteAsync, where the default BackgroundServiceExceptionBehavior would stop the host.
    [Theory]
    [InlineData("-5")]
    [InlineData("86401")]
    [InlineData("4294968")]
    [InlineData("2147483647")]
    public async Task An_out_of_range_interval_warns_and_disables_sampling(string seconds)
    {
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var logger = new RecordingLogger();
        var sampler = new PostgresConnectionSampler(scopes.Object, Config(seconds), logger);

        await sampler.StartAsync(CancellationToken.None);
        await sampler.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        sampler.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        scopes.Verify(s => s.CreateScope(), Times.Never);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("Database:ServerConnectionSampleSeconds").And.Contain(seconds);
        await sampler.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("86400")]
    public async Task The_interval_bounds_are_accepted(string seconds)
    {
        // In range: the sampler goes on to the provider check (a scope), which this mock fails
        // and the sampler logs as "could not be resolved" — proof it did not stop at the range check.
        var scopes = new Mock<IServiceScopeFactory>();
        scopes.Setup(s => s.CreateScope()).Throws(new InvalidOperationException("no provider in this test"));
        var logger = new RecordingLogger();
        var sampler = new PostgresConnectionSampler(scopes.Object, Config(seconds), logger);

        await sampler.StartAsync(CancellationToken.None);
        await sampler.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        scopes.Verify(s => s.CreateScope(), Times.Once);
        logger.Entries.Should().NotContain(e => e.Message.Contains("ServerConnectionSampleSeconds"));
        await sampler.StopAsync(CancellationToken.None);
    }

    private sealed class RecordingLogger : ILogger<PostgresConnectionSampler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task Stopping_before_the_first_sample_ends_quietly()
    {
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var sampler = new PostgresConnectionSampler(scopes.Object, Config(null), NullLogger<PostgresConnectionSampler>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await sampler.StartAsync(cts.Token);
        await sampler.StopAsync(CancellationToken.None);

        scopes.Verify(s => s.CreateScope(), Times.Never);
    }

    // QA-3, against a real server: the API's EF pool (cap 2) is fully held, yet a sample succeeds on
    // the sampler's own connection and counts the two held backends plus itself. Needs a server:
    // without REELBOLT_TEST_PG it returns early (reported as passed), like the other gated classes;
    // inference/tests/run-postgres-tests.sh runs it for real.
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task A_saturated_api_pool_does_not_blind_the_sampler()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (baseConnection is null) return;
        string apiConnection = new NpgsqlConnectionStringBuilder(baseConnection) { MaxPoolSize = 2, Timeout = 2 }.ConnectionString;

        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(apiConnection));
        await using ServiceProvider sp = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ServerConnectionSampleSeconds"] = "1",
            ["ConnectionStrings:DefaultConnection"] = apiConnection,
        }).Build();

        // Hold both connections of the API pool open, and prove a third EF open now fails.
        using IServiceScope first = sp.CreateScope();
        using IServiceScope second = sp.CreateScope();
        var held = first.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        await held.Database.OpenConnectionAsync();
        await second.ServiceProvider.GetRequiredService<InferenceApiDbContext>().Database.OpenConnectionAsync();
        using (IServiceScope third = sp.CreateScope())
        {
            Func<Task> exhausted = () => third.ServiceProvider.GetRequiredService<InferenceApiDbContext>().Database.OpenConnectionAsync();
            await exhausted.Should().ThrowAsync<Exception>("the API pool is saturated");
        }

        var logger = new RecordingLogger();
        using var sampler = new PostgresConnectionSampler(sp.GetRequiredService<IServiceScopeFactory>(), configuration, logger);
        await sampler.StartAsync(CancellationToken.None);
        List<(string Instrument, int Value)> observed = new();
        for (int i = 0; i < 100 && observed.Count == 0; i++)
        {
            await Task.Delay(100);
            observed = Observed();
        }

        // The server's own count, read on a held connection while the sampler's idle one stays open.
        await using var command = held.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT (SELECT coalesce(sum(numbackends), 0) FROM pg_stat_database)::int, current_setting('max_connections')::int";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        int serverCount = reader.GetInt32(0);
        int serverMax = reader.GetInt32(1);
        await reader.DisposeAsync();
        await sampler.StopAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        observed.Should().Contain(("reelbolt.db.server.connections", serverCount));
        observed.Should().Contain(("reelbolt.db.server.max_connections", serverMax));
        serverCount.Should().BeGreaterThanOrEqualTo(3, "two held API connections plus the sampler's own");
        Console.WriteLine($"QA-3 saturated-pool sample: connections={serverCount} max_connections={serverMax}");
    }
}
