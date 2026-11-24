using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression test: found live producing the alpha launch film. The AuthorAgent probed for
/// Remotion's conventional <c>public/</c> asset directory with <c>list_sandbox_files</c> before
/// deciding whether to create it. The directory did not exist, the sandbox correctly answered
/// 404, and <c>ReadJsonAsync</c> turned that into an <see cref="InvalidOperationException"/> that
/// propagated out of the whole agent run — failing step 11 and terminating a 19-step execution
/// that had already spent an hour on analysis and rendering.
///
/// Same shape as the binary-file guardrail in <see cref="ProjectFileAgentToolsTests"/>: an
/// ordinary, recoverable answer must come back as a tool RESULT, never as an exception.
/// </summary>
public class SandboxListMissingDirectoryTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body)
            });
    }

    private static ReactRemotionSandboxTools BuildTools(HttpStatusCode status, string body)
    {
        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHandler(status, body)));

        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.SetupGet(a => a.Current).Returns(new WorkflowExecutionContext
        {
            ExecutionId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            CorrelationId = "test"
        });

        return new ReactRemotionSandboxTools(
            factory.Object,
            accessor.Object,
            Mock.Of<IProjectFileWorkspace>(),
            Mock.Of<IAmazonS3>(),
            new ConfigurationBuilder().Build(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            Mock.Of<IServiceScopeFactory>());
    }

    [Fact]
    public async Task Listing_a_missing_directory_returns_a_result_instead_of_throwing()
    {
        ReactRemotionSandboxTools tools = BuildTools(
            HttpStatusCode.NotFound,
            """{"error":"openat public: no such file or directory"}""");

        string json = await tools.ListSandboxFiles("public");

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("path").GetString().Should().Be("public");
        doc.RootElement.GetProperty("entries").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("public");
    }

    [Fact]
    public async Task A_successful_listing_is_still_returned_verbatim_as_an_array()
    {
        // The 404 branch must not disturb the normal contract: callers/models parse a plain
        // JSON ARRAY of entries on success, not an envelope.
        ReactRemotionSandboxTools tools = BuildTools(
            HttpStatusCode.OK,
            """[{"name":"index.ts","isDir":false,"size":12}]""");

        string json = await tools.ListSandboxFiles(".");

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Other_failures_still_throw_so_real_breakage_is_not_hidden(HttpStatusCode status)
    {
        // Only "not found" is an answer. A broken or unauthorized sandbox is a genuine fault and
        // must not be laundered into an empty directory listing.
        ReactRemotionSandboxTools tools = BuildTools(status, """{"error":"boom"}""");

        Func<Task> act = () => tools.ListSandboxFiles("public");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
