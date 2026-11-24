using System;
using System.Collections.Generic;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-110: both copies of the Author prompt tell it about the event manifest, and both copies of the
/// SoundDesigner prompt tell it about <c>e{n}</c> anchors (byte-identity itself is pinned by the
/// existing consistency tests).
/// </summary>
public class RenderEventPromptConsistencyTests
{
    private static string Fallback(Type agent)
    {
        FieldInfo? field = agent.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull();
        return (string)field!.GetValue(null)!;
    }

    private static string Seeded(AgentType type)
    {
        FieldInfo? field = typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        var agents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)field!.GetValue(null)!;
        return agents[type].SystemPrompt;
    }

    public const string AuthorSentence =
        "If your film has moments a sound effect should hit (a stamp landing, a row appearing, a door slamming), " +
        "call `DeclareRenderEvents` before rendering with each moment's name, frame and a few words saying what happens.";

    [Fact]
    public void Both_Author_copies_carry_the_event_manifest_sentence()
    {
        Fallback(typeof(AuthorAgentImpl)).Should().Contain(AuthorSentence);
        Seeded(AgentType.AuthorAgent).Should().Contain(AuthorSentence);
    }

    [Fact]
    public void Both_SoundDesigner_copies_offer_event_anchors()
    {
        foreach (string prompt in new[] { Fallback(typeof(SoundDesignerAgent)), Seeded(AgentType.SoundDesigner) })
        {
            prompt.Should().Contain("\"renderEvents\"");
            prompt.Should().Contain("\"e{n}\"");
            prompt.Should().Contain("never a time");
        }
    }
}
