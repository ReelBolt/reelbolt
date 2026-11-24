using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>Body of <c>PUT /api/v1/marketplace/recipes/{id}/rating</c>.</summary>
public sealed record RateRecipeRequest(int Stars);

/// <summary>The caller's rating and the recipe's public figures; the average shows only from <see cref="MarketplaceOptions.MinRatingsForAverage"/> ratings.</summary>
public sealed record RatingResponse(int MyStars, int RatingCount, double? RatingAverage);

/// <summary><c>GET /api/v1/marketplace/recipes/{id}/me</c>.</summary>
public sealed record RecipeMeResponse(bool Copied, bool CanRate, int? MyStars, bool Reported);

/// <summary>
/// Ratings: 1-5 stars, one per account, from an account whose ORGANIZATION copied the recipe and has at least one
/// <see cref="ExecutionStatus.Passed"/> run of a copy. No text. The author's own organization never rates its own recipe.
/// </summary>
public sealed class MarketplaceRatingService(
    InferenceApiDbContext db,
    ICurrentUser user,
    IOptions<MarketplaceOptions> options)
{
    public const string NotEligibleCode = "RATING_NOT_ELIGIBLE";

    private MarketplaceOptions Options => options.Value;

    public async Task<MarketplaceResult<RatingResponse>> RateAsync(Guid recipeId, int stars, CancellationToken ct)
    {
        static MarketplaceResult<RatingResponse> Fail(MarketplaceError e) => MarketplaceResult<RatingResponse>.Fail(e);

        if (!Options.Enabled)
            return Fail(new MarketplaceError(404));
        if (!user.EmailVerified)
        {
            return Fail(new MarketplaceError(403, EmailVerificationGate.ErrorCode,
                "Verify your email address before rating a recipe. Check your inbox for the verification link."));
        }

        if (user.OrganizationId is not { } orgId)
            return Fail(new MarketplaceError(404));
        if (stars is < 1 or > 5)
            return Fail(new MarketplaceError(400, "INVALID_STARS", "Rate with 1 to 5 stars."));

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is not { Status: RecipeStatus.Listed })
            return Fail(new MarketplaceError(404));
        if (recipe.AuthorOrgId == orgId || recipe.AuthorUserId == user.UserId || !await IsEligibleAsync(recipeId, orgId, recipe.StepCount, ct))
            return Fail(NotEligible());

        // One writer per (recipe, account): the row and the recipe's sum/count change together or not at all.
        await using (MarketplaceKeyLock gate = await MarketplaceKeyLock.AcquireAsync(db, $"market-rate:{recipeId:N}:{user.UserId:N}", ct))
        {
            MarketplaceRating? existing = await db.MarketplaceRatings.FirstOrDefaultAsync(r => r.RecipeId == recipeId && r.UserId == user.UserId, ct);
            if (existing is null)
            {
                db.MarketplaceRatings.Add(new MarketplaceRating
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipeId,
                    UserId = user.UserId,
                    Stars = stars,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);
                await MarketplaceCounters.AddRatingAsync(db, recipeId, stars, 1, ct);
            }
            else if (existing.Stars != stars)
            {
                int delta = stars - existing.Stars;
                existing.Stars = stars;
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await MarketplaceCounters.AddRatingAsync(db, recipeId, delta, 0, ct);
            }

            await gate.CommitAsync(ct);
        }

        var figures = await db.MarketplaceRecipes.AsNoTracking()
            .Where(r => r.Id == recipeId)
            .Select(r => new { r.RatingSum, r.RatingCount })
            .FirstAsync(ct);
        return MarketplaceResult<RatingResponse>.Ok(new RatingResponse(stars, figures.RatingCount, Average(figures.RatingSum, figures.RatingCount)));
    }

    public async Task<MarketplaceResult<RecipeMeResponse>> MeAsync(Guid recipeId, CancellationToken ct)
    {
        if (!Options.Enabled || user.OrganizationId is not { } orgId)
            return MarketplaceResult<RecipeMeResponse>.Fail(new MarketplaceError(404));

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is null || (recipe.Status != RecipeStatus.Listed && recipe.AuthorOrgId != orgId))
            return MarketplaceResult<RecipeMeResponse>.Fail(new MarketplaceError(404));

        bool copied = await db.MarketplaceCopies.AsNoTracking().AnyAsync(c => c.RecipeId == recipeId && c.OrgId == orgId, ct);
        int? mine = await db.MarketplaceRatings.AsNoTracking()
            .Where(r => r.RecipeId == recipeId && r.UserId == user.UserId)
            .Select(r => (int?)r.Stars)
            .FirstOrDefaultAsync(ct);
        bool reported = await db.MarketplaceReports.AsNoTracking().AnyAsync(r => r.RecipeId == recipeId && r.ReporterUserId == user.UserId, ct);
        bool canRate = user.EmailVerified && recipe.Status == RecipeStatus.Listed && recipe.AuthorOrgId != orgId
            && recipe.AuthorUserId != user.UserId && copied && await HasPassedRunAsync(recipeId, orgId, recipe.StepCount, ct);
        return MarketplaceResult<RecipeMeResponse>.Ok(new RecipeMeResponse(copied, canRate, mine, reported));
    }

    /// <summary>The organization copied the recipe AND a copy of it in that organization has a <see cref="ExecutionStatus.Passed"/> run.</summary>
    public async Task<bool> IsEligibleAsync(Guid recipeId, Guid orgId, int recipeStepCount, CancellationToken ct) =>
        await db.MarketplaceCopies.AsNoTracking().AnyAsync(c => c.RecipeId == recipeId && c.OrgId == orgId, ct)
        && await HasPassedRunAsync(recipeId, orgId, recipeStepCount, ct);

    /// <summary>
    /// "Passed run" = a <c>workflow_executions</c> row of the caller's organization with <see cref="ExecutionStatus.Passed"/> (the same
    /// terminal success status the run wake and review-link code use) whose workflow is a standard workflow of that organization's
    /// projects with <c>template_key = 'market:&lt;id&gt;'</c>.
    /// </summary>
    private async Task<bool> HasPassedRunAsync(Guid recipeId, Guid orgId, int recipeStepCount, CancellationToken ct)
    {
        string key = PortableRecipeKey(recipeId);
        List<Guid> workflows = await db.WorkflowExecutions.AsNoTracking().IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(e => e.OrganizationId == orgId && e.Status == ExecutionStatus.Passed)
            .Join(
                db.WorkflowDefinitions.AsNoTracking().IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
                    .Where(w => w.TemplateKey == key && w.Kind == WorkflowDefinitionKind.Standard),
                e => e.WorkflowDefinitionId, w => w.Id, (e, w) => w.Id)
            .Distinct()
            .ToListAsync(ct);
        if (workflows.Count == 0)
            return false;
        if (recipeStepCount <= 0)
            return true;

        // A copy cut down to a step or two is not the recipe: the passing workflow must still have all of the recipe's steps.
        return await db.WorkflowSteps.AsNoTracking()
            .Where(s => workflows.Contains(s.WorkflowDefinitionId))
            .GroupBy(s => s.WorkflowDefinitionId)
            .AnyAsync(g => g.Count() >= recipeStepCount, ct);
    }

    public double? Average(int sum, int count) =>
        count >= Options.MinRatingsForAverage && count > 0 ? Math.Round((double)sum / count, 2) : null;

    private static string PortableRecipeKey(Guid recipeId) => Snapshot.PortableRecipeMaterializer.TemplateKeyOf(recipeId);

    private static MarketplaceError NotEligible() => new(403, NotEligibleCode,
        "You can rate a recipe after your organization has copied it and a run of that copy has finished successfully.");
}
