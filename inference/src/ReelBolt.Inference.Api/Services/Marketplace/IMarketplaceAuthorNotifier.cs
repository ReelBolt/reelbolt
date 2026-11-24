using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// Tells an author that a recipe was rejected, removed or could not be previewed. The mail carries the REASON CATEGORY only: never an
/// admin note, a reporter or any free text. Best effort: a failure is logged and never fails the action that caused it.
/// </summary>
public interface IMarketplaceAuthorNotifier
{
    Task NotifyAsync(Guid recipeId, string reasonCategory, CancellationToken ct);
}

public static class MarketplaceNotice
{
    public const string Template = "marketplace_notice";

    /// <summary>The closed list of categories a notice may carry. Anything else is sent as <c>other</c>.</summary>
    public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.Ordinal)
    {
        "content", "unsafe", "copyright", "low_quality", "other", "preview", "safety"
    };

    public static string Normalize(string? category) => category is not null && Categories.Contains(category) ? category : "other";
}

public sealed class MarketplaceAuthorNotifier(InferenceApiDbContext db, IAccountMailClient mail, ILogger<MarketplaceAuthorNotifier> logger) : IMarketplaceAuthorNotifier
{
    public async Task NotifyAsync(Guid recipeId, string reasonCategory, CancellationToken ct)
    {
        try
        {
            Guid? authorId = await db.MarketplaceRecipes.AsNoTracking().Where(r => r.Id == recipeId).Select(r => (Guid?)r.AuthorUserId).FirstOrDefaultAsync(ct);
            if (authorId is not { } id)
                return;

            ApplicationUser? author = await db.ApplicationUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
            if (author is null || author.EmailVerifiedAt is null || author.DeletionRequestedAt is not null || string.IsNullOrWhiteSpace(author.Email))
                return;

            // No locale is sent: the Go mailer uses the account's saved locale, else its default.
            await mail.SendAsync(new AccountMail(
                author.Email, MarketplaceNotice.Template,
                new AccountMailData { Name = string.IsNullOrWhiteSpace(author.DisplayName) ? null : author.DisplayName, Reason = MarketplaceNotice.Normalize(reasonCategory) }), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The marketplace notice for recipe {RecipeId} could not be sent", recipeId);
        }
    }
}
