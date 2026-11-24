using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Implementation of <see cref="IDecisionClient"/> for the TypeSafe API (api.typesafe.ai).
/// Sends decisions to the verified TypeSafe endpoint and parses responses according to the
/// type-specific answer shapes (Choice, Score, Noul).
/// </summary>
public sealed class TypeSafeDecisionClient : IDecisionClient
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;

    public TypeSafeDecisionClient(HttpClient httpClient, string modelName)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(modelName);

        _httpClient = httpClient;
        _modelName = modelName;
    }

    public async Task<DecisionResult> DecideAsync(
        DecisionState state,
        IReadOnlyList<DecisionQuestion> questions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);

        var request = BuildRequest(state, questions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = JsonContent.Create(request, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })
        };

        using var response = await _httpClient.SendAsync(httpRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"TypeSafe API returned {(int)response.StatusCode}: {errorBody}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<TypeSafeResponse>(cancellationToken: ct);
        if (result == null)
        {
            throw new InvalidOperationException("TypeSafe API response was null or could not be parsed.");
        }

        return ParseResponse(result, questions);
    }

    private TypeSafeRequest BuildRequest(DecisionState state, IReadOnlyList<DecisionQuestion> questions)
    {
        var questionsDict = new Dictionary<string, TypeSafeQuestion>();

        foreach (var q in questions)
        {
            questionsDict[q.Name] = new TypeSafeQuestion
            {
                Type = q.Kind switch
                {
                    DecisionKind.Choice => "choice",
                    DecisionKind.Score => "score",
                    DecisionKind.Noul => "noul",
                    _ => throw new ArgumentException($"Unknown decision kind: {q.Kind}")
                },
                Instructions = q.Instructions,
                Criteria = q.Kind switch
                {
                    DecisionKind.Choice => BuildChoiceCriteria(q.Options),
                    DecisionKind.Score => BuildScoreCriteria(q.Options),
                    DecisionKind.Noul => null,
                    _ => null
                }
            };
        }

        return new TypeSafeRequest
        {
            State = state.Content,
            Model = _modelName,
            Questions = questionsDict
        };
    }

    private static object? BuildChoiceCriteria(IReadOnlyList<string> options)
    {
        var criteria = new Dictionary<string, string>();
        foreach (var option in options)
        {
            criteria[option] = "description";
        }
        return criteria;
    }

    private static object? BuildScoreCriteria(IReadOnlyList<string> options)
    {
        return options.ToList();
    }

    private static DecisionResult ParseResponse(TypeSafeResponse response, IReadOnlyList<DecisionQuestion> questions)
    {
        var questionsByName = questions.ToDictionary(q => q.Name);
        var answers = new Dictionary<string, DecisionAnswer>();

        foreach (var (name, answerObj) in response.Answers)
        {
            if (!questionsByName.TryGetValue(name, out var question))
            {
                throw new InvalidOperationException($"Received answer for unknown question: {name}");
            }

            answers[name] = ParseAnswer(name, question, answerObj);
        }

        return new DecisionResult(
            Model: response.Model,
            Answers: answers,
            InputTokens: response.Usage.InputTokens,
            OutputTokens: response.Usage.OutputTokens);
    }

    private static DecisionAnswer ParseAnswer(
        string name,
        DecisionQuestion question,
        JsonElement answerElement)
    {
        var type = answerElement.GetProperty("type").GetString();
        if (type != question.Kind.ToString().ToLowerInvariant())
        {
            throw new InvalidOperationException(
                $"Answer type mismatch for {name}: expected {question.Kind}, got {type}");
        }

        return question.Kind switch
        {
            DecisionKind.Choice => ParseChoiceAnswer(name, answerElement),
            DecisionKind.Score => ParseScoreAnswer(name, answerElement),
            DecisionKind.Noul => ParseNoulAnswer(name, answerElement),
            _ => throw new ArgumentException($"Unknown decision kind: {question.Kind}")
        };
    }

    private static DecisionAnswer ParseChoiceAnswer(string name, JsonElement answerElement)
    {
        var choice = answerElement.GetProperty("choice").GetString();
        var confidence = answerElement.GetProperty("confidence").GetDouble();
        var probabilities = ParseProbabilities(answerElement.GetProperty("probabilities"));

        return new DecisionAnswer(
            Name: name,
            Kind: DecisionKind.Choice,
            Choice: choice,
            Score: null,
            Noul: null,
            Confidence: confidence,
            Probabilities: probabilities);
    }

    private static DecisionAnswer ParseScoreAnswer(string name, JsonElement answerElement)
    {
        var score = answerElement.GetProperty("score").GetDouble();
        var confidence = answerElement.GetProperty("confidence").GetDouble();
        var probabilities = ParseProbabilities(answerElement.GetProperty("probabilities"));

        return new DecisionAnswer(
            Name: name,
            Kind: DecisionKind.Score,
            Choice: null,
            Score: score,
            Noul: null,
            Confidence: confidence,
            Probabilities: probabilities);
    }

    private static DecisionAnswer ParseNoulAnswer(string name, JsonElement answerElement)
    {
        var noul = answerElement.GetProperty("noul").GetDouble();
        var probabilities = new Dictionary<string, double>();

        return new DecisionAnswer(
            Name: name,
            Kind: DecisionKind.Noul,
            Choice: null,
            Score: null,
            Noul: noul,
            Confidence: null,
            Probabilities: probabilities);
    }

    private static IReadOnlyDictionary<string, double> ParseProbabilities(JsonElement probsElement)
    {
        var probs = new Dictionary<string, double>();
        foreach (var prop in probsElement.EnumerateObject())
        {
            probs[prop.Name] = prop.Value.GetDouble();
        }
        return probs;
    }

    // Wire format types for JSON serialization
    private sealed class TypeSafeRequest
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("questions")]
        public Dictionary<string, TypeSafeQuestion> Questions { get; set; } = new();
    }

    private sealed class TypeSafeQuestion
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("instructions")]
        public string Instructions { get; set; } = string.Empty;

        [JsonPropertyName("criteria")]
        public object? Criteria { get; set; }
    }

    private sealed class TypeSafeResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("answers")]
        public Dictionary<string, JsonElement> Answers { get; set; } = new();

        [JsonPropertyName("usage")]
        public UsageInfo Usage { get; set; } = new();
    }

    private sealed class UsageInfo
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }
    }
}
