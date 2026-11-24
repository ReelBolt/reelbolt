using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using System;
using System.Linq;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// Model-level coverage for the server-persisted assistant threads (platform-assistant T-001).
///
/// A relational provider is used rather than InMemory on purpose: InMemory enforces neither
/// indexes nor cascade deletes, so it cannot answer any of the questions this file asks. Sqlite
/// is an in-memory database with real constraints, so the unique index and the cascade are
/// asserted against actual enforcement, and the column types / index shapes are asserted against
/// the model metadata the migration is generated from.
/// </summary>
public class AssistantThreadModelTests
{
    private static (SqliteConnection Connection, InferenceApiDbContext Context) NewContext()
    {
        SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .Options;

        InferenceApiDbContext context = new InferenceApiDbContext(options);
        context.Database.EnsureCreated();

        return (connection, context);
    }

    /// <summary>
    /// Sqlite enforces the user foreign key, so a thread must have a real owner row.
    /// </summary>
    private static Guid AddUser(InferenceApiDbContext db, string email = "owner@example.com")
    {
        ApplicationUser user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Owner"
        };

        db.ApplicationUsers.Add(user);

        // A thread must belong to an organization (FK enforced by Sqlite); the user's Personal
        // org reuses the user's id so NewThread can derive it without a lookup.
        db.Organizations.Add(new Organization
        {
            Id = user.Id,
            Name = "Personal",
            Slug = "personal-" + user.Id.ToString("N"),
            Kind = ReelBolt.Shared.Tenancy.OrganizationKind.Personal,
            CreatedByUserId = user.Id
        });
        db.SaveChanges();

        return user.Id;
    }

    private static AssistantThread NewThread(Guid userId, params AssistantMessage[] messages)
    {
        AssistantThread thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = userId,
            Title = "New chat"
        };

        foreach (AssistantMessage message in messages)
        {
            thread.Messages.Add(message);
        }

        return thread;
    }

    // --- Existence, shape and registration ---

    [Fact]
    public void AssistantThread_IsOwnedByExactlyOneUser_AndCascadesToMessages()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            Guid userId = AddUser(db);
            AssistantThread thread = NewThread(userId, new AssistantMessage
            {
                Id = Guid.NewGuid(),
                Role = "user",
                Content = "hi",
                Sequence = 1
            });

            db.AssistantThreads.Add(thread);
            db.SaveChanges();

            db.AssistantThreads.Single().UserId.Should().Be(userId);
            db.AssistantMessages.Where(m => m.ThreadId == thread.Id).Should().ContainSingle();
        }
    }

    [Fact]
    public void Both_DbSets_Are_Registered_On_The_Inference_Context()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            db.AssistantThreads.Should().NotBeNull();
            db.AssistantMessages.Should().NotBeNull();

            db.Model.FindEntityType(typeof(AssistantThread)).Should().NotBeNull();
            db.Model.FindEntityType(typeof(AssistantMessage)).Should().NotBeNull();
        }
    }

    [Fact]
    public void Tables_Are_Named_AssistantThreads_And_AssistantMessages()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            db.Model.FindEntityType(typeof(AssistantThread))!.GetTableName().Should().Be("assistant_threads");
            db.Model.FindEntityType(typeof(AssistantMessage))!.GetTableName().Should().Be("assistant_messages");
        }
    }

    [Fact]
    public void Defaults_Match_The_Pinned_Shape()
    {
        AssistantThread thread = new AssistantThread();
        AssistantMessage message = new AssistantMessage();

        thread.Title.Should().Be("New chat");
        thread.ProjectId.Should().BeNull();
        thread.ArchivedAt.Should().BeNull();
        thread.Messages.Should().NotBeNull().And.BeEmpty();

        message.Role.Should().BeEmpty();
        message.Content.Should().BeEmpty();
        message.MetadataJson.Should().BeNull();

        // CreatedAt/UpdatedAt follow Project: non-default (i.e. auto-populated) on construction.
        thread.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        thread.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        message.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // --- Column types ---

    [Fact]
    public void AssistantMessage_Content_Is_Text_And_MetadataJson_Is_Nullable_Jsonb()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            var entityType = db.Model.FindEntityType(typeof(AssistantMessage));
            entityType.Should().NotBeNull();

            var content = entityType!.FindProperty(nameof(AssistantMessage.Content));
            content.Should().NotBeNull();
            content!.GetColumnType().Should().Be("text");
            content.IsNullable.Should().BeFalse();

            var metadata = entityType.FindProperty(nameof(AssistantMessage.MetadataJson));
            metadata.Should().NotBeNull();
            metadata!.GetColumnType().Should().Be("jsonb");
            metadata.IsNullable.Should().BeTrue();
        }
    }

    [Fact]
    public void AssistantThread_ProjectId_Is_Nullable_And_Has_No_ForeignKey_To_Projects()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            var entityType = db.Model.FindEntityType(typeof(AssistantThread));
            entityType.Should().NotBeNull();

            var projectId = entityType!.FindProperty(nameof(AssistantThread.ProjectId));
            projectId.Should().NotBeNull();
            projectId!.IsNullable.Should().BeTrue();

            // A thread survives project deletion: deliberately no FK, so Postgres cannot cascade
            // or restrict on it.
            entityType.GetForeignKeys()
                .Should().NotContain(fk => fk.PrincipalEntityType.ClrType == typeof(Project));
        }
    }

    // --- Indexes ---

    [Fact]
    public void AssistantMessage_Has_A_Unique_Index_On_ThreadId_And_Sequence()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            var entityType = db.Model.FindEntityType(typeof(AssistantMessage));
            entityType.Should().NotBeNull();

            entityType!.GetIndexes().Should().Contain(i =>
                i.IsUnique &&
                i.Properties.Select(p => p.Name).SequenceEqual(new[]
                {
                    nameof(AssistantMessage.ThreadId),
                    nameof(AssistantMessage.Sequence)
                }));
        }
    }

    [Fact]
    public void AssistantThread_Has_A_NonUnique_Index_On_UserId()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            var entityType = db.Model.FindEntityType(typeof(AssistantThread));
            entityType.Should().NotBeNull();

            var index = entityType!.GetIndexes().SingleOrDefault(i =>
                i.Properties.Count == 1 &&
                i.Properties[0].Name == nameof(AssistantThread.UserId));

            index.Should().NotBeNull(because: "listing a user's threads is the hot path");
            index!.IsUnique.Should().BeFalse();
        }
    }

    // --- Relationships ---

    [Fact]
    public void AssistantThread_UserForeignKey_Cascades_And_Message_ThreadForeignKey_Cascades()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            var threadFk = db.Model.FindEntityType(typeof(AssistantThread))!
                .GetForeignKeys()
                .Single(fk => fk.PrincipalEntityType.ClrType == typeof(ApplicationUser));
            threadFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

            var messageFk = db.Model.FindEntityType(typeof(AssistantMessage))!
                .GetForeignKeys()
                .Single(fk => fk.PrincipalEntityType.ClrType == typeof(AssistantThread));
            messageFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
            messageFk.Properties.Select(p => p.Name)
                .Should().Equal(nameof(AssistantMessage.ThreadId));
        }
    }

    [Fact]
    public void Messages_Navigation_Is_Loaded_Via_Include()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            Guid userId = AddUser(db);
            AssistantThread thread = NewThread(userId,
                new AssistantMessage { Id = Guid.NewGuid(), Role = "user", Content = "hi", Sequence = 1 },
                new AssistantMessage { Id = Guid.NewGuid(), Role = "assistant", Content = "hello", Sequence = 2 });

            db.AssistantThreads.Add(thread);
            db.SaveChanges();
            db.ChangeTracker.Clear();

            AssistantThread reloaded = db.AssistantThreads
                .Include(t => t.Messages)
                .Single(t => t.Id == thread.Id);

            reloaded.Messages.Should().HaveCount(2);
            reloaded.Messages.Select(m => m.Sequence).Should().BeEquivalentTo(new[] { 1, 2 });
        }
    }

    // --- Real enforcement (not just metadata) ---

    [Fact]
    public void Deleting_A_Thread_Deletes_Its_Messages()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            Guid userId = AddUser(db);
            AssistantThread thread = NewThread(userId,
                new AssistantMessage { Id = Guid.NewGuid(), Role = "user", Content = "hi", Sequence = 1 },
                new AssistantMessage { Id = Guid.NewGuid(), Role = "assistant", Content = "hello", Sequence = 2 });

            db.AssistantThreads.Add(thread);

            AssistantThread? other = NewThread(userId,
                new AssistantMessage { Id = Guid.NewGuid(), Role = "user", Content = "other", Sequence = 1 });
            db.AssistantThreads.Add(other);

            db.SaveChanges();

            db.AssistantThreads.Remove(thread);
            db.SaveChanges();

            db.AssistantMessages.Where(m => m.ThreadId == thread.Id)
                .Should().BeEmpty(because: "messages cascade with their thread");
            db.AssistantMessages.Where(m => m.ThreadId == other.Id).Should().ContainSingle();

            // ChangeTracker agrees: nothing orphaned is still being tracked.
            db.ChangeTracker.Entries<AssistantMessage>()
                .Should().NotContain(e => e.Entity.ThreadId == thread.Id);
        }
    }

    [Fact]
    public void Deleting_The_Owner_User_Deletes_Their_Threads()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            Guid userId = AddUser(db, "cascade@example.com");

            AssistantThread thread = NewThread(userId,
                new AssistantMessage { Id = Guid.NewGuid(), Role = "user", Content = "hi", Sequence = 1 });
            db.AssistantThreads.Add(thread);
            db.SaveChanges();

            ApplicationUser user = db.ApplicationUsers.Single(u => u.Id == userId);
            db.ApplicationUsers.Remove(user);
            db.SaveChanges();

            db.AssistantThreads.Should().BeEmpty();
            db.AssistantMessages.Should().BeEmpty();
        }
    }

    [Fact]
    public void The_ThreadId_Sequence_Index_Is_Actually_Enforced_By_The_Database()
    {
        var (connection, db) = NewContext();
        using (connection)
        using (db)
        {
            Guid userId = AddUser(db);
            AssistantThread thread = NewThread(userId,
                new AssistantMessage { Id = Guid.NewGuid(), Role = "user", Content = "one", Sequence = 1 });
            db.AssistantThreads.Add(thread);
            db.SaveChanges();

            // A second thread may reuse sequence 1 - the index is per-thread, not global.
            AssistantThread sibling = NewThread(userId,
                new AssistantMessage { Id = Guid.NewGuid(), Role = "user", Content = "one", Sequence = 1 });
            db.AssistantThreads.Add(sibling);
            db.SaveChanges();

            // The same sequence inside the same thread may not.
            db.AssistantMessages.Add(new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Role = "assistant",
                Content = "duplicate sequence",
                Sequence = 1
            });

            Action save = () => db.SaveChanges();
            save.Should().Throw<DbUpdateException>();
        }
    }
}
