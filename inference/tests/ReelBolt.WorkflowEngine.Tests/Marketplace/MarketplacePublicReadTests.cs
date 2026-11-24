using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Shared.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplacePublicReadTests
{
    private static readonly byte[] PreviewBytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
    private static readonly string PosterSha = Convert.ToHexString(SHA256.HashData(new byte[] { 7, 7, 7 })).ToLowerInvariant();
    private static readonly string Sha = Convert.ToHexString(SHA256.HashData(PreviewBytes)).ToLowerInvariant();

    private sealed class FakeObjects : IMarketplacePreviewObjects
    {
        public readonly Dictionary<string, byte[]> Store = new();
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
            Store.TryGetValue(key, out byte[]? b) ? Task.FromResult<Stream>(new MemoryStream(b)) : throw new FileNotFoundException();
        public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct, IReadOnlySet<string>? exclude = null) => Task.CompletedTask;
        public Task MoveAsync(string fromKey, string toKey, CancellationToken ct) => Task.CompletedTask;
        public Task CopyAsync(string fromKey, string toKey, CancellationToken ct) => Task.CompletedTask;
        public Task DeletePrefixAsync(string prefix, CancellationToken ct, IReadOnlySet<string>? exclude = null) => Task.CompletedTask;
    }

    private sealed class H
    {
        public readonly InferenceApiDbContext Db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly FakeObjects Objects = new();
        public readonly Mock<IAmazonS3> S3 = new();
        public bool Enabled = true;
        private int _n;

        public H()
        {
            S3.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string b, string k, CancellationToken _) => Objects.Store.TryGetValue(k, out byte[]? v)
                    ? new GetObjectMetadataResponse { ContentLength = v.Length }
                    : throw new AmazonS3Exception("nf") { StatusCode = System.Net.HttpStatusCode.NotFound });
            S3.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((GetObjectRequest r, CancellationToken _) =>
                {
                    byte[] all = Objects.Store[r.Key];
                    byte[] part = r.ByteRange is null ? all : all[(int)r.ByteRange.Start..((int)r.ByteRange.End + 1)];
                    return new GetObjectResponse { ResponseStream = new MemoryStream(part) };
                });
        }

        public Guid Add(string name, RecipeStatus status = RecipeStatus.Listed, bool hidden = false, PreviewStatus preview = PreviewStatus.Ready,
            int copies = 0, int ratingSum = 0, int ratingCount = 0, string category = "ads", string cost = "Uses credits",
            string[]? stepTypes = null, string desc = "", DateTime? listedAt = null, bool store = true, string? sha = null, string? posterSha = null)
        {
            Guid id = Guid.NewGuid();
            Guid vid = Guid.NewGuid();
            Db.MarketplaceRecipes.Add(new MarketplaceRecipe
            {
                Id = id,
                AuthorUserId = Guid.NewGuid(),
                AuthorOrgId = Guid.NewGuid(),
                Status = status,
                HiddenByReports = hidden,
                Name = name,
                Description = desc,
                Category = category,
                Language = "en",
                CostClass = cost,
                StepCount = 2,
                StepTypes = stepTypes ?? ["Agent", "Voiceover"],
                CurrentVersionId = vid,
                AuthorLabel = "Ann",
                ListedAt = listedAt ?? DateTime.UtcNow.AddMinutes(-_n++),
                CopyOrgs = copies,
                RatingSum = ratingSum,
                RatingCount = ratingCount
            });
            Db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion
            {
                Id = vid,
                RecipeId = id,
                N = 1,
                PreviewStatus = preview,
                PreviewKey = MarketplacePreviewKeys.Preview(id, vid),
                PosterKey = MarketplacePreviewKeys.Poster(id, vid),
                PreviewSha256 = sha ?? Sha,
                PosterSha256 = posterSha ?? PosterSha,
                PreviewSeconds = 8,
                SnapshotJson = """{"schemaVersion":1,"steps":[{"order":1,"stepType":"Agent","label":"Write","maxIterations":1}],"slots":[{"kind":"voice","stepOrder":2,"field":"voiceId","label":"Pick a voice"}],"removed":[]}"""
            });
            Db.SaveChanges();
            if (store)
            {
                Objects.Store[MarketplacePreviewKeys.Preview(id, vid)] = PreviewBytes;
                Objects.Store[MarketplacePreviewKeys.Poster(id, vid)] = [7, 7, 7];
            }

            return id;
        }

        public MarketplacePublicController Controller(string? range = null, string method = "GET")
        {
            IOptionsSnapshot<MarketplaceOptions> opts = Mock.Of<IOptionsSnapshot<MarketplaceOptions>>(o => o.Value == new MarketplaceOptions { Enabled = Enabled });
            var query = new MarketplacePublicQuery(Db, new InMemoryRecipeSearch(), opts);
            IConfiguration cfg = new ConfigurationBuilder().Build();
            var c = new MarketplacePublicController(query, S3.Object, Objects, cfg, NullLogger<MarketplacePublicController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            c.HttpContext.Request.Method = method;
            if (range is not null)
                c.HttpContext.Request.Headers["Range"] = range;
            return c;
        }

        public async Task<RecipeListDto> List(string? sort = null, string? cursor = null, int? limit = null, string? q = null, string? cat = null)
        {
            IActionResult r = await Controller().List(q, cat, null, null, null, sort, cursor, limit, default);
            return (RecipeListDto)((ObjectResult)r).Value!;
        }
    }

    [Fact]
    public async Task Visibility_matrix_lists_only_listed_visible_ready_recipes()
    {
        H h = new();
        Guid ok = h.Add("ok");
        h.Add("draft", RecipeStatus.Draft);
        h.Add("pending", RecipeStatus.Pending);
        h.Add("rejected", RecipeStatus.Rejected);
        h.Add("removed", RecipeStatus.Removed);
        h.Add("unpub", RecipeStatus.Unpublished);
        h.Add("hidden", hidden: true);
        h.Add("failed", preview: PreviewStatus.Failed);
        h.Add("preparing", preview: PreviewStatus.Preparing);

        (await h.List()).Items.Select(i => i.Id).Should().Equal(ok);
        Guid hidden = h.Db.MarketplaceRecipes.Single(r => r.Name == "hidden").Id;
        foreach (Guid id in h.Db.MarketplaceRecipes.Where(r => r.Id != ok).Select(r => r.Id).ToList())
        {
            (await h.Controller().Detail(id, default)).Should().BeOfType<EmptyResult>();
            (await h.Controller().Preview(id, default)).Should().BeOfType<EmptyResult>();
            (await h.Controller().Poster(id, default)).Should().BeOfType<EmptyResult>();
        }

        (await h.Controller().Detail(ok, default)).Should().BeOfType<ObjectResult>();
        hidden.Should().NotBe(ok);
    }

    [Fact]
    public async Task Bare_404_has_no_body_and_no_headers_and_is_identical_for_missing_and_hidden()
    {
        H h = new();
        Guid draft = h.Add("d", RecipeStatus.Draft);
        MarketplacePublicController a = h.Controller();
        MarketplacePublicController b = h.Controller();
        IActionResult r1 = await a.Detail(draft, default);
        IActionResult r2 = await b.Detail(Guid.NewGuid(), default);
        r1.Should().BeOfType<EmptyResult>();
        r2.Should().BeOfType<EmptyResult>();
        a.Response.StatusCode.Should().Be(404);
        b.Response.StatusCode.Should().Be(404);
        a.Response.Headers.Should().BeEquivalentTo(b.Response.Headers);
        a.Response.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task Flag_off_answers_the_same_bare_404_through_the_filter()
    {
        var filter = new MarketplaceEnabledFilter(Mock.Of<IOptionsSnapshot<MarketplaceOptions>>(o => o.Value == new MarketplaceOptions { Enabled = false }));
        var ctx = new Microsoft.AspNetCore.Mvc.Filters.ResourceExecutingContext(
            new ActionContext(new DefaultHttpContext(), new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            [], []);
        filter.OnResourceExecuting(ctx);
        ctx.Result.Should().BeOfType<NotFoundResult>();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Cursor_tamper_is_400_never_an_exception()
    {
        H h = new();
        for (int i = 0; i < 3; i++)
            h.Add("r" + i);
        foreach (string bad in new[] { "!!!", "AAAA", "bm9wZQ", new string('A', 500), "MXxufDF8MHww" })
        {
            IActionResult r = await h.Controller().List(null, null, null, null, null, null, bad, null, default);
            ((StatusCodeResult)r).StatusCode.Should().Be(400);
        }

        RecipeListDto page = await h.List(limit: 1);
        page.NextCursor.Should().NotBeNull();
        IActionResult wrongSort = await h.Controller().List(null, null, null, null, null, "copied", page.NextCursor, null, default);
        ((StatusCodeResult)wrongSort).StatusCode.Should().Be(400);
        IActionResult badSort = await h.Controller().List(null, null, null, null, null, "bogus", null, null, default);
        ((StatusCodeResult)badSort).StatusCode.Should().Be(400);
        IActionResult longQ = await h.Controller().List(new string('x', 101), null, null, null, null, null, null, null, default);
        ((StatusCodeResult)longQ).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Page_size_defaults_and_caps()
    {
        H h = new();
        for (int i = 0; i < 60; i++)
            h.Add("r" + i);
        (await h.List()).Items.Should().HaveCount(24);
        (await h.List(limit: 500)).Items.Should().HaveCount(50);
        (await h.List(limit: 0)).Items.Should().HaveCount(24);
        (await h.List(limit: 3)).Items.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("copied")]
    [InlineData("rated")]
    public async Task Sort_is_stable_across_pages_without_duplicates_or_gaps(string sort)
    {
        H h = new();
        for (int i = 0; i < 23; i++)
            h.Add("r" + i, copies: i % 4, ratingSum: i % 3 == 0 ? 20 : 3, ratingCount: i % 3 == 0 ? 5 : 1, listedAt: DateTime.UtcNow.AddHours(-(i / 2)));
        List<Guid> seen = [];
        string? cursor = null;
        do
        {
            RecipeListDto page = await h.List(sort, cursor, 5);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        seen.Should().HaveCount(23).And.OnlyHaveUniqueItems();
        if (sort == "copied")
        {
            List<int> copies = seen.Select(id => h.Db.MarketplaceRecipes.Single(r => r.Id == id).CopyOrgs).ToList();
            copies.Should().BeInDescendingOrder();
        }
    }

    [Fact]
    public async Task Rated_sort_puts_unrated_after_and_hides_average_under_the_minimum()
    {
        H h = new();
        h.Add("low", ratingSum: 5, ratingCount: 1);
        Guid top = h.Add("top", ratingSum: 25, ratingCount: 5);
        h.Add("mid", ratingSum: 20, ratingCount: 5);
        RecipeListDto list = await h.List("rated");
        list.Items.Select(i => i.Name).Should().Equal("top", "mid", "low");
        list.Items[0].RatingAverage.Should().Be(5);
        list.Items[2].RatingAverage.Should().BeNull();
        list.Items[2].RatingCount.Should().Be(1);
        top.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Search_and_filters_and_facets()
    {
        H h = new();
        h.Add("Product teaser", category: "ads", desc: "short promo", cost: "No credits", stepTypes: ["Agent"]);
        h.Add("Podcast clips", category: "audio", cost: "Uses credits");
        h.Add("Hidden teaser", hidden: true, category: "ads");
        (await h.List(q: "teaser")).Items.Select(i => i.Name).Should().Equal("Product teaser");
        (await h.List(cat: "audio")).Items.Select(i => i.Name).Should().Equal("Podcast clips");
        IActionResult byCost = await h.Controller().List(null, null, "No credits", null, null, null, null, null, default);
        ((RecipeListDto)((ObjectResult)byCost).Value!).Items.Should().ContainSingle();
        IActionResult byStep = await h.Controller().List(null, null, null, "Voiceover", null, null, null, null, default);
        ((RecipeListDto)((ObjectResult)byStep).Value!).Items.Should().ContainSingle();

        MarketplacePublicController c = h.Controller();
        FacetsDto f = (FacetsDto)((ObjectResult)await c.Facets(default)).Value!;
        f.Categories.Should().BeEquivalentTo([new FacetEntryDto("ads", 1), new FacetEntryDto("audio", 1)]);
        f.CostClasses.Select(x => x.Key).Should().BeEquivalentTo("No credits", "Uses credits");
        f.StepTypes.Single(x => x.Key == "Agent").Count.Should().Be(2);
        c.Response.Headers["Cache-Control"].ToString().Should().Be("public, max-age=60");
    }

    [Fact]
    public async Task List_and_detail_headers_and_detail_content()
    {
        H h = new();
        Guid id = h.Add("A recipe", desc: new string('d', 300));
        MarketplacePublicController c = h.Controller();
        RecipeListDto list = (RecipeListDto)((ObjectResult)await c.List(null, null, null, null, null, null, null, null, default)).Value!;
        c.Response.Headers["Cache-Control"].ToString().Should().Be("public, max-age=30");
        c.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        list.Items[0].Description.Should().HaveLength(200);

        MarketplacePublicController d = h.Controller();
        RecipeDetailDto detail = (RecipeDetailDto)((ObjectResult)await d.Detail(id, default)).Value!;
        d.Response.Headers["Cache-Control"].ToString().Should().Be("public, max-age=60");
        detail.Description.Should().HaveLength(300);
        detail.Steps.Should().ContainSingle().Which.Label.Should().Be("Write");
        detail.Slots.Should().ContainSingle().Which.Kind.Should().Be("voice");
        detail.CostNote.Should().Contain("credits");
    }

    [Fact]
    public async Task Public_dtos_carry_no_identifier_properties()
    {
        string[] forbidden = ["author", "org", "user", "project", "workflow", "provider", "file", "voiceid", "stepresult", "storage", "sha"];
        foreach (Type t in new[] { typeof(RecipeCardDto), typeof(RecipeDetailDto), typeof(StepDto), typeof(PromptDto), typeof(SlotDto), typeof(RecipeInputDto), typeof(FacetsDto), typeof(RecipeListDto) })
        {
            foreach (PropertyInfo p in t.GetProperties())
            {
                string n = p.Name.ToLowerInvariant();
                foreach (string f in forbidden)
                {
                    if (n.Contains(f))
                        n.Should().Be("authorlabel", $"{t.Name}.{p.Name} must not carry an identifier");
                }
            }
        }

        H h = new();
        Guid id = h.Add("x");
        var recipe = h.Db.MarketplaceRecipes.Single(r => r.Id == id);
        string json = System.Text.Json.JsonSerializer.Serialize(((ObjectResult)await h.Controller().Detail(id, default)).Value);
        json.Should().NotContain(recipe.AuthorUserId.ToString()).And.NotContain(recipe.AuthorOrgId.ToString()).And.NotContain("marketplace/");
    }

    [Fact]
    public async Task Preview_without_range_is_200_and_with_range_is_206()
    {
        H h = new();
        Guid id = h.Add("p");
        MarketplacePublicController full = h.Controller();
        IActionResult r = await full.Preview(id, default);
        r.Should().BeOfType<FileStreamResult>().Which.ContentType.Should().Be("video/mp4");
        full.Response.Headers["Accept-Ranges"].ToString().Should().Be("bytes");
        full.Response.Headers["Cache-Control"].ToString().Should().Be("public, max-age=120");
        full.Response.StatusCode.Should().Be(200);
        full.Response.ContentLength.Should().Be(100);

        MarketplacePublicController part = h.Controller("bytes=10-19");
        await part.Preview(id, default);
        part.Response.StatusCode.Should().Be(206);
        part.Response.Headers["Content-Range"].ToString().Should().Be("bytes 10-19/100");
        part.Response.ContentLength.Should().Be(10);

        MarketplacePublicController bad = h.Controller("bytes=500-600");
        IActionResult unsat = await bad.Preview(id, default);
        ((StatusCodeResult)unsat).StatusCode.Should().Be(416);
    }

    [Fact]
    public async Task Head_requests_are_accepted_by_every_route()
    {
        foreach (MethodInfo m in typeof(MarketplacePublicController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var verbs = m.GetCustomAttribute<AcceptVerbsAttribute>();
            verbs.Should().NotBeNull(m.Name);
            verbs!.HttpMethods.Should().BeEquivalentTo("GET", "HEAD");
        }

        H h = new();
        Guid id = h.Add("p");
        IActionResult r = await h.Controller(method: "HEAD").Poster(id, default);
        r.Should().BeOfType<FileStreamResult>().Which.ContentType.Should().Be("image/jpeg");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Missing_object_wrong_hash_and_missing_hash_are_the_same_bare_404()
    {
        H h = new();
        Guid missing = h.Add("m", store: false);
        Guid wrong = h.Add("w", sha: new string('a', 64));
        Guid noSha = h.Add("n");
        h.Db.MarketplaceRecipeVersions.Single(v => v.RecipeId == noSha).PreviewSha256 = null;
        h.Db.SaveChanges();
        foreach (Guid id in new[] { missing, wrong, noSha })
        {
            MarketplacePublicController c = h.Controller();
            (await c.Preview(id, default)).Should().BeOfType<EmptyResult>();
            c.Response.Headers.Should().BeEmpty();
        }

        MarketplacePublicController poster = h.Controller();
        (await poster.Poster(missing, default)).Should().BeOfType<EmptyResult>();
        poster.Response.Headers.Should().BeEmpty();
    }

    [Fact]
    public void Npgsql_search_and_keyset_translate_to_full_text_sql_without_connecting()
    {
        var npg = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p").Options);
        Guid after = Guid.NewGuid();
        string sql = new NpgsqlRecipeSearch().Ranked(npg.MarketplaceRecipes, "red shoes")
            .Where(k => k.K1 < 5 || (k.K1 == 5 && (k.K2 < 1 || (k.K2 == 1 && k.Recipe.Id.CompareTo(after) < 0))))
            .OrderByDescending(k => k.K1).ThenByDescending(k => k.K2).ThenByDescending(k => k.Recipe.Id)
            .Take(25).ToQueryString();
        sql.Should().Contain("websearch_to_tsquery('simple'").And.Contain("search_vector").And.Contain("ts_rank").And.Contain("LIMIT");
    }

    [Fact]
    public async Task Media_has_a_strong_etag_from_the_stored_hash_and_answers_304_on_a_match()
    {
        H h = new();
        Guid id = h.Add("e");
        MarketplacePublicController first = h.Controller();
        await first.Preview(id, default);
        first.Response.Headers["ETag"].ToString().Should().Be($"\"{Sha}\"");
        first.Response.Headers["Cache-Control"].ToString().Should().Be("public, max-age=120");

        MarketplacePublicController cond = h.Controller();
        cond.HttpContext.Request.Headers["If-None-Match"] = $"\"{Sha}\"";
        IActionResult r = await cond.Preview(id, default);
        ((StatusCodeResult)r).StatusCode.Should().Be(304);
        cond.Response.Headers["ETag"].ToString().Should().Be($"\"{Sha}\"");

        MarketplacePublicController other = h.Controller();
        other.HttpContext.Request.Headers["If-None-Match"] = "\"deadbeef\"";
        (await other.Poster(id, default)).Should().BeOfType<FileStreamResult>();
        other.Response.Headers["ETag"].ToString().Should().Be($"\"{PosterSha}\"");
    }

    [Fact]
    public async Task Poster_with_wrong_or_missing_hash_is_a_bare_404()
    {
        H h = new();
        Guid wrong = h.Add("w", posterSha: new string('b', 64));
        Guid none = h.Add("n");
        h.Db.MarketplaceRecipeVersions.Single(v => v.RecipeId == none).PosterSha256 = null;
        h.Db.SaveChanges();
        foreach (Guid id in new[] { wrong, none })
        {
            MarketplacePublicController c = h.Controller();
            (await c.Poster(id, default)).Should().BeOfType<EmptyResult>();
            c.Response.StatusCode.Should().Be(404);
            c.Response.Headers.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Control_characters_in_any_parameter_are_a_400_not_a_database_error()
    {
        H h = new();
        h.Add("x");
        foreach (var args in new (string? q, string? cat)[] { ("a\0b", null), (null, "ads\0") })
        {
            IActionResult r = await h.Controller().List(args.q, args.cat, null, null, null, null, null, null, default);
            ((StatusCodeResult)r).StatusCode.Should().Be(400);
        }
    }

    [Theory]
    [InlineData("new")]
    [InlineData("copied")]
    [InlineData("rated")]
    [InlineData(null)]
    public async Task Query_text_composes_with_every_sort_and_with_keyset_paging(string? sort)
    {
        H h = new();
        for (int i = 0; i < 12; i++)
            h.Add("teaser " + i, copies: i % 3, ratingSum: i % 2 == 0 ? 20 : 3, ratingCount: i % 2 == 0 ? 5 : 1, category: "ads", listedAt: DateTime.UtcNow.AddHours(-i));
        for (int i = 0; i < 5; i++)
            h.Add("podcast " + i, copies: 9, ratingSum: 25, ratingCount: 5, category: "audio");
        List<Guid> seen = [];
        string? cursor = null;
        do
        {
            RecipeListDto page = await h.List(sort, cursor, 4, q: "teaser");
            page.Items.Should().OnlyContain(c => c.Name.StartsWith("teaser"));
            seen.AddRange(page.Items.Select(c => c.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        seen.Should().HaveCount(12).And.OnlyHaveUniqueItems();

        IActionResult filtered = await h.Controller().List("teaser", "audio", null, null, null, sort, null, null, default);
        ((RecipeListDto)((ObjectResult)filtered).Value!).Items.Should().BeEmpty();
    }
}
