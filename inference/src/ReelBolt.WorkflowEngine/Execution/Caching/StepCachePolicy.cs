using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Decision;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <inheritdoc cref="IStepCachePolicy"/>
public sealed class StepCachePolicy : IStepCachePolicy
{
    private readonly IConfiguration? _configuration;
    private readonly ILogger<StepCachePolicy>? _logger;

    /// <summary>
    /// <paramref name="configuration"/> is the host configuration, read for exactly one thing: the
    /// decision-gate rule in <see cref="HasNoActiveDecisionGate"/>.
    ///
    /// <para>
    /// It is optional (defaulting to null) so a caller that has no configuration — these unit tests,
    /// and any composition predating the rule — gets the "absent key" answer, which is precisely the
    /// behaviour of an install that configures no <c>DecisionGate</c> at all. The real composition
    /// always supplies it: <c>WorkflowEngineServiceCollectionExtensions</c> registers
    /// <c>AddSingleton&lt;IStepCachePolicy, StepCachePolicy&gt;()</c> and <c>IConfiguration</c> is
    /// itself a singleton, so no lifetime change was needed.
    /// </para>
    ///
    /// <para>
    /// <paramref name="logger"/> is optional for the same reason and is resolved by the same DI
    /// registration (<c>ILogger&lt;T&gt;</c> is always registered). It exists only so that a typo'd
    /// <c>Mode</c> value produces the same warning here as it does from <c>AgentStepExecutor</c>:
    /// both readers go through <see cref="DecisionGateAgentConfig.ReadMode"/>, and a caller that
    /// passed no logger would otherwise be the one silent path to an inert-by-accident gate. A null
    /// logger suppresses the message and nothing else.
    /// </para>
    /// </summary>
    public StepCachePolicy(IConfiguration? configuration = null, ILogger<StepCachePolicy>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsCacheable(WorkflowStep step)
    {
        // Encode-once: a Plan-stage compile's result is a plan with no file. It is neither stored nor served - ahead of
        // the per-step override, which cannot make a plan stand in for an encoded result.
        if (CompileEncodeStages.IsPlanStage(step))
            return false;

        // A per-step override always wins, in either direction, over the built-in table below —
        // the workflow author is explicitly asserting they know better for this one step.
        //
        // That includes the decision-gate rule on the Agent arm. CacheMode=Always still forces
        // caching for a gate-configured Colorist/MusicSupervisor step, which re-opens the hazard the
        // rule exists to close — an author can thereby get a plan only the decision model produced
        // served to a run whose gate is Off. Left exactly as it is, deliberately: CacheMode=Always
        // means what it says, and quietly moving the gate check above the override would turn the
        // override into a lie for this one case. The residual is stated rather than hidden.
        if (step.CacheMode == StepCacheMode.Never)
            return false;
        if (step.CacheMode == StepCacheMode.Always)
            return true;

        return step.StepType switch
        {
            // Deterministic, non-LLM step types: same inputs always produce the same output, and
            // none of them has a side effect outside the output they return (Extract is pure
            // projection; VideoAnalyze/VideoCompile/Voiceover only touch WorkflowEngine-owned scratch space
            // and a fresh S3 object of their own; a room's own transcript/synthesis output is what
            // gets cached, not any external mutation).
            StepType.Extract => true,
            StepType.VideoAnalyze => true,
            StepType.VideoCompile => true,
            StepType.Voiceover => true,
            StepType.EditRoom => true,
            StepType.ColorGradeRoom => true,
            // DailiesRoom: same read-only room machinery as EditRoom/ColorGradeRoom — its seats
            // and director carry no sandbox/render grant, so a cached TakeSelectionOutput skips
            // nothing observable outside the output itself.
            StepType.DailiesRoom => true,

            // ThumbnailGenerate is deterministic ffmpeg over an analysis artifact that is itself in
            // the key, and its only side effects are an object of its own and a row that names it —
            // the same shape as VideoCompile. Re-running an identical step must not decode the
            // source again, which is exactly what this row buys.
            StepType.ThumbnailGenerate => true,

            // ExtractFrame is deterministic ffmpeg over one video named by its config (the upstream
            // output is covered by the key's upstream digest / the StepResult id in the config); its only
            // side effects are an image object of its own and the project file that names it.
            StepType.ExtractFrame => true,

            // PinInsert (T-206) is pure C# over the analysis artifact and the references in its config: the
            // analysis and any Step content are upstream (in the key's upstream digest), StepResult ids are in
            // the config, project files in the project-file fingerprint. It writes nothing but its plan, and the
            // plan names project-scoped objects (a file id or an existing output), never a key of this run.
            StepType.PinInsert => true,

            // AudioCleanup is deterministic for a given source, target and resolved provider: the config (target,
            // source, provider id, consent) is in the key, the source's content is in the project-file fingerprint, the
            // upstream digest or the StepResult sources, and the resolved isolation row is in the provider fingerprint.
            // A hosted target that was refused (no consent, no plan, no provider) fails, and a failure is never cached,
            // so a refusal can never be replayed as a result for a run that would have been allowed (or the reverse).
            StepType.AudioCleanup => true,

            // VideoEnhance is deterministic for a given source, config and resolved provider: the config (op, engine,
            // parameters, consent) is in the key via VideoEnhanceConfigJson, the source's content via the project-file
            // fingerprint, the upstream digest or the StepResult sources, a hosted provider via its fingerprint. A cache hit is
            // free (no hold, no upload, no vendor call). A refused or failed run is never stored, so a consent-false hosted step
            // cannot poison the key (the consent is part of the config, so it has its own key anyway). Authors who want a
            // fresh hosted result set CacheMode=Never.
            StepType.VideoEnhance => true,

            // VideoGenerate spends real money per take. Replaying a prior successful result for
            // byte-identical inputs (prompt, model, resolution, duration, ratio, takes, provider are
            // all in the key via VideoGenerateConfigJson) is exactly the spend a re-run should
            // avoid; the clip lives in our own object store and the hit's OutputStorageKey is
            // HEAD-validated like VideoCompile's. Authors who want fresh takes set CacheMode=Never.
            StepType.VideoGenerate => true,

            // AudioGenerate (T-108) spends money per take too, but is deliberately NOT step-cached: an
            // identical take is reused one level down, from the spend ledger (same project, same RESOLVED
            // provider row, same request hash, a Succeeded row whose object still exists), which follows a
            // change of the default music/SFX provider — the step cache's provider fingerprint does not
            // cover those two capabilities. CacheMode=Always still overrides this row.
            StepType.AudioGenerate => false,

            // ImageGenerate spends money per picture, and is deliberately NOT step-cached either, for the same reason:
            // an identical picture is reused one level down, from the spend ledger (same project, same RESOLVED provider
            // row, same request hash, a Succeeded row whose object still exists). An Edit also reads a source picture the
            // step-cache key would have to fingerprint. CacheMode=Always still overrides this row.
            StepType.ImageGenerate => false,

            // TimelineRender is deterministic too, but deliberately NOT step-cached: its real input
            // is the edit_timelines row at the pinned revision, and the step-cache key sees only the
            // config JSON. With Revision = null ("latest at start") an identical config would replay
            // a render of an OLDER document after the user kept editing — the one thing a render
            // must never do. Its cache lives one level down instead: the region cache keyed on the
            // region recipe hash (document content + asset fingerprints + renderer version), which
            // is what makes an edit re-render exactly the regions it touched. CacheMode=Always still
            // overrides this, as it does every row of this table.
            StepType.TimelineRender => false,

            // DesignSystem is deterministic but deliberately NOT step-cached: its real cache is the
            // artifact keyed on the parse inputs (and the composed design system's members), not the
            // step output. A step-cache hit would replay an artifact this run never produced.
            StepType.DesignSystem => false,

            // GraphicsRoom is deliberately NOT in the list above even though it is structurally
            // the same shared-room machinery as EditRoom/ColorGradeRoom: its director/seats carry
            // the same full sandbox+Remotion+render tool grant as MotionGraphicsPlanner (minus
            // WriteProjectFile — see ToolGroupCatalog's rationale for AgentType.
            // MotionGraphicsDirector), and its standalone synthesis call may render a real overlay
            // asset into the sandbox/storage as a side effect of producing its output. Replaying a
            // cached MotionGraphicsPlanOutput would silently skip that render, leaving a plan that
            // references an asset key nothing ever produced this execution. Spelled out here
            // rather than falling through to the Agent-step tool-scope check below, since
            // GraphicsRoom is never a StepType.Agent step in the first place.
            StepType.GraphicsRoom => false,

            // Revise restores a workspace, edits it in a sandbox and renders + uploads a new film:
            // every side effect the Agent arm below excludes, and its whole point is a NEW version.
            // A cached Revise would replay a render key and a version link this run never produced.
            StepType.Revise => false,

            // DesignGraphic writes a sandbox, uploads a PNG and appends a workspace version; its point is
            // a NEW graphic. A cached one would replay a storage key and a version this run never made.
            StepType.DesignGraphic => false,

            // An Agent step is cacheable only if the agent's own granted tools carry no
            // state-mutating side effect OUTSIDE the output text/keys this cache actually stores.
            // ProjectWrite (WriteProjectFile), SandboxAuthoring (write/install/build inside the
            // sandbox workspace) and SandboxRender (render + upload a real media artifact) are all
            // side effects a downstream step or a human reviewer can observe independently of the
            // agent's returned Output — replaying a cached Output would silently skip whichever of
            // those actually happened, leaving referenced files/renders that were never produced
            // this run. This is exactly why AuthorAgent, RemotionComponentTranslator,
            // MotionGraphicsPlanner and MotionGraphicsDirector are excluded: every one of them is
            // granted at least one of those three groups (see ToolGroupCatalog.GroupsFor).
            //
            // On top of that tool-scope check, an Agent step the DECISION GATE may synthesize has a
            // second, independent reason not to be served from cache: see
            // HasNoActiveDecisionGate — the same shape of rule, in the same place, as the
            // ReviewLoop and GraphicsRoom exclusions around it.
            StepType.Agent => IsAgentStepCacheable(step),

            // Conditional/ForEach/Parallel: control-flow/fan-out step types whose "output" is a
            // structural artifact of the workflow graph itself (a branch choice, a merged array of
            // OTHER steps' outputs) rather than a value worth memoizing on its own.
            //
            // ReviewLoop especially: its entire reason to exist is to re-judge FRESH output
            // against deterministic facts computed by the step it is reviewing — serving a stale
            // verdict from a cache would defeat the review loop's purpose outright, not just miss
            // an optimization.
            _ => false
        };
    }

    /// <summary>
    /// D12: a completed result may be written to the step-result cache only when it did NOT come from
    /// the organization's own runner.
    ///
    /// <para>
    /// <b>Why a runner result must never be cached.</b> The runner is a machine the customer
    /// controls, running a binary they can modify (docs/runner-protocol.md "Threat model"). Its
    /// output is trusted only after <c>IOutputVerifier</c> has checked it against the platform-side
    /// job spec — a check performed once, for the run that produced it. A cache hit serves those
    /// bytes to a later run with <b>no</b> executor having verified them for that run, so the
    /// verification the whole OrgRunner path rests on would be bypassed by exactly the second
    /// execution it was supposed to protect. That is the same shape of rule as the decision-gate and
    /// ReviewLoop exclusions below: the step's output is not a value the cache can be trusted to
    /// replay.
    /// </para>
    ///
    /// <para>
    /// <b>Why it is narrow rather than blanket.</b> The exclusion keys on
    /// <see cref="StepExecutionResult.FromRunner"/> — i.e. on <see cref="ComputeTargetKind.OrgRunner"/>
    /// alone — and nothing else:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// A <see cref="ComputeTargetKind.CloudPool"/> result is one of OUR OWN workers. It is verified
    /// for the same reasons, but it is trustworthy hardware, so it stays admissible and
    /// <c>VideoGenerate</c>/<c>VideoCompile</c> keep the "an identical re-run does not pay twice"
    /// property their own entries in <see cref="IsCacheable"/> exist for.
    /// </item>
    /// <item>
    /// A <see cref="ComputeTargetKind.Local"/> result is this process's own work: unchanged.
    /// </item>
    /// <item>
    /// Nothing here touches the KEY. The exclusion is a decision about writing, so it cannot orphan a
    /// row or reorder a component — which is why D12 does not bump
    /// <c>StepCacheKeyInputs.SchemaVersion</c> (R10: A10 already took it to v4 for the
    /// resolved-provider fingerprint, and the two must land on one version).
    /// </item>
    /// </list>
    ///
    /// <para>
    /// A pure function of the result, so it is answered after the step runs — the caller asks
    /// <see cref="IsCacheable"/> before the step (to decide whether to consult the cache at all) and
    /// this afterwards (to decide whether this particular output may be stored).
    /// </para>
    /// </summary>
    public bool IsAdmissible(StepExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // A step that made a WebSearch call depends on the live web: never stored, whatever its CacheMode.
        return !result.FromRunner && result.WebSearchCalls == 0;
    }

    /// <summary>
    /// The Agent-step arm of <see cref="IsCacheable"/>: cacheable only when NONE of the agent
    /// type's granted <see cref="ToolGroup"/>s is <see cref="ToolGroup.ProjectWrite"/>,
    /// <see cref="ToolGroup.SandboxAuthoring"/>, <see cref="ToolGroup.SandboxRender"/>. Reads
    /// the built-in scoping table directly from <see cref="ToolGroupCatalog"/> — the same single
    /// source of truth <c>AgentToolProvider</c> uses at runtime — so this policy can never drift
    /// from what an agent is actually granted. A step that clears that check must additionally not
    /// be a configured decision-gate site (<see cref="HasNoActiveDecisionGate"/>).
    /// </summary>
    private bool IsAgentStepCacheable(WorkflowStep step)
    {
        AgentType agentType = step.AgentDefinition?.AgentType ?? AgentType.Custom;
        IReadOnlyList<ToolGroup> groups = ToolGroupCatalog.GroupsFor(agentType);

        return !groups.Contains(ToolGroup.ProjectWrite)
            && !groups.Contains(ToolGroup.SandboxAuthoring)
            && !groups.Contains(ToolGroup.SandboxBlocks)
            && !groups.Contains(ToolGroup.SandboxRender)
            // WebSearch is not excluded here: a step that actually searched is refused by IsAdmissible
            // (never stored, even with CacheMode=Always); a step that had the tool and did not search may be cached.
            // MomentLook (LookAtMoment) is not excluded either, on purpose, and a step that looked may be stored: a look
            // reads frames the analysis stored, which never change, and writes nothing. The analysis the frames belong
            // to is in the key (the resolved input and the upstream digest) and so is the default Vision provider (the
            // resolved-provider fingerprint), so a hit replays an answer to the same question on the same pictures.
            // Known residual: a Vision provider the analysis step names itself is in that step's key, not this one's.
            && HasNoActiveDecisionGate(agentType);
    }

    /// <summary>
    /// True when <paramref name="agentType"/> is not a decision-gate site at all, or its
    /// <c>Agents:&lt;name&gt;:DecisionGate:Mode</c> resolves to <see cref="DecisionGateMode.Off"/>
    /// — the default, and the only mode any shipped configuration writes.
    ///
    /// <para>
    /// A step whose output may be SYNTHESIZED BY A DECISION MODEL is not served from the step-result
    /// cache. That is one rule with four reasons, all of them about the same property — the cache
    /// key is built <b>before</b> the step runs and contains none of the gate's configuration:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>There is no second implementation to keep in sync.</b> Adding the gate's config as a cache
    /// KEY component would mean resolving it before the step executes, but <c>AgentStepExecutor</c>
    /// resolves it <b>per question</b>: the site thresholds depend on a question set derived from
    /// the agent type at execution time. Reproducing that in the key path would be two resolutions
    /// that must agree exactly, and any divergence recreates this very bug class. This method asks
    /// one question — "is this step's mode <see cref="DecisionGateMode.Off"/>?" — and has no
    /// per-question logic to drift. (<c>Mode</c> is agent-level by construction; phase 2 adds no
    /// per-site mode.)
    /// </item>
    /// <item>
    /// <b>The forgone caching is the cheap half.</b> The gate exists to avoid the <i>agent</i> call,
    /// and it does that on every run whether or not the step is cached. What a non-cacheable gated
    /// step gives up is caching the <i>decision</i> call — a few hundred tokens by construction.
    /// The expensive call is still avoided.
    /// </item>
    /// <item>
    /// <b>A cached gated step would replay one decision indefinitely</b>, quietly turning "System
    /// One decides" into "System One decided once".
    /// </item>
    /// <item>
    /// <b>It is the precedent this file already sets</b> — see <c>ReviewLoop</c> ("serving a stale
    /// verdict from a cache would defeat the review loop's purpose outright") and
    /// <c>GraphicsRoom</c> (a cached plan would reference an asset nothing produced) in
    /// <see cref="IsCacheable"/>.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// The name of the config block comes from <see cref="DecisionGateAgentConfig.ConfigNameFor"/>,
    /// the same single mapping <c>AgentStepExecutor</c> resolves the gate's own config through, so
    /// the two can never disagree about which block this rule is reading.
    /// </para>
    ///
    /// <para>
    /// Callers reach this only after the <see cref="StepCacheMode"/> override, so
    /// <c>CacheMode = Always</c> on a gate-configured step re-opens the hazard; see
    /// <see cref="IsCacheable"/>.
    /// </para>
    /// </summary>
    private bool HasNoActiveDecisionGate(AgentType agentType)
    {
        string? configName = DecisionGateAgentConfig.ConfigNameFor(agentType);
        if (configName is null)
            return true;

        // Through DecisionGateAgentConfig.ReadMode, the same helper AgentStepExecutor resolves the
        // gate's own mode with — never a private Configuration.GetValue<DecisionGateMode?>. That
        // call threw InvalidOperationException on a non-empty unparseable string (a typo'd key), and
        // while both of this method's call sites sit inside a `catch (Exception)` — so a typo here
        // degraded to "no cache" and a warning log rather than aborting the run, unlike the
        // executor's path — it was the same defect and it let the two readers drift on exactly the
        // inputs that mattered. An absent key, a blank value and a typo all read as Off now.
        DecisionGateMode mode = DecisionGateAgentConfig.ReadMode(_configuration, configName, _logger);

        return mode == DecisionGateMode.Off;
    }
}
