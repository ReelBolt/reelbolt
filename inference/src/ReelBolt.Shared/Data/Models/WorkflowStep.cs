namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A single step in a workflow definition, linked to an agent.
/// Enhanced with step type discriminator and conditional/loop support.
/// </summary>
public class WorkflowStep
{
    public Guid Id { get; set; }
    public Guid WorkflowDefinitionId { get; set; }
    public Guid AgentDefinitionId { get; set; }
    public int StepOrder { get; set; }
    public string? EdgeConditionJson { get; set; }
    public string? Label { get; set; }

    // Enhanced workflow support
    public StepType StepType { get; set; } = StepType.Agent;
    public string? ConditionExpression { get; set; }
    public string? LoopSourceExpression { get; set; }
    public int? LoopTargetStepOrder { get; set; }
    public int MaxIterations { get; set; } = 3;
    public int? MinScore { get; set; }
    public string? InputMappingJson { get; set; }
    public AgentInputContextMode? AgentInputContextMode { get; set; }
    public string? SelectedPriorStepOrdersJson { get; set; }
    public string? TrueBranchStepOrder { get; set; }
    public string? FalseBranchStepOrder { get; set; }

    /// <summary>
    /// JSON array of agent definition GUIDs to execute in parallel (StepType.Parallel only).
    /// All agents run concurrently; the primary AgentDefinitionId is included automatically.
    /// Output format: [{"agentName":"...","output":"..."},...]
    /// </summary>
    public string? ParallelAgentIdsJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.Extract. Deserialised to ExtractStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? ExtractConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.VideoAnalyze. Deserialised to VideoAnalyzeStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? VideoAnalyzeConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.VideoCompile. Deserialised to VideoCompileStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? VideoCompileConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.VideoGenerate. Deserialised to VideoGenerateStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? VideoGenerateConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.EditRoom. Deserialised to EditRoomStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? EditRoomConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.GraphicsRoom. Deserialised to GraphicsRoomStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? GraphicsRoomConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.ColorGradeRoom. Deserialised to ColorGradeRoomStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? ColorGradeRoomConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.Conditional. Deserialised to ConditionalStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? ConditionalConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.ReviewLoop. Deserialised to ReviewLoopStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? ReviewLoopConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.Voiceover. Deserialised to VoiceoverStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? VoiceoverConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.ThumbnailGenerate. Deserialised to ThumbnailGenerateStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? ThumbnailGenerateConfigJson { get; set; }
    /// JSON configuration for StepType.TimelineRender. Deserialised to TimelineRenderStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? TimelineRenderConfigJson { get; set; }

    /// <summary>
    /// JSON-serialized <c>ReelBolt.Shared.Workflows.DailiesRoomStepConfig</c> (jsonb column
    /// <c>dailies_room_config_json</c>). Only meaningful when <see cref="StepType"/> is
    /// <see cref="StepType.DailiesRoom"/>; null otherwise. Same opaque-passthrough contract as the
    /// other three room columns.
    /// </summary>
    public string? DailiesRoomConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.DesignSystem. Deserialised to DesignSystemStepConfig.
    /// Null for every other step type.
    /// </summary>
    public string? DesignSystemConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.ExtractFrame (jsonb column <c>extract_frame_config_json</c>).
    /// Deserialised to ExtractFrameStepConfig. Null for every other step type.
    /// </summary>
    public string? ExtractFrameConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.AudioGenerate (jsonb column <c>audio_generate_config_json</c>).
    /// Deserialised to AudioGenerateStepConfig. Null for every other step type.
    /// </summary>
    public string? AudioGenerateConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.PinInsert (jsonb column <c>pin_insert_config_json</c>).
    /// Deserialised to PinInsertStepConfig. Null for every other step type.
    /// </summary>
    public string? PinInsertConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.AudioCleanup (jsonb column <c>audio_cleanup_config_json</c>).
    /// Deserialised to AudioCleanupStepConfig. Null for every other step type.
    /// </summary>
    public string? AudioCleanupConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.VideoEnhance (jsonb column <c>video_enhance_config_json</c>).
    /// Deserialised to VideoEnhanceStepConfig. Null for every other step type.
    /// </summary>
    public string? VideoEnhanceConfigJson { get; set; }

    /// <summary>
    /// JSON configuration for StepType.ImageGenerate (jsonb column <c>image_generate_config_json</c>).
    /// Deserialised to ImageGenerateStepConfig. Null for every other step type.
    /// </summary>
    public string? ImageGenerateConfigJson { get; set; }

    /// <summary>
    /// Step option: continue from an earlier run's composition. JSON-serialized
    /// <c>ReelBolt.Shared.Workflows.ContinueCompositionConfig</c> (jsonb column
    /// <c>continue_composition_json</c>, <c>{"executionId"}</c> or <c>{"versionId"}</c>). Only an Agent
    /// step whose agent holds SandboxAuthoring may carry it; same-project sources only. Null = off.
    /// </summary>
    public string? ContinueCompositionJson { get; set; }

    /// <summary>
    /// Step option: register the step's delivered output (every delivered clip for VideoGenerate) as an
    /// <c>outputFiles</c> project file, in place (no copy). Only on steps that produce playable output.
    /// </summary>
    public bool RegisterOutputAsProjectFile { get; set; }

    /// <summary>
    /// Per-step override for cross-execution step-result caching (see
    /// ReelBolt.WorkflowEngine.Execution.Caching.IStepCachePolicy). Null means
    /// <see cref="StepCacheMode.Default"/> — the built-in StepType/AgentType-driven decision.
    /// </summary>
    public StepCacheMode? CacheMode { get; set; }

    public WorkflowDefinition WorkflowDefinition { get; set; } = null!;
    public AgentDefinition AgentDefinition { get; set; } = null!;
    public ICollection<WorkflowStepResult> Results { get; set; } = new List<WorkflowStepResult>();
}
