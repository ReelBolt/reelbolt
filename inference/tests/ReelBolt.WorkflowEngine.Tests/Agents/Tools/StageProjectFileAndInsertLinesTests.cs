using ReelBolt.Shared.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// <see cref="ReactRemotionSandboxTools.StageProjectFile"/> (copy a project file's bytes into the
/// sandbox's <c>public/</c> folder) and <see cref="ReactRemotionSandboxTools.InsertSandboxFileLines"/>
/// (insert lines at a position), plus the pure helpers behind them.
/// </summary>
public class StageProjectFileAndInsertLinesTests
{
    // --- SandboxTextEditor.InsertAt ---------------------------------------------------------------

    [Theory]
    [InlineData("a\nb\nc\n", 1, "x", "x\na\nb\nc\n")]
    [InlineData("a\nb\nc\n", 2, "x\ny", "a\nx\ny\nb\nc\n")]
    [InlineData("a\nb\nc\n", 3, "x\n", "a\nb\nx\nc\n")]
    [InlineData("a\nb\nc\n", -1, "x", "a\nb\nc\nx\n")]
    [InlineData("a\nb\nc\n", 4, "x", "a\nb\nc\nx\n")]
    [InlineData("a\nb", -1, "x", "a\nb\nx")]
    [InlineData("a\nb", 3, "x\n", "a\nb\nx")]
    [InlineData("", 1, "x", "x\n")]
    [InlineData("", -1, "x", "x\n")]
    public void InsertAt_puts_the_text_on_a_line_boundary_and_changes_no_existing_line(
        string original, int beforeLine, string text, string expected)
    {
        SandboxEditOutcome outcome = SandboxTextEditor.InsertAt(original, beforeLine, text);

        outcome.Ok.Should().BeTrue(outcome.Error);
        outcome.Content.Should().Be(expected);
    }

    [Fact]
    public void InsertAt_writes_the_files_own_line_ending()
    {
        SandboxEditOutcome outcome = SandboxTextEditor.InsertAt("a\r\nb\r\n", 2, "x\ny");

        outcome.Content.Should().Be("a\r\nx\r\ny\r\nb\r\n");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(6)]
    public void InsertAt_refuses_a_position_outside_the_file_and_names_the_valid_range(int beforeLine)
    {
        // "a\nb\nc\n" is four lines as ReadSandboxFileLines numbers it (the last one empty).
        SandboxEditOutcome outcome = SandboxTextEditor.InsertAt("a\nb\nc\n", beforeLine, "x");

        outcome.Ok.Should().BeFalse();
        outcome.Content.Should().BeNull();
        outcome.Error.Should().Contain("1..5").And.Contain("-1");
    }

    [Fact]
    public void InsertAt_refuses_empty_text()
    {
        SandboxTextEditor.InsertAt("a\n", 1, "").Ok.Should().BeFalse();
    }

    // --- InsertSandboxFileLines -------------------------------------------------------------------

    [Fact]
    public async Task InsertSandboxFileLines_writes_the_file_back_with_the_lines_inserted()
    {
        RecordingSession session = new() { Text = "import a from './a.tsx';\nexport const X = 1;\n" };
        ReactRemotionSandboxTools tool = Build(session, files: []);

        string json = await tool.InsertSandboxFileLines("src/x.tsx", 2, "import b from './b.tsx';");

        using JsonDocument result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("linesInserted").GetInt32().Should().Be(1);
        session.Writes.Should().ContainSingle();
        session.Writes[0].Should().Be(("src/x.tsx", "import a from './a.tsx';\nimport b from './b.tsx';\nexport const X = 1;\n"));
    }

    [Fact]
    public async Task InsertSandboxFileLines_reports_a_bad_position_as_data_and_writes_nothing()
    {
        RecordingSession session = new() { Text = "one\n" };
        ReactRemotionSandboxTools tool = Build(session, files: []);

        string json = await tool.InsertSandboxFileLines("src/x.tsx", 9, "two");

        using JsonDocument result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("error").GetString().Should().Contain("outside the file");
        session.Writes.Should().BeEmpty();
    }

    // --- StageProjectFile -------------------------------------------------------------------------

    [Fact]
    public async Task StageProjectFile_copies_the_object_into_public_through_a_presigned_get()
    {
        ProjectWorkspaceFile logo = File("logo.png", "userFiles/logo.png", "image/png");
        RecordingSession session = new();
        FakePresignedUrlService presigner = new();
        ReactRemotionSandboxTools tool = Build(session, [logo], presigner);

        string json = await tool.StageProjectFile(logo.Id.ToString(), "public/brand/logo.png");

        using JsonDocument result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("path").GetString().Should().Be("public/brand/logo.png");
        result.RootElement.GetProperty("staticFile").GetString().Should().Be("brand/logo.png");

        presigner.Gets.Should().ContainSingle().Which.Key.Should().Be(logo.StorageKey);
        session.Stages.Should().ContainSingle();
        session.Stages[0].Path.Should().Be("public/brand/logo.png");
        session.Stages[0].Url.Should().Be("https://objects.test/get?key=" + Uri.EscapeDataString(logo.StorageKey));
    }

    [Fact]
    public async Task StageProjectFile_defaults_to_public_and_the_files_own_name()
    {
        ProjectWorkspaceFile font = File("Brand Font.woff2", "userFiles/font.woff2", "font/woff2");
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [font], new FakePresignedUrlService());

        string json = await tool.StageProjectFile("Brand Font.woff2");

        using JsonDocument result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("path").GetString().Should().Be("public/Brand Font.woff2");
        session.Stages.Should().ContainSingle().Which.Path.Should().Be("public/Brand Font.woff2");
    }

    [Fact]
    public async Task StageProjectFile_refuses_an_unknown_reference_without_touching_the_sandbox()
    {
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [File("logo.png", "userFiles/logo.png", "image/png")], new FakePresignedUrlService());

        string json = await tool.StageProjectFile("missing.png");

        JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        session.Stages.Should().BeEmpty();
    }

    [Fact]
    public async Task StageProjectFile_names_the_closest_real_files_on_a_miss_and_stages_none_of_them()
    {
        // Production run 36df4287: the Author asked for made-up names and got a bare "no match".
        ProjectWorkspaceFile walking = File("man-walking.mp4", "userFiles/man-walking.mp4", "video/mp4") with { DirectoryPath = "footage" };
        ProjectWorkspaceFile ad = File("ad_01.mp4", "userFiles/ad_01.mp4", "video/mp4");
        ProjectWorkspaceFile[] files =
        [
            walking, ad,
            File("logo.png", "userFiles/logo.png", "image/png"),
            File("notes.txt", "userFiles/notes.txt", "text/plain"),
            File("script.md", "userFiles/script.md", "text/markdown"),
            File("brief.pdf", "userFiles/brief.pdf", "application/pdf"),
            File("budget.xlsx", "userFiles/budget.xlsx", "application/octet-stream"),
        ];
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, files, new FakePresignedUrlService());

        using JsonDocument first = JsonDocument.Parse(await tool.StageProjectFile("ad-01.mp4"));
        using JsonDocument second = JsonDocument.Parse(await tool.StageProjectFile("ad-follow-man-walking.mp4"));

        first.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        first.RootElement.GetProperty("error").GetString().Should().Contain("Nothing was staged");
        JsonElement closest = first.RootElement.GetProperty("closestFiles");
        closest.GetArrayLength().Should().Be(ReactRemotionSandboxTools.MaxClosestFiles);
        closest[0].GetProperty("fileName").GetString().Should().Be("ad_01.mp4");
        closest[0].GetProperty("id").GetGuid().Should().Be(ad.Id);

        JsonElement best = second.RootElement.GetProperty("closestFiles")[0];
        best.GetProperty("fileName").GetString().Should().Be("man-walking.mp4");
        best.GetProperty("directoryPath").GetString().Should().Be("footage");
        best.GetProperty("id").GetGuid().Should().Be(walking.Id);

        session.Stages.Should().BeEmpty("a near name is only suggested, never staged in its place");
    }

    [Fact]
    public void ResolveProjectFile_accepts_directory_and_name_but_never_a_near_miss()
    {
        // No original path stored: the folder lives only in DirectoryPath.
        ProjectWorkspaceFile clip = File("walk.mp4", "userFiles/walk.mp4", "video/mp4") with { DirectoryPath = "footage/day1/" };
        ProjectWorkspaceFile withPath = File("intro.mp4", "userFiles/intro.mp4", "video/mp4") with { OriginalPath = "clips/intro.mp4", DirectoryPath = "clips" };
        ProjectWorkspaceFile[] files = [clip, withPath];

        ReactRemotionSandboxTools.ResolveProjectFile(files, "footage/day1/walk.mp4").Should().BeSameAs(clip);
        ReactRemotionSandboxTools.ResolveProjectFile(files, "./footage/day1/walk.mp4").Should().BeSameAs(clip);
        ReactRemotionSandboxTools.ResolveProjectFile(files, "clips/intro.mp4").Should().BeSameAs(withPath);
        ReactRemotionSandboxTools.ResolveProjectFile(files, "Footage/day1/walk.mp4").Should().BeNull();
        ReactRemotionSandboxTools.ResolveProjectFile(files, "footage/walk.mp4").Should().BeNull();
        ReactRemotionSandboxTools.ResolveProjectFile(files, "walk.MP4").Should().BeNull();
    }

    [Fact]
    public async Task StageProjectFile_refuses_a_file_over_the_staging_cap()
    {
        ProjectWorkspaceFile big = File("big.mp4", "userFiles/big.mp4", "video/mp4") with
        {
            SizeBytes = ReactRemotionSandboxTools.DefaultSourceMaxBytes + 1
        };
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [big], new FakePresignedUrlService());

        string json = await tool.StageProjectFile("big.mp4");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().Should().Contain("limit");
        session.Stages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("payload.js", null)]
    [InlineData("Comp.tsx", null)]
    [InlineData("mod.wasm", null)]
    [InlineData("page.html", null)]
    [InlineData("payload.js", "public/payload.png")]
    [InlineData("logo.png", "public/logo.mjs")]
    public async Task StageProjectFile_refuses_code_and_data_files_whatever_they_are_renamed_to(string name, string? sandboxPath)
    {
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [File(name, "userFiles/" + name, "application/octet-stream")], new FakePresignedUrlService());

        string json = await tool.StageProjectFile(name, sandboxPath);

        using JsonDocument result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("error").GetString().Should().Contain("WriteSandboxFile");
        session.Stages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("public/../package.json")]
    [InlineData("src/root.tsx/../../x")]
    [InlineData("public/a/./b.png")]
    [InlineData("public/")]
    [InlineData("public/a//b.png")]
    [InlineData("public/$(rm).png")]
    public async Task StageProjectFile_refuses_a_destination_that_is_not_a_plain_path_under_public(string sandboxPath)
    {
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [File("logo.png", "userFiles/logo.png", "image/png")], new FakePresignedUrlService());

        string json = await tool.StageProjectFile("logo.png", sandboxPath);

        JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        session.Stages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("brand/logo.png", "public/brand/logo.png")]
    [InlineData("/brand/logo.png", "public/brand/logo.png")]
    [InlineData("./public/logo.png", "public/logo.png")]
    [InlineData("public\\brand\\logo.png", "public/brand/logo.png")]
    public void NormalizeStagedPath_keeps_every_destination_inside_public(string requested, string expected)
    {
        ReactRemotionSandboxTools.NormalizeStagedPath(requested, "logo.png", out string? error)
            .Should().Be(expected, error);
    }

    [Fact]
    public void NormalizeStagedPath_sanitizes_an_unusual_original_file_name()
    {
        ReactRemotionSandboxTools.NormalizeStagedPath(null, "dir/lo$go?.png", out _)
            .Should().Be("public/lo_go_.png");
        ReactRemotionSandboxTools.NormalizeStagedPath(null, "..", out _)
            .Should().Be("public/file");
    }

    [Fact]
    public void ResolveProjectFile_prefers_id_then_storage_key_then_the_newest_file_of_that_name()
    {
        ProjectWorkspaceFile older = File("logo.png", "userFiles/old.png", "image/png") with { UploadedAt = DateTime.UtcNow.AddDays(-1) };
        ProjectWorkspaceFile newer = File("logo.png", "userFiles/new.png", "image/png");
        ProjectWorkspaceFile[] files = [older, newer];

        ReactRemotionSandboxTools.ResolveProjectFile(files, older.Id.ToString()).Should().BeSameAs(older);
        ReactRemotionSandboxTools.ResolveProjectFile(files, older.StorageKey).Should().BeSameAs(older);
        ReactRemotionSandboxTools.ResolveProjectFile(files, "logo.png").Should().BeSameAs(newer);
        ReactRemotionSandboxTools.ResolveProjectFile(files, Guid.NewGuid().ToString()).Should().BeNull();
    }

    private static IAmazonS3 S3Returning(string body)
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(c => c.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Amazon.S3.Model.GetObjectResponse
            {
                ResponseStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)),
            });
        return s3.Object;
    }

    [Fact]
    public async Task StageProjectFile_json_is_refused_while_tool_screening_is_on()
    {
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [File("data.json", "userFiles/data.json", "application/json")],
            new FakePresignedUrlService(), screeningOn: true);

        using JsonDocument result = JsonDocument.Parse(await tool.StageProjectFile("data.json"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("error").GetString().Should().Be(
            "code and data files (.js, .ts, .tsx, .json, .wasm, .html, ...) cannot be staged; read the file with " +
            "ReadProjectFile and write it with WriteSandboxFile instead");
        session.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task StageProjectFile_skips_a_utf8_bom_before_parsing_json()
    {
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [File("data.json", "userFiles/data.json", "application/json")],
            new FakePresignedUrlService(), s3: S3Returning("\uFEFF{\"a\":1}"));

        using JsonDocument result = JsonDocument.Parse(await tool.StageProjectFile("data.json"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        session.ByteWrites.Should().ContainSingle().Which.Content.Should().StartWith((byte)'{');
    }

    [Fact]
    public async Task StageProjectFile_stages_valid_json_while_tool_screening_is_off()
    {
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [File("data.json", "userFiles/data.json", "application/json")],
            new FakePresignedUrlService(), s3: S3Returning("{\"a\":[1,2]}"));

        using JsonDocument result = JsonDocument.Parse(await tool.StageProjectFile("data.json"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        session.ByteWrites.Should().ContainSingle().Which.Path.Should().Be("public/data.json");
    }

    [Fact]
    public async Task StageProjectFile_rejects_invalid_json_and_oversized_json()
    {
        RecordingSession session = new();
        ReactRemotionSandboxTools bad = Build(session, [File("data.json", "userFiles/data.json", "application/json")],
            new FakePresignedUrlService(), s3: S3Returning("{not json"));
        using JsonDocument invalid = JsonDocument.Parse(await bad.StageProjectFile("data.json"));
        invalid.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        invalid.RootElement.GetProperty("error").GetString().Should().Contain("not valid JSON");

        ProjectWorkspaceFile big = new(Guid.NewGuid(), ProjectId, "big.json", null, "userFiles",
            $"projects/{ProjectId}/userFiles/big.json", "application/json", 9L * 1024 * 1024, DateTime.UtcNow, null);
        ReactRemotionSandboxTools huge = Build(session, [big], new FakePresignedUrlService(), s3: S3Returning("{}"));
        using JsonDocument over = JsonDocument.Parse(await huge.StageProjectFile("big.json"));
        over.RootElement.GetProperty("error").GetString().Should().Contain("8 MB");
        session.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task StageProjectFile_still_refuses_code_renamed_to_json_destination_when_source_is_code()
    {
        RecordingSession session = new();
        ReactRemotionSandboxTools tool = Build(session, [File("payload.js", "userFiles/payload.js", "text/javascript")],
            new FakePresignedUrlService());

        using JsonDocument result = JsonDocument.Parse(await tool.StageProjectFile("payload.js", "public/payload.json"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        session.ByteWrites.Should().BeEmpty();
    }

    // --- Fixture ----------------------------------------------------------------------------------

    private static readonly Guid ProjectId = Guid.NewGuid();

    private static ProjectWorkspaceFile File(string name, string keySuffix, string mime) => new(
        Guid.NewGuid(), ProjectId, name, null, "userFiles",
        $"projects/{ProjectId}/{keySuffix}", mime, 1234, DateTime.UtcNow, null);

    private static ReactRemotionSandboxTools Build(
        RecordingSession session, IReadOnlyList<ProjectWorkspaceFile> files, IPresignedUrlService? presigner = null,
        bool screeningOn = false, IAmazonS3? s3 = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MinIO:BucketName"] = "reelforge",
                ["WorkflowEngine:Guardrails:ToolScreening"] = screeningOn ? "On" : "Off",
            })
            .Build();

        WorkflowExecutionContextAccessor accessor = new();
        accessor.BeginScope(session.ExecutionId, ProjectId, "correlation", null);

        Mock<IProjectFileWorkspace> workspace = new();
        workspace
            .Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        return new ReactRemotionSandboxTools(
            Mock.Of<IHttpClientFactory>(),
            accessor,
            workspace.Object,
            s3 ?? new Mock<IAmazonS3>(MockBehavior.Strict).Object,
            configuration,
            NullLogger<ReactRemotionSandboxTools>.Instance,
            Mock.Of<IServiceScopeFactory>(),
            sandboxSessions: new FixedSessionFactory(session),
            presignedUrls: presigner);
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>A session that holds one text file and records every write and stage; nothing else is used.</summary>
    private sealed class RecordingSession : ISandboxSession
    {
        public Guid ExecutionId { get; } = Guid.NewGuid();

        public string Text { get; set; } = string.Empty;

        public List<(string Path, string Content)> Writes { get; } = [];

        public List<SandboxStageRequest> Stages { get; } = [];

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxTextFile(path, Text));

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            Writes.Add((path, content));
            return Task.FromResult(new SandboxActionResult(true));
        }

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken)
        {
            Stages.Add(request);
            return Task.FromResult(new SandboxTransfer(4, null));
        }

        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public List<(string Path, byte[] Content)> ByteWrites { get; } = [];

        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
        {
            ByteWrites.Add((path, content));
            return Task.CompletedTask;
        }
        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
