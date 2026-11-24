using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

public sealed class DecisionProbabilityTests
{
    private static DecisionAnswer Answer(
        DecisionKind kind, double? noul = null, IReadOnlyDictionary<string, double>? probabilities = null) =>
        new(
            Name: "q1",
            Kind: kind,
            Choice: null,
            Score: null,
            Noul: noul,
            Confidence: null,
            Probabilities: probabilities ?? new Dictionary<string, double>());

    [Fact]
    public void TopTwo_WithNoulKind_DerivesThePairFromNoul()
    {
        (double top, double second) = DecisionProbability.TopTwo(Answer(DecisionKind.Noul, noul: 0.9));

        top.Should().Be(0.9);
        second.Should().BeApproximately(0.1, 1e-12);
    }

    [Fact]
    public void TopTwo_WithNoulKindBelowAHalf_ReturnsTheComplementAsTop()
    {
        // p(yes) = 0.2 means the model is confident the answer is "no": the top is 0.8, not 0.2.
        (double top, double second) = DecisionProbability.TopTwo(Answer(DecisionKind.Noul, noul: 0.2));

        top.Should().BeApproximately(0.8, 1e-12);
        second.Should().BeApproximately(0.2, 1e-12);
    }

    [Fact]
    public void TopTwo_WithNoulKindAndPopulatedProbabilities_StillUsesTheNoulBranch()
    {
        // A Noul answer has no option set; a stray probability map must not override its own kind.
        var probabilities = new Dictionary<string, double> { { "yes", 0.1 }, { "no", 0.9 } };

        (double top, double second) = DecisionProbability.TopTwo(Answer(DecisionKind.Noul, noul: 0.8, probabilities));

        top.Should().BeApproximately(0.8, 1e-12);
        second.Should().BeApproximately(0.2, 1e-12);
    }

    [Fact]
    public void TopTwo_WithEmptyProbabilities_DerivesThePairFromNoul()
    {
        (double top, double second) = DecisionProbability.TopTwo(Answer(DecisionKind.Choice, noul: 0.7));

        top.Should().Be(0.7);
        second.Should().BeApproximately(0.3, 1e-12);
    }

    [Fact]
    public void TopTwo_WithEmptyProbabilitiesAndNoNoul_ReadsAsConfidentNo()
    {
        // p = 0.0 is a confident "no", so the top is 1.0. Preserved phase-1 semantics: an answer
        // with no distribution is not read as a zero-confidence answer.
        (double top, double second) = DecisionProbability.TopTwo(Answer(DecisionKind.Choice));

        top.Should().Be(1.0);
        second.Should().Be(0.0);
    }

    [Fact]
    public void TopTwo_WithTwoValues_ReturnsThemDescending()
    {
        var probabilities = new Dictionary<string, double> { { "a", 0.3 }, { "b", 0.7 } };

        (double top, double second) = DecisionProbability.TopTwo(Answer(DecisionKind.Choice, probabilities: probabilities));

        top.Should().Be(0.7);
        second.Should().Be(0.3);
    }

    [Fact]
    public void TopTwo_WithMoreThanTwoValues_ReturnsTheTwoLargest()
    {
        var probabilities = new Dictionary<string, double> { { "a", 0.2 }, { "b", 0.5 }, { "c", 0.3 } };

        (double top, double second) = DecisionProbability.TopTwo(Answer(DecisionKind.Choice, probabilities: probabilities));

        top.Should().Be(0.5);
        second.Should().Be(0.3);
    }

    [Fact]
    public void TopTwo_WithASingleValue_ReturnsAZeroSecond()
    {
        // Second = 0.0 when only one value exists, so the margin is the whole of Top and a
        // single-option answer is not penalised into escalating.
        var probabilities = new Dictionary<string, double> { { "only", 0.6 } };

        (double top, double second) = DecisionProbability.TopTwo(Answer(DecisionKind.Choice, probabilities: probabilities));

        top.Should().Be(0.6);
        second.Should().Be(0.0);
    }

    [Fact]
    public void TopTwo_WithANullAnswer_Throws()
    {
        Action act = () => DecisionProbability.TopTwo(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
