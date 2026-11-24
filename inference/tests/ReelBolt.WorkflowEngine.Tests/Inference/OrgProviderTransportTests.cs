#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Webhooks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// Security assessment ENG-1 / ENG-2 / API-IA-1 / API-IA-4 / HOOK-01. An org-owned provider row's endpoint passed
/// only a save-time check, so every client built for it must dial public addresses only. A loopback server (never
/// the live network) stands in for "an internal host": a platform row reaches it (the call really dials), an org
/// row never does.
/// </summary>
public class OrgProviderTransportTests
{
    private const InferenceProviderKind Azure = InferenceProviderKind.AzureOpenAI;
    private const InferenceProviderKind OpenAi = InferenceProviderKind.OpenAICompatible;

    private static (HttpListener Listener, ConcurrentQueue<string> Paths, Task Loop, string Url) Server()
    {
        // HttpListener cannot pick its own port, so bind a random one and retry on a clash: no probe-then-close
        // window in which another process can take the port.
        HttpListener listener = null!;
        int port;
        for (int attempt = 0; ; attempt++)
        {
            port = Random.Shared.Next(20000, 60000);
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 50)
            {
                listener.Close();
            }
        }

        ConcurrentQueue<string> paths = new();
        Task loop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                paths.Enqueue(context.Request.Url!.AbsolutePath);
                try
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync("{}"u8.ToArray());
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // The caller hung up; the request was still recorded.
                }
            }
        });
        return (listener, paths, loop, $"http://127.0.0.1:{port}");
    }

    private static readonly DecisionQuestion[] Questions = [new("q", DecisionKind.Noul, "Is it?", [])];

    /// <summary>One call per client the factories can build: (endpoint, owning organization, ct) -> a request.</summary>
    private static readonly Dictionary<string, Func<string, Guid?, CancellationToken, Task>> Calls = new()
    {
        ["Video/FalAi"] = (e, o, ct) => Video(InferenceProviderKind.FalAi, e, o).ProbeAsync(ct),
        ["Video/MiniMax"] = (e, o, ct) => Video(InferenceProviderKind.MiniMax, e, o).ProbeAsync(ct),
        ["Video/Higgsfield"] = (e, o, ct) => Video(InferenceProviderKind.Higgsfield, e, o).ProbeAsync(ct),
        ["Video/ComfyUI"] = (e, o, ct) => Video(InferenceProviderKind.ComfyUI, e, o).ProbeAsync(ct),
        ["Image/MiniMax"] = (e, o, ct) => Image(InferenceProviderKind.MiniMax, e, o).ProbeAsync(ct),
        ["Image/OpenAICompatible"] = (e, o, ct) => Image(OpenAi, e, o).ProbeAsync(ct),
        ["Image/BlackForestLabs"] = (e, o, ct) => Image(InferenceProviderKind.BlackForestLabs, e, o).ProbeAsync(ct),
        ["Image/FalAi"] = (e, o, ct) => Image(InferenceProviderKind.FalAi, e, o).ProbeAsync(ct),
        ["ImageAnalysis/FalAi"] = (e, o, ct) => new ImageAnalysisClientFactory().Get(new ResolvedImageAnalysisProvider(
            Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, e, "m", "test-key", 10,
            OrganizationId: o, IsPlatformManaged: o is null)).ProbeAsync(ct),
        ["Transcription/AzureOpenAI"] = (e, o, ct) => Transcribe(Azure, e, o, ct),
        ["Transcription/OpenAICompatible"] = (e, o, ct) => Transcribe(OpenAi, e, o, ct),
        ["Transcription/FishAudio"] = (e, o, ct) => Transcribe(InferenceProviderKind.FishAudio, e, o, ct),
        ["Speech/FishAudio"] = (e, o, ct) => Speak(InferenceProviderKind.FishAudio, e, o, ct),
        ["Speech/OpenAICompatible"] = (e, o, ct) => Speak(OpenAi, e, o, ct),
        ["Decision/TypeSafe"] = (e, o, ct) => Decide(InferenceProviderKind.TypeSafe, e, o, ct),
        ["Decision/OpenJev"] = (e, o, ct) => Decide(InferenceProviderKind.OpenJev, e, o, ct),
        ["Decision/AzureOpenAI"] = (e, o, ct) => Decide(Azure, e, o, ct),
        ["Decision/OpenAICompatible"] = (e, o, ct) => Decide(OpenAi, e, o, ct),
        ["Decision/DeepSeek"] = (e, o, ct) => Decide(InferenceProviderKind.DeepSeek, e, o, ct),
        ["Chat/AzureOpenAI"] = (e, o, ct) => Chat(Azure, e, o, ct),
        ["Chat/OpenAICompatible"] = (e, o, ct) => Chat(OpenAi, e, o, ct),
        ["Chat/Anthropic"] = (e, o, ct) => Chat(InferenceProviderKind.Anthropic, e, o, ct),
        ["Chat/Gemini"] = (e, o, ct) => Chat(InferenceProviderKind.Gemini, e, o, ct),
        ["Chat/DeepSeek"] = (e, o, ct) => Chat(InferenceProviderKind.DeepSeek, e, o, ct),
        ["Embedding/AzureOpenAI"] = (e, o, ct) => Embed(Azure, e, o, ct),
        ["Embedding/OpenAICompatible"] = (e, o, ct) => Embed(OpenAi, e, o, ct),
        ["VideoSegmentation/FalAi"] = (e, o, ct) => new FalAiVideoSegmentationClientFactory().Get(
                new ResolvedImageAnalysisProvider(
                    Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.VideoSegmentation, e,
                    "fal-ai/sam-3-1/video-rle", "test-key", 10, OrganizationId: o, IsPlatformManaged: o is null),
                new VideoSegmentationFrameSize(1280, 720))
            .SegmentVideoAsync(
                new VideoSegmentationRequest("fal-ai/sam-3-1/video-rle", new Uri("https://files.example.com/c.mp4"), "cat"), ct),
        ["ContentModeration"] = (e, o, ct) => new ContentModerationClient(new ResolvedContentModerationProvider(
                Guid.NewGuid(), "row", OpenAi, e, "m", "test-key", 10, OrganizationId: o, IsPlatformManaged: o is null))
            .ClassifyAsync(new ModerationClassifyRequest("some text", [], "m"), ct),
    };

    public static IEnumerable<object[]> CallNames() => Calls.Keys.Select(k => new object[] { k });

    private static IVideoGenerationClient Video(InferenceProviderKind kind, string endpoint, Guid? org) =>
        new VideoGenerationClientFactory().Get(new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "row", kind, endpoint,
            kind == InferenceProviderKind.FalAi ? FalAiVideoGenerationClient.Catalog.AllIds.Split(',')[0].Trim() : "m",
            "id:secret", 10, OrganizationId: org, IsPlatformManaged: org is null));

    private static IImageClient Image(InferenceProviderKind kind, string endpoint, Guid? org) =>
        new ImageClientFactory().Get(new ResolvedImageProvider(
            Guid.NewGuid(), "row", kind,
            // fal probes only a catalogue endpoint that serves the row's job; blank = that job's default.
            kind == InferenceProviderKind.FalAi ? InferenceProviderCapability.ImageErase : InferenceProviderCapability.ImageGeneration,
            endpoint, kind == InferenceProviderKind.FalAi ? "" : "m", "test-key", 10,
            OrganizationId: org, IsPlatformManaged: org is null));

    private static Task Transcribe(InferenceProviderKind kind, string endpoint, Guid? org, CancellationToken ct) =>
        new TranscriptionClientFactory().Get(new ResolvedTranscriptionProvider(
                Guid.NewGuid(), "row", kind, endpoint, "m", "test-key", 10, OrganizationId: org, IsPlatformManaged: org is null))
            .TranscribeAsync(new MemoryStream(new byte[64]), "a.wav", null, false, ct);

    private static Task Speak(InferenceProviderKind kind, string endpoint, Guid? org, CancellationToken ct) =>
        new SpeechSynthesisClientFactory().Get(new ResolvedSpeechSynthesisProvider(
                Guid.NewGuid(), "row", kind, endpoint, "m", "test-key", 10, OrganizationId: org, IsPlatformManaged: org is null))
            .SynthesizeAsync(new SpeechRequest("hello", "voice", null), ct);

    private static Task Decide(InferenceProviderKind kind, string endpoint, Guid? org, CancellationToken ct) =>
        new DecisionClientFactory().Get(new ResolvedDecisionProvider(
                Guid.NewGuid(), "row", kind, endpoint, "m", "test-key", 10, OrganizationId: org, IsPlatformManaged: org is null))
            .DecideAsync(new DecisionState("state"), Questions, ct);

    private static Task Chat(InferenceProviderKind kind, string endpoint, Guid? org, CancellationToken ct) =>
        new ChatClientFactory().Get(new ResolvedInferenceProvider(
                Guid.NewGuid(), "row", kind, endpoint, "m", "test-key", 10, OrganizationId: org, IsPlatformManaged: org is null))
            .GetResponseAsync("hello", cancellationToken: ct);

    private static Task Embed(InferenceProviderKind kind, string endpoint, Guid? org, CancellationToken ct) =>
        new EmbeddingClientFactory().Get(new ResolvedEmbeddingProvider(
                Guid.NewGuid(), "row", kind, endpoint, "m", "test-key", 10, OrganizationId: org, IsPlatformManaged: org is null))
            .GenerateAsync(["hello"], cancellationToken: ct);

    /// <summary>Runs the call; its outcome is irrelevant here (only what reached the server matters).</summary>
    private static async Task<ConcurrentQueue<string>> RunAsync(string name, Guid? org)
    {
        (HttpListener listener, ConcurrentQueue<string> paths, Task loop, string url) = Server();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(4));
        try
        {
            await Calls[name](url, org, cts.Token);
        }
        catch (Exception)
        {
            // A refused connection, a nonsense "{}" answer, or the SDK's retry cut short.
        }
        finally
        {
            listener.Stop();
            await loop;
        }

        return paths;
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public async Task A_platform_row_still_reaches_a_private_address(string name)
    {
        ConcurrentQueue<string> paths = await RunAsync(name, org: null);

        paths.Should().NotBeEmpty("a platform row keeps its transport (and proves this call really dials the endpoint)");
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public async Task An_org_row_client_never_connects_to_a_non_public_address(string name)
    {
        ConcurrentQueue<string> paths = await RunAsync(name, org: Guid.NewGuid());

        paths.Should().BeEmpty("an org row's connection is checked against the public-only rule at connect time");
    }

    // ---- ENG-1: Higgsfield clip download ----

    private static HiggsfieldVideoGenerationClient Higgsfield(HttpMessageHandler download) =>
        new(new HttpClient(new Stub(_ => throw new InvalidOperationException("the API client must not download")))
        {
            BaseAddress = new Uri("https://platform.higgsfield.ai/")
        }, "id:secret", downloadClient: new HttpClient(download));

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A body that only declares its length, so a 512 MB cap can be tested without allocating it.</summary>
    private sealed class DeclaredLengthContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long computed)
        {
            computed = length;
            return true;
        }
    }

    [Theory]
    [InlineData("http://cdn.example.com/clip.mp4")]
    [InlineData("https://127.0.0.1/clip.mp4")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://rabbitmq/api")]
    [InlineData("https://inference.internal/clip.mp4")]
    public async Task Higgsfield_download_refuses_a_non_public_or_non_https_url(string url)
    {
        Stub download = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        HiggsfieldVideoGenerationClient client = Higgsfield(download);

        Func<Task> act = () => client.DownloadAsync(url, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        download.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Higgsfield_download_refuses_a_clip_declared_over_the_cap()
    {
        Stub download = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DeclaredLengthContent(HiggsfieldVideoGenerationClient.MaxDownloadBytes + 1)
        });

        Func<Task> act = () => Higgsfield(download).DownloadAsync("https://cdn.example.com/clip.mp4", CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*limit*");
    }

    [Fact]
    public async Task Higgsfield_download_goes_through_the_download_client_and_caps_the_stream()
    {
        Stub download = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) });

        await using Stream stream = await Higgsfield(download).DownloadAsync("https://cdn.example.com/clip.mp4", CancellationToken.None);

        download.Calls.Should().Be(1);
        stream.Should().BeOfType<LimitedReadStream>();
    }

    // ---- API-IA-4: one address classifier ----

    [Theory]
    [InlineData("0.1.2.3")]
    [InlineData("192.0.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("::10.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("93.184.216.34")]
    [InlineData("::ffff:93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void The_provider_endpoint_classifier_matches_the_connect_time_guard(string address)
    {
        IPAddress ip = IPAddress.Parse(address);
        IPAddress judged = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

        InferenceProvidersController.IsDisallowedAddress(ip).Should().Be(GeneratedClipUrlGuard.IsPrivate(judged));
    }

    [Theory]
    [InlineData("0.1.2.3")]
    [InlineData("192.0.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("::10.0.0.1")]
    public void The_provider_endpoint_classifier_refuses_the_ranges_it_used_to_miss(string address) =>
        InferenceProvidersController.IsDisallowedAddress(IPAddress.Parse(address)).Should().BeTrue();

    // ---- HOOK-01: trailing dot, and one message for the DNS outcome ----

    [Theory]
    [InlineData("https://postgres./x")]
    [InlineData("https://localhost./x")]
    [InlineData("https://db.svc.cluster.local./x")]
    [InlineData("https://metadata.google.internal./")]
    public void A_trailing_dot_does_not_get_an_internal_name_past_the_url_guard(string url)
    {
        Action act = () => GeneratedClipUrlGuard.EnsureAllowed(url);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_trailing_dot_on_a_public_name_is_still_allowed()
    {
        Action act = () => GeneratedClipUrlGuard.EnsureAllowed("https://hooks.example.com./x");

        act.Should().NotThrow();
    }

    private sealed class Resolver(Func<IPAddress[]> resolve) : IWebhookHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(resolve());
    }

    [Fact]
    public async Task The_webhook_url_check_answers_the_same_for_unresolvable_and_internal_hosts()
    {
        const string url = "https://candidate.example.com/hook";

        string? unresolvable = await new WebhookUrlValidator(new Resolver(() => throw new SocketException()))
            .ValidateAsync(url, CancellationToken.None);
        string? empty = await new WebhookUrlValidator(new Resolver(() => [])).ValidateAsync(url, CancellationToken.None);
        string? internalHost = await new WebhookUrlValidator(new Resolver(() => [IPAddress.Parse("10.0.0.5")]))
            .ValidateAsync(url, CancellationToken.None);
        string? ok = await new WebhookUrlValidator(new Resolver(() => [IPAddress.Parse("93.184.216.34")]))
            .ValidateAsync(url, CancellationToken.None);

        unresolvable.Should().Be(WebhookUrlValidator.HostNotPublicMessage);
        empty.Should().Be(unresolvable);
        internalHost.Should().Be(unresolvable);
        ok.Should().BeNull();
    }

    // ---- Drift: a new provider-row client cannot regress ----

    private static string SourceRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("repo root not found"), "inference", "src");
    }

    /// <summary>The three projects a provider row's endpoint can be dialled from.</summary>
    private static readonly string[] Projects = ["ReelBolt.Shared", "ReelBolt.WorkflowEngine", "ReelBolt.Inference.Api"];

    private static IEnumerable<string> Sources(string root) => Projects
        .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
        .Where(f => !Regex.IsMatch(f, @"[\\/](obj|bin|Migrations)[\\/]"));

    /// <summary>
    /// Factories that take a resolved row but build no HTTP client themselves, and the file that does. Anything
    /// not listed is checked in its own file.
    /// </summary>
    private static readonly Dictionary<string, string> Delegated = new()
    {
        // Builds ContentModerationClient, which picks the transport from the row.
        ["ContentModerationClientFactory.cs"] = Path.Combine("ReelBolt.Shared", "Inference", "ContentModerationClient.cs"),
    };

    /// <summary>
    /// Files that mention a provider row and build an HTTP client that never dials a row's endpoint, with the reason.
    /// </summary>
    private static readonly Dictionary<string, string> NotARowClient = new()
    {
        ["PriceBook.cs"] = "MiniMaxVideoGenerationClient.Describe only: a static table lookup, no request, no endpoint",
        ["ModelCatalogSeed.cs"] = "MiniMaxVideoGenerationClient.Describe only: a static table lookup, no request, no endpoint",
    };

    private static readonly Regex TakesProviderRow = new(@"\b(Resolved\w*Provider|ModelCapabilityView)\b", RegexOptions.Compiled);
    private static readonly Regex Guarded = new(@"ProviderRowTransport\.|GeneratedClipUrlGuard\.CreateUserUrlHandler\(", RegexOptions.Compiled);

    /// <summary>Any construction of an HTTP client, handler or vendor SDK client.</summary>
    private static readonly Regex BuildsTransport = new(
        @"new HttpClient\b|HttpClient\s+\w+\s*=\s*new\s*\(|new SocketsHttpHandler\b|new HttpClientHandler\b|HttpClientPipelineTransport" +
        @"|new (Azure)?OpenAIClient\s*\(|new AnthropicClient\s*\(|\w+ClientOptions\s+\w+\s*=\s*new\s*\(",
        RegexOptions.Compiled);

    /// <summary>A client with no handler of its own: follows redirects and dials anything.</summary>
    private static readonly Regex BareClient = new(
        @"new HttpClient\s*\(\s*\)|new HttpClient\s*\{|HttpClient\s+\w+\s*=\s*new\s*\(\s*\)|new HttpClientHandler\b", RegexOptions.Compiled);

    private static readonly Lazy<Dictionary<string, bool>> InterfaceByName = new(() =>
    {
        Dictionary<string, bool> byName = new(StringComparer.Ordinal);
        foreach (AssemblyName reference in typeof(OrgProviderTransportTests).Assembly.GetReferencedAssemblies()
                     .Where(a => a.Name!.StartsWith("ReelBolt.", StringComparison.Ordinal)))
        {
            Type?[] types;
            try
            {
                types = Assembly.Load(reference).GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            foreach (Type? type in types)
            {
                if (type is null || type.IsNested) continue;
                byName[type.Name] = byName.GetValueOrDefault(type.Name) || type.IsInterface;
            }
        }

        return byName;
    });

    /// <summary>Whether the file's type (named after the file) is an interface: by <see cref="Type.IsInterface"/>, not by its first letter.</summary>
    private static bool IsInterfaceFile(string path) =>
        InterfaceByName.Value.TryGetValue(Path.GetFileNameWithoutExtension(path), out bool isInterface) && isInterface;

    [Fact]
    public void Every_client_factory_that_takes_a_provider_row_uses_the_guarded_transport()
    {
        string root = SourceRoot();
        string[] factories = Sources(root)
            .Where(f => Path.GetFileName(f).EndsWith("ClientFactory.cs", StringComparison.Ordinal) && !IsInterfaceFile(f))
            .ToArray();
        factories.Should().HaveCountGreaterThan(9, "the scan found the factories");
        factories.Select(Path.GetFileName).Should().Contain("FalAiVideoSegmentationClientFactory.cs");
        factories.Select(Path.GetFileName).Should().Contain(["ImageClientFactory.cs", "ImageAnalysisClientFactory.cs"],
            "a class whose name starts with I is still a factory; only interfaces are skipped");

        List<string> offenders = [];
        foreach (string factory in factories)
        {
            if (!TakesProviderRow.IsMatch(File.ReadAllText(factory))) continue;

            bool delegated = Delegated.TryGetValue(Path.GetFileName(factory), out string? other);
            string file = delegated ? Path.Combine(root, other!) : factory;
            string source = File.ReadAllText(file);
            if (!Guarded.IsMatch(source))
                offenders.Add($"{Path.GetFileName(file)}: no guarded transport for an org-owned row");
            if (!delegated && BareClient.IsMatch(source))
                offenders.Add($"{Path.GetFileName(file)}: builds a bare HttpClient; use ProviderRowTransport");
        }

        offenders.Should().BeEmpty(
            "a client built for an org-owned provider row must dial through GeneratedClipUrlGuard.CreateUserUrlHandler() " +
            "(see ProviderRowTransport); a platform row may keep its own handler but never a handler-less client. Offenders: " +
            string.Join("; ", offenders));
    }

    [Fact]
    public void Every_file_that_builds_a_client_from_a_provider_row_uses_the_guarded_transport()
    {
        string root = SourceRoot();
        List<string> offenders = [];
        int checkedFiles = 0;
        foreach (string file in Sources(root))
        {
            string source = File.ReadAllText(file);
            if (!TakesProviderRow.IsMatch(source) || !BuildsTransport.IsMatch(source)) continue;
            if (NotARowClient.ContainsKey(Path.GetFileName(file))) continue;

            checkedFiles++;
            if (!Guarded.IsMatch(source)) offenders.Add(Path.GetRelativePath(root, file));
        }

        checkedFiles.Should().BeGreaterThan(10, "the scan found the provider-row clients");
        offenders.Should().BeEmpty(
            "a file that reads a provider row and builds an HTTP client, handler or vendor SDK client must give an " +
            "org-owned row the public-only transport (ProviderRowTransport); if it never dials the row's endpoint, " +
            "list it in NotARowClient with the reason. Offenders: " + string.Join("; ", offenders));
    }

    [Fact]
    public void The_provider_tester_passes_row_ownership_to_every_client_it_builds()
    {
        string source = File.ReadAllText(Path.Combine(
            SourceRoot(), "ReelBolt.Inference.Api", "Services", "Inference", "InferenceProviderTester.cs"));
        MatchCollection rows = Regex.Matches(
            source, @"(new Resolved\w+Provider\(|Resolved\w+Provider \w+ = new\(|\bnew\(id, name, kind)[^;]*?(\), ct\)|;)", RegexOptions.Singleline);

        rows.Count.Should().BeGreaterThan(9, "the scan found the rows the tester builds");
        rows.Select(m => m.Value).Where(v => !v.Contains("IsPlatformManaged", StringComparison.Ordinal)
                && !v.Contains("OrganizationId is null", StringComparison.Ordinal))
            .Should().BeEmpty("a test of an org-owned row must build its client as org-owned, or it dials unguarded");
    }

    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    [InlineData(InferenceProviderKind.Anthropic)]
    public void The_provider_tester_marks_an_org_chat_row_as_not_platform_managed(InferenceProviderKind kind)
    {
        ResolvedInferenceProvider org = ReelBolt.Inference.Api.Services.Inference.InferenceProviderTester.ChatProvider(
            Guid.NewGuid(), "x", kind, "https://e.example.com", "m", "k", 10, false, Guid.NewGuid(), InferenceProviderCapability.Chat);
        ResolvedInferenceProvider platform = ReelBolt.Inference.Api.Services.Inference.InferenceProviderTester.ChatProvider(
            Guid.NewGuid(), "x", kind, "https://e.example.com", "m", "k", 10, false, null, InferenceProviderCapability.Chat);

        org.IsPlatformManaged.Should().BeFalse();
        platform.IsPlatformManaged.Should().BeTrue();
    }
}
