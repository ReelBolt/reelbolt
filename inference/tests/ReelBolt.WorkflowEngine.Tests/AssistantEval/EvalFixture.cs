using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>The project state one eval goal is run against (fixture <c>project</c> block).</summary>
internal sealed record EvalProject(
    string Code,
    int VideoClips,
    int Images,
    bool MusicTrack,
    bool SfxFiles,
    bool ConsentedVoice,
    string DesignSystem,
    IReadOnlyList<string> Providers,
    string Plan);

/// <summary>What the fixture says a correct answer looks like (<c>expected</c> block).</summary>
internal sealed record EvalExpected(
    string Route,
    string? TemplateKey,
    IReadOnlyList<string> Alternates,
    IReadOnlyList<string> Patch,
    IReadOnlyDictionary<string, JsonElement> Brief,
    IReadOnlyList<string> Clarify,
    IReadOnlyList<string> Lint,
    bool Paid,
    string? Availability);

internal sealed record EvalGoal(
    string Id,
    string Locale,
    string Goal,
    EvalProject Project,
    EvalExpected Expected,
    IReadOnlyList<string> Assumptions)
{
    /// <summary>A goal that names a planned (not yet shipped) template is scored as a decline.</summary>
    public bool IsPlanned => Expected.Availability?.StartsWith("planned:", StringComparison.Ordinal) == true;

    /// <summary>The route the scorer compares against (planned templates score as declines, eval-goals.md).</summary>
    public string EffectiveRoute => IsPlanned ? "decline_with_alternative" : Expected.Route;
}

/// <summary>Loads <c>docs/design/workflow-apps/eval-goals.json</c>.</summary>
internal static class EvalFixture
{
    public const string RelativePath = "docs/design/workflow-apps/eval-goals.json";

    /// <summary>Walks up from the test output directory to the repository root that holds the fixture.</summary>
    public static string LocateFile()
    {
        string? env = Environment.GetEnvironmentVariable("REELBOLT_EVAL_GOALS_FILE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, RelativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{RelativePath} not found above {AppContext.BaseDirectory}");
    }

    public static IReadOnlyList<EvalGoal> Load(string? path = null)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path ?? LocateFile()));
        return doc.RootElement.GetProperty("goals").EnumerateArray().Select(Parse).ToList();
    }

    public static IReadOnlyList<string> LoadPlannedKeys(string? path = null)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path ?? LocateFile()));
        return doc.RootElement.TryGetProperty("plannedTemplates", out JsonElement p)
            ? p.EnumerateArray().Select(e => e.GetProperty("key").GetString()!).ToList()
            : [];
    }

    private static EvalGoal Parse(JsonElement g)
    {
        JsonElement p = g.GetProperty("project");
        JsonElement e = g.GetProperty("expected");
        var project = new EvalProject(
            p.GetProperty("code").GetString()!,
            p.GetProperty("videoClips").GetInt32(),
            p.GetProperty("images").GetInt32(),
            p.GetProperty("musicTrack").GetBoolean(),
            p.GetProperty("sfxFiles").GetBoolean(),
            p.GetProperty("consentedVoice").GetBoolean(),
            p.GetProperty("designSystem").GetString()!,
            Strings(p, "providers"),
            p.GetProperty("plan").GetString()!);
        var brief = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("brief", out JsonElement b) && b.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty prop in b.EnumerateObject()) brief[prop.Name] = prop.Value.Clone();
        var expected = new EvalExpected(
            e.GetProperty("route").GetString()!,
            e.TryGetProperty("templateKey", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
            Strings(e, "alternates"),
            Strings(e, "patch"),
            brief,
            Strings(e, "clarify"),
            Strings(e, "lint"),
            e.TryGetProperty("paid", out JsonElement paid) && paid.GetBoolean(),
            e.TryGetProperty("availability", out JsonElement av) ? av.GetString() : null);
        return new EvalGoal(
            g.GetProperty("id").GetString()!, g.GetProperty("locale").GetString()!, g.GetProperty("goal").GetString()!,
            project, expected, Strings(g, "assumptions"));
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
}
