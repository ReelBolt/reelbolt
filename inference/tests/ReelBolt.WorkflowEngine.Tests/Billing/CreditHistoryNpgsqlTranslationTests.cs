#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The hold and display history queries group per step result, take Max(OccurredAt), order by it
/// descending (id breaking ties) and cap with Take. The InMemory provider evaluates all of that in
/// LINQ-to-objects, so it cannot tell whether EF can translate it for PostgreSQL. These tests build the
/// real Npgsql contexts (both call sites: the Inference API's and the WorkflowEngine's) against an
/// unused host, suppress the connection open and the command execution with interceptors, and capture
/// the SQL EF generated: an untranslatable query throws InvalidOperationException before any command
/// is built. No database or container is involved.
/// </summary>
public sealed class CreditHistoryNpgsqlTranslationTests(ITestOutputHelper output)
{
    private const int Cap = 1234;
    private static readonly Guid Org = Guid.Parse("0a0a0a0a-0000-4000-8000-00000000000a");
    private static readonly DateTime Since = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HoldQuery_TranslatesOnNpgsql_ForTheInferenceApiContext()
    {
        SqlCapture capture = new();
        await using InferenceApiDbContext db = new(Options<InferenceApiDbContext>(capture));

        IReadOnlyList<StepCreditSample> rows = await StepCreditHistoryQuery.LoadAsync(
            db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions,
            Org, Since, Cap, CancellationToken.None);

        rows.Should().BeEmpty();
        AssertOrderedAndCapped(capture, output);
    }

    [Fact]
    public async Task HoldQuery_TranslatesOnNpgsql_ForTheWorkflowEngineContext()
    {
        SqlCapture capture = new();
        await using WorkflowEngineDbContext db = new(Options<WorkflowEngineDbContext>(capture));

        IReadOnlyList<StepCreditSample> rows = await StepCreditHistoryQuery.LoadAsync(
            db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions,
            Org, Since, Cap, CancellationToken.None);

        rows.Should().BeEmpty();
        AssertOrderedAndCapped(capture, output);
    }

    [Fact]
    public async Task DisplayQuery_TranslatesOnNpgsql_ForTheInferenceApiContext()
    {
        SqlCapture capture = new();
        await using InferenceApiDbContext db = new(Options<InferenceApiDbContext>(capture));

        IReadOnlyList<DisplayStepSample> rows = await DisplayCreditHistory.LoadStepSamplesAsync(
            db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions, db.AgentDefinitions,
            Org, Since, Cap, CancellationToken.None);

        rows.Should().BeEmpty();
        AssertOrderedAndCapped(capture, output);
    }

    private static DbContextOptions<TContext> Options<TContext>(SqlCapture capture)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql("Host=unused")
            .AddInterceptors(new SuppressOpen(), capture)
            .Options;

    private static void AssertOrderedAndCapped(SqlCapture capture, ITestOutputHelper output)
    {
        capture.Commands.Should().ContainSingle("the history query is one SQL statement");
        string sql = capture.Commands[0];
        output.WriteLine(sql);
        sql.Should().Contain("GROUP BY");
        sql.Should().MatchRegex(@"ORDER BY max\([^)]*occurred_at\) DESC", "the newest samples come first, ordered in SQL");
        sql.Should().MatchRegex(@"LIMIT @\w+", "the cap is applied in SQL");
        capture.Parameters.Should().Contain(Cap, "the LIMIT parameter carries the cap");
    }

    /// <summary>Never opens a socket: the connection reports itself opened.</summary>
    private sealed class SuppressOpen : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    /// <summary>Records each command's SQL and parameters and answers it with an empty reader.</summary>
    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public List<object?> Parameters { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader());
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(
                InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader()));
        }

        private void Record(DbCommand command)
        {
            Commands.Add(command.CommandText);
            foreach (DbParameter p in command.Parameters)
            {
                Parameters.Add(p.Value);
            }
        }
    }
}
