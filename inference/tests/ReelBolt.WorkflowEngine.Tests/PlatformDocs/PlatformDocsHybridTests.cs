using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client.Grpc;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs;

public class PlatformDocsMetadataTests
{
    private const string KbDoc = """
        ---
        title: Compile cards
        kb:
          kind: step-card
          stepTypes: [VideoCompile, "ForEach"]
          costClass: credits
          goals:
            - make a promo video
            - cut my footage
        ---
        # Compile cards

        ## VideoCompile

        Renders the final cut. Used by quick-win-promo-ds and by quick-win-promo.

        ## Other

        Mentions ReviewLoop and SpeechSynthesis and the AgentType CodeStructureAnalyzer.
        """;

    [Fact]
    public void Kb_front_matter_is_parsed_and_declared_values_win_over_the_scan()
    {
        PlatformDocsChunk first = PlatformDocsCorpus.ChunkDocument("assistant-kb/cards.md", KbDoc, 2400).First();

        first.Title.Should().Be("Compile cards");
        first.Meta.Kind.Should().Be("step-card");
        first.Meta.StepTypes.Should().BeEquivalentTo("ForEach", "VideoCompile");
        first.Meta.CostClass.Should().Be("credits");
        first.Meta.Goals.Should().Equal("make a promo video", "cut my footage");
        first.Meta.HeadingPath.Should().Be("# Compile cards > ## VideoCompile");
        first.Meta.Locale.Should().Be("en");
        first.Meta.DocVersion.Should().HaveLength(12);
        first.Audience.Should().Be(PlatformDocsAudience.Developer);
    }

    [Fact]
    public void Absent_metadata_falls_back_to_the_deterministic_scan_and_folder_rule()
    {
        List<PlatformDocsChunk> chunks = PlatformDocsCorpus.ChunkDocument("assistant-kb/cards.md", KbDoc.Replace("kb:", "other:"), 2400).ToList();

        PlatformDocsChunk other = chunks.Single(c => c.Section == "Other");
        other.Meta.Kind.Should().Be("concept");
        other.Meta.StepTypes.Should().BeEquivalentTo("ReviewLoop");
        other.Meta.AgentTypes.Should().BeEquivalentTo("CodeStructureAnalyzer");
        other.Meta.Capabilities.Should().Contain("SpeechSynthesis");

        PlatformDocsCorpus.ChunkDocument("user-guide/recipes/x.md", "# X\n\nbody", 2400).Single().Meta.Kind.Should().Be("recipe");
        PlatformDocsCorpus.ChunkDocument("user-guide/x.md", "# X\n\nbody", 2400).Single().Meta.Kind.Should().Be("user-guide");
        PlatformDocsCorpus.ChunkDocument("editor.md", "# X\n\nbody", 2400).Single().Meta.Kind.Should().Be("reference");
    }

    [Fact]
    public void Template_keys_match_whole_tokens_only()
    {
        PlatformDocsMetadata.ScanTemplateKeys("Use quick-win-promo-ds for this.").Should().Equal("quick-win-promo-ds");
        PlatformDocsMetadata.ScanTemplateKeys("quick-win-promo, then quick-win-promo-ds").Should().Equal("quick-win-promo", "quick-win-promo-ds");
        PlatformDocsMetadata.ScanTemplateKeys("no keys here").Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_declared_kind_or_cost_class_is_dropped_not_stored()
    {
        const string md = "---\nkb:\n  kind: banana\n  costClass: huge\n  stepTypes: [NotAStep, VideoCompile]\n---\n# T\n\nbody";
        PlatformDocsChunk c = PlatformDocsCorpus.ChunkDocument("assistant-kb/t.md", md, 2400).Single();

        c.Meta.Kind.Should().Be("concept");
        c.Meta.CostClass.Should().BeNull();
        c.Meta.StepTypes.Should().Equal("VideoCompile");
    }

    [Fact]
    public void Neighbours_link_chunks_of_one_document_only()
    {
        List<PlatformDocsChunk> chunks = PlatformDocsCorpus.ChunkDocument("editor.md", "# T\n\n## A\n\na\n\n## B\n\nb\n\n## C\n\nc", 2400).ToList();

        chunks[0].Meta.PrevId.Should().BeNull();
        chunks[0].Meta.NextId.Should().Be(chunks[1].Id);
        chunks[1].Meta.PrevId.Should().Be(chunks[0].Id);
        chunks[2].Meta.NextId.Should().BeNull();
    }

    [Fact]
    public void Assistant_kb_is_admitted_as_internal_background_and_design_stays_excluded()
    {
        PlatformDocsCorpus.IsIncluded("assistant-kb/recipes.md").Should().BeTrue();
        PlatformDocsCorpus.IsIncluded("assistant-kb/deep/more.md").Should().BeTrue();
        PlatformDocsCorpus.IsIncluded("design/workflow-apps.md").Should().BeFalse();
        PlatformDocsCorpus.IsIncluded("reviews/x.md").Should().BeFalse();
        PlatformDocsCorpus.ChunkDocument("assistant-kb/recipes.md", "# R\n\nbody", 2400).Single().Url.Should().BeEmpty();
    }

    [Fact]
    public void Chunker_version_is_three_and_a_metadata_only_edit_changes_the_corpus_hash()
    {
        PlatformDocsCorpus.ChunkerVersion.Should().Be("3");

        const string a = "---\nkb:\n  kind: recipe\n---\n# T\n\nbody";
        string b = a.Replace("recipe", "how-to");
        string Hash(string md) => new PlatformDocsCorpus(PlatformDocsCorpus.ChunkDocument("assistant-kb/t.md", md, 2400).ToList()).CorpusHash;

        Hash(a).Should().NotBe(Hash(b));
        Hash(a).Should().Be(Hash(a));
    }

    [Fact]
    public void Token_stream_is_unicode_folded_lowercase_and_unstemmed()
    {
        PlatformDocsMetadata.Tokenize("Vidéo Éditée, Résumés! a 4K-clip").Should().Equal("video", "editee", "resumes", "4k", "clip");
        PlatformDocsMetadata.Tokenize("Running runs").Should().Equal("running", "runs");
    }

    [Fact]
    public void Plain_english_words_that_are_also_enum_names_are_not_tagged_but_code_like_uses_are()
    {
        const string prose = "Agent notes: Extract the audio first. A Decision was made. Chat with us. Parallel lines.";
        PlatformDocsMetadata.ScanStepTypes(prose).Should().BeEmpty();
        PlatformDocsMetadata.ScanAgentTypes(prose).Should().BeEmpty();
        PlatformDocsMetadata.ScanCapabilities(prose).Should().BeEmpty();

        PlatformDocsMetadata.ScanStepTypes("Use the `Extract` output; an Agent step runs next.").Should().BeEquivalentTo("Agent", "Extract");
        PlatformDocsMetadata.ScanStepTypes("a Conditional step, step type ForEach").Should().BeEquivalentTo("Conditional", "ForEach");
        PlatformDocsMetadata.ScanCapabilities("the `Chat` capability and SpeechSynthesis").Should().BeEquivalentTo("Chat", "SpeechSynthesis");
        PlatformDocsHints.FromText("Extract the audio").StepTypes.Should().BeEmpty();
    }

    [Fact]
    public void Front_matter_comments_are_stripped_and_declared_lists_merge_with_the_section_scan()
    {
        const string md = "---\nkb:\n  kind: recipe # note\n  stepTypes: [VideoCompile] # page level\n  goals:\n    - cut it # why\n---\n# T\n\n## One\n\nUses ForEach.\n\n## Two\n\nNothing special.";
        List<PlatformDocsChunk> chunks = PlatformDocsCorpus.ChunkDocument("assistant-kb/t.md", md, 2400).ToList();

        chunks[0].Meta.Kind.Should().Be("recipe");
        chunks[0].Meta.Goals.Should().Equal("cut it");
        chunks[0].Meta.StepTypes.Should().BeEquivalentTo("ForEach", "VideoCompile");
        chunks[1].Meta.StepTypes.Should().BeEquivalentTo("VideoCompile");
    }

    [Fact]
    public void Build_intent_is_detected_from_the_query_and_neutral_otherwise()
    {
        PlatformDocsHints.FromText("help me build a promo workflow").BuildIntent.Should().BeTrue();
        PlatformDocsHints.FromText("what is a clip").BuildIntent.Should().BeFalse();
        PlatformDocsHints.None.Merge(PlatformDocsHints.FromText("create it")).BuildIntent.Should().BeTrue();
    }

    [Fact]
    public void Query_cache_keys_differ_by_organization()
    {
        Guid a = Guid.NewGuid();
        QueryEmbeddingCache.Key("t", "m", a).Should().NotBe(QueryEmbeddingCache.Key("t", "m", Guid.NewGuid()));
        QueryEmbeddingCache.Key("t", "m", a).Should().NotBe(QueryEmbeddingCache.Key("t", "m"));
    }

    [Fact]
    public void Sparse_vector_carries_term_frequencies_with_sorted_unique_indices()
    {
        (float[] values, uint[] indices) = PlatformDocsMetadata.SparseTermFrequencies("clip clip Clip video");

        indices.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems().And.HaveCount(2);
        values.OrderBy(v => v).Should().Equal(1f, 3f);
        indices[Array.IndexOf(values, 3f)].Should().Be(PlatformDocsMetadata.TermIndex("clip"));

        (float[] qv, uint[] qi) = PlatformDocsMetadata.SparseQueryTerms("clip clip video");
        qv.Should().AllBeEquivalentTo(1f);
        qi.Should().HaveCount(2);
    }
}

public class PlatformDocsHybridQueryTests
{
    [Fact]
    public void Hybrid_request_has_two_same_filter_prefetches_fused_by_rrf()
    {
        var o = new PlatformDocsHybridOptions();
        Filter? must = PlatformDocsQueryBuilder.MustFilter(new PlatformDocsSearchOptions { StepType = "VideoCompile", Kind = "recipe" });

        QueryPoints q = PlatformDocsQueryBuilder.Hybrid("col", [0.1f, 0.2f], "compile the video", must, o);

        q.CollectionName.Should().Be("col");
        q.Query.Rrf.K.Should().Be(60u);
        q.Limit.Should().Be(20ul);
        q.Prefetch.Should().HaveCount(2);
        q.Prefetch.Select(p => p.Using).Should().Equal("dense", "bm25");
        q.Prefetch.Should().OnlyContain(p => p.Limit == 30ul);
        q.Prefetch[0].Query.Nearest.Dense.Data.Should().Equal(0.1f, 0.2f);
        q.Prefetch[1].Query.Nearest.Sparse.Indices.Should().HaveCount(3);
        q.Prefetch.Should().OnlyContain(p => p.Filter.Must.Count == 2);
        q.Filter.Should().BeNull("the filter lives in the prefetches");
        q.WithPayload.Include.Fields.Should().Contain(["content", "kind", "stepTypes", "prevId", "nextId"]).And.NotContain("embedHash");
    }

    [Fact]
    public void Without_explicit_filters_no_filter_is_sent_and_hints_never_filter()
    {
        var options = new PlatformDocsSearchOptions { Hints = PlatformDocsHints.FromText("use VideoCompile with quick-win-promo") };

        options.Hints.StepTypes.Should().Equal("VideoCompile");
        options.Hints.TemplateKeys.Should().Equal("quick-win-promo");
        PlatformDocsQueryBuilder.MustFilter(options).Should().BeNull();

        QueryPoints q = PlatformDocsQueryBuilder.Hybrid("c", [1f], "x y", null, new PlatformDocsHybridOptions());
        q.Prefetch.Should().OnlyContain(p => p.Filter == null);
    }

    [Fact]
    public void Dense_cosine_request_is_a_named_dense_search_without_payload()
    {
        QueryPoints q = PlatformDocsQueryBuilder.DenseCosine("c", [1f], null, new PlatformDocsHybridOptions());

        q.Using.Should().Be("dense");
        q.Limit.Should().Be(30ul);
        q.Query.Nearest.Dense.Data.Should().Equal(1f);
        q.Prefetch.Should().BeEmpty();
    }

    [Fact]
    public void Tool_filters_are_canonicalised_and_unknown_values_are_reported()
    {
        AssistantKnowledgeTools.TryBuildDocsFilters("videocompile", "RECIPE", null, "Credits", out PlatformDocsSearchOptions ok, out string? error).Should().BeTrue();
        error.Should().BeNull();
        ok.StepType.Should().Be("VideoCompile");
        ok.Kind.Should().Be("recipe");
        ok.CostClass.Should().Be("credits");

        AssistantKnowledgeTools.TryBuildDocsFilters("Nope", null, null, null, out _, out error).Should().BeFalse();
        error.Should().Contain("unknown stepType");
        AssistantKnowledgeTools.TryBuildDocsFilters(null, null, null, null, out PlatformDocsSearchOptions none, out _).Should().BeTrue();
        none.HasMustFilter.Should().BeFalse();
    }

    [Fact]
    public void The_hybrid_path_is_on_by_default_and_the_cache_holds_256_queries()
    {
        var o = new PlatformDocsOptions().Hybrid;
        o.Enabled.Should().BeTrue();
        (o.PrefetchLimit, o.FuseLimit, o.RrfK, o.MaxChunksPerDoc, o.QueryEmbeddingCacheSize).Should().Be((30, 20, 60, 2, 256));
    }

    [Fact]
    public void Query_embedding_cache_is_a_bounded_lru_keyed_on_text_and_model()
    {
        var cache = new QueryEmbeddingCache(2);
        var e = new Func<Embedding<float>>(() => new Embedding<float>(new float[] { 1f }));
        cache.Set(QueryEmbeddingCache.Key("a", "m"), e());
        cache.Set(QueryEmbeddingCache.Key("b", "m"), e());
        cache.TryGet(QueryEmbeddingCache.Key("a", "m"), out _).Should().BeTrue();
        cache.Set(QueryEmbeddingCache.Key("c", "m"), e());

        cache.Count.Should().Be(2);
        cache.TryGet(QueryEmbeddingCache.Key("b", "m"), out _).Should().BeFalse("b was least recently used");
        cache.TryGet(QueryEmbeddingCache.Key("a", "other"), out _).Should().BeFalse("another model is another key");
    }
}

public class PlatformDocsStageATests
{
    private static PlatformDocsHit Hit(
        string id, string doc, float fused, string kind = "reference", PlatformDocsAudience audience = PlatformDocsAudience.Developer,
        float? dense = null, string[]? steps = null, string[]? templates = null) =>
        new("T", id, "", audience, "c", dense ?? fused)
        {
            ChunkId = id,
            DocPath = doc,
            FusedScore = fused,
            DenseScore = dense,
            Meta = new PlatformDocsChunkMeta { Kind = kind, StepTypes = steps ?? [], TemplateKeys = templates ?? [] }
        };

    private static IReadOnlyList<string> Ids(IReadOnlyList<PlatformDocsHit> hits) => hits.Select(h => h.ChunkId).ToList();

    [Fact]
    public void Kind_priority_orders_near_ties_recipe_then_step_card_then_how_to_then_concept()
    {
        var hits = new[]
        {
            Hit("concept", "d1", 0.5f, "concept"), Hit("howto", "d2", 0.5f, "how-to"),
            Hit("step", "d3", 0.5f, "step-card"), Hit("recipe", "d4", 0.5f, "recipe"), Hit("ref", "d5", 0.5f)
        };

        var build = new PlatformDocsSearchOptions { Hints = new PlatformDocsHints { BuildIntent = true } };

        Ids(PlatformDocsStageA.Apply(hits, build, 2, 20)).Should().Equal("recipe", "step", "howto", "concept", "ref");
        Ids(PlatformDocsStageA.Apply(hits, new PlatformDocsSearchOptions(), 2, 20))
            .Should().Equal("concept", "howto", "recipe", "ref", "step");
    }

    [Fact]
    public void An_exact_step_or_template_match_with_the_open_workflow_outranks_a_small_score_lead()
    {
        var hints = new PlatformDocsHints { StepTypes = ["VideoCompile"], TemplateKeys = ["quick-win-promo"] };
        var hits = new[]
        {
            Hit("generic", "d1", 0.60f), Hit("step", "d2", 0.55f, steps: ["VideoCompile"]), Hit("tpl", "d3", 0.54f, templates: ["quick-win-promo"])
        };

        Ids(PlatformDocsStageA.Apply(hits, new PlatformDocsSearchOptions { Hints = hints }, 2, 20)).Should().Equal("tpl", "step", "generic");
        Ids(PlatformDocsStageA.Apply(hits, new PlatformDocsSearchOptions(), 2, 20)).Should().Equal("generic", "step", "tpl");
    }

    [Fact]
    public void Customer_facing_audience_gets_the_existing_boost()
    {
        var hits = new[] { Hit("dev", "d1", 0.52f), Hit("user", "d2", 0.50f, audience: PlatformDocsAudience.User) };

        Ids(PlatformDocsStageA.Apply(hits, new PlatformDocsSearchOptions(), 2, 20)).Should().Equal("user", "dev");
    }

    [Fact]
    public void One_document_contributes_at_most_two_chunks()
    {
        var hits = new[] { Hit("a0", "a", 0.9f), Hit("a1", "a", 0.8f), Hit("a2", "a", 0.7f), Hit("b0", "b", 0.1f) };

        Ids(PlatformDocsStageA.Apply(hits, new PlatformDocsSearchOptions(), 2, 20)).Should().Equal("a0", "a1", "b0");
    }

    [Fact]
    public void The_floor_needs_one_dense_cosine_above_it_and_drops_known_low_cosines()
    {
        var options = new PlatformDocsSearchOptions { MinScore = 0.35f };
        var supported = new[] { Hit("hi", "a", 0.5f, dense: 0.6f), Hit("lo", "b", 0.4f, dense: 0.2f), Hit("lexical", "c", 0.3f) };
        var offTopic = new[] { Hit("lo", "a", 0.5f, dense: 0.2f), Hit("lexical", "b", 0.4f) };

        Ids(PlatformDocsStageA.Apply(supported, options, 2, 20)).Should().Equal("hi", "lexical");
        PlatformDocsStageA.Apply(offTopic, options, 2, 20).Should().BeEmpty();
        PlatformDocsStageA.Apply(offTopic, new PlatformDocsSearchOptions(), 2, 20).Should().HaveCount(2, "the search tool has no floor");
    }

    [Fact]
    public void Ordering_is_reproducible_on_equal_scores()
    {
        var hits = new[] { Hit("b", "x", 0.5f), Hit("a", "y", 0.5f), Hit("c", "z", 0.5f) };

        Ids(PlatformDocsStageA.Apply(hits, new PlatformDocsSearchOptions(), 2, 20)).Should().Equal("a", "b", "c");
        Ids(PlatformDocsStageA.Apply(hits.Reverse().ToArray(), new PlatformDocsSearchOptions(), 2, 20)).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Payload_maps_back_to_a_hit_with_metadata_and_old_payloads_map_to_no_metadata()
    {
        var payload = new Dictionary<string, Value>
        {
            ["chunkId"] = "a.md#1",
            ["docPath"] = "a.md",
            ["title"] = "T",
            ["section"] = "S",
            ["audience"] = "Developer",
            ["content"] = "c",
            ["kind"] = "recipe",
            ["costClass"] = "voice",
            ["prevId"] = "a.md#0",
            ["stepTypes"] = new Value { ListValue = new ListValue { Values = { new Value { StringValue = "VideoCompile" } } } }
        };

        PlatformDocsHit hit = QdrantPlatformDocsIndex.MapPayload(payload, 0.4f);
        hit.ChunkId.Should().Be("a.md#1");
        hit.Meta!.Kind.Should().Be("recipe");
        hit.Meta.StepTypes.Should().Equal("VideoCompile");
        hit.Meta.CostClass.Should().Be("voice");
        hit.Meta.PrevId.Should().Be("a.md#0");

        QdrantPlatformDocsIndex.MapPayload(new Dictionary<string, Value> { ["title"] = "T" }, 0.4f).Meta.Should().BeNull();
    }

    [Fact]
    public async Task Builder_passes_message_hints_and_floor_and_keeps_a_ranked_result_in_order()
    {
        var spy = new SpySearch(new PlatformDocsSearchResult(PlatformDocsSearchStatus.Ok,
        [
            new PlatformDocsHit("Low", "S", "", PlatformDocsAudience.Developer, "low", 0.10f),
            new PlatformDocsHit("High", "S", "", PlatformDocsAudience.Developer, "high", 0.90f)
        ])
        { Ranked = true });
        var builder = new PlatformDocsContextBuilder(spy, Options.Create(new PlatformDocsOptions()), NullLogger<PlatformDocsContextBuilder>.Instance);

        string? block = await builder.BuildAsync("how do I use VideoCompile?", CancellationToken.None);

        spy.Options!.Hints.StepTypes.Should().Equal("VideoCompile");
        spy.Options.MinScore.Should().Be(0.35f);
        spy.Options.HasMustFilter.Should().BeFalse();
        block!.IndexOf("Low", StringComparison.Ordinal).Should().BeLessThan(block.IndexOf("High", StringComparison.Ordinal), "a Ranked result is trusted as-is");
    }

    [Fact]
    public async Task A_dense_only_search_ignores_options_and_is_ranked_by_the_builder_as_before()
    {
        var dense = new DenseOnly(new PlatformDocsSearchResult(PlatformDocsSearchStatus.Ok,
        [
            new PlatformDocsHit("Low", "S", "", PlatformDocsAudience.Developer, "low", 0.10f),
            new PlatformDocsHit("High", "S", "", PlatformDocsAudience.Developer, "high", 0.90f)
        ]));
        var builder = new PlatformDocsContextBuilder(dense, Options.Create(new PlatformDocsOptions()), NullLogger<PlatformDocsContextBuilder>.Instance);

        string? block = await builder.BuildAsync("hello", CancellationToken.None);

        dense.Calls.Should().Be(1, "the 4-argument default forwards to the 3-argument search");
        block.Should().Contain("High").And.NotContain("Low");
    }

    private sealed class SpySearch(PlatformDocsSearchResult result) : IPlatformDocsSearch
    {
        public PlatformDocsSearchOptions? Options { get; private set; }

        public Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, CancellationToken ct) => Task.FromResult(result);

        public Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, PlatformDocsSearchOptions options, CancellationToken ct)
        {
            Options = options;
            return Task.FromResult(result);
        }
    }

    private sealed class DenseOnly(PlatformDocsSearchResult result) : IPlatformDocsSearch
    {
        public int Calls { get; private set; }

        public Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
