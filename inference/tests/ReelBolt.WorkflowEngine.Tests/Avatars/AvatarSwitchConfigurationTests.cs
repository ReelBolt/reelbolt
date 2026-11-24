#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Tests;
using ReelBolt.WorkflowEngine.Services.Avatars;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// A mistyped avatar switch is off, in the Inference API and in the engine alike, never an exception on a request:
/// <c>AVATARS_GENERATION=true</c> or <c>2</c> is <c>Off</c>, <c>AVATARS_ENABLED=yes</c> is false, and the problem is
/// logged once, at Error, naming the key and what it accepts.
/// </summary>
[Collection(AvatarSwitchCollection.Name)]
public class AvatarSwitchConfigurationTests
{
    /// <summary>The installer sets process-wide switches; put them back to what the other tests expect.</summary>
    private static async Task StartInstallerAsync(IConfiguration configuration, ILogger<AvatarSwitchInstaller> log)
    {
        try
        {
            await new AvatarSwitchInstaller(configuration, log).StartAsync(CancellationToken.None);
        }
        finally
        {
            AppStepFacts.UseBriefAvatarSwitch(() => true);
            AppStepFacts.UsePersonAvatarSwitch(null);
            AppStepFacts.UseObjectAvatarSwitch(null);
        }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private sealed class Recorder<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("1", false)]
    [InlineData("on", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_bool_that_cannot_be_read_is_false(string? raw, bool expected) =>
        AvatarSwitchConfiguration.ReadBool(Config((AvatarSwitchConfiguration.EnabledKey, raw)), AvatarSwitchConfiguration.EnabledKey).Should().Be(expected);

    [Theory]
    [InlineData("Off", "Off")]
    [InlineData("On", "On")]
    [InlineData("on", "On")]
    [InlineData("PlatformAdminOnly", "PlatformAdminOnly")]
    [InlineData("platformadminonly", "PlatformAdminOnly")]
    [InlineData("true", "Off")]
    [InlineData("yes", "Off")]
    [InlineData("2", "Off")]
    [InlineData("1", "Off")]
    [InlineData("0", "Off")]
    [InlineData("99", "Off")]
    [InlineData("On,Off", "Off")]
    [InlineData("", "Off")]
    [InlineData(null, "Off")]
    public void A_generation_mode_that_cannot_be_read_is_off_and_a_number_is_not_a_member(string? raw, string expected) =>
        AvatarSwitchConfiguration.ReadGeneration(Config((AvatarSwitchConfiguration.GenerationKey, raw))).Should().Be(expected);

    [Theory]
    [InlineData("true", "yes", "true")]
    [InlineData("yes", "1", "2")]
    [InlineData("false", "true", "On")]
    [InlineData("true", "true", "PlatformAdminOnly")]
    [InlineData(null, null, null)]
    public void The_api_and_the_engine_read_every_value_the_same_way(string? people, string? objects, string? generation)
    {
        IConfiguration configuration = Config(
            (AvatarSwitchConfiguration.EnabledKey, people), (AvatarSwitchConfiguration.ObjectsEnabledKey, objects), (AvatarSwitchConfiguration.GenerationKey, generation));
        var api = new AvatarOptions();
        AvatarOptions.Apply(api, configuration);
        var engine = new AvatarGenerationOptions();
        AvatarGenerationOptions.Apply(engine, configuration);

        (engine.Enabled, engine.ObjectsEnabled, engine.Generation.ToString()).Should().Be((api.Enabled, api.Objects.Enabled, api.Generation.ToString()));
    }

    [Fact]
    public void No_options_read_throws_on_a_mistyped_value_in_the_api_or_the_engine()
    {
        IConfiguration configuration = Config(
            (AvatarSwitchConfiguration.EnabledKey, "yes"), (AvatarSwitchConfiguration.ObjectsEnabledKey, "enabled"), (AvatarSwitchConfiguration.GenerationKey, "true"));
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddOptions<AvatarOptions>().Configure<IConfiguration>(AvatarOptions.Apply);
        services.AddOptions<AvatarGenerationOptions>().Configure(o => AvatarGenerationOptions.Apply(o, configuration));
        using ServiceProvider provider = services.BuildServiceProvider();

        AvatarOptions api = provider.GetRequiredService<IOptions<AvatarOptions>>().Value;
        AvatarGenerationOptions engine = provider.GetRequiredService<IOptions<AvatarGenerationOptions>>().Value;

        (api.Enabled, api.Objects.Enabled, api.Generation).Should().Be((false, false, AvatarPersonGeneration.Off));
        (engine.Enabled, engine.ObjectsEnabled, engine.Generation).Should().Be((false, false, AvatarGenerationMode.Off));
        engine.AllowsGeneration(ReelBolt.Shared.Data.Models.AvatarSubjectKind.Person).Should().BeFalse();
        AvatarOptions.AnyEnabledIn(configuration).Should().BeFalse();
    }

    [Fact]
    public void A_numeric_generation_value_is_not_on()
    {
        var api = new AvatarOptions();
        AvatarOptions.Apply(api, Config((AvatarSwitchConfiguration.GenerationKey, "2"), (AvatarSwitchConfiguration.EnabledKey, "true")));
        api.Generation.Should().Be(AvatarPersonGeneration.Off);
        var engine = new AvatarGenerationOptions();
        AvatarGenerationOptions.Apply(engine, Config((AvatarSwitchConfiguration.GenerationKey, "2"), (AvatarSwitchConfiguration.EnabledKey, "true")));
        engine.AllowsGeneration(ReelBolt.Shared.Data.Models.AvatarSubjectKind.Person).Should().BeFalse("the number 2 is not the mode On");
    }

    [Fact]
    public async Task Each_host_logs_the_problem_once_at_error_naming_the_key_and_the_accepted_values()
    {
        IConfiguration configuration = Config(
            (AvatarSwitchConfiguration.EnabledKey, "yes"), (AvatarSwitchConfiguration.GenerationKey, "true"), (AvatarSwitchConfiguration.ObjectsEnabledKey, "true"));

        var api = new Recorder<AvatarSwitchInstaller>();
        await StartInstallerAsync(configuration, api);
        var engine = new Recorder<AvatarSwitchStartupCheck>();
        await new AvatarSwitchStartupCheck(configuration, engine).StartAsync(CancellationToken.None);

        foreach (List<(LogLevel Level, string Message)> entries in new[] { api.Entries, engine.Entries })
        {
            entries.Should().HaveCount(2, "one line per key that cannot be read, none for the one that can");
            entries.Should().OnlyContain(e => e.Level == LogLevel.Error);
            entries.Should().Contain(e => e.Message.Contains("Avatars:Enabled") && e.Message.Contains("true, false"));
            entries.Should().Contain(e => e.Message.Contains("Avatars:Generation") && e.Message.Contains("Off, PlatformAdminOnly, On"));
        }
    }

    [Fact]
    public async Task A_configuration_that_reads_fine_logs_nothing()
    {
        IConfiguration configuration = Config(
            (AvatarSwitchConfiguration.EnabledKey, "true"), (AvatarSwitchConfiguration.GenerationKey, "On"), (AvatarSwitchConfiguration.ObjectsEnabledKey, "false"));
        var log = new Recorder<AvatarSwitchInstaller>();
        await StartInstallerAsync(configuration, log);
        log.Entries.Should().BeEmpty();
    }
}
