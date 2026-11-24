using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class ScriptwriterOutputShapeTests
{
    private static List<string> Read(string onScreenTextJson) =>
        JsonSerializer.Deserialize<ScriptwriterOutput>(
            $$"""{"scenes":[{"sceneId":"s1","voiceover":"v","onScreenText":{{onScreenTextJson}}}]}""",
            ScriptwriterOutputJson.Options)!.Scenes[0].OnScreenText;

    [Fact]
    public void String_with_newline_splits_into_lines() =>
        Read("\"Northwind Staffing\\nExpense portal for HR teams\"").Should().Equal("Northwind Staffing", "Expense portal for HR teams");

    [Fact]
    public void String_with_crlf_blank_lines_and_padding_is_trimmed() =>
        Read("\"  a \\r\\n\\r\\n b\\r c \\n\"").Should().Equal("a", "b", "c");

    [Fact]
    public void Array_passes_through() => Read("[\"x\",\"y\"]").Should().Equal("x", "y");

    [Fact]
    public void Null_is_empty() => Read("null").Should().BeEmpty();

    [Theory]
    [InlineData("[\"x\", 3]", new[] { "x", "3" })]
    [InlineData("42", new[] { "42" })]
    [InlineData("{\"text\":\"Prova gratis\"}", new[] { "Prova gratis" })]
    [InlineData("[{\"text\":\"Riga uno\"},{\"line\":\"Riga due\"}]", new[] { "Riga uno", "Riga due" })]
    [InlineData("[\"a\", null, [\"b\"]]", new[] { "a", "b" })]
    [InlineData("{\"it\":\"Ciao a tutti\",\"n\":1}", new[] { "Ciao a tutti" })]
    public void Drifted_shapes_keep_their_text_instead_of_failing(string json, string[] expected) =>
        Read(json).Should().Equal(expected);

    [Fact]
    public void Generated_schema_still_types_OnScreenText_as_array_of_strings()
    {
        ChatResponseFormat format = ChatResponseFormat.ForJsonSchema<ScriptwriterOutput>();
        string schema = ((ChatResponseFormatJson)format).Schema!.Value.GetRawText();
        using JsonDocument doc = JsonDocument.Parse(schema);
        JsonElement scene = doc.RootElement.GetProperty("properties").GetProperty("scenes").GetProperty("items");
        JsonElement ost = scene.GetProperty("properties").GetProperty("onScreenText");
        ost.GetProperty("type").GetString().Should().Be("array");
        ost.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
    }

    [Fact]
    public void Scriptwriter_prompt_is_byte_identical_and_states_the_array_rule()
    {
        string fallback = (string)typeof(ScriptwriterAgentImpl)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var builtIns = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string seeded = builtIns[AgentType.ScriptwriterAgent].SystemPrompt;

        seeded.Should().Be(fallback);
        fallback.Should().Contain("OnScreenText: a list of 1-2 short lines (a JSON array of strings, one string per line; never one string containing line breaks)");
        fallback.Should().Contain("at most two lines per scene");
    }

    private sealed class ScriptedExecutor(bool retryable) : IStepExecutor
    {
        public int CallCount { get; private set; }
        public StepType StepType => StepType.Agent;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            CallCount++;
            return Task.FromResult(new StepExecutionResult
            {
                Output = "{}",
                NextStepIndex = 1,
                Status = StepStatus.Failed,
                ErrorDetails = "SCRIPTWRITER_OUTPUT_INVALID: bad",
                Retryable = retryable
            });
        }
    }

    private static async Task<(int Calls, InvalidOperationException Ex)> RunAsync(bool retryable)
    {
        var executor = new ScriptedExecutor(retryable);
        var service = new WorkflowExecutorService(
            scopeFactory: null!,
            eventPublisher: null!,
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: new[] { executor },
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions { MaxStepRetries = 3, RetryBaseDelaySeconds = 0 }),
            cancellationRegistry: new ExecutionCancellationRegistry());
        var step = new WorkflowStep { StepOrder = 1, StepType = StepType.Agent };
        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution(),
            Step = step,
            AllSteps = new List<WorkflowStep> { step },
            AccumulatedOutput = string.Empty,
            StepOutputHistory = new List<StepOutputHistoryEntry>(),
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "",
            CancellationToken = CancellationToken.None
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteStepWithRetryAsync(executor, context, step, CancellationToken.None));
        return (executor.CallCount, ex);
    }

    [Fact]
    public async Task Non_retryable_failure_runs_the_executor_once()
    {
        var (calls, ex) = await RunAsync(retryable: false);
        calls.Should().Be(1);
        ex.Message.Should().Contain("not retried");
    }

    [Fact]
    public async Task Retryable_failure_still_retries()
    {
        var (calls, _) = await RunAsync(retryable: true);
        calls.Should().Be(3);
    }
}
