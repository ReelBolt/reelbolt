#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Guardrails;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// Coverage for the tool-call screening wrapper in <see cref="AgentToolProvider"/>: the five
/// sandbox-write/render tools are screened through <see cref="IGuardrailScreener"/>, and everything
/// else about the tool surface is untouched.
///
/// Two properties carry this ticket and are asserted directly rather than inferred. First, with
/// screening inert — the shipped default — the wrapped call is <b>byte-identical</b> to the
/// unwrapped one, compared on the serialized tool result specifically so "equivalent" cannot pass
/// for "the same". Second, a rejection <b>does not run the tool</b>: the handler that would have
/// received the sandbox request is asserted to have seen zero requests, which is a stronger
/// statement than checking the return value alone.
/// </summary>
public sealed class AgentToolProviderScreeningTests
{
    /// <summary>
    /// The exact JSON <c>WriteSandboxFile</c> returns on success. Written out rather than derived so
    /// the byte-identity assertions below compare against the real shipped shape — note the
    /// capitalized <c>Ok</c>, which is what <c>JsonSerializer.Serialize(new { result.Ok, path })</c>
    /// has always produced (it is the sandbox result's own property name).
    /// </summary>
    private const string WriteSuccessJson = """{"Ok":true,"path":"src/App.tsx"}""";

    private const string SamplePath = "src/App.tsx";
    private const string SampleBody = "export const App = () => null;";

    private static readonly string[] ScreenedNames =
    [
        "WriteSandboxFile",
        "EditSandboxFile",
        "ApplySandboxFileEdits",
        "InsertSandboxFileLines",
        "RunSandboxRemotionCommand",
        "RenderVideoAndUploadToStorage",
    ];

    private static readonly string[] UnscreenedToolNames =
    [
        "EnsureSandbox",
        "StageProjectFile",
        "StageFont",
        "DeleteSandboxPath",
        "InstallNpmPackages",
        "RunSandboxNpmScript",
        "GetSandboxStatus",
        "ListSandboxFiles",
        "ReadSandboxFile",
        "ReadSandboxFileLines",
        "GetSandboxFileOutline",
        "CheckLintAndTypeErrors",
        "GetSandbox",
        "WriteProjectFile",
        "EditProjectFile",
        "ReadProjectFileLines",
        "GetProjectFileOutline",
        "GetCompositionInfo",
        "RenderPreviewStills",
        "CheckLayout",
        "DeclareRenderEvents",
        "FailWorkflow",
    ];

    // --- 1. Byte-identical when inert (the shipped default) --------------------------------------

    [Fact]
    public async Task With_no_screener_at_all_a_wrapped_write_returns_the_exact_unwrapped_result()
    {
        RecordingSandboxHandler handler = new();
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(handler);

        // The drift-guard construction: three positional arguments, no screener — which is also what
        // a host that never registered one would get.
        AgentToolProvider provider = CreateProvider(sandboxTools, screener: null);

        object? wrapped = await Invoke(ToolNamed(provider, "WriteSandboxFile"), WriteArguments());
        object? unwrapped = await Invoke(
            AIFunctionFactory.Create(sandboxTools.WriteSandboxFile), WriteArguments());

        AssertSameToolResult(wrapped, unwrapped);
        ResultJson(wrapped).Should().Be(WriteSuccessJson);
        handler.RequestCount.Should().Be(2, "both calls must actually have run the real tool");
    }

    [Fact]
    public async Task With_a_screener_that_allows_everything_a_wrapped_write_is_still_byte_identical()
    {
        RecordingSandboxHandler handler = new();
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(handler);
        StubScreener screener = new();
        AgentToolProvider provider = CreateProvider(sandboxTools, screener);

        object? wrapped = await Invoke(ToolNamed(provider, "WriteSandboxFile"), WriteArguments());
        object? unwrapped = await Invoke(
            AIFunctionFactory.Create(sandboxTools.WriteSandboxFile), WriteArguments());

        AssertSameToolResult(wrapped, unwrapped);
        ResultJson(wrapped).Should().Be(WriteSuccessJson);

        GuardrailToolCall call = screener.ToolCalls.Should().ContainSingle().Subject;
        call.ToolName.Should().Be("WriteSandboxFile");
        call.ArgumentsJson.Should().Contain(SamplePath);
    }

    // --- 2. The model's tool declaration is unchanged ---------------------------------------------

    [Fact]
    public void Wrapping_forwards_name_description_and_json_schema_byte_for_byte()
    {
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(new RecordingSandboxHandler());
        AgentToolProvider provider = CreateProvider(sandboxTools, new StubScreener());

        IReadOnlyDictionary<string, AIFunction> inner = new Dictionary<string, AIFunction>
        {
            ["WriteSandboxFile"] = AIFunctionFactory.Create(sandboxTools.WriteSandboxFile),
            ["EditSandboxFile"] = AIFunctionFactory.Create(sandboxTools.EditSandboxFile),
            ["ApplySandboxFileEdits"] = AIFunctionFactory.Create(sandboxTools.ApplySandboxFileEdits),
            ["InsertSandboxFileLines"] = AIFunctionFactory.Create(sandboxTools.InsertSandboxFileLines),
            ["RunSandboxRemotionCommand"] = AIFunctionFactory.Create(sandboxTools.RunSandboxRemotionCommand),
            ["RenderVideoAndUploadToStorage"] = AIFunctionFactory.Create(sandboxTools.RenderVideoAndUploadToStorage),
        };

        foreach (string name in ScreenedNames)
        {
            AIFunction wrapped = ToolNamed(provider, name);
            AIFunction source = inner[name];

            wrapped.Name.Should().Be(source.Name);
            wrapped.Description.Should().Be(source.Description, "a reworded description is prompt drift");
            wrapped.JsonSchema.GetRawText().Should().Be(
                source.JsonSchema.GetRawText(),
                "the wrapper must not regenerate the schema from a delegate — the model's tool declaration has to be byte-identical");
            wrapped.AdditionalProperties.Count.Should().Be(source.AdditionalProperties.Count);
            wrapped.UnderlyingMethod.Should().BeSameAs(
                source.UnderlyingMethod,
                "the framework's own forwarding keeps every declaration-only surface on the inner function");
        }
    }

    // --- 3. A rejection is a tool RESULT and the tool never runs ----------------------------------

    [Fact]
    public async Task A_rejected_call_returns_the_self_correcting_shape_and_never_invokes_the_tool()
    {
        RecordingSandboxHandler handler = new();
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(handler);
        StubScreener screener = new()
        {
            ToolVerdict = GuardrailVerdict.Reject(
                "The call to tool call 'WriteSandboxFile' does not look consistent with the task (p=0.97).",
                GuardrailKind.Probabilistic),
        };
        AgentToolProvider provider = CreateProvider(sandboxTools, screener);

        object? result = await Invoke(ToolNamed(provider, "WriteSandboxFile"), WriteArguments());

        using JsonDocument document = JsonDocument.Parse(ResultJson(result));
        document.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString()
            .Should().Contain("does not look consistent with the task");

        handler.RequestCount.Should().Be(
            0,
            "a rejected call must not reach the sandbox at all — returning the error shape while still running the tool would be the worst of both worlds");
        screener.ToolCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task A_rejection_is_reported_with_the_exact_shape_the_sandbox_editors_use()
    {
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(new RecordingSandboxHandler());
        StubScreener screener = new()
        {
            ToolVerdict = GuardrailVerdict.Reject("nope", GuardrailKind.HardRule),
        };

        object? result = await Invoke(
            ToolNamed(CreateProvider(sandboxTools, screener), "EditSandboxFile"),
            new AIFunctionArguments
            {
                ["path"] = SamplePath,
                ["oldText"] = "a",
                ["newText"] = "b",
            });

        ResultJson(result).Should().Be(
            """{"ok":false,"error":"nope"}""",
            "the model must read a rejection as the same self-correcting shape the sandbox tools already return, or it will not fix its next call");
    }

    // --- 4. Fail open on a throwing screener ------------------------------------------------------

    [Fact]
    public async Task A_throwing_screener_fails_open_and_the_tool_still_runs()
    {
        RecordingSandboxHandler handler = new();
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(handler);
        StubScreener screener = new()
        {
            OnToolCall = _ => throw new InvalidOperationException("the screener blew up"),
        };
        AgentToolProvider provider = CreateProvider(sandboxTools, screener);

        object? wrapped = await Invoke(ToolNamed(provider, "WriteSandboxFile"), WriteArguments());
        object? unwrapped = await Invoke(
            AIFunctionFactory.Create(sandboxTools.WriteSandboxFile), WriteArguments());

        AssertSameToolResult(wrapped, unwrapped);
        handler.RequestCount.Should().Be(2);
    }

    // --- 4b. ...but cancellation is NOT a failure to open -----------------------------------------

    [Fact]
    public async Task A_cancelled_execution_never_reaches_the_sandbox_tool()
    {
        // Fail-open is a rule about provider failures. Swallowing a cancellation here let a
        // cancelled execution enter the very sandbox write/render tool the wrapper was about to
        // judge, which is the opposite of what stopping a run is supposed to mean.
        RecordingSandboxHandler handler = new();
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(handler);
        StubScreener screener = new()
        {
            OnToolCall = _ => throw new OperationCanceledException(),
        };
        AgentToolProvider provider = CreateProvider(sandboxTools, screener);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        AIFunction write = ToolNamed(provider, "WriteSandboxFile");
        Func<Task> act = () => write.InvokeAsync(WriteArguments(), cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.RequestCount.Should().Be(
            0,
            "a cancelled execution must not proceed into the tool it was about to screen");
    }

    [Fact]
    public async Task A_screener_cancellation_without_our_token_still_fails_open()
    {
        // The other half of the guard: a provider TIMEOUT surfaces as an OperationCanceledException
        // too, but with our token uncancelled — plan §1 D2 requires that shape to still fail open.
        RecordingSandboxHandler handler = new();
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(handler);
        StubScreener screener = new()
        {
            OnToolCall = _ => throw new TaskCanceledException("the decision provider timed out"),
        };
        AgentToolProvider provider = CreateProvider(sandboxTools, screener);

        object? wrapped = await Invoke(ToolNamed(provider, "WriteSandboxFile"), WriteArguments());
        object? unwrapped = await Invoke(
            AIFunctionFactory.Create(sandboxTools.WriteSandboxFile), WriteArguments());

        AssertSameToolResult(wrapped, unwrapped);
        handler.RequestCount.Should().Be(2);
    }

    // --- 5. The state is a bounded summary, never the file body -----------------------------------

    [Fact]
    public async Task A_large_file_body_is_summarized_and_the_state_never_exceeds_the_configured_bound()
    {
        const int stateMaxChars = 512;
        string body = new('x', 100_000);

        RecordingSandboxHandler handler = new();
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(handler);
        StubScreener screener = new();
        AgentToolProvider provider = CreateProvider(
            sandboxTools,
            screener,
            new GuardrailConfig { StateMaxChars = stateMaxChars });

        object? result = await Invoke(ToolNamed(provider, "WriteSandboxFile"), WriteArguments(body));

        ResultJson(result).Should().Be(WriteSuccessJson, "screening an allowed call must not alter it");

        GuardrailToolCall call = screener.ToolCalls.Should().ContainSingle().Subject;
        call.ArgumentsJson.Length.Should().BeLessThanOrEqualTo(
            stateMaxChars,
            "GuardrailConfig.StateMaxChars is the bound, and the wrapper — not only the screener — has to honour it");
        call.ArgumentsJson.Should().NotContain(body, "the whole file body must never reach the screen");
        call.ArgumentsJson.Should().Contain(SamplePath, "the path is the part of the call the question is really about");

        // The summary parses as JSON, which is what lets the screener render it argument by argument
        // instead of collapsing to a single unparsed excerpt.
        using JsonDocument document = JsonDocument.Parse(call.ArgumentsJson);
        JsonElement content = document.RootElement.GetProperty("content");
        content.GetProperty("length").GetInt32().Should().Be(body.Length);
        content.GetProperty("sha256").GetString().Should().HaveLength(64);
        content.GetProperty("head").GetString().Should().NotBeNull();
        content.GetProperty("head").GetString()!.Length.Should().BeLessThan(body.Length);
    }

    [Fact]
    public async Task With_the_shipping_default_bound_a_large_body_is_still_bounded()
    {
        string body = new('y', 250_000);

        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(new RecordingSandboxHandler());
        StubScreener screener = new();

        await Invoke(ToolNamed(CreateProvider(sandboxTools, screener), "WriteSandboxFile"), WriteArguments(body));

        GuardrailToolCall call = screener.ToolCalls.Should().ContainSingle().Subject;
        call.ArgumentsJson.Length.Should().BeLessThanOrEqualTo(GuardrailConfig.DefaultStateMaxChars);
        call.ArgumentsJson.Should().NotContain(body);
    }

    // --- 6. Exactly five tools are wrapped, and no others are --------------------------------------

    [Fact]
    public void Exactly_the_five_named_tools_are_wrapped_for_an_agent_granted_both_sandbox_groups()
    {
        // AuthorAgent is the agent whose grant includes SandboxAuthoring AND SandboxRender.
        AgentToolProvider provider = CreateProvider(
            CreateSandboxTools(new RecordingSandboxHandler()), new StubScreener());

        IReadOnlyList<AIFunction> tools = provider.GetTools(AgentType.AuthorAgent);

        HashSet<string> wrapped = tools
            .Where(t => t is AgentToolProvider.ScreenedAIFunction)
            .Select(t => t.Name)
            .ToHashSet();

        wrapped.Should().BeEquivalentTo(ScreenedNames);

        // Every other granted tool — including the four siblings inside the very same SandboxAuthoring
        // group — is handed to the model untouched, so ToolScopingDriftGuardTests and every existing
        // tool test keep meaning exactly what they meant.
        foreach (string unscreened in UnscreenedToolNames)
        {
            AIFunction tool = tools.Should().ContainSingle(t => t.Name == unscreened).Subject;
            tool.Should().NotBeOfType<AgentToolProvider.ScreenedAIFunction>(
                $"{unscreened} is deliberately not screened — the wrapped set is exactly the five the brief names");
        }
    }

    [Fact]
    public void No_agent_type_ever_gets_a_screened_tool_outside_the_five()
    {
        AgentToolProvider provider = CreateProvider(
            CreateSandboxTools(new RecordingSandboxHandler()), new StubScreener());

        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            IEnumerable<string> wrapped = provider.GetTools(agentType)
                .Where(t => t is AgentToolProvider.ScreenedAIFunction)
                .Select(t => t.Name);

            wrapped.Should().BeSubsetOf(ScreenedNames, $"{agentType} must never have an unnamed tool screened");
        }
    }

    [Fact]
    public void Without_a_screener_nothing_is_wrapped_at_all()
    {
        AgentToolProvider provider = CreateProvider(
            CreateSandboxTools(new RecordingSandboxHandler()), screener: null);

        provider.GetTools(AgentType.AuthorAgent)
            .Should().NotContain(t => t is AgentToolProvider.ScreenedAIFunction);
    }

    // --- 7. The real container actually reaches the new constructor parameter -----------------------

    [Fact]
    public void The_dependency_injection_container_injects_the_screener_into_the_new_constructor()
    {
        // The constructor's guardrail parameters are optional, which would be worthless (or actively
        // harmful) if the container then failed to supply a registered screener and silently fell
        // back to the default. This resolves the provider through the real
        // Microsoft.Extensions.DependencyInjection path, not ActivatorUtilities, because that is the
        // path WorkflowEngineServiceCollectionExtensions uses.
        ReactRemotionSandboxTools sandboxTools = CreateSandboxTools(new RecordingSandboxHandler());
        StubScreener screener = new();

        ServiceCollection services = new();
        services.AddSingleton(sandboxTools);
        services.AddSingleton(new ProjectFileAgentTools(
            Mock.Of<IProjectFileWorkspace>(),
            Mock.Of<IWorkflowExecutionContextAccessor>(),
            Mock.Of<IHttpClientFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<ProjectFileAgentTools>.Instance));
        services.AddSingleton(new WorkflowControlAgentTools(
            Mock.Of<IWorkflowExecutionContextAccessor>(),
            NullLogger<WorkflowControlAgentTools>.Instance));
        services.AddSingleton(new DesignSystemAgentTools(
            Mock.Of<IWorkflowExecutionContextAccessor>(),
            NullLogger<DesignSystemAgentTools>.Instance));
        services.AddSingleton(WebSearchAgentToolsTests.CreateInert());
        services.AddSingleton(BlockAgentToolsTests.CreateInert());
        services.AddSingleton(new VideoEventsAgentTools(
            Mock.Of<IWorkflowExecutionContextAccessor>(), Mock.Of<IServiceScopeFactory>(), Mock.Of<IProjectFileWorkspace>()));
        services.AddSingleton(MomentLookAgentToolsTests.CreateInert());
        services.AddSingleton<IGuardrailScreener>(screener);
        services.Configure<GuardrailConfig>(_ => { });
        services.AddSingleton<AgentToolProvider>();

        AgentToolProvider provider = services.BuildServiceProvider().GetRequiredService<AgentToolProvider>();

        provider.GetTools(AgentType.AuthorAgent)
            .Where(t => t is AgentToolProvider.ScreenedAIFunction)
            .Select(t => t.Name)
            .Should().BeEquivalentTo(ScreenedNames, "a registered IGuardrailScreener must reach the provider");
    }

    // --- helpers ----------------------------------------------------------------------------------

    private static AgentToolProvider CreateProvider(
        ReactRemotionSandboxTools sandboxTools,
        IGuardrailScreener? screener,
        GuardrailConfig? guardrailConfig = null) =>
        new(
            projectFileTools: new ProjectFileAgentTools(
                Mock.Of<IProjectFileWorkspace>(),
                Mock.Of<IWorkflowExecutionContextAccessor>(),
                Mock.Of<IHttpClientFactory>(),
                new ConfigurationBuilder().Build(),
                NullLogger<ProjectFileAgentTools>.Instance),
            sandboxTools: sandboxTools,
            workflowControlTools: new WorkflowControlAgentTools(
                Mock.Of<IWorkflowExecutionContextAccessor>(),
                NullLogger<WorkflowControlAgentTools>.Instance),
            designSystemTools: new DesignSystemAgentTools(
                Mock.Of<IWorkflowExecutionContextAccessor>(),
                NullLogger<DesignSystemAgentTools>.Instance),
            webSearchTools: WebSearchAgentToolsTests.CreateInert(),
            blockTools: BlockAgentToolsTests.CreateInert(),
            videoEventsTools: new VideoEventsAgentTools(
                Mock.Of<IWorkflowExecutionContextAccessor>(), Mock.Of<IServiceScopeFactory>(), Mock.Of<IProjectFileWorkspace>()),
            momentLookTools: MomentLookAgentToolsTests.CreateInert(),
            guardrailScreener: screener,
            guardrailConfig: guardrailConfig is null ? null : Options.Create(guardrailConfig));

    private static ReactRemotionSandboxTools CreateSandboxTools(HttpMessageHandler handler)
    {
        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler));

        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.SetupGet(a => a.Current).Returns(new WorkflowExecutionContext
        {
            ExecutionId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            CorrelationId = "screening-test",
        });

        // A REAL scope factory, deliberately not Mock.Of<IServiceScopeFactory>(): the mock's
        // CreateScope() returns null, which would satisfy the constructor and leave the same
        // silent-null defect one layer down. The sandbox tools resolve nothing from this scope on
        // the paths under test — they only need it to be a working factory.
        IServiceScopeFactory scopeFactory = new ServiceCollection()
            .AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()))
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new ReactRemotionSandboxTools(
            factory.Object,
            accessor.Object,
            Mock.Of<IProjectFileWorkspace>(),
            Mock.Of<IAmazonS3>(),
            new ConfigurationBuilder().Build(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopeFactory);
    }

    private static AIFunction ToolNamed(AgentToolProvider provider, string name) =>
        provider.GetTools(AgentType.AuthorAgent).Single(t => t.Name == name);

    private static AIFunctionArguments WriteArguments(string content = SampleBody) => new()
    {
        ["path"] = SamplePath,
        ["content"] = content,
    };

    private static async Task<object?> Invoke(AIFunction function, AIFunctionArguments arguments) =>
        await function.InvokeAsync(arguments, CancellationToken.None);

    /// <summary>
    /// The tool-result JSON text. The framework marshals a string-returning tool into a JSON string
    /// element, so the text the model reads is <c>GetString()</c> — normalized here the same way the
    /// existing <c>WorkflowAssistantVideoGenerateValidationTests</c> does it.
    /// </summary>
    private static string ResultJson(object? result) =>
        result is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : result!.ToString()!;

    /// <summary>
    /// Asserts two tool results are byte-identical as serialized values — not merely equivalent
    /// objects — which is what the ticket's no-screening contract actually promises.
    /// </summary>
    private static void AssertSameToolResult(object? actual, object? expected)
    {
        JsonSerializer.Serialize(actual).Should().Be(
            JsonSerializer.Serialize(expected),
            "with screening inert the wrapped tool must produce a byte-identical serialized result");
        ResultJson(actual).Should().Be(ResultJson(expected));
    }

    /// <summary>
    /// Records how many requests the sandbox API actually received. This is the "recording fake"
    /// that proves a rejected call never ran the tool: asserting on the returned string alone could
    /// not distinguish "blocked" from "ran and then lied".
    /// </summary>
    private sealed class RecordingSandboxHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}"""),
            });
        }
    }

    private sealed class StubScreener : IGuardrailScreener
    {
        public List<GuardrailToolCall> ToolCalls { get; } = [];

        public GuardrailVerdict ToolVerdict { get; set; } = GuardrailVerdict.Allow;

        public Func<GuardrailToolCall, Task<GuardrailVerdict>>? OnToolCall { get; set; }

        public Task<GuardrailVerdict> ScreenToolCallAsync(GuardrailToolCall call, CancellationToken ct)
        {
            ToolCalls.Add(call);
            return OnToolCall is null ? Task.FromResult(ToolVerdict) : OnToolCall(call);
        }

        public Task<GuardrailVerdict> ScreenSpanAsync(GuardrailSpan span, CancellationToken ct) =>
            Task.FromResult(GuardrailVerdict.Allow);
    }
}
