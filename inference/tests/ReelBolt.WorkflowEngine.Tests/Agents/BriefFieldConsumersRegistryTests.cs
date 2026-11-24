using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Services.Briefs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// CAT-12: <see cref="BriefFieldConsumers"/> (read by WL011, the step catalog's readsFromRun and the app manifest
/// deriver) must agree with the engine's as-built <see cref="RunBriefRegistry"/> on the four fields the registry covers
/// (language, source language, length, voice), for every agent and every room, so the two cannot drift.
/// </summary>
public class BriefFieldConsumersRegistryTests
{
    private static string Name(RunBriefField field) => field switch
    {
        RunBriefField.Language => BriefFieldConsumers.Language,
        RunBriefField.SourceLanguage => BriefFieldConsumers.SourceLanguage,
        RunBriefField.Length => BriefFieldConsumers.LengthSec,
        _ => BriefFieldConsumers.Voice
    };

    private static readonly string[] Covered =
        [BriefFieldConsumers.Language, BriefFieldConsumers.SourceLanguage, BriefFieldConsumers.LengthSec, BriefFieldConsumers.Voice];

    private static string[] Registry(AgentType agent) => [.. RunBriefRegistry.FieldsRead(agent).Select(Name).Order()];

    private static string[] Table(AppStepFacts step) => [.. BriefFieldConsumers.FieldsReadBy(step).Where(Covered.Contains).Order()];

    public static IEnumerable<object[]> Agents() => Enum.GetValues<AgentType>().Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(Agents))]
    public void An_agent_step_reads_exactly_the_registry_fields_of_its_agent(AgentType agent)
    {
        Table(new AppStepFacts(StepType.Agent, agent)).Should().Equal(Registry(agent), $"{agent}");
    }

    [Theory]
    [InlineData(StepType.EditRoom)]
    [InlineData(StepType.GraphicsRoom)]
    [InlineData(StepType.ColorGradeRoom)]
    [InlineData(StepType.DailiesRoom)]
    public void A_room_reads_the_registry_fields_of_its_agents(StepType room)
    {
        string[] expected = [.. RunBriefRegistry.RoomAgents(room).SelectMany(RunBriefRegistry.FieldsRead).Select(Name).Distinct().Order()];

        Table(new AppStepFacts(room)).Should().Equal(expected, $"{room}");
    }

    [Fact]
    public void The_colour_and_dailies_rooms_read_no_brief_field_and_music_and_sound_agents_are_not_editorial()
    {
        BriefFieldConsumers.FieldsReadBy(new AppStepFacts(StepType.ColorGradeRoom)).Should().BeEmpty();
        BriefFieldConsumers.FieldsReadBy(new AppStepFacts(StepType.DailiesRoom)).Should().BeEmpty();
        BriefFieldConsumers.FieldsReadBy(new AppStepFacts(StepType.Agent, AgentType.MusicSupervisor)).Should().BeEmpty();
        BriefFieldConsumers.FieldsReadBy(new AppStepFacts(StepType.Agent, AgentType.SoundDesigner)).Should().BeEmpty();
    }

    [Fact]
    public void A_review_loop_reads_what_the_reviewers_read()
    {
        Table(new AppStepFacts(StepType.ReviewLoop, AgentType.VideoReviewAgent)).Should().Equal(Registry(AgentType.VideoReviewAgent));
        Table(new AppStepFacts(StepType.ReviewLoop, AgentType.ReviewAgent)).Should().Equal(Registry(AgentType.ReviewAgent));
    }
}
