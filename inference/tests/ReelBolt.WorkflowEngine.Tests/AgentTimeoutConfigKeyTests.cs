using System;
using System.IO;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression guard for a real bug found producing the alpha launch film: <see
/// cref="ReelBolt.WorkflowEngine.Agents.ReelBoltAgentBase"/> resolves a per-agent timeout override
/// via <c>Agents:{name}:RunTimeoutSeconds</c>, where <c>name</c> is the literal string each concrete
/// agent class passes to the base constructor (e.g. <c>AuthorAgent</c> passes <c>"Author"</c>, not
/// its own class/AgentType name). appsettings.json's <c>Agents</c> section had a key of
/// <c>"AuthorAgent"</c> instead of <c>"Author"</c>, so the override silently never matched and every
/// Author run fell back to the 3000s global default — exactly long enough to time out a real
/// multi-render "hero block" step that needed the intended 10800s. This loads the actual shipped
/// appsettings.json (not a hand-built config) and pins the keys that must match each agent's real
/// constructor-literal name.
/// </summary>
public class AgentTimeoutConfigKeyTests
{
    private static IConfigurationRoot LoadRealAppSettings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "WorkflowEngine.appsettings.json");
        return new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
    }

    [Theory]
    [InlineData("RemotionComponentTranslator", 10800)]
    [InlineData("Author", 10800)]
    [InlineData("MotionGraphicsPlanner", 7200)]
    [InlineData("MotionGraphicsDirector", 7200)]
    public void Agents_section_key_matches_the_agent_s_real_constructor_name(string agentName, int expectedSeconds)
    {
        IConfigurationRoot config = LoadRealAppSettings();

        config.GetValue<int?>($"Agents:{agentName}:RunTimeoutSeconds").Should().Be(expectedSeconds,
            $"ReelBoltAgentBase looks up this key using the agent's constructor-literal name (\"{agentName}\"), " +
            "not its class or AgentType name — a mismatched section header silently falls back to the global default.");
    }

    [Fact]
    public void Agents_section_does_not_key_Author_s_override_by_its_class_name()
    {
        // The exact wrong key this bug shipped with. If this ever comes back, the override above
        // is dead again and Author silently reverts to the 3000s global timeout.
        LoadRealAppSettings().GetValue<int?>("Agents:AuthorAgent:RunTimeoutSeconds").Should().BeNull();
    }
}
