using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The pricing basis one <c>VideoGenerate</c> step attempt prices against (the async price oracle, T-901).
/// </summary>
/// <remarks>
/// <para>
/// Two arms, one type, and the difference between them is the whole point of this file: a client
/// that is NOT an <see cref="IVideoGenerationPriceOracle"/> (MiniMax) keeps the FULL published
/// capability set from <see cref="IVideoGenerationClient.Describe"/>, and a client that IS one is
/// priced by the vendor's own quote, whose capability set is a POINT at the configuration it
/// answered for.
/// </para>
/// <para>
/// NO NETWORK, NO VENDOR KEY. Every oracle here is a Moq proxy built with
/// <c>Mock&lt;IVideoGenerationClient&gt;.As&lt;IVideoGenerationPriceOracle&gt;()</c> that answers
/// from memory or throws. <c>Describe</c> is a pure, compiled-in table lookup, and nothing in this
/// file reads an API key from the environment or requires one.
/// </para>
/// <para>
/// THE EXACT FIGURES. A vendor quote of <c>$5.00</c> for a <c>3s</c> clip derives a per-second rate
/// of <c>1.6666666666666666666666666667m</c> whose re-multiplication by 3 is
/// <c>5.0000000000000000000000000001m</c> — a DIFFERENT <see cref="decimal"/> from the vendor's own
/// <c>5.00m</c>. <c>$10.00</c> at 3s was tried first and is NOT usable as a discriminator: .NET
/// rounds <c>(10m / 3m) * 3m</c> back to a value equal to <c>10m</c>, which would make every
/// assertion below vacuous. See <see cref="The_quoted_separation_this_file_rests_on"/>.
/// </para>
/// </remarks>
public class VideoGenerationPricingBasisTests
{
    private const string Model = "hf-ep-9f1c2b";
    private const string Resolution = "1080P";
    private const string Ratio = "16:9";
    private const int QuotedDurationSeconds = 3;
    private const decimal QuotedTotalUsd = 5.00m;

    /// <summary>
    /// What a re-multiplied per-second rate would produce for the quote above: the figure a
    /// rate-derived pricing path would charge instead of the vendor's own total.
    /// </summary>
    private const decimal ReMultipliedFromTheRate = 5.0000000000000000000000000001m;

    // ==========================================================================================
    // ResolveAsync: the client's own type selects the arm
    // ==========================================================================================

    /// <summary>
    /// The Moq detail this suite rests on, pinned in one place: a
    /// <c>Mock&lt;IVideoGenerationClient&gt;</c> that has had
    /// <c>.As&lt;IVideoGenerationPriceOracle&gt;()</c> called on it really does satisfy
    /// <c>client is IVideoGenerationPriceOracle</c> inside <c>ResolveAsync</c>, so the ORACLE arm
    /// runs. A test that silently exercised the static arm instead would be worse than no test.
    /// </summary>
    /// <remarks>
    /// Moq is strict about the order here: <c>As&lt;T&gt;()</c> must be called BEFORE the mock's
    /// <c>Object</c> property is first read, or it throws "Mock type has already been initialized".
    /// Every fixture in this file adds the oracle first, for that reason — and this test is what
    /// would catch a future one that did not, because accessing <c>Object</c> is the mistake.
    /// </remarks>
    [Fact]
    public void A_client_mocked_with_As_really_is_seen_as_an_oracle()
    {
        var plain = new Mock<IVideoGenerationClient>();

        plain.Object.Should().NotBeAssignableTo<IVideoGenerationPriceOracle>(
            "a plain Mock<IVideoGenerationClient> is not an oracle — that is MiniMax's shape");

        var oracle = new Mock<IVideoGenerationClient>();
        oracle.As<IVideoGenerationPriceOracle>();

        oracle.Object.Should().BeAssignableTo<IVideoGenerationPriceOracle>(
            "the proxy implements every interface asked for through As<T>, and that is what ResolveAsync tests");
    }

    [Fact]
    public async Task ResolveAsync_prices_an_oracle_client_at_the_exact_configuration_it_asked_about()
    {
        var quote = VendorQuote();
        var seen = new List<VideoGenerationPriceQuery>();
        var client = OracleClient(quote, DecoyDescribe(), seen);

        var basis = await VideoGenerationPricingBasis.ResolveAsync(
            client.Object, Query(), CancellationToken.None);

        basis.IsOraclePriced.Should().BeTrue();
        basis.Quote.Should().BeSameAs(quote);

        seen.Should().ContainSingle().Which.Should().Be(
            Query(), "the oracle is asked about exactly the configuration the step will submit");

        client.Verify(
            c => c.Describe(It.IsAny<string>()),
            Times.Never,
            "the oracle arm must NEVER consult the fail-closed static table — that table is why a Higgsfield model offered zero models");

        // The capability set is the quote's own POINT: the one resolution, the one ratio, and the
        // one duration the vendor actually priced — not the whole 5..10s range Describe publishes.
        basis.Capabilities.MinDurationSeconds.Should().Be(QuotedDurationSeconds);
        basis.Capabilities.MaxDurationSeconds.Should().Be(QuotedDurationSeconds);
        basis.Capabilities.AllowedRatios.Should().Equal(Ratio);
        basis.Capabilities.PricePerSecondByResolution.Should().HaveCount(1);
        basis.Capabilities.PricePerSecondByResolution[Resolution].Should().Be(quote.PricePerSecondUsd);
    }

    [Fact]
    public async Task ResolveAsync_keeps_the_FULL_published_capability_set_for_a_client_that_is_not_an_oracle()
    {
        var published = PublishedCapabilities();
        var client = StaticClient(published);

        client.Object.Should().NotBeAssignableTo<IVideoGenerationPriceOracle>();

        var basis = await VideoGenerationPricingBasis.ResolveAsync(
            client.Object, Query(), CancellationToken.None);

        basis.IsOraclePriced.Should().BeFalse();
        basis.Quote.Should().BeNull();
        basis.Capabilities.Should().BeSameAs(published, "the non-oracle arm takes Describe's own object, unmodified");
        client.Verify(c => c.Describe(Model), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_rejects_a_null_client()
    {
        Func<Task> act = () => VideoGenerationPricingBasis.ResolveAsync(null!, Query(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ResolveAsync_rejects_a_null_query()
    {
        var client = StaticClient(PublishedCapabilities()).Object;

        Func<Task> act = () => VideoGenerationPricingBasis.ResolveAsync(client, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ==========================================================================================
    // ClipCostUsd: the vendor's own figure, or a refusal — never a re-derived rate, never 0
    // ==========================================================================================

    /// <summary>
    /// The separation this file rests on, asserted rather than assumed: for this quote the vendor's
    /// own total and the re-multiplied per-second rate are different decimals, so every assertion
    /// below genuinely discriminates between the two pricing paths.
    /// </summary>
    [Fact]
    public void The_quoted_separation_this_file_rests_on()
    {
        var quote = VendorQuote();

        decimal reMultiplied = quote.PricePerSecondUsd * quote.DurationSeconds;

        reMultiplied.Should().Be(ReMultipliedFromTheRate);
        reMultiplied.Should().NotBe(
            quote.TotalUsd, "this pair is chosen precisely because the derived rate does NOT round-trip");
    }

    [Fact]
    public void ClipCostUsd_is_the_vendors_own_total_and_not_the_re_multiplied_rate()
    {
        var basis = new VideoGenerationPricingBasis(VendorQuote().ToCapabilities(), VendorQuote());

        var cost = basis.ClipCostUsd(Resolution, Ratio, QuotedDurationSeconds);

        cost.Should().Be(QuotedTotalUsd);
        cost.Should().NotBe(
            ReMultipliedFromTheRate,
            "re-deriving the price from the rate drifts from the figure the vendor actually quoted");
    }

    /// <summary>
    /// NO-ZERO TEST — an oracle-priced model refuses a configuration it holds no quote for rather
    /// than pricing it at 0, at a re-derived rate, or at the quoted figure for a different
    /// configuration. The comparison is ordinal, with no case folding and no trimming, matching
    /// every other resolution/ratio lookup in this feature: <c>1080p</c> is not <c>1080P</c>.
    /// </summary>
    [Theory]
    [InlineData("720P", "16:9", 3)]
    [InlineData("1080p", "16:9", 3)]
    [InlineData("1080P", "9:16", 3)]
    [InlineData("1080P", "16:9", 4)]
    public void An_oracle_priced_model_refuses_a_configuration_it_has_no_quote_for(
        string resolution, string ratio, int durationSeconds)
    {
        var basis = new VideoGenerationPricingBasis(VendorQuote().ToCapabilities(), VendorQuote());

        Action act = () => basis.ClipCostUsd(resolution, ratio, durationSeconds);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{resolution}/{ratio}/{durationSeconds}s has no price*");
    }

    [Fact]
    public void A_cost_is_never_zero_on_either_arm()
    {
        var oraclePriced = new VideoGenerationPricingBasis(VendorQuote().ToCapabilities(), VendorQuote());
        var tablePriced = new VideoGenerationPricingBasis(PublishedCapabilities());

        oraclePriced.ClipCostUsd(Resolution, Ratio, QuotedDurationSeconds).Should().BePositive();
        tablePriced.ClipCostUsd("768P", Ratio, 15).Should().BePositive();
    }

    [Fact]
    public void With_no_quote_ClipCostUsd_is_the_published_per_second_path_unchanged()
    {
        var published = PublishedCapabilities();
        var basis = new VideoGenerationPricingBasis(published);

        basis.ClipCostUsd("768P", Ratio, 15)
            .Should().Be(VideoGenerationPricing.EstimateClipCostUsd(published, "768P", 15))
            .And.Be(0.08m * 15);
    }

    [Fact]
    public void With_no_quote_an_unpriced_resolution_still_throws_the_exception_it_always_did()
    {
        var basis = new VideoGenerationPricingBasis(PublishedCapabilities());

        Action act = () => basis.ClipCostUsd("4K", Ratio, 10);

        act.Should().Throw<ArgumentException>();
    }

    // ==========================================================================================
    // EstimateWholePlanUsd: exact sums of the vendor's own quotes, with the estimator's own guards
    // ==========================================================================================

    [Fact]
    public void With_no_quote_the_whole_plan_estimate_is_the_published_estimators_own_number()
    {
        var published = PublishedCapabilities();
        var basis = new VideoGenerationPricingBasis(published);
        var shots = new[] { Shot("Short"), Shot("Long") };

        var fromBasis = basis.EstimateWholePlanUsd("768P", Ratio, shots, takes: 2);

        fromBasis.Should().Be(ShotPlanCostEstimator.EstimateWholePlanUsd(published, "768P", shots, 2));
        fromBasis.Should().Be(
            (0.08m * 5 + 0.08m * 10) * 2,
            "Short snaps to the model's own minimum and Long to its maximum");
    }

    [Fact]
    public void An_oracle_priced_plan_is_the_exact_sum_of_the_vendors_own_quotes_per_shot_and_take()
    {
        var quote = VendorQuote();
        var basis = new VideoGenerationPricingBasis(quote.ToCapabilities(), quote);

        // Every word snaps to the POINT the quote priced, because that is the only duration the
        // vendor answered for — so the plan is an exact sum of the vendor's own figure.
        var total = basis.EstimateWholePlanUsd(
            Resolution, Ratio, new[] { Shot("Short"), Shot("Medium"), Shot("Long") }, takes: 2);

        total.Should().Be(QuotedTotalUsd * 3 * 2);
    }

    [Fact]
    public void An_empty_plan_is_the_one_legitimate_zero_on_both_arms()
    {
        var quote = VendorQuote();

        new VideoGenerationPricingBasis(quote.ToCapabilities(), quote)
            .EstimateWholePlanUsd(Resolution, Ratio, Array.Empty<ShotPlan>(), takes: 1)
            .Should().Be(0m, "there is genuinely nothing to buy");

        new VideoGenerationPricingBasis(PublishedCapabilities())
            .EstimateWholePlanUsd("768P", Ratio, Array.Empty<ShotPlan>(), takes: 1)
            .Should().Be(0m);
    }

    [Fact]
    public void The_oracle_arm_mirrors_the_estimators_own_guards_rather_than_answering_free()
    {
        var quote = VendorQuote();
        var basis = new VideoGenerationPricingBasis(quote.ToCapabilities(), quote);

        Action noTakes = () => basis.EstimateWholePlanUsd(Resolution, Ratio, new[] { Shot("Short") }, takes: 0);
        Action nullShot = () => basis.EstimateWholePlanUsd(Resolution, Ratio, new ShotPlan[] { null! }, takes: 1);
        Action badWord = () => basis.EstimateWholePlanUsd(Resolution, Ratio, new[] { Shot("Epic") }, takes: 1);
        Action nullShots = () => basis.EstimateWholePlanUsd(Resolution, Ratio, null!, takes: 1);

        noTakes.Should().Throw<ArgumentOutOfRangeException>("takes < 1 would otherwise price the plan as free");
        nullShot.Should().Throw<ArgumentException>("an unreadable shot must not be counted as free");
        badWord.Should().Throw<ArgumentException>("the snapped duration drives the cost, so it is never defaulted");
        nullShots.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_oracle_arms_takes_guard_reads_exactly_like_the_published_estimators()
    {
        var quote = VendorQuote();
        var oracleArm = new VideoGenerationPricingBasis(quote.ToCapabilities(), quote);
        var publishedArm = new VideoGenerationPricingBasis(PublishedCapabilities());

        Action onOracle = () => oracleArm.EstimateWholePlanUsd(Resolution, Ratio, new[] { Shot("Short") }, takes: 0);
        Action onPublished = () => publishedArm.EstimateWholePlanUsd("768P", Ratio, new[] { Shot("Short") }, takes: 0);

        onOracle.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*never priced as free*");
        onPublished.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*never priced as free*");
    }

    // ==========================================================================================
    // Fixtures
    // ==========================================================================================

    private static VideoGenerationPriceQuery Query() =>
        new(Model, Resolution, Ratio, QuotedDurationSeconds);

    /// <summary>
    /// The vendor's own quote for one <see cref="Resolution"/> <see cref="Ratio"/> clip of
    /// <see cref="QuotedDurationSeconds"/>s. Every argument is one the shared quote type accepts:
    /// it has no way to represent a non-positive price.
    /// </summary>
    private static VideoGenerationPriceQuote VendorQuote() =>
        new(
            Model,
            Resolution,
            Ratio,
            QuotedDurationSeconds,
            pricePerSecondUsd: QuotedTotalUsd / QuotedDurationSeconds,
            totalUsd: QuotedTotalUsd,
            credits: 12,
            source: VideoGenerationPriceSource.VendorEstimate);

    /// <summary>
    /// What a table-priced provider publishes: a whole RANGE of durations and one rate per
    /// resolution. Deliberately different from the quote's own answer, so a test that took the
    /// wrong arm fails on the values instead of passing by accident.
    /// </summary>
    private static VideoGenerationCapabilities PublishedCapabilities() =>
        new(
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["768P"] = 0.08m },
            MinDurationSeconds: 5,
            MaxDurationSeconds: 10,
            AllowedRatios: new[] { Ratio });

    /// <summary>What <c>Describe</c> would answer on a provider that DOES publish a table.</summary>
    private static VideoGenerationCapabilities DecoyDescribe() => PublishedCapabilities();

    private static Mock<IVideoGenerationClient> StaticClient(VideoGenerationCapabilities capabilities)
    {
        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns(capabilities);
        return client;
    }

    /// <summary>
    /// A client that is BOTH a video-generation client and a price oracle, exactly as the ticket
    /// describes: start from <c>Mock&lt;IVideoGenerationClient&gt;</c> and add
    /// <c>.As&lt;IVideoGenerationPriceOracle&gt;()</c>. <paramref name="seen"/> records the queries
    /// the oracle was actually asked.
    /// </summary>
    private static Mock<IVideoGenerationClient> OracleClient(
        VideoGenerationPriceQuote quote,
        VideoGenerationCapabilities describe,
        List<VideoGenerationPriceQuery>? seen = null)
    {
        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns(describe);
        client.As<IVideoGenerationPriceOracle>()
            .Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .Callback<VideoGenerationPriceQuery, CancellationToken>((q, _) => seen?.Add(q))
            .ReturnsAsync(quote);
        return client;
    }

    private static ShotPlan Shot(string durationWord) => new()
    {
        Purpose = "Cutaway",
        Anchor = "s1",
        Camera = "Static",
        Duration = durationWord,
        Prompt = $"A shot that is {durationWord}",
        Reason = "test"
    };
}
