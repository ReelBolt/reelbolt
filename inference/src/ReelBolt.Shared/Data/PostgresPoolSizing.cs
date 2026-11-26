using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ReelBolt.Shared.Data;

/// <summary>
/// Gives every Npgsql connection string an explicit <c>Maximum Pool Size</c>, so the sum of all
/// service pools is a number someone chose (docs/database-connections.md) rather than Npgsql's
/// default of 100 per process. Precedence: <c>Database:MaxPoolSize</c> (env
/// <c>Database__MaxPoolSize</c>) &gt; a value already in the connection string &gt; the service default.
/// </summary>
public static class PostgresPoolSizing
{
    /// <summary>Configuration key that overrides every other source of the pool size.</summary>
    public const string MaxPoolSizeKey = "Database:MaxPoolSize";

    /// <summary>Npgsql's canonical keyword, the one written back.</summary>
    public const string CanonicalKeyword = "Maximum Pool Size";

    /// <summary>Npgsql's canonical minimum keyword, the one a minimum in any spelling is written back as.</summary>
    public const string CanonicalMinKeyword = "Minimum Pool Size";

    // Npgsql 11 itself accepts "Maximum Pool Size" and "MaxPoolSize" (case-insensitive) but rejects
    // "Max Pool Size" (the SqlClient spelling); it is recognised here and rewritten to the canonical key.
    private static readonly string[] Aliases = [CanonicalKeyword, "MaxPoolSize", "Max Pool Size"];

    // Security S-21: Npgsql 11 likewise rejects "Min Pool Size"; every spelling is validated against the
    // effective maximum and rewritten to the canonical key.
    private static readonly string[] MinAliases = [CanonicalMinKeyword, "MinPoolSize", "Min Pool Size"];

    /// <summary>
    /// Returns <paramref name="connectionString"/> rebuilt with exactly one <c>Maximum Pool Size</c>, and a
    /// <c>Minimum Pool Size</c> in any accepted spelling rewritten to that canonical keyword.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>Database:MaxPoolSize</c>, or a pool size already in the
    /// connection string, is set but is not an integer &gt; 0; or a <c>Minimum Pool Size</c> in the connection
    /// string is not an integer &gt;= 0 or exceeds the effective maximum.</exception>
    public static string ApplyMaxPoolSize(string connectionString, IConfiguration configuration, int defaultMaxPoolSize)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(defaultMaxPoolSize, 0);

        int? configured = ReadConfigured(configuration);
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        int? fromString = null;
        string? fromStringKeyword = null;
        foreach (string alias in Aliases)
        {
            if (builder.TryGetValue(alias, out object? value))
            {
                // Validated even when Database:MaxPoolSize overrides it: a 0 would fail every open and
                // abc/-5 the first Npgsql use, so startup is the place to say so.
                int size = ValidateFromString(alias, value, minimum: 1);
                if (fromString is null)
                {
                    fromString = size;
                    fromStringKeyword = alias;
                }
                builder.Remove(alias);
            }
        }

        (int effective, string source) = configured is { } c ? (c, MaxPoolSizeKey)
            : fromString is { } s ? (s, $"the connection string's '{fromStringKeyword}' setting")
            : (defaultMaxPoolSize, "the service default");
        int? minimum = TakeMinimum(builder, effective, source);

        builder[CanonicalKeyword] = effective.ToString(CultureInfo.InvariantCulture);
        if (minimum is { } m)
            builder[CanonicalMinKeyword] = m.ToString(CultureInfo.InvariantCulture);
        return builder.ConnectionString;
    }

    // QA-4: Npgsql only rejects Minimum > Maximum at the first open; startup is the place to say so.
    // The message carries the two keywords and the two parsed numbers, never connection-string text.
    // Removes every spelling and returns the first one's value, for the caller to write back canonically.
    private static int? TakeMinimum(DbConnectionStringBuilder builder, int effectiveMax, string maxSource)
    {
        int? first = null;
        foreach (string alias in MinAliases)
        {
            if (!builder.TryGetValue(alias, out object? value)) continue;
            int min = ValidateFromString(alias, value, minimum: 0);
            if (min > effectiveMax)
                throw new InvalidOperationException(
                    $"The '{alias}' setting in the database connection string (ConnectionStrings:DefaultConnection) is {min}, " +
                    $"greater than the effective {CanonicalKeyword} of {effectiveMax} (from {maxSource}).");
            first ??= min;
            builder.Remove(alias);
        }
        return first;
    }

    /// <summary>
    /// Returns <paramref name="connectionString"/> without any pool-size setting, maximum or minimum, in
    /// every spelling <see cref="ApplyMaxPoolSize"/> accepts (case-insensitive), so a caller that sets its
    /// own pool size on an Npgsql builder is not rejected over the SqlClient spellings <c>Max Pool Size</c>
    /// and <c>Min Pool Size</c>.
    /// </summary>
    public static string RemoveMaxPoolSize(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string alias in Aliases)
            builder.Remove(alias);
        foreach (string alias in MinAliases)
            builder.Remove(alias);
        return builder.ConnectionString;
    }

    // Names the keyword and the requirement only: neither the value (a mis-quoted one can carry
    // ";Password=...") nor the rest of the connection string is ever echoed.
    private static int ValidateFromString(string keyword, object? value, int minimum)
    {
        string raw = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) && size >= minimum)
            return size;
        throw new InvalidOperationException(
            $"The '{keyword}' setting in the database connection string (ConnectionStrings:DefaultConnection) " +
            (minimum > 0 ? "must be a whole number greater than 0." : "must be a whole number of 0 or more."));
    }

    private static int? ReadConfigured(IConfiguration configuration)
    {
        string? raw = configuration[MaxPoolSizeKey];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) && size > 0)
            return size;
        throw new InvalidOperationException(
            $"{MaxPoolSizeKey} must be a whole number greater than 0 (got '{raw}').");
    }
}
