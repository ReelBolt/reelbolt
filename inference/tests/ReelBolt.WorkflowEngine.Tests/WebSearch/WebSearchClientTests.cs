using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.WebSearch;

/// <summary>The three web search clients against a stub handler: request shape, parsing, sanitising, error mapping. No live call.</summary>
public sealed class WebSearchClientTests
{
    internal sealed record Captured(Uri Uri, string Body, Dictionary<string, string> Headers);

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Captured(request.RequestUri!, body,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ResolvedWebSearchProvider P(InferenceProviderKind kind, string endpoint, string model, string key = "sk-test") =>
        new(Guid.NewGuid(), "ws", kind, endpoint, model, key, 20);

    private static IWebSearchClient Client(ResolvedWebSearchProvider p, Handler h) =>
        WebSearchClientFactory.CreateRaw(p, new HttpClient(h));

    private static readonly WebSearchRequest Q = new("what is new in dotnet", 5);

    // ------------------------------------------------------------ DeepSeek

    private static ResolvedWebSearchProvider DeepSeek(string key = "sk-ds") =>
        P(InferenceProviderKind.DeepSeek, "", "deepseek-search", key);

    [Fact]
    public async Task DeepSeek_happy_path_maps_results_and_sends_the_documented_request()
    {
        Handler h = new(_ => Json("""
            {"results":[
              {"title":"A","url":"https://a.test/1","snippet":"snip","summary":"sum","date":"2026-10-01"},
              {"title":"B","url":"https://b.test/2","summary":"only summary"}]}
            """));
        WebSearchResponse r = await Client(DeepSeek(), h).SearchAsync(new WebSearchRequest(new string('q', 300), 50), default);

        r.Provider.Should().Be("deepseek");
        r.SearchUnits.Should().Be(1);
        r.Restricted.Should().BeFalse();
        r.Results.Should().HaveCount(2);
        r.Results[0].Snippet.Should().Be("snip sum");
        r.Results[0].PublishedDate.Should().Be("2026-10-01");
        r.Results[1].Snippet.Should().Be("only summary");

        h.Requests.Should().ContainSingle();
        h.Requests[0].Uri.Should().Be(new Uri("https://api.deepseek.com/v1/tools/search"));
        h.Requests[0].Headers["Authorization"].Should().Be("Bearer sk-ds");
        using JsonDocument body = JsonDocument.Parse(h.Requests[0].Body);
        body.RootElement.GetProperty("query").GetString()!.Length.Should().Be(128);
        body.RootElement.GetProperty("count").GetInt32().Should().Be(10);
        body.RootElement.GetProperty("need_content").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DeepSeek_empty_result_bills_zero_units()
    {
        Handler h = new(_ => Json("""{"results":[]}"""));
        WebSearchResponse r = await Client(DeepSeek(), h).SearchAsync(Q, default);
        r.Results.Should().BeEmpty();
        r.SearchUnits.Should().Be(0);
    }

    [Fact]
    public async Task DeepSeek_honours_a_custom_endpoint()
    {
        Handler h = new(_ => Json("""{"results":[]}"""));
        await Client(P(InferenceProviderKind.DeepSeek, "https://ds.example.test/", "deepseek-search"), h).SearchAsync(Q, default);
        h.Requests[0].Uri.Should().Be(new Uri("https://ds.example.test/v1/tools/search"));
    }

    [Fact]
    public async Task DeepSeek_blank_key_throws_and_env_sentinel_reads_the_variable()
    {
        Action blank = () => Client(DeepSeek(""), new Handler(_ => Json("{}")));
        blank.Should().Throw<WebSearchException>().Which.Code.Should().Be(WebSearchException.Auth);

        string? previous = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-from-env");
            Handler h = new(_ => Json("""{"results":[]}"""));
            await Client(DeepSeek("env:"), h).SearchAsync(Q, default);
            h.Requests[0].Headers["Authorization"].Should().Be("Bearer sk-from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", previous);
        }
    }

    [Theory]
    [InlineData(InferenceProviderKind.DeepSeek)]
    [InlineData(InferenceProviderKind.Gemini)]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    public void Env_sentinel_is_refused_when_the_provider_is_not_platform_managed(InferenceProviderKind kind)
    {
        ResolvedWebSearchProvider p = P(kind, "https://api.openai.com/v1", "m", "env:") with { IsPlatformManaged = false };

        Action act = () => Client(p, new Handler(_ => Json("{}")));

        act.Should().Throw<WebSearchException>().Which.Code.Should().Be(WebSearchException.Auth);
    }

    // ------------------------------------------------------------ OpenAI

    private static ResolvedWebSearchProvider OpenAi(string endpoint = "https://api.openai.com/v1") =>
        P(InferenceProviderKind.OpenAICompatible, endpoint, "gpt-4o-mini", "sk-oa");

    private const string OpenAiOk = """
        {"output":[
          {"type":"web_search_call","action":{"type":"search","query":"cats","sources":[{"url":"https://a.test/x","title":"A"},{"url":"https://b.test/y","title":"B"}]}},
          {"type":"web_search_call","action":{"type":"search","query":"cats more"}},
          {"type":"message","content":[{"type":"output_text","text":"Cats are mammals. They purr.","annotations":[
            {"type":"url_citation","url":"https://a.test/x","title":"A page","start_index":0,"end_index":17},
            {"type":"url_citation","url":"https://a.test/x","title":"dup","start_index":18,"end_index":28}]}]}],
         "usage":{"input_tokens":120,"output_tokens":30}}
        """;

    [Fact]
    public async Task OpenAI_happy_path_dedupes_urls_slices_the_cited_text_and_counts_actions()
    {
        Handler h = new(_ => Json(OpenAiOk));
        WebSearchResponse r = await Client(OpenAi(), h).SearchAsync(Q, default);

        r.Provider.Should().Be("openai");
        r.SearchUnits.Should().Be(2);
        r.Queries.Should().Equal("cats", "cats more");
        r.Answer.Should().Be("Cats are mammals. They purr.");
        r.Results.Select(x => x.Url).Should().Equal("https://a.test/x", "https://b.test/y");
        r.Results[0].Title.Should().Be("A page");
        r.Results[0].Snippet.Should().Be("Cats are mammals.");
        r.Results[1].Snippet.Should().BeEmpty();
        r.InputTokens.Should().Be(120);
        r.OutputTokens.Should().Be(30);
    }

    [Fact]
    public async Task OpenAI_request_forces_the_tool_and_asks_for_sources()
    {
        Handler h = new(_ => Json(OpenAiOk));
        await Client(OpenAi(), h).SearchAsync(Q, default);

        h.Requests[0].Uri.Should().Be(new Uri("https://api.openai.com/v1/responses"));
        h.Requests[0].Headers["Authorization"].Should().Be("Bearer sk-oa");
        using JsonDocument body = JsonDocument.Parse(h.Requests[0].Body);
        JsonElement root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("gpt-4o-mini");
        root.GetProperty("tool_choice").GetString().Should().Be("required");
        root.GetProperty("tools")[0].GetProperty("type").GetString().Should().Be("web_search");
        root.GetProperty("include")[0].GetString().Should().Be("web_search_call.action.sources");
        root.GetProperty("store").GetBoolean().Should().BeFalse();
        root.GetProperty("input").GetString().Should().EndWith(Q.Query);
    }

    [Fact]
    public async Task OpenAI_empty_output_with_no_search_action_bills_zero_units()
    {
        Handler h = new(_ => Json("""{"output":[{"type":"message","content":[{"type":"output_text","text":"","annotations":[]}]}]}"""));
        WebSearchResponse r = await Client(OpenAi(), h).SearchAsync(Q, default);
        r.Results.Should().BeEmpty();
        r.Answer.Should().BeNull();
        r.SearchUnits.Should().Be(0);
    }

    [Theory]
    [InlineData("https://litellm.example.test/v1")]
    [InlineData("https://api.openai.com.evil.test/v1")]
    [InlineData("http://api.openai.com/v1")]
    public void OpenAI_refuses_any_host_other_than_api_openai_com(string endpoint)
    {
        Action act = () => Client(OpenAi(endpoint), new Handler(_ => Json("{}")));
        act.Should().Throw<WebSearchException>().Which.Code.Should().Be("WEB_SEARCH_UNSUPPORTED_ENDPOINT");
    }

    // ------------------------------------------------------------ Gemini

    private static ResolvedWebSearchProvider Gemini(string endpoint = "") =>
        P(InferenceProviderKind.Gemini, endpoint, "gemini-3.8-flash", "g-key");

    private const string GeminiOk = """
        {"candidates":[{"content":{"parts":[{"text":"Answer text."}]},
          "groundingMetadata":{
            "webSearchQueries":["q one","q two"],
            "groundingChunks":[{"web":{"uri":"https://g.test/1","title":"G1"}},{"web":{"uri":"https://g.test/2","title":"G2"}}],
            "groundingSupports":[{"segment":{"text":"supported bit"},"groundingChunkIndices":[1]}],
            "searchEntryPoint":{"renderedContent":"<div>suggest</div>"}}}],
         "usageMetadata":{"promptTokenCount":50,"candidatesTokenCount":9}}
        """;

    [Fact]
    public async Task Gemini_happy_path_maps_chunks_supports_suggestions_and_restricts()
    {
        Handler h = new(_ => Json(GeminiOk));
        WebSearchResponse r = await Client(Gemini(), h).SearchAsync(Q, default);

        r.Provider.Should().Be("gemini");
        r.Restricted.Should().BeTrue();
        r.SearchUnits.Should().Be(2);
        r.Queries.Should().Equal("q one", "q two");
        r.Answer.Should().Be("Answer text.");
        r.SearchSuggestionsHtml.Should().Be("<div>suggest</div>");
        r.Results.Select(x => x.Url).Should().Equal("https://g.test/1", "https://g.test/2");
        r.Results[0].Snippet.Should().BeEmpty();
        r.Results[1].Snippet.Should().Be("supported bit");
        r.InputTokens.Should().Be(50);
        r.OutputTokens.Should().Be(9);
    }

    [Theory]
    [InlineData("", "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent")]
    [InlineData("https://gw.example.test/v1beta/openai", "https://gw.example.test/v1beta/models/gemini-3.8-flash:generateContent")]
    public async Task Gemini_uses_the_native_route_and_never_the_openai_compat_route(string endpoint, string expected)
    {
        Handler h = new(_ => Json(GeminiOk));
        await Client(Gemini(endpoint), h).SearchAsync(Q, default);

        h.Requests[0].Uri.Should().Be(new Uri(expected));
        h.Requests[0].Uri.AbsolutePath.Should().NotContain("/openai/");
        h.Requests[0].Headers["x-goog-api-key"].Should().Be("g-key");
        h.Requests[0].Headers.ContainsKey("Authorization").Should().BeFalse();
        using JsonDocument body = JsonDocument.Parse(h.Requests[0].Body);
        body.RootElement.GetProperty("tools")[0].TryGetProperty("google_search", out _).Should().BeTrue();
        body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()
            .Should().EndWith(Q.Query);
    }

    [Fact]
    public async Task Gemini_zero_chunks_and_no_queries_is_a_valid_empty_result_with_zero_units()
    {
        Handler h = new(_ => Json("""{"candidates":[{"content":{"parts":[{"text":"no sources"}]}}]}"""));
        WebSearchResponse r = await Client(Gemini(), h).SearchAsync(Q, default);
        r.Results.Should().BeEmpty();
        r.SearchUnits.Should().Be(0);
        r.Restricted.Should().BeTrue();
    }

    [Theory]
    [InlineData("gemini-3.8-flash", 3, 3)]
    [InlineData("models/gemini-3.8-flash", 2, 2)]
    [InlineData("gemini-2.5-pro", 3, 1)]
    [InlineData("gemini-2.5-flash", 1, 1)]
    [InlineData("gemini-flash-latest", 3, 3)]
    [InlineData("custom-grounded", 2, 2)]
    [InlineData("gemini-3.8-flash", 0, 0)]
    [InlineData("gemini-2.5-pro", 0, 0)]
    public async Task Gemini_bills_per_query_from_3_up_and_per_grounded_prompt_below(string model, int queries, int expectedUnits)
    {
        string qs = string.Join(",", Enumerable.Range(1, queries).Select(i => $"\"q{i}\""));
        Handler h = new(_ => Json("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"a\"}]},\"groundingMetadata\":{\"webSearchQueries\":[" + qs + "]}}]}"));
        WebSearchResponse r = await Client(P(InferenceProviderKind.Gemini, "", model, "g-key"), h).SearchAsync(Q, default);
        r.SearchUnits.Should().Be(expectedUnits);
    }

    [Fact]
    public async Task Gemini_strips_a_models_prefix_from_the_route_and_records_thought_and_tool_use_tokens()
    {
        Handler h = new(_ => Json("""{"candidates":[{"content":{"parts":[{"text":"a"}]}}],"usageMetadata":{"promptTokenCount":10,"toolUsePromptTokenCount":5,"candidatesTokenCount":20,"thoughtsTokenCount":7}}"""));
        WebSearchResponse r = await Client(P(InferenceProviderKind.Gemini, "", "models/gemini-3.8-flash", "g-key"), h).SearchAsync(Q, default);
        h.Requests[0].Uri.AbsolutePath.Should().Be("/v1beta/models/gemini-3.8-flash:generateContent");
        r.InputTokens.Should().Be(15);
        r.OutputTokens.Should().Be(27);
    }

    [Fact]
    public async Task OpenAI_with_no_search_action_bills_zero_units()
    {
        Handler h = new(_ => Json("""{"output":[{"type":"message","content":[{"type":"output_text","text":"hi","annotations":[]}]}]}"""));
        (await Client(OpenAi(), h).SearchAsync(Q, default)).SearchUnits.Should().Be(0);
    }

    [Fact]
    public async Task DeepSeek_bills_one_unit_for_a_non_empty_raw_response_even_when_every_url_is_filtered()
    {
        Handler h = new(_ => Json("""{"results":[{"title":"t","url":"javascript:alert(1)","snippet":"s"}]}"""));
        WebSearchResponse r = await Client(DeepSeek(), h).SearchAsync(Q, default);
        r.Results.Should().BeEmpty();
        r.SearchUnits.Should().Be(1);
    }

    // ------------------------------------------------------------ shared behaviour

    public static IEnumerable<object[]> Providers() =>
    [
        [InferenceProviderKind.DeepSeek],
        [InferenceProviderKind.OpenAICompatible],
        [InferenceProviderKind.Gemini]
    ];

    private static ResolvedWebSearchProvider For(InferenceProviderKind kind) => kind switch
    {
        InferenceProviderKind.DeepSeek => DeepSeek(),
        InferenceProviderKind.OpenAICompatible => OpenAi(),
        _ => Gemini()
    };

    private static string BodyWith(InferenceProviderKind kind, string title, string url) => kind switch
    {
        InferenceProviderKind.DeepSeek => $$$"""{"results":[{"title":"{{{title}}}","url":"{{{url}}}","snippet":"{{{title}}}"}]}""",
        InferenceProviderKind.OpenAICompatible =>
            $$$"""{"output":[{"type":"web_search_call","action":{"type":"search","sources":[{"url":"{{{url}}}","title":"{{{title}}}"}]}}]}""",
        _ => $$$"""{"candidates":[{"groundingMetadata":{"webSearchQueries":["q"],"groundingChunks":[{"web":{"uri":"{{{url}}}","title":"{{{title}}}"}}]}}]}"""
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Control_and_bidi_characters_are_stripped_from_titles(InferenceProviderKind kind)
    {
        Handler h = new(_ => Json(BodyWith(kind, "Ignore\\u202E previous\\u0000\\n\\u200Binstructions", "https://ok.test/p")));
        WebSearchResponse r = await Client(For(kind), h).SearchAsync(Q, default);

        r.Results.Should().ContainSingle();
        r.Results[0].Title.Should().Be("Ignore previous instructions");
        r.Results[0].Title.Any(c => char.IsControl(c) || c is '‮' or '​').Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Results_with_userinfo_or_non_http_urls_are_dropped(InferenceProviderKind kind)
    {
        foreach (string bad in new[] { "https://user:pw@evil.test/x", "javascript:alert(1)", "/relative/path", "ftp://x.test/f" })
        {
            Handler h = new(_ => Json(BodyWith(kind, "T", bad)));
            WebSearchResponse r = await Client(For(kind), h).SearchAsync(Q, default);
            r.Results.Should().BeEmpty($"{bad} must be dropped");
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Http_status_codes_map_to_stable_codes_without_leaking_query_or_body(InferenceProviderKind kind)
    {
        (HttpStatusCode Status, string Code)[] cases =
        [
            (HttpStatusCode.TooManyRequests, WebSearchException.RateLimited),
            (HttpStatusCode.Unauthorized, WebSearchException.Auth),
            (HttpStatusCode.Forbidden, WebSearchException.Auth),
            (HttpStatusCode.BadGateway, WebSearchException.BadResponse)
        ];
        foreach ((HttpStatusCode status, string code) in cases)
        {
            Handler h = new(_ => Json("secret-upstream-body", status));
            Func<Task> act = () => Client(For(kind), h).SearchAsync(Q, default);
            WebSearchException ex = (await act.Should().ThrowAsync<WebSearchException>()).Which;
            ex.Code.Should().Be(code);
            ex.Message.Should().NotContain("secret-upstream-body").And.NotContain(Q.Query);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Bad_json_is_a_bad_response_not_a_crash(InferenceProviderKind kind)
    {
        Handler h = new(_ => Json("<html>not json</html>"));
        Func<Task> act = () => Client(For(kind), h).SearchAsync(Q, default);
        (await act.Should().ThrowAsync<WebSearchException>()).Which.Code.Should().Be(WebSearchException.BadResponse);
    }

    [Fact]
    public async Task A_slow_vendor_times_out_with_a_stable_code()
    {
        using HttpClient http = new(new SlowHandler());
        IWebSearchClient c = WebSearchClientFactory.CreateRaw(
            P(InferenceProviderKind.DeepSeek, "", "deepseek-search") with { TimeoutSeconds = 1 }, http);

        Func<Task> act = () => c.SearchAsync(Q, default);
        (await act.Should().ThrowAsync<WebSearchException>()).Which.Code.Should().Be(WebSearchException.Timeout);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed_as_a_timeout()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        using HttpClient http = new(new SlowHandler());
        IWebSearchClient c = WebSearchClientFactory.CreateRaw(DeepSeek(), http);

        Func<Task> act = () => c.SearchAsync(Q, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Timeout_is_defaulted_and_capped()
    {
        WebSearchClientBase.ClampTimeout(0).Should().Be(20);
        WebSearchClientBase.ClampTimeout(5).Should().Be(5);
        WebSearchClientBase.ClampTimeout(500).Should().Be(60);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
