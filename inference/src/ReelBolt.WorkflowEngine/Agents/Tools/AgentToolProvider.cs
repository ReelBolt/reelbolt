using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.Guardrails;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

public class AgentToolProvider : IAgentToolProvider
{
    /// <summary>
    /// Exactly the tools guardrail screening wraps: the sandbox write surface and the render
    /// surface. Those are the two ways an instruction that reached an agent through untrusted,
    /// media-derived content (an on-screen text caption or an ASR span — the documented
    /// <c>MotionGraphicsPlanner</c>/<c>MotionGraphicsDirector</c> tradeoff) can turn into something
    /// that actually happens: writing code into the sandbox that later runs, or producing/uploading
    /// a rendered artefact.
    /// </summary>
    /// <remarks>
    /// Deliberately a closed set of six and not "everything in <see cref="ToolGroup.SandboxAuthoring"/>
    /// plus <see cref="ToolGroup.SandboxRender"/>". Their siblings in the same groups are not wrapped:
    /// <c>EnsureSandbox</c> (creates a workspace), <c>DeleteSandboxPath</c> (removes a path),
    /// <c>StageVoiceoverAudio</c>/<c>StageGeneratedClips</c>/<c>StageProjectFile</c> (copy stored
    /// bytes the model never authored — <c>StageProjectFile</c> only into <c>public/</c>, where nothing
    /// runs unless screened code imports it), <c>InstallNpmPackages</c>/<c>RunSandboxNpmScript</c> (run a fixed, allow-listed set of npm
    /// scripts) and <c>CompleteSandbox</c> (not granted to any agent at all). Screening is a
    /// per-call model question with a cost, so the set stays at the two verbs that carry content and
    /// consequence. Every tool that writes model-authored text into the sandbox is in it —
    /// <c>InsertSandboxFileLines</c> joined the three editors for that reason, since leaving it out
    /// would have been a way around screening the others.
    /// </remarks>
    internal static readonly IReadOnlySet<string> ScreenedToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ReactRemotionSandboxTools.WriteSandboxFile),
        nameof(ReactRemotionSandboxTools.EditSandboxFile),
        nameof(ReactRemotionSandboxTools.ApplySandboxFileEdits),
        nameof(ReactRemotionSandboxTools.InsertSandboxFileLines),
        nameof(ReactRemotionSandboxTools.RunSandboxRemotionCommand),
        nameof(ReactRemotionSandboxTools.RenderVideoAndUploadToStorage),
    };

    private const int LongValueHeadChars = 256;
    private static readonly int[] FallbackHeadChars = [128, 64, 0];
    private const string TruncationMarker = "…[truncated]";

    private static readonly JsonSerializerOptions SummaryJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ProjectFileAgentTools _projectFileTools;
    private readonly ReactRemotionSandboxTools _sandboxTools;
    private readonly WorkflowControlAgentTools _workflowControlTools;
    private readonly DesignSystemAgentTools _designSystemTools;
    private readonly WebSearchAgentTools _webSearchTools;
    private readonly BlockAgentTools _blockTools;
    private readonly VideoEventsAgentTools _videoEventsTools;
    private readonly MomentLookAgentTools _momentLookTools;
    private readonly IGuardrailScreener? _guardrailScreener;
    private readonly int _guardrailStateMaxChars;

    /// <param name="designSystemTools">
    /// The <see cref="ToolGroup.DesignSystemRead"/> tools (SPEC §12), read-only over the run's pinned
    /// design/product documents. Required (not optional): every agent the catalog grants the group to
    /// must actually receive the two functions, and a null here would silently under-grant exactly the
    /// agents the drift guard exists to protect.
    /// </param>
    /// <param name="webSearchTools">
    /// The <see cref="ToolGroup.WebSearch"/> tool. Required for the same reason as the design-system tools: a null
    /// would silently under-grant the agents the drift guard protects.
    /// </param>
    /// <param name="blockTools">
    /// The <see cref="ToolGroup.SandboxBlocks"/> tools. Required for the same reason as the other tool groups.
    /// </param>
    /// <param name="videoEventsTools">
    /// The <see cref="ToolGroup.EventsRead"/> tools. Required for the same reason as the other tool groups.
    /// </param>
    /// <param name="momentLookTools">
    /// The <see cref="ToolGroup.MomentLook"/> tool. Required for the same reason as the other tool groups.
    /// </param>
    /// <param name="guardrailScreener">
    /// The shipped <see cref="IGuardrailScreener"/> singleton. Optional (and nullable) because a
    /// provider built without a screener wraps nothing and every tool call behaves exactly as it did
    /// before guardrails existed, which is the same thing an <see cref="GuardrailMode.Off"/> screener
    /// does.
    /// </param>
    /// <param name="guardrailConfig">
    /// Bounds the arguments summary this provider hands the screener (never the full file body).
    /// Read from configuration rather than hardcoded to <see cref="GuardrailConfig.DefaultStateMaxChars"/>
    /// so an operator lowering <c>WorkflowEngine:Guardrails:StateMaxChars</c> lowers the wrapper's
    /// own bound too — the screener's independent bound would otherwise be the only one, and this
    /// provider would be handing it a larger blob than the operator asked for.
    /// </param>
    public AgentToolProvider(
        ProjectFileAgentTools projectFileTools,
        ReactRemotionSandboxTools sandboxTools,
        WorkflowControlAgentTools workflowControlTools,
        DesignSystemAgentTools designSystemTools,
        WebSearchAgentTools webSearchTools,
        BlockAgentTools blockTools,
        VideoEventsAgentTools videoEventsTools,
        MomentLookAgentTools momentLookTools,
        IGuardrailScreener? guardrailScreener = null,
        IOptions<GuardrailConfig>? guardrailConfig = null)
    {
        _projectFileTools = projectFileTools;
        _sandboxTools = sandboxTools;
        _workflowControlTools = workflowControlTools;
        _designSystemTools = designSystemTools;
        _webSearchTools = webSearchTools;
        _blockTools = blockTools;
        _videoEventsTools = videoEventsTools;
        _momentLookTools = momentLookTools;
        _guardrailScreener = guardrailScreener;
        _guardrailStateMaxChars = ResolveStateMaxChars(guardrailConfig?.Value);
    }

    /// <summary>
    /// Resolves the <see cref="ToolGroup"/>s <see cref="ToolGroupCatalog.GroupsFor"/> grants a
    /// given <see cref="AgentType"/> into the actual, runnable <see cref="AIFunction"/>s. The
    /// scoping DECISION (which groups an agent type gets, and why) lives in
    /// <see cref="ToolGroupCatalog"/> — the single source of truth shared with
    /// <c>DatabaseSeeder.GetAvailableToolsJson</c> on the Inference API side. This class only
    /// knows how to turn a group into real <c>AIFunctionFactory.Create(...)</c> calls, since it
    /// is the one holding the injected tool-implementation instances.
    ///
    /// Remotion knowledge-base access is deliberately NOT a static tool group here — it is
    /// resolved per agent-run in <c>ReelBoltAgentBase.CreateAgentAsync</c> via
    /// <c>SkillCatalog.DefaultsFor</c>/<c>ISkillAgentToolsFactory</c> instead, since it is a
    /// named, on-demand-loadable skill (UseSkill/ReadSkillResource), not a tool with a fixed
    /// static surface. See ReelBolt.Shared/Skills/SkillCatalog.cs.
    /// </summary>
    public IReadOnlyList<AIFunction> GetTools(AgentType agentType)
    {
        IReadOnlyList<ToolGroup> groups = ToolGroupCatalog.GroupsFor(agentType);

        List<AIFunction> tools = new();
        foreach (ToolGroup group in groups)
            tools.AddRange(BuildGroup(group));

        return tools;
    }

    /// <summary>
    /// Wraps a member of <see cref="ScreenedToolNames"/> in a <see cref="ScreenedAIFunction"/> and
    /// returns every other tool untouched — the wrapper set is exactly the six above, never more.
    /// With no screener injected (and with the shipped <see cref="GuardrailMode.Off"/> config) this
    /// is a pure pass-through, so the tool list a model sees is unchanged.
    /// </summary>
    private AIFunction Screen(AIFunction function) =>
        _guardrailScreener is null || !ScreenedToolNames.Contains(function.Name)
            ? function
            : new ScreenedAIFunction(function, _guardrailScreener, _guardrailStateMaxChars);

    private IEnumerable<AIFunction> BuildGroup(ToolGroup group) => group switch
    {
        ToolGroup.ProjectRead =>
        [
            AIFunctionFactory.Create(_projectFileTools.ListProjectFiles),
            AIFunctionFactory.Create(_projectFileTools.ReadProjectFile),
            AIFunctionFactory.Create(_projectFileTools.ReadProjectFileLines),
            AIFunctionFactory.Create(_projectFileTools.GetProjectFileOutline),
            AIFunctionFactory.Create(_projectFileTools.SearchProjectFiles),
            AIFunctionFactory.Create(_projectFileTools.GetDeterministicContextFiles)
        ],

        ToolGroup.ProjectWrite =>
        [
            AIFunctionFactory.Create(_projectFileTools.WriteProjectFile),
            AIFunctionFactory.Create(_projectFileTools.EditProjectFile)
        ],

        // ReadSandboxFileLines/GetSandboxFileOutline are read-only — see ToolGroupCatalog's
        // matching comment for why they sit in Browse rather than Authoring.
        ToolGroup.SandboxBrowse =>
        [
            AIFunctionFactory.Create(_sandboxTools.GetSandboxStatus),
            AIFunctionFactory.Create(_sandboxTools.ListSandboxFiles),
            AIFunctionFactory.Create(_sandboxTools.ReadSandboxFile),
            AIFunctionFactory.Create(_sandboxTools.ReadSandboxFileLines),
            AIFunctionFactory.Create(_sandboxTools.GetSandboxFileOutline)
        ],

        ToolGroup.SandboxMetadata =>
        [
            AIFunctionFactory.Create(_sandboxTools.GetSandbox)
        ],

        ToolGroup.SandboxLint =>
        [
            AIFunctionFactory.Create(_sandboxTools.CheckLintAndTypeErrors)
        ],

        // EditSandboxFile/ApplySandboxFileEdits mutate an existing file — see ToolGroupCatalog's
        // matching comment for why they sit in Authoring alongside WriteSandboxFile.
        //
        // Only the four members that write model-authored text are screened; EnsureSandbox, the
        // three Stage* copies, DeleteSandboxPath, InstallNpmPackages and RunSandboxNpmScript are
        // not — see ScreenedToolNames for why the set is exactly six.
        ToolGroup.SandboxAuthoring =>
        [
            AIFunctionFactory.Create(_sandboxTools.EnsureSandbox),
            Screen(AIFunctionFactory.Create(_sandboxTools.WriteSandboxFile)),
            AIFunctionFactory.Create(_sandboxTools.StageVoiceoverAudio),
            AIFunctionFactory.Create(_sandboxTools.StageGeneratedClips),
            AIFunctionFactory.Create(_sandboxTools.StageProjectFile),
            AIFunctionFactory.Create(_sandboxTools.StageFont),
            Screen(AIFunctionFactory.Create(_sandboxTools.EditSandboxFile)),
            Screen(AIFunctionFactory.Create(_sandboxTools.ApplySandboxFileEdits)),
            Screen(AIFunctionFactory.Create(_sandboxTools.InsertSandboxFileLines)),
            AIFunctionFactory.Create(_sandboxTools.DeleteSandboxPath),
            AIFunctionFactory.Create(_sandboxTools.InstallNpmPackages),
            AIFunctionFactory.Create(_sandboxTools.RunSandboxNpmScript)
        ],

        ToolGroup.SandboxRender =>
        [
            Screen(AIFunctionFactory.Create(_sandboxTools.RunSandboxRemotionCommand)),
            Screen(AIFunctionFactory.Create(_sandboxTools.RenderVideoAndUploadToStorage)),
            // GetCompositionInfo/RenderPreviewStills are NOT screened (ScreenedToolNames stays exactly
            // six): they write no file and upload nothing, and the only code they execute is code
            // that already went through the screened WriteSandboxFile/EditSandboxFile/
            // ApplySandboxFileEdits/InsertSandboxFileLines. They are the cheap checks that precede the screened render.
            AIFunctionFactory.Create(_sandboxTools.GetCompositionInfo),
            AIFunctionFactory.Create(_sandboxTools.RenderPreviewStills),
            // CheckLayout is unscreened for the same reason: it measures, writes no source and uploads nothing.
            AIFunctionFactory.Create(_sandboxTools.CheckLayout),
            // DeclareRenderEvents is unscreened: it writes only a validated manifest (names [a-z0-9_-],
            // integer frames, allowlist-sanitized labels), never source code.
            AIFunctionFactory.Create(_sandboxTools.DeclareRenderEvents)
            // CompleteSandbox intentionally omitted — see ToolGroupCatalog.SandboxRender.
        ],

        // The reviewer's look at the Author's frames (ToolGroupCatalog.SandboxPreview): the same two
        // unscreened preview functions SandboxRender carries, without anything that renders a deliverable.
        ToolGroup.SandboxPreview =>
        [
            AIFunctionFactory.Create(_sandboxTools.GetCompositionInfo),
            AIFunctionFactory.Create(_sandboxTools.RenderPreviewStills)
        ],

        // AddBlock copies vetted catalog files under the fixed src/blocks/ prefix; nothing the model writes is
        // executed or uploaded by the tool itself, so it is not screened (the agent's later edits are).
        ToolGroup.SandboxBlocks =>
        [
            AIFunctionFactory.Create(_blockTools.ListBlocks),
            AIFunctionFactory.Create(_blockTools.GetBlock),
            AIFunctionFactory.Create(_blockTools.AddBlock)
        ],

        ToolGroup.WorkflowControl =>
        [
            AIFunctionFactory.Create(_workflowControlTools.FailWorkflow)
        ],

        // ReadDesignSystem/ReadProductProfile read the run's ambient pin; they touch no caller-named
        // project or file, so they are neither screened nor parameterized by id.
        ToolGroup.DesignSystemRead =>
        [
            AIFunctionFactory.Create(_designSystemTools.ReadDesignSystem),
            AIFunctionFactory.Create(_designSystemTools.ReadProductProfile)
        ],

        // WebSearch is an input to the model, not text-to-action: it is not in ScreenedToolNames. Its output is
        // fenced and labelled untrusted by the tool itself, and no tool can open the URLs it returns.
        ToolGroup.WebSearch =>
        [
            AIFunctionFactory.Create(_webSearchTools.WebSearch)
        ],

        // Read-only windows onto this run's own video analysis: no caller-named project or file, nothing to screen.
        ToolGroup.EventsRead =>
        [
            AIFunctionFactory.Create(_videoEventsTools.ListVideoEvents),
            AIFunctionFactory.Create(_videoEventsTools.DescribeVideoWindow)
        ],

        ToolGroup.FootageRead =>
        [
            AIFunctionFactory.Create(_videoEventsTools.ReadVideoSection),
            AIFunctionFactory.Create(_videoEventsTools.ReadVideoTranscript),
            AIFunctionFactory.Create(_videoEventsTools.ListVideoSfxClips),
            AIFunctionFactory.Create(_videoEventsTools.ListVideoMusic)
        ],

        ToolGroup.OfferListRead =>
        [
            AIFunctionFactory.Create(_videoEventsTools.ListVideoSfxClips),
            AIFunctionFactory.Create(_videoEventsTools.ListVideoMusic),
            AIFunctionFactory.Create(_videoEventsTools.ReadVideoSection)
        ],

        // LookAtMoment is an input to the model, not text-to-action: it is not in ScreenedToolNames. It takes one event
        // id of this run's own analysis, and its answer is cleaned and labelled untrusted by the tool itself.
        ToolGroup.MomentLook =>
        [
            AIFunctionFactory.Create(_momentLookTools.LookAtMoment)
        ],

        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown ToolGroup.")
    };

    private static int ResolveStateMaxChars(GuardrailConfig? config)
    {
        int configured = config?.StateMaxChars ?? GuardrailConfig.DefaultStateMaxChars;

        // A non-positive bound is a misconfiguration, not an instruction to screen every call
        // against nothing. Fall back to the shipping default rather than truncating the summary
        // to the empty string.
        return configured > 0 ? configured : GuardrailConfig.DefaultStateMaxChars;
    }

    /// <summary>
    /// Builds the arguments <em>summary</em> handed to the screener: one compact JSON object, with
    /// every long string argument replaced by its length, a SHA-256 of its content and a truncated
    /// head — never the value itself. Bounded by <c>GuardrailConfig.StateMaxChars</c>.
    /// </summary>
    /// <remarks>
    /// This exists because a <c>WriteSandboxFile</c> call routinely carries tens of KB of TypeScript
    /// in its <c>content</c> argument, and the decision model's window is small. Handing the raw
    /// arguments blob to the screen would blow the state budget, and it would also be useless: the
    /// question asked is whether the call is consistent with the task, which the path, the size, the
    /// hash and a head of the content answer just as well as the whole body. The screener bounds its
    /// own composed state too — this is the same discipline applied one layer earlier, so the
    /// operator-configured bound is honoured by the producer as well as by the consumer.
    /// </remarks>
    internal static string SummarizeArguments(AIFunctionArguments arguments, int stateMaxChars)
    {
        string summary = BuildSummary(arguments, LongValueHeadChars);
        if (summary.Length <= stateMaxChars)
            return summary;

        // Shrink the retained head rather than emitting invalid JSON: the screener parses this as
        // JSON to render one line per argument, and a truncated blob would collapse to a single
        // unparsed excerpt. Only reachable with a very low configured bound or an unusually wide
        // argument set.
        foreach (int headChars in FallbackHeadChars)
        {
            summary = BuildSummary(arguments, headChars);
            if (summary.Length <= stateMaxChars)
                return summary;
        }

        return summary.Length <= stateMaxChars
            ? summary
            : summary[..Math.Max(0, stateMaxChars - TruncationMarker.Length)] + TruncationMarker;
    }

    private static string BuildSummary(AIFunctionArguments arguments, int headChars)
    {
        JsonObject summary = new();

        foreach (KeyValuePair<string, object?> argument in arguments)
            summary[argument.Key] = DescribeArgument(argument.Value, headChars);

        return summary.ToJsonString(SummaryJsonOptions);
    }

    private static JsonNode? DescribeArgument(object? value, int headChars)
    {
        switch (value)
        {
            case null:
                return null;

            case string text when text.Length > headChars:
                // The shape the ticket asks for: path/name stays verbatim, the body becomes
                // length + hash + a bounded head. Never the body.
                return new JsonObject
                {
                    ["length"] = text.Length,
                    ["sha256"] = Sha256Hex(text),
                    ["head"] = text[..headChars],
                };

            case string text:
                return JsonValue.Create(text);

            default:
                string json;
                try
                {
                    json = JsonSerializer.Serialize(value, SummaryJsonOptions);
                }
                catch (NotSupportedException)
                {
                    // An argument value the serializer cannot render (an exotic CLR object) is not
                    // a reason to skip screening entirely — nor to fail the tool call. Name its
                    // type and move on.
                    return JsonValue.Create(value.GetType().Name);
                }

                if (json.Length <= headChars)
                    return JsonNode.Parse(json);

                return JsonValue.Create(json[..headChars] + TruncationMarker);
        }
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Screens one sandbox-write or render tool call through
    /// <see cref="IGuardrailScreener.ScreenToolCallAsync"/> before letting the real tool run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inherits <see cref="DelegatingAIFunction"/> rather than re-deriving the tool with
    /// <c>AIFunctionFactory.Create</c> over a new delegate. That is the whole point: a rebuilt
    /// function would be named after the delegate and would generate its own JSON schema from the
    /// delegate's signature, so the model would see a different tool than before. A
    /// <see cref="DelegatingAIFunction"/> forwards <see cref="AIFunction.Name"/>,
    /// <see cref="AIFunction.Description"/> and <see cref="AIFunction.JsonSchema"/> — plus
    /// <c>UnderlyingMethod</c>, <c>JsonSerializerOptions</c> and <c>AdditionalProperties</c>, which a
    /// hand-written pass-through would have to remember — from the inner function, so the tool
    /// declaration the model sees is byte-identical. Only <c>InvokeCoreAsync</c> is overridden, and
    /// only to insert the screen in front of the delegate.
    /// </para>
    /// <para>
    /// <b>Step-cache key discipline: no new key component, and none is needed.</b> Screening runs
    /// per tool call at run time and never changes a step's persisted output. An allowed call is
    /// byte-identical to today's; a rejected one returns the same <c>{"ok":false,"error":…}</c>
    /// self-correcting tool result the model was already expected to recover from in its next turn,
    /// and it is that next turn — not the guardrail — that changes anything. The existing key
    /// already hashes the fully resolved step input, the agent's system prompt, provider and every
    /// step config blob, so there is nothing about screening to fold in; a key field here would
    /// describe nothing. Note also that this path is not even cacheable: an agent whose grant
    /// includes <c>SandboxAuthoring</c> or <c>SandboxRender</c> is already <c>CacheMode = Never</c>
    /// in <c>StepCachePolicy</c>, precisely because those tools mutate state outside the cached
    /// output.
    /// </para>
    /// <para>
    /// <b>Never throws — except on cancellation.</b> Summarization, the screen itself and a screener
    /// that throws all degrade to "run the inner function". A guardrail that turns a provider outage
    /// into a failed workflow is a worse bug than the injection it was watching for — the same
    /// fail-open polarity the screener documents internally, upheld one layer out rather than
    /// reintroduced as a throw. The one exception is an <see cref="OperationCanceledException"/>
    /// raised while our own token is cancelled: that is a cancelled execution, not a provider
    /// fault, and running the tool anyway is the opposite of useful.
    /// </para>
    /// </remarks>
    internal sealed class ScreenedAIFunction : DelegatingAIFunction
    {
        private readonly IGuardrailScreener _screener;
        private readonly int _stateMaxChars;

        internal ScreenedAIFunction(AIFunction innerFunction, IGuardrailScreener screener, int stateMaxChars)
            : base(innerFunction)
        {
            _screener = screener ?? throw new ArgumentNullException(nameof(screener));
            _stateMaxChars = stateMaxChars;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                GuardrailToolCall call = new(
                    Name,
                    SummarizeArguments(arguments, _stateMaxChars),
                    // No step brief is reachable from here, and it is not worth a new dependency to
                    // fake one: the ambient execution context (IWorkflowExecutionContextAccessor)
                    // carries ExecutionId/ProjectId/CorrelationId/AbortReason and no task text, and
                    // this provider is a singleton shared by every step. The screener handles a null
                    // brief — it simply omits the "task:" line — and the call arguments plus the
                    // system prompt the agent was already given remain the evidence.
                    TaskBrief: null);

                GuardrailVerdict verdict = await _screener.ScreenToolCallAsync(call, cancellationToken);

                if (!verdict.Allowed)
                {
                    // The sandbox tools' own self-correcting shape, serialized exactly the way they
                    // serialize theirs, so the model reads a rejection as an ordinary tool answer and
                    // fixes its next call instead of burning a step retry.
                    return JsonSerializer.Serialize(
                        new
                        {
                            ok = false,
                            error = verdict.Reason ?? $"The call to {Name} was rejected by guardrail screening."
                        },
                        SummaryJsonOptions);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A CANCELLED EXECUTION IS NOT A FAILURE TO OPEN. Re-thrown, because swallowing it
                // here is what let a cancelled execution walk into the very sandbox write/render
                // tool this wrapper was about to judge — the tool call is the thing that then runs
                // with an already-cancelled token, and "fail open" is a rule about provider
                // failures, not about the caller having given up. The `when` clause is what keeps
                // this from colliding with plan §D2: a provider TIMEOUT reaches this wrapper as a
                // TaskCanceledException too, but with our own token uncancelled, so it still falls
                // through to the fail-open catch below.
                throw;
            }
            catch (Exception)
            {
                // Fail OPEN, deliberately, and silently here: the screener already logs every
                // failure it sees at warning level, and re-throwing would turn a screening fault
                // into a failed agent run.
            }

            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}
