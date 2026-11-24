using ReelBolt.Inference.Api.Data.Marketplace;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>Who performs a status transition.</summary>
public enum RecipeActor
{
    Author,
    System,
    Admin
}

/// <summary>
/// The pure status machine of a marketplace recipe. A transition is a (from, to, actor) triple; anything not in
/// the table is refused. <see cref="RecipeStatus.Removed"/> only leaves through an admin restore.
/// </summary>
public static class RecipeStatusMachine
{
    private static readonly HashSet<(RecipeStatus From, RecipeStatus To, RecipeActor Actor)> Allowed =
    [
        // The publish pipeline decides what a prepared version becomes.
        (RecipeStatus.Draft, RecipeStatus.Pending, RecipeActor.System),
        (RecipeStatus.Draft, RecipeStatus.Listed, RecipeActor.System),
        (RecipeStatus.Draft, RecipeStatus.Rejected, RecipeActor.System),
        (RecipeStatus.Draft, RecipeStatus.Unpublished, RecipeActor.Author),

        // A human decides what is pending; the author may still withdraw it.
        (RecipeStatus.Pending, RecipeStatus.Listed, RecipeActor.Admin),
        (RecipeStatus.Pending, RecipeStatus.Rejected, RecipeActor.Admin),
        // A new version of a pending recipe that fails its own automatic check leaves nothing for a human to review.
        (RecipeStatus.Pending, RecipeStatus.Rejected, RecipeActor.System),
        (RecipeStatus.Pending, RecipeStatus.Unpublished, RecipeActor.Author),

        // A listed recipe is withdrawn by its author, or sent back for review by the system.
        (RecipeStatus.Listed, RecipeStatus.Unpublished, RecipeActor.Author),
        (RecipeStatus.Listed, RecipeStatus.Pending, RecipeActor.System),
        // A CSAM match on a preview takes a listed recipe down at once (reason safety).
        (RecipeStatus.Listed, RecipeStatus.Removed, RecipeActor.System),

        // The author may fix and republish a rejected or withdrawn recipe.
        (RecipeStatus.Rejected, RecipeStatus.Draft, RecipeActor.Author),
        (RecipeStatus.Unpublished, RecipeStatus.Draft, RecipeActor.Author),

        // Takedown: an admin may remove a recipe from any other state.
        (RecipeStatus.Draft, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Pending, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Listed, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Rejected, RecipeStatus.Removed, RecipeActor.Admin),
        (RecipeStatus.Unpublished, RecipeStatus.Removed, RecipeActor.Admin),

        // Restore: the only way out of Removed.
        (RecipeStatus.Removed, RecipeStatus.Listed, RecipeActor.Admin),
        // Restore when the preview objects are gone: back to review while a new preview is made.
        (RecipeStatus.Removed, RecipeStatus.Pending, RecipeActor.Admin),
        (RecipeStatus.Removed, RecipeStatus.Unpublished, RecipeActor.Admin)
    ];

    public static bool IsAllowed(RecipeStatus from, RecipeStatus to, RecipeActor actor) =>
        Allowed.Contains((from, to, actor));

    /// <summary>
    /// Applies a transition to the recipe. Sets <see cref="MarketplaceRecipe.ListedAt"/> whenever it becomes listed and
    /// <see cref="MarketplaceRecipe.EverListedAt"/> the first time only. Returns false and changes nothing when the
    /// transition is illegal.
    /// </summary>
    public static bool TryTransition(MarketplaceRecipe recipe, RecipeStatus to, RecipeActor actor, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (!IsAllowed(recipe.Status, to, actor))
            return false;

        recipe.Status = to;
        if (actor == RecipeActor.Admin && to is RecipeStatus.Rejected or RecipeStatus.Removed)
            recipe.HumanRejectedAt = nowUtc;
        recipe.UpdatedAt = nowUtc;
        if (to == RecipeStatus.Listed)
        {
            recipe.ListedAt = nowUtc;
            recipe.EverListedAt ??= nowUtc;
        }
        return true;
    }
}
