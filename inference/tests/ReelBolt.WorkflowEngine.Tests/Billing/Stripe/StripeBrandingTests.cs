using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripeKit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

public sealed class StripeBrandingTests
{
    private static Plan Creator => new() { Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}""" };

    private static StripeBrandingOptions Full() => new()
    {
        BackgroundColor = "#0B0B12",
        ButtonColor = "#7C3AED",
        BorderStyle = "rectangular",
        FontFamily = "chakra_petch",
        DisplayName = "ReelBolt",
        LogoUrl = "https://reelbolt.ai/brand/stripe-logo.png",
        IconUrl = "https://reelbolt.ai/brand/stripe-icon.png"
    };

    private static async Task<StripeCall> Checkout(StripeBrandingOptions branding, bool managed)
    {
        StripeFake fake = new StripeFake()
            .On("POST", "/v1/checkout/sessions", new { id = "cs_test_1", url = "https://checkout.stripe.com/c/pay/cs_test_1" });
        StripeOptions o = Options(managed);
        o.Branding = branding;
        await Provider(fake, o).CreateCheckoutAsync(new CheckoutRequest(StripePayloads.OrgId, System.Guid.NewGuid(), null, Creator, 1, null, "cus_1"), default);
        return fake.To("/v1/checkout/sessions").Single();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AllSet_SendsEveryBrandingField_InBothTaxModes(bool managed)
    {
        StripeCall c = await Checkout(Full(), managed);

        c.F("branding_settings[background_color]").Should().Be("#0B0B12");
        c.F("branding_settings[button_color]").Should().Be("#7C3AED");
        c.F("branding_settings[border_style]").Should().Be("rectangular");
        c.F("branding_settings[font_family]").Should().Be("chakra_petch");
        c.F("branding_settings[display_name]").Should().Be("ReelBolt");
        c.F("branding_settings[logo][type]").Should().Be("url");
        c.F("branding_settings[logo][url]").Should().Be("https://reelbolt.ai/brand/stripe-logo.png");
        c.F("branding_settings[icon][type]").Should().Be("url");
        c.F("branding_settings[icon][url]").Should().Be("https://reelbolt.ai/brand/stripe-icon.png");
        c.FKeys.Count(k => k.StartsWith("branding_settings", System.StringComparison.Ordinal)).Should().Be(9);
        c.HasF("managed_payments[enabled]").Should().Be(managed);
        c.HasF("automatic_tax[enabled]").Should().Be(!managed);
    }

    [Fact]
    public async Task OnlySomeSet_SendsOnlyThose()
    {
        StripeCall c = await Checkout(new StripeBrandingOptions { DisplayName = "ReelBolt", LogoUrl = "https://reelbolt.ai/brand/stripe-logo.png", ButtonColor = " " }, true);

        c.FKeys.Where(k => k.StartsWith("branding_settings", System.StringComparison.Ordinal)).Should().BeEquivalentTo(
            "branding_settings[display_name]", "branding_settings[logo][type]", "branding_settings[logo][url]");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoneSet_SendsNoBrandingKey(bool managed)
    {
        StripeCall c = await Checkout(new StripeBrandingOptions(), managed);

        c.FKeys.Should().NotContain(k => k.StartsWith("branding_settings", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BackgroundColor", "red")]
    [InlineData("BackgroundColor", "#FFF")]
    [InlineData("BackgroundColor", "#0B0B12\n")]
    [InlineData("ButtonColor", "7C3AED")]
    [InlineData("BorderStyle", "square")]
    [InlineData("FontFamily", "Chakra Petch")]
    [InlineData("DisplayName", "Bad\nName")]
    [InlineData("LogoUrl", "http://reelbolt.ai/a.png")]
    [InlineData("IconUrl", "https://user:pass@reelbolt.ai/a.png")]
    [InlineData("LogoUrl", "/relative.png")]
    public void InvalidValue_IsRefused_NamingTheSetting_NotTheValue(string setting, string value)
    {
        StripeBrandingOptions b = Full();
        typeof(StripeBrandingOptions).GetProperty(setting)!.SetValue(b, value);
        StripeOptions o = Options();
        o.Branding = b;

        ValidateOptionsResult r = new StripeOptionsValidator(Microsoft.Extensions.Options.Options.Create(new BillingProviderOptions { Provider = "Stripe" })).Validate(null, o);

        r.Failed.Should().BeTrue();
        r.FailureMessage.Should().Contain($"Billing:Stripe:Branding:{setting}");
        r.FailureMessage.Should().NotContain("pass");
    }

    [Fact]
    public void ValidBranding_Passes() => Options().Branding.DescribeProblem().Should().BeNull();

    [Fact]
    public void FullValidBranding_Passes() => Full().DescribeProblem().Should().BeNull();
}
