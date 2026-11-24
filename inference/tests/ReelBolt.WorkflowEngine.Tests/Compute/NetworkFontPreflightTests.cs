using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// <see cref="NetworkFontPreflight"/>: the sandbox has no internet, so a composition that loads a font
/// from the network fails mid-render. The preflight reads the project's sources first and refuses such
/// a project at once, naming the file and line. Both directions are pinned (Review Focus 2): every
/// kind of network font LOAD is refused, and a font host or package name that only appears in a
/// comment, in copy or in a string that loads nothing is not, because a false refusal blocks every render.
/// </summary>
public class NetworkFontPreflightTests
{
    // ── Refused: network font loads ────────────────────────────────────────

    [Theory]
    [InlineData("src/Title.tsx", "import {loadFont} from \"@remotion/google-fonts/Inter\";", "@remotion/google-fonts")]
    [InlineData("src/Title.tsx", "import { loadFont as load } from '@remotion/google-fonts/PlayfairDisplay';", "@remotion/google-fonts")]
    [InlineData("src/Title.tsx", "import '@remotion/google-fonts/Inter';", "@remotion/google-fonts")]
    [InlineData("src/Title.tsx", "const m = await import(\"@remotion/google-fonts/Inter\");", "@remotion/google-fonts")]
    [InlineData("src/Title.js", "const { loadFont } = require('@remotion/google-fonts/Inter');", "@remotion/google-fonts")]
    [InlineData("src/fonts.ts", "export { loadFont } from \"@remotion/google-fonts/Inter\";", "@remotion/google-fonts")]
    [InlineData("src/style.css", "@import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;700');", "@import")]
    [InlineData("src/style.css", "@import \"https://cdn.example.com/brand.css\";", "@import")]
    [InlineData("src/style.css", "@import url(//fonts.googleapis.com/css2?family=Inter);", "@import")]
    [InlineData("src/style.css", "@font-face { font-family: Brand; src: url(https://cdn.example.com/brand.woff2) format('woff2'); }", "@font-face")]
    [InlineData("src/Title.tsx", "<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css2?family=Inter\" />", "fonts.googleapis.com")]
    [InlineData("src/Title.tsx", "const css = 'body { background: url(https://fonts.gstatic.com/s/inter/v1/a.woff2) }';", "fonts.gstatic.com")]
    [InlineData("src/Title.tsx", "await fetch('https://use.typekit.net/abc1234.css');", "use.typekit.net")]
    [InlineData("src/Title.tsx", "const face = new FontFace('Brand', 'url(https://cdn.example.com/brand.woff2)');", "FontFace")]
    [InlineData("src/Title.tsx", "loadFont({ family: 'Brand', url: 'https://cdn.example.com/brand.woff2', weight: '700' });", "loadFont")]
    [InlineData("src/Title.mjs", "const src = \"url('https://cdn.example.com/brand.ttf')\";", "font file")]
    public void A_network_font_load_is_found(string path, string source, string what)
    {
        NetworkFontLoad? load = NetworkFontPreflight.Scan(path, source);

        load.Should().NotBeNull();
        load!.Path.Should().Be(path);
        load.Line.Should().Be(1);
        load.What.Should().Contain(what);
    }

    [Fact]
    public void A_font_face_block_spanning_lines_is_reported_at_its_remote_url()
    {
        const string css = """
            body { margin: 0; }

            @font-face {
              font-family: "Brand";
              src: url("https://cdn.example.com/brand.woff2") format("woff2");
            }
            """;

        NetworkFontPreflight.Scan("src/style.css", css)!.Line.Should().Be(5);
    }

    [Fact]
    public void A_template_literal_style_tag_with_a_remote_import_is_found()
    {
        const string tsx = """
            export const Title = () => (
              <>
                <style>{`
                  @import url('https://fonts.googleapis.com/css2?family=Inter');
                `}</style>
                <h1 style={{fontFamily: 'Inter'}}>Hello</h1>
              </>
            );
            """;

        NetworkFontLoad load = NetworkFontPreflight.Scan("src/Title.tsx", tsx)!;
        load.Line.Should().Be(4);
    }

    // ── Not refused: mentions that load nothing ─────────────────────────────

    [Theory]
    [InlineData("src/Title.tsx", "const copy = \"Fonts by fonts.googleapis.com and use.typekit.net\";")]
    [InlineData("src/Title.tsx", "<p>We moved off @remotion/google-fonts and fonts.gstatic.com last year.</p>")]
    [InlineData("src/Title.tsx", "// import { loadFont } from \"@remotion/google-fonts/Inter\";")]
    [InlineData("src/Title.tsx", "/* @import url('https://fonts.googleapis.com/css2?family=Inter'); */")]
    [InlineData("src/style.css", "/* @font-face { src: url(https://cdn.example.com/brand.woff2) } */")]
    [InlineData("src/Title.tsx", "const url = 'https://example.com'; // see https://fonts.googleapis.com")]
    [InlineData("src/Title.tsx", "<div style={{ fontFamily: 'Inter', backgroundImage: 'url(/bg.png)' }} />")]
    [InlineData("src/style.css", "@font-face { font-family: Brand; src: url('/fonts/brand.woff2'); }")]
    [InlineData("src/Title.tsx", "loadFont({ family: 'Brand', url: staticFile('brand.woff2'), weight: '700' });")]
    [InlineData("src/Title.tsx", "const face = new FontFace('Brand', `url(${staticFile('brand.woff2')})`);")]
    [InlineData("src/style.css", ".hero { background: url(https://example.com/hero.png) center / cover; }")]
    [InlineData("src/Title.tsx", "const label = 'Load it with @import url(...) in CSS';")]
    [InlineData("src/Title.tsx", "<a href=\"https://example.com/fonts\">Fonts</a>")]
    public void A_mention_that_loads_nothing_is_not_refused(string path, string source)
    {
        NetworkFontPreflight.Scan(path, source).Should().BeNull();
    }

    [Fact]
    public void The_StageFont_snippet_text_is_accepted_and_a_remote_url_in_the_same_shape_is_refused()
    {
        const string snippet = "import {staticFile} from 'remotion';\nimport {loadFont} from '@remotion/fonts';\n\n" +
            "loadFont({ family: 'Lobster', url: staticFile('fonts/Lobster/Lobster-normal-400.ttf'), weight: '400', style: 'normal' });\n";
        NetworkFontPreflight.Scan("src/fonts.ts", snippet).Should().BeNull();
        NetworkFontPreflight.Scan("src/fonts.ts", snippet.Replace("staticFile('fonts/Lobster/Lobster-normal-400.ttf')", "'https://cdn.example.com/Lobster.ttf'"))
            .Should().NotBeNull();
    }

    // ── The walk over the sandbox project ───────────────────────────────────

    [Fact]
    public async Task A_clean_project_passes()
    {
        FileSession session = new();
        session.Add("src/index.ts", "import {registerRoot} from 'remotion';\nregisterRoot(Root);");
        session.Add("src/root.tsx", "export const Root = () => <Composition id=\"Main\" />;");
        session.Add("src/scenes/Title.tsx", "export const Title = () => <h1 style={{fontFamily: 'Playfair Display'}}>Hi</h1>;");

        (await NetworkFontPreflight.CheckAsync(session, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_nested_network_font_is_refused_with_the_file_line_and_installed_list()
    {
        FileSession session = new();
        session.Add("src/root.tsx", "export const Root = () => null;");
        session.Add("src/scenes/intro/Title.tsx", "import React from 'react';\n\nimport {loadFont} from '@remotion/google-fonts/Inter';\n");

        string? refusal = await NetworkFontPreflight.CheckAsync(session, CancellationToken.None);

        refusal.Should().NotBeNull();
        refusal.Should().StartWith("NETWORK_FONT: src/scenes/intro/Title.tsx:3 loads a font from the network");
        refusal.Should().Contain("The sandbox has no internet.").And.Contain("StageFont");
        refusal.Should().Contain("Use an installed font by CSS font-family name: " + SandboxFonts.FamilyList + ", or call StageFont");
    }

    [Fact]
    public async Task Node_modules_other_extensions_and_oversized_files_are_not_read()
    {
        FileSession session = new();
        session.Add("src/node_modules/pkg/index.js", "import '@remotion/google-fonts/Inter';");
        session.Add("src/README.md", "import '@remotion/google-fonts/Inter';");
        session.Add("src/data.json", "{\"a\": \"@import url(https://fonts.googleapis.com)\"}");
        session.Add("src/big.tsx", new string(' ', NetworkFontPreflight.MaxFileBytes + 1) + "import '@remotion/google-fonts/Inter';");
        session.Add("src/ok.tsx", "export const x = 1;");

        (await NetworkFontPreflight.CheckAsync(session, CancellationToken.None)).Should().BeNull();
        session.Reads.Should().Equal("src/ok.tsx");
        session.Listed.Should().NotContain("src/node_modules");
    }

    [Fact]
    public async Task The_total_bytes_read_are_capped()
    {
        FileSession session = new();
        int perFile = NetworkFontPreflight.MaxFileBytes;
        int files = (int)(NetworkFontPreflight.MaxTotalBytes / perFile) + 3;
        for (int i = 0; i < files; i++)
            session.Add($"src/f{i:D3}.ts", new string(' ', perFile));

        await NetworkFontPreflight.CheckAsync(session, CancellationToken.None);

        long read = session.Reads.Sum(path => session.Files[path].Length);
        read.Should().BeLessThanOrEqualTo(NetworkFontPreflight.MaxTotalBytes);
        session.Reads.Count.Should().BeLessThan(files);
    }

    [Fact]
    public async Task A_project_without_src_or_an_unreadable_sandbox_is_not_refused()
    {
        (await NetworkFontPreflight.CheckAsync(new FileSession(), CancellationToken.None)).Should().BeNull();

        FileSession broken = new() { ListFails = true };
        (await NetworkFontPreflight.CheckAsync(broken, CancellationToken.None)).Should().BeNull(
            "the preflight is a fast-fail guard: when it cannot read the sources the render goes ahead as before");

        FileSession unreadable = new() { ReadFails = true };
        unreadable.Add("src/root.tsx", "import '@remotion/google-fonts/Inter';");
        (await NetworkFontPreflight.CheckAsync(unreadable, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        FileSession session = new();
        session.Add("src/root.tsx", "export const x = 1;");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = () => NetworkFontPreflight.CheckAsync(session, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>A sandbox workspace whose files live in a dictionary; directories are implied by the paths.</summary>
    internal sealed class FileSession : ISandboxSession
    {
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];
        public List<string> Listed { get; } = [];
        public bool ListFails { get; set; }
        public bool ReadFails { get; set; }

        public void Add(string path, string content) => Files[path] = content;

        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ListFails)
                throw new System.Net.Http.HttpRequestException("sandbox unreachable");

            Listed.Add(path);
            string prefix = path.TrimEnd('/') + "/";
            var entries = new Dictionary<string, SandboxFileEntry>(StringComparer.Ordinal);
            foreach ((string file, string content) in Files)
            {
                if (!file.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string rest = file[prefix.Length..];
                int slash = rest.IndexOf('/');
                string name = slash < 0 ? rest : rest[..slash];
                entries[name] = new SandboxFileEntry(name, slash >= 0, slash < 0 ? content.Length : 0, DateTime.UtcNow);
            }

            if (entries.Count == 0)
                throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "Sandbox API request failed (404): not found");
            return Task.FromResult<IReadOnlyList<SandboxFileEntry>>([.. entries.Values.OrderBy(e => e.Name, StringComparer.Ordinal)]);
        }

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadFails)
                throw new IOException("read failed");
            Reads.Add(path);
            return Task.FromResult(new SandboxTextFile(path, Files[path]));
        }

        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxWorkspaceArchive> ExportWorkspaceAsync(int commandTimeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxWorkspaceImport> ImportWorkspaceAsync(Stream archive, long length, int commandTimeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

}
