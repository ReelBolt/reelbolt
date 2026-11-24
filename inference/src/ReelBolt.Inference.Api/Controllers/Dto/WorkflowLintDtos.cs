using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Body of <c>POST .../workflows/{id}/lint</c> and <c>POST .../workflows/lint</c>. <paramref name="Steps"/> lints an
/// unsaved draft (on the id route it replaces the saved steps for this call; on the id-less route it is required).
/// </summary>
public record LintWorkflowRequest(
    RunBrief? Brief = null,
    List<CreateWorkflowStepRequest>? Steps = null,
    ReelBolt.Shared.Workflows.Apps.AppManifest? AppManifest = null);

/// <summary>
/// The fix a finding offers: <c>kind</c> is camelCase (<c>retargetReference</c>), <c>patchId</c> a closed assistant
/// patch when one exists. <c>label</c> is today's English; <c>labelId</c> + <c>args</c> let the web translate the
/// button (<c>lint.fix.&lt;labelId&gt;</c> in the catalogue) and are null/empty when the fix was not built by a rule.
/// </summary>
public record LintFixResponse(
    string Kind,
    string Label,
    string? LabelId,
    IReadOnlyDictionary<string, string> Args,
    string? PatchId);

/// <summary>
/// One finding on the wire: <c>severity</c> is <c>error</c>, <c>warning</c> or <c>info</c>; <c>message</c> stays the
/// English sentence, <c>messageKey</c> names the catalogue sentence (<c>lint.&lt;messageKey&gt;</c>) and <c>args</c>
/// carries its tokens.
/// </summary>
public record LintFindingResponse(
    string RuleId,
    string Severity,
    int? StepOrder,
    string Message,
    string MessageKey,
    IReadOnlyDictionary<string, string> Args,
    LintFixResponse? Fix)
{
    /// <summary>The wire form of a finding.</summary>
    public static LintFindingResponse From(LintFinding finding) => new(
        finding.RuleId,
        finding.Severity.ToString().ToLowerInvariant(),
        finding.StepOrder,
        finding.Message,
        finding.MessageKey,
        finding.Args,
        finding.Fix is { } fix
            ? new LintFixResponse(
                CamelCase(fix.Kind.ToString()),
                fix.Label,
                fix.LabelId,
                fix.Args ?? LintMessage.NoArgs,
                fix.PatchId)
            : null);

    /// <summary>
    /// The form of a finding that goes to the assistant or an MCP client: the app's "allow the footage to be sent" fix is
    /// dropped, because that permission is the customer's to give in the app and the assistant must not be offered it.
    /// </summary>
    public static LintFindingResponse ForAssistant(LintFinding finding) =>
        finding.Fix?.Kind == LintFixKind.AllowEgress ? From(finding with { Fix = null }) : From(finding);

    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>The answer of the lint endpoints: the findings, most serious first, and their counts.</summary>
public record WorkflowLintResponse(
    IReadOnlyList<LintFindingResponse> Findings, int Errors, int Warnings, int Infos, IReadOnlyList<ReelBolt.Inference.Api.Services.Workflows.VideoSourceRefusal>? SourceProblems = null)
{
    /// <summary>Builds the response from findings.</summary>
    public static WorkflowLintResponse From(IReadOnlyList<LintFinding> findings) => new(
        [.. findings.Select(LintFindingResponse.From)],
        findings.Count(f => f.Severity == LintSeverity.Error),
        findings.Count(f => f.Severity == LintSeverity.Warning),
        findings.Count(f => f.Severity == LintSeverity.Info));
}
