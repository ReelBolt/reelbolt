using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>
/// The <c>generated-fact</c> chunks (docs/design/workflow-apps.md section 7.4, "Facts into the index"): one chunk
/// per step type and one per recipe, produced from <see cref="StepCatalog"/> at index time so retrieval covers the
/// same facts the <c>GetStepCatalog</c> tool returns, from the same source. They enter the corpus hash, so a code
/// change that alters a card re-indexes the collection. Developer audience: internal background, no URL. Cost
/// class only, never a price.
/// </summary>
internal static class PlatformDocsGeneratedFacts
{
    internal const string StepCardsDoc = "generated/step-catalog";
    internal const string TemplatesDoc = "generated/recipes";

    internal static IReadOnlyList<PlatformDocsChunk> Build()
    {
        List<PlatformDocsChunk> chunks = [];
        int ordinal = 0;
        foreach (StepCard card in StepCatalog.GetAll())
        {
            string text = StepCatalog.FactText(card);
            string title = $"Step catalog: {card.StepType}";
            chunks.Add(new PlatformDocsChunk($"{StepCardsDoc}#{ordinal++}", StepCardsDoc, PlatformDocsAudience.Developer, title, string.Empty, string.Empty, text)
            {
                Meta = new PlatformDocsChunkMeta
                {
                    Kind = "generated-fact",
                    StepTypes = [card.StepType],
                    CostClass = StepCatalog.CostClassKey(card.CostClass),
                    HeadingPath = $"# {title}",
                    DocVersion = PlatformDocsMetadata.DocVersionOf(text)
                }
            });
        }

        ordinal = 0;
        foreach (TemplateFact fact in StepCatalog.GetTemplateFacts())
        {
            string text = StepCatalog.FactText(fact);
            string title = $"Recipe: {fact.Name}";
            chunks.Add(new PlatformDocsChunk($"{TemplatesDoc}#{ordinal++}", TemplatesDoc, PlatformDocsAudience.Developer, title, string.Empty, string.Empty, text)
            {
                Meta = new PlatformDocsChunkMeta
                {
                    Kind = "generated-fact",
                    StepTypes = fact.StepTypes,
                    AgentTypes = fact.AgentTypes,
                    TemplateKeys = [fact.Key],
                    CostClass = StepCatalog.CostClassKey(fact.CostClass),
                    HeadingPath = $"# {title}",
                    DocVersion = PlatformDocsMetadata.DocVersionOf(text)
                }
            });
        }

        return chunks;
    }
}
