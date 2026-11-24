#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// agent-model-choice: <c>costIndex</c> on the selectable list, a RATIO only (no money leaves the server): the price of a
/// fixed mix (1M input + 0.25M output tokens) at the platform price, over the cheapest priced platform Chat model, so the
/// customer sees "2.5x" next to a model. Null for BYO, non-Chat and unpriced rows.
/// </summary>
public sealed class SelectableCostIndexTests
{
    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = string.Empty;
            return ciphertext is not null;
        }
    }

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options);

    /// <summary>Credits per MILLION tokens by capability id: (input, output). A capability with no entry is unpriced.</summary>
    private readonly Dictionary<Guid, (long Input, long Output)> _rates = [];

    private readonly List<PriceRequest> _requests = [];

    private InferenceProviderAdminService Service()
    {
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PriceRequest, CancellationToken>((r, _) =>
            {
                _requests.Add(r);
                if (r.ModelCapabilityId is not { } id || !_rates.TryGetValue(id, out (long Input, long Output) rate))
                    throw new UnpricedUsageException(r.Unit, r.Kind, r.ModelCode ?? "?");
                long perMillion = r.Unit == UsageKind.LlmInputTokens ? rate.Input : rate.Output;
                return new ValueTask<PriceQuote>(new PriceQuote((long)(r.Quantity / 1_000_000m * perMillion), 0m, null));
            });
        return new InferenceProviderAdminService(
            _db, new PlainSecrets(),
            new InferenceProviderTester(_db, new PlainSecrets(), new Mock<IChatClientFactory>().Object,
                new Mock<ITranscriptionClientFactory>().Object, new Mock<IEmbeddingClientFactory>().Object,
                new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
                new Mock<ISpeechSynthesisClientFactory>().Object, NullLogger<InferenceProviderTester>.Instance),
            prices: prices.Object);
    }

    private Guid Add(string name, long? input, long? output, Guid? org = null, bool enabled = true,
        InferenceProviderCapability capability = InferenceProviderCapability.Chat)
    {
        ModelCapabilityView row = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            ModelId = Guid.NewGuid(),
            OrganizationId = org,
            Name = name,
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = capability,
            Endpoint = "https://198.51.100.7/v1",
            ModelName = "m-" + name,
            ApiKeyEncrypted = "enc:k",
            IsEnabled = enabled,
        };
        _db.AddFlat(row);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        if (input is { } i && output is { } o) _rates[row.Id] = (i, o);
        return row.Id;
    }

    private static decimal? IndexOf(List<SelectableProviderResponse> list, Guid id) => list.Single(r => r.Id == id).CostIndex;

    [Fact]
    public async Task The_cheapest_priced_platform_chat_model_is_1_and_the_rest_are_ratios_of_the_fixed_mix()
    {
        Guid cheap = Add("cheap", input: 100, output: 400);     // 100 + 0.25*400 = 200
        Guid dearer = Add("dearer", input: 500, output: 1000);  // 500 + 250 = 750 -> 3.75
        Guid odd = Add("odd", input: 300, output: 136);         // 300 + 34 = 334 -> 1.67

        List<SelectableProviderResponse> list = await Service().ListSelectableAsync(Guid.NewGuid(), InferenceProviderCapability.Chat, CancellationToken.None);

        IndexOf(list, cheap).Should().Be(1.00m);
        IndexOf(list, dearer).Should().Be(3.75m);
        IndexOf(list, odd).Should().Be(1.67m);
    }

    [Fact]
    public async Task Pricing_uses_one_million_input_and_a_quarter_million_output_tokens_in_platform_mode()
    {
        Add("only", input: 100, output: 400);

        await Service().ListSelectableAsync(Guid.NewGuid(), InferenceProviderCapability.Chat, CancellationToken.None);

        _requests.Should().OnlyContain(r => r.BillingMode == UsageBillingMode.Platform && r.Capability == InferenceProviderCapability.Chat);
        _requests.Single(r => r.Unit == UsageKind.LlmInputTokens).Quantity.Should().Be(1_000_000m);
        _requests.Single(r => r.Unit == UsageKind.LlmOutputTokens).Quantity.Should().Be(250_000m);
    }

    [Fact]
    public async Task Byo_non_chat_and_unpriced_rows_have_no_index_and_do_not_set_the_baseline()
    {
        Guid org = Guid.NewGuid();
        Guid platform = Add("platform", input: 100, output: 400);
        Guid byo = Add("byo", input: 1, output: 1, org: org);
        Guid unpriced = Add("unpriced", input: null, output: null);
        Guid vision = Add("vision", input: 1, output: 1, capability: InferenceProviderCapability.Vision);

        List<SelectableProviderResponse> list = await Service().ListSelectableAsync(org, null, CancellationToken.None);

        IndexOf(list, platform).Should().Be(1.00m, "a cheaper BYO or Vision row must not become the baseline");
        IndexOf(list, byo).Should().BeNull();
        IndexOf(list, unpriced).Should().BeNull();
        IndexOf(list, vision).Should().BeNull();
    }

    [Fact]
    public async Task The_baseline_is_the_cheapest_among_all_enabled_models_not_just_the_listed_ones()
    {
        Guid org = Guid.NewGuid();
        Guid cheapest = Add("cheapest", input: 100, output: 0);
        Guid listed = Add("listed", input: 200, output: 0);
        // A disabled model never counts toward the baseline.
        Add("disabled-cheaper", input: 10, output: 0, enabled: false);

        List<SelectableProviderResponse> list = await Service().ListSelectableAsync(org, InferenceProviderCapability.Chat, CancellationToken.None);

        IndexOf(list, cheapest).Should().Be(1.00m);
        IndexOf(list, listed).Should().Be(2.00m);
    }

    [Fact]
    public async Task Without_a_price_service_the_index_is_null()
    {
        Guid id = Add("any", input: 100, output: 400);
        InferenceProviderAdminService service = new(
            _db, new PlainSecrets(),
            new InferenceProviderTester(_db, new PlainSecrets(), new Mock<IChatClientFactory>().Object,
                new Mock<ITranscriptionClientFactory>().Object, new Mock<IEmbeddingClientFactory>().Object,
                new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
                new Mock<ISpeechSynthesisClientFactory>().Object, NullLogger<InferenceProviderTester>.Instance));

        List<SelectableProviderResponse> list = await service.ListSelectableAsync(Guid.NewGuid(), null, CancellationToken.None);

        IndexOf(list, id).Should().BeNull();
    }
}
