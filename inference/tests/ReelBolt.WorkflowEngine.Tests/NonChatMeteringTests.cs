#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Prices the non-chat kinds of any capability alike — 1 millicredit per embedding token, 100 per ASR second, 2 per TTS
/// byte, 1000 per generated second (cost = credits / 1000) — and compute through the REAL price service against the
/// system compute capability priced at the defaults (<see cref="PricingDefaults.ComputePrices"/>).
/// </summary>
internal sealed class NonChatPriceService : IModelPriceService
{
    private static readonly DateTime Past = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<UsageKind, decimal> Flat = new Dictionary<UsageKind, decimal>
    {
        [UsageKind.EmbeddingTokens] = 1m,
        [UsageKind.AsrAudioSeconds] = 100m,
        [UsageKind.TtsInputBytes] = 2m,
        [UsageKind.VideoGenSeconds] = 1000m
    };

    private readonly ModelPriceService _compute = new(new ComputeSource());
    private readonly object _gate = new();
    private readonly List<PriceRequest> _requests = [];

    public IReadOnlyList<PriceRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.ToList();
        }
    }

    public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
    {
        lock (_gate) _requests.Add(request);
        if (request.Capability == InferenceProviderCapability.Compute) return _compute.PriceAsync(request, ct);
        if (!Flat.TryGetValue(request.Unit, out decimal millis))
            return _compute.PriceAsync(request, ct);

        bool platform = request.BillingMode == UsageBillingMode.Platform;
        return ValueTask.FromResult(new PriceQuote(
            platform ? (long)Math.Round(request.Quantity * millis, MidpointRounding.AwayFromZero) : 0,
            platform ? request.Quantity * millis / 1000m : 0m,
            Guid.NewGuid()));
    }

    public ValueTask<PriceQuote> PriceQuotedUsdAsync(
        decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
        _compute.PriceQuotedUsdAsync(costUsd, unit, billingMode, at, ct);

    public void Invalidate() => _compute.Invalidate();

    private sealed class ComputeSource : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(new ModelPriceSnapshot(
            [new PricedCapability(WellKnownModelCatalog.ComputeCapabilityId, WellKnownModelCatalog.ComputeProviderId, null,
                ComputeMetering.ProviderKind, InferenceProviderCapability.Compute, WellKnownModelCatalog.ComputeModelCode)],
            [.. PricingDefaults.ComputePrices().Select(p =>
                ReelBolt.Inference.Api.Services.Billing.PriceBook.ToRow(WellKnownModelCatalog.ComputeCapabilityId, p, Past))],
            []));
    }
}

/// <summary>B3c: embeddings, ASR, TTS, compute and the attribution of indexing/docs usage.</summary>
public class NonChatMeteringTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    private static (UsageRecorder Recorder, CapturingSink Sink) NewRecorder()
    {
        CapturingSink sink = new();
        return (new UsageRecorder(new NonChatPriceService(), sink, NullLogger<UsageRecorder>.Instance), sink);
    }

    private static async Task<IReadOnlyList<UsageEvent>> Flush(UsageRecorder recorder, CapturingSink sink)
    {
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        return sink.Events;
    }

    private static IDisposable Scope(UsageSource source = UsageSource.Workflow) =>
        UsageScope.Begin(OrgId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), source);

    // ---- embeddings ----

    private static ResolvedEmbeddingProvider EmbeddingProvider(Guid? org = null) => new(
        Guid.NewGuid(), "emb", InferenceProviderKind.OpenAICompatible, "http://x", "emb-model", "", 30,
        OrganizationId: org, IsPlatformManaged: org is null);

    private sealed class FakeEmbeddings(UsageDetails? usage) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            GeneratedEmbeddings<Embedding<float>> result = new(values.Select(_ => new Embedding<float>(new float[] { 1, 2, 3 })))
            {
                Usage = usage
            };
            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Embedding_decorator_records_the_reported_input_tokens_on_the_ambient_scope()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        ResolvedEmbeddingProvider provider = EmbeddingProvider();
        IEmbeddingGenerator<string, Embedding<float>> client = new MeteringEmbeddingGenerator(
            new FakeEmbeddings(new UsageDetails { InputTokenCount = 321, TotalTokenCount = 321 }), provider, recorder);

        using (Scope(UsageSource.Indexing))
            await client.GenerateAsync(["a", "b"]);

        UsageEvent row = (await Flush(recorder, sink)).Should().ContainSingle().Subject;
        row.Kind.Should().Be(UsageKind.EmbeddingTokens);
        row.Quantity.Should().Be(321);
        row.CreditsMillis.Should().Be(321);
        row.OrganizationId.Should().Be(OrgId);
        row.Source.Should().Be(UsageSource.Indexing);
        row.ModelName.Should().Be("emb-model");
        row.BillingMode.Should().Be(UsageBillingMode.Platform);
    }

    [Fact]
    public async Task Embedding_decorator_estimates_four_characters_per_token_when_the_backend_reports_none_and_never_records_free()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        IEmbeddingGenerator<string, Embedding<float>> client = new MeteringEmbeddingGenerator(
            new FakeEmbeddings(null), EmbeddingProvider(), recorder);

        using (Scope())
            await client.GenerateAsync(["12345678", "123456789"]);

        // 17 chars -> ceil(17 / 4) = 5
        (await Flush(recorder, sink)).Should().ContainSingle().Which.Quantity.Should().Be(5);
    }

    [Fact]
    public async Task Embedding_through_a_byo_provider_is_recorded_byo_and_costs_no_credits()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        IEmbeddingGenerator<string, Embedding<float>> client = new MeteringEmbeddingGenerator(
            new FakeEmbeddings(new UsageDetails { InputTokenCount = 50 }), EmbeddingProvider(org: OrgId), recorder);

        using (Scope())
            await client.GenerateAsync(["x"]);

        UsageEvent row = (await Flush(recorder, sink)).Should().ContainSingle().Subject;
        row.BillingMode.Should().Be(UsageBillingMode.Byo);
        row.CreditsMillis.Should().Be(0);
    }

    [Fact]
    public void Embedding_factory_wraps_only_when_a_recorder_is_supplied()
    {
        ResolvedEmbeddingProvider provider = new(
            Guid.NewGuid(), "emb", InferenceProviderKind.OpenAICompatible, "http://localhost:1", "m", "", 30);

        new EmbeddingClientFactory().Get(provider).Should().NotBeOfType<MeteringEmbeddingGenerator>();
        new EmbeddingClientFactory(NewRecorder().Recorder).Get(provider).Should().BeOfType<MeteringEmbeddingGenerator>();
    }

    // ---- ASR ----

    private static byte[] Wav(int sampleRate, double seconds, bool streamedHeader = false, int listChunkBytes = 0)
    {
        int dataBytes = (int)(sampleRate * 2 * seconds);
        int listBody = listChunkBytes + (listChunkBytes % 2);
        using MemoryStream ms = new();
        using BinaryWriter w = new(ms);
        w.Write("RIFF"u8);
        w.Write(4 + 24 + (listChunkBytes == 0 ? 0 : 8 + listBody) + 8 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        if (listChunkBytes > 0)
        {
            // A metadata chunk between "fmt " and "data", like the tags ffmpeg or Audacity write.
            w.Write("LIST"u8);
            w.Write(listChunkBytes);
            w.Write(new byte[listBody]);
        }

        w.Write("data"u8);
        w.Write(streamedHeader ? 0 : dataBytes);
        w.Write(new byte[dataBytes]);
        return ms.ToArray();
    }

    private sealed class FakeTranscription : ITranscriptionClient
    {
        public long BytesSeen;

        public Task<TranscriptResult> TranscribeAsync(Stream wav, string fileName, string? language, bool wordTimestamps, CancellationToken ct)
        {
            BytesSeen = wav.Length - wav.Position;
            return Task.FromResult(new TranscriptResult("hi", [], [], null));
        }
    }

    private static ResolvedTranscriptionProvider AsrProvider() => new(
        Guid.NewGuid(), "asr", InferenceProviderKind.OpenAICompatible, "http://x", "whisper-test", "", 30);

    [Fact]
    public async Task Transcription_decorator_records_wav_seconds_from_the_header_and_leaves_the_stream_for_the_inner_client()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        FakeTranscription inner = new();
        ITranscriptionClient client = new MeteringTranscriptionClient(inner, AsrProvider(), recorder);
        byte[] wav = Wav(16_000, 2.5);

        using (Scope())
        {
            await using MemoryStream stream = new(wav);
            await client.TranscribeAsync(stream, "a.wav", null, false, CancellationToken.None);
        }

        inner.BytesSeen.Should().Be(wav.Length, "measuring the header must not consume the stream");
        UsageEvent row = (await Flush(recorder, sink)).Should().ContainSingle().Subject;
        row.Kind.Should().Be(UsageKind.AsrAudioSeconds);
        row.Quantity.Should().Be(2.5m);
        row.CreditsMillis.Should().Be(250);
        row.ModelName.Should().Be("whisper-test");
    }

    [Fact]
    public async Task Transcription_decorator_does_not_record_when_the_duration_cannot_be_read()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        ITranscriptionClient client = new MeteringTranscriptionClient(new FakeTranscription(), AsrProvider(), recorder);

        using (Scope())
        {
            await using MemoryStream notAWav = new(new byte[100]);
            await client.TranscribeAsync(notAWav, "a.wav", null, false, CancellationToken.None);
        }

        (await Flush(recorder, sink)).Should().BeEmpty();
    }

    [Fact]
    public void Wav_duration_uses_the_bytes_present_when_the_header_declares_a_streamed_data_size()
    {
        using MemoryStream stream = new(Wav(8_000, 3, streamedHeader: true));

        WavDuration.TryGetSeconds(stream, out double seconds).Should().BeTrue();
        seconds.Should().BeApproximately(3.0, 0.0001);
        stream.Position.Should().Be(0);
    }

    [Fact]
    public void Wav_duration_walks_past_a_metadata_chunk_bigger_than_any_header_read()
    {
        // A 1 MB LIST chunk ahead of "data": the reader used to stop at its first 4 KB and answer false.
        using MemoryStream stream = new(Wav(8_000, 3, listChunkBytes: 1_000_000));

        WavDuration.TryGetSeconds(stream, out double seconds).Should().BeTrue();
        seconds.Should().BeApproximately(3.0, 0.0001);
        stream.Position.Should().Be(0, "measuring the header must not consume the stream");
    }

    [Fact]
    public void Wav_duration_refuses_a_chunk_that_runs_past_the_end_of_the_file()
    {
        // Cut in the middle of the LIST body: its declared size no longer fits, so the walk stops
        // instead of reading past the file.
        byte[] full = Wav(8_000, 2, listChunkBytes: 4_000);
        using MemoryStream stream = new(full[..2_048]);

        WavDuration.TryGetSeconds(stream, out double seconds).Should().BeFalse();
        seconds.Should().Be(0);
        stream.Position.Should().Be(0);
    }

    [Fact]
    public void Transcription_factory_wraps_only_when_a_recorder_is_supplied()
    {
        ResolvedTranscriptionProvider provider = AsrProvider() with { Endpoint = "http://localhost:1" };

        new TranscriptionClientFactory().Get(provider).Should().NotBeOfType<MeteringTranscriptionClient>();
        new TranscriptionClientFactory(NewRecorder().Recorder).Get(provider).Should().BeOfType<MeteringTranscriptionClient>();
    }

    // ---- TTS ----

    private sealed class FakeSpeech : ISpeechSynthesisClient
    {
        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) =>
            Task.FromResult(new SynthesizedSpeech(new MemoryStream(), "audio/wav"));
    }

    private static ResolvedSpeechSynthesisProvider TtsProvider() => new(
        Guid.NewGuid(), "tts", InferenceProviderKind.FishAudio, "http://x", "s2-pro", "", 30);

    [Fact]
    public async Task Speech_decorator_records_the_utf8_byte_length_of_the_text_not_its_character_count()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        ISpeechSynthesisClient client = new MeteringSpeechSynthesisClient(new FakeSpeech(), TtsProvider(), recorder);

        using (Scope())
            await client.SynthesizeAsync(new SpeechRequest("héllo €", null, null), CancellationToken.None);

        UsageEvent row = (await Flush(recorder, sink)).Should().ContainSingle().Subject;
        row.Kind.Should().Be(UsageKind.TtsInputBytes);
        row.Quantity.Should().Be(Encoding.UTF8.GetByteCount("héllo €"));
        row.Quantity.Should().Be(10);
        row.CreditsMillis.Should().Be(20);
        row.ProviderKind.Should().Be(InferenceProviderKind.FishAudio);
    }

    [Fact]
    public async Task Speech_decorator_records_a_per_request_model_override_as_the_model()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        ISpeechSynthesisClient client = new MeteringSpeechSynthesisClient(new FakeSpeech(), TtsProvider(), recorder);

        using (Scope())
            await client.SynthesizeAsync(new SpeechRequest("hi", null, "other-model"), CancellationToken.None);

        (await Flush(recorder, sink)).Should().ContainSingle().Which.ModelName.Should().Be("other-model");
    }

    [Fact]
    public async Task Embedding_transcription_and_speech_are_priced_under_their_own_capability_and_account()
    {
        NonChatPriceService prices = new();
        CapturingSink sink = new();
        UsageRecorder recorder = new(prices, sink, NullLogger<UsageRecorder>.Instance);
        Guid account = Guid.NewGuid();

        using (Scope())
        {
            await new MeteringEmbeddingGenerator(new FakeEmbeddings(new UsageDetails { InputTokenCount = 3 }),
                EmbeddingProvider() with { AccountId = account }, recorder).GenerateAsync(["a"]);
            await using MemoryStream stream = new(Wav(16_000, 1));
            await new MeteringTranscriptionClient(new FakeTranscription(), AsrProvider() with { AccountId = account }, recorder)
                .TranscribeAsync(stream, "a.wav", null, false, CancellationToken.None);
            await new MeteringSpeechSynthesisClient(new FakeSpeech(), TtsProvider() with { AccountId = account }, recorder)
                .SynthesizeAsync(new SpeechRequest("hi", null, null), CancellationToken.None);
        }

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        prices.Requests.Select(r => (r.Unit, r.Capability)).Should().Equal(
            (UsageKind.EmbeddingTokens, InferenceProviderCapability.Embedding),
            (UsageKind.AsrAudioSeconds, InferenceProviderCapability.Transcription),
            (UsageKind.TtsInputBytes, InferenceProviderCapability.SpeechSynthesis));
        prices.Requests.Should().OnlyContain(r => r.AccountId == account && r.Variant == null);
        rows.Should().OnlyContain(r => r.PriceId != null && r.Variant == null);
    }

    [Fact]
    public void Speech_factory_wraps_only_when_a_recorder_is_supplied()
    {
        ResolvedSpeechSynthesisProvider provider = TtsProvider() with { Endpoint = "http://localhost:1" };

        new SpeechSynthesisClientFactory().Get(provider).Should().NotBeOfType<MeteringSpeechSynthesisClient>();
        new SpeechSynthesisClientFactory(NewRecorder().Recorder).Get(provider).Should().BeOfType<MeteringSpeechSynthesisClient>();
    }

    // ---- compute pricing keys ----

    [Theory]
    [InlineData(1280, 720, "720p")]
    [InlineData(720, 1280, "720p")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1080, 1920, "1080p")]
    [InlineData(2560, 1440, "uhd")]
    [InlineData(3840, 2160, "uhd")]
    [InlineData(0, 0, "720p")]
    public void Resolution_tier_is_taken_from_the_short_side(int width, int height, string tier)
    {
        ComputeMetering.TierFor(width, height).Should().Be(tier);
    }

    [Fact]
    public async Task Compute_is_priced_per_tier_on_the_system_compute_capability_and_a_runner_job_is_free()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();

        using (Scope())
        {
            await ComputeMetering.RecordSecondsAsync(recorder, UsageKind.RenderOutputSeconds, 10, 1280, 720, null);
            await ComputeMetering.RecordSecondsAsync(recorder, UsageKind.RenderOutputSeconds, 10, 3840, 2160, null);
            await ComputeMetering.RecordSecondsAsync(recorder, UsageKind.RenderOutputSeconds, 10, 1920, 1080, null,
                billingMode: UsageBillingMode.Runner);
        }

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().OnlyContain(r => r.ProviderId == WellKnownModelCatalog.ComputeCapabilityId
            && r.ModelName == WellKnownModelCatalog.ComputeModelCode && r.PriceId != null);
        UsageEvent hd = rows.Single(r => r.Variant == "720p");
        UsageEvent uhd = rows.Single(r => r.Variant == "uhd");
        uhd.CreditsMillis.Should().BeGreaterThan(hd.CreditsMillis * 7, "a 4K second is priced well above a 720p second without any code change");
        // Derived from the defaults rather than hardcoded: re-pricing compute must move this with them.
        hd.CostUsd.Should().Be(10m * PricingDefaults.ComputeRenderUsdPerSecond);
        hd.CreditsMillis.Should().Be((long)Math.Round(10m * PricingDefaults.ComputeRenderUsdPerSecond * 1.2m * 1000m / 0.006m,
            MidpointRounding.AwayFromZero));
        UsageEvent runner = rows.Single(r => r.BillingMode == UsageBillingMode.Runner);
        runner.CreditsMillis.Should().Be(0);
        runner.Variant.Should().Be("1080p");
    }

    [Fact]
    public async Task Compute_with_a_non_positive_duration_records_nothing()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();

        using (Scope())
        {
            await ComputeMetering.RecordSecondsAsync(recorder, UsageKind.EncodeOutputSeconds, 0, 1920, 1080, null);
            await ComputeMetering.RecordSecondsAsync(recorder, UsageKind.EncodeOutputSeconds, double.NaN, 1920, 1080, null);
        }

        (await Flush(recorder, sink)).Should().BeEmpty();
    }

    [Fact]
    public void Default_compute_prices_cover_every_compute_unit_and_tier_with_unique_keys_and_a_placeholder_note()
    {
        IReadOnlyList<SuggestedPrice> prices = PricingDefaults.ComputePrices();

        prices.Should().HaveCount(PricingDefaults.ComputeUnits.Length * 3);
        prices.Select(p => (p.Unit, p.Variant)).Should().OnlyHaveUniqueItems();
        prices.Should().OnlyContain(p => p.Notes.Contains("PLACEHOLDER"));
        prices.Select(p => p.Variant).Distinct().Should().BeEquivalentTo(
            [ComputeMetering.Tier720, ComputeMetering.Tier1080, ComputeMetering.TierUhd]);
    }

    /// <summary>The defaults a fresh install prices compute with are, figure for figure, the old seed's compute cards rebuilt on the current render anchor.</summary>
    [Fact]
    public void Default_compute_prices_equal_the_pre_catalog_compute_cards()
    {
        PricingDefaults.ComputePrices().Select(p => (p.Unit, p.Variant, p.CostUsdPerUnit)).Should().BeEquivalentTo(
            ReelBolt.WorkflowEngine.Tests.Pricing.LegacyRateCardSeed.ComputeCards(DateTime.UtcNow, PricingDefaults.ComputeRenderUsdPerSecond)
                .Select(c => (c.UsageKind, (string?)c.ModelPattern, c.CostUsdPerUnit)));
    }

    // ---- explicit idempotency key ----

    [Fact]
    public async Task An_explicit_idempotency_key_replaces_the_generated_one_and_consumes_no_call_number()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();

        using (Scope())
        {
            UsageScopeState scope = UsageScope.Current!;
            await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.VideoGenSeconds, 5, null, InferenceProviderKind.MiniMax, "m", UsageBillingMode.Platform, IdempotencyKey: "videogen:abc"));

            scope.NextCallSeq().Should().Be(1);
        }

        (await Flush(recorder, sink)).Should().ContainSingle().Which.IdempotencyKey.Should().Be("videogen:abc");
    }

    // ---- indexing and docs-index attribution ----

    private sealed class SingleChunk : IFileChunker
    {
        public IReadOnlyList<FileChunk> Chunk(string content, string fileName) => [new FileChunk(0, 1, "text", content)];
    }

    [Fact]
    public async Task Indexing_embeddings_are_charged_to_the_projects_organization_with_source_indexing()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        Guid orgId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();

        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Projects.Add(new Project { Id = projectId, Name = "p", OrganizationId = orgId, OwnerId = Guid.NewGuid() });
        db.ProjectFiles.Add(new ProjectFile { Id = fileId, ProjectId = projectId, OriginalFileName = "a.md", StorageKey = "k" });
        await db.SaveChangesAsync();

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(projectId, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("twelve chars")));

        ProjectFileIndexingConsumer consumer = new(
            db, storage.Object, new SingleChunk(),
            new MeteringEmbeddingGenerator(new FakeEmbeddings(new UsageDetails { InputTokenCount = 7 }), EmbeddingProvider(), recorder),
            Mock.Of<IVectorIndexService>(), NullLogger<ProjectFileIndexingConsumer>.Instance);

        Mock<ConsumeContext<ProjectFileIndexingRequested>> context = new();
        context.SetupGet(c => c.Message).Returns(new ProjectFileIndexingRequested { ProjectId = projectId, FileId = fileId, Operation = "Upsert" });
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        await consumer.Consume(context.Object);

        UsageEvent row = (await Flush(recorder, sink)).Should().ContainSingle().Subject;
        row.Kind.Should().Be(UsageKind.EmbeddingTokens);
        row.Quantity.Should().Be(7);
        row.OrganizationId.Should().Be(orgId, "the project's organization pays, not the platform");
        row.ProjectId.Should().Be(projectId);
        row.Source.Should().Be(UsageSource.Indexing);
        UsageScope.Current.Should().BeNull("the scope ends with the message");
    }

    [Fact]
    public async Task Docs_indexing_runs_under_a_platform_org_docs_index_scope()
    {
        UsageScopeState? seen = null;
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveEmbeddingAsync(null, It.IsAny<CancellationToken>()))
            .Callback(() => seen = UsageScope.Current)
            .ReturnsAsync((ResolvedEmbeddingProvider?)null);

        PlatformDocsCorpus corpus = new([new PlatformDocsChunk("c1", "a.md", PlatformDocsAudience.User, "T", "S", "/docs/a", "text")]);
        QdrantPlatformDocsIndex index = new(
            Options.Create(new VectorSearchOptions()),
            Options.Create(new PlatformDocsOptions()),
            new PlatformDocsCorpusProvider(corpus),
            resolver.Object,
            new FakeEmbeddings(null),
            NullLogger<QdrantPlatformDocsIndex>.Instance);

        (await index.EnsureIndexedAsync(CancellationToken.None)).Should().BeFalse("no embedding provider is configured in this test");

        seen.Should().NotBeNull();
        seen!.OrganizationId.Should().Be(WellKnownOrganizations.PlatformId);
        seen.Source.Should().Be(UsageSource.DocsIndex);
        UsageScope.Current.Should().BeNull();
    }

    // ---- rendered output ----

    private sealed class SandboxHandler(byte[] rendered) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"output":"rendered"}""") });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(rendered) });
        }
    }

    private static (ReactRemotionSandboxTools Tool, Mock<IMediaProbe> Probe) RenderTool(
        IUsageRecorder? recorder, byte[] rendered, out WorkflowExecutionContext ctx)
    {
        Guid executionId = Guid.NewGuid();
        ctx = new WorkflowExecutionContext { ExecutionId = executionId, ProjectId = Guid.NewGuid(), CorrelationId = "t" };

        Mock<IHttpClientFactory> http = new();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new SandboxHandler(rendered)));

        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.SetupGet(a => a.Current).Returns(ctx);

        Mock<IAmazonS3> s3 = new();
        s3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutObjectResponse());
        s3.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(rendered) });

        Mock<IMediaProbe> probe = new();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sandbox:BaseUrl"] = "http://sandbox:8080",
            ["Sandbox:ApiToken"] = "t",
            ["MinIO:BucketName"] = "b"
        }).Build();

        ReactRemotionSandboxTools tool = new(
            http.Object, accessor.Object, Mock.Of<IProjectFileWorkspace>(), s3.Object, config,
            NullLogger<ReactRemotionSandboxTools>.Instance, Mock.Of<IServiceScopeFactory>(), probe.Object, recorder);
        return (tool, probe);
    }

    [Fact]
    public async Task Render_is_metered_from_the_probe_of_the_uploaded_file_not_from_wall_clock()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        (ReactRemotionSandboxTools tool, Mock<IMediaProbe> probe) = RenderTool(recorder, new byte[] { 1, 2, 3, 4 }, out _);
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(12.5, 30, 1, 1920, 1080, "h264", null, null));

        string result;
        using (Scope())
            result = await tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        result.Should().Contain("\"success\":true");
        UsageEvent row = (await Flush(recorder, sink)).Should().ContainSingle().Subject;
        row.Kind.Should().Be(UsageKind.RenderOutputSeconds);
        row.Quantity.Should().Be(12.5m, "the probed duration of the rendered file, however long the render took");
        row.Variant.Should().Be("1080p");
        row.ProviderId.Should().Be(WellKnownModelCatalog.ComputeCapabilityId);
        row.OrganizationId.Should().Be(OrgId);
    }

    [Fact]
    public async Task A_still_render_is_not_probed_or_metered_as_video_seconds()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        (ReactRemotionSandboxTools tool, Mock<IMediaProbe> probe) = RenderTool(recorder, new byte[] { 1, 2, 3, 4 }, out _);

        using (Scope())
            await tool.RenderVideoAndUploadToStorage("Main", "thumb.png");

        probe.Verify(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await Flush(recorder, sink)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_probe_failure_never_fails_the_render()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        (ReactRemotionSandboxTools tool, Mock<IMediaProbe> probe) = RenderTool(recorder, new byte[] { 1, 2, 3, 4 }, out WorkflowExecutionContext ctx);
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("ffprobe died"));

        string result;
        using (Scope())
            result = await tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        result.Should().Contain("\"success\":true");
        ctx.PendingOutputStorageKey.Should().NotBeNull();
        (await Flush(recorder, sink)).Should().BeEmpty();
    }
}
