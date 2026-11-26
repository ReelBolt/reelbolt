#nullable enable
using System.Collections.Generic;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>GET /api/v1/billing/promotions/first-video: the anonymous "is the grant on" read for the sign-up page.</summary>
public sealed class PromotionFirstVideoReadTests : PromotionTestBase
{
    private static IConfiguration Config(string? enabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { ["Billing:FirstVideoGrant:Enabled"] = enabled })
        .Build();

    [Fact]
    public void Billing_off_is_404()
    {
        Anonymous(enabled: false).FirstVideo(Config(null)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Default_is_enabled_and_cached_30s()
    {
        PromotionsController controller = Anonymous();

        IActionResult result = controller.FirstVideo(Config(null));

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(new { enabled = true });
        controller.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=30");
    }

    [Fact]
    public void Kill_switch_off_is_disabled()
    {
        Anonymous().FirstVideo(Config("false")).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new { enabled = false });
    }
}
