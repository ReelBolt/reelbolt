using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// T-206: font and chroma advice on sandbox writes — warnings in the tool result, never a refusal, and only for
/// what the write introduces.
/// </summary>
public sealed class AuthoringAdvisoriesTests
{
    private static readonly string[] Installed = [.. SandboxFonts.Families.Select(f => f.Family)];

    /// <summary>A measured green plate (the compile keys on dominance relative to 202).</summary>
    private static readonly PlateKey Green = new("r0", "green", 0, 202, 0, 0.3, 202);

    [Fact]
    public void The_scan_stops_after_one_finding_past_the_cap_instead_of_collecting_every_one()
    {
        // 200 distinct unknown families: the old scan built all 200 findings (a line count each) and then cut them.
        string after = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"const s{i} = {{ fontFamily: 'Nope{i}' }};"));

        IReadOnlyList<string> findings = AuthoringAdvisories.Scan("src/a.tsx", null, after, Installed, [Green]);

        findings.Should().HaveCount(AuthoringAdvisories.MaxFindings + 1);
        findings[^1].Should().Be($"... and more warning(s) like these (only the first {AuthoringAdvisories.MaxFindings} are listed).");
    }

    [Fact]
    public void Exactly_the_cap_is_listed_without_a_more_line()
    {
        string after = string.Join("\n", Enumerable.Range(0, AuthoringAdvisories.MaxFindings).Select(i => $"const s{i} = {{ fontFamily: 'Nope{i}' }};"));

        AuthoringAdvisories.Scan("src/a.tsx", null, after, Installed, []).Should().HaveCount(AuthoringAdvisories.MaxFindings)
            .And.OnlyContain(f => f.StartsWith("Font warning", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_font_family_is_warned_with_its_line_and_an_installed_one_is_not()
    {
        const string after = "const a = {fontFamily: 'Inter'};\nconst b = {fontFamily: \"Helvetica Neue, Inter, sans-serif\"};\n";

        IReadOnlyList<string> warnings = AuthoringAdvisories.Scan("src/Scene.tsx", null, after, Installed, []);

        warnings.Should().ContainSingle().Which.Should()
            .Contain("src/Scene.tsx line 2").And.Contain("'Helvetica Neue'").And.Contain("StageFont");
    }

    [Fact]
    public void A_family_loaded_by_the_StageFont_snippet_is_not_warned()
    {
        const string snippet = "import {staticFile} from 'remotion';\nimport {loadFont} from '@remotion/fonts';\n\n" +
            "loadFont({ family: 'Lobster', url: staticFile('fonts/Lobster/Lobster-normal-400.ttf'), weight: '400', style: 'normal' });\n" +
            "export const t = {fontFamily: 'Lobster'};\n";
        AuthoringAdvisories.Scan("src/Title.tsx", null, snippet, Installed, []).Should().BeEmpty();
    }

    [Fact]
    public void A_family_declared_from_a_staged_file_or_in_the_design_system_or_already_in_the_file_is_not_warned()
    {
        const string declared = "@font-face { font-family: 'Brand Sans'; src: url('/fonts/brand.woff2'); }\n.title { font-family: 'Brand Sans', sans-serif; }\n";
        AuthoringAdvisories.Scan("src/style.css", null, declared, Installed, []).Should().BeEmpty();

        AuthoringAdvisories.Scan("src/A.tsx", null, "x = {fontFamily: 'Acme Grotesk'}", [.. Installed, "Acme Grotesk"], []).Should().BeEmpty();

        const string before = "x = {fontFamily: 'Helvetica'}";
        AuthoringAdvisories.Scan("src/A.tsx", before, before + "\ny = 1;", Installed, []).Should().BeEmpty("nothing new was introduced");
    }

    [Fact]
    public void Generic_families_variables_and_non_source_files_are_ignored()
    {
        AuthoringAdvisories.Scan("src/A.tsx", null, "x = {fontFamily: 'sans-serif'}; y = {fontFamily: `${theme.font}`}", Installed, []).Should().BeEmpty();
        AuthoringAdvisories.Scan("public/data.json", null, "{\"fontFamily\": \"Comic Sans\"}", Installed, []).Should().BeEmpty();
    }

    [Theory]
    [InlineData("background: '#22c55e'", "#22c55e")]
    [InlineData("color: 'rgb(20, 200, 40)'", "rgb(20, 200, 40)")]
    [InlineData("const c = 'lime';", "lime")]
    [InlineData("fill: '#0a3d0a'", "#0a3d0a")]
    public void A_colour_the_compile_would_key_as_the_plate_is_warned_with_colour_and_line(string line, string colour)
    {
        IReadOnlyList<string> warnings = AuthoringAdvisories.Scan("src/Screen.tsx", null, "// screen\n" + line, Installed, [Green]);

        warnings.Should().ContainSingle().Which.Should()
            .Contain(colour).And.Contain("line 2").And.Contain("green screen (r0)");
    }

    [Theory]
    [InlineData("background: '#10b981'")]   // emerald: green, but too little dominance to key
    [InlineData("color: '#ffffff'")]
    [InlineData("color: '#1d4ed8'")]        // blue on a green plate
    [InlineData("text: 'Go green today'")]   // a word, not a colour value
    public void Colours_the_matte_would_not_key_are_left_alone(string line) =>
        AuthoringAdvisories.Scan("src/Screen.tsx", null, line, Installed, [Green]).Should().BeEmpty();

    [Fact]
    public void Without_plates_there_is_no_colour_advice_and_a_colour_already_in_the_file_is_not_repeated()
    {
        AuthoringAdvisories.Scan("src/Screen.tsx", null, "c = '#00ff00'", Installed, []).Should().BeEmpty();
        AuthoringAdvisories.Scan("src/Screen.tsx", "c = '#00ff00'", "c = '#00ff00'; d = 1", Installed, [Green]).Should().BeEmpty();
    }

    [Fact]
    public void A_blue_plate_keys_blue_and_a_colorkey_only_plate_uses_its_distance()
    {
        PlateKey blue = new("r1", "blue", 0, 0, 230, null, null);
        AuthoringAdvisories.Keys(blue, 30, 60, 240).Should().BeTrue();
        AuthoringAdvisories.Keys(blue, 30, 200, 40).Should().BeFalse();

        PlateKey colorKey = new("r2", "green", 40, 180, 70, 0.1, null);
        AuthoringAdvisories.Keys(colorKey, 45, 175, 72).Should().BeTrue();
        AuthoringAdvisories.Keys(colorKey, 120, 220, 120).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Through the tools
    // ------------------------------------------------------------------

    [Fact]
    public async Task WriteSandboxFile_returns_the_warnings_and_still_writes()
    {
        Harness h = new();

        JsonElement result = await h.WriteAsync("src/Hook.tsx", "export const S = {fontFamily: 'Papyrus', background: '#00ee00'};\n");

        result.GetProperty("Ok").GetBoolean().Should().BeTrue();
        string[] warnings = [.. result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)];
        warnings.Should().HaveCount(2);
        warnings.Should().Contain(w => w.Contains("'Papyrus'"));
        warnings.Should().Contain(w => w.Contains("#00ee00") && w.Contains("(r0)"));
        h.Session.Files["src/Hook.tsx"].Should().Contain("Papyrus", "advice never blocks the write");
    }

    [Fact]
    public async Task A_clean_write_keeps_its_exact_result_shape_and_an_edit_reports_only_what_it_introduces()
    {
        Harness h = new();
        string clean = await h.Tool.WriteSandboxFile("src/A.tsx", "export const S = {fontFamily: 'Inter', color: '#ffffff'};\n");
        clean.Should().Be("{\"Ok\":true,\"path\":\"src/A.tsx\"}");

        string edited = await h.Tool.EditSandboxFile("src/A.tsx", "'#ffffff'", "'#00ee00'");
        using JsonDocument doc = JsonDocument.Parse(edited);
        doc.RootElement.GetProperty("warnings").EnumerateArray().Should().ContainSingle()
            .Which.GetString().Should().Contain("#00ee00").And.Contain("line 1");
    }

    private sealed class Harness
    {
        private const string ArtifactKey = "projects/p/agentFiles/analysis.json";

        public MemorySession Session { get; }
        public ReactRemotionSandboxTools Tool { get; }

        public Harness()
        {
            Guid executionId = Guid.NewGuid();
            Guid projectId = Guid.NewGuid();
            Session = new MemorySession(executionId);

            string dbName = "advisories-" + Guid.NewGuid().ToString("N");
            ServiceCollection services = new();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
            ServiceProvider provider = services.BuildServiceProvider();
            using (IServiceScope scope = provider.CreateScope())
            {
                WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
                WorkflowStep analyze = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, Label = "Analyze" };
                db.WorkflowSteps.Add(analyze);
                db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = projectId });
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = analyze.Id,
                    WorkflowStep = analyze,
                    Status = StepStatus.Completed,
                    ArtifactStorageKey = ArtifactKey
                });
                db.SaveChanges();
            }

            Mock<IProjectFileWorkspace> workspace = new();
            workspace.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), ArtifactKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, path, _) => File.WriteAllTextAsync(path, """
                    {"insertRegions":[{"id":"r0","colorName":"green","method":"chroma",
                      "chromaKey":{"r":0,"g":202,"b":0,"similarity":0.3,"blend":0.1,"score":0.9,"plateDominance":202}}],
                     "offeredInsertRegionIds":["r0"]}
                    """));

            WorkflowExecutionContextAccessor accessor = new();
            accessor.BeginScope(executionId, projectId, "correlation");
            Tool = new ReactRemotionSandboxTools(
                Mock.Of<IHttpClientFactory>(),
                accessor,
                workspace.Object,
                new Mock<IAmazonS3>(MockBehavior.Strict).Object,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build(),
                NullLogger<ReactRemotionSandboxTools>.Instance,
                provider.GetRequiredService<IServiceScopeFactory>(),
                sandboxSessions: new FixedSessionFactory(Session));
        }

        public async Task<JsonElement> WriteAsync(string path, string content)
        {
            using JsonDocument doc = JsonDocument.Parse(await Tool.WriteSandboxFile(path, content));
            return doc.RootElement.Clone();
        }
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>A session over an in-memory file map; only reads and writes are used.</summary>
    private sealed class MemorySession(Guid executionId) : ISandboxSession
    {
        public Guid ExecutionId { get; } = executionId;
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            Files.TryGetValue(path, out string? text)
                ? Task.FromResult(new SandboxTextFile(path, text))
                : throw new FileNotFoundException(path);

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            Files[path] = content;
            return Task.FromResult(new SandboxActionResult(true));
        }

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
