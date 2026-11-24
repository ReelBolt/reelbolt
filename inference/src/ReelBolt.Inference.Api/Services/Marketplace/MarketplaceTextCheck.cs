using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Services.Marketplace;

public sealed record TextCheckField(string Field, string Text);

public sealed record TextCheckFieldResult(string Field, TextVerdict Verdict);

public sealed record TextCheckResult(TextVerdict Verdict, IReadOnlyList<TextCheckFieldResult> Fields);

public interface IMarketplaceTextCheck
{
    Task<TextCheckResult> CheckAsync(
        IReadOnlyList<TextCheckField> fields, Guid organizationId, Guid projectId, Guid userId, CancellationToken ct);
}

/// <summary>
/// Sends every text an author wrote through the existing content moderation gate (hash-cached audit rows, same path text exports
/// use). Fails closed toward a human: a disabled or provider-less gate, an unclassified answer or any failure is
/// <see cref="TextVerdict.Unavailable"/>, which can never auto-list.
/// </summary>
public sealed class MarketplaceTextCheck(IContentModerationGate gate, ILogger<MarketplaceTextCheck> logger) : IMarketplaceTextCheck
{
    public async Task<TextCheckResult> CheckAsync(
        IReadOnlyList<TextCheckField> fields, Guid organizationId, Guid projectId, Guid userId, CancellationToken ct)
    {
        List<TextCheckFieldResult> results = [];
        TextVerdict overall = TextVerdict.Allow;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (TextCheckField field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Text) || !seen.Add(field.Text))
                continue;

            TextVerdict verdict = await CheckOneAsync(field.Text, organizationId, projectId, userId, ct);
            results.Add(new TextCheckFieldResult(field.Field, verdict));
            overall = Worse(overall, verdict);
            if (overall == TextVerdict.Block)
                break;
        }

        return new TextCheckResult(overall, results);
    }

    private async Task<TextVerdict> CheckOneAsync(string text, Guid organizationId, Guid projectId, Guid userId, CancellationToken ct)
    {
        try
        {
            ModerationDecision decision = await gate.ModerateAsync(
                new ModerationInput(text, [], ModerationContentKind.Text),
                new ModerationAttribution(organizationId, projectId, userId, null, null, ModerationSurface.Publish, ModerationContentKind.Text),
                ct);
            return decision.Action switch
            {
                ModerationAction.Block => TextVerdict.Block,
                ModerationAction.Unavailable => TextVerdict.Unavailable,
                _ when !decision.Available => TextVerdict.Unavailable,
                ModerationAction.Review => TextVerdict.Review,
                ModerationAction.Allow => TextVerdict.Allow,
                _ => TextVerdict.Unavailable
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Marketplace text check failed; treating it as unavailable");
            return TextVerdict.Unavailable;
        }
    }

    private static TextVerdict Worse(TextVerdict a, TextVerdict b) => (TextVerdict)Math.Max((int)a, (int)b);
}
