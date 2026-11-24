using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Billing;

/// <summary>One recommended Chat model for a built-in agent, with the sentence a customer reads next to it.</summary>
/// <param name="AgentType">The built-in agent type (never <see cref="Data.Models.AgentType.Custom"/>).</param>
/// <param name="Kind">The provider kind of the recommended model.</param>
/// <param name="ModelCode">The model code of the recommended model, matched ordinally.</param>
/// <param name="Why">One plain sentence for the customer: no vendor cost, no internal names.</param>
public sealed record AgentRecommendation(AgentType AgentType, InferenceProviderKind Kind, string ModelCode, string Why);

/// <summary>
/// The typed view of <c>Billing/agent-model-recommendations.seed.json</c>: which platform Chat model is recommended
/// for which built-in agent. Data, not code, so a better model is a one-line change. Read at boot by
/// <see cref="AgentRecommendedModelSeeder"/> and on every <c>GET /api/v1/org/agent-models</c> for the "why".
/// </summary>
public sealed record AgentRecommendationSeed(IReadOnlyList<AgentRecommendation> Entries)
{
    public const string FileName = "agent-model-recommendations.seed.json";

    /// <summary>The shipped seed, read once: the file is immutable for the life of the process.</summary>
    private static readonly Lazy<AgentRecommendationSeed> Shipped = new(
        () => Load(Path.Combine(AppContext.BaseDirectory, "Billing", FileName)));

    /// <summary>Reads and types the seed at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a well-formed seed.</exception>
    public static AgentRecommendationSeed Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The seed shipped next to the binaries.</summary>
    public static AgentRecommendationSeed LoadDefault() => Shipped.Value;

    public static AgentRecommendationSeed Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            List<AgentRecommendation> entries = [];
            foreach (JsonElement item in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                string agent = item.GetProperty("agentType").GetString() ?? string.Empty;
                string kind = item.GetProperty("kind").GetString() ?? string.Empty;
                if (!Enum.TryParse(agent, ignoreCase: false, out AgentType agentType) || !Enum.IsDefined(agentType)
                    || int.TryParse(agent, out _))
                    throw new InvalidDataException($"{FileName}: '{agent}' is not an agent type.");
                if (!Enum.TryParse(kind, ignoreCase: false, out InferenceProviderKind providerKind) || !Enum.IsDefined(providerKind)
                    || int.TryParse(kind, out _))
                    throw new InvalidDataException($"{FileName}: '{kind}' is not a provider kind.");
                entries.Add(new AgentRecommendation(
                    agentType, providerKind,
                    item.GetProperty("modelCode").GetString() ?? string.Empty,
                    item.GetProperty("why").GetString() ?? string.Empty));
            }

            return new AgentRecommendationSeed(entries);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"{FileName} is not a valid recommendation seed: {ex.Message}", ex);
        }
    }

    /// <summary>The recommendation for <paramref name="agentType"/>, or null.</summary>
    public AgentRecommendation? For(AgentType agentType) => Entries.FirstOrDefault(e => e.AgentType == agentType);

    /// <summary>Every problem with the seed, one line each; empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];
        foreach (AgentRecommendation entry in Entries)
        {
            if (entry.AgentType == AgentType.Custom)
                problems.Add("Custom has no built-in definition to recommend a model for.");
            if (string.IsNullOrWhiteSpace(entry.ModelCode))
                problems.Add($"{entry.AgentType}: modelCode is empty.");
            if (string.IsNullOrWhiteSpace(entry.Why))
                problems.Add($"{entry.AgentType}: why is empty.");
        }

        problems.AddRange(Entries.GroupBy(e => e.AgentType).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: more than one recommendation."));
        return problems;
    }
}
