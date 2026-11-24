using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Body of <c>POST /api/v1/assistant/tool-call</c> — one tool invocation over the bridge.
/// </summary>
/// <remarks>
/// <para>
/// <b>This body carries no authority.</b> There is no user id, no role, no admin bit and no
/// permission field, and there must never be one: the caller is the verified JWT <c>sub</c> the
/// transport resolved, read from <c>ICurrentUser</c>, and every gated tool derives its own authority
/// inside its own body on the call that is executing. A property added here that could carry
/// authority would be a privilege-escalation hole in the bridge.
/// </para>
/// <para>
/// <see cref="Context"/> is the caller's <em>page</em> context, not a grant. Every tool that reads
/// it re-resolves and re-ownership-checks the id it names (see
/// <c>AssistantToolContext</c>).
/// </para>
/// </remarks>
public sealed class AssistantToolCallRequest
{
    /// <summary>The tool name, exactly as the catalog reported it.</summary>
    [Required(AllowEmptyStrings = false)]
    public string? Name { get; set; }

    /// <summary>
    /// The tool's own arguments, verbatim, keyed by the tool's parameter names. Unknown keys are
    /// ignored by the tool binder; a missing required parameter is a tool execution failure
    /// (<c>isError: true</c>).
    /// </summary>
    public Dictionary<string, JsonElement>? Arguments { get; set; }

    /// <summary>Optional page context. All three ids may be omitted.</summary>
    public AssistantToolCallPageContext? Context { get; set; }
}

/// <summary>The caller's page context for one bridged tool call.</summary>
/// <remarks>
/// Deliberately an object under one property rather than three top-level ones: real tools already
/// declare a <c>workflowId</c> parameter (<c>ProposeWorkflow</c>, <c>ApplyWorkflow</c>,
/// <c>ExecuteWorkflow</c>), so a top-level <c>workflowId</c> would be ambiguous.
/// </remarks>
public sealed class AssistantToolCallPageContext
{
    /// <summary>The project the caller has open, or null.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>The workflow the caller has open, or null.</summary>
    public Guid? WorkflowId { get; set; }

    /// <summary>The picture document open in the picture editor, or null.</summary>
    public Guid? ImageDocumentId { get; set; }
}

/// <summary>
/// Response of <c>GET /api/v1/assistant/tool-catalog</c>: the assistant's own registered tool set,
/// described by the descriptors themselves.
/// </summary>
public sealed record AssistantToolCatalogResponse(IReadOnlyList<AssistantToolDescriptorDto> Tools);

/// <summary>
/// One catalog entry: the <c>AIFunction</c> descriptor (<c>Name</c>, <c>Description</c>,
/// <c>JsonSchema</c>) plus the directory-facing annotations every MCP tool must carry.
/// </summary>
/// <param name="Title">The human title, also repeated inside <paramref name="Annotations"/>.</param>
/// <param name="Annotations">The MCP <c>annotations</c> object (title + four hints).</param>
public sealed record AssistantToolDescriptorDto(
    string Name,
    string Description,
    JsonElement InputSchema,
    string Title,
    AssistantToolAnnotationsDto Annotations);

/// <summary>
/// The MCP <c>annotations</c> object. Present on every catalog entry; a tool that is neither
/// read-only nor destructive is a classification error the catalog test rejects.
/// </summary>
public sealed record AssistantToolAnnotationsDto(
    string Title,
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool IdempotentHint,
    bool OpenWorldHint);

/// <summary>
/// Response of <c>POST /api/v1/assistant/tool-call</c>, mirroring the MCP <c>tools/call</c> result so
/// the proxy has nothing to translate.
/// </summary>
/// <remarks>
/// <see cref="IsError"/> is <c>false</c> for every tool-level outcome the model is meant to read —
/// an admin-gate refusal, a plan-gate refusal, a rate-limit refusal and an unknown workflow all
/// arrive as the tool's own JSON text. It is <c>true</c> only for a genuine execution failure (the
/// function could not be invoked, or it threw).
/// </remarks>
public sealed record AssistantToolCallResultDto(IReadOnlyList<AssistantToolContentDto> Content, bool IsError)
{
    /// <summary>The single-text-block result shape both halves of the contract use.</summary>
    public static AssistantToolCallResultDto TextResult(string text, bool isError = false) =>
        new([new AssistantToolContentDto("text", text)], isError);
}

/// <summary>One content block of a tool result. Only <c>text</c> is produced today.</summary>
public sealed record AssistantToolContentDto(string Type, string Text);
