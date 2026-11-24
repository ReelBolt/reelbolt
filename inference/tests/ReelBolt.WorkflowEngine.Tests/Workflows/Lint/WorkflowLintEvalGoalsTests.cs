using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>
/// The <c>lint</c> expectations of the eval set (19 goals in the fixture; the spec text says 18) (docs/design/workflow-apps/eval-goals.json), fed as the goal's
/// project facts and brief against the template it routes to. A goal is "covered" when the linter reports every
/// rule the fixture expects. The rest are named gaps with the reason, each covered elsewhere or waiting for a
/// later ticket; the test fails if a gap starts to pass (so the table cannot go stale) or a covered goal breaks.
/// </summary>
public class WorkflowLintEvalGoalsTests(ITestOutputHelper output)
{
    /// <summary>
    /// Goals whose lint expectation the linter does not produce from the template alone, with the reason.
    /// Lane 3a's harness (the assistant building the graph/patch from the goal) is where these are scored end to end.
    /// </summary>
    private static readonly Dictionary<string, string> Gaps = new()
    {
        ["G01"] = "WL002 is expected on the patched graph (a Voiceover inserted, then the compile wiring); the unpatched template has no compile. Scored by lane 3a's harness with the patch applied.",
        ["G08"] = "WL006 is expected for the Voiceover the patch adds (no voice provider on a trial project); the unpatched template has no voice step. WL016 is covered. Scored by lane 3a's harness with the patch applied.",
        ["G11"] = "WL002 is expected on the patched graph (see G01).",
        ["G26"] = "WL009 needs the SpeechLanguageSupport fact table (P2-2); unknown support is silent here by decision.",
        ["G36"] = "clarify_first: no template is chosen, so there is no graph to lint; WL012 on the brief alone is covered by the rule tests.",
        ["G40"] = "WL019 is what a plain captions flag on a footage template trips; the shipped video-subtitles template carries a transcript caption source and lints clean. WL019 is covered by the rule tests.",
        ["G47"] = "clarify_first: no template and no brief files; WL010 on a missing slot is covered by the rule tests.",
        ["G50"] = "clarify_first: no template and no brief files; WL010 on a missing slot is covered by the rule tests."
    };

    /// <summary>
    /// Errors the linter raises that the fixture does not list, each a real finding the fixture's project facts omit.
    /// </summary>
    private static readonly Dictionary<string, string[]> ExtraErrors = new()
    {
        // video-derush-edit-pickups also transcribes the footage (Required) and the fixture's providers list no transcription.
        ["G24"] = ["WL006"]
    };

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs", "design", "workflow-apps", "eval-goals.json")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("eval-goals.json not found above the test binaries");
    }

    private static List<JsonElement> LintGoals()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "design", "workflow-apps", "eval-goals.json")));
        return [.. doc.RootElement.GetProperty("goals").EnumerateArray()
            .Where(g => g.GetProperty("expected").GetProperty("lint").GetArrayLength() > 0)
            .Select(g => g.Clone())];
    }

    public static IEnumerable<object[]> GoalIds()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "design", "workflow-apps", "eval-goals.json")));
        return [.. doc.RootElement.GetProperty("goals").EnumerateArray()
            .Where(g => g.GetProperty("expected").GetProperty("lint").GetArrayLength() > 0)
            .Select(g => new object[] { g.GetProperty("id").GetString()! })];
    }

    [Fact]
    public void The_fixture_has_nineteen_lint_goals()
    {
        LintGoals().Should().HaveCount(19);
    }

    [Theory]
    [MemberData(nameof(GoalIds))]
    public async Task Each_lint_goal_is_covered_or_a_documented_gap(string goalId)
    {
        JsonElement goal = LintGoals().Single(g => g.GetProperty("id").GetString() == goalId);
        JsonElement expected = goal.GetProperty("expected");
        string[] expectedRules = [.. expected.GetProperty("lint").EnumerateArray().Select(e => e.GetString()!)];
        string? templateKey = expected.TryGetProperty("templateKey", out JsonElement key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;

        WorkflowTemplateDefinition? template = templateKey is null ? null : WorkflowTemplateCatalog.GetAll().FirstOrDefault(t => t.Key == templateKey);
        HashSet<string> found = [];
        HashSet<string> foundErrors = [];
        if (template is not null)
        {
            (RunBrief brief, FakeFacts facts) = Context(goal);
            IReadOnlyList<LintFinding> findings = await LintAsync(FromTemplate(template), brief, facts);
            found = [.. findings.Select(f => f.RuleId)];
            foundErrors = [.. findings.Where(f => f.Severity == LintSeverity.Error).Select(f => f.RuleId)];
            output.WriteLine($"{goalId} {templateKey}: expected [{string.Join(",", expectedRules)}], found [{string.Join(",", found.OrderBy(f => f))}]");
        }

        bool covered = template is not null && expectedRules.All(found.Contains);
        if (Gaps.TryGetValue(goalId, out string? reason))
            covered.Should().BeFalse($"{goalId} is listed as a gap ({reason}); if it passes now, remove it from the table");
        else
        {
            // Exact: every error the linter raises is one the fixture expects (or a documented extra), so a template
            // that also tripped an unexpected error cannot count as covered.
            HashSet<string> allowed = [.. expectedRules, .. ExtraErrors.GetValueOrDefault(goalId, [])];
            foundErrors.Should().BeSubsetOf(allowed, $"{goalId} must raise no error the fixture does not expect");
            covered.Should().BeTrue($"{goalId} expects [{string.Join(",", expectedRules)}] on {templateKey}; found [{string.Join(",", found)}]");
        }
    }

    /// <summary>The goal's project facts and its expected brief, as the linter sees a run of the routed template.</summary>
    private static (RunBrief Brief, FakeFacts Facts) Context(JsonElement goal)
    {
        JsonElement project = goal.GetProperty("project");
        JsonElement brief = goal.GetProperty("expected").GetProperty("brief");

        FakeFacts facts = new()
        {
            FileCounts = new Dictionary<RunBriefFileRole, int>
            {
                [RunBriefFileRole.Code] = project.GetProperty("code").GetString() == "none" ? 0 : 1,
                [RunBriefFileRole.Video] = project.GetProperty("videoClips").GetInt32(),
                [RunBriefFileRole.Image] = project.GetProperty("images").GetInt32(),
                [RunBriefFileRole.Music] = project.GetProperty("musicTrack").GetBoolean() ? 1 : 0,
                [RunBriefFileRole.Sfx] = project.GetProperty("sfxFiles").GetBoolean() ? 1 : 0,
                [RunBriefFileRole.Document] = 0
            },
            DesignApproved = project.GetProperty("designSystem").GetString() == "approved"
        };

        HashSet<string> providers = [.. project.GetProperty("providers").EnumerateArray().Select(p => p.GetString()!)];
        (string Name, InferenceProviderCapability Capability)[] known =
        [
            ("voice", InferenceProviderCapability.SpeechSynthesis),
            ("videoGeneration", InferenceProviderCapability.VideoGeneration),
            ("transcription", InferenceProviderCapability.Transcription),
            ("vision", InferenceProviderCapability.Vision),
            ("musicGeneration", InferenceProviderCapability.MusicGeneration)
        ];
        foreach ((string name, InferenceProviderCapability capability) in known.Where(k => !providers.Contains(k.Name)))
            facts.MissingProviders.Add(capability);

        string plan = project.GetProperty("plan").GetString()!;
        bool trial = plan == "trial";
        facts.Plan = new OrganizationEntitlementSet
        {
            Tier = plan,
            VideoGenAllowed = !trial,
            RoomsAllowed = !trial,
            AudioGenAllowed = !trial,
            MaxOutputHeight = trial ? 720 : 2160
        };

        string? voice = brief.TryGetProperty("voice", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        Guid? projectVoice = null;
        if (voice == "project-voice")
        {
            projectVoice = Guid.NewGuid();
            facts.Voices[projectVoice.Value] = new LintVoiceFact(
                true, project.GetProperty("consentedVoice").GetBoolean(),
                project.GetProperty("consentedVoice").GetBoolean() ? null : "consent_missing", "Presenter", null);
        }

        RunBrief runBrief = new(
            Language: Str(brief, "language"),
            LengthSec: brief.TryGetProperty("lengthSec", out JsonElement len) ? len.GetInt32() : null,
            Format: Str(brief, "format"),
            Resolution: Str(brief, "resolution"),
            DesignSystem: Str(brief, "designSystem"),
            Voice: voice switch
            {
                "narrated" => new RunBriefVoice(RunBriefVoiceMode.Narrated),
                "none" => new RunBriefVoice(RunBriefVoiceMode.None),
                "project-voice" => new RunBriefVoice(RunBriefVoiceMode.Narrated, ProjectVoiceId: projectVoice),
                _ => null
            });
        return (runBrief, facts);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
