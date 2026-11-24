using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// Two writers of one assistant thread can both compute "max + 1". The unique index lets one win; the other must
/// re-read and land after it, never lose its turn and never reuse a sequence.
/// </summary>
public sealed class AssistantMessageAppenderTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly RivalWriter _rival = new();
    private readonly InferenceApiDbContext _db;
    private readonly AssistantThread _thread;

    public AssistantMessageAppenderTests()
    {
        _connection.Open();
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_rival)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options);
        _db.Database.EnsureCreated();
        using (SqliteCommand pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        _thread = new AssistantThread { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        _db.AssistantThreads.Add(_thread);
        _db.SaveChanges();
        _rival.Connection = _connection;
        _rival.ThreadId = _thread.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Appends_take_consecutive_sequences()
    {
        await AssistantMessageAppender.AppendAsync(_db, _thread, "user", "a", CancellationToken.None);
        await AssistantMessageAppender.AppendAsync(_db, _thread, "assistant", "b", CancellationToken.None);

        _db.AssistantMessages.Select(m => m.Sequence).OrderBy(s => s).ToList().Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_lost_race_re_reads_the_maximum_and_keeps_the_turn()
    {
        _rival.Remaining = 1; // a rival takes sequence 1 between our read and our save

        AssistantMessage turn = await AssistantMessageAppender.AppendAsync(_db, _thread, "assistant", "reply", CancellationToken.None);

        turn.Sequence.Should().Be(2);
        _db.AssistantMessages.AsNoTracking().OrderBy(m => m.Sequence).Select(m => m.Role).ToList()
            .Should().Equal("user", "assistant");
    }

    [Fact]
    public async Task A_conflict_that_never_clears_is_thrown_not_swallowed()
    {
        _rival.Remaining = int.MaxValue;

        Func<Task> act = () => AssistantMessageAppender.AppendAsync(_db, _thread, "assistant", "reply", CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        _db.AssistantMessages.AsNoTracking().Any(m => m.Role == "assistant").Should().BeFalse();
    }

    /// <summary>Inserts a competing message with the sequence the save is about to use, on the same connection.</summary>
    private sealed class RivalWriter : SaveChangesInterceptor
    {
        public SqliteConnection? Connection;
        public Guid ThreadId;
        public int Remaining;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            AssistantMessage? mine = eventData.Context?.ChangeTracker.Entries<AssistantMessage>()
                .FirstOrDefault(e => e.State == EntityState.Added)?.Entity;
            if (mine is not null && Remaining > 0 && Connection is not null)
            {
                Remaining--;
                using SqliteCommand cmd = Connection.CreateCommand();
                cmd.CommandText = "INSERT INTO assistant_messages (id, thread_id, role, content, sequence, created_at) VALUES ($id, $t, 'user', 'x', $s, $c)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                cmd.Parameters.AddWithValue("$t", ThreadId.ToString().ToUpperInvariant());
                cmd.Parameters.AddWithValue("$s", mine.Sequence);
                cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
