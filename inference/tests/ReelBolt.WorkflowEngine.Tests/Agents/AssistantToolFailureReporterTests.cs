using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

public class AssistantToolFailureReporterTests
{
    private sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger
    {
        public List<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }

    private static string Boom(Exception ex) => throw ex;

    private static readonly Guid Thread = Guid.NewGuid();

    private static (AIFunction tool, CapturingLogger log) Build(Delegate body, bool chat, string name = "SomeTool")
    {
        var log = new CapturingLogger();
        AIFunction inner = AIFunctionFactory.Create(body, name: name);
        return (new AssistantToolFailureReporter(inner, new AssistantToolFailureReporting(log, chat ? "chat" : "mcp", chat), Thread), log);
    }

    private static JsonElement Json(object? raw) => raw switch
    {
        JsonElement e => e,
        _ => JsonSerializer.SerializeToElement(raw)
    };

    [Fact]
    public async Task A_throwing_tool_is_logged_at_warning_with_name_thread_and_text_but_not_its_arguments()
    {
        (AIFunction tool, CapturingLogger log) = Build((string secretNote) => Boom(new InvalidOperationException("disk exploded\nsecond line")), chat: true);

        await tool.InvokeAsync(AssistantWorkflowOmittedIdTests.FromJson("""{"secretNote":"ARG-VALUE-123"}"""));

        Entry entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull("the raw exception (its unredacted message) is never handed to the logger");
        entry.Message.Should().Contain("InvalidOperationException");
        entry.Message.Should().Contain("SomeTool").And.Contain(Thread.ToString()).And.Contain("chat").And.Contain("disk exploded second line");
        entry.Message.Should().NotContain("ARG-VALUE-123");
        entry.Message.Should().NotContain("\n");
    }

    [Fact]
    public async Task An_error_result_is_logged_at_warning_in_every_result_shape()
    {
        foreach (Func<object> make in new Func<object>[]
        {
            () => JsonDocument.Parse("""{"error":"no such workflow","code":"NOT_FOUND"}""").RootElement.Clone(),
            () => """{"error":"no such workflow","code":"NOT_FOUND"}""",
            () => new JsonObject { ["error"] = "no such workflow", ["code"] = "NOT_FOUND" },
            () => new { error = "no such workflow", code = "NOT_FOUND" }
        })
        {
            (AIFunction tool, CapturingLogger log) = Build(() => make(), chat: false);
            await tool.InvokeAsync(new AIFunctionArguments());

            Entry entry = log.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Warning);
            entry.Message.Should().Contain("no such workflow").And.Contain("NOT_FOUND").And.Contain("mcp").And.Contain("SomeTool");
        }
    }

    [Fact]
    public async Task A_success_logs_nothing_and_a_secret_in_an_error_is_redacted()
    {
        (AIFunction ok, CapturingLogger okLog) = Build(() => new { stepCount = 2 }, chat: true);
        await ok.InvokeAsync(new AIFunctionArguments());
        okLog.Entries.Should().BeEmpty();

        (AIFunction bad, CapturingLogger badLog) = Build(() => new { error = "bad key sk-" + "ant-api03-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" }, chat: true);
        await bad.InvokeAsync(new AIFunctionArguments());
        badLog.Entries.Single().Message.Should().NotContain("AAAAAAAAAAAAAAAA");
    }

    [Fact]
    public async Task A_long_error_is_capped_and_a_key_across_the_cap_is_still_redacted()
    {
        string key = "sk-ant-api03-" + new string('B', 60);
        string error = new string('x', 480) + " " + key + " " + new string('y', 600);
        (AIFunction bad, CapturingLogger log) = Build(() => new { error }, chat: true);
        await bad.InvokeAsync(new AIFunctionArguments());

        string message = log.Entries.Single().Message;
        message.Should().NotContain("BBBBBBBBBBBB", "redaction runs before the cap, so a cut never exposes part of a key");
        message.Should().NotContain(new string('y', 100), "the error text is capped");
    }

    [Fact]
    public async Task A_turn_answers_a_bounded_number_of_throws_then_rethrows_so_the_framework_stop_still_applies()
    {
        (AIFunction tool, CapturingLogger log) = Build(() => Boom(new InvalidOperationException("boom")), chat: true);

        for (int i = 0; i < AssistantToolFailureReporting.MaxAnsweredExceptions; i++)
            Json(await tool.InvokeAsync(new AIFunctionArguments())).GetProperty("code").GetString().Should().Be("TOOL_FAILED");

        Func<Task> past = async () => await tool.InvokeAsync(new AIFunctionArguments());
        await past.Should().ThrowAsync<InvalidOperationException>("past the budget the throw reaches the framework again");
        log.Entries.Should().HaveCount(AssistantToolFailureReporting.MaxAnsweredExceptions + 1, "every throw is still logged");
    }

    [Fact]
    public async Task Chat_answers_a_missing_required_parameter_naming_it_and_the_workflowId_hint()
    {
        (AIFunction tool, CapturingLogger log) = Build((Guid workflowId, int count) => 1, chat: true);

        JsonElement answer = Json(await tool.InvokeAsync(AssistantWorkflowOmittedIdTests.FromJson("""{"count":2}""")));

        answer.GetProperty("code").GetString().Should().Be("TOOL_ARGUMENTS_INVALID");
        answer.GetProperty("error").GetString().Should().Contain("workflowId").And.Contain("GUID").And.Contain("omit workflowId");
        log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task Chat_answers_an_unreadable_parameter_naming_it_and_its_type()
    {
        (AIFunction tool, _) = Build((Guid? workflowId, int count) => 1, chat: true);

        JsonElement answer = Json(await tool.InvokeAsync(AssistantWorkflowOmittedIdTests.FromJson("""{"workflowId":"","count":2}""")));

        answer.GetProperty("code").GetString().Should().Be("TOOL_ARGUMENTS_INVALID");
        answer.GetProperty("error").GetString().Should().Contain("'workflowId'").And.Contain("GUID or null");
    }

    [Fact]
    public async Task Chat_answers_an_internal_exception_with_a_fixed_sentence_and_the_reference_the_log_carries()
    {
        (AIFunction tool, CapturingLogger log) = Build(() => Boom(new InvalidOperationException("host db-internal-7 refused s3://bucket/key")), chat: true);

        JsonElement answer = Json(await tool.InvokeAsync(new AIFunctionArguments()));

        answer.GetProperty("code").GetString().Should().Be("TOOL_FAILED");
        string text = answer.GetProperty("error").GetString()!;
        text.Should().NotContain("db-internal-7").And.NotContain("InvalidOperation").And.NotContain("s3://");
        text.Should().Contain("not the problem").And.Contain("more than once");
        string reference = text[(text.LastIndexOf(' ') + 1)..].TrimEnd('.');
        log.Entries.Single().Message.Should().Contain(reference);
    }

    [Fact]
    public async Task Mcp_rethrows_the_exception_unchanged()
    {
        (AIFunction tool, CapturingLogger log) = Build(() => Boom(new InvalidOperationException("boom")), chat: false);

        Func<Task> act = async () => await tool.InvokeAsync(new AIFunctionArguments());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_always_propagates_untouched_and_is_not_logged(bool chat)
    {
        (AIFunction tool, CapturingLogger log) = Build(() => Boom(new OperationCanceledException()), chat);

        Func<Task> act = async () => await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_provider_reports_a_ProposeWorkflow_call_with_a_bad_workflowId_instead_of_Function_failed()
    {
        var (connection, db) = ReelBolt.WorkflowEngine.Tests.Controllers.WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid owner = Guid.NewGuid();
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var log = new CapturingLogger();
            provider.FailureReporting = new AssistantToolFailureReporting(log, "chat", true);

            AIFunction propose = provider.GetTools(new AssistantToolContext(owner)).Single(t => t.Name == "ProposeWorkflow");
            JsonElement answer = Json(await propose.InvokeAsync(AssistantWorkflowOmittedIdTests.FromJson("""{"workflowId":"","name":"x","steps":[]}""")));

            answer.GetProperty("code").GetString().Should().Be("TOOL_ARGUMENTS_INVALID");
            answer.GetProperty("error").GetString().Should().Contain("workflowId").And.Contain("ListWorkflows");
            log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        }
    }
}
