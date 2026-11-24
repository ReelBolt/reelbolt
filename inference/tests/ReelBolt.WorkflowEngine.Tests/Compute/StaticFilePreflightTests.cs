using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// <see cref="StaticFilePreflight"/>: a <c>staticFile('...')</c> whose target was never staged into
/// <c>public/</c> is found before Remotion runs (production run 36df4287 died in the renderer with
/// "Could not extract frame" instead). Only plain literals outside comments count, and the check fails
/// open: when it cannot read the project, it answers null and the command runs as before.
/// </summary>
public class StaticFilePreflightTests
{
    [Fact]
    public void Single_and_double_quoted_literals_are_found_with_their_line()
    {
        const string source = "import {staticFile} from 'remotion';\n" +
                              "const a = staticFile('ad-01.mp4');\n" +
                              "const b = staticFile( \"brand/logo.png\" );\n" +
                              "const c = staticFile('./voiceover/scene-1.wav');\n" +
                              "const d = staticFile('/fonts/Brand.woff2');\n";

        IReadOnlyList<MissingStaticFile> found = StaticFilePreflight.Scan("src/Scene.tsx", source);

        found.Select(f => (f.StaticPath, f.Line)).Should().Equal(
            ("ad-01.mp4", 2), ("brand/logo.png", 3), ("voiceover/scene-1.wav", 4), ("fonts/Brand.woff2", 5));
        found.Should().OnlyContain(f => f.SourceFile == "src/Scene.tsx");
    }

    [Theory]
    [InlineData("// const a = staticFile('gone.mp4');")]
    [InlineData("/* staticFile('gone.mp4') */")]
    [InlineData("const a = staticFile(`clips/${name}.mp4`);")]
    [InlineData("const a = staticFile(name);")]
    [InlineData("const a = staticFile('clips/' + name);")]
    [InlineData("const a = staticFile('../outside.mp4');")]
    [InlineData("const a = staticFile('https://cdn.example.com/a.mp4');")]
    [InlineData("const a = staticFile('frames/');")]
    [InlineData("const a = staticFile('clip%20one.mp4');")]
    public void What_cannot_be_judged_is_not_reported(string source)
    {
        StaticFilePreflight.Scan("src/Scene.tsx", source).Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_targets_are_named_and_present_ones_are_not()
    {
        FakeTree tree = new()
        {
            ["src/Root.tsx"] = "export const R = () => <Video src={staticFile('clip.mp4')} />;",
            ["src/scenes/Ad.tsx"] = "const a = staticFile('ad-01.mp4');\nconst l = staticFile('brand/logo.png');\nconst x = staticFile('brand/missing.svg');\nconst g = staticFile('generated');",
            ["public/clip.mp4"] = "x",
            ["public/brand/logo.png"] = "x",
            ["public/generated/v1.mp4"] = "x",
        };

        IReadOnlyList<MissingStaticFile>? missing = await StaticFilePreflight.FindMissingAsync(tree.Session(), CancellationToken.None);

        missing.Should().NotBeNull();
        missing!.Select(m => m.StaticPath).Should().Equal("ad-01.mp4", "brand/missing.svg");
        missing[0].SourceFile.Should().Be("src/scenes/Ad.tsx");
        missing[0].Line.Should().Be(1);
    }

    [Fact]
    public async Task A_project_whose_targets_are_all_staged_has_nothing_missing()
    {
        FakeTree tree = new()
        {
            ["src/Root.tsx"] = "const v = staticFile('clip.mp4');",
            ["public/clip.mp4"] = "x",
        };

        (await StaticFilePreflight.FindMissingAsync(tree.Session(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_with_no_public_folder_reports_every_target()
    {
        FakeTree tree = new() { ["src/Root.tsx"] = "const v = staticFile('clip.mp4');" };

        (await StaticFilePreflight.FindMissingAsync(tree.Session(), CancellationToken.None))!
            .Select(m => m.StaticPath).Should().Equal("clip.mp4");
    }

    [Fact]
    public async Task An_unreadable_source_makes_the_check_fail_open()
    {
        Mock<ISandboxSession> session = new();
        session.Setup(s => s.ListFilesAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SandboxFileEntry("Root.tsx", false, 40, DateTime.UtcNow)]);
        session.Setup(s => s.ReadTextAsync("src/Root.tsx", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SandboxRequestFailedException(HttpStatusCode.InternalServerError, "boom"));

        await FluentActions.Awaiting(() => StaticFilePreflight.FindMissingAsync(session.Object, CancellationToken.None))
            .Should().ThrowAsync<SandboxRequestFailedException>("the tool turns any failure of the scan into 'proceed'");
    }

    [Fact]
    public void The_refusal_names_each_path_and_how_to_stage_it()
    {
        string message = StaticFilePreflight.RefusalMessage([new MissingStaticFile("ad-01.mp4", "src/Ad.tsx", 3)]);

        message.Should().StartWith("MISSING_STATIC_FILE:");
        message.Should().Contain("public/ad-01.mp4 (staticFile in src/Ad.tsx:3)");
        message.Should().Contain("StageProjectFile").And.Contain("ListProjectFiles");
    }

    /// <summary>Files keyed by workspace path; directories are implied, a missing one is a 404.</summary>
    private sealed class FakeTree : Dictionary<string, string>
    {
        public FakeTree() : base(StringComparer.Ordinal) { }

        public ISandboxSession Session()
        {
            Mock<ISandboxSession> session = new();
            session.Setup(s => s.ListFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string path, CancellationToken _) =>
                {
                    string prefix = path.TrimEnd('/') + "/";
                    var entries = new Dictionary<string, SandboxFileEntry>(StringComparer.Ordinal);
                    foreach ((string file, string content) in this)
                    {
                        if (!file.StartsWith(prefix, StringComparison.Ordinal))
                            continue;
                        string rest = file[prefix.Length..];
                        int slash = rest.IndexOf('/');
                        string name = slash < 0 ? rest : rest[..slash];
                        entries[name] = new SandboxFileEntry(name, slash >= 0, slash < 0 ? content.Length : 0, DateTime.UtcNow);
                    }

                    if (entries.Count == 0)
                        throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "not found");
                    return Task.FromResult<IReadOnlyList<SandboxFileEntry>>([.. entries.Values]);
                });
            session.Setup(s => s.ReadTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string path, CancellationToken _) => Task.FromResult(new SandboxTextFile(path, this[path])));
            return session.Object;
        }
    }
}
