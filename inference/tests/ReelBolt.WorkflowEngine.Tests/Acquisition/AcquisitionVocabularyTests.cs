#nullable enable
using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Acquisition;

/// <summary>
/// The closed sets of the new-user funnel (docs/design/new-user-funnel.md section 4), pinned token by
/// token: the site, the app, the Go API and BI use the same lists, so a change here is a change to a
/// cross-service contract and to the database CHECK constraints built from these lists.
/// </summary>
public class AcquisitionVocabularyTests
{
    [Fact]
    public void Personas_are_exactly_the_seven_of_the_design()
    {
        AcquisitionVocabulary.Personas.Should().Equal(
            "creator", "business", "educator", "nonprofit", "software", "agency", "other");
        AcquisitionVocabulary.OtherPersona.Should().Be("other");
    }

    [Fact]
    public void Materials_are_exactly_footage_code_and_idea()
    {
        AcquisitionVocabulary.Materials.Should().Equal("footage", "code", "idea");
    }

    [Fact]
    public void Link_channels_are_exactly_the_nineteen_of_the_design()
    {
        AcquisitionVocabulary.LinkChannels.Should().Equal(
            "hackernews", "reddit", "linkedin", "x", "producthunt", "youtube", "tiktok", "instagram", "facebook",
            "github", "discord", "newsletter", "email", "blog", "directory", "press", "partner", "ads", "other");
    }

    [Fact]
    public void Link_media_are_exactly_the_eight_of_the_design()
    {
        AcquisitionVocabulary.LinkMedia.Should().Equal(
            "social", "community", "video", "email", "referral", "paid", "owned", "press");
    }

    [Fact]
    public void Attribution_captures_are_cookie_and_url()
    {
        AcquisitionVocabulary.AttributionCaptures.Should().Equal("cookie", "url");
    }

    [Fact]
    public void Every_token_is_lower_case_ascii_and_fits_its_column()
    {
        // persona, material, channel and medium columns are varchar(32); capture is varchar(16).
        foreach (string token in AcquisitionVocabulary.Personas
                     .Concat(AcquisitionVocabulary.Materials)
                     .Concat(AcquisitionVocabulary.LinkChannels)
                     .Concat(AcquisitionVocabulary.LinkMedia))
        {
            token.Should().MatchRegex("^[a-z0-9]{1,32}$");
        }

        AcquisitionVocabulary.AttributionCaptures.Should().OnlyContain(c => c.Length <= 16 && c.All(char.IsAsciiLetterLower));
    }

    [Fact]
    public void Channels_and_media_are_valid_utm_values()
    {
        // utm_source = channel and utm_medium = medium on a tracked link, so each must survive the UTM sanitizer.
        AcquisitionVocabulary.LinkChannels.Should().OnlyContain(c => AcquisitionVocabulary.IsUtmValue(c));
        AcquisitionVocabulary.LinkMedia.Should().OnlyContain(m => AcquisitionVocabulary.IsUtmValue(m));
    }

    [Theory]
    [InlineData("creator", true)]
    [InlineData("other", true)]
    [InlineData("Creator", false)]
    [InlineData(" creator", false)]
    [InlineData("developer", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPersona_is_an_exact_match(string? value, bool expected)
    {
        AcquisitionVocabulary.IsPersona(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("footage", true)]
    [InlineData("code", true)]
    [InlineData("idea", true)]
    [InlineData("Footage", false)]
    [InlineData("video", false)]
    [InlineData(null, false)]
    public void IsMaterial_is_an_exact_match(string? value, bool expected)
    {
        AcquisitionVocabulary.IsMaterial(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("hackernews", true)]
    [InlineData("x", true)]
    [InlineData("twitter", false)]
    [InlineData("HackerNews", false)]
    [InlineData(null, false)]
    public void IsLinkChannel_is_an_exact_match(string? value, bool expected)
    {
        AcquisitionVocabulary.IsLinkChannel(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("community", true)]
    [InlineData("paid", true)]
    [InlineData("cpc", false)]
    [InlineData("Social", false)]
    [InlineData(null, false)]
    public void IsLinkMedium_is_an_exact_match(string? value, bool expected)
    {
        AcquisitionVocabulary.IsLinkMedium(value).Should().Be(expected);
    }

    [Fact]
    public void The_link_code_pattern_is_the_one_of_the_design()
    {
        AcquisitionVocabulary.LinkCodePattern.Should().Be("^[a-z0-9][a-z0-9-]{1,39}$");
        AcquisitionVocabulary.UtmValuePattern.Should().Be("^[a-z0-9][a-z0-9._~+-]{0,63}$");
    }

    [Theory]
    [InlineData("hn-launch", true)]
    [InlineData("a1", true)]
    [InlineData("9-to-5", true)]
    [InlineData("a", false)] // at least two characters
    [InlineData("-hn", false)] // may not start with a hyphen
    [InlineData("HN-launch", false)]
    [InlineData("hn_launch", false)]
    [InlineData("hn.launch", false)]
    [InlineData("hn launch", false)]
    [InlineData("hn/launch", false)]
    [InlineData("hn-launch\n", false)] // a trailing newline is not part of a code
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLinkCode_follows_the_pattern(string? value, bool expected)
    {
        AcquisitionVocabulary.IsLinkCode(value).Should().Be(expected);
    }

    [Fact]
    public void A_link_code_is_at_most_forty_characters_and_always_a_utm_value()
    {
        string forty = new('a', 40);
        AcquisitionVocabulary.IsLinkCode(forty).Should().BeTrue();
        AcquisitionVocabulary.IsLinkCode(forty + "a").Should().BeFalse();
        AcquisitionVocabulary.LinkCodeMaxLength.Should().Be(40);
        // The code is the link's utm_content, so whatever the code pattern admits the UTM sanitizer keeps.
        foreach (string code in new[] { "hn-launch", "a1", "9-to-5", forty })
        {
            AcquisitionVocabulary.IsUtmValue(code).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("launch-2026-11", true)]
    [InlineData("spring.sale_v2", true)]
    [InlineData("a/b", false)]
    [InlineData("a%20b", false)]
    [InlineData("a+b~c.d", true)]
    [InlineData("Launch", false)]
    [InlineData(".launch", false)]
    [InlineData("launch 2026", false)]
    [InlineData("launch\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUtmValue_follows_the_pattern(string? value, bool expected)
    {
        AcquisitionVocabulary.IsUtmValue(value).Should().Be(expected);
    }

    [Fact]
    public void A_utm_value_is_at_most_sixty_four_characters()
    {
        AcquisitionVocabulary.IsUtmValue(new string('a', 64)).Should().BeTrue();
        AcquisitionVocabulary.IsUtmValue(new string('a', 65)).Should().BeFalse();
    }

    [Theory]
    [InlineData("footage", "edit-my-video")]
    [InlineData("idea", "edit-my-video")]
    [InlineData("code", "quick-win-promo")]
    public void Each_material_has_its_starter_recipe(string material, string expected)
    {
        AcquisitionVocabulary.StarterTemplateKeyFor(material).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Footage")]
    [InlineData("other")]
    public void Anything_that_is_not_a_material_has_no_starter_recipe(string? material)
    {
        AcquisitionVocabulary.StarterTemplateKeyFor(material).Should().BeNull();
    }

    [Fact]
    public void Every_material_maps_to_a_listed_recipe_of_the_catalog()
    {
        // The starter is created for a new project, so it must be a recipe a person can actually get:
        // in the catalog, listed (not hidden) and not superseded by another.
        string[] listed = WorkflowTemplateCatalog.GetListed().Select(t => t.Key).ToArray();
        foreach (string material in AcquisitionVocabulary.Materials)
        {
            string? key = AcquisitionVocabulary.StarterTemplateKeyFor(material);
            key.Should().NotBeNull();
            listed.Should().Contain(key!, "the starter recipe of '{0}' must be a listed catalog recipe", material);
            WorkflowTemplateCatalog.GetByKey(key!)!.SupersededBy.Should().BeNull();
        }

        AcquisitionVocabulary.CodeStarterTemplateKey.Should().Be(WorkflowTemplateCatalog.StarterTemplateKey,
            "code starts from the same recipe a new project gets automatically");
    }

    [Fact]
    public void SqlInList_quotes_each_token()
    {
        AcquisitionVocabulary.SqlInList(AcquisitionVocabulary.Materials).Should().Be("'footage', 'code', 'idea'");
        AcquisitionVocabulary.SqlInList(Array.Empty<string>()).Should().BeEmpty();
    }
}
