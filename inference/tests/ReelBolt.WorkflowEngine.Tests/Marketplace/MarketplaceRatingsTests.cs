using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplaceRatingsTests
{
    private sealed class FakeUser(Guid org) : ICurrentUser
    {
        public Guid UserId { get; set; } = Guid.NewGuid();
        public string Email => "rater@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; set; } = org;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
        public bool EmailVerified { get; set; } = true;
    }

    private sealed class Harness
    {
        public readonly string DbName = Guid.NewGuid().ToString();
        public readonly InferenceApiDbContext Db;
        public readonly MarketplaceOptions Options = new() { Enabled = true };
        public readonly Guid AuthorOrg = Guid.NewGuid();
        public readonly Guid Org = Guid.NewGuid();
        public readonly Guid RecipeId = Guid.NewGuid();
        public readonly Guid ProjectId = Guid.NewGuid();
        public readonly Guid WorkflowId = Guid.NewGuid();
        public readonly FakeUser User;

        public Harness()
        {
            Db = NewDb();
            User = new FakeUser(Org);
            Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = RecipeId, AuthorOrgId = AuthorOrg, Status = RecipeStatus.Listed, Name = "r" });
            Db.Projects.Add(new Project { Id = ProjectId, Name = "p", OwnerId = User.UserId, OrganizationId = Org });
            Db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = WorkflowId,
                ProjectId = ProjectId,
                Name = "copy",
                TemplateKey = "market:" + RecipeId.ToString("N"),
                CreatedVia = WorkflowCreatedVia.Marketplace
            });
            Db.SaveChanges();
        }

        public InferenceApiDbContext NewDb() =>
            new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(DbName).Options);

        public void Copied() { Db.MarketplaceCopies.Add(new MarketplaceCopy { Id = Guid.NewGuid(), RecipeId = RecipeId, VersionId = Guid.NewGuid(), OrgId = Org, WorkflowId = WorkflowId }); Db.SaveChanges(); }

        public void Run(ExecutionStatus status, Guid? org = null, Guid? workflow = null)
        {
            Db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow ?? WorkflowId,
                ProjectId = ProjectId,
                OrganizationId = org ?? Org,
                Status = status
            });
            Db.SaveChanges();
        }

        public MarketplaceRatingService Service(InferenceApiDbContext? db = null, FakeUser? user = null) =>
            new(db ?? Db, user ?? User, Microsoft.Extensions.Options.Options.Create(Options));
    }

    private static T Ok<T>(MarketplaceResult<T> r)
    {
        r.Error.Should().BeNull();
        return r.Value!;
    }

    [Fact]
    public void Routes_sit_behind_the_flag_filter()
    {
        typeof(MarketplaceRatingsController).GetCustomAttributes(typeof(TypeFilterAttribute), true)
            .Cast<TypeFilterAttribute>().Should().Contain(a => a.ImplementationType == typeof(MarketplaceEnabledFilter));
    }

    [Fact]
    public async Task Flag_off_is_a_bare_404_for_both_actions()
    {
        Harness h = new();
        h.Options.Enabled = false;
        (await h.Service().RateAsync(h.RecipeId, 5, default)).Error.Should().BeEquivalentTo(new MarketplaceError(404));
        (await h.Service().MeAsync(h.RecipeId, default)).Error.Should().BeEquivalentTo(new MarketplaceError(404));
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(true, ExecutionStatus.Failed, false)]
    [InlineData(true, ExecutionStatus.Running, false)]
    [InlineData(true, ExecutionStatus.Cancelled, false)]
    [InlineData(true, ExecutionStatus.Passed, true)]
    public async Task Eligibility_needs_a_copy_and_a_passed_run(bool copied, ExecutionStatus? run, bool eligible)
    {
        Harness h = new();
        if (copied)
            h.Copied();
        if (run is { } status)
            h.Run(status);

        MarketplaceResult<RatingResponse> r = await h.Service().RateAsync(h.RecipeId, 4, default);
        if (eligible)
        {
            Ok(r).MyStars.Should().Be(4);
        }
        else
        {
            r.Error!.Status.Should().Be(403);
            r.Error.Code.Should().Be("RATING_NOT_ELIGIBLE");
            (await h.Db.MarketplaceRatings.CountAsync()).Should().Be(0);
        }

        (await h.Service().MeAsync(h.RecipeId, default)).Value!.CanRate.Should().Be(eligible);
    }

    [Fact]
    public async Task A_passed_run_of_another_organization_or_of_another_workflow_does_not_count()
    {
        Harness h = new();
        h.Copied();
        h.Run(ExecutionStatus.Passed, org: Guid.NewGuid());
        Guid other = Guid.NewGuid();
        h.Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = other, ProjectId = h.ProjectId, Name = "other", TemplateKey = "template-x" });
        h.Db.SaveChanges();
        h.Run(ExecutionStatus.Passed, workflow: other);

        (await h.Service().RateAsync(h.RecipeId, 3, default)).Error!.Code.Should().Be("RATING_NOT_ELIGIBLE");
    }

    [Fact]
    public async Task The_authors_own_organization_cannot_rate()
    {
        Harness h = new();
        FakeUser author = new(h.AuthorOrg);
        h.Db.MarketplaceCopies.Add(new MarketplaceCopy { Id = Guid.NewGuid(), RecipeId = h.RecipeId, VersionId = Guid.NewGuid(), OrgId = h.AuthorOrg });
        h.Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = h.ProjectId, Name = "own", TemplateKey = "market:" + h.RecipeId.ToString("N") });
        h.Db.SaveChanges();
        h.Run(ExecutionStatus.Passed, org: h.AuthorOrg);

        (await h.Service(user: author).RateAsync(h.RecipeId, 5, default)).Error!.Code.Should().Be("RATING_NOT_ELIGIBLE");
        (await h.Service(user: author).MeAsync(h.RecipeId, default)).Value!.CanRate.Should().BeFalse();
    }

    [Fact]
    public async Task The_author_cannot_rate_from_a_second_organization()
    {
        Harness h = new();
        Guid authorUser = Guid.NewGuid();
        (await h.Db.MarketplaceRecipes.SingleAsync()).AuthorUserId = authorUser;
        await h.Db.SaveChangesAsync();
        h.Copied();
        h.Run(ExecutionStatus.Passed);
        FakeUser author = new(h.Org) { UserId = authorUser };

        (await h.Service(user: author).RateAsync(h.RecipeId, 5, default)).Error!.Code.Should().Be("RATING_NOT_ELIGIBLE");
        (await h.Service(user: author).MeAsync(h.RecipeId, default)).Value!.CanRate.Should().BeFalse();
    }

    [Fact]
    public async Task A_passed_run_of_a_cut_down_copy_does_not_make_the_organization_eligible()
    {
        Harness h = new();
        (await h.Db.MarketplaceRecipes.SingleAsync()).StepCount = 3;
        h.Copied();
        h.Db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = h.WorkflowId, StepOrder = 1 });
        await h.Db.SaveChangesAsync();
        h.Run(ExecutionStatus.Passed);
        (await h.Service().RateAsync(h.RecipeId, 5, default)).Error!.Code.Should().Be("RATING_NOT_ELIGIBLE");

        h.Db.WorkflowSteps.AddRange(
            new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = h.WorkflowId, StepOrder = 2 },
            new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = h.WorkflowId, StepOrder = 3 });
        await h.Db.SaveChangesAsync();
        Ok(await h.Service().RateAsync(h.RecipeId, 5, default)).MyStars.Should().Be(5);
    }

    [Fact]
    public async Task Gates_unverified_email_unlisted_recipe_and_bad_stars()
    {
        Harness h = new();
        h.Copied();
        h.Run(ExecutionStatus.Passed);

        h.User.EmailVerified = false;
        (await h.Service().RateAsync(h.RecipeId, 4, default)).Error!.Code.Should().Be("EMAIL_NOT_VERIFIED");
        h.User.EmailVerified = true;

        foreach (int stars in new[] { 0, 6, -1 })
            (await h.Service().RateAsync(h.RecipeId, stars, default)).Error!.Status.Should().Be(400);

        MarketplaceRecipe r = await h.Db.MarketplaceRecipes.SingleAsync();
        r.Status = RecipeStatus.Unpublished;
        await h.Db.SaveChangesAsync();
        (await h.Service().RateAsync(h.RecipeId, 4, default)).Error.Should().BeEquivalentTo(new MarketplaceError(404));
        (await h.Service().RateAsync(Guid.NewGuid(), 4, default)).Error.Should().BeEquivalentTo(new MarketplaceError(404));
        (await h.Db.MarketplaceRatings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Upsert_keeps_sum_and_count_consistent_for_create_change_and_same_value()
    {
        Harness h = new();
        h.Copied();
        h.Run(ExecutionStatus.Passed);

        Ok(await h.Service().RateAsync(h.RecipeId, 4, default));
        await Totals(h, 4, 1);
        Ok(await h.Service().RateAsync(h.RecipeId, 4, default));
        await Totals(h, 4, 1);
        Ok(await h.Service().RateAsync(h.RecipeId, 1, default));
        await Totals(h, 1, 1);

        FakeUser second = new(h.Org);
        Ok(await h.Service(user: second).RateAsync(h.RecipeId, 5, default));
        await Totals(h, 6, 2);
        (await h.Db.MarketplaceRatings.CountAsync()).Should().Be(2);
        (await h.Service().MeAsync(h.RecipeId, default)).Value!.MyStars.Should().Be(1);
    }

    private static async Task Totals(Harness h, int sum, int count)
    {
        MarketplaceRecipe r = await h.NewDb().MarketplaceRecipes.SingleAsync();
        (r.RatingSum, r.RatingCount).Should().Be((sum, count));
    }

    [Fact]
    public async Task Average_shows_only_from_the_minimum_number_of_ratings()
    {
        Harness h = new();
        h.Copied();
        h.Run(ExecutionStatus.Passed);
        RatingResponse last = null!;
        for (int i = 0; i < 5; i++)
        {
            last = Ok(await h.Service(user: new FakeUser(h.Org)).RateAsync(h.RecipeId, i % 2 == 0 ? 5 : 4, default));
            if (i < 4)
                last.RatingAverage.Should().BeNull();
        }

        last.RatingCount.Should().Be(5);
        last.RatingAverage.Should().Be(4.6);
    }

    [Fact]
    public async Task Concurrent_ratings_lose_no_update()
    {
        Harness h = new();
        h.Copied();
        h.Run(ExecutionStatus.Passed);
        FakeUser same = new(h.Org);
        List<Task> tasks = [];
        for (int i = 0; i < 8; i++)
        {
            FakeUser who = i < 4 ? same : new FakeUser(h.Org);
            int stars = i % 5 + 1;
            InferenceApiDbContext db = h.NewDb();
            tasks.Add(Task.Run(async () => Ok(await h.Service(db, who).RateAsync(h.RecipeId, stars, default))));
        }

        await Task.WhenAll(tasks);

        InferenceApiDbContext verify = h.NewDb();
        MarketplaceRecipe r = await verify.MarketplaceRecipes.SingleAsync();
        List<MarketplaceRating> rows = await verify.MarketplaceRatings.ToListAsync();
        rows.Should().HaveCount(5);
        r.RatingCount.Should().Be(rows.Count);
        r.RatingSum.Should().Be(rows.Sum(x => x.Stars));
    }

    [Fact]
    public async Task Me_reports_state_and_hides_unlisted_recipes_from_non_owners()
    {
        Harness h = new();
        RecipeMeResponse none = (await h.Service().MeAsync(h.RecipeId, default)).Value!;
        (none.Copied, none.CanRate, none.MyStars, none.Reported).Should().Be((false, false, null, false));

        h.Copied();
        h.Db.MarketplaceReports.Add(new MarketplaceReport { Id = Guid.NewGuid(), RecipeId = h.RecipeId, ReporterUserId = h.User.UserId });
        h.Db.SaveChanges();
        RecipeMeResponse copied = (await h.Service().MeAsync(h.RecipeId, default)).Value!;
        (copied.Copied, copied.CanRate, copied.Reported).Should().Be((true, false, true));

        (await h.Db.MarketplaceRecipes.SingleAsync()).Status = RecipeStatus.Pending;
        await h.Db.SaveChangesAsync();
        (await h.Service().MeAsync(h.RecipeId, default)).Error!.Status.Should().Be(404);
        (await h.Service(user: new FakeUser(h.AuthorOrg)).MeAsync(h.RecipeId, default)).Error.Should().BeNull();
    }

    // ---------------------------------------------------------------- sweeper

    [Fact]
    public async Task Sweeper_counts_passed_executions_per_recipe_stores_only_totals_and_is_idempotent()
    {
        Harness h = new();
        h.Copied();
        h.Run(ExecutionStatus.Passed);
        h.Run(ExecutionStatus.Failed);
        h.Run(ExecutionStatus.Passed, org: Guid.NewGuid());

        // A second recipe with a copy and no runs, and a recipe with no copies at all.
        Guid quiet = Guid.NewGuid();
        Guid unused = Guid.NewGuid();
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = quiet, AuthorOrgId = h.AuthorOrg, Status = RecipeStatus.Listed, RunCount = 7 });
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = unused, AuthorOrgId = h.AuthorOrg, Status = RecipeStatus.Listed, RunCount = 9 });
        h.Db.MarketplaceCopies.Add(new MarketplaceCopy { Id = Guid.NewGuid(), RecipeId = quiet, VersionId = Guid.NewGuid(), OrgId = h.Org });
        // A non-marketplace workflow's runs never count.
        Guid template = Guid.NewGuid();
        h.Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = template, ProjectId = h.ProjectId, Name = "t", TemplateKey = "quick-win-promo" });
        h.Db.SaveChanges();
        h.Run(ExecutionStatus.Passed, workflow: template);

        MarketplaceRunCounts sweep = new(h.NewDb(), NullLogger<MarketplaceRunCounts>.Instance);
        (await sweep.SweepOnceAsync(default)).Should().Be(2);

        InferenceApiDbContext verify = h.NewDb();
        (await verify.MarketplaceRecipes.FindAsync(h.RecipeId))!.RunCount.Should().Be(2);
        (await verify.MarketplaceRecipes.FindAsync(quiet))!.RunCount.Should().Be(0);
        (await verify.MarketplaceRecipes.FindAsync(unused))!.RunCount.Should().Be(9);

        (await new MarketplaceRunCounts(h.NewDb(), NullLogger<MarketplaceRunCounts>.Instance).SweepOnceAsync(default)).Should().Be(0);
        h.Run(ExecutionStatus.Queued);
        (await new MarketplaceRunCounts(h.NewDb(), NullLogger<MarketplaceRunCounts>.Instance).SweepOnceAsync(default)).Should().Be(0);
        h.Run(ExecutionStatus.Passed);
        (await new MarketplaceRunCounts(h.NewDb(), NullLogger<MarketplaceRunCounts>.Instance).SweepOnceAsync(default)).Should().Be(1);
        (await h.NewDb().MarketplaceRecipes.FindAsync(h.RecipeId))!.RunCount.Should().Be(3);
    }

    [Fact]
    public async Task Sweeper_does_nothing_while_the_flag_is_off()
    {
        Harness h = new();
        h.Copied();
        h.Run(ExecutionStatus.Passed);
        h.Options.Enabled = false;

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(h.NewDb());
        services.AddScoped<MarketplaceRunCounts>(sp => new MarketplaceRunCounts(sp.GetRequiredService<InferenceApiDbContext>(), NullLogger<MarketplaceRunCounts>.Instance));
        using ServiceProvider provider = services.BuildServiceProvider();
        MarketplaceRunCountSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedMonitor(h.Options),
            Microsoft.Extensions.Options.Options.Create(new MarketplaceRunCountOptions { StartupDelaySeconds = 0, IntervalMinutes = 1 }),
            NullLogger<MarketplaceRunCountSweeper>.Instance);

        await sweeper.StartAsync(default);
        await Task.Delay(500);
        await sweeper.StopAsync(default);

        (await h.NewDb().MarketplaceRecipes.FindAsync(h.RecipeId))!.RunCount.Should().Be(0);
    }

    private sealed class FixedMonitor(MarketplaceOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<MarketplaceOptions>
    {
        public MarketplaceOptions CurrentValue => value;
        public MarketplaceOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<MarketplaceOptions, string?> listener) => null;
    }
}
