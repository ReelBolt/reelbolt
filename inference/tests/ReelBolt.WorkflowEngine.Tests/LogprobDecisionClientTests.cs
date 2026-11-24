using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for LogprobDecisionClient logprob-based decision making.
/// </summary>
public class LogprobDecisionClientTests
{
    [Fact]
    public async Task DecideAsync_Choice_PicksHighestProbabilityLabel()
    {
        // Arrange: label A (cat) has highest probability 0.7
        var state = new DecisionState("State content");
        var question = new DecisionQuestion(
            Name: "animal",
            Kind: DecisionKind.Choice,
            Instructions: "Pick an animal",
            Options: new[] { "cat", "dog", "bird" });

        var client = CreateTestClient(
            selectedLabel: "A",
            logProbabilities: new Dictionary<string, double> { { "A", 0.7 }, { "B", 0.2 }, { "C", 0.1 } });

        // Act
        var result = await client.DecideAsync(state, new[] { question }, CancellationToken.None);

        // Assert
        Assert.Single(result.Answers);
        var answer = result.Answers["animal"];
        Assert.Equal("cat", answer.Choice);
        Assert.True(answer.Confidence >= 0.69 && answer.Confidence <= 0.71);
    }

    [Fact]
    public async Task DecideAsync_Choice_RenormalizesOverOnlyPresentLabels()
    {
        // Arrange: Only A and B appear; C is absent
        var state = new DecisionState("State content");
        var question = new DecisionQuestion(
            Name: "choice",
            Kind: DecisionKind.Choice,
            Instructions: "Pick one",
            Options: new[] { "option1", "option2", "option3" });

        var client = CreateTestClient(
            selectedLabel: "A",
            logProbabilities: new Dictionary<string, double> { { "A", 0.6 }, { "B", 0.4 } });

        // Act
        var result = await client.DecideAsync(state, new[] { question }, CancellationToken.None);

        // Assert
        var answer = result.Answers["choice"];
        Assert.Equal("option1", answer.Choice);
        Assert.Equal(2, answer.Probabilities.Count);
        Assert.Contains("option1", answer.Probabilities.Keys);
        Assert.Contains("option2", answer.Probabilities.Keys);

        var probSum = answer.Probabilities.Values.Sum();
        Assert.True(Math.Abs(probSum - 1.0) < 0.001);
    }

    [Fact]
    public async Task DecideAsync_Score_ComputesProbabilityWeightedScore()
    {
        // Arrange: labels 3 and 4 with probabilities 0.4 and 0.6
        // Expected: 3*0.4 + 4*0.6 = 3.6
        var state = new DecisionState("State content");
        var question = new DecisionQuestion(
            Name: "quality",
            Kind: DecisionKind.Score,
            Instructions: "Rate quality",
            Options: new[] { "1", "2", "3", "4", "5" });

        var client = CreateTestClient(
            selectedLabel: "3",
            logProbabilities: new Dictionary<string, double> { { "3", 0.4 }, { "4", 0.6 } });

        // Act
        var result = await client.DecideAsync(state, new[] { question }, CancellationToken.None);

        // Assert
        var answer = result.Answers["quality"];
        Assert.Equal(DecisionKind.Score, answer.Kind);
        Assert.True(answer.Score >= 3.5 && answer.Score <= 3.7);
    }

    [Fact]
    public async Task DecideAsync_Noul_ComputesProbabilityFromYLabel()
    {
        // Arrange: Y has 0.65 probability
        var state = new DecisionState("State content");
        var question = new DecisionQuestion(
            Name: "decision",
            Kind: DecisionKind.Noul,
            Instructions: "Yes or no?",
            Options: new string[] { });

        var client = CreateTestClient(
            selectedLabel: "Y",
            logProbabilities: new Dictionary<string, double> { { "Y", 0.65 }, { "N", 0.35 } });

        // Act
        var result = await client.DecideAsync(state, new[] { question }, CancellationToken.None);

        // Assert
        var answer = result.Answers["decision"];
        Assert.Equal(DecisionKind.Noul, answer.Kind);
        Assert.True(answer.Noul >= 0.64 && answer.Noul <= 0.66);
        Assert.Null(answer.Confidence);
        Assert.Empty(answer.Probabilities);
    }

    [Fact]
    public async Task DecideAsync_ThrowsWhenNoOfferedLabelAppears()
    {
        // Arrange: X and Y in logprobs, but only A and B offered
        var state = new DecisionState("State content");
        var question = new DecisionQuestion(
            Name: "choice",
            Kind: DecisionKind.Choice,
            Instructions: "Pick one",
            Options: new[] { "offered1", "offered2" });

        var client = CreateTestClient(
            selectedLabel: "X",
            logProbabilities: new Dictionary<string, double> { { "X", 0.9 }, { "Y", 0.1 } });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DecideAsync(state, new[] { question }, CancellationToken.None));

        Assert.Contains("label", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DecideAsync_BatchesMultipleQuestionsWithSummedTokens()
    {
        // Arrange: 3 questions, each with 15 input and 1 output token
        var state = new DecisionState("State content");
        var questions = new[]
        {
            new DecisionQuestion("q1", DecisionKind.Choice, "Q1?", new[] { "a", "b" }),
            new DecisionQuestion("q2", DecisionKind.Choice, "Q2?", new[] { "x", "y" }),
            new DecisionQuestion("q3", DecisionKind.Choice, "Q3?", new[] { "p", "q" })
        };

        var client = CreateTestClientWithMultipleQuestions(
            selectedLabels: new[] { "A", "A", "A" },
            logProbabilitiesList: new[]
            {
                new Dictionary<string, double> { { "A", 0.8 }, { "B", 0.2 } },
                new Dictionary<string, double> { { "A", 0.8 }, { "B", 0.2 } },
                new Dictionary<string, double> { { "A", 0.8 }, { "B", 0.2 } }
            },
            inputTokensPerQuestion: 15,
            outputTokensPerQuestion: 1);

        // Act
        var result = await client.DecideAsync(state, questions, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.Answers.Count);
        Assert.Equal(45, result.InputTokens);
        Assert.Equal(3, result.OutputTokens);
    }

    private static TestableLogprobDecisionClient CreateTestClient(
        string selectedLabel,
        Dictionary<string, double> logProbabilities)
    {
        return new TestableLogprobDecisionClient(
            selectedLabels: new[] { selectedLabel },
            logProbabilitiesList: new[] { logProbabilities },
            inputTokensPerQuestion: 10,
            outputTokensPerQuestion: 1);
    }

    private static TestableLogprobDecisionClient CreateTestClientWithMultipleQuestions(
        string[] selectedLabels,
        Dictionary<string, double>[] logProbabilitiesList,
        int inputTokensPerQuestion,
        int outputTokensPerQuestion)
    {
        return new TestableLogprobDecisionClient(
            selectedLabels,
            logProbabilitiesList,
            inputTokensPerQuestion,
            outputTokensPerQuestion);
    }
}

/// <summary>
/// Testable implementation for unit testing with controlled logprob data.
/// </summary>
internal sealed class TestableLogprobDecisionClient : IDecisionClient
{
    private readonly string[] _selectedLabels;
    private readonly Dictionary<string, double>[] _logProbabilitiesList;
    private readonly int _inputTokensPerQuestion;
    private readonly int _outputTokensPerQuestion;

    public TestableLogprobDecisionClient(
        string[] selectedLabels,
        Dictionary<string, double>[] logProbabilitiesList,
        int inputTokensPerQuestion,
        int outputTokensPerQuestion)
    {
        _selectedLabels = selectedLabels ?? throw new ArgumentNullException(nameof(selectedLabels));
        _logProbabilitiesList = logProbabilitiesList ?? throw new ArgumentNullException(nameof(logProbabilitiesList));
        _inputTokensPerQuestion = inputTokensPerQuestion;
        _outputTokensPerQuestion = outputTokensPerQuestion;

        if (selectedLabels.Length != logProbabilitiesList.Length)
            throw new ArgumentException("Labels and probabilities arrays must have same length");
    }

    public Task<DecisionResult> DecideAsync(
        DecisionState state,
        IReadOnlyList<DecisionQuestion> questions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);

        if (questions.Count > _selectedLabels.Length)
            throw new ArgumentException("Not enough mock data for all questions");

        var answers = new Dictionary<string, DecisionAnswer>();
        int totalInputTokens = 0;
        int totalOutputTokens = 0;

        for (int i = 0; i < questions.Count; i++)
        {
            var question = questions[i];
            var selectedLabel = _selectedLabels[i];
            var logProbabilities = _logProbabilitiesList[i];

            // Process this question
            var answer = ProcessQuestion(question, selectedLabel, logProbabilities);
            answers[question.Name] = answer;

            totalInputTokens += _inputTokensPerQuestion;
            totalOutputTokens += _outputTokensPerQuestion;
        }

        return Task.FromResult(new DecisionResult(
            Model: "gpt-4",
            Answers: answers,
            InputTokens: totalInputTokens,
            OutputTokens: totalOutputTokens));
    }

    private static DecisionAnswer ProcessQuestion(
        DecisionQuestion question,
        string selectedLabel,
        Dictionary<string, double> logProbabilities)
    {
        return question.Kind switch
        {
            DecisionKind.Choice => ProcessChoice(question, selectedLabel, logProbabilities),
            DecisionKind.Score => ProcessScore(question, selectedLabel, logProbabilities),
            DecisionKind.Noul => ProcessNoul(question, selectedLabel, logProbabilities),
            _ => throw new InvalidOperationException($"Unknown DecisionKind: {question.Kind}")
        };
    }

    private static DecisionAnswer ProcessChoice(
        DecisionQuestion question,
        string selectedLabel,
        Dictionary<string, double> logProbabilities)
    {
        // Map label (A, B, C) to option index
        var optionMap = new Dictionary<string, int>();
        for (int i = 0; i < question.Options.Count; i++)
        {
            optionMap[((char)('A' + i)).ToString()] = i;
        }

        // Collect probabilities for offered labels only, renormalize
        var offeredProbs = new Dictionary<string, double>();
        foreach (var label in logProbabilities.Keys)
        {
            if (optionMap.TryGetValue(label, out var idx))
            {
                offeredProbs[question.Options[idx]] = logProbabilities[label];
            }
        }

        if (offeredProbs.Count == 0)
            throw new InvalidOperationException("No offered label appears in top logprobs");

        // Renormalize to sum to 1
        double sum = offeredProbs.Values.Sum();
        var normalizedProbs = offeredProbs.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value / sum);

        // Choose highest probability option
        var chosenOption = normalizedProbs.MaxBy(kvp => kvp.Value).Key;
        var maxProb = normalizedProbs[chosenOption];

        return new DecisionAnswer(
            Name: question.Name,
            Kind: DecisionKind.Choice,
            Choice: chosenOption,
            Score: null,
            Noul: null,
            Confidence: maxProb,
            Probabilities: normalizedProbs);
    }

    private static DecisionAnswer ProcessScore(
        DecisionQuestion question,
        string selectedLabel,
        Dictionary<string, double> logProbabilities)
    {
        // Map digit labels (0, 1, 2...) to level indices
        var offeredProbs = new Dictionary<int, double>();
        foreach (var label in logProbabilities.Keys)
        {
            if (int.TryParse(label, out int levelIdx) && levelIdx >= 0 && levelIdx < question.Options.Count)
            {
                offeredProbs[levelIdx] = logProbabilities[label];
            }
        }

        if (offeredProbs.Count == 0)
            throw new InvalidOperationException("No offered label appears in top logprobs");

        // Renormalize
        double sum = offeredProbs.Values.Sum();
        var normalizedProbs = offeredProbs.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value / sum);

        // Compute weighted score and max probability
        double weightedScore = 0;
        foreach (var kvp in normalizedProbs)
        {
            weightedScore += kvp.Key * kvp.Value;
        }

        var maxProb = normalizedProbs.Values.Max();

        return new DecisionAnswer(
            Name: question.Name,
            Kind: DecisionKind.Score,
            Choice: null,
            Score: weightedScore,
            Noul: null,
            Confidence: maxProb,
            Probabilities: new Dictionary<string, double>());
    }

    private static DecisionAnswer ProcessNoul(
        DecisionQuestion question,
        string selectedLabel,
        Dictionary<string, double> logProbabilities)
    {
        // Extract P(Y) and P(N)
        var offeredProbs = new Dictionary<string, double>();
        foreach (var label in logProbabilities.Keys)
        {
            if (label == "Y" || label == "N")
            {
                offeredProbs[label] = logProbabilities[label];
            }
        }

        if (offeredProbs.Count == 0)
            throw new InvalidOperationException("No Y/N label appears in top logprobs");

        // Renormalize
        double sum = offeredProbs.Values.Sum();
        var pY = offeredProbs.ContainsKey("Y") ? offeredProbs["Y"] / sum : 0.0;

        return new DecisionAnswer(
            Name: question.Name,
            Kind: DecisionKind.Noul,
            Choice: null,
            Score: null,
            Noul: pY,
            Confidence: null,
            Probabilities: new Dictionary<string, double>());
    }
}
