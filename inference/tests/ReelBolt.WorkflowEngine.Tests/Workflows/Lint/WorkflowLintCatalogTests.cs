using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>
/// All 23 catalog templates lint with zero errors (docs/design/workflow-apps.md 3.11): inserting a step in a
/// template can no longer silently mis-target a review loop or a Previous reference. The facts are permissive on
/// purpose (every provider, plan feature, file and approval present), so what is left is the graph itself.
/// </summary>
public class WorkflowLintCatalogTests(ITestOutputHelper output)
{
    /// <summary>
    /// The one documented exception: the region-effect walkthrough ships with the egress switch OFF on purpose (a
    /// template must never pre-consent to a customer's footage leaving the host); its description tells the author
    /// to switch it on, and until they do the run stops with EGRESS_REFUSED. The rule is right and the template is
    /// right: WL015 is exactly the warning the author needs before pressing Run.
    /// </summary>
    private static readonly HashSet<(string Template, string Rule, int Step)> DocumentedErrors =
    [
        (WorkflowTemplateCatalog.RegionEffectTemplateKey, "WL015", 1),
        // Effects on a clip: the object-finding analyses (blur at step 3, follow at step 6) ship with the same switch OFF.
        ("effects-on-a-clip", "WL015", 3),
        ("effects-on-a-clip", "WL015", 6)
    ];

    public static IEnumerable<object[]> Templates() =>
        WorkflowTemplateCatalog.GetAll().Select(t => new object[] { t.Key });

    [Fact]
    public void The_catalog_has_twenty_six_templates_with_the_two_subtitle_ones_and_the_testimonial_recipe()
    {
        // 28 since avatars phase 3 added "talking-avatar-budget" (27 with phase 2's "spokesperson-clip"); the method
        // name keeps its history.
        WorkflowTemplateCatalog.GetAll().Should().HaveCount(39, "the 29 authored templates (the sound-effects-only recipe is the newest) plus the merged Avatar clip, the six preset recipes and the three video apps");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task Every_template_lints_with_no_errors_beyond_the_documented_one(string key)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().Single(t => t.Key == key);
        IReadOnlyList<LintFinding> findings = await LintAsync(FromTemplate(template));

        foreach (LintFinding f in findings)
            output.WriteLine($"{key}: {f.RuleId} {f.Severity} step {f.StepOrder}: {f.Message}");

        findings
            .Where(f => f.Severity == LintSeverity.Error)
            .Where(f => !DocumentedErrors.Contains((key, f.RuleId, f.StepOrder ?? 0)))
            .Should().BeEmpty($"template {key} must lint clean of errors");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task No_template_reviews_the_film_before_its_voice_and_final_cut(string key)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().Single(t => t.Key == key);
        IReadOnlyList<LintFinding> findings = await LintAsync(FromTemplate(template));
        findings.Rule("WL020").Should().BeEmpty($"template {key} must put its review after the narration and the final cut");
    }

    [Fact]
    public async Task The_documented_exception_is_real()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().Single(t => t.Key == WorkflowTemplateCatalog.RegionEffectTemplateKey);
        IReadOnlyList<LintFinding> findings = await LintAsync(FromTemplate(template));
        findings.Rule("WL015").Should().ContainSingle(f => f.StepOrder == 1);
    }

    [Fact]
    public async Task Removing_a_step_above_a_review_loop_without_retargeting_it_is_caught()
    {
        // The catalog hard-codes its loop targets (10, 11, 6, 7, 9, 2 ...). Take the first step out of
        // quick-win-promo and renumber the rest, as an editor insert/delete does, and the loop's target now points
        // at the review step itself: the exact silent mis-target WL003 exists for.
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().Single(t => t.Key == WorkflowTemplateCatalog.StarterTemplateKey);
        List<WorkflowStep> steps = FromTemplate(template);
        steps.RemoveAt(0);
        foreach (WorkflowStep s in steps)
            s.StepOrder--;

        IReadOnlyList<LintFinding> findings = await LintAsync(steps);

        findings.Rule("WL003").Should().ContainSingle().Which.StepOrder.Should().Be(10);
    }
}
