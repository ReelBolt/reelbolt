using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

public class AssistantToolProgressTests
{
    private static AssistantToolProvider Provider(InferenceApiDbContext db, Guid userId) =>
        AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(userId));

    private static InferenceApiDbContext Db() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void Every_registered_tool_has_a_plain_language_caption()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        var missing = Provider(db, user).GetTools(new AssistantToolContext(user))
            .Select(t => t.Name)
            .Where(n => !AssistantToolProgress.CaptionedTools.Contains(n))
            .ToList();

        missing.Should().BeEmpty("a new tool needs a caption, or the chat panel falls back to a generic one");
    }

    [Fact]
    public async Task Wrapped_tools_report_their_caption_and_keep_their_declaration()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        AssistantToolProvider provider = Provider(db, user);
        var context = new AssistantToolContext(user);
        AIFunction plain = provider.GetTools(context).Single(t => t.Name == "ListProjects");

        var captions = new List<string>();
        provider.OnToolStarting = (caption, _) => { captions.Add(caption); return Task.CompletedTask; };
        AIFunction wrapped = provider.GetTools(context).Single(t => t.Name == "ListProjects");

        wrapped.Description.Should().Be(plain.Description);
        wrapped.JsonSchema.GetRawText().Should().Be(plain.JsonSchema.GetRawText());

        await wrapped.InvokeAsync(new AIFunctionArguments());
        captions.Should().Equal("Looking at your projects");
    }

    [Fact]
    public async Task A_failing_progress_callback_never_fails_the_tool()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        AssistantToolProvider provider = Provider(db, user);
        provider.OnToolStarting = (_, _) => throw new InvalidOperationException("stream closed");

        AIFunction tool = provider.GetTools(new AssistantToolContext(user)).Single(t => t.Name == "ListProjects");
        object? result = await tool.InvokeAsync(new AIFunctionArguments());

        result.Should().NotBeNull();
    }
}
