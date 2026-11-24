using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>
/// The base story editor and edit director system prompts, and the edit room charter, are byte-identical to master (dc66550b7): the dynamic edit's
/// wording lives in the style nudge, added only to a run that has dynamics, so a run without it prompts as before.
/// The hashes were computed from the master text of both copies (the agent's in-process fallback and the seeded row).
/// A change to a base prompt must be deliberate: update the hash with it.
/// </summary>
public class BasePromptsUnchangedTests
{
    private const string StoryEditorMasterSha256 = "c1bbdb79e8a1fec33cf52c77258287598f32ef38b3bc3a1748dd7666ab346beb";
    private const string EditDirectorMasterSha256 = "f989a408db770f33e50db6b617e8f42809a7da72a7025a7247f377ddde7bfcb9";

    private const string EditRoomCharterMasterSha256 = "4387290092425645dad41dca0cd12d5586ee240518d201c936fefa13563912cf";

    [Fact]
    public void The_edit_room_charter_is_master_s() =>
        Sha(ReelBolt.WorkflowEngine.Execution.StepExecutors.EditRoomStepExecutor.EditRoomCharterPrompt).Should().Be(EditRoomCharterMasterSha256);

    [Fact]
    public void The_story_editor_prompt_is_master_s_in_both_copies() =>
        Both(typeof(VideoStoryEditorAgent), AgentType.VideoStoryEditor).Should().AllBe(StoryEditorMasterSha256);

    [Fact]
    public void The_edit_director_prompt_is_master_s_in_both_copies() =>
        Both(typeof(VideoEditDirectorAgent), AgentType.VideoEditDirector).Should().AllBe(EditDirectorMasterSha256);

    private static string[] Both(System.Type agentType, AgentType builtIn)
    {
        string fallback = (string)agentType.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var seeded = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return [Sha(fallback), Sha(seeded[builtIn].SystemPrompt)];
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
