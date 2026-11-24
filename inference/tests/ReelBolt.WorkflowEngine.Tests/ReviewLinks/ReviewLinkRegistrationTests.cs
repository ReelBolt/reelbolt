#nullable enable
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ReviewLinks;

/// <summary>Both review-link controllers and their services build from the registrations Program.cs uses.</summary>
public class ReviewLinkRegistrationTests
{
    [Fact]
    public void The_owner_and_public_controllers_resolve_from_the_real_registrations()
    {
        using var root = CompositionRootHarness.Build(
            "inference-api",
            (services, configuration) => services.AddInferenceApiServices(configuration));

        (int activated, var failures) = root.ActivateAll([typeof(ReviewLinksController), typeof(ReviewLinksPublicController)]);
        failures.Should().BeEmpty();
        activated.Should().Be(2);

        root.Services.GetRequiredService<ReviewLinkWriteLimiter>().Should().BeSameAs(root.Services.GetRequiredService<ReviewLinkWriteLimiter>(), "one window per replica");
        using IServiceScope scope = root.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ReviewLinkResolver>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ReviewLinkActivityNotifier>().Should().NotBeNull();
        Microsoft.Extensions.Options.IOptions<ReviewLinkOptions> options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReviewLinkOptions>>();
        options.Value.DefaultExpiryDays.Should().Be(14);
        options.Value.MaxExpiryDays.Should().Be(90);
        options.Value.MaxLinksPerTimeline.Should().Be(20);
        options.Value.MaxGuestCommentsPerLink.Should().Be(200);
        options.Value.GuestCommentsPerMinute.Should().Be(10);
    }
}
