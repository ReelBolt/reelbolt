#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Services.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Startup validation of the two lock-wait keys: <c>Billing:Promotions:CheckoutLockWaitMilliseconds</c>
/// (1..60000) and <c>Assistant:ExecutionGuardLockWaitMilliseconds</c> (0..60000). A bad value fails the boot,
/// and the message names the key and the range but never the value.
/// </summary>
public sealed class LockWaitStartupValidationTests
{
    private const string PromotionKey = "Billing:Promotions:CheckoutLockWaitMilliseconds";
    private const string AssistantKey = "Assistant:ExecutionGuardLockWaitMilliseconds";

    [Theory]
    [InlineData("0")]
    [InlineData("60001")]
    public void A_promotion_checkout_lock_wait_outside_1_to_60000_fails_startup_naming_only_the_key(string value)
    {
        using var root = CompositionRootHarness.Build(
            "lock-wait-promotion-" + value,
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?> { [PromotionKey] = value });

        Action act = () => root.Services.GetRequiredService<IStartupValidator>().Validate();

        OptionsValidationException failure = act.Should().Throw<OptionsValidationException>().Which;
        failure.Message.Should().Contain(PromotionKey);
        AssertValueNotLeaked(failure.Message, value);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5000")]
    [InlineData("60000")]
    public void A_promotion_checkout_lock_wait_inside_1_to_60000_passes_startup(string value)
    {
        using var root = CompositionRootHarness.Build(
            "lock-wait-promotion-ok-" + value,
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?> { [PromotionKey] = value });

        Action act = () => _ = root.Services.GetRequiredService<IOptions<ReelBolt.Inference.Api.Services.Billing.Promotions.PromotionOptions>>().Value;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("60001")]
    public void An_assistant_guard_lock_wait_outside_0_to_60000_fails_startup_naming_only_the_key(string value)
    {
        using var root = CompositionRootHarness.Build(
            "lock-wait-assistant-" + value.Replace("-", "neg"),
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?> { [AssistantKey] = value });

        Action act = () => root.Services.GetRequiredService<IStartupValidator>().Validate();

        OptionsValidationException failure = act.Should().Throw<OptionsValidationException>().Which;
        failure.Message.Should().Contain(AssistantKey);
        AssertValueNotLeaked(failure.Message, value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5000")]
    [InlineData("60000")]
    public void An_assistant_guard_lock_wait_inside_0_to_60000_passes_startup(string value)
    {
        using var root = CompositionRootHarness.Build(
            "lock-wait-assistant-ok-" + value,
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?> { [AssistantKey] = value });

        Action act = () => _ = root.Services.GetRequiredService<IOptions<AssistantExecutionGuardOptions>>().Value;

        act.Should().NotThrow();
    }

    /// <summary>The bad value must not appear as a standalone number (the range text itself contains digits like 0).</summary>
    private static void AssertValueNotLeaked(string message, string value)
    {
        Regex.IsMatch(message, $@"(?<![\d-]){Regex.Escape(value)}(?!\d)").Should().BeFalse(
            "the startup message must not echo the configured value; message was: {0}", message);
    }
}
