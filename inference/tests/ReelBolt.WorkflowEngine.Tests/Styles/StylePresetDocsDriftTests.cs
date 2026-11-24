using System;
using System.IO;
using System.Linq;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>Pins the built-in preset list and the dynamic-effect catalogue to the docs that name them.</summary>
public sealed class StylePresetDocsDriftTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "editing-styles.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root with docs/editing-styles.md not found");
    }

    private static string Doc(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    public static TheoryData<string> DocPages() => new()
    {
        Path.Combine("docs", "editing-styles.md"),
        Path.Combine("docs", "user-guide", "workflows", "editing-styles.md"),
        Path.Combine("docs", "user-guide-i18n", "it", "docusaurus-plugin-content-docs", "current", "workflows", "editing-styles.md"),
    };

    [Theory]
    [MemberData(nameof(DocPages))]
    public void Every_preset_key_appears_in_the_page(string page)
    {
        var text = Doc(page);
        var missing = StylePresetCatalog.All.Select(p => p.Key).Where(k => !text.Contains(k, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"{page} does not mention: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_dynamic_effect_appears_in_editing_styles()
    {
        var text = Doc("docs", "editing-styles.md");
        var missing = Enum.GetNames<DynamicEffect>().Where(n => !text.Contains(n, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"docs/editing-styles.md does not mention: {string.Join(", ", missing)}");
    }
}
