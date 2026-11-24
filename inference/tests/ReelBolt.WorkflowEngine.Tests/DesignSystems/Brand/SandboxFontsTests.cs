using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Brand;

/// <summary>
/// <see cref="SandboxFonts.Families"/> is the engine's copy of what the sandbox-runtime image installs.
/// The bundled half is pinned to <c>sandbox/fonts/fonts.json</c>, the manifest the image build copies
/// the font files from, so a family added to or dropped from the image cannot drift from what the
/// prompts, the matcher and the network-font refusal tell a model it may use.
/// </summary>
public class SandboxFontsTests
{
    [Fact]
    public void The_bundled_families_are_the_sandbox_font_manifest()
    {
        string manifest = Path.Combine(RepositoryRoot(), "sandbox", "fonts", "fonts.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));

        var fromManifest = doc.RootElement.EnumerateArray()
            .Select(entry => (entry.GetProperty("family").GetString()!, entry.GetProperty("category").GetString()!))
            .ToList();

        SandboxFonts.Families.Where(f => f.Bundled).Select(f => (f.Family, f.Category))
            .Should().BeEquivalentTo(fromManifest, "SandboxFonts.Families must list exactly the families sandbox/fonts/fonts.json bundles");
    }

    [Fact]
    public void The_installed_set_is_the_bundled_families_plus_the_liberation_trio()
    {
        SandboxFonts.Families.Where(f => f.Bundled).Should().HaveCountGreaterThanOrEqualTo(40, "the baked set is about forty OFL families");
        SandboxFonts.Families.Should().HaveCount(SandboxFonts.Families.Count(f => f.Bundled) + 3);
        SandboxFonts.Families.Select(f => f.Family).Should().OnlyHaveUniqueItems();
        SandboxFonts.Families.Where(f => !f.Bundled).Select(f => (f.Family, f.Category)).Should().Equal(
            ("Liberation Sans", "sans"), ("Liberation Serif", "serif"), ("Liberation Mono", "mono"));
        SandboxFonts.Families.Select(f => f.Category).Distinct().Should().BeSubsetOf(["sans", "serif", "mono", "display"]);
        SandboxFonts.FamilyList.Should().Be(string.Join(", ", SandboxFonts.Families.Select(f => f.Family)));
    }

    [Fact]
    public void Every_manifest_file_exists_with_its_recorded_sha256_and_an_OFL_licence_beside_it()
    {
        string root = Path.Combine(RepositoryRoot(), "sandbox", "fonts");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fonts.json")));
        foreach (JsonElement entry in doc.RootElement.EnumerateArray())
        {
            string family = entry.GetProperty("family").GetString()!;
            File.Exists(Path.Combine(root, family, "OFL.txt")).Should().BeTrue($"{family} ships its licence text");
            foreach (JsonElement file in entry.GetProperty("files").EnumerateArray())
            {
                string path = Path.Combine(root, file.GetProperty("path").GetString()!);
                File.Exists(path).Should().BeTrue(path);
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                    .Should().Be(file.GetProperty("sha256").GetString(), path);
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(SandboxFontsTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
                return dir;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
