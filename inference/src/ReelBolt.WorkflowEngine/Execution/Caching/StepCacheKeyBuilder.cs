using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <inheritdoc cref="IStepCacheKeyBuilder"/>
public sealed class StepCacheKeyBuilder : IStepCacheKeyBuilder
{
    /// <summary>
    /// Hashes every hash-eligible field of <paramref name="inputs"/> (everything except
    /// <see cref="StepCacheKeyInputs.WorkflowDefinitionId"/> — see that record's doc comment) as
    /// a length-prefixed <c>name=value</c> block, one per line, then SHA-256s the UTF-8 bytes of
    /// the whole thing.
    ///
    /// <para>
    /// <b>Why length-prefixed, not just newline-separated.</b> A plain <c>"{name}={value}\n"</c>
    /// join is NOT collision-safe: two different (name, value) field sets can concatenate to the
    /// exact same byte string if a value happens to contain the separator characters (a step's
    /// system prompt or JSON config can contain <c>=</c> or <c>\n</c> freely). Prefixing both the
    /// name and the value with their own byte length makes the framing unambiguous regardless of
    /// what either contains — two different field sets can never concatenate to the same string,
    /// which is exactly what the two-field concatenation-collision test in
    /// <c>StepCacheKeyBuilderTests</c> exercises (e.g. <c>{A="ab", B="c"}</c> vs
    /// <c>{A="a", B="bc"}</c>).
    /// </para>
    ///
    /// <para><b>Null handling.</b> A null field hashes as the literal <c>"\0null"</c> — a value no
    /// real string can ever equal (it embeds a NUL byte), so null is always distinguishable from
    /// any actual empty or non-empty string value.</para>
    /// </summary>
    public string Build(StepCacheKeyInputs inputs)
    {
        StringBuilder sb = new();

        AppendField(sb, "SchemaVersion", StepCacheKeyInputs.SchemaVersion);
        AppendField(sb, "ProjectId", inputs.ProjectId.ToString());
        AppendField(sb, "StepType", inputs.StepType.ToString());
        // The revision may carry a config-conditional suffix (VideoAnalyze and VideoCompile only); each
        // step type's own config is what decides it.
        AppendField(sb, "AlgorithmRevision", DeterministicStepRevisions.For(
            inputs.StepType,
            inputs.StepType == StepType.VideoCompile ? inputs.VideoCompileConfigJson : inputs.VideoAnalyzeConfigJson,
            inputs.RunDynamics));
        AppendField(sb, "AgentType", inputs.AgentType.ToString());
        AppendField(sb, "AgentDefinitionId", inputs.AgentDefinitionId.ToString());
        AppendField(sb, "AgentSystemPrompt", inputs.AgentSystemPrompt);
        AppendField(sb, "AgentOutputSchemaName", inputs.AgentOutputSchemaName);
        AppendField(sb, "AgentInferenceProviderId", inputs.AgentInferenceProviderId?.ToString());
        AppendField(sb, "AgentAssignedSkillsJson", inputs.AgentAssignedSkillsJson);
        AppendField(sb, "AgentInputContextMode", inputs.AgentInputContextMode.ToString());
        AppendField(sb, "SelectedPriorStepOrdersJson", inputs.SelectedPriorStepOrdersJson);
        AppendField(sb, "InputMappingJson", inputs.InputMappingJson);
        AppendField(sb, "ExtractConfigJson", inputs.ExtractConfigJson);
        AppendField(sb, "VideoAnalyzeConfigJson", inputs.VideoAnalyzeConfigJson);
        AppendField(sb, "VideoCompileConfigJson", inputs.VideoCompileConfigJson);
        AppendField(sb, "VideoGenerateConfigJson", inputs.VideoGenerateConfigJson);
        AppendField(sb, "VoiceoverConfigJson", inputs.VoiceoverConfigJson);
        AppendField(sb, "TimelineRenderConfigJson", inputs.TimelineRenderConfigJson);
        AppendField(sb, "EditRoomConfigJson", inputs.EditRoomConfigJson);
        AppendField(sb, "GraphicsRoomConfigJson", inputs.GraphicsRoomConfigJson);
        AppendField(sb, "ColorGradeRoomConfigJson", inputs.ColorGradeRoomConfigJson);
        AppendField(sb, "ThumbnailGenerateConfigJson", inputs.ThumbnailGenerateConfigJson);
        AppendField(sb, "DailiesRoomConfigJson", inputs.DailiesRoomConfigJson);
        AppendField(sb, "ConditionExpression", inputs.ConditionExpression);
        AppendField(sb, "MaxIterations", inputs.MaxIterations.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, "MinScore", inputs.MinScore?.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, "ResolvedInput", inputs.ResolvedInput);
        AppendField(sb, "UserRequest", inputs.UserRequest);
        // Appended only when present, unlike every field above: a run with no style keeps exactly
        // the key it had before styles existed (no cache invalidation for unstyled runs), and the
        // length-prefixed framing keeps "field absent" distinct from any present value.
        if (inputs.RunStyle is not null)
            AppendField(sb, "RunStyle", inputs.RunStyle);
        // QA-DYN-61: the run's dynamics, set for a VideoCompile only and appended only when present.
        if (inputs.RunDynamics is not null)
            AppendField(sb, "RunDynamics", inputs.RunDynamics);
        AppendField(sb, "ProjectFileFingerprint", inputs.ProjectFileFingerprint);
        // Appended LAST so every pre-v4 component keeps its position and order: R10 ("do not
        // reorder existing components") — the SchemaVersion bump above is what invalidates the
        // older rows, not a reshuffle of the framing.
        AppendField(sb, "ResolvedProviderFingerprint", inputs.ResolvedProviderFingerprint);
        // v5, appended last for the same reason: what the step READS from earlier steps by step order
        // (a compile's decision and analysis artifact, a voiceover's narration plan …), which the
        // resolved input — only the previous step's output for a deterministic step — never covered.
        AppendField(sb, "UpstreamDigest", inputs.UpstreamDigest);
        // v6, appended last and only when present, exactly like RunStyle: the rendered design/product
        // blocks the role consumes. A run with no usable pin (or a role with no view) keeps the key it
        // had before the profile existed.
        if (inputs.DesignSystem is not null)
            AppendField(sb, "DesignSystem", inputs.DesignSystem);
        // v7, appended last and only when present: what each StepResult source resolved to (S-17).
        if (inputs.StepResultSources is not null)
            AppendField(sb, "StepResultSources", inputs.StepResultSources);
        // ExtractFrame's config, appended last and only when present like RunStyle/DesignSystem: every
        // other step type keeps the key it had before this step existed.
        if (inputs.ExtractFrameConfigJson is not null)
            AppendField(sb, "ExtractFrameConfigJson", inputs.ExtractFrameConfigJson);
        // AudioGenerate's config, the same way (T-108): only an author's CacheMode=Always reaches this key.
        if (inputs.AudioGenerateConfigJson is not null)
            AppendField(sb, "AudioGenerateConfigJson", inputs.AudioGenerateConfigJson);
        // ImageGenerate's config, the same way: only an author's CacheMode=Always reaches this key.
        if (inputs.ImageGenerateConfigJson is not null)
            AppendField(sb, "ImageGenerateConfigJson", inputs.ImageGenerateConfigJson);
        // PinInsert's config (T-206), the same way: keyed only when present.
        if (inputs.PinInsertConfigJson is not null)
            AppendField(sb, "PinInsertConfigJson", inputs.PinInsertConfigJson);
        // AudioCleanup's config (target, source, provider id, consent), the same way: keyed only when present.
        if (inputs.AudioCleanupConfigJson is not null)
            AppendField(sb, "AudioCleanupConfigJson", inputs.AudioCleanupConfigJson);
        // VideoEnhance's config (op, engine, parameters, consent), the same way: keyed only when present.
        if (inputs.VideoEnhanceConfigJson is not null)
            AppendField(sb, "VideoEnhanceConfigJson", inputs.VideoEnhanceConfigJson);
        // The selectable voice a Voiceover step defaults to when it names none, the same way: keyed only when present.
        if (inputs.VoiceoverDefaultVoice is not null)
            AppendField(sb, "VoiceoverDefaultVoice", inputs.VoiceoverDefaultVoice);
        // The built-in sound library's content, the same way: keyed only when the workflow offers it.
        if (inputs.SfxLibraryDigest is not null)
            AppendField(sb, "SfxLibraryDigest", inputs.SfxLibraryDigest);
        // The run brief's fields this step reads, the same way: keyed only when present.
        if (inputs.RunBrief is not null)
            AppendField(sb, "RunBrief", inputs.RunBrief);
        // The project pronunciation glossary a Voiceover step speaks with, the same way: keyed only when present.
        if (inputs.ProjectPronunciation is not null)
            AppendField(sb, "ProjectPronunciation", inputs.ProjectPronunciation);
        // The project's quarantined files, the same way: keyed only when the project has any.
        if (inputs.QuarantineState is not null)
            AppendField(sb, "QuarantineState", inputs.QuarantineState);

        // The avatar a VideoGenerate step shows (consent time + reference digests), the same way: keyed only when set.
        if (inputs.AvatarConsent is not null)
            AppendField(sb, "AvatarConsent", inputs.AvatarConsent);

        // The app-input values the step consumed (docs/design/app-inputs.md 10.1), the same way: keyed only when set.
        if (inputs.AppInputs is not null)
            AppendField(sb, "AppInputs", inputs.AppInputs);

        // Camera-profile versions, the same way: keyed only when the step names a source profile.
        if (inputs.InputProfileRecipes is not null)
            AppendField(sb, "InputProfileRecipes", inputs.InputProfileRecipes);

        // The project subjects a shot planner is offered (docs/design/avatar-objects.md), the same way: keyed only when set.
        if (inputs.ProjectSubjects is not null)
            AppendField(sb, "ProjectSubjects", inputs.ProjectSubjects);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder sb, string name, string? value)
    {
        string v = value ?? "\0null";
        sb.Append(name.Length).Append(':').Append(name)
          .Append('=')
          .Append(v.Length).Append(':').Append(v)
          .Append('\n');
    }
}
