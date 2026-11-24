#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>GET /projects/{id}/voices/library</c>: browsing the speech provider's public voice library.
/// Mocks only (Product decision 4) — the Fish listing below is an ASSUMED recorded fixture of
/// <c>GET /model</c> (must measure; docs/voiceover.md), never a live call.
/// </summary>
public class ProjectVoiceLibraryTests
{
    /// <summary>Assumed shape of Fish Audio's public GET /model answer.</summary>
    internal const string FishModelListingFixture = """
        {
          "total": 2,
          "items": [
            { "_id": "7f92f8afb8ec43bf81429cc1c9199cb1", "title": "Energetic Male", "languages": ["en", "zh"], "tags": ["male", "energetic"],
              "samples": [ { "title": "s", "audio": "https://public-platform.r2.fish.audio/s.mp3", "text": "hi" } ] },
            { "_id": "bf322df2096a46f18c579d0baa36f41d", "title": "Calm Narrator", "languages": ["en"], "tags": [], "samples": [] },
            { "title": "no id, skipped" }
          ]
        }
        """;

    private sealed class FakeUser(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "o@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private sealed class LibraryClient(VoiceLibraryPage page) : ISpeechSynthesisClient, IVoiceLibraryClient
    {
        public List<VoiceLibraryQuery> Queries { get; } = [];
        public bool Unsupported { get; set; }
        public bool Fails { get; set; }
        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<VoiceLibraryPage> ListLibraryVoicesAsync(VoiceLibraryQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            if (Unsupported) throw new NotSupportedException();
            if (Fails) throw new HttpRequestException("Connection refused (fish-internal:8080) vendor said: quota for acct-42");
            return Task.FromResult(page);
        }
    }

    private sealed class SynthesisOnly : ISpeechSynthesisClient
    {
        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Rig
    {
        public Guid OwnerId { get; } = Guid.NewGuid();
        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public VoiceLibraryCache Cache { get; } = new();
        public Mock<ISpeechSynthesisClientFactory> Factory { get; } = new();
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();

        public Rig(ISpeechSynthesisClient client, InferenceProviderKind kind = InferenceProviderKind.FishAudio, bool metered = false)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = OwnerId, OrganizationId = TestTenancy.OrgOf(OwnerId) };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            var resolved = new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "p", kind, "https://api.fish.audio", "s2.1-pro", "k", 30);
            Resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(resolved);
            // Same wrapping the real SpeechSynthesisClientFactory.Get applies when a recorder is registered.
            Factory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(
                metered ? new ReelBolt.Shared.Metering.MeteringSpeechSynthesisClient(client, resolved, Mock.Of<ReelBolt.Shared.Metering.IUsageRecorder>()) : client);
        }

        public ProjectVoicesController Controller(Guid? asUser = null)
        {
            FakeUser user = new(asUser ?? OwnerId);
            return new(Db, user, TestTenancy.Access(Db, user), new Mock<IFileStorageService>().Object, Resolver.Object,
                Factory.Object, new Mock<ITranscriptionClientFactory>().Object, NullLogger<ProjectVoicesController>.Instance, Cache);
        }
    }

    private static VoiceLibraryPage Page() => new(
        [new VoiceLibraryVoice("v1", "Energetic Male", ["en"], ["male"], true), new VoiceLibraryVoice("v2", "Calm", ["en"], [], false)], 2);

    [Fact]
    public async Task Maps_the_library_page_for_a_Fish_provider()
    {
        LibraryClient client = new(Page());
        Rig rig = new(client);

        var ok = (await rig.Controller().Library(rig.Project.Id, "male", "en", 2, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;

        var body = ok.Value.Should().BeOfType<VoiceLibraryResponse>().Subject;
        body.Total.Should().Be(2);
        body.Page.Should().Be(2);
        body.Items.Select(i => i.VoiceId).Should().Equal("v1", "v2");
        body.Items[0].PreviewAvailable.Should().BeTrue();
        client.Queries.Should().ContainSingle().Which.Should().Be(new VoiceLibraryQuery("male", "en", 2, ProjectVoicesController.LibraryPageSize));
    }

    [Fact]
    public async Task Browsing_through_the_metering_wrapper_reaches_the_vendor_library()
    {
        LibraryClient client = new(Page());
        Rig rig = new(client, metered: true);

        (await rig.Controller().Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();

        client.Queries.Should().ContainSingle();
    }

    [Fact]
    public async Task A_metered_provider_without_a_library_is_still_404()
    {
        Rig rig = new(new SynthesisOnly(), InferenceProviderKind.FishAudio, metered: true);

        var nf = (await rig.Controller().Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result.Should().BeOfType<NotFoundObjectResult>().Subject;

        nf.Value!.ToString().Should().Contain("voice_library_not_supported");
    }

    [Fact]
    public async Task A_provider_without_a_library_is_404_voice_library_not_supported()
    {
        foreach (ISpeechSynthesisClient c in new ISpeechSynthesisClient[] { new SynthesisOnly(), new LibraryClient(Page()) { Unsupported = true } })
        {
            Rig rig = new(c, InferenceProviderKind.OpenAICompatible);
            var nf = (await rig.Controller().Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result.Should().BeOfType<NotFoundObjectResult>().Subject;
            nf.Value!.ToString().Should().Contain("voice_library_not_supported");
        }
    }

    [Fact]
    public async Task The_same_search_is_cached_per_provider_for_the_next_call()
    {
        LibraryClient client = new(Page());
        Rig rig = new(client);

        await rig.Controller().Library(rig.Project.Id, "x", null, 1, CancellationToken.None);
        await rig.Controller().Library(rig.Project.Id, "x", null, 1, CancellationToken.None);
        await rig.Controller().Library(rig.Project.Id, "y", null, 1, CancellationToken.None);

        client.Queries.Select(q => q.Search).Should().Equal("x", "y");
    }

    [Fact]
    public async Task Another_users_project_is_forbidden_and_a_vendor_failure_is_502()
    {
        LibraryClient client = new(Page()) { Fails = true };
        Rig rig = new(client);

        (await rig.Controller(Guid.NewGuid()).Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result
            .Should().NotBeOfType<OkObjectResult>();
        var bad = (await rig.Controller().Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Subject;
        bad.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task A_vendor_failure_502_carries_a_generic_message_not_the_vendor_or_transport_text()
    {
        Rig rig = new(new LibraryClient(Page()) { Fails = true });

        var bad = (await rig.Controller().Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Subject;

        bad.StatusCode.Should().Be(502);
        string text = System.Text.Json.JsonSerializer.Serialize(bad.Value);
        text.Should().Contain("voice_library_failed");
        text.Should().NotContain("fish-internal").And.NotContain("8080").And.NotContain("acct-42");
    }

    [Fact]
    public void The_cache_key_is_length_prefixed_so_different_queries_never_share_an_entry()
    {
        // The old ':' join mapped (q "a:b", no language) and (q "a", language "b") to the same key.
        VoiceLibraryCache.Key("p", "a:b", null, 1).Should().NotBe(VoiceLibraryCache.Key("p", "a", "b", 1));
        VoiceLibraryCache.Key("p:a", null, null, 1).Should().NotBe(VoiceLibraryCache.Key("p", "a", null, 1));
        VoiceLibraryCache.Key("p", null, null, 1).Should().NotBe(VoiceLibraryCache.Key("p", "", null, 1));
        VoiceLibraryCache.Key("p", "x", "en", 2).Should().Be(VoiceLibraryCache.Key("p", "x", "en", 2));
    }

    [Fact]
    public async Task The_dedicated_cache_is_size_limited_and_every_entry_carries_a_size()
    {
        Rig rig = new(new LibraryClient(Page()));
        VoiceLibraryCache.Options.SizeLimit.Should().BeGreaterThan(0);

        // A size-limited MemoryCache throws on an entry with no Size, so this proves the Set carries one.
        (await rig.Controller().Library(rig.Project.Id, "x", null, 1, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        rig.Cache.Cache.Count.Should().Be(1);
    }

    [Fact]
    public async Task Vendor_strings_and_counts_are_capped()
    {
        List<VoiceLibraryVoice> many = Enumerable.Range(0, 40).Select(i => new VoiceLibraryVoice(
            "id" + i, new string('n', 1000), Enumerable.Range(0, 50).Select(j => new string('l', 100)).ToList(),
            Enumerable.Range(0, 50).Select(j => new string('t', 500)).ToList(), true)).ToList();
        many.Add(new VoiceLibraryVoice(new string('i', 600), "overlong id", [], [], false));
        Rig rig = new(new LibraryClient(new VoiceLibraryPage(many, 41)));

        var body = ((await rig.Controller().Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result as OkObjectResult)!.Value as VoiceLibraryResponse;

        body!.Items.Should().HaveCount(ProjectVoicesController.LibraryPageSize);
        body.Items.Should().OnlyContain(i => i.Name.Length <= VoiceLibraryCache.MaxNameChars
            && i.Tags.Count <= VoiceLibraryCache.MaxTagsPerVoice && i.Tags.All(t => t.Length <= VoiceLibraryCache.MaxTagChars)
            && i.Languages.Count <= VoiceLibraryCache.MaxLanguagesPerVoice && i.Languages.All(l => l.Length <= VoiceLibraryCache.MaxLanguageChars)
            && i.VoiceId.Length <= VoiceLibraryCache.MaxVoiceIdChars);
    }

    [Theory]
    [InlineData("en;drop")]
    [InlineData("EN")]
    [InlineData("english")]
    [InlineData("en-")]
    public async Task A_language_that_is_not_a_language_code_is_400(string language)
    {
        LibraryClient client = new(Page());
        Rig rig = new(client);

        (await rig.Controller().Library(rig.Project.Id, null, language, 1, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        client.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_language_code_with_a_region_is_accepted()
    {
        LibraryClient client = new(Page());
        Rig rig = new(client);

        (await rig.Controller().Library(rig.Project.Id, null, "pt-BR", 1, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        client.Queries.Should().ContainSingle().Which.Language.Should().Be("pt-BR");
    }

    [Fact]
    public async Task A_voice_cloned_on_the_platform_account_is_never_listed_even_from_the_cache()
    {
        LibraryClient client = new(Page());
        Rig rig = new(client);
        // A clone in ANOTHER org's project whose vendor id the listing returns.
        Guid otherOwner = Guid.NewGuid();
        Project other = new() { Id = Guid.NewGuid(), Name = "O", OwnerId = otherOwner, OrganizationId = TestTenancy.OrgOf(otherOwner) };
        rig.Db.Projects.Add(other);
        rig.Db.ProjectVoices.Add(new ProjectVoice { Id = Guid.NewGuid(), ProjectId = rig.Project.Id, Name = "lib", Source = ProjectVoiceSource.Library, RemoteVoiceId = "v1", CreatedAt = DateTime.UtcNow });
        rig.Db.SaveChanges();

        var first = ((await rig.Controller().Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result as OkObjectResult)!.Value as VoiceLibraryResponse;
        first!.Items.Select(i => i.VoiceId).Should().Equal(["v1", "v2"], "a Library row does not hide a public voice");

        rig.Db.ProjectVoices.Add(new ProjectVoice { Id = Guid.NewGuid(), ProjectId = other.Id, Name = "clone", Source = ProjectVoiceSource.Cloned, RemoteVoiceId = "v2", CreatedAt = DateTime.UtcNow });
        rig.Db.SaveChanges();

        var second = ((await rig.Controller().Library(rig.Project.Id, null, null, 1, CancellationToken.None)).Result as OkObjectResult)!.Value as VoiceLibraryResponse;
        second!.Items.Select(i => i.VoiceId).Should().Equal("v1");
        client.Queries.Should().ContainSingle("the second answer came from the cache and was still filtered");
    }

    [Fact]
    public async Task Out_of_range_paging_and_overlong_search_are_400()
    {
        Rig rig = new(new LibraryClient(Page()));

        (await rig.Controller().Library(rig.Project.Id, null, null, 0, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await rig.Controller().Library(rig.Project.Id, new string('a', 101), null, 1, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    private sealed class Stub(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task The_Fish_client_maps_the_recorded_GET_model_fixture()
    {
        Stub stub = new(FishModelListingFixture);
        FishAudioSpeechSynthesisClient client = new(new HttpClient(stub) { BaseAddress = new Uri("https://api.fish.audio") }, "s2.1-pro");

        VoiceLibraryPage page = await client.ListLibraryVoicesAsync(new VoiceLibraryQuery("energetic man", "en", 3, 24), CancellationToken.None);

        page.Total.Should().Be(2);
        page.Items.Should().HaveCount(2, "an item with no _id is skipped");
        page.Items[0].Should().BeEquivalentTo(new VoiceLibraryVoice("7f92f8afb8ec43bf81429cc1c9199cb1", "Energetic Male", ["en", "zh"], ["male", "energetic"], true));
        page.Items[1].PreviewAvailable.Should().BeFalse();
        stub.Seen.Should().ContainSingle().Which.PathAndQuery.Should()
            .Be("/model?page_size=24&page_number=3&sort_by=score&title=energetic%20man&language=en");
    }

    [Fact]
    public async Task The_Fish_client_stops_reading_a_listing_body_over_the_cap()
    {
        string huge = "{\"total\":1,\"items\":[{\"_id\":\"a\",\"title\":\"" + new string('x', 3 * 1024 * 1024) + "\"}]}";
        FishAudioSpeechSynthesisClient client = new(new HttpClient(new Stub(huge)) { BaseAddress = new Uri("https://api.fish.audio") }, null);

        Func<Task> act = () => client.ListLibraryVoicesAsync(new VoiceLibraryQuery(null, null, 1, 24), CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("size limit");
    }

    [Fact]
    public async Task The_Fish_client_surfaces_a_vendor_error_and_refuses_a_self_hosted_endpoint()
    {
        FishAudioSpeechSynthesisClient failing = new(new HttpClient(new Stub("{\"message\":\"nope\"}", HttpStatusCode.Unauthorized)) { BaseAddress = new Uri("https://api.fish.audio") }, null);
        Func<Task> act = () => failing.ListLibraryVoicesAsync(new VoiceLibraryQuery(null, null, 1, 24), CancellationToken.None);
        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("nope");

        // A non-hosted host is probed with GET /v1/references/list; a reference_ids answer means self-hosted.
        FishAudioSpeechSynthesisClient selfHosted = new(new HttpClient(new Stub("{\"reference_ids\":[]}")) { BaseAddress = new Uri("http://fish:8080") }, null);
        Func<Task> act2 = () => selfHosted.ListLibraryVoicesAsync(new VoiceLibraryQuery(null, null, 1, 24), CancellationToken.None);
        await act2.Should().ThrowAsync<NotSupportedException>();
    }
    [Fact]
    public async Task The_Fish_client_looks_one_public_voice_up_by_id()
    {
        Stub stub = new("""
            { "_id": "7f92f8afb8ec43bf81429cc1c9199cb1", "title": "Energetic Male", "visibility": "public", "languages": ["en"], "tags": ["male"], "samples": [ {} ] }
            """);
        FishAudioSpeechSynthesisClient client = new(new HttpClient(stub) { BaseAddress = new Uri("https://api.fish.audio") }, null);

        VoiceLibraryVoice? voice = await client.GetLibraryVoiceAsync("7f92f8afb8ec43bf81429cc1c9199cb1", CancellationToken.None);

        voice.Should().BeEquivalentTo(new VoiceLibraryVoice("7f92f8afb8ec43bf81429cc1c9199cb1", "Energetic Male", ["en"], ["male"], true));
        stub.Seen.Should().ContainSingle().Which.AbsolutePath.Should().Be("/model/7f92f8afb8ec43bf81429cc1c9199cb1");
    }

    [Theory]
    [InlineData("{\"_id\":\"a\",\"title\":\"t\",\"visibility\":\"private\"}", HttpStatusCode.OK)]
    [InlineData("{\"_id\":\"a\",\"title\":\"t\",\"visibility\":\"unlist\"}", HttpStatusCode.OK)]
    [InlineData("{\"_id\":\"someone-else\",\"title\":\"t\"}", HttpStatusCode.OK)]
    [InlineData("{\"message\":\"not found\"}", HttpStatusCode.NotFound)]
    public async Task The_Fish_client_answers_null_for_a_voice_that_is_not_a_public_library_voice(string body, HttpStatusCode status)
    {
        FishAudioSpeechSynthesisClient client = new(new HttpClient(new Stub(body, status)) { BaseAddress = new Uri("https://api.fish.audio") }, null);

        (await client.GetLibraryVoiceAsync("a", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task The_Fish_lookup_escapes_the_id_and_refuses_a_self_hosted_endpoint()
    {
        Stub stub = new("{\"message\":\"nope\"}", HttpStatusCode.NotFound);
        FishAudioSpeechSynthesisClient client = new(new HttpClient(stub) { BaseAddress = new Uri("https://api.fish.audio") }, null);
        await client.GetLibraryVoiceAsync("../v1/tts?x=1", CancellationToken.None);
        stub.Seen.Single().AbsolutePath.Should().Be("/model/..%2Fv1%2Ftts%3Fx%3D1");

        FishAudioSpeechSynthesisClient selfHosted = new(new HttpClient(new Stub("{\"reference_ids\":[]}")) { BaseAddress = new Uri("http://fish:8080") }, null);
        Func<Task> act = () => selfHosted.GetLibraryVoiceAsync("a", CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
