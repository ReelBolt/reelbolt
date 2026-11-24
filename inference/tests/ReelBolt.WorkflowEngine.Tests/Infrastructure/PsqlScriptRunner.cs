#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;

namespace ReelBolt.WorkflowEngine.Tests.Infrastructure;

/// <summary>
/// Runs an operator psql script (<c>infra/postgres/*.sql</c>) through Npgsql, so the role tests execute the
/// file an operator runs and not a copy of it. It understands only the psql features those scripts use:
/// <c>\set</c>, <c>\if :{?name}</c> / <c>\else</c> / <c>\endif</c> and <c>:'name'</c> substitution. Any other
/// backslash command throws, so a script that starts using one fails here instead of being half run.
/// </summary>
public static partial class PsqlScriptRunner
{
    /// <summary>The repository root: the first parent directory that holds <c>infra/postgres</c>.</summary>
    public static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "infra", "postgres")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repository root (the directory holding infra/postgres) not found.");
    }

    /// <summary>Reads a repo-relative script.</summary>
    public static string Read(string repoRelativePath) => File.ReadAllText(Path.Combine(RepoRoot(), repoRelativePath));

    /// <summary>Renders the script to plain SQL the way psql would with <paramref name="variables"/> given as <c>-v</c>.</summary>
    public static string Render(string script, IReadOnlyDictionary<string, string> variables)
    {
        Dictionary<string, string> values = new(variables, StringComparer.Ordinal);
        Stack<bool> active = new();
        StringBuilder sql = new();
        foreach (string line in script.Split('\n'))
        {
            string trimmed = line.Trim();
            bool on = active.Count == 0 || active.Peek();
            if (trimmed.StartsWith('\\'))
            {
                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "\\if":
                        Match defined = DefinedTest().Match(parts.Length > 1 ? parts[1] : string.Empty);
                        if (!defined.Success)
                        {
                            throw new NotSupportedException($"Unsupported psql condition: {trimmed}");
                        }

                        active.Push(on && values.ContainsKey(defined.Groups[1].Value));
                        break;
                    case "\\else":
                        bool current = active.Pop();
                        bool parent = active.Count == 0 || active.Peek();
                        active.Push(parent && !current);
                        break;
                    case "\\endif":
                        active.Pop();
                        break;
                    case "\\set":
                        if (on && parts.Length > 1 && parts[1] != "ON_ERROR_STOP")
                        {
                            string value = parts.Length > 2 ? string.Join(' ', parts[2..]) : string.Empty;
                            values[parts[1]] = value == "''" ? string.Empty : value;
                        }

                        break;
                    default:
                        throw new NotSupportedException($"Unsupported psql command: {trimmed}");
                }

                continue;
            }

            if (!on)
            {
                continue;
            }

            sql.Append(QuotedVariable().Replace(line, m =>
                values.TryGetValue(m.Groups[1].Value, out string? value)
                    ? "'" + value.Replace("'", "''") + "'"
                    : throw new InvalidOperationException($"psql variable {m.Groups[1].Value} is not set."))).Append('\n');
        }

        return sql.ToString();
    }

    /// <summary>Renders and executes a repo-relative script on <paramref name="connectionString"/>.</summary>
    public static async Task RunAsync(string connectionString, string repoRelativePath, IReadOnlyDictionary<string, string> variables)
    {
        string sql = Render(Read(repoRelativePath), variables);
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [GeneratedRegex(@"^:\{\?(\w+)\}$")]
    private static partial Regex DefinedTest();

    [GeneratedRegex(@":'(\w+)'")]
    private static partial Regex QuotedVariable();
}
