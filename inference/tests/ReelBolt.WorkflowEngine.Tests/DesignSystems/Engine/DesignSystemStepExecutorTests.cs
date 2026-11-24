using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Coverage;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.ProductMap;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

public sealed class DesignSystemStepExecutorTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    // =================================================================
    // Parse
    // =================================================================

    [Fact]
    public async Task Parse_builds_artifact_and_view_booleans()
    {
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        const string css = ":root { --color-brand-primary: #6d28d9; }";
        const string packageJson = """
            {"name":"acme","dependencies":{"next":"^15.0.0","react":"^19.0.0","tailwindcss":"^4.0.0"}}
            """;
        const string page = "export default function Home() { return <main>hi</main>; }";
        const string route = "export async function GET() { return new Response(\"ok\"); }\nexport async function POST() { return new Response(\"ok\"); }";

        (ProjectWorkspaceFile File, string Text)[] fixtures =
        [
            (WorkspaceFile(projectId, "globals.css", "app", css), css),
            (WorkspaceFile(projectId, "package.json", null, packageJson), packageJson),
            (WorkspaceFile(projectId, "page.tsx", "app", page), page),
            (WorkspaceFile(projectId, "route.ts", "app/api/hello", route), route)
        ];

        var texts = fixtures.ToDictionary(entry => entry.File.Id, entry => entry.Text);
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixtures.Select(entry => entry.File).ToList());
        workspace.Setup(w => w.ReadFileAsync(projectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string reference, CancellationToken _) => Task.FromResult(texts[Guid.Parse(reference)]));

        string? capturedArtifact = null;
        string? capturedName = null;
        workspace.Setup(w => w.UploadArtifactAsync(
                projectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, name, _, _, _) =>
            {
                capturedArtifact = System.IO.File.ReadAllText(path);
                capturedName = name;
            })
            .ReturnsAsync("projects/p/agentFiles/design-system/exec/step-1-candidates.json");

        StepExecutionContext context = CreateContext(
            """{"op":"Parse","version":1,"parse":{}}""",
            execution: new WorkflowExecution { Id = executionId, ProjectId = projectId });

        StepExecutionResult result = await CreateExecutor(workspace.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.ArtifactStorageKey.Should().NotBeNullOrEmpty();
        result.TokensUsed.Should().Be(0);
        capturedName.Should().Be($"design-system/{executionId:D}/step-1-candidates.json");

        JsonObject artifact = JsonNode.Parse(capturedArtifact!)!.AsObject();
        artifact["schema"]!.GetValue<string>().Should().Be(DesignSystemStepExecutor.ArtifactSchema);
        artifact["design"]!["color"]!["brand"]!["primary"].Should().NotBeNull();
        artifact["productMap"]!["routes"]!.AsArray().Count.Should().BeGreaterThan(0);

        JsonObject view = JsonNode.Parse(result.Output)!.AsObject()["view"]!.AsObject();
        view["fallbackProduct"].Should().NotBeNull();
        view["fallbackDesign"].Should().NotBeNull();
        view["designTokenCount"]!.GetValue<int>().Should().Be(artifact["coverage"]!["design"]!["tokenCount"]!.GetValue<int>());
        view["routeCount"]!.GetValue<int>().Should().Be(artifact["coverage"]!["productMap"]!["routes"]!.GetValue<int>());
        view["componentCount"]!.GetValue<int>().Should().Be(artifact["coverage"]!["productMap"]!["components"]!.GetValue<int>());
    }

    [Fact]
    public async Task Parse_does_not_ask_for_the_design_fallback_when_only_the_product_map_is_missing()
    {
        // the LLM fallback is charged only for the part the files miss. A complete design
        // with no app code must ask for the product analyzers and NOT for the style extractor.
        Guid projectId = Guid.NewGuid();
        const string css = """
            :root {
              --color-brand-primary: #6d28d9; --color-brand-secondary: #0f172a; --color-background: #ffffff;
              --color-foreground: #020617; --color-muted: #f1f5f9; --color-accent: #f59e0b;
              --font-sans: Inter, sans-serif; --font-mono: "JetBrains Mono", monospace;
              --radius: 0.625rem; --spacing-4: 1rem;
            }
            """;
        ProjectWorkspaceFile file = WorkspaceFile(projectId, "globals.css", "app", css);

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile> { file });
        workspace.Setup(w => w.ReadFileAsync(projectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(css);
        string? capturedArtifact = null;
        workspace.Setup(w => w.UploadArtifactAsync(
                projectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) => capturedArtifact = System.IO.File.ReadAllText(path))
            .ReturnsAsync("key");

        StepExecutionContext context = CreateContext(
            """{"op":"Parse","version":1,"parse":{}}""",
            execution: new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId });

        StepExecutionResult result = await CreateExecutor(workspace.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonNode coverage = JsonNode.Parse(capturedArtifact!)!["coverage"]!;
        coverage["design"]!["fallbackRecommended"]!.GetValue<bool>().Should().BeFalse("the fixture is a complete design");
        coverage["productMap"]!["fallbackRecommended"]!.GetValue<bool>().Should().BeTrue("there is no app code");

        JsonObject view = JsonNode.Parse(result.Output)!.AsObject()["view"]!.AsObject();
        view["fallbackProduct"]!.GetValue<bool>().Should().BeTrue();
        view["fallbackDesign"]!.GetValue<bool>().Should().BeFalse(
            "a covered design must not be re-extracted (and charged) just because the product map is missing");
    }

    [Fact]
    public async Task Parse_skips_oversize_file_without_reading()
    {
        Guid projectId = Guid.NewGuid();
        ProjectWorkspaceFile oversize = WorkspaceFile(projectId, "globals.css", "app", "small", sizeBytes: 262145);

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile> { oversize });

        string? capturedArtifact = null;
        workspace.Setup(w => w.UploadArtifactAsync(
                projectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) => capturedArtifact = System.IO.File.ReadAllText(path))
            .ReturnsAsync("key");

        StepExecutionContext context = CreateContext(
            """{"op":"Parse","version":1,"parse":{}}""",
            execution: new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId });

        StepExecutionResult result = await CreateExecutor(workspace.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        workspace.Verify(
            w => w.ReadFileAsync(projectId, oversize.Id.ToString(), It.IsAny<CancellationToken>()), Times.Never);

        JsonArray diagnostics = JsonNode.Parse(capturedArtifact!)!.AsObject()["diagnostics"]!.AsArray();
        diagnostics.Any(d => d!["code"]!.GetValue<string>() == "FILE_TOO_LARGE").Should().BeTrue();
    }

    // =================================================================
    // Compose
    // =================================================================

    [Fact]
    public async Task Compose_merges_static_and_llm_and_calls_ingest()
    {
        ComposeRun run = await RunComposeAsync(
            StyleOutputJson(), HttpStatusCode.OK, """{"versionIds":["11111111-1111-1111-1111-111111111111"]}""");

        run.Result.Status.Should().Be(StepStatus.Completed);
        run.Handler.LastRequestUri!.AbsolutePath.Should()
            .Be($"/api/v1/projects/{run.ProjectId:D}/design-system/ingest");
        run.Handler.LastMethod.Should().Be(HttpMethod.Post);
        run.Handler.LastAuthorization.Should().BeNull("the named InferenceApi client owns the bearer");

        JsonObject body = JsonNode.Parse(run.Handler.LastBody!)!.AsObject();
        body["executionId"]!.GetValue<string>().Should().Be(run.ExecutionId.ToString("D"));
        body["stepOrder"]!.GetValue<int>().Should().Be(9);

        run.CapturedName.Should().Be($"design-system/{run.ExecutionId:D}/step-9-profile.json");
        JsonObject artifact = JsonNode.Parse(run.CapturedArtifact!)!.AsObject();
        artifact["design"]!["color"]!["brand"]!["primary"]!["$value"]!["hex"]!.GetValue<string>()
            .Should().Be("#6d28d9", "the imported static candidate outranks the LLM fallback");
        artifact["design"]!["typography"]!["size"]!["lg"].Should().NotBeNull("the style fallback contributes tokens");

        JsonObject output = JsonNode.Parse(run.Result.Output)!.AsObject();
        output["meta"]!["ingest"]!.GetValue<string>().Should().Be("done");
        output["meta"]!["versionIds"]!.AsArray().Count.Should().Be(1);
    }

    [Fact]
    public async Task Compose_survives_a_staging_shaped_style_output_and_reports_dropped_tokens()
    {
        string style = JsonSerializer.Serialize(
            ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose.StagingLikeStyleFixture.Build(), WebOptions);

        ComposeRun run = await RunComposeAsync(style, HttpStatusCode.OK, "{}");

        run.Result.Status.Should().Be(StepStatus.Completed, run.Result.Output);
        JsonObject artifact = JsonNode.Parse(run.CapturedArtifact!)!.AsObject();
        JsonObject extra = artifact["design"]!["color"]!["extra"]!.AsObject();
        extra["violet"]!["$value"]!["hex"]!.GetValue<string>().Should().Be("#7c3aed");
        extra["violet"]!["$value"]!["colorSpace"]!.GetValue<string>().Should().Be("srgb");
        extra.ContainsKey("gradient-hero").Should().BeFalse();
        extra.ContainsKey("prose").Should().BeFalse();

        DesignDocumentValidator.Validate(
                artifact["design"]!.AsObject(), new DesignValidationContext(_ => true, new HashSet<string>()))
            .IsValid.Should().BeTrue();

        JsonArray diagnostics = artifact["diagnostics"]!.AsArray();
        diagnostics.Count(d => d!["code"]!.GetValue<string>() == "COLOR_UNPARSEABLE").Should().BeGreaterThanOrEqualTo(
            ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose.StagingLikeStyleFixture.ExpectedDropped.Length);

        JsonObject meta = JsonNode.Parse(run.Result.Output)!["meta"]!.AsObject();
        meta["droppedTokens"]!.GetValue<int>().Should().BeGreaterThan(0);
        meta["diagnostics"]!.GetValue<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Compose_hostile_style_output_leaks_no_secret_and_bounds_diagnostics()
    {
        var style = new StyleAndThemeOutput { Colors = new ColorPalette(), Typography = new Typography() };
        foreach (string secret in ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose.DiagnosticHygieneTests.Secrets)
        {
            style.Colors.Additional[secret] = "linear-gradient(red, blue)";
            style.Colors.Additional[secret + "-v"] = "#112233";
            style.Typography.FontSizes[secret] = "calc(1px + 2px)";
        }

        for (int i = 0; i < 5000; i++)
            style.Colors.Additional["junk" + i] = "not-a-colour";

        ComposeRun run = await RunComposeAsync(JsonSerializer.Serialize(style, WebOptions), HttpStatusCode.OK, "{}");

        run.Result.Status.Should().Be(StepStatus.Completed, run.Result.Output);
        foreach (string secret in ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose.DiagnosticHygieneTests.Secrets)
        {
            run.CapturedArtifact.Should().NotContain(secret);
            run.Result.Output.Should().NotContain(secret);
        }

        JsonObject artifact = JsonNode.Parse(run.CapturedArtifact!)!.AsObject();
        artifact["diagnostics"]!.AsArray().Count.Should().BeLessThanOrEqualTo(210);
        JsonObject meta = JsonNode.Parse(run.Result.Output)!["meta"]!.AsObject();
        meta["droppedTokens"]!.GetValue<int>().Should().BeGreaterThan(5000);
    }

    [Fact]
    public async Task Compose_ingest_500_still_completes_with_pending()
    {
        ComposeRun run = await RunComposeAsync(StyleOutputJson(), HttpStatusCode.InternalServerError, "{}");

        run.Result.Status.Should().Be(StepStatus.Completed);
        JsonObject output = JsonNode.Parse(run.Result.Output)!.AsObject();
        output["meta"]!["ingest"]!.GetValue<string>().Should().Be("pending");
    }

    [Fact]
    public async Task Compose_completes_when_the_code_structure_analyzer_returns_object_entryPoints()
    {
        // Production run 5761becc: the analyzer returned entryPoints as objects while
        // CodeStructureOutput.EntryPoints is List<string>. Strict deserialization nulled the whole
        // record and Compose failed with "step 4 did not yield a CodeStructureOutput."
        const string drifted = """
            {"projectType":"web-app","framework":"next@15",
             "directories":[{"path":"app","purpose":"routes","fileCount":3}],
             "entryPoints":[{"path":"app/page.tsx","type":"page","description":"home"}],
             "overallArchitecture":"app router"}
            """;

        ComposeRun run = await RunComposeAsync(StyleOutputJson(), HttpStatusCode.OK, "{}", drifted);

        run.Result.Status.Should().Be(StepStatus.Completed, run.Result.Output);
        JsonObject artifact = JsonNode.Parse(run.CapturedArtifact!)!.AsObject();
        artifact["productMap"]!["entryPoints"]!.AsArray()
            .Select(node => node!["path"]!.GetValue<string>())
            .Should().Contain("app/page.tsx");
    }

    [Fact]
    public async Task Compose_trims_the_prod_150_char_routingStrategy_the_model_supplies()
    {
        // Production run f0314035: the static map has no routingStrategy (a plain React/Vite tree), the model's
        // RouteAndApi output carries 150 characters (limit 120), and the build used to die in Validate.
        ImportFile[] files = PlainReactFiles();
        string strategy = string.Join(' ', Enumerable.Repeat("client side routing", 12))[..150];
        strategy.Length.Should().Be(150);
        RouteAndApiOutput ra = RouteAndApi();
        ra.RoutingStrategy = strategy;

        ComposeRun run = await RunComposeAsync(
            StyleOutputJson(), HttpStatusCode.OK, "{}", null, JsonSerializer.Serialize(ra, WebOptions), null, files);

        run.Result.Status.Should().Be(StepStatus.Completed, run.Result.Output);
        JsonObject artifact = JsonNode.Parse(run.CapturedArtifact!)!.AsObject();
        string trimmed = artifact["productMap"]!["routingStrategy"]!.GetValue<string>();
        trimmed.Length.Should().BeLessThanOrEqualTo(120);
        trimmed.Should().EndWith("\u2026");
        artifact["diagnostics"]!.AsArray().Should().Contain(d =>
            d!["code"]!.GetValue<string>() == "PROSE_TRIMMED"
            && d["path"]!.GetValue<string>() == "routingStrategy"
            && d["message"]!.GetValue<string>().Contains("150")
            && d["message"]!.GetValue<string>().Contains("120"));
        int count = artifact["diagnostics"]!.AsArray().Count(d => d!["code"]!.GetValue<string>() == "PROSE_TRIMMED");
        JsonNode.Parse(run.Result.Output)!["meta"]!["trimmedFields"]!.GetValue<int>().Should().Be(count).And.BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Compose_keeps_the_diagnostics_bound_and_the_trim_warnings_when_many_tokens_are_dropped()
    {
        RouteAndApiOutput ra = RouteAndApi();
        ra.RoutingStrategy = new string('r', 150);
        var style = new StyleAndThemeOutput { Colors = new ColorPalette() };
        for (int i = 0; i < 300; i++)
            style.Colors.Additional[$"c{i}"] = "linear-gradient(red, blue)";

        ComposeRun run = await RunComposeAsync(
            JsonSerializer.Serialize(style, WebOptions), HttpStatusCode.OK, "{}", null, JsonSerializer.Serialize(ra, WebOptions), null, PlainReactFiles());

        run.Result.Status.Should().Be(StepStatus.Completed, run.Result.Output);
        JsonArray diagnostics = JsonNode.Parse(run.CapturedArtifact!)!["diagnostics"]!.AsArray();
        diagnostics.Count.Should().BeLessThanOrEqualTo(201);
        int trimmedCount = diagnostics.Count(d => d!["code"]!.GetValue<string>() == "PROSE_TRIMMED");
        trimmedCount.Should().BeGreaterThanOrEqualTo(1);
        diagnostics.Count(d => d!["code"]!.GetValue<string>() == "DIAGNOSTICS_TRUNCATED").Should().Be(1);
        JsonNode.Parse(run.Result.Output)!["meta"]!["trimmedFields"]!.GetValue<int>().Should().Be(trimmedCount);
    }

    [Fact]
    public async Task Compose_invalid_llm_output_fails()
    {
        ComposeRun run = await RunComposeAsync("not json", HttpStatusCode.OK, "{}");

        run.Result.Status.Should().Be(StepStatus.Failed);
        run.Result.FailureCode.Should().Be("DESIGN_COMPOSE_INVALID");
        run.Handler.LastRequestUri.Should().BeNull("the step never reached the ingest call");
    }

    // =================================================================
    // Require
    // =================================================================

    [Fact]
    public async Task Require_with_pin()
    {
        // The pin must be LOADABLE (digest verified, ambient enabled), not merely well-formed.
        LoadedPinFixture.Loaded loaded = await LoadedPinFixture.LoadAsync(designNumber: 7, productNumber: 3);
        loaded.State.Should().NotBeNull();

        StepExecutionContext context = CreateContext(
            """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":true}}""",
            execution: new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), DesignSystemPinJson = loaded.PinJson });

        using IDisposable ambient = DesignSystemAmbient.Enter(loaded.State);
        StepExecutionResult result = await CreateExecutor(Mock.Of<IProjectFileWorkspace>()).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.TokensUsed.Should().Be(0);
        JsonObject view = JsonNode.Parse(result.Output)!.AsObject()["view"]!.AsObject();
        view["design"]!["versionNumber"]!.GetValue<int>().Should().Be(7);
        view["productMap"]!["versionNumber"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task Require_fails_closed_when_the_pin_is_well_formed_but_nothing_is_loadable()
    {
        string pin = JsonSerializer.Serialize(new
        {
            v = 1,
            design = new { versionId = Guid.NewGuid().ToString("D"), versionNumber = 7, digest = new string('a', 64), libraryVersionId = (string?)null },
            productMap = new { versionId = Guid.NewGuid().ToString("D"), versionNumber = 3, digest = new string('b', 64) }
        });

        StepExecutionContext context = CreateContext(
            """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":true}}""",
            execution: new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), DesignSystemPinJson = pin });

        StepExecutionResult result = await CreateExecutor(Mock.Of<IProjectFileWorkspace>()).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("PROFILE_MISSING");
    }

    [Fact]
    public async Task Require_fails_when_the_pinned_design_digest_no_longer_verifies()
    {
        LoadedPinFixture.Loaded loaded = await LoadedPinFixture.LoadAsync(corruptDesignDigest: true);

        StepExecutionResult result = await RequireWithAmbientAsync(
            loaded, """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":false}}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("PROFILE_MISSING");
    }

    [Fact]
    public async Task Require_fails_when_the_ambient_is_disabled()
    {
        LoadedPinFixture.Loaded loaded = await LoadedPinFixture.LoadAsync(enabled: false);
        loaded.State.Should().BeNull("a disabled ambient loads nothing");

        StepExecutionResult result = await RequireWithAmbientAsync(
            loaded, """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":true}}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("PROFILE_MISSING");
    }

    [Fact]
    public async Task Require_only_demands_the_members_it_asks_for()
    {
        LoadedPinFixture.Loaded loaded = await LoadedPinFixture.LoadAsync(corruptDesignDigest: true);

        StepExecutionResult result = await RequireWithAmbientAsync(
            loaded, """{"op":"Require","version":1,"require":{"requireDesign":false,"requireProductMap":true}}""");

        result.Status.Should().Be(StepStatus.Completed);
        JsonObject view = JsonNode.Parse(result.Output)!.AsObject()["view"]!.AsObject();
        view.ContainsKey("design").Should().BeFalse("the unloadable design is not reported as present");
        view["productMap"]!["versionNumber"]!.GetValue<int>().Should().Be(1);
    }

    private static async Task<StepExecutionResult> RequireWithAmbientAsync(LoadedPinFixture.Loaded loaded, string configJson)
    {
        StepExecutionContext context = CreateContext(
            configJson,
            execution: new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), DesignSystemPinJson = loaded.PinJson });

        using IDisposable ambient = DesignSystemAmbient.Enter(loaded.State);
        return await CreateExecutor(Mock.Of<IProjectFileWorkspace>()).ExecuteAsync(context);
    }

    [Fact]
    public async Task Require_without_required_member_fails()
    {
        string designOnly = JsonSerializer.Serialize(new
        {
            v = 1,
            design = new
            {
                versionId = Guid.NewGuid().ToString("D"),
                versionNumber = 7,
                digest = new string('a', 64),
                libraryVersionId = (string?)null
            }
        });

        StepExecutionContext present = CreateContext(
            """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":true}}""",
            execution: new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), DesignSystemPinJson = designOnly });
        StepExecutionResult presentResult = await CreateExecutor(Mock.Of<IProjectFileWorkspace>()).ExecuteAsync(present);
        presentResult.Status.Should().Be(StepStatus.Failed);
        presentResult.FailureCode.Should().Be("PROFILE_MISSING");
        presentResult.FailureClass.Should().Be(FailureClass.UserInput);

        StepExecutionContext absent = CreateContext(
            """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":true}}""",
            execution: new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), DesignSystemPinJson = null });
        StepExecutionResult absentResult = await CreateExecutor(Mock.Of<IProjectFileWorkspace>()).ExecuteAsync(absent);
        absentResult.Status.Should().Be(StepStatus.Failed);
        absentResult.FailureCode.Should().Be("PROFILE_MISSING");
        absentResult.FailureClass.Should().Be(FailureClass.UserInput);
    }

    // =================================================================
    // Harness
    // =================================================================

    private sealed record ComposeRun(
        StepExecutionResult Result,
        StubHandler Handler,
        string? CapturedArtifact,
        string? CapturedName,
        Guid ProjectId,
        Guid ExecutionId);

    private static async Task<ComposeRun> RunComposeAsync(
        string styleOutput, HttpStatusCode status, string responseBody, string? codeStructureOutput = null,
        string? routeAndApiOutput = null, string? componentInventoryOutput = null,
        ImportFile[]? importFilesOverride = null)
    {
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        ImportFile[] importFiles = importFilesOverride ?? BuildImportFiles();
        string candidatesJson = BuildCandidatesArtifact(importFiles);

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.DownloadStorageKeyToFileAsync(
                projectId, "parse-key", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, CancellationToken>((_, _, path, _) => System.IO.File.WriteAllText(path, candidatesJson))
            .Returns(Task.CompletedTask);

        string? capturedArtifact = null;
        string? capturedName = null;
        workspace.Setup(w => w.UploadArtifactAsync(
                projectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, name, _, _, _) =>
            {
                capturedArtifact = System.IO.File.ReadAllText(path);
                capturedName = name;
            })
            .ReturnsAsync("projects/p/agentFiles/design-system/exec/step-9-profile.json");

        var db = new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase($"DesignSystemExecutor_{Guid.NewGuid():N}")
                .Options);

        (int Order, AgentDefinition Definition)[] analyzers =
        [
            (4, new AgentDefinition { Id = Guid.NewGuid(), Name = "cs", AgentType = AgentType.CodeStructureAnalyzer }),
            (5, new AgentDefinition { Id = Guid.NewGuid(), Name = "dep", AgentType = AgentType.DependencyAnalyzer }),
            (6, new AgentDefinition { Id = Guid.NewGuid(), Name = "comp", AgentType = AgentType.ComponentInventoryAnalyzer }),
            (7, new AgentDefinition { Id = Guid.NewGuid(), Name = "ra", AgentType = AgentType.RouteAndApiAnalyzer }),
            (8, new AgentDefinition { Id = Guid.NewGuid(), Name = "style", AgentType = AgentType.StyleAndThemeExtractor })
        ];
        db.AgentDefinitions.AddRange(analyzers.Select(entry => entry.Definition));
        await db.SaveChangesAsync();

        var allSteps = analyzers
            .Select(entry => new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                AgentDefinitionId = entry.Definition.Id,
                StepOrder = entry.Order,
                StepType = ReelBolt.Shared.Data.Models.StepType.Agent
            })
            .ToList();

        WorkflowStep composeStep = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 9,
            StepType = ReelBolt.Shared.Data.Models.StepType.DesignSystem,
            DesignSystemConfigJson =
                """{"op":"Compose","version":1,"compose":{"parseStepOrder":1,"designFallbackStepOrders":[8],"productFallbackStepOrders":[4,5,6,7],"ingest":true}}"""
        };
        allSteps.Add(composeStep);

        var history = new List<StepOutputHistoryEntry>
        {
            new(1, "parse", "{}", null, "parse-key"),
            new(4, "cs", codeStructureOutput ?? JsonSerializer.Serialize(CodeStructure(), WebOptions)),
            new(5, "dep", JsonSerializer.Serialize(DependencyAnalysis(), WebOptions)),
            new(6, "comp", componentInventoryOutput ?? JsonSerializer.Serialize(ComponentInventory(), WebOptions)),
            new(7, "ra", routeAndApiOutput ?? JsonSerializer.Serialize(RouteAndApi(), WebOptions)),
            new(8, "style", styleOutput)
        };

        StepExecutionContext context = CreateContext(
            composeStep.DesignSystemConfigJson!,
            execution: new WorkflowExecution { Id = executionId, ProjectId = projectId },
            history: history,
            allSteps: allSteps,
            step: composeStep);

        var scopeFactory = new ServiceCollection()
            .AddSingleton(db)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("InferenceApi"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://inference-api/") });

        DesignSystemStepExecutor executor = new(
            workspace.Object, scopeFactory, httpFactory.Object, NullLogger<DesignSystemStepExecutor>.Instance);

        StepExecutionResult result = await executor.ExecuteAsync(context);
        return new ComposeRun(result, handler, capturedArtifact, capturedName, projectId, executionId);
    }

    private static DesignSystemStepExecutor CreateExecutor(
        IProjectFileWorkspace workspace, IServiceScopeFactory? scopeFactory = null) =>
        new(
            workspace,
            scopeFactory ?? new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<DesignSystemStepExecutor>.Instance);

    private static StepExecutionContext CreateContext(
        string configJson,
        WorkflowExecution? execution = null,
        IReadOnlyList<StepOutputHistoryEntry>? history = null,
        List<WorkflowStep>? allSteps = null,
        WorkflowStep? step = null)
    {
        WorkflowStep actualStep = step ?? new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = ReelBolt.Shared.Data.Models.StepType.DesignSystem,
            DesignSystemConfigJson = configJson
        };

        return new StepExecutionContext
        {
            Execution = execution ?? new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = actualStep,
            AllSteps = allSteps ?? [actualStep],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history ?? [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static ProjectWorkspaceFile WorkspaceFile(
        Guid projectId, string fileName, string? originalPath, string content, long? sizeBytes = null) =>
        new(
            Guid.NewGuid(),
            projectId,
            fileName,
            originalPath,
            "sourceFiles",
            $"projects/{projectId:D}/sourceFiles/{fileName}",
            "text/plain",
            sizeBytes ?? Encoding.UTF8.GetByteCount(content),
            DateTime.UtcNow,
            null);

    private static ImportFile[] PlainReactFiles()
    {
        ImportFile File(string path, string content) =>
            new(path, Encoding.UTF8.GetBytes(content), new string('0', 64), null);

        return
        [
            File("package.json", """{"name":"acme","dependencies":{"react":"^19.0.0"}}"""),
            File("src/main.tsx", "export const x = 1;"),
        ];
    }

    private static ImportFile[] BuildImportFiles()
    {
        ImportFile File(string path, string content) =>
            new(path, Encoding.UTF8.GetBytes(content), new string('0', 64), null);

        return
        [
            File("package.json", """{"name":"acme","dependencies":{"next":"^15.0.0","react":"^19.0.0","tailwindcss":"^4.0.0"}}"""),
            File("pnpm-lock.yaml", string.Empty),
            File("app/globals.css", ":root { --color-brand-primary: #6d28d9; }"),
            File("app/page.tsx", "export default function Home() { return <main>hi</main>; }"),
            File("app/api/hello/route.ts", "export async function GET() { return new Response(\"ok\"); }")
        ];
    }

    private static string BuildCandidatesArtifact(IReadOnlyList<ImportFile> importFiles)
    {
        DesignImportResult design = DesignImportRegistry.ImportAll(importFiles);
        ProductMapExtraction extraction = ProductMapExtractor.Extract(importFiles);
        CoverageReport coverage = DesignCoverage.Compute(design.Design, extraction.Map);

        var artifact = new JsonObject
        {
            ["schema"] = DesignSystemStepExecutor.ArtifactSchema,
            ["design"] = design.Design?.DeepClone(),
            ["productMap"] = ProductMapJson.ToNode(extraction.Map),
            ["sources"] = new JsonArray(),
            ["coverage"] = JsonSerializer.SerializeToNode(coverage, WebOptions),
            ["diagnostics"] = new JsonArray()
        };
        return artifact.ToJsonString(WebOptions);
    }

    private static string StyleOutputJson() =>
        JsonSerializer.Serialize(
            new StyleAndThemeOutput
            {
                Colors = new ColorPalette { Primary = "#112233" },
                Typography = new Typography
                {
                    PrimaryFont = "Inter",
                    FontSizes = new Dictionary<string, string> { ["lg"] = "1.5rem" }
                },
                Spacing = new Spacing
                {
                    Unit = "px",
                    Scale = new Dictionary<string, string> { ["4"] = "16px" }
                },
                StylingApproach = "utility"
            },
            WebOptions);

    private static CodeStructureOutput CodeStructure() => new()
    {
        ProjectType = "web-app",
        Framework = "next@15",
        Directories = [new DirectoryInfo { Path = "app", Purpose = "routes", FileCount = 3 }],
        EntryPoints = ["app/page.tsx"],
        OverallArchitecture = "app router"
    };

    private static DependencyAnalysisOutput DependencyAnalysis() => new()
    {
        Dependencies =
        [
            new Dependency { Name = "next", Version = "15.0.0", Purpose = "framework", IsCore = true },
            new Dependency { Name = "react", Version = "19.0.0", Purpose = "ui", IsCore = true }
        ],
        DevDependencies = ["typescript"],
        PackageManager = "pnpm"
    };

    private static ComponentInventoryOutput ComponentInventory() => new()
    {
        Components =
        [
            new ComponentInfo
            {
                Name = "Button",
                FilePath = "components/ui/Button.tsx",
                Responsibility = "Primary action",
                Props = [new PropInfo { Name = "label", Type = "string", Required = true }]
            }
        ],
        TotalComponents = 1,
        CommonPatterns = ["functional"]
    };

    private static RouteAndApiOutput RouteAndApi() => new()
    {
        ClientRoutes = [new RouteInfo { Path = "/pricing", ComponentName = "Pricing" }],
        ApiEndpoints = [new ApiEndpoint { Method = "GET", Path = "/api/hello", Purpose = "hello" }],
        RoutingStrategy = "app-router"
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        public string? LastBody { get; private set; }

        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            LastAuthorization = request.Headers.Authorization;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
