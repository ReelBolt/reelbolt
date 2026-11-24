using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <summary>
/// Everything that affects a cacheable step's output, gathered into one immutable value so
/// <see cref="IStepCacheKeyBuilder.Build"/> can hash it deterministically. Two executions that
/// produce identical <see cref="StepCacheKeyInputs"/> are, by construction, expected to produce
/// identical step output — that is the entire correctness argument for serving one from the
/// other.
///
/// <para>
/// Every property here EXCEPT <see cref="WorkflowDefinitionId"/> feeds
/// <see cref="IStepCacheKeyBuilder.Build"/>'s hash — see that method's implementation for the
/// exact field list and framing. <see cref="WorkflowDefinitionId"/> is carried only so
/// <see cref="IStepResultCache.StoreAsync"/> can populate
/// <see cref="WorkflowStepCacheEntry.WorkflowDefinitionId"/> (metadata/observability only); it is
/// deliberately NOT hashed, for the same reason <see cref="WorkflowStepCacheEntry"/> carries no
/// foreign key to <c>workflow_definitions</c> — see that entity's doc comment.
/// </para>
/// </summary>
public sealed record StepCacheKeyInputs
{
    /// <summary>
    /// Bumped whenever the hashed field set or its framing changes, so a pre-existing cache entry
    /// from an older engine version can never be mistaken for a hit against the new key shape —
    /// it simply becomes an orphaned row that ages out via <see cref="StepCacheOptions.TtlHours"/>
    /// instead of being (incorrectly) matched or (expensively) migrated.
    /// <para>
    /// <b>v2</b>: <see cref="VoiceoverConfigJson"/> joined the hashed field set. Without it, two
    /// distinct <see cref="StepType.Voiceover"/> steps in one project hashed identically — the
    /// second would replay the first's lines, and its per-line object keys, as if they were its own.
    /// </para>
    /// <para>
    /// <b>v3</b>: <see cref="DeterministicStepRevisions"/> joined the hashed field set, so a fix to
    /// a deterministic executor stops an identical re-run from replaying the pre-fix output.
    /// </para>
    /// <para>
    /// <b>v4</b>: <see cref="ResolvedProviderFingerprint"/> joined the hashed field set (A10/R10), so
    /// switching the inference provider a step resolves to stops an identical re-run from replaying
    /// output the previous provider produced. The step's own config and its agent's configured
    /// provider id were already hashed — what was missing was the RESOLVED provider, which changes
    /// without any workflow edit the moment an operator repoints an organization's default row.
    /// R10: D12 changes step-cache ADMISSIBILITY (excluding <c>FromRunner</c> results) and
    /// deliberately does NOT bump this again, so the two land on one version.
    /// </para>
    /// <para>
    /// <b>v5</b>: <see cref="UpstreamDigest"/> joined the hashed field set. A deterministic step's
    /// <see cref="ResolvedInput"/> is the previous step's output only, but most of them read OTHER steps
    /// by step order — a compile reads its decision and its analysis artifact, a voiceover its narration
    /// plan. A re-run whose previous step was unchanged while an earlier one changed (a composition
    /// revision re-renders the film, the analysis changes, the kept edit decision does not) hashed
    /// identically and was served the OLD compile. The digest covers every step the dependency analysis
    /// (<c>WorkflowStepDependencyGraph</c>) says this step reads: output text and storage keys.
    /// </para>
    /// <para>
    /// <b>v6</b>: <see cref="DesignSystem"/> joined the hashed field set. A step's ambient design and
    /// product profile is rendered into its system prompt but appears in none of the fields above, so
    /// two runs whose pinned profile differed hashed identically and a role could be served output
    /// produced for a different brand. Only the roles whose view actually consumes a block carry the
    /// component (see <c>DesignSystemRunState.CacheKeyComponent</c>), and a run with no usable pin
    /// appends nothing at all, keeping its key shape unchanged.
    /// </para>
    /// <para>
    /// <b>v7</b>: <see cref="StepResultSources"/> joined the hashed field set (T-023, S-17). A VideoAnalyze or
    /// VideoCompile step naming a <c>StepResult</c> source hashed only the configured id, so a source pruned,
    /// deleted or re-pointed since the entry was stored still hit. The component is what each id RESOLVED to.
    /// S-64 later added a compile's <c>StepResult</c> insert references and ExtractFrame's source to the same
    /// component with no bump: it is appended only when present, so a config naming one of them now hashes a
    /// field (or a longer value) it never hashed before and misses its old rows, and every other key is unchanged.
    /// <para>
    /// <b>v8</b>: <see cref="InferenceProviderCapability.VideoSegmentation"/> joined
    /// <see cref="ResolvedProviderFingerprint"/>'s capability set (prompted-region-effects contracts).
    /// The fingerprint's framed FIELD SET is unchanged — what changed is that one more
    /// <c>{capability}:{providerId}:{model}</c> line is inside the hashed value, so an existing v6
    /// fingerprint would silently be compared against a value that can never equal it. Bumping here
    /// is the same discipline v4 applied when the fingerprint itself joined: a changed meaning of a
    /// hashed field is a framing change, and a pre-existing row must become an orphan rather than a
    /// wrong hit.
    /// </para>
    /// <para>
    /// <b>v9</b>: the segmentation mask content key is now segmented by the user's
    /// <c>ObjectMatchScope</c> (prompted-region-effects security remediation). No field in this record
    /// changed shape, but a pre-fix <see cref="VideoAnalyzeConfigJson"/> key can now resolve to a
    /// different output — the old mask cache could serve <c>AllInstances</c> tracks to a
    /// <c>SingleTrack</c> plan under the same config — so the row's meaning moved and it must become an
    /// orphan rather than a stale hit.
    /// </para>
    /// <para>
    /// <b>v10</b>: the resolved-provider fingerprint (<c>ResolvedProviderFingerprint</c>) gained
    /// <c>InferenceProviderCapability.AudioIsolation</c> (audio-cleanup initiative). A new line in the hashed
    /// fingerprint changes every step's key, so a pre-v10 row must become an orphan rather than a hit whose
    /// fingerprint can no longer be reproduced. An unresolved capability is <c>-</c>, as before.
    /// </para>
    /// <para>
    /// <b>v11</b>: the resolved-provider fingerprint (<c>ResolvedProviderFingerprint</c>) gained
    /// <c>InferenceProviderCapability.VideoUpscale</c> and <c>FrameInterpolation</c> (video-enhance initiative). Two
    /// new lines in the hashed fingerprint change every step's key, so a pre-v11 row must become an orphan rather than
    /// a hit whose fingerprint can no longer be reproduced. An unresolved capability is <c>-</c>, as before.
    /// (v10 is the audio-cleanup initiative's.)
    /// </para>
    /// <para>
    /// <b>v12</b>: the resolved-provider fingerprint gained <c>InferenceProviderCapability.VideoOutpaint</c> (the
    /// VideoEnhance <c>Extend</c> op). One more line in the hashed fingerprint changes every step's key, so a pre-v12 row
    /// must become an orphan. An unresolved capability is <c>-</c>, as before.
    /// </para>
    /// <para>
    /// <b>v13</b>: the resolved-provider fingerprint gained <c>InferenceProviderCapability.LipSync</c> (the talking-head
    /// lip-sync capability). One more line in the hashed fingerprint changes every step's key, so a pre-v13 row must
    /// become an orphan. An unresolved capability is <c>-</c>, as before.
    /// </para>
    /// <para>
    /// (The sibling branch's design-system work took <c>v6</c> before this arrived; the numbers are
    /// literal, not ordered by landing time, so the next change takes <c>v14</c>.)
    /// </para>
    /// </summary>
    public const string SchemaVersion = "v13";

    public required Guid ProjectId { get; init; }
    public required StepType StepType { get; init; }
    public required AgentType AgentType { get; init; }
    public required Guid AgentDefinitionId { get; init; }
    public string? AgentSystemPrompt { get; init; }
    public string? AgentOutputSchemaName { get; init; }
    public Guid? AgentInferenceProviderId { get; init; }
    public string? AgentAssignedSkillsJson { get; init; }
    public required AgentInputContextMode AgentInputContextMode { get; init; }
    public string? SelectedPriorStepOrdersJson { get; init; }
    public string? InputMappingJson { get; init; }
    public string? ExtractConfigJson { get; init; }
    public string? VideoAnalyzeConfigJson { get; init; }
    public string? VideoCompileConfigJson { get; init; }
    public string? VideoGenerateConfigJson { get; init; }
    public string? VoiceoverConfigJson { get; init; }

    /// <summary>
    /// A Voiceover step's project-default-voice component (<c>ProjectVoiceDefault.CacheKeyComponent</c>): the
    /// selectable voice (or candidate set) the step speaks in when it names none. Appended only when present, so no
    /// key of a step that names a voice, or of a project without a selectable voice, changes and
    /// <see cref="SchemaVersion"/> stays put.
    /// </summary>
    public string? VoiceoverDefaultVoice { get; init; }

    /// <summary>
    /// Digest of the built-in sound library (<c>SfxLibraryDigest</c>), set only for a VideoAnalyze or VideoCompile step of a
    /// workflow that offers the library, so a new, changed or taken-down sound misses the cache. Null (not hashed) otherwise.
    /// </summary>
    public string? SfxLibraryDigest { get; init; }

    /// <summary>
    /// The avatar a <c>VideoGenerate</c> step shows, as it is now (<c>Services.Avatars.AvatarFingerprint</c>: id,
    /// consent attestation time, reference digests in order), or <c>unusable:{reason}</c>; null (not hashed) when the
    /// step names no avatar. Appended only when present, so every step without an avatar keeps its key and no
    /// <see cref="SchemaVersion"/> bump is needed: no hashed field changes meaning, a new field appears only when set.
    /// </summary>
    public string? AvatarConsent { get; init; }

    /// <summary>
    /// The app-input values this step consumed (docs/design/app-inputs.md 10.1): <c>AppInputBinder.CacheComponent</c>,
    /// sorted, length-prefixed <c>id=value</c> pairs of what was bound into its config or read by its condition (an agent's
    /// share is the composed request, already hashed as <see cref="UserRequest"/>). Null (not hashed) when the run carries
    /// no input or the step consumed none, so every existing key is byte-identical and no <see cref="SchemaVersion"/> bump
    /// is needed: no hashed field changes meaning, a new field appears only when set.
    /// </summary>
    public string? AppInputs { get; init; }

    /// <summary>
    /// The <c>[PROJECT SUBJECTS]</c> block a shot-planner Agent step is given (docs/design/avatar-objects.md "Avatars and
    /// objects in planned shots": the project's usable people and objects by offer id, kind, name and description).
    /// Null (not hashed) for every other step and for a run that offers no subject, so every existing key is
    /// byte-identical and no <see cref="SchemaVersion"/> bump is needed: no hashed field changes meaning, a new field
    /// appears only when set.
    /// </summary>
    public string? ProjectSubjects { get; init; }
    public string? TimelineRenderConfigJson { get; init; }
    public string? EditRoomConfigJson { get; init; }
    public string? GraphicsRoomConfigJson { get; init; }
    public string? ColorGradeRoomConfigJson { get; init; }
    public string? ThumbnailGenerateConfigJson { get; init; }

    /// <summary>ExtractFrame's config; keyed only when present, so no other step's key changes.</summary>
    public string? ExtractFrameConfigJson { get; init; }

    /// <summary>AudioGenerate's config (T-108); keyed only when present, so no other step's key changes.</summary>
    public string? AudioGenerateConfigJson { get; init; }

    /// <summary>ImageGenerate's config; keyed only when present (an author's CacheMode=Always), so no other step's key changes.</summary>
    public string? ImageGenerateConfigJson { get; init; }

    /// <summary>PinInsert's config (T-206); keyed only when present, so no other step's key changes.</summary>
    public string? PinInsertConfigJson { get; init; }

    /// <summary>
    /// AudioCleanup's config (target, source, provider, consent); keyed only when present, so no other step's key changes.
    /// The source's content is covered by <see cref="ProjectFileFingerprint"/> (a project file), <see cref="UpstreamDigest"/>
    /// (a step output) and <see cref="StepResultSources"/> (a step result); the hosted provider by <see cref="ResolvedProviderFingerprint"/>.
    /// </summary>
    public string? AudioCleanupConfigJson { get; init; }

    /// <summary>
    /// VideoEnhance's config (op, engine, source, provider, consent, parameters); keyed only when present, so no other
    /// step's key changes. The source's content is covered by <see cref="ProjectFileFingerprint"/>, <see cref="UpstreamDigest"/>
    /// or <see cref="StepResultSources"/>; a hosted provider by <see cref="ResolvedProviderFingerprint"/>.
    /// </summary>
    public string? VideoEnhanceConfigJson { get; init; }
    public string? DailiesRoomConfigJson { get; init; }
    public string? ConditionExpression { get; init; }
    public required int MaxIterations { get; init; }
    public int? MinScore { get; init; }

    /// <summary>The fully-built step input string — <c>StepExecutionContext.LastResolvedAgentInput</c> (Agent steps) or the equivalent resolved-input descriptor a deterministic executor recorded.</summary>
    public string? ResolvedInput { get; init; }

    /// <summary>
    /// Camera-profile recipe/table/detector versions for a step whose config sets <c>SourceInputProfiles</c>
    /// (<c>SourceCameraProfile.CacheKeyComponent</c>). Null (and NOT hashed) otherwise, so every other key is unchanged.
    /// </summary>
    public string? InputProfileRecipes { get; init; }

    public string? UserRequest { get; init; }

    /// <summary>
    /// The run's editing style, output-format override and target format, as one canonical string
    /// (<c>RunStyleOverrides.CacheKeyComponent</c>), so a styled run never reuses an unstyled
    /// result. Null for a run with none of them — and a null value is NOT hashed at all, so every
    /// key of such a run is byte-identical to one computed before styles existed.
    /// </summary>
    public string? RunStyle { get; init; }

    /// <summary>
    /// QA-DYN-61: the run's effective dynamics (<c>RunStyleOverrides.DynamicsCacheKeyComponent</c>), set for a VideoCompile step ONLY
    /// (null for every other step type) and NOT hashed when null, so a pace-only change re-runs only the compile.
    /// </summary>
    public string? RunDynamics { get; init; }

    /// <summary>
    /// A hash of the project's file inventory (see <see cref="IProjectFileFingerprintProvider"/>).
    /// Required even for a step whose config doesn't obviously reference project files: any
    /// agent granted <see cref="Shared.Agents.ToolGroup.ProjectRead"/> can read file content that
    /// never appears in its prompt, so the file inventory itself must be part of the key or a
    /// cache hit could replay an answer about files that have since changed.
    /// </summary>
    public required string ProjectFileFingerprint { get; init; }

    /// <summary>
    /// What this step's inference providers RESOLVED to — see
    /// <see cref="ResolvedProviderFingerprint.ComputeAsync"/>, which builds it. Hash-eligible like
    /// every other non-metadata field, and hashed unconditionally: a null value (no resolver wired,
    /// which is what a minimally-constructed unit test gets) is a stable value of its own, and the
    /// <see cref="SchemaVersion"/> bump above already orphaned every pre-v4 row, so there is nothing
    /// to preserve by omitting it the way <see cref="RunStyle"/> is omitted.
    /// </summary>
    public string? ResolvedProviderFingerprint { get; init; }

    /// <summary>
    /// A SHA-256 over every earlier step this step reads (<see cref="StepUpstreamDigest"/>): each one's
    /// output and its output/artifact storage keys, by step order. Null when the caller computed none
    /// (a minimally-constructed test); a stable value of its own, like every other nullable field.
    /// </summary>
    public string? UpstreamDigest { get; init; }

    /// <summary>
    /// The digest of the RENDERED ambient design and product blocks this step's role consumes
    /// (<c>DesignSystemRunState.CacheKeyComponent</c>, P2-18): <c>d={sha256(design)[0..16]}|p={sha256(product)[0..16]}</c>,
    /// with <c>-</c> for a missing block. Null when the run has no usable pin, the role sees no view,
    /// or there is no agent type — and a null value is NOT hashed at all, mirroring
    /// <see cref="RunStyle"/>, so such a run keeps exactly the key it had before the profile existed.
    /// </summary>
    public string? DesignSystem { get; init; }

    /// <summary>
    /// v7 (T-023): what every <c>StepResult</c> source of the step's video config resolved to, as
    /// <c>{stepResultId}={storageKey | "\0missing"}</c> entries joined by <c>|</c>, in config order. Filled by
    /// <see cref="StepResultCache"/> itself on both lookup and store (it owns the project-scoped read), so every
    /// caller gets it. Null when the step names no such source — and a null value is NOT hashed, mirroring
    /// <see cref="RunStyle"/>.
    /// </summary>
    public string? StepResultSources { get; init; }

    /// <summary>
    /// The run brief's contribution (<c>RunBriefState.CacheKeyComponent</c>): a canonical string of ONLY the
    /// brief fields this step's role or type reads. Null when it reads none that are set — and a null value
    /// is NOT hashed, mirroring <see cref="RunStyle"/>, so a run with no brief keeps exactly the key it had
    /// before briefs existed (no <see cref="SchemaVersion"/> bump). An analyzer is reused across languages.
    /// </summary>
    public string? RunBrief { get; init; }

    /// <summary>
    /// Digest of the project pronunciation glossary a Voiceover step speaks with
    /// (<c>PronunciationRespeller.Digest</c>). Null when the project has no glossary — and a null value is NOT
    /// hashed, so every key without a glossary is byte-identical to before it existed (no
    /// <see cref="SchemaVersion"/> bump).
    /// </summary>
    public string? ProjectPronunciation { get; init; }

    /// <summary>
    /// Digest of the project's quarantined files, read fresh by <see cref="StepResultCache"/> on every lookup and
    /// store, never by the caller. Null when the project has none, and a null value is NOT hashed, so a project
    /// that never had a quarantine keeps its keys byte-identical (no <see cref="SchemaVersion"/> bump). A new
    /// quarantine moves every key of the project, whatever an entry's own fingerprint recorded.
    /// </summary>
    public string? QuarantineState { get; init; }

    /// <summary>See the class doc comment — metadata only, not part of the hash.</summary>
    public Guid WorkflowDefinitionId { get; init; }

    /// <summary>
    /// NOT part of the hash (like <see cref="WorkflowDefinitionId"/>): the tallest short side the run
    /// asking may produce (<c>RunStyleOverrides.OutputCap</c>), set for a <c>VideoCompile</c> step of a
    /// run with a plan, null otherwise. <see cref="StepResultCache.TryGetAsync"/> treats a cached
    /// compile whose output is taller than this as a miss, so a render made under a higher cap (before
    /// a downgrade) is never served to a run that may not have it. Null means "not checked".
    /// </summary>
    public int? MaxOutputShortSide { get; init; }
}
