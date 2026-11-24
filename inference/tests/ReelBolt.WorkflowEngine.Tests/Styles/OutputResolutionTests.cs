using AwesomeAssertions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>
/// The run's output resolution (short side in pixels): parsing, the plan-cap arithmetic the engine and
/// the estimator share, and the plan gate that refuses a choice above the plan.
/// </summary>
public class OutputResolutionTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("Auto", null)]
    [InlineData(" auto ", null)]
    [InlineData("720p", 720)]
    [InlineData("720", 720)]
    [InlineData("1080P", 1080)]
    [InlineData("1080", 1080)]
    [InlineData("2160p", 2160)]
    [InlineData("2160", 2160)]
    [InlineData("4K", 2160)]
    [InlineData("4k", 2160)]
    public void Allowed_values_parse_to_their_short_side(string? input, int? expected)
    {
        OutputResolution.TryParse(input, out int? shortSide).Should().BeTrue();
        shortSide.Should().Be(expected);
    }

    [Theory]
    [InlineData("1440p")]
    [InlineData("1440")]
    [InlineData("480p")]
    [InlineData("8K")]
    [InlineData("1920x1080")]
    [InlineData("hd")]
    public void Anything_else_is_refused(string input)
    {
        OutputResolution.TryParse(input, out int? shortSide).Should().BeFalse();
        shortSide.Should().BeNull();
    }

    [Fact]
    public void The_allowed_list_and_labels_are_the_closed_set()
    {
        OutputResolution.Allowed.Should().Equal(720, 1080, 2160);
        OutputResolution.Label(720).Should().Be("720p");
        OutputResolution.Label(1080).Should().Be("1080p");
        OutputResolution.Label(2160).Should().Be("2160p");
        OutputResolution.DisplayName(2160).Should().Be("4K (2160p)");
        OutputResolution.DisplayName(1080).Should().Be("1080p");
        OutputResolution.AutoCeiling.Should().Be(1080);
    }

    [Theory]
    [InlineData(OrganizationEntitlementSet.UnlimitedInt, null, OrganizationEntitlementSet.UnlimitedInt)]
    [InlineData(2160, null, 1080)]
    [InlineData(1080, null, 1080)]
    [InlineData(720, null, 720)]
    [InlineData(2160, 2160, 2160)]
    [InlineData(1080, 2160, 1080)]
    [InlineData(2160, 720, 720)]
    [InlineData(OrganizationEntitlementSet.UnlimitedInt, 2160, 2160)]
    public void Effective_max_height_keeps_auto_at_todays_output(int plan, int? chosen, int expected)
    {
        OutputResolution.EffectiveMaxHeight(plan, chosen).Should().Be(expected);
    }

    [Fact]
    public void Pixel_factor_scales_with_the_square_of_the_short_side()
    {
        OutputResolution.PixelFactor(null).Should().Be(1.0);
        OutputResolution.PixelFactor(720).Should().BeApproximately(0.444, 0.001);
        OutputResolution.PixelFactor(1080).Should().Be(1.0);
        OutputResolution.PixelFactor(2160).Should().Be(4.0);
    }

    [Fact]
    public void A_1080_class_canvas_scales_to_even_sizes()
    {
        OutputResolution.Scale1080Class(1080, 1920, 720).Should().Be((720, 1280));
        OutputResolution.Scale1080Class(1080, 1350, 2160).Should().Be((2160, 2700));
        OutputResolution.Scale1080Class(1920, 1080, 2160).Should().Be((3840, 2160));
        OutputResolution.Scale1080Class(1080, 1080, 1080).Should().Be((1080, 1080));
        OutputResolution.Scale1080Class(1080, 1350, 720).Should().Be((720, 900));
    }

    // --- PlanGate.CheckResolution ------------------------------------------

    private static readonly OrganizationEntitlementSet Creator = new() { Tier = "creator", MaxOutputHeight = 1080 };
    private static readonly OrganizationEntitlementSet Pro = new() { Tier = "pro", MaxOutputHeight = 2160 };

    [Fact]
    public void Creator_asking_for_4K_is_refused_with_a_402_body()
    {
        PlanGateViolation? v = PlanGate.CheckResolution(2160, Creator);

        v.Should().NotBeNull();
        v!.Code.Should().Be("PLAN_NOT_ENTITLED");
        v.Feature.Should().Be(PlanGate.ResolutionFeature).And.Be("resolution");
        v.StatusCode.Should().Be(402);
        v.Limit.Should().Be(1080);
        v.Current.Should().Be(2160);
        v.Message.Should().Be("Your plan renders up to 1080p. Choose 1080p or lower, or upgrade your plan to render at 4K (2160p).");
    }

    [Fact]
    public void Pro_asking_for_4K_passes()
    {
        PlanGate.CheckResolution(2160, Pro).Should().BeNull();
    }

    [Fact]
    public void No_choice_always_passes()
    {
        PlanGate.CheckResolution(null, Creator).Should().BeNull();
        PlanGate.CheckResolution(null, new OrganizationEntitlementSet { MaxOutputHeight = 720 }).Should().BeNull();
    }

    [Fact]
    public void A_choice_at_or_below_the_cap_passes_and_unlimited_never_refuses()
    {
        PlanGate.CheckResolution(1080, Creator).Should().BeNull();
        PlanGate.CheckResolution(720, Creator).Should().BeNull();
        PlanGate.CheckResolution(2160, OrganizationEntitlementSet.Unlimited).Should().BeNull();
        PlanGate.CheckResolution(1080, new OrganizationEntitlementSet { MaxOutputHeight = 720 })!.Message
            .Should().Be("Your plan renders up to 720p. Choose 720p or lower, or upgrade your plan to render at 1080p.");
    }

    [Fact]
    public void The_refusal_echoes_a_short_value_whole()
    {
        OutputResolution.UnknownMessage("1440p").Should().Be("Unknown output resolution '1440p'. Use Auto, 720p, 1080p or 2160p.");
        OutputResolution.UnknownMessage(new string('x', 32))
            .Should().Be($"Unknown output resolution '{new string('x', 32)}'. Use Auto, 720p, 1080p or 2160p.");
    }

    [Fact]
    public void The_refusal_truncates_a_long_value_to_32_characters()
    {
        string junk = new string('a', 30) + "bcdefgh" + new string('z', 5000);

        OutputResolution.UnknownMessage(junk)
            .Should().Be($"Unknown output resolution '{new string('a', 30)}bc\u2026'. Use Auto, 720p, 1080p or 2160p.");
    }
}
