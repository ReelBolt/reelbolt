#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.PostTexts;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PostTexts;

/// <summary>Opt-in "read the video" and the web-search hashtag source, with fakes only (no provider is called).</summary>
public class PostTextVideoAndWebTests
{
    // ---------------------------------------------------------------------------------------------------
    // Web search hashtag source
    // ---------------------------------------------------------------------------------------------------

    private static readonly ResolvedWebSearchProvider Search = new(Guid.NewGuid(), "s", InferenceProviderKind.DeepSeek, "http://x", "m", "k", 20);

    private sealed class FakeSearch(Func<WebSearchRequest, WebSearchResponse> answer) : IWebSearchClient, IWebSearchClientFactory
    {
        public List<string> Queries { get; } = [];

        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken ct)
        {
            Queries.Add(request.Query);
            return Task.FromResult(answer(request));
        }

        public IWebSearchClient Create(ResolvedWebSearchProvider provider) => this;
    }

    private static WebSearchResponse Response(bool restricted = false) => new(
        "p",
        [
            new WebSearchResult("Lake hashtags", "https://blog.example.com/a", "Use #lake #sunrise and #lake today"),
            new WebSearchResult("More", "https://other.example.org/b", "Try #Sunrise #calmwater, see https://x.io #1234")
        ],
        null, [], 1, null, restricted);

    private static WebSearchHashtagSource Source(ResolvedWebSearchProvider? provider, FakeSearch search)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveWebSearchAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        return new WebSearchHashtagSource(resolver.Object, search, NullLogger<WebSearchHashtagSource>.Instance);
    }

    private static HashtagSourceRequest HashRequest(string platform = "instagram_reels", bool single = false) =>
        new(platform, "en", "Lake trip", ["modeltag"], single);

    [Fact]
    public void The_query_has_no_links_and_tags_are_read_by_code_most_repeated_first()
    {
        WebSearchHashtagSource.BuildQuery("Trip https://evil.example/x www.a.com <b>lake</b>", "Instagram Reels")
            .Should().NotContain("http").And.NotContain("www").And.NotContain("<");
        WebSearchHashtagSource.BuildQuery(new string('a', 400), "X").Length.Should().BeLessThanOrEqualTo(WebSearchLimits.MaxQueryChars);

        var tags = WebSearchHashtagSource.ExtractTags(Response().Results);
        tags.Select(t => t.Tag).Should().Equal("lake", "sunrise", "calmwater");
    }

    [Fact]
    public async Task Web_tags_are_cited_labelled_web_and_one_search_serves_every_platform()
    {
        var search = new FakeSearch(_ => Response());
        WebSearchHashtagSource source = Source(Search, search);

        HashtagSuggestion first = await source.SuggestAsync(HashRequest("instagram_reels"), CancellationToken.None);
        HashtagSuggestion second = await source.SuggestAsync(HashRequest("tiktok"), CancellationToken.None);

        first.Kind.Should().Be(HashtagKind.WebSearched);
        first.Tags.Should().StartWith(["lake", "sunrise"]);
        first.Sources!.Select(c => c.Host).Should().Contain("blog.example.com");
        first.Label.Should().Contain("cited").And.NotContain("trend");
        second.Kind.Should().Be(HashtagKind.WebSearched);
        search.Queries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Only_http_and_https_pages_are_cited_or_read_for_tags()
    {
        var search = new FakeSearch(_ => new WebSearchResponse(
            "p",
            [
                new WebSearchResult("bad", "javascript:alert(1)", "#evil"),
                new WebSearchResult("data", "data:text/html,<b>", "#evil2"),
                new WebSearchResult("file", "file:///etc/passwd", "#evil3"),
                new WebSearchResult("ok", "https://good.example.com/p", "#fine")
            ],
            null, [], 1, null, false));

        HashtagSuggestion s = await Source(Search, search).SuggestAsync(HashRequest(), CancellationToken.None);

        s.Kind.Should().Be(HashtagKind.WebSearched);
        s.Tags.Should().Contain("fine").And.NotContain(["evil", "evil2", "evil3"]);
        s.Sources!.Should().ContainSingle().Which.Url.Should().Be("https://good.example.com/p");
        WebSearchHashtagSource.IsWebUrl("ftp://a.example/x").Should().BeFalse();
        WebSearchHashtagSource.IsWebUrl("http://a.example/x").Should().BeTrue();
    }

    [Fact]
    public async Task The_web_disclosure_says_the_name_and_about_text_are_the_query_even_without_read_video()
    {
        using Rig r = Build(web: true);
        var body = (await r.Controller.Estimate(r.ProjectId, r.OutputId, new PostTextRequest(["x"], HashtagSource: "web"), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PostTextEstimateResponse>().Subject;

        body.Disclosure.Should().Contain("project name and the \"about\" text").And.NotContain("still frames");
    }

    [Fact]
    public async Task No_provider_a_restricted_provider_a_failure_or_no_tags_fall_back_to_the_model_with_a_note()
    {
        async Task<HashtagSuggestion> Run(ResolvedWebSearchProvider? p, FakeSearch s) =>
            await Source(p, s).SuggestAsync(HashRequest(), CancellationToken.None);

        HashtagSuggestion none = await Run(null, new FakeSearch(_ => Response()));
        HashtagSuggestion gemini = await Run(Search with { Kind = InferenceProviderKind.Gemini }, new FakeSearch(_ => Response()));
        HashtagSuggestion restricted = await Run(Search, new FakeSearch(_ => Response(restricted: true)));
        HashtagSuggestion failed = await Run(Search, new FakeSearch(_ => throw new WebSearchException(WebSearchException.Timeout, "t")));
        HashtagSuggestion empty = await Run(Search, new FakeSearch(_ => new WebSearchResponse("p", [new WebSearchResult("t", "https://a.example/x", "no tags here")], null, [], 1, null, false)));

        foreach (HashtagSuggestion s in new[] { none, gemini, restricted, failed, empty })
        {
            s.Kind.Should().Be(HashtagKind.ModelSuggested);
            s.Tags.Should().Equal("modeltag");
            s.Sources.Should().BeNull();
            s.Note.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task The_writer_uses_the_web_source_only_when_asked_and_still_validates_its_tags()
    {
        const string answer = "{\"posts\":[{\"platform\":\"x\",\"title\":\"\",\"body\":\"A calm lake\",\"hashtags\":[\"modeltag\"]}]}";
        var chat = new Mock<IAgentChatClientProvider>();
        chat.Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UsageReportingChatClient(1, 1, answer: answer));
        var gate = new Mock<IContentModerationGate>();
        gate.Setup(g => g.ScreenGeneratedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var search = new FakeSearch(_ => Response());
        var writer = new PostTextWriter(chat.Object, gate.Object, NullLogger<PostTextWriter>.Instance, null, null, Source(Search, search));

        PostTextWriteRequest Req(bool web) => new(
            new PostTextContext("Lake trip", null, null, null, null, null, null, null, null),
            [PostTextPlatformLimits.Find("x")!], "en", null,
            new ModerationAttribution(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, ModerationSurface.GeneratedText, ModerationContentKind.Text),
            null, web);

        PostTextWriteResult off = (await writer.WriteAsync(Req(false), CancellationToken.None))!;
        off.HashtagBasis!.Kind.Should().Be("ModelSuggested");
        search.Queries.Should().BeEmpty("the default is free: no search without the opt-in");

        PostTextWriteResult on = (await writer.WriteAsync(Req(true), CancellationToken.None))!;
        on.HashtagBasis!.Kind.Should().Be("WebSearched");
        on.HashtagBasis.Sources.Should().NotBeEmpty();
        on.Drafts[0].Draft.Hashtags.Count.Should().BeLessThanOrEqualTo(PostTextPlatformLimits.Find("x")!.MaxHashtags);
        search.Queries.Should().HaveCount(1);
    }

    // ---------------------------------------------------------------------------------------------------
    // Read the video
    // ---------------------------------------------------------------------------------------------------

    private sealed class Rig : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public required PostTextsController Controller { get; init; }
        public required Guid ProjectId { get; init; }
        public required Guid OutputId { get; init; }
        public required Billing.FakeNonWorkflowCharges Charges { get; init; }
        public required Mock<IOutputDescriber> Describer { get; init; }
        public required List<PostTextWriteRequest> Written { get; init; }
        public void Dispose() => Db.Dispose();
    }

    private static Rig Build(bool vision = true, bool storedTranscript = false, bool stills = true, bool asr = true, bool web = false, IStoredCopyGate? gate = null)
    {
        Guid owner = Guid.NewGuid();
        var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var project = new Project { Id = Guid.NewGuid(), Name = "Trip", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner) };
        var file = new ProjectFile { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "r.mp4", MimeType = "video/mp4", StorageKey = $"projects/{project.Id}/r.mp4" };
        db.Projects.Add(project);
        db.ProjectFiles.Add(file);
        if (storedTranscript)
        {
            var result = new MediaAnalysisResult
            {
                Id = Guid.NewGuid(),
                OrganizationId = project.OrganizationId,
                Digest = "d",
                Kind = MediaAnalysisKinds.Transcript,
                ResultJson = JsonSerializer.Serialize(new AnalysisTranscriptResult("en", [new AnalysisWord("hello", 0, 1, false), new AnalysisWord("lake", 1, 2, false)], false), MediaAnalysisJson.Options)
            };
            db.MediaAnalysisResults.Add(result);
            db.MediaAnalysisRequests.Add(new MediaAnalysisRequest
            {
                Id = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                ProjectId = project.Id,
                OrganizationId = project.OrganizationId,
                MediaRef = $"file:{file.Id}",
                Kind = MediaAnalysisKinds.Transcript,
                State = MediaAnalysisStates.Ready,
                ResultId = result.Id
            });
        }

        db.SaveChanges();

        var source = new OutputSource(file.Id, "file", "r.mp4", "video/mp4", 100, file.StorageKey, new MediaSourceRef(MediaSourceKind.ProjectFile, file.Id), file.Id, 20.0);
        var describer = new Mock<IOutputDescriber>();
        describer.Setup(d => d.FindSourceAsync(project.Id, file.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        describer.Setup(d => d.FindStillsAsync(project.Id, source, It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stills ? (IReadOnlyList<OutputStillObject>)[new OutputStillObject(1, "a"), new OutputStillObject(5, "b")] : null);

        var visionProvider = new ResolvedInferenceProvider(Guid.NewGuid(), "v", InferenceProviderKind.OpenAICompatible, "http://x", "vis", "", 30);
        var providers = new Mock<IInferenceProviderResolver>();
        providers.Setup(p => p.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(vision ? visionProvider : null);
        providers.Setup(p => p.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(asr ? new ResolvedTranscriptionProvider(Guid.NewGuid(), "t", InferenceProviderKind.OpenAICompatible, "http://x", "whisper", "", 30) : null);
        providers.Setup(p => p.ResolveWebSearchAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(web ? Search : null);

        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(project.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(new UsageReportingChatClient(10, 10, answer: "A lake at dawn with a small boat."));
        var reader = new PostTextVideoReader(db, describer.Object, providers.Object, storage.Object, new StillsExtractionGate(), NullLogger<PostTextVideoReader>.Instance, factory.Object, gate);

        var written = new List<PostTextWriteRequest>();
        var writer = new Mock<IPostTextWriter>();
        writer.Setup(w => w.WriteAsync(It.IsAny<PostTextWriteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PostTextWriteRequest r, CancellationToken _) =>
            {
                written.Add(r);
                return new PostTextWriteResult([PostTextValidator.Check(new PostTextDraft("x", null, "Calm", []), PostTextPlatformLimits.Find("x")!)], false, false, null);
            });

        var user = new Mock<ICurrentUser>();
        user.SetupGet(u => u.UserId).Returns(owner);
        user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(owner));
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
        var charges = new Billing.FakeNonWorkflowCharges();
        var controller = new PostTextsController(
            db, user.Object, TestTenancy.Access(db, user.Object), writer.Object, new PostTextContextBuilder(db),
            new PostTextRateLimiter(Microsoft.Extensions.Options.Options.Create(new PostTextOptions())), charges,
            null, null, null, reader, providers.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return new Rig { Db = db, Controller = controller, ProjectId = project.Id, OutputId = file.Id, Charges = charges, Describer = describer, Written = written };
    }

    [Fact]
    public async Task Without_the_opt_in_nothing_is_read_or_priced_beyond_the_text()
    {
        using Rig r = Build();
        var body = (await r.Controller.Estimate(r.ProjectId, r.OutputId, new PostTextRequest(["x"]), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PostTextEstimateResponse>().Subject;

        body.LineItems.Select(i => i.Kind).Should().Equal("text");
        body.Disclosure.Should().BeNull();
        body.Video.Should().BeNull();
        r.Describer.Verify(d => d.FindStillsAsync(It.IsAny<Guid>(), It.IsAny<OutputSource>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_estimate_lists_text_frames_and_transcript_and_states_what_leaves_with_counts_only()
    {
        using Rig r = Build(stills: true, storedTranscript: false, web: true);
        var body = (await r.Controller.Estimate(r.ProjectId, r.OutputId, new PostTextRequest(["x"], ReadVideo: true, HashtagSource: "web"), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PostTextEstimateResponse>().Subject;

        body.LineItems.Select(i => i.Kind).Should().Equal("text", "frames", "transcript", "webSearch");
        body.LineItems.Single(i => i.Kind == "frames").State.Should().Be("ready");
        PostTextLineItem transcript = body.LineItems.Single(i => i.Kind == "transcript");
        transcript.State.Should().Be("missing");
        transcript.HeldSeparately.Should().BeTrue();
        transcript.Credits.Should().BeGreaterThan(0);
        body.Video!.StartTranscript!.Path.Should().EndWith($"/media/file:{r.OutputId}/analysis");
        body.Disclosure.Should().Contain("6 still frames").And.Contain("project name and the \"about\" text").And.Contain("web search provider").And.NotContain(r.OutputId.ToString());
        body.TotalCredits.Should().BeGreaterThan(body.Credits!.Value, "the transcript is on top of the hold");
        body.BillingEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_transcript_or_frames_answer_202_preparing_with_no_hold_and_no_model_call()
    {
        using Rig r = Build(stills: false);

        var accepted = (await r.Controller.Write(r.ProjectId, r.OutputId, new PostTextRequest(["x"], ReadVideo: true), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        JsonSerializer.SerializeToElement(accepted.Value).GetProperty("status").GetString().Should().Be("preparing");
        r.Written.Should().BeEmpty();
        r.Charges.Holds.Should().BeEmpty();
        r.Describer.Verify(d => d.RequestStillsAsync(r.ProjectId, It.IsAny<OutputSource>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ready_frames_and_transcript_are_read_fenced_inside_one_hold_that_covers_the_frames()
    {
        using Rig r = Build(storedTranscript: true, stills: true);

        PostTextResponse body = (await r.Controller.Write(r.ProjectId, r.OutputId, new PostTextRequest(["x"], ReadVideo: true), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PostTextResponse>().Subject;

        body.Used.VideoTranscript.Should().BeTrue();
        body.Used.Frames.Should().BeTrue();
        body.Video!.Transcript.Should().Be("ready");
        PostTextContext seen = r.Written.Single().Context;
        seen.RenderTranscript.Should().Be("hello lake");
        seen.FrameDescription.Should().Contain("lake at dawn");
        seen.Render().Should().Contain("Speech in the finished video").And.EndWith(PostTextContext.EndMarker);
        r.Charges.Holds.Should().ContainSingle().Which.EstimateMillis.Should().BeGreaterThan(3_000, "the hold covers the vision call");
        r.Charges.Settled.Should().ContainSingle();
    }

    [Fact]
    public async Task No_vision_provider_skips_the_frames_with_a_flag_and_never_fails()
    {
        using Rig r = Build(vision: false, storedTranscript: true);

        PostTextResponse body = (await r.Controller.Write(r.ProjectId, r.OutputId, new PostTextRequest(["x"], ReadVideo: true), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PostTextResponse>().Subject;

        body.Video!.Frames.Should().Be("skipped");
        body.Video.FramesNote.Should().NotBeNullOrWhiteSpace();
        body.Used.Frames.Should().BeFalse();
        body.Used.VideoTranscript.Should().BeTrue();
    }

    [Fact]
    public async Task A_quarantined_or_held_output_is_refused_409_with_the_gate_code_and_nothing_is_held_or_read()
    {
        var gate = new Mock<IStoredCopyGate>();
        gate.Setup(g => g.GateStoredCopyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishModerationOutcome.Blocked("This video may violate the content policy."));
        using Rig r = Build(storedTranscript: true, gate: gate.Object);

        ObjectResult write = (await r.Controller.Write(r.ProjectId, r.OutputId, new PostTextRequest(["x"], ReadVideo: true), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Subject;
        ObjectResult estimate = (await r.Controller.Estimate(r.ProjectId, r.OutputId, new PostTextRequest(["x"], ReadVideo: true), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        foreach (ObjectResult refused in new[] { write, estimate })
        {
            refused.StatusCode.Should().Be(StatusCodes.Status409Conflict);
            JsonSerializer.SerializeToElement(refused.Value).GetProperty("code").GetString().Should().Be("CONTENT_BLOCKED");
        }

        r.Written.Should().BeEmpty();
        r.Charges.Holds.Should().BeEmpty();
        r.Describer.Verify(d => d.FindStillsAsync(It.IsAny<Guid>(), It.IsAny<OutputSource>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never);

        // The same output without the opt-in is not read, so the gate is not consulted.
        (await r.Controller.Write(r.ProjectId, r.OutputId, new PostTextRequest(["x"]), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Disclosures_and_line_item_notes_never_say_trending()
    {
        using Rig r = Build(vision: false, asr: false, web: true);
        var body = (await r.Controller.Estimate(r.ProjectId, r.OutputId, new PostTextRequest(["x"], ReadVideo: true, HashtagSource: "web"), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PostTextEstimateResponse>().Subject;

        string everything = JsonSerializer.Serialize(body);
        everything.Should().NotContainEquivalentOf("trending");
    }

    [Fact]
    public async Task An_unknown_hashtag_source_is_400()
    {
        using Rig r = Build();
        (await r.Controller.Write(r.ProjectId, r.OutputId, new PostTextRequest(["x"], HashtagSource: "scrape"), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
