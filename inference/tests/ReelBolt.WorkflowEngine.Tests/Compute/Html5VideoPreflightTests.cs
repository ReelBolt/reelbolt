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
/// <see cref="Html5VideoPreflight"/>: a composition that plays a clip through remotion's HTML5 <c>&lt;Video&gt;</c>
/// (or <c>&lt;Html5Video&gt;</c>) is refused before the render, because the HTML5 element captures stale frames
/// (production reel 2026-11-05, sc6: 56 of 145 frames showed source frame i-2). <c>&lt;OffthreadVideo&gt;</c>,
/// <c>@remotion/media</c>, comments and a <c>Video</c> imported from anywhere else are never reported, and the
/// check fails open.
/// </summary>
public class Html5VideoPreflightTests
{
    [Fact]
    public void The_html5_video_of_the_production_reel_is_found_with_its_line()
    {
        const string source = "import React from 'react';\n" +
                              "import {AbsoluteFill, Sequence, Video, staticFile} from 'remotion';\n" +
                              "export const Sc6: React.FC = () => (\n" +
                              "  <Sequence from={405} durationInFrames={180}>\n" +
                              "    <Video src={staticFile('caffe-lume-ad-clean.mp4')} endAt={180} />\n" +
                              "  </Sequence>\n" +
                              ");\n";

        IReadOnlyList<Html5VideoUse> found = Html5VideoPreflight.Scan("src/Sc6.tsx", source);

        found.Should().ContainSingle();
        found[0].Should().Be(new Html5VideoUse("src/Sc6.tsx", 5, "Video"));
    }

    [Fact]
    public void Html5Video_aliases_and_self_closing_elements_are_found()
    {
        const string source = "import {Html5Video as Clip, Video as V} from \"remotion\";\n" +
                              "const a = <Clip src={s}/>;\n" +
                              "const b = (\n  <V\n    src={s}\n  />\n);\n";

        Html5VideoPreflight.Scan("src/A.tsx", source)
            .Select(u => (u.Component, u.Line))
            .Should().Equal(("Html5Video", 2), ("Video", 4));
    }

    [Theory]
    [InlineData("import {OffthreadVideo, staticFile} from 'remotion';\nconst a = <OffthreadVideo src={staticFile('a.mp4')} />;")]
    [InlineData("import {Video} from '@remotion/media';\nconst a = <Video src={s} />;")]
    [InlineData("import {Video} from './Video';\nconst a = <Video src={s} />;")]
    [InlineData("import {Video} from 'remotion';\n// const a = <Video src={s} />;\n/* <Video src={s} /> */")]
    [InlineData("import {Video} from 'remotion';\nconst a = <VideoFrame src={s} />;")]
    [InlineData("import {AbsoluteFill} from 'remotion';\nconst a = <Video src={s} />;")]
    public void What_is_not_remotions_html5_video_is_not_reported(string source)
    {
        Html5VideoPreflight.Scan("src/Scene.tsx", source).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_use_across_src_is_found_and_other_folders_are_not_read()
    {
        FakeTree tree = new()
        {
            ["src/Root.tsx"] = "import {OffthreadVideo} from 'remotion';\nexport const R = () => <OffthreadVideo src={s} />;",
            ["src/scenes/Ad.tsx"] = "import {Video} from 'remotion';\n\nexport const Ad = () => <Video src={s} />;",
            ["src/node_modules/x.tsx"] = "import {Video} from 'remotion';\nconst a = <Video src={s} />;",
            ["public/Clip.tsx"] = "import {Video} from 'remotion';\nconst a = <Video src={s} />;",
        };

        IReadOnlyList<Html5VideoUse>? uses = await Html5VideoPreflight.FindAsync(tree.Session(), CancellationToken.None);

        uses.Should().NotBeNull();
        uses!.Should().ContainSingle().Which.Should().Be(new Html5VideoUse("src/scenes/Ad.tsx", 3, "Video"));
    }

    [Fact]
    public async Task A_project_without_src_has_nothing_to_refuse()
    {
        FakeTree tree = new() { ["public/a.mp4"] = "x" };

        (await Html5VideoPreflight.FindAsync(tree.Session(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_source_throws_so_the_tool_can_proceed()
    {
        Mock<ISandboxSession> session = new();
        session.Setup(s => s.ListFilesAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SandboxFileEntry("Root.tsx", false, 40, DateTime.UtcNow)]);
        session.Setup(s => s.ReadTextAsync("src/Root.tsx", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SandboxRequestFailedException(HttpStatusCode.InternalServerError, "boom"));

        await FluentActions.Awaiting(() => Html5VideoPreflight.FindAsync(session.Object, CancellationToken.None))
            .Should().ThrowAsync<SandboxRequestFailedException>("the tool turns any failure of the scan into 'proceed'");
    }

    [Fact]
    public void The_refusal_names_each_use_and_says_to_use_OffthreadVideo()
    {
        string message = Html5VideoPreflight.RefusalMessage([new Html5VideoUse("src/Sc6.tsx", 5, "Video")]);

        message.Should().StartWith("HTML5_VIDEO_IN_RENDER:");
        message.Should().Contain("<Video> in src/Sc6.tsx:5");
        message.Should().Contain("<OffthreadVideo>").And.Contain("muted").And.Contain("<Loop");
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
