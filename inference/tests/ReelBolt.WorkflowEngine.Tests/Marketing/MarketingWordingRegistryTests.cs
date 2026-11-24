#nullable enable
using System.IO;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// saas-promotions T-004: the consent-wording registry. A published version's text is evidence of
/// what a person agreed to, so it is never edited (GDPR 7(1), 5(2)): new text needs a new version.
/// </summary>
public sealed class MarketingWordingRegistryTests
{
    private static readonly MarketingWording Shipped = MarketingWording.LoadFile(MarketingWording.DefaultPath);

    [Fact]
    public void The_registry_ships_next_to_the_assembly()
    {
        File.Exists(MarketingWording.DefaultPath).Should().BeTrue();
        MarketingWording.DefaultPath.Replace('\\', '/').Should().EndWith("Marketing/marketing-consent.wording.json");
    }

    [Fact]
    public void Version_2026_10_marketing_v1_keeps_its_exact_text()
    {
        Shipped.TextOf("2026-10-marketing-v1").Should().Be(
            "Email me ReelBolt product news, tips and offers. You can unsubscribe at any time from any email or in Settings. See our Privacy Policy.");
    }

    [Fact]
    public void The_current_version_is_one_of_the_published_versions()
    {
        Shipped.Current.Should().NotBeNullOrWhiteSpace();
        Shipped.TextOf(Shipped.Current).Should().NotBeNullOrWhiteSpace();
        Shipped.CurrentText.Should().Be(Shipped.TextOf(Shipped.Current));
    }

    [Fact]
    public void An_unknown_version_has_no_text()
    {
        Shipped.TextOf("1999-01-marketing-v0").Should().BeNull();
        Shipped.TextOf(null).Should().BeNull();
    }

    [Fact]
    public void A_registry_whose_current_version_is_not_published_is_rejected()
    {
        FluentActions.Invoking(() => MarketingWording.Load("""{"current":"b","versions":{"a":"text"}}"""))
            .Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => MarketingWording.Load("""{"current":"a","versions":{"a":""}}"""))
            .Should().Throw<InvalidDataException>();
    }
}
