using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>DS-5: library names are unique per organization (409, not a 500) and an organization has a library cap.</summary>
public sealed class OrgLibraryNamingTests : IDisposable
{
    private static readonly Guid Org = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000d5");

    private readonly TestTenancy.MutableTenant _tenant = new();
    private readonly InferenceApiDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();

    public OrgLibraryNamingTests()
    {
        _db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"org-library-naming-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            _tenant);
        _tenant.ActAsOrganization(Org);
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = Org;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
    }

    private OrgDesignLibrariesController Controller(int? cap = null)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(cap is { } c
                ? new Dictionary<string, string?> { ["DesignSystem:Library:MaxPerOrganization"] = c.ToString() }
                : new Dictionary<string, string?>())
            .Build();
        return new OrgDesignLibrariesController(
            _db, new FakeUser(_userId),
            new DesignSystemService(
                _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(), NullLogger<DesignSystemService>.Instance),
            NullLogger<OrgDesignLibrariesController>.Instance,
            config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static string Code(object? value) =>
        value!.GetType().GetProperty("code")!.GetValue(value)!.ToString()!;

    private async Task<Guid> CreateAsync(OrgDesignLibrariesController controller, string name)
    {
        ActionResult<LibrarySummaryResponse> created = await controller.Create(
            new CreateLibraryRequest(name, null), CancellationToken.None);
        return created.Result.Should().BeOfType<ObjectResult>().Subject.Value
            .Should().BeOfType<LibrarySummaryResponse>().Subject.Id;
    }

    [Fact]
    public async Task Creating_a_second_library_with_the_same_name_is_409_library_name_taken()
    {
        OrgDesignLibrariesController controller = Controller();
        await CreateAsync(controller, "Acme");

        ActionResult<LibrarySummaryResponse> second = await controller.Create(
            new CreateLibraryRequest("Acme", null), CancellationToken.None);

        ConflictObjectResult conflict = second.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        Code(conflict.Value).Should().Be("library_name_taken");
        _db.DesignSystemLibraries.Count().Should().Be(1);
        _db.DesignSystems.Count().Should().Be(1, "a refused create leaves no orphan container");
    }

    [Fact]
    public async Task Renaming_onto_another_librarys_name_is_409_library_name_taken()
    {
        OrgDesignLibrariesController controller = Controller();
        await CreateAsync(controller, "Acme");
        Guid other = await CreateAsync(controller, "Other");

        ActionResult<LibrarySummaryResponse> rename = await controller.Rename(
            other, new RenameLibraryRequest("Acme"), CancellationToken.None);

        Code(rename.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value).Should().Be("library_name_taken");
        (await controller.Rename(other, new RenameLibraryRequest("Other"), CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>("renaming a library to its own current name is not a clash");
    }

    [Fact]
    public async Task An_organization_cannot_exceed_the_library_cap()
    {
        OrgDesignLibrariesController controller = Controller(cap: 2);
        await CreateAsync(controller, "One");
        await CreateAsync(controller, "Two");

        ActionResult<LibrarySummaryResponse> third = await controller.Create(
            new CreateLibraryRequest("Three", null), CancellationToken.None);

        Code(third.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value).Should().Be("library_limit_reached");
        _db.DesignSystemLibraries.Count().Should().Be(2);
    }

    // -----------------------------------------------------------------------------------------------
    // The pre-check is racy: two requests can both pass AnyAsync. The unique index decides, and the
    // loser must see 409 library_name_taken, not an unhandled DbUpdateException (500).
    // -----------------------------------------------------------------------------------------------

    /// <summary>On the first save after <see cref="Arm"/>, slips in a second library with the same name, as a racing request would.</summary>
    private sealed class RacingInsertInterceptor(Guid orgId, string name) : SaveChangesInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed && eventData.Context is InferenceApiDbContext db)
            {
                _armed = false;
                db.DesignSystemLibraries.Add(new DesignSystemLibrary
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = orgId,
                    Name = name,
                    DesignSystemId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private (SqliteConnection Connection, InferenceApiDbContext Db, RacingInsertInterceptor Race) SqliteWithRace(string racingName)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        var race = new RacingInsertInterceptor(Org, racingName);
        var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(race)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options,
            _tenant);
        db.Database.EnsureCreated();
        return (connection, db, race);
    }

    private OrgDesignLibrariesController ControllerOn(InferenceApiDbContext db) =>
        new(
            db, new FakeUser(_userId),
            new DesignSystemService(
                db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(), NullLogger<DesignSystemService>.Instance),
            NullLogger<OrgDesignLibrariesController>.Instance,
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task A_create_that_loses_the_unique_index_race_is_409_not_a_500()
    {
        (SqliteConnection connection, InferenceApiDbContext db, RacingInsertInterceptor race) = SqliteWithRace("Acme");
        using (connection)
        using (db)
        {
            OrgDesignLibrariesController controller = ControllerOn(db);
            race.Arm();

            ActionResult<LibrarySummaryResponse> result = await controller.Create(
                new CreateLibraryRequest("Acme", null), CancellationToken.None);

            Code(result.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value).Should().Be("library_name_taken");
            db.ChangeTracker.Clear();
            db.DesignSystemLibraries.Count().Should().Be(0, "the losing transaction rolled back");
            db.DesignSystems.Count().Should().Be(0);
        }
    }

    [Fact]
    public async Task A_rename_that_loses_the_unique_index_race_is_409_not_a_500()
    {
        (SqliteConnection connection, InferenceApiDbContext db, RacingInsertInterceptor race) = SqliteWithRace("Taken");
        using (connection)
        using (db)
        {
            OrgDesignLibrariesController controller = ControllerOn(db);
            ActionResult<LibrarySummaryResponse> created = await controller.Create(
                new CreateLibraryRequest("Mine", null), CancellationToken.None);
            Guid id = created.Result.Should().BeOfType<ObjectResult>().Subject.Value
                .Should().BeOfType<LibrarySummaryResponse>().Subject.Id;
            race.Arm();

            ActionResult<LibrarySummaryResponse> result = await controller.Rename(
                id, new RenameLibraryRequest("Taken"), CancellationToken.None);

            Code(result.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value).Should().Be("library_name_taken");
        }
    }
}
