using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// One limit of a C5 decision: at most <see cref="Limit"/> sends per sliding <see cref="Window"/> on
/// <see cref="Bucket"/>. An <see cref="Alarm"/> limit refuses nothing by itself; the caller decides
/// what being over it means (contract C6: only bot-checked requests still get a mail).
/// </summary>
public sealed record MailRateLimit(string Cap, string Bucket, TimeSpan Window, int Limit, bool Alarm = false);

/// <summary>A limit's sliding estimate at decision time, before this send.</summary>
public sealed record MailRateEstimate(MailRateLimit Limit, DateTime WindowStart, double Estimate)
{
    /// <summary>C5: a send is within the limit iff estimate + 1 ≤ limit.</summary>
    public bool Admits => Estimate + 1 <= Limit.Limit;
}

/// <summary>The outcome of one C5 decision: whether the send was allowed (and so counted), with every estimate.</summary>
public sealed record MailRateDecision(bool Allowed, IReadOnlyList<MailRateEstimate> Estimates);

/// <summary>The persistent counters of shared contracts C2 and C5 (<c>mail_rate_counters</c>).</summary>
public interface IMailRateCounterStore
{
    /// <summary>
    /// The raw C2 upsert: adds one to <paramref name="bucket"/> in the fixed window starting at
    /// <paramref name="windowStart"/> and returns the new count. Not a C5 decision: it counts whether
    /// or not the caller goes on to act.
    /// </summary>
    Task<int> IncrementAsync(string bucket, DateTime windowStart, CancellationToken ct);

    /// <summary>
    /// One C5 decision, atomic across every replica and the Go relay: under a
    /// <c>pg_advisory_xact_lock(hashtext(bucket))</c> per distinct bucket, taken in ordinal-sorted
    /// bucket order, reads each limit's current and previous window, computes the sliding estimates,
    /// asks <paramref name="allow"/>, and only when it answers true adds one to the current window of
    /// every distinct bucket. A refused send increments nothing. Runs in the DbContext's open
    /// transaction when there is one (and leaves committing to its owner), else in its own.
    /// </summary>
    Task<MailRateDecision> DecideAsync(
        IReadOnlyList<MailRateLimit> limits,
        DateTimeOffset now,
        Func<IReadOnlyList<MailRateEstimate>, bool> allow,
        CancellationToken ct);

    /// <summary>
    /// True for exactly one caller per (<paramref name="bucket"/>, <paramref name="windowStart"/>)
    /// across every replica: the "once per window" of an alarm warning. Not a counter any decision reads.
    /// </summary>
    Task<bool> TryMarkOnceAsync(string bucket, DateTime windowStart, CancellationToken ct);
}

/// <summary>The C5 sliding-window arithmetic, shared by every <see cref="IMailRateCounterStore"/>.</summary>
public static class SlidingWindow
{
    /// <summary>
    /// The estimates of <paramref name="limits"/> at <paramref name="now"/>, reading counts through
    /// <paramref name="count"/>: count(current window) + count(previous window) × (1 − elapsed fraction
    /// of the current window). Also returns the distinct (bucket, current window) rows a send increments.
    /// </summary>
    public static (IReadOnlyList<MailRateEstimate> Estimates, IReadOnlyList<(string Bucket, DateTime WindowStart)> Increments) Evaluate(
        IReadOnlyList<MailRateLimit> limits, DateTimeOffset now, Func<string, DateTime, int> count)
    {
        List<MailRateEstimate> estimates = new(limits.Count);
        List<(string, DateTime)> increments = [];
        foreach (MailRateLimit limit in limits)
        {
            TimeSpan length = Length(limit.Window);
            DateTime current = MailRateBuckets.WindowStart(now, length);
            double elapsed = Math.Clamp((now.UtcDateTime - current).Ticks / (double)length.Ticks, 0, 1);
            double estimate = count(limit.Bucket, current) + (count(limit.Bucket, current - length) * (1 - elapsed));
            estimates.Add(new MailRateEstimate(limit, current, estimate));
            if (!increments.Contains((limit.Bucket, current)))
            {
                increments.Add((limit.Bucket, current));
            }
        }

        return (estimates, increments);
    }

    /// <summary>Every (bucket, window start) row <see cref="Evaluate"/> reads: each limit's current and previous window.</summary>
    public static IReadOnlyList<(string Bucket, DateTime WindowStart)> Reads(IReadOnlyList<MailRateLimit> limits, DateTimeOffset now)
    {
        List<(string, DateTime)> reads = [];
        foreach (MailRateLimit limit in limits)
        {
            TimeSpan length = Length(limit.Window);
            DateTime current = MailRateBuckets.WindowStart(now, length);
            foreach (DateTime start in new[] { current, current - length })
            {
                if (!reads.Contains((limit.Bucket, start)))
                {
                    reads.Add((limit.Bucket, start));
                }
            }
        }

        return reads;
    }

    /// <summary>The distinct buckets of a decision in the order their locks are taken (ordinal), so two decisions never deadlock.</summary>
    public static IReadOnlyList<string> LockOrder(IReadOnlyList<MailRateLimit> limits) =>
        limits.Select(l => l.Bucket).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>The window length as the counters use it: whole seconds, at least one.</summary>
    private static TimeSpan Length(TimeSpan window) => TimeSpan.FromSeconds(Math.Max(1, (long)window.TotalSeconds));
}

/// <summary>
/// The C2/C5 counters on PostgreSQL, on the DbContext's own connection (and its transaction, when one
/// is open), so every API replica and the Go relay share the counts and a restart resets nothing.
/// </summary>
public sealed class PostgresMailRateCounterStore : IMailRateCounterStore
{
    /// <summary>The C2 upsert, byte for byte the statement the Go relay runs.</summary>
    public const string IncrementSql =
        "INSERT INTO mail_rate_counters (bucket, window_start, count, updated_at) VALUES ($1, $2, 1, now()) "
        + "ON CONFLICT (bucket, window_start) DO UPDATE SET count = mail_rate_counters.count + 1, updated_at = now() RETURNING count";

    /// <summary>The C5 per-bucket transaction lock, held until commit or rollback.</summary>
    public const string LockSql = "SELECT pg_advisory_xact_lock(hashtext($1))";

    /// <summary>The counts of the given (bucket, window start) pairs; missing rows are zero.</summary>
    public const string ReadSql =
        "SELECT c.bucket, c.window_start, c.count FROM mail_rate_counters c "
        + "JOIN unnest($1::text[], $2::timestamptz[]) AS k(bucket, window_start) "
        + "ON c.bucket = k.bucket AND c.window_start = k.window_start";

    /// <summary>Inserts a marker row only if none exists; returns a row only to the inserter.</summary>
    public const string MarkSql =
        "INSERT INTO mail_rate_counters (bucket, window_start, count, updated_at) VALUES ($1, $2, 1, now()) "
        + "ON CONFLICT (bucket, window_start) DO NOTHING RETURNING count";

    private readonly InferenceApiDbContext _db;

    public PostgresMailRateCounterStore(InferenceApiDbContext db) => _db = db;

    public async Task<int> IncrementAsync(string bucket, DateTime windowStart, CancellationToken ct)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            object? result = await ScalarAsync(IncrementSql, ct, bucket, Utc(windowStart));
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    public async Task<MailRateDecision> DecideAsync(
        IReadOnlyList<MailRateLimit> limits,
        DateTimeOffset now,
        Func<IReadOnlyList<MailRateEstimate>, bool> allow,
        CancellationToken ct)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            IDbContextTransaction? own = _db.Database.CurrentTransaction is null
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;
            try
            {
                foreach (string bucket in SlidingWindow.LockOrder(limits))
                {
                    await ScalarAsync(LockSql, ct, bucket);
                }

                IReadOnlyList<(string Bucket, DateTime WindowStart)> reads = SlidingWindow.Reads(limits, now);
                Dictionary<(string, DateTime), int> counts = [];
                await using (DbCommand read = Command(
                    ReadSql,
                    reads.Select(r => r.Bucket).ToArray(),
                    reads.Select(r => Utc(r.WindowStart)).ToArray()))
                await using (DbDataReader reader = await read.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        counts[(reader.GetString(0), Utc(reader.GetDateTime(1)))] = reader.GetInt32(2);
                    }
                }

                (IReadOnlyList<MailRateEstimate> estimates, IReadOnlyList<(string Bucket, DateTime WindowStart)> increments) =
                    SlidingWindow.Evaluate(limits, now, (bucket, start) => counts.GetValueOrDefault((bucket, Utc(start))));
                bool allowed = allow(estimates);
                if (allowed)
                {
                    foreach ((string bucket, DateTime start) in increments)
                    {
                        await ScalarAsync(IncrementSql, ct, bucket, Utc(start));
                    }
                }

                if (own is not null)
                {
                    await own.CommitAsync(ct);
                }

                return new MailRateDecision(allowed, estimates);
            }
            finally
            {
                if (own is not null)
                {
                    await own.DisposeAsync();
                }
            }
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    public async Task<bool> TryMarkOnceAsync(string bucket, DateTime windowStart, CancellationToken ct)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            return await ScalarAsync(MarkSql, ct, bucket, Utc(windowStart)) is not null and not DBNull;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct, params object[] values)
    {
        await using DbCommand command = Command(sql, values);
        return await command.ExecuteScalarAsync(ct);
    }

    /// <summary>A command on the context's connection and transaction, with unnamed parameters bound to <c>$1</c>, <c>$2</c> … in order.</summary>
    private DbCommand Command(string sql, params object[] values)
    {
        DbCommand command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (object value in values)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>A UTC DateTime binds as timestamptz.</summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>C2 bucket names and windows for the anonymous confirmation mail. Never a raw address or IP.</summary>
public static class MailRateBuckets
{
    /// <summary>The canonical mailbox's cooldown (one mail per cooldown window).</summary>
    public static string ConfirmMailboxCooldown(string canonical) => "confirm:mbox:cool:" + Sha256Hex(canonical);

    /// <summary>The canonical mailbox's daily allowance.</summary>
    public static string ConfirmMailboxDay(string canonical) => "confirm:mbox:day:" + Sha256Hex(canonical);

    public static string ConfirmDomain(string domain) => "confirm:domain:" + domain;

    /// <summary>The client IP's bucket: IPv4 on the /32, IPv6 on the /64 prefix (N-04), IPv4-mapped IPv6 as IPv4.</summary>
    public static string ConfirmIp(string? ip) => "confirm:ip:" + Sha256Hex(IpKey(ip));

    public const string ConfirmGlobal = "confirm:global";

    /// <summary>The marker row of an alarm's once-per-window warning.</summary>
    public static string AlarmMarker(string bucket) => "confirm:alarm:" + bucket;

    /// <summary>The address a per-IP limit keys on: <c>a.b.c.d/32</c>, <c>xxxx:xxxx:xxxx:xxxx::/64</c>, or the raw text when it is no IP.</summary>
    public static string IpKey(string? ip)
    {
        string raw = ip?.Trim() ?? string.Empty;
        if (!IPAddress.TryParse(raw, out IPAddress? address))
        {
            return raw;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address + "/32";
        }

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>UTC <paramref name="now"/> floored to a window of <paramref name="length"/> (epoch seconds div window seconds).</summary>
    public static DateTime WindowStart(DateTimeOffset now, TimeSpan length)
    {
        long window = Math.Max(1, (long)length.TotalSeconds);
        long seconds = now.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(seconds - (((seconds % window) + window) % window)).UtcDateTime;
    }

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Metrics for the marketing confirmation caps.</summary>
public static class MarketingDiagnostics
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.Marketing";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>One per refused confirmation mail, tagged <c>cap</c> = mailbox | ip | ceiling | domain | global.</summary>
    public static readonly Counter<long> ConfirmCapHit =
        Meter.CreateCounter<long>("reelbolt.marketing.confirm_cap_hit", "mails", "Anonymous marketing confirmation mails refused by a cap, tagged with the cap");

    /// <summary>
    /// One per confirmation request over an alarm threshold, tagged <c>alarm</c> = domain | global and
    /// <c>outcome</c> = sent (a bot-checked site forward) | refused (a direct anonymous call, or another cap).
    /// </summary>
    public static readonly Counter<long> ConfirmAlarm =
        Meter.CreateCounter<long>("reelbolt.marketing.confirm_alarm", "requests", "Anonymous marketing confirmation requests over the domain or global alarm threshold");
}
