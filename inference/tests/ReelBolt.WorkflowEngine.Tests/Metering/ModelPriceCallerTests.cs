#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Metering;

/// <summary>
/// Review focus 3 (fail expensive, docs/design/model-catalog.md §5.3) through real callers: the EF price source and
/// <see cref="ModelPriceService"/> over a consolidated (Sqlite) catalog, priced by <see cref="UsageRecorder"/> as the
/// picture-op path (<see cref="MeteringImageClient"/>) records it. Promoted from the QA review probes (QA-6, QA-2).
/// </summary>
public sealed class ModelPriceCallerTests : IAsyncLifetime
{
    private static readonly DateTime SeedFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ISecretProtector _secrets =
        new DataProtectionSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<DataProtectionSecretProtector>.Instance);
    private InferenceApiDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = NewContext();
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private InferenceApiDbContext NewContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    /// <summary>One pre-catalog row after the 1:1 migration: provider + model + capability (capability id = old row id).</summary>
    private ModelCapability OldRow(string name, InferenceProviderCapability capability, string code, string cipher, DateTime created)
    {
        InferenceProvider provider = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = InferenceProviderKind.FalAi,
            Endpoint = "https://fal.run",
            ApiKeyEncrypted = cipher,
            CreatedAt = created,
            UpdatedAt = created
        };
        InferenceModel model = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            ModelCode = code,
            DisplayName = name,
            CreatedAt = created,
            UpdatedAt = created
        };
        ModelCapability row = new()
        {
            Id = provider.Id,
            ModelId = model.Id,
            Capability = capability,
            IsDefault = true,
            CreatedAt = created,
            UpdatedAt = created
        };
        _db.InferenceProviders.Add(provider);
        _db.InferenceModels.Add(model);
        _db.ModelCapabilities.Add(row);
        return row;
    }

    private sealed class Scopes(IServiceProvider root) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => root.CreateScope();
    }

    /// <summary>Keeps what each recorded call was charged (the image client discards it).</summary>
    private sealed class ChargedRecorder(IUsageRecorder inner) : IUsageRecorder
    {
        public List<long> Charged { get; } = [];

        public async ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
        {
            long credits = await inner.RecordAsync(request, ct);
            Charged.Add(credits);
            return credits;
        }
    }

    /// <summary>
    /// A consolidated fal account: the configured (cheap, default) ImageEdit model and a dear sibling ImageEdit model,
    /// first created as two providers and merged into the cheap one's (the oldest).
    /// </summary>
    private async Task<(ModelCapability Cheap, ModelCapability Dear, Guid Account, ServiceProvider Root)> ConsolidatedFalAccountAsync()
    {
        string falCipher = _secrets.Protect("placeholder-fal");
        ModelCapability cheap = OldRow("fal", InferenceProviderCapability.ImageEdit, "fal-ai/flux-pro/kontext", falCipher, SeedFrom);
        ModelCapability dear = OldRow("fal dear", InferenceProviderCapability.ImageEdit, "fal-ai/dear-edit", falCipher, SeedFrom.AddDays(1));
        dear.IsDefault = false;
        await _db.SaveChangesAsync();
        await new ModelCatalogUpgrader(_db, _secrets, new ConfigurationBuilder().Build(), NullLogger<ModelCatalogUpgrader>.Instance)
            .ApplyConsolidationAsync();

        _db.ModelCapabilityPrices.AddRange(
            new ModelCapabilityPrice { Id = Guid.NewGuid(), ModelCapabilityId = cheap.Id, Unit = UsageKind.ImageEditImages, CostUsdPerUnit = 0.04m, EffectiveFrom = SeedFrom },
            new ModelCapabilityPrice { Id = Guid.NewGuid(), ModelCapabilityId = dear.Id, Unit = UsageKind.ImageEditImages, CostUsdPerUnit = 0.10m, EffectiveFrom = SeedFrom });
        await _db.SaveChangesAsync();

        Guid account;
        await using (InferenceApiDbContext db = NewContext())
            account = (await db.InferenceModels.IgnoreQueryFilters().SingleAsync(m => m.Id == dear.ModelId)).ProviderId;
        account.Should().Be(cheap.Id, "the dear provider was merged into the cheap (oldest) one");

        ServiceCollection services = new();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(_connection));
        return (cheap, dear, account, services.BuildServiceProvider());
    }

    private static UsageRecorder Recorder(ServiceProvider root) =>
        new(new ModelPriceService(new EfModelPriceSource<InferenceApiDbContext>(new Scopes(root))), new CapturingSink(),
            NullLogger<UsageRecorder>.Instance);

    [Theory]
    [InlineData("fal-ai/not-configured-anywhere", null)]
    [InlineData("fal-ai/NOT-configured", "4K")]
    public async Task ExplicitUnknownModel_ThroughTheRealSourceAndRecorder_NeverPricesBelowConfigured(string model, string? variant)
    {
        (ModelCapability cheap, _, Guid account, ServiceProvider root) = await ConsolidatedFalAccountAsync();
        await using ServiceProvider services = root;
        UsageRecorder recorder = Recorder(root);

        long configured = await recorder.RecordAsync(new UsageRecordRequest(
            UsageKind.ImageEditImages, 1, cheap.Id, InferenceProviderKind.FalAi, "fal-ai/flux-pro/kontext",
            UsageBillingMode.Platform, Capability: InferenceProviderCapability.ImageEdit, AccountId: account));
        long explicitUnknown = await recorder.RecordAsync(new UsageRecordRequest(
            UsageKind.ImageEditImages, 1, cheap.Id, InferenceProviderKind.FalAi, model,
            UsageBillingMode.Platform, Capability: InferenceProviderCapability.ImageEdit, Variant: variant, AccountId: account));
        // The AccountId-less shape (a caller that only knows the capability id) must reach the same account.
        long noAccount = await recorder.RecordAsync(new UsageRecordRequest(
            UsageKind.ImageEditImages, 1, cheap.Id, InferenceProviderKind.FalAi, model,
            UsageBillingMode.Platform, Capability: InferenceProviderCapability.ImageEdit));

        configured.Should().Be(8000, "0.04 x 1.2 x 1000 / 0.006");
        explicitUnknown.Should().Be(20000, "the dearest ImageEdit row of the account");
        noAccount.Should().Be(explicitUnknown);
    }

    /// <summary>
    /// QA-2 through the picture-op path: a resolver snapshot cached before the consolidation (60 s TTL) still carries
    /// the merged-away provider as the account. An explicit unknown model must still fail expensive, never record 0.
    /// </summary>
    [Fact]
    public async Task PictureOp_WithAStaleAccountIdFromAPreConsolidationSnapshot_StillFailsExpensive()
    {
        (_, ModelCapability dear, _, ServiceProvider root) = await ConsolidatedFalAccountAsync();
        await using ServiceProvider services = root;
        ChargedRecorder recorder = new(Recorder(root));
        Guid mergedAway = dear.Id; // the dear row's own provider before it was merged (capability id = old row id)
        ResolvedImageProvider stale = new(dear.Id, "fal dear", InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit,
            "https://fal.run", "fal-ai/dear-edit", "placeholder-fal", 60, AccountId: mergedAway);
        Mock<IImageClient> inner = new();
        inner.Setup(c => c.EditAsync(It.IsAny<ImageEditRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedImageSet([new GeneratedImage([1], "image/png")]) { ResolvedModel = "fal-ai/not-configured-anywhere" });

        await new MeteringImageClient(inner.Object, stale, recorder)
            .EditAsync(new ImageEditRequest("fal-ai/not-configured-anywhere", "make it blue", "https://example.test/a.png"), default);

        recorder.Charged.Should().Equal([20000L], "the referenced capability's account now is the merged survivor");
    }
}
