using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// Tests for <see cref="McpOAuthPurgeService"/> — the sweep that keeps the four MCP OAuth tables
/// bounded (docs/design/mcp-oauth.md §5). A real SQLite database, not the in-memory provider, because
/// the sweep deletes through <c>ExecuteDelete</c>, which the in-memory provider does not implement.
/// </summary>
public class McpOAuthPurgeServiceTests
{
    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static (SqliteConnection Connection, InferenceApiDbContext Db) CreateContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new InferenceApiDbContext(options);
        db.Database.EnsureCreated();
        return (connection, db);
    }

    /// <summary>
    /// Builds the sweep over <paramref name="db"/> and runs <paramref name="body"/> with it. The service
    /// opens its own scope per sweep, so the test needs a real scope factory — and the container owns
    /// what it resolves, so the provider is disposed only AFTER the body's assertions have run.
    /// </summary>
    private static async Task WithServiceAsync(
        InferenceApiDbContext db,
        Func<McpOAuthPurgeService, Task> body,
        McpOAuthPurgeOptions? options = null)
    {
        var services = new ServiceCollection();
        // ROOT-level, not scoped: the sweep disposes the child scope it opens, and a scoped
        // registration would take the test's own context down with it mid-test. A singleton is
        // disposed when the root provider is, i.e. after the body has finished asserting.
        services.AddSingleton(_ => db);
        using ServiceProvider provider = services.BuildServiceProvider();

        var service = new McpOAuthPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? new McpOAuthPurgeOptions()),
            NullLogger<McpOAuthPurgeService>.Instance);

        await body(service);
    }

    private static OAuthClient NewClient(string clientId, DateTime createdAt, DateTime? lastUsedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ClientName = clientId,
        RedirectUris = ["https://client.example/callback"],
        GrantTypes = ["authorization_code"],
        ResponseTypes = ["code"],
        TokenEndpointAuthMethod = "none",
        CreatedAt = createdAt,
        LastUsedAt = lastUsedAt
    };

    private static OAuthAuthorizationCode NewCode(
        string clientId, DateTime expiresAt, DateTime? consumedAt = null, Guid? userId = null) => new()
        {
            Id = Guid.NewGuid(),
            CodeHash = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            UserId = userId ?? Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            RedirectUri = "https://client.example/callback",
            CodeChallenge = "challenge",
            Scope = "assistant.tools",
            ExpiresAt = expiresAt,
            ConsumedAt = consumedAt
        };

    private static OAuthRefreshToken NewToken(
        string clientId, DateTime expiresAt, DateTime? revokedAt = null, Guid? userId = null) => new()
        {
            Id = Guid.NewGuid(),
            TokenHash = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            UserId = userId ?? Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            Scope = "assistant.tools",
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt
        };

    // ---------------------------------------------------------------------
    // Authorization codes
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Purge_removes_expired_and_consumed_codes_and_keeps_live_ones()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime now = DateTime.UtcNow;
            db.OAuthClients.Add(NewClient("client", now.AddDays(-1), now.AddHours(-1)));
            db.OAuthAuthorizationCodes.AddRange(
                NewCode("client", expiresAt: now.AddMinutes(-1)),                                  // expired
                NewCode("client", expiresAt: now.AddMinutes(5), consumedAt: now.AddSeconds(-30)),   // consumed
                NewCode("client", expiresAt: now.AddMinutes(5)));                                   // live
            await db.SaveChangesAsync();

            await WithServiceAsync(db, async service =>
            {
                McpOAuthPurgeResult result = await service.PurgeOnceAsync(now);

                result.AuthorizationCodesDeleted.Should().Be(2);
                result.RefreshTokensDeleted.Should().Be(0);
                result.ClientsDeleted.Should().Be(0);

                OAuthAuthorizationCode survivor = await db.OAuthAuthorizationCodes.SingleAsync();
                survivor.ConsumedAt.Should().BeNull();
                survivor.ExpiresAt.Should().BeAfter(now);
            });
        }
    }

    // ---------------------------------------------------------------------
    // Refresh tokens
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Purge_removes_expired_and_grace_aged_revoked_tokens_and_keeps_the_rest()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime now = DateTime.UtcNow;
            db.OAuthClients.Add(NewClient("client", now.AddDays(-1), now.AddHours(-1)));
            db.OAuthRefreshTokens.AddRange(
                NewToken("client", expiresAt: now.AddHours(-1)),                                // expired
                NewToken("client", expiresAt: now.AddDays(10), revokedAt: now.AddMinutes(-1)),   // revoked just now
                NewToken("client", expiresAt: now.AddDays(10), revokedAt: now.AddDays(-2)),      // revoked past the grace
                NewToken("client", expiresAt: now.AddDays(10)));                                 // live
            await db.SaveChangesAsync();

            await WithServiceAsync(db, async service =>
            {
                McpOAuthPurgeResult result = await service.PurgeOnceAsync(now);

                result.RefreshTokensDeleted.Should().Be(2);

                // A just-rotated token is KEPT: finding it is what revokes the whole grant when it is
                // replayed (docs/design/mcp-oauth.md §4).
                List<OAuthRefreshToken> remaining = await db.OAuthRefreshTokens.ToListAsync();
                remaining.Should().HaveCount(2);
                remaining.Should().Contain(t => t.RevokedAt != null && t.ExpiresAt > now,
                    "a token revoked inside the grace window is what reuse detection looks up");
                remaining.Should().Contain(t => t.RevokedAt == null && t.ExpiresAt > now);
            });
        }
    }

    // ---------------------------------------------------------------------
    // Unclaimed clients
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Purge_removes_unclaimed_clients_past_the_window_and_keeps_used_or_recent_ones()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime now = DateTime.UtcNow;
            db.OAuthClients.AddRange(
                NewClient("abandoned", createdAt: now.AddDays(-10)),                                   // unclaimed, past the 7-day window
                NewClient("fresh", createdAt: now.AddDays(-1)),                                        // unclaimed, inside it
                NewClient("in-use", createdAt: now.AddDays(-10), lastUsedAt: now.AddDays(-9)));        // claimed
            await db.SaveChangesAsync();

            await WithServiceAsync(db, async service =>
            {
                McpOAuthPurgeResult result = await service.PurgeOnceAsync(now);

                result.ClientsDeleted.Should().Be(1);
                (await db.OAuthClients.Select(c => c.ClientId).ToListAsync())
                    .Should().BeEquivalentTo(["fresh", "in-use"]);
            });
        }
    }

    [Fact]
    public async Task Purge_respects_a_configured_client_retention_window()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime now = DateTime.UtcNow;
            db.OAuthClients.Add(NewClient("ten-days", createdAt: now.AddDays(-10)));
            await db.SaveChangesAsync();

            await WithServiceAsync(db, async service =>
            {
                McpOAuthPurgeResult result = await service.PurgeOnceAsync(now);

                result.ClientsDeleted.Should().Be(0, "the window is 30 days, not the 7-day default");
                (await db.OAuthClients.CountAsync()).Should().Be(1);
            }, new McpOAuthPurgeOptions { UnclaimedClientRetentionDays = 30 });
        }
    }

    [Fact]
    public async Task An_unclaimed_client_holding_a_live_credential_is_never_purged()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime now = DateTime.UtcNow;
            // Both look abandoned (last_used_at NULL, older than the window). The first really is; the
            // second holds a live refresh token, so the best-effort last_used_at stamp must not be
            // what decides it — deleting it would break a working MCP client.
            db.OAuthClients.AddRange(
                NewClient("truly-abandoned", createdAt: now.AddDays(-30)),
                NewClient("still-live", createdAt: now.AddDays(-30)));
            db.OAuthRefreshTokens.Add(NewToken("still-live", expiresAt: now.AddDays(10)));
            await db.SaveChangesAsync();

            await WithServiceAsync(db, async service =>
            {
                McpOAuthPurgeResult result = await service.PurgeOnceAsync(now);

                result.ClientsDeleted.Should().Be(1);
                (await db.OAuthClients.Select(c => c.ClientId).ToListAsync())
                    .Should().BeEquivalentTo(["still-live"]);
            });
        }
    }

    // ---------------------------------------------------------------------
    // Across runs: idempotence and a row that expires between two sweeps
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_code_that_expires_between_two_runs_is_caught_by_the_second()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime firstSweep = DateTime.UtcNow;
            db.OAuthClients.Add(NewClient("client", createdAt: firstSweep.AddDays(-1), lastUsedAt: firstSweep));
            db.OAuthAuthorizationCodes.Add(NewCode("client", expiresAt: firstSweep.AddMinutes(30)));
            await db.SaveChangesAsync();

            await WithServiceAsync(db, async service =>
            {
                McpOAuthPurgeResult first = await service.PurgeOnceAsync(firstSweep);
                first.AuthorizationCodesDeleted.Should().Be(0, "the code is still valid at the first sweep");
                (await db.OAuthAuthorizationCodes.CountAsync()).Should().Be(1);

                McpOAuthPurgeResult second = await service.PurgeOnceAsync(firstSweep.AddHours(1));
                second.AuthorizationCodesDeleted.Should().Be(1, "the hour that passed expired it");
                (await db.OAuthAuthorizationCodes.CountAsync()).Should().Be(0);
            });
        }
    }

    [Fact]
    public async Task Purge_is_idempotent_and_a_second_run_finds_nothing_left()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime now = DateTime.UtcNow;
            db.OAuthAuthorizationCodes.Add(NewCode("client", expiresAt: now.AddMinutes(-5)));
            db.OAuthRefreshTokens.Add(NewToken("client", expiresAt: now.AddHours(-1)));
            db.OAuthClients.Add(NewClient("abandoned", createdAt: now.AddDays(-30)));
            await db.SaveChangesAsync();

            await WithServiceAsync(db, async service =>
            {
                McpOAuthPurgeResult first = await service.PurgeOnceAsync(now);
                first.Total.Should().Be(3);
                first.AuthorizationCodesDeleted.Should().Be(1);
                first.RefreshTokensDeleted.Should().Be(1);
                first.ClientsDeleted.Should().Be(1);

                McpOAuthPurgeResult second = await service.PurgeOnceAsync(now);
                second.Total.Should().Be(0, "the sweep deletes nothing twice");
            });
        }
    }

    [Fact]
    public async Task A_sweep_is_bounded_and_the_next_one_continues()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < 3; i++)
                db.OAuthAuthorizationCodes.Add(NewCode("client", expiresAt: now.AddMinutes(-1 - i)));
            await db.SaveChangesAsync();

            // At most 2 rows per table per sweep, one per statement: the backlog is reclaimed over
            // several runs rather than in one long delete.
            await WithServiceAsync(db, async service =>
            {
                (await service.PurgeOnceAsync(now)).AuthorizationCodesDeleted.Should().Be(2);
                (await db.OAuthAuthorizationCodes.CountAsync()).Should().Be(1);

                (await service.PurgeOnceAsync(now)).AuthorizationCodesDeleted.Should().Be(1);
                (await db.OAuthAuthorizationCodes.CountAsync()).Should().Be(0);
            }, new McpOAuthPurgeOptions { BatchSize = 1, MaxRowsPerTablePerRun = 2 });
        }
    }

    [Fact]
    public async Task The_sweep_leaves_unrelated_tables_alone()
    {
        var (connection, db) = CreateContext();
        using (connection)
        using (db)
        {
            DateTime now = DateTime.UtcNow;
            db.McpToolCalls.Add(new McpToolCall
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                ToolName = "ListStepSchemas",
                CalledAt = now.AddDays(-90),
                Succeeded = true,
                DurationMs = 3
            });
            db.OAuthClients.Add(NewClient("abandoned", createdAt: now.AddDays(-90)));
            await db.SaveChangesAsync();

            await WithServiceAsync(db, async service =>
            {
                await service.PurgeOnceAsync(now);

                // mcp_tool_calls is the usage record: it is history an operator reads, never swept.
                (await db.McpToolCalls.CountAsync()).Should().Be(1);
                (await db.OAuthClients.CountAsync()).Should().Be(0);
            });
        }
    }
}
