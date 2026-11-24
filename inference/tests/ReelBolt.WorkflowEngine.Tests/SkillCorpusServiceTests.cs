using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Skills;
using ReelBolt.WorkflowEngine.Services.Skills;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class SkillCorpusServiceTests
{
    [Fact]
    public async Task Loads_every_vendored_skill_with_no_validation_failures()
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        SkillCorpusStatus status = await service.GetStatusAsync(CancellationToken.None);

        status.Failed.Should().BeEmpty(
            "the real vendored corpus under inference/skills/ should load cleanly — " +
            string.Join("; ", status.Failed.Select(f => $"{f.Name}: {f.Reason}")));
        status.Loaded.Select(s => s.Name).Should().BeEquivalentTo(
            SkillCatalog.All.Select(s => s.Name));
        status.Loaded.Should().OnlyContain(s => s.BodyLength > 0);
    }

    [Fact]
    public async Task LoadSkillBodyAsync_returns_the_markdown_body_with_frontmatter_stripped()
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        string? body = await service.LoadSkillBodyAsync("remotion-markup", CancellationToken.None);

        body.Should().NotBeNullOrWhiteSpace();
        body.Should().NotContain("name: remotion-markup", "the YAML frontmatter block should be stripped from the body");
        body.Should().NotStartWith("---");
    }

    // --- Design taste skills: vendored unmodified, with a ReelBolt preface in front ---

    // SHA-256 of the upstream SKILL.md blobs at Leonxlnx/taste-skill 717446e07a78d4e6d7918b1fde1a263378eebac9
    // (also recorded in inference/skills/UPSTREAM.md). A vendored skill is never edited; a refresh
    // bumps the pin, these hashes and the doc together.
    [Theory]
    [InlineData("taste-skill", "aa194351b246b8b4799099d4ed7b033d29eab6e6e3d58d8d2172978be7b3ec89")]
    [InlineData("minimalist-skill", "36bc7328f085405f43b476938e62460fa573bf7e984e949e072bcf014831a44c")]
    [InlineData("brutalist-skill", "fffbaac8597f07679e9d87145533567658aedba3b5eca6f16a7075a333048caa")]
    public void Taste_skill_SKILL_md_is_byte_identical_to_the_pinned_upstream_file(string skill, string expectedSha256)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(ResolveRealSkillsCorpusRoot(), skill, "SKILL.md"));

        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()
            .Should().Be(expectedSha256);
    }

    [Theory]
    [InlineData("taste-skill")]
    [InlineData("minimalist-skill")]
    [InlineData("brutalist-skill")]
    public void Taste_skill_directories_carry_the_MIT_licence_and_a_preface(string skill)
    {
        string dir = Path.Combine(ResolveRealSkillsCorpusRoot(), skill);

        File.ReadAllText(Path.Combine(dir, "LICENSE")).Should().StartWith("MIT License");
        File.ReadAllText(Path.Combine(dir, SkillPreface.FileName)).Should().Contain("written by ReelBolt");
    }

    [Theory]
    [InlineData("taste-skill")]
    [InlineData("minimalist-skill")]
    [InlineData("brutalist-skill")]
    public async Task Taste_skills_load_with_the_ReelBolt_preface_in_front_of_the_unmodified_upstream_body(string skill)
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        string? body = await service.LoadSkillBodyAsync(skill, CancellationToken.None);

        body.Should().NotBeNull();
        body.Should().StartWith("## ReelBolt preface to `" + skill + "`");
        body.Should().Contain("NOT part of the upstream skill");

        // After the divider, the upstream body is exactly SKILL.md minus its frontmatter.
        string upstream = File.ReadAllText(Path.Combine(ResolveRealSkillsCorpusRoot(), skill, "SKILL.md"))
            .Replace("\r\n", "\n");
        string upstreamBody = upstream[(upstream.IndexOf("\n---", 4, StringComparison.Ordinal) + 4)..].Trim();
        body.Should().EndWith(upstreamBody);
        body.Should().NotContain("name: design-taste-frontend", "the upstream frontmatter is stripped like any other skill's");
    }

    [Theory]
    [InlineData("remotion-markup")]
    [InlineData("remotion-create")]
    [InlineData("remotion-captions")]
    [InlineData("remotion-multimedia")]
    [InlineData("remotion-render")]
    public async Task Remotion_skills_load_with_an_offline_sandbox_preface_in_front_of_the_upstream_body(string skill)
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        string? body = await service.LoadSkillBodyAsync(skill, CancellationToken.None);

        body.Should().StartWith("## ReelBolt preface to `" + skill + "`");
        body.Should().Contain("No network at all");
        body.Should().Contain("npx remotion add");
        body.Should().Contain("Never import `@remotion/google-fonts`");
    }

    [Fact]
    public async Task The_preface_can_be_read_as_a_resource_of_its_own_skill_only()
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        (await service.ReadSkillResourceAsync("taste-skill", SkillPreface.FileName, CancellationToken.None))
            .Should().Contain("ReelBolt preface to `taste-skill`");
        (await service.ReadSkillResourceAsync("remotion-markup", SkillPreface.FileName, CancellationToken.None))
            .Should().Contain("ReelBolt preface to `remotion-markup`");
    }

    [Fact]
    public void Preface_Compose_returns_the_upstream_text_unchanged_when_there_is_no_preface()
    {
        SkillPreface.Compose(null, "upstream").Should().Be("upstream");
        SkillPreface.Compose("  \r\n ", "upstream").Should().Be("upstream");
    }

    [Fact]
    public void Preface_Compose_puts_the_trimmed_preface_then_a_divider_then_the_upstream_text()
    {
        SkillPreface.Compose("\nours\n", "upstream").Should().Be(
            "ours\n\n---\n\n*The upstream skill follows, unmodified.*\n\nupstream");
    }

    [Fact]
    public async Task LoadSkillBodyAsync_returns_null_for_an_unknown_skill()
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        (await service.LoadSkillBodyAsync("not-a-real-skill", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ReadSkillResourceAsync_reads_a_real_topic_file()
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        string? content = await service.ReadSkillResourceAsync("remotion-markup", "timing.md", CancellationToken.None);

        content.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\secret.txt")]
    [InlineData("../SKILL.md")]
    public async Task ReadSkillResourceAsync_rejects_a_path_that_escapes_the_skill_directory(string escapingPath)
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        (await service.ReadSkillResourceAsync("remotion-markup", escapingPath, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task ReadSkillResourceAsync_rejects_an_absolute_path()
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        (await service.ReadSkillResourceAsync("remotion-markup", "/etc/passwd", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task ReadSkillResourceAsync_returns_null_for_a_nonexistent_resource_under_a_valid_skill()
    {
        ISkillCorpusService service = CreateServiceOverRealCorpus();

        (await service.ReadSkillResourceAsync("remotion-markup", "does-not-exist.md", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_reports_a_failure_for_a_missing_skill_directory()
    {
        string tempRoot = Directory.CreateTempSubdirectory("skillcorpus-missing-").FullName;
        try
        {
            // Deliberately create none of the five directories — every skill should fail to load.
            ISkillCorpusService service = CreateService(tempRoot);

            SkillCorpusStatus status = await service.GetStatusAsync(CancellationToken.None);

            status.Loaded.Should().BeEmpty();
            status.Failed.Should().HaveCount(SkillCatalog.All.Count);
            status.Failed.Select(f => f.Name).Should().BeEquivalentTo(SkillCatalog.All.Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_skill_without_a_preface_is_returned_exactly_as_before()
    {
        // Every vendored skill now has a preface, so the no-preface path is pinned on a synthetic corpus.
        string tempRoot = Directory.CreateTempSubdirectory("skillcorpus-nopreface-").FullName;
        try
        {
            foreach (SkillDescriptor descriptor in SkillCatalog.All)
            {
                string dir = Path.Combine(tempRoot, descriptor.RelativePath);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                    $"---\nname: {descriptor.Name}\ndescription: test\nversion: 1.0.0\n---\n\nBody content for {descriptor.Name}.\n");
            }

            ISkillCorpusService service = CreateService(tempRoot);

            string? body = await service.LoadSkillBodyAsync("remotion-markup", CancellationToken.None);

            body.Should().Be("Body content for remotion-markup.");
            body.Should().NotContain("ReelBolt preface");
            body.Should().NotContain("The upstream skill follows");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetStatusAsync_reports_a_failure_for_a_skill_missing_its_SKILL_md()
    {
        string tempRoot = Directory.CreateTempSubdirectory("skillcorpus-nomd-").FullName;
        try
        {
            foreach (SkillDescriptor descriptor in SkillCatalog.All)
            {
                string dir = Path.Combine(tempRoot, descriptor.RelativePath);
                Directory.CreateDirectory(dir);
                if (descriptor.Name != "remotion-markup")
                {
                    File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                        $"---\nname: {descriptor.Name}\ndescription: test\nversion: 1.0.0\n---\n\nBody content for {descriptor.Name}.\n");
                }
                // remotion-markup deliberately has no SKILL.md at all.
            }

            ISkillCorpusService service = CreateService(tempRoot);
            SkillCorpusStatus status = await service.GetStatusAsync(CancellationToken.None);

            status.Failed.Should().ContainSingle(f => f.Name == "remotion-markup");
            status.Loaded.Should().HaveCount(SkillCatalog.All.Count - 1);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static ISkillCorpusService CreateService(string corpusRoot)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Skills:CorpusPath"] = corpusRoot })
            .Build();

        return new SkillCorpusService(configuration, NullLogger<SkillCorpusService>.Instance);
    }

    private static ISkillCorpusService CreateServiceOverRealCorpus() => CreateService(ResolveRealSkillsCorpusRoot());

    /// <summary>
    /// Walks up from the test assembly's output directory to find the repo's real
    /// inference/skills/ corpus (identified by its UPSTREAM.md marker file), so these tests
    /// exercise the actual vendored content rather than a synthetic fixture.
    /// </summary>
    private static string ResolveRealSkillsCorpusRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "skills", "UPSTREAM.md");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "skills");

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate inference/skills/UPSTREAM.md above {AppContext.BaseDirectory}.");
    }
}
