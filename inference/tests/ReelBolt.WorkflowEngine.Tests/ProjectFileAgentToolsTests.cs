using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class ProjectFileAgentToolsTests
{
    private static ProjectFileAgentTools CreateTools(
        IProjectFileWorkspace workspace, IWorkflowExecutionContextAccessor accessor) =>
        new(
            workspace,
            accessor,
            Mock.Of<IHttpClientFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<ProjectFileAgentTools>.Instance);

    [Fact]
    public async Task ReadProjectFile_returns_the_guardrail_message_instead_of_throwing_for_a_binary_file()
    {
        // Regression test: found live producing a real promo video. ProjectFileWorkspace.
        // ReadFileAsync deliberately throws InvalidOperationException rather than decoding a
        // binary file (a music/SFX/video candidate) as UTF-8 text — but that exception, left
        // uncaught here, propagated out of the whole agent run and failed a step that otherwise
        // had everything it needed (the model's own unnecessary curiosity about an unrelated
        // file). The tool must catch it and return the message as an ordinary tool result instead.
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ReadFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "File 'music.mp3' (audio/mpeg, 3,080,358 bytes) is a binary file and cannot be read as text. " +
                "Use its file name and any metadata you were already given instead of reading its content."));

        var accessor = new WorkflowExecutionContextAccessor();
        using IDisposable _ = accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "test");

        ProjectFileAgentTools tools = CreateTools(workspace.Object, accessor);

        string result = await tools.ReadProjectFile("music.mp3");

        result.Should().StartWith("Error:");
        result.Should().Contain("not a readable text file");
    }

    [Fact]
    public async Task ReadProjectFile_returns_a_usable_message_instead_of_throwing_when_the_reference_does_not_resolve()
    {
        // The other half of the same crash class the binary-file test above covers, and equally
        // not hypothetical. A VideoAnalyze step's bounded view hands the agent
        // meta.artifactStorageKey, which points at a real S3 object that is deliberately NOT a
        // project_files row — so an agent following that key into this tool used to take the whole
        // step down with a KeyNotFoundException. Seen live twice: once failing a VideoStoryEditor
        // step outright, and once as the stated reason a SoundDesigner step gave up.
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ReadFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException(
                "Project file 'projects/abc/agentFiles/video-analysis/def/step-1-analysis.json' not found in project scope."));

        var accessor = new WorkflowExecutionContextAccessor();
        using IDisposable _ = accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "test");

        ProjectFileAgentTools tools = CreateTools(workspace.Object, accessor);

        string result = await tools.ReadProjectFile(
            "projects/abc/agentFiles/video-analysis/def/step-1-analysis.json");

        result.Should().StartWith("Error:");
        result.Should().Contain("was not found in this project");
        // Must steer the agent somewhere useful, not just report the failure: without this the
        // agent retries the same unreadable key or concludes the task is impossible.
        result.Should().Contain("analysis view");
    }

    [Fact]
    public async Task ReadProjectFile_returns_file_content_unchanged_on_success()
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ReadFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync("export const x = 1;");

        var accessor = new WorkflowExecutionContextAccessor();
        using IDisposable _ = accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "test");

        ProjectFileAgentTools tools = CreateTools(workspace.Object, accessor);

        string result = await tools.ReadProjectFile("src/index.ts");

        result.Should().Be("export const x = 1;");
    }

    // ── The narrower write set for the model's authoring tools ───────────────

    private static ProjectWorkspaceFile File(string name, string mime) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, name, "userFiles",
            $"projects/p/userFiles/{name}", mime, 128, DateTime.UtcNow, null);

    private static void VerifyNoWrite(Mock<IProjectFileWorkspace> workspace) =>
        workspace.Verify(
            w => w.WriteTextFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);

    [Fact]
    public async Task EditProjectFile_refuses_a_build_file_the_model_may_not_author()
    {
        // A .csproj is readable (a human may read and edit it through the dashboard) but the
        // model's authoring tools get the narrower write set and must refuse to edit it. The refusal
        // sits after the read, so a binary still gets its own "binary file" message (pinned by
        // ProjectFileTargetedToolsTests) and only a readable-but-unwritable text file lands here.
        var workspace = new Mock<IProjectFileWorkspace>();
        ProjectWorkspaceFile csproj = File("Contoso.Portal.Client.csproj", "application/xml");
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ProjectWorkspaceFile>)[csproj]);
        workspace
            .Setup(w => w.ReadFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var accessor = new WorkflowExecutionContextAccessor();
        using IDisposable _ = accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "test");
        ProjectFileAgentTools tools = CreateTools(workspace.Object, accessor);

        string result = await tools.EditProjectFile("Contoso.Portal.Client.csproj", "<a/>", "<b/>");

        using JsonDocument doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("does not author");
        VerifyNoWrite(workspace);
    }

    [Fact]
    public async Task WriteProjectFile_refuses_a_script_name_the_model_may_not_author()
    {
        // WriteProjectFile had no text gate at all before this change, so an agent could author a
        // .sh through it regardless of the allowlist. It now refuses one.
        var workspace = new Mock<IProjectFileWorkspace>();
        var accessor = new WorkflowExecutionContextAccessor();
        using IDisposable _ = accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "test");
        ProjectFileAgentTools tools = CreateTools(workspace.Object, accessor);

        string result = await tools.WriteProjectFile("deploy.sh", "echo hi", "text/plain");

        using JsonDocument doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("does not author");
        VerifyNoWrite(workspace);
    }

    [Fact]
    public async Task WriteProjectFile_refuses_a_name_whose_stored_form_is_a_script()
    {
        // The gate and the writer must agree about the name. WriteTextFileAsync normalises
        // "evil.sh/." to the stored name "evil.sh", so the gate must inspect that same name. Before
        // this, the gate saw Path.GetFileName("evil.sh/.") == "." (no extension, admitted by a
        // caller-supplied text/plain MIME) and the writer stored "evil.sh" — the gate was walked
        // around entirely.
        var workspace = new Mock<IProjectFileWorkspace>();
        var accessor = new WorkflowExecutionContextAccessor();
        using IDisposable _ = accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "test");
        ProjectFileAgentTools tools = CreateTools(workspace.Object, accessor);

        string result = await tools.WriteProjectFile("evil.sh/.", "echo hi", "text/plain");

        using JsonDocument doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("does not author");
        VerifyNoWrite(workspace);
    }

    [Fact]
    public async Task WriteProjectFile_returns_a_failure_instead_of_throwing_for_a_traversal_name()
    {
        // NormalizeRelativePath throws on ".."; today that exception escapes the tool. A refusal the
        // model can read is what the other project-file tools return for the same input.
        var workspace = new Mock<IProjectFileWorkspace>();
        var accessor = new WorkflowExecutionContextAccessor();
        using IDisposable _ = accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "test");
        ProjectFileAgentTools tools = CreateTools(workspace.Object, accessor);

        string result = await tools.WriteProjectFile("../escape.tsx", "export const x = 1;", "text/plain");

        using JsonDocument doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("invalid path");
        VerifyNoWrite(workspace);
    }

    [Fact]
    public async Task WriteProjectFile_still_writes_an_ordinary_source_file()
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        Guid projectId = Guid.NewGuid();
        ProjectWorkspaceFile saved = new(
            Guid.NewGuid(), projectId, "scene-01.tsx", "scene-01.tsx", "agentFiles",
            "projects/x/agentFiles/scene-01.tsx", "text/plain", 19, DateTime.UtcNow, null);
        string? storedName = null;
        string? storedPath = null;
        workspace
            .Setup(w => w.WriteTextFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Guid, string, string, string, CancellationToken, string, string?>(
                (_, name, _, _, _, _, originalPath) => { storedName = name; storedPath = originalPath; })
            .ReturnsAsync(saved);

        var accessor = new WorkflowExecutionContextAccessor();
        using IDisposable _ = accessor.BeginScope(Guid.NewGuid(), projectId, "test");
        ProjectFileAgentTools tools = CreateTools(workspace.Object, accessor);

        string result = await tools.WriteProjectFile("scene-01.tsx", "export const x = 1;", "text/plain");

        using JsonDocument doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("Id").GetGuid().Should().Be(saved.Id);
        doc.RootElement.GetProperty("OriginalFileName").GetString().Should().Be("scene-01.tsx");
        storedName.Should().Be("scene-01.tsx", "the name gated must be byte-for-byte the name stored");
        storedPath.Should().Be("scene-01.tsx");
        workspace.Verify(
            w => w.WriteTextFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
    }
}
