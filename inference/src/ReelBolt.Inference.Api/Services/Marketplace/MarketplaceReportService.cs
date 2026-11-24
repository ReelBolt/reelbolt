using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// User reports of a listed recipe. The reporter's identity is stored for platform admins only and is never part of any public or
/// author-facing answer. Enough open reports from DISTINCT users hide the recipe from the public API until an admin has looked.
/// </summary>
public sealed class MarketplaceReportService(
    InferenceApiDbContext db,
    ICurrentUser user,
    IOptions<MarketplaceOptions> options,
    ILogger<MarketplaceReportService> logger)
{
    public const int MaxNoteChars = 500;

    private MarketplaceOptions Options => options.Value;

    public async Task<MarketplaceResult<ReportRecipeResponse>> ReportAsync(Guid recipeId, ReportRecipeRequest request, CancellationToken ct)
    {
        static MarketplaceResult<ReportRecipeResponse> Fail(MarketplaceError e) => MarketplaceResult<ReportRecipeResponse>.Fail(e);

        if (!user.EmailVerified)
        {
            return Fail(new MarketplaceError(403, EmailVerificationGate.ErrorCode,
                "Verify your email address before reporting a recipe. Check your inbox for the verification link."));
        }

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is null || recipe.Status != RecipeStatus.Listed || recipe.HiddenByReports)
            return Fail(new MarketplaceError(404));

        if (!Enum.TryParse(request.Reason?.Trim(), true, out ReportReason reason) || !Enum.IsDefined(reason) || string.IsNullOrWhiteSpace(request.Reason))
            return Fail(new MarketplaceError(400, "INVALID_FIELD", "The reason must be one of: illegal, sexual, copyright, spam, misleading, other."));

        string? note = null;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            note = MarketplaceText.Sanitize(request.Note);
            if (note.Length > MaxNoteChars)
                return Fail(new MarketplaceError(400, "INVALID_FIELD", $"The note can be at most {MaxNoteChars} characters of plain text."));
            if (note.Length == 0)
                note = null;
        }

        Guid reporter = user.UserId;
        if (await db.MarketplaceReports.AsNoTracking().AnyAsync(r => r.RecipeId == recipeId && r.ReporterUserId == reporter && r.State == ReportState.Open, ct))
            return MarketplaceResult<ReportRecipeResponse>.Ok(new ReportRecipeResponse(true));

        DateTime now = DateTime.UtcNow;
        DateTime midnight = now.Date;
        int today = await db.MarketplaceReports.AsNoTracking().CountAsync(r => r.ReporterUserId == reporter && r.CreatedAt >= midnight, ct);
        if (today >= Options.MaxReportsPerUserPerDay)
            return Fail(new MarketplaceError(429, "REPORT_RATE_LIMITED", "You have sent many reports today. Try again tomorrow."));

        db.MarketplaceReports.Add(new MarketplaceReport
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            ReporterUserId = reporter,
            Reason = reason,
            Note = note,
            State = ReportState.Open,
            CreatedAt = now
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The unique open-report index: the same reporter raced themselves. The answer is the same as a repeat.
            logger.LogInformation(ex, "A duplicate open marketplace report was refused by the index");
            return MarketplaceResult<ReportRecipeResponse>.Ok(new ReportRecipeResponse(true));
        }

        await HideIfThresholdReachedAsync(recipeId, now, ct);
        return MarketplaceResult<ReportRecipeResponse>.Ok(new ReportRecipeResponse(true));
    }

    private async Task HideIfThresholdReachedAsync(Guid recipeId, DateTime now, CancellationToken ct)
    {
        // Only reports that carry weight count: from a verified account that is old enough, and never from a reporter whose earlier
        // report on this recipe an admin dismissed.
        DateTime oldEnough = now - TimeSpan.FromDays(Math.Max(0, Options.MinReporterAccountAgeDays));
        Guid[] dismissed = await db.MarketplaceReports.AsNoTracking()
            .Where(r => r.RecipeId == recipeId && r.State == ReportState.Dismissed).Select(r => r.ReporterUserId).Distinct().ToArrayAsync(ct);
        Guid[] openReporters = await db.MarketplaceReports.AsNoTracking()
            .Where(r => r.RecipeId == recipeId && r.State == ReportState.Open).Select(r => r.ReporterUserId).Distinct().ToArrayAsync(ct);
        int distinct = await db.ApplicationUsers.AsNoTracking()
            .CountAsync(u => openReporters.Contains(u.Id) && !dismissed.Contains(u.Id) && u.EmailVerifiedAt != null && u.CreatedAt <= oldEnough, ct);
        if (distinct < Math.Max(1, Options.ReportsAutoHideThreshold))
            return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            MarketplaceRecipe? recipe = await db.MarketplaceRecipes.FirstOrDefaultAsync(r => r.Id == recipeId, ct);
            if (recipe is null || recipe.HiddenByReports)
                return;

            recipe.HiddenByReports = true;
            recipe.UpdatedAt = now;
            recipe.Revision++;
            db.MarketplaceAudit.Add(new MarketplaceAuditEntry
            {
                Id = Guid.NewGuid(),
                RecipeId = recipeId,
                VersionId = recipe.CurrentVersionId,
                Action = "auto_hidden",
                ActorKind = "system",
                DetailJson = new JsonObject { ["reason"] = "reports_threshold", ["reports"] = distinct }.ToJsonString(),
                CreatedAt = now
            });
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
