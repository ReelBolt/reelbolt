using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// <c>ReadProjectFileLines</c>, <c>GetProjectFileOutline</c> and <c>EditProjectFile</c> — the
/// project-file twins of the sandbox's ranged-read, outline and targeted-edit tools — plus the cap
/// that now bounds a whole-file <c>ReadProjectFile</c>.
/// </summary>
public class ProjectFileTargetedToolsTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    /// <summary>An in-memory project behind a mocked workspace: files by id, with the workspace's own newest-first name lookup.</summary>
    private sealed class FakeWorkspace
    {
        public FakeWorkspace()
        {
            Mock.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (IReadOnlyList<ProjectWorkspaceFile>)[.. Files.Values.Select(v => v.File)]);

            Mock.Setup(w => w.ReadFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, string reference, CancellationToken _) => Task.FromResult(Read(reference)));

            Mock.Setup(w => w.WriteTextFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
                .Returns((Guid _, string fileName, string content, string contentType, CancellationToken _, string category, string? originalPath) =>
                {
                    Writes.Add((fileName, content, contentType, category, originalPath));
                    return Task.FromResult(Add(originalPath ?? fileName, content, category, contentType, DateTime.UtcNow.AddSeconds(1)));
                });
        }

        public Mock<IProjectFileWorkspace> Mock { get; } = new();

        public Dictionary<Guid, (ProjectWorkspaceFile File, string Text)> Files { get; } = [];

        public List<(string FileName, string Content, string ContentType, string Category, string? OriginalPath)> Writes { get; } = [];

        public ProjectWorkspaceFile Add(string path, string text, string category = "userFiles", string mime = "text/plain", DateTime? uploadedAt = null)
        {
            ProjectWorkspaceFile file = new(
                Guid.NewGuid(), ProjectId, System.IO.Path.GetFileName(path), path, category,
                $"projects/{ProjectId}/{category}/{path}", mime, text.Length, uploadedAt ?? DateTime.UtcNow, null);
            Files[file.Id] = (file, text);
            return file;
        }

        private string Read(string reference)
        {
            if (Guid.TryParse(reference, out Guid id) && Files.TryGetValue(id, out var byId))
                return RefuseBinary(byId.File, byId.Text);

            var matches = Files.Values
                .Where(v => v.File.StorageKey == reference || v.File.OriginalFileName == reference || v.File.OriginalPath == reference)
                .OrderByDescending(v => v.File.UploadedAt)
                .ToList();

            if (matches.Count == 0)
                throw new KeyNotFoundException($"Project file '{reference}' not found in project scope.");
            return RefuseBinary(matches[0].File, matches[0].Text);
        }

        /// <summary>The real workspace refuses a binary file whichever way it was addressed.</summary>
        private static string RefuseBinary(ProjectWorkspaceFile file, string text) =>
            file.MimeType.StartsWith("audio/", StringComparison.Ordinal)
                ? throw new InvalidOperationException($"File '{file.OriginalFileName}' is a binary file and cannot be read as text.")
                : text;
    }

    private static ProjectFileAgentTools CreateTools(FakeWorkspace workspace)
    {
        WorkflowExecutionContextAccessor accessor = new();
        accessor.BeginScope(Guid.NewGuid(), ProjectId, "test");

        return new ProjectFileAgentTools(
            workspace.Mock.Object,
            accessor,
            Mock.Of<IHttpClientFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<ProjectFileAgentTools>.Instance);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "access to it was denied")]
    [InlineData(typeof(System.IO.IOException), "storage is temporarily unavailable")]
    [InlineData(typeof(TimeoutException), "storage is temporarily unavailable")]
    [InlineData(typeof(ArgumentException), "its content could not be loaded")]
    public async Task ReadProjectFile_turns_any_unexpected_exception_into_a_descriptive_tool_result(Type exceptionType, string phrase)
    {
        FakeWorkspace workspace = new();
        workspace.Mock.Setup(w => w.ReadFileAsync(It.IsAny<Guid>(), "Pages/Home.razor", It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "boom")!);

        string result = await CreateTools(workspace).ReadProjectFile("Pages/Home.razor");

        result.Should().Contain("could not be read").And.Contain("Pages/Home.razor")
            .And.Contain(phrase).And.Contain("ListProjectFiles");
        result.Should().NotContain("Function failed").And.NotContain("boom").And.NotContain(exceptionType.Name);
    }

    [Fact]
    public async Task ReadProjectFileLines_turns_an_unexpected_exception_into_an_ok_false_result()
    {
        FakeWorkspace workspace = new();
        workspace.Mock.Setup(w => w.ReadFileAsync(It.IsAny<Guid>(), "a.razor", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("scope"));

        using JsonDocument result = JsonDocument.Parse(await CreateTools(workspace).ReadProjectFileLines("a.razor"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetDeterministicContextFiles_ranks_blazor_sources_above_markdown_and_styles()
    {
        FakeWorkspace workspace = new();
        workspace.Add("README.md", "x");
        workspace.Add("wwwroot/app.css", "x");
        workspace.Add("Pages/Home.razor", "x");
        workspace.Add("App.csproj", "x");

        string json = await CreateTools(workspace).GetDeterministicContextFiles(null, 4);

        json.IndexOf("Home.razor", StringComparison.Ordinal).Should().BeLessThan(json.IndexOf("README.md", StringComparison.Ordinal));
        json.IndexOf("App.csproj", StringComparison.Ordinal).Should().BeLessThan(json.IndexOf("README.md", StringComparison.Ordinal));
    }

    private static string Numbered(int lines) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(i => $"const line{i} = {i};"));

    // ── ReadProjectFileLines ────────────────────────────────────────────────

    [Fact]
    public async Task ReadProjectFileLines_returns_a_numbered_range_with_totals_and_a_truncated_flag()
    {
        FakeWorkspace workspace = new();
        workspace.Add("src/big.ts", Numbered(1000));

        string json = await CreateTools(workspace).ReadProjectFileLines("src/big.ts", startLine: 10, lineCount: 3);

        using JsonDocument result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("startLine").GetInt32().Should().Be(10);
        result.RootElement.GetProperty("endLine").GetInt32().Should().Be(12);
        result.RootElement.GetProperty("totalLines").GetInt32().Should().Be(1000);
        result.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("totalChars").GetInt32().Should().BeGreaterThan(10_000);
        result.RootElement.GetProperty("content").GetString().Should().Be(
            "    10| const line10 = 10;\n    11| const line11 = 11;\n    12| const line12 = 12;\n");
    }

    [Fact]
    public async Task ReadProjectFileLines_reads_the_end_of_a_file_without_truncation()
    {
        FakeWorkspace workspace = new();
        workspace.Add("notes.md", "a\nb\nc");

        using JsonDocument result = JsonDocument.Parse(await CreateTools(workspace).ReadProjectFileLines("notes.md", 2, 100));

        result.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("endLine").GetInt32().Should().Be(3);
    }

    [Theory]
    [InlineData("missing.ts", "was not found in this project")]
    [InlineData("song.mp3", "not a readable text file")]
    public async Task The_read_tools_return_an_ok_false_result_instead_of_throwing(string reference, string expected)
    {
        FakeWorkspace workspace = new();
        workspace.Add("song.mp3", "xx", mime: "audio/mpeg");
        ProjectFileAgentTools tools = CreateTools(workspace);

        foreach (string json in new[] { await tools.ReadProjectFileLines(reference), await tools.GetProjectFileOutline(reference) })
        {
            using JsonDocument result = JsonDocument.Parse(json);
            result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
            result.RootElement.GetProperty("error").GetString().Should().Contain(expected);
        }
    }

    // ── GetProjectFileOutline ───────────────────────────────────────────────

    [Fact]
    public async Task GetProjectFileOutline_outlines_source_and_markdown_by_extension()
    {
        FakeWorkspace workspace = new();
        workspace.Add("src/Scene.tsx", "import React from 'react';\nexport const Scene = () => <div/>;\n");
        workspace.Add("PLAN.md", "# Plan\n## Step one\n");
        ProjectFileAgentTools tools = CreateTools(workspace);

        using JsonDocument code = JsonDocument.Parse(await tools.GetProjectFileOutline("src/Scene.tsx"));
        code.RootElement.GetProperty("symbols")[0].GetProperty("name").GetString().Should().Be("Scene");

        using JsonDocument markdown = JsonDocument.Parse(await tools.GetProjectFileOutline("PLAN.md"));
        markdown.RootElement.GetProperty("headings").GetArrayLength().Should().Be(2);
    }

    // ── EditProjectFile ─────────────────────────────────────────────────────

    [Fact]
    public async Task EditProjectFile_saves_a_new_version_beside_the_original_and_leaves_the_original_untouched()
    {
        FakeWorkspace workspace = new();
        ProjectWorkspaceFile original = workspace.Add("src/Title.tsx", "const color = 'red';\nconst size = 1;\n", mime: "text/plain");

        string json = await CreateTools(workspace).EditProjectFile("src/Title.tsx", "'red'", "'blue'");

        using JsonDocument result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("replacements").GetInt32().Should().Be(1);
        result.RootElement.GetProperty("previousFileId").GetGuid().Should().Be(original.Id);

        workspace.Writes.Should().ContainSingle();
        var write = workspace.Writes[0];
        write.Content.Should().Be("const color = 'blue';\nconst size = 1;\n");
        write.Category.Should().Be("agentFiles", "an edit never writes into the user's own uploads");
        write.OriginalPath.Should().Be("src/Title.tsx");
        write.ContentType.Should().Be("text/plain");

        workspace.Files[original.Id].Text.Should().Be("const color = 'red';\nconst size = 1;\n");
    }

    [Fact]
    public async Task EditProjectFile_edits_the_newest_version_when_called_twice_by_name()
    {
        FakeWorkspace workspace = new();
        workspace.Add("a.ts", "one two three", uploadedAt: DateTime.UtcNow.AddHours(-1));
        ProjectFileAgentTools tools = CreateTools(workspace);

        await tools.EditProjectFile("a.ts", "two", "2");
        string again = await tools.EditProjectFile("a.ts", "2", "II");

        using JsonDocument result = JsonDocument.Parse(again);
        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        workspace.Writes.Select(w => w.Content).Should().Equal("one 2 three", "one II three");
    }

    [Fact]
    public async Task EditProjectFile_fails_cleanly_and_writes_nothing_when_the_snippet_is_missing_or_ambiguous()
    {
        FakeWorkspace workspace = new();
        workspace.Add("a.ts", "foo\nfoo\nbar\n");
        ProjectFileAgentTools tools = CreateTools(workspace);

        using JsonDocument missing = JsonDocument.Parse(await tools.EditProjectFile("a.ts", "nope", "x"));
        missing.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        missing.RootElement.GetProperty("error").GetString().Should().Contain("not found");

        using JsonDocument ambiguous = JsonDocument.Parse(await tools.EditProjectFile("a.ts", "foo", "x"));
        ambiguous.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        ambiguous.RootElement.GetProperty("error").GetString().Should().Contain("2 times");

        workspace.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task EditProjectFile_replaceAll_replaces_every_occurrence()
    {
        FakeWorkspace workspace = new();
        workspace.Add("a.ts", "foo\nfoo\nbar\n");

        using JsonDocument result = JsonDocument.Parse(
            await CreateTools(workspace).EditProjectFile("a.ts", "foo", "x", replaceAll: true));

        result.RootElement.GetProperty("replacements").GetInt32().Should().Be(2);
        workspace.Writes.Single().Content.Should().Be("x\nx\nbar\n");
    }

    [Fact]
    public async Task EditProjectFile_reports_an_unknown_reference_and_refuses_a_binary_file()
    {
        FakeWorkspace workspace = new();
        workspace.Add("song.mp3", "xx", mime: "audio/mpeg");
        ProjectFileAgentTools tools = CreateTools(workspace);

        using JsonDocument unknown = JsonDocument.Parse(await tools.EditProjectFile("ghost.ts", "a", "b"));
        unknown.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        unknown.RootElement.GetProperty("error").GetString().Should().Contain("ListProjectFiles");

        using JsonDocument binary = JsonDocument.Parse(await tools.EditProjectFile("song.mp3", "a", "b"));
        binary.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        binary.RootElement.GetProperty("error").GetString().Should().Contain("not a readable text file");

        workspace.Writes.Should().BeEmpty();
    }

    // ── ReadProjectFile cap ─────────────────────────────────────────────────

    [Fact]
    public async Task ReadProjectFile_still_returns_an_ordinary_file_whole()
    {
        FakeWorkspace workspace = new();
        string text = Numbered(500);
        workspace.Add("src/ok.ts", text);

        (await CreateTools(workspace).ReadProjectFile("src/ok.ts")).Should().Be(text);
    }

    [Fact]
    public async Task ReadProjectFile_cuts_an_oversized_file_at_a_line_boundary_and_says_how_to_continue()
    {
        FakeWorkspace workspace = new();
        string text = Numbered(20_000);
        text.Length.Should().BeGreaterThan(ProjectFileAgentTools.MaxReadProjectFileChars);
        workspace.Add("lock.json", text);

        string result = await CreateTools(workspace).ReadProjectFile("lock.json");

        result.Length.Should().BeLessThan(ProjectFileAgentTools.MaxReadProjectFileChars + 600);
        result.Should().Contain("[truncated: showing the first");
        result.Should().Contain("ReadProjectFileLines");
        result.Should().Contain("of 20000)", "the note states the file's total line count");

        // The cut is on a line boundary: the last kept line is complete, and the note names the next.
        int note = result.IndexOf("\n\n[truncated", StringComparison.Ordinal);
        string kept = result[..note];
        kept.Split('\n').Last().Should().MatchRegex(@"^const line\d+ = \d+;$");
        int keptLines = kept.Split('\n').Length;
        result.Should().Contain($"startLine={keptLines + 1}");
    }
}
