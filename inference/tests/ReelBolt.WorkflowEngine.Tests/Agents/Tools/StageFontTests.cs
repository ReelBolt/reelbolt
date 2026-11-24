using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Fonts;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>The Google Fonts fetcher's controls and the StageFont tool. No live network: a fake handler answers.</summary>
public class StageFontTests
{
    private static readonly byte[] FontBytes = [0x00, 0x01, 0x00, 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static GoogleFontFamily Family(byte[] content, long? size = null) => new(
        "Test Face", "OFL", "sans", "ofl/inter",
        [new GoogleFontFile("Test-Regular.ttf", "normal", 400, 400, size ?? content.Length, Sha(content))]);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static GoogleFontFetcher Fetcher(FakeHandler handler, bool enabled = true) =>
        new(new HttpClient(handler), Options.Create(new GoogleFontsOptions { Enabled = enabled }));

    private static HttpResponseMessage Ok(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    [Fact]
    public async Task Fetch_uses_only_the_allowlisted_https_host_at_the_pinned_commit_and_accepts_a_matching_hash()
    {
        FakeHandler handler = new(_ => Ok(FontBytes));
        GoogleFontFamily family = Family(FontBytes);

        GoogleFontFetchResult result = await Fetcher(handler).FetchAsync(family, family.Files[0], CancellationToken.None);

        result.Ok.Should().BeTrue(result.Error);
        Uri uri = handler.Requests.Should().ContainSingle().Subject;
        uri.Scheme.Should().Be("https");
        uri.Host.Should().Be("raw.githubusercontent.com");
        uri.AbsolutePath.Should().StartWith("/google/fonts/" + GoogleFontCatalog.Commit + "/ofl/inter/");
    }

    [Fact]
    public async Task Fetch_refuses_a_redirect()
    {
        FakeHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.test/x.ttf") } });
        GoogleFontFamily family = Family(FontBytes);

        GoogleFontFetchResult result = await Fetcher(handler).FetchAsync(family, family.Files[0], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("redirect");
        handler.Requests.Should().ContainSingle("the redirect target is never requested");
    }

    [Fact]
    public async Task Fetch_refuses_a_hash_mismatch()
    {
        FakeHandler handler = new(_ => Ok([.. FontBytes, 99]));
        GoogleFontFamily family = Family(FontBytes);

        GoogleFontFetchResult result = await Fetcher(handler).FetchAsync(family, family.Files[0], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("SHA-256");
    }

    [Fact]
    public async Task Fetch_refuses_an_oversize_file_before_and_while_downloading()
    {
        GoogleFontFamily declaredHuge = Family(FontBytes, GoogleFontFetcher.MaxBytes + 1);
        FakeHandler never = new(_ => Ok(FontBytes));
        (await Fetcher(never).FetchAsync(declaredHuge, declaredHuge.Files[0], CancellationToken.None)).Error.Should().Contain("10 MB");
        never.Requests.Should().BeEmpty();

        byte[] big = new byte[GoogleFontFetcher.MaxBytes + 10];
        GoogleFontFamily lying = Family(FontBytes);
        FakeHandler streamed = new(_ => Ok(big));
        (await Fetcher(streamed).FetchAsync(lying, lying.Files[0], CancellationToken.None)).Error.Should().Contain("10 MB");
    }

    [Fact]
    public async Task Fetch_when_disabled_makes_no_request()
    {
        FakeHandler handler = new(_ => Ok(FontBytes));
        GoogleFontFamily family = Family(FontBytes);

        GoogleFontFetchResult result = await Fetcher(handler, enabled: false).FetchAsync(family, family.Files[0], CancellationToken.None);

        result.Ok.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    // --- the tool ----------------------------------------------------------------------------

    private static readonly Guid ProjectId = Guid.NewGuid();

    private sealed class StubFetcher(bool enabled, byte[]? bytes = null) : IGoogleFontFetcher
    {
        public bool Enabled => enabled;
        public int Calls { get; private set; }

        public Task<GoogleFontFetchResult> FetchAsync(GoogleFontFamily family, GoogleFontFile file, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(bytes is null ? GoogleFontFetchResult.Fail("nope") : GoogleFontFetchResult.Success(bytes));
        }
    }

    private sealed record Rig(ReactRemotionSandboxTools Tool, List<(string Path, byte[] Bytes)> Writes, Guid ExecutionId);

    private static Rig Build(IGoogleFontFetcher? fetcher, IReadOnlyList<ProjectWorkspaceFile>? files = null, byte[]? stored = null)
    {
        Guid executionId = Guid.NewGuid();
        List<(string, byte[])> writes = [];
        Mock<ISandboxSession> session = new();
        session.SetupGet(s => s.ExecutionId).Returns(executionId);
        session.Setup(s => s.WriteBytesAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], CancellationToken>((p, b, _) => writes.Add((p, b))).Returns(Task.CompletedTask);
        Mock<ISandboxSessionFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<Guid>())).Returns(session.Object);

        WorkflowExecutionContextAccessor accessor = new();
        accessor.BeginScope(executionId, ProjectId, "correlation", null);
        Mock<IProjectFileWorkspace> workspace = new();
        workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(files ?? []);
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(stored ?? []), ContentLength = stored?.Length ?? 0 });

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build();
        var tool = new ReactRemotionSandboxTools(
            Mock.Of<IHttpClientFactory>(), accessor, workspace.Object, s3.Object, configuration,
            NullLogger<ReactRemotionSandboxTools>.Instance, Mock.Of<IServiceScopeFactory>(),
            sandboxSessions: factory.Object, googleFonts: fetcher);
        return new Rig(tool, writes, executionId);
    }

    private static ProjectWorkspaceFile ProjectFont(string name, string key) =>
        new(Guid.NewGuid(), ProjectId, name, null, "userFiles", key, "font/ttf", 64, DateTime.UtcNow, null);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Disabled_lists_the_installed_families()
    {
        Rig rig = Build(new StubFetcher(enabled: false));

        JsonElement r = Parse(await rig.Tool.StageFont(GoogleFontCatalog.Families.First(f => SandboxFonts.Find(f.Family) is null).Family));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.GetProperty("error").GetString().Should().Contain("disabled").And.Contain(SandboxFonts.FamilyList);
        rig.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_installed_family_is_not_downloaded()
    {
        StubFetcher fetcher = new(enabled: true, FontBytes);
        Rig rig = Build(fetcher);
        string installed = SandboxFonts.Families[0].Family;

        JsonElement r = Parse(await rig.Tool.StageFont(installed));

        r.GetProperty("ok").GetBoolean().Should().BeTrue();
        r.GetProperty("installed").GetBoolean().Should().BeTrue();
        fetcher.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_family_gets_a_message_with_suggestions()
    {
        Rig rig = Build(new StubFetcher(enabled: true, FontBytes));

        JsonElement r = Parse(await rig.Tool.StageFont("Playfair Displayy"));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.GetProperty("error").GetString().Should().Contain("not in the Google Fonts catalog").And.Contain("Did you mean: ").And.Contain("Playfair Display");
        GoogleFontCatalog.Suggest("Montserat").Should().Contain("Montserrat");
    }

    [Theory]
    [InlineData("O'Brand")]
    [InlineData("Brand\"x")]
    public async Task A_family_name_with_a_quote_is_refused(string family)
    {
        Rig rig = Build(new StubFetcher(enabled: true, FontBytes));

        Parse(await rig.Tool.StageFont(family)).GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Log_values_lose_control_characters_and_are_capped()
    {
        ReactRemotionSandboxTools.LogSafe("a\r\nb\u001bc").Should().Be("abc");
        ReactRemotionSandboxTools.LogSafe(new string('x', 500)).Length.Should().BeLessThan(90);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("Inter/../x")]
    [InlineData("https://evil.test/a.ttf")]
    [InlineData("")]
    public async Task A_family_name_that_is_a_path_or_url_is_refused(string family)
    {
        StubFetcher fetcher = new(enabled: true, FontBytes);
        Rig rig = Build(fetcher);

        JsonElement r = Parse(await rig.Tool.StageFont(family));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        fetcher.Calls.Should().Be(0);
        rig.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_catalogued_google_family_is_staged_with_css_and_registered_as_declared()
    {
        GoogleFontFamily target = GoogleFontCatalog.Families.First(f =>
            SandboxFonts.Find(f.Family) is null && f.Files.Any(x => x.Style == "normal" && x.Covers(400)));
        Rig rig = Build(new StubFetcher(enabled: true, FontBytes));

        JsonElement r = Parse(await rig.Tool.StageFont(target.Family, "google"));

        r.GetProperty("ok").GetBoolean().Should().BeTrue(r.ToString());
        r.GetProperty("family").GetString().Should().Be(target.Family);
        string snippet = r.GetProperty("snippet").GetString()!;
        string dir = System.Text.RegularExpressions.Regex.Replace(target.Family, "[^A-Za-z0-9]+", "");
        snippet.Should().Contain("import {staticFile} from 'remotion';").And.Contain("import {loadFont} from '@remotion/fonts';")
            .And.Contain($"loadFont({{ family: '{target.Family}', url: staticFile('fonts/{dir}/").And.Contain("weight: '").And.Contain("style: 'normal'");
        snippet.Should().NotContain("@font-face").And.NotContain("url('fonts/");
        r.TryGetProperty("css", out _).Should().BeFalse();
        r.GetProperty("usage").GetString().Should().Contain("delayRender").And.Contain("top level");
        NetworkFontPreflight.Scan("src/fonts.ts", snippet).Should().BeNull("a local staticFile loadFont is not a network font");
        rig.Writes.Should().ContainSingle().Which.Path.Should().StartWith("public/fonts/");
        rig.Tool.DeclaredFamiliesFor(rig.ExecutionId).Should().Contain(target.Family);
    }

    [Fact]
    public async Task A_project_font_with_the_right_magic_bytes_is_staged()
    {
        ProjectWorkspaceFile file = ProjectFont("Brand.ttf", $"projects/{ProjectId}/userFiles/Brand.ttf");
        Rig rig = Build(null, [file], FontBytes);

        JsonElement r = Parse(await rig.Tool.StageFont("Brand Sans", "Brand.ttf"));

        r.GetProperty("ok").GetBoolean().Should().BeTrue(r.ToString());
        rig.Writes.Should().ContainSingle().Which.Path.Should().Be("public/fonts/BrandSans/BrandSans-normal-400.ttf");
        rig.Tool.DeclaredFamiliesFor(rig.ExecutionId).Should().Contain("Brand Sans");
    }

    [Fact]
    public async Task A_project_font_with_the_wrong_magic_bytes_is_refused()
    {
        ProjectWorkspaceFile file = ProjectFont("Fake.ttf", $"projects/{ProjectId}/userFiles/Fake.ttf");
        Rig rig = Build(null, [file], "<html>not a font at all</html>"u8.ToArray());

        JsonElement r = Parse(await rig.Tool.StageFont("Fake", "Fake.ttf"));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.GetProperty("error").GetString().Should().Contain("signature");
        rig.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_font_under_another_tenants_prefix_is_refused()
    {
        ProjectWorkspaceFile file = ProjectFont("Other.ttf", $"projects/{Guid.NewGuid()}/userFiles/Other.ttf");
        Rig rig = Build(null, [file], FontBytes);

        JsonElement r = Parse(await rig.Tool.StageFont("Other", "Other.ttf"));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.GetProperty("error").GetString().Should().Contain("outside this project");
        rig.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_file_that_is_not_a_font_extension_is_refused()
    {
        ProjectWorkspaceFile file = ProjectFont("evil.js", $"projects/{ProjectId}/userFiles/evil.js");
        Rig rig = Build(null, [file], FontBytes);

        Parse(await rig.Tool.StageFont("Evil", "evil.js")).GetProperty("ok").GetBoolean().Should().BeFalse();
        rig.Writes.Should().BeEmpty();
    }
}
