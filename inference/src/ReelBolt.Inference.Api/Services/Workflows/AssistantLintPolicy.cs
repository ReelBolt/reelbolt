using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Which lint findings stop the assistant from proposing a workflow (docs/design/workflow-apps.md 6.4: lint errors never
/// reach a card, a proposal with errors is refused to the model with the findings). Only the shape of the graph blocks a
/// proposal; the state of the project or workspace (a provider, the plan, a voice, files, the design system) does not,
/// because the proposal can be right while the project is not ready yet: that is reported as "needed before running".
/// </summary>
public static class AssistantLintPolicy
{
    /// <summary>
    /// Errors about the state of the PROJECT or WORKSPACE rather than the shape of the graph. WL015 is a consent the
    /// customer gives in the app (allow source media to be sent to a vendor), so the assistant tells the user about it.
    /// </summary>
    public static readonly IReadOnlySet<string> ProjectStateRules =
        new HashSet<string>(StringComparer.Ordinal) { "WL006", "WL007", "WL008", "WL010", "WL012", "WL015" };

    /// <summary>WL018 is a dead control on an app page; the save paths refuse it by name, so it never blocks a proposal here.</summary>
    private const string ManifestRule = "WL018";

    /// <summary>
    /// WL028 (a required app input has no value) is about the values of a RUN, not the graph (docs/design/app-inputs.md
    /// 11.3): an app whose page asks a required field is right, and the run page or the run's brief fills it in.
    /// </summary>
    private const string RunValueRule = "WL028";

    /// <summary>True when the finding is an error about the graph's shape (not the project's state, not a manifest control).</summary>
    public static bool IsStructuralError(LintFinding finding) =>
        finding.Severity == LintSeverity.Error
        && finding.RuleId != ManifestRule
        && finding.RuleId != RunValueRule
        && !ProjectStateRules.Contains(finding.RuleId);
}
