#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

/// <summary>Fixed random keys per secret name, recording every name asked for.</summary>
internal sealed class InMemorySecretStore : IMarketingSecretStore
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    public List<string> Requested { get; } = [];

    public byte[] GetKey(string name)
    {
        Requested.Add(name);
        if (!_keys.TryGetValue(name, out byte[]? key))
        {
            key = RandomNumberGenerator.GetBytes(32);
            _keys[name] = key;
        }

        return key;
    }
}

/// <summary>Records the confirmation mails instead of sending them.</summary>
internal sealed class RecordingOutbox : IMarketingMailOutbox
{
    public List<AccountMail> Sent { get; } = [];
    public List<MarketingMailOrigin> Origins { get; } = [];

    public bool Enqueue(AccountMail mail, MarketingMailOrigin origin)
    {
        Sent.Add(mail);
        Origins.Add(origin);
        return true;
    }
}

/// <summary>
/// The test double for <see cref="IMailRateCounterStore"/>: the C2 rows in a dictionary, and the C5
/// decision through the production <see cref="SlidingWindow"/> arithmetic under one lock (the
/// advisory locks' job). EF's providers cannot run the Postgres statements, so tests share one
/// instance across service instances to model the shared table; MailRateCounterStorePostgresTests
/// runs the real store.
/// </summary>
internal sealed class InMemoryMailRateCounterStore : IMailRateCounterStore
{
    private readonly object _gate = new();

    public Dictionary<(string Bucket, DateTime WindowStart), int> Counts { get; } = [];

    /// <summary>When set, every call throws, as a missing table or a dead database would.</summary>
    public bool Fail { get; set; }

    /// <summary>The message of the exception thrown while <see cref="Fail"/> is set.</summary>
    public string FailMessage { get; set; } = "relation \"mail_rate_counters\" does not exist";

    public Task<int> IncrementAsync(string bucket, DateTime windowStart, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            Counts.TryGetValue((bucket, windowStart), out int count);
            Counts[(bucket, windowStart)] = ++count;
            return Task.FromResult(count);
        }
    }

    public Task<MailRateDecision> DecideAsync(
        IReadOnlyList<MailRateLimit> limits, DateTimeOffset now, Func<IReadOnlyList<MailRateEstimate>, bool> allow, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            (IReadOnlyList<MailRateEstimate> estimates, IReadOnlyList<(string Bucket, DateTime WindowStart)> increments) =
                SlidingWindow.Evaluate(limits, now, (bucket, start) => Counts.GetValueOrDefault((bucket, start)));
            bool allowed = allow(estimates);
            if (allowed)
            {
                foreach ((string bucket, DateTime start) in increments)
                {
                    Counts[(bucket, start)] = Counts.GetValueOrDefault((bucket, start)) + 1;
                }
            }

            return Task.FromResult(new MailRateDecision(allowed, estimates));
        }
    }

    public Task<bool> TryMarkOnceAsync(string bucket, DateTime windowStart, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(Counts.TryAdd((bucket, windowStart), 1));
        }
    }

    private void ThrowIfFailing()
    {
        if (Fail)
        {
            throw new InvalidOperationException(FailMessage);
        }
    }
}

/// <summary>
/// A logger that keeps every entry, with its level and event id. The message carries the exception's
/// full text too, as a console or JSON sink would print it.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();

    public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            string message = formatter(state, exception);
            Entries.Add((logLevel, eventId, exception is null ? message : message + Environment.NewLine + exception));
        }
    }
}

/// <summary>
/// One Sqlite in-memory database plus a <see cref="MarketingConsentService"/> wired to fakes. Sqlite,
/// not the InMemory provider, because the sweeper uses <c>ExecuteDelete</c>.
/// </summary>
internal sealed class MarketingHarness : IDisposable
{
    public const string BaseUrl = "https://app.example.test";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public MarketingHarness()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        Tokens = new MarketingLinkTokens(Secrets);
        Suppression = new MarketingSuppressionHasher(Secrets);
    }

    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
    public InMemorySecretStore Secrets { get; } = new();
    public RecordingOutbox Outbox { get; } = new();
    public MarketingLinkTokens Tokens { get; }
    public MarketingSuppressionHasher Suppression { get; }
    public MarketingWording Wording { get; } = MarketingWording.LoadFile(MarketingWording.DefaultPath);
    public MarketingOptions Options { get; } = new() { PublicBaseUrl = BaseUrl };
    public CloudStorageOptions CloudStorage { get; } = new();
    public InMemoryMailRateCounterStore Counters { get; } = new();
    public RecordingLogger<MarketingConsentService> Logger { get; } = new();

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    /// <summary>A context on the same database under a tenant context (a non-system one filters like production).</summary>
    public InferenceApiDbContext NewDb(ReelBolt.Shared.Tenancy.ITenantContext tenant)
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options, tenant);

    /// <summary>A context on the same database with command interceptors (to land a change between two statements).</summary>
    public InferenceApiDbContext NewDb(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    public MarketingConsentService NewService(InferenceApiDbContext db) => new(
        db,
        Tokens,
        Suppression,
        Outbox,
        Wording,
        Microsoft.Extensions.Options.Options.Create(Options),
        Microsoft.Extensions.Options.Options.Create(CloudStorage),
        Counters,
        Logger,
        Clock);

    public Guid AddUser(string email, DateTime? verifiedAt)
    {
        using InferenceApiDbContext db = NewDb();
        Guid id = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = id,
            Email = email,
            DisplayName = "U",
            EmailVerifiedAt = verifiedAt,
        });
        db.SaveChanges();
        return id;
    }

    public void Dispose() => _connection.Dispose();
}
