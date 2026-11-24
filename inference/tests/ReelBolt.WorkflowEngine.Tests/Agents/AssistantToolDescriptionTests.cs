using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Anthropic's connector review refuses tool descriptions that give the model behavioural
/// direction or promote the product. Every description the chat assistant registers is scanned for
/// the banned phrasings; WatchExecution is included because it is chat-only, so the scan opens a
/// thread context and covers the full 139-tool chat surface.
/// </summary>
public class AssistantToolDescriptionTests
{
    /// <summary>
    /// The banned patterns, kept here so the check is auditable. They are the phrasings the review
    /// rejects: instructions to the model (what to ask, tell, confirm, remember or make sure of),
    /// mandated call sequences, polling direction, and "use it instead" promotion.
    /// </summary>
    private static readonly (string Pattern, string Reason)[] BannedPatterns =
    [
        (@"\byou (must|should|need to)\b", "instructs the model"),
        (@"\bask the user\b", "instructs the model to converse"),
        (@"\btell the user\b", "instructs the model to converse"),
        (@"\bconfirm (it )?with the user\b", "instructs the model to converse"),
        (@"\buse it instead\b", "promotes one tool over another"),
        (@"\bdo not poll\b", "directs the model's call pattern"),
        (@"\bfirst call\b", "mandates a call sequence"),
        (@"\balways call\b", "mandates a call"),
        (@"\bnever call\b", "mandates a call"),
        (@"\bremember to\b", "instructs the model"),
        (@"\bmake sure to\b", "instructs the model"),
    ];

    private static InferenceApiDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    [Fact]
    public void Every_chat_tool_description_is_free_of_instructions_and_promotion()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(userId));

        // A thread id registers the chat-only WatchExecution tool, so this is the 153-tool surface.
        IReadOnlyList<AIFunction> tools = provider.GetTools(
            new AssistantToolContext(userId, ProjectId: Guid.NewGuid(), ThreadId: Guid.NewGuid()));

        tools.Should().HaveCount(153, "the chat surface includes the chat-only WatchExecution tool");

        var violations = new List<string>();
        foreach (AIFunction tool in tools)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace($"'{tool.Name}' needs a description");

            foreach ((string pattern, string reason) in BannedPatterns)
            {
                Match match = Regex.Match(tool.Description!, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                    violations.Add($"{tool.Name}: '{match.Value}' ({reason})");
            }
        }

        violations.Should().BeEmpty();
    }
}
