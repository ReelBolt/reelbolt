using System;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public class RecipeStatusMachineTests
{
    private static readonly (RecipeStatus From, RecipeStatus To, RecipeActor Actor)[] Legal =
    [
        (RecipeStatus.Draft, RecipeStatus.Pending, RecipeActor.System),
        (RecipeStatus.Draft, RecipeStatus.Listed, RecipeActor.System),
        (RecipeStatus.Draft, RecipeStatus.Rejected, RecipeActor.System),
        (RecipeStatus.Draft, RecipeStatus.Unpublished, RecipeActor.Author),
        (RecipeStatus.Pending, RecipeStatus.Listed, RecipeActor.Admin),
        (RecipeStatus.Pending, RecipeStatus.Rejected, RecipeActor.Admin),
        (RecipeStatus.Pending, RecipeStatus.Rejected, RecipeActor.System),
        (RecipeStatus.Pending, RecipeStatus.Unpublished, RecipeActor.Author),
        (RecipeStatus.Listed, RecipeStatus.Unpublished, RecipeActor.Author),
        (RecipeStatus.Listed, RecipeStatus.Pending, RecipeActor.System),
        (RecipeStatus.Rejected, RecipeStatus.Draft, RecipeActor.Author),
        (RecipeStatus.Unpublished, RecipeStatus.Draft, RecipeActor.Author),
        (RecipeStatus.Draft, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Pending, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Listed, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Listed, RecipeStatus.Removed, RecipeActor.System),
        (RecipeStatus.Rejected, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Unpublished, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Removed, RecipeStatus.Listed, RecipeActor.Admin),
        (RecipeStatus.Removed, RecipeStatus.Pending, RecipeActor.Admin),
        (RecipeStatus.Removed, RecipeStatus.Unpublished, RecipeActor.Admin)
    ];

    [Fact]
    public void Every_listed_transition_is_allowed_and_every_other_triple_is_refused()
    {
        foreach (RecipeStatus from in Enum.GetValues<RecipeStatus>())
        {
            foreach (RecipeStatus to in Enum.GetValues<RecipeStatus>())
            {
                foreach (RecipeActor actor in Enum.GetValues<RecipeActor>())
                {
                    bool expected = Legal.Contains((from, to, actor));
                    RecipeStatusMachine.IsAllowed(from, to, actor).Should().Be(expected, $"{from} -> {to} by {actor}");
                }
            }
        }
    }

    [Theory]
    [InlineData(RecipeActor.Author)]
    [InlineData(RecipeActor.System)]
    public void Removed_only_leaves_through_an_admin_restore(RecipeActor actor)
    {
        foreach (RecipeStatus to in Enum.GetValues<RecipeStatus>())
            RecipeStatusMachine.IsAllowed(RecipeStatus.Removed, to, actor).Should().BeFalse();
        RecipeStatusMachine.IsAllowed(RecipeStatus.Removed, RecipeStatus.Listed, RecipeActor.Admin).Should().BeTrue();
    }

    [Fact]
    public void An_illegal_transition_changes_nothing()
    {
        MarketplaceRecipe recipe = new() { Status = RecipeStatus.Removed };
        RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Listed, RecipeActor.Author, DateTime.UtcNow).Should().BeFalse();
        recipe.Status.Should().Be(RecipeStatus.Removed);
        recipe.ListedAt.Should().BeNull();
        recipe.EverListedAt.Should().BeNull();
    }

    [Fact]
    public void EverListedAt_is_set_the_first_time_a_recipe_is_listed_and_never_moved()
    {
        MarketplaceRecipe recipe = new() { Status = RecipeStatus.Draft };
        DateTime first = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        DateTime second = first.AddDays(3);

        RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Pending, RecipeActor.System, first).Should().BeTrue();
        recipe.EverListedAt.Should().BeNull("pending is not listed");
        RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Listed, RecipeActor.Admin, first).Should().BeTrue();
        recipe.EverListedAt.Should().Be(first);
        recipe.ListedAt.Should().Be(first);

        RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Removed, RecipeActor.Admin, first).Should().BeTrue();
        RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Listed, RecipeActor.Admin, second).Should().BeTrue();
        recipe.ListedAt.Should().Be(second);
        recipe.EverListedAt.Should().Be(first, "the trusted-author mark records the first listing");
    }

    [Fact]
    public void Marketplace_tables_are_unfiltered_unique_where_promised_and_the_preview_source_is_internal()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only").Options);
        var model = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;

        var recipe = model.FindEntityType(typeof(MarketplaceRecipe))!;
        recipe.GetTableName().Should().Be("marketplace_recipes");
        var search = recipe.FindProperty(nameof(MarketplaceRecipe.SearchVector))!;
        search.FindAnnotation("Npgsql:TsVectorConfig")!.Value.Should().Be("simple");
        ((string[])search.FindAnnotation("Npgsql:TsVectorProperties")!.Value!)
            .Should().Equal("Name", "Description", "Category");
        recipe.GetIndexes().Should().Contain(i => i.GetMethod() == "GIN");
        recipe.GetIndexes().Should().Contain(i => i.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { "Status", "ListedAt", "Id" }));
        recipe.FindProperty(nameof(MarketplaceRecipe.Status))!.GetProviderClrType().Should().Be<string>();

        model.FindEntityType(typeof(MarketplaceCopy))!.GetIndexes()
            .Should().Contain(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "RecipeId", "OrgId" }));
        model.FindEntityType(typeof(MarketplaceRating))!.GetIndexes()
            .Should().Contain(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "RecipeId", "UserId" }));
        model.FindEntityType(typeof(MarketplaceReport))!.GetIndexes()
            .Should().Contain(i => i.IsUnique && i.GetFilter() != null);
        model.FindEntityType(typeof(MarketplaceRecipeVersion))!.FindProperty(nameof(MarketplaceRecipeVersion.SnapshotJson))!
            .GetColumnType().Should().Be("jsonb");

        typeof(MarketplaceRecipeVersion).GetProperty(nameof(MarketplaceRecipeVersion.PreviewSourceJson)).Should().NotBeNull();
    }
}
