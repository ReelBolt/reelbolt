using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Graphics;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The assistant's picture-editor surface: authority re-read per call (another organization's picture is
/// ABSENT, a refusal writes nothing), propose/apply token binding (document + payload + the target layers'
/// ids and names), writes routed through the editor's own controller actions, the change announcements the
/// editor follows, and the paid tools' cost in the proposal.
/// </summary>
public class AssistantPictureToolTests
{
    private sealed class CountingDb(DbContextOptions<InferenceApiDbContext> options) : InferenceApiDbContext(options)
    {
        public int SaveCount { get; private set; }

        public void Reset() => SaveCount = 0;

        public override int SaveChanges()
        {
            SaveCount++;
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class Caller(Guid id, OrganizationRole role = OrganizationRole.Member, bool verified = true) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole { get; set; } = role;
        public bool EmailVerified => verified;
    }

    /// <summary>Records every command instead of running it (used where a vendor would be called).</summary>
    private sealed class RecordingCommands : IImageDocumentCommands
    {
        public List<object> Calls { get; } = [];
        public Dictionary<string, byte[]> Masks { get; } = [];

        public Task<ImageCommandOutcome> CreateDocumentAsync(Guid projectId, CreateEditImageDocumentRequest request, CancellationToken ct) => Record(request);
        public Task<ImageCommandOutcome> CreateTextLayerAsync(Guid projectId, Guid documentId, CreateTextLayerRequest request, CancellationToken ct) => Record(request);
        public Task<ImageCommandOutcome> UpdateLayerAsync(Guid projectId, Guid documentId, Guid layerId, UpdateEditImageLayerRequest request, CancellationToken ct) => Record(request);
        public Task<ImageCommandOutcome> DeleteLayerAsync(Guid projectId, Guid documentId, Guid layerId, CancellationToken ct) => Record(layerId);
        public Task<ImageCommandOutcome> UpdateDocumentAsync(Guid projectId, Guid documentId, UpdateEditImageDocumentRequest request, CancellationToken ct) => Record(request);
        public Task<ImageCommandOutcome> SegmentSubjectAsync(Guid projectId, Guid documentId, SegmentSubjectRequest request, CancellationToken ct) => Record(request, "[]");

        public Task<ImageCommandOutcome> RunOperationAsync(Guid projectId, Guid documentId, EditImageOperationRequest request, CancellationToken ct) =>
            Record(request, "{\"layers\":[],\"operation\":\"" + request.Operation + "\",\"provider\":{\"kind\":\"FalAi\",\"model\":\"m\",\"capability\":\"ImageErase\"},\"revision\":2}");

        public Task<string> StoreMaskAsync(Guid projectId, Guid organizationId, Guid documentId, byte[] png, CancellationToken ct)
        {
            string key = $"{EditImageDocumentSupport.EditorPrefix(projectId, documentId)}mask-{Guid.NewGuid()}.png";
            Masks[key] = png;
            return Task.FromResult(key);
        }

        private Task<ImageCommandOutcome> Record(object request, string body = "{}")
        {
            Calls.Add(request);
            return Task.FromResult(new ImageCommandOutcome(200, JsonDocument.Parse(body).RootElement.Clone()));
        }
    }

    private sealed class World : IDisposable
    {
        public CountingDb Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Me { get; } = Guid.NewGuid();
        public Caller User { get; }
        public Project Project { get; }
        public EditImageDocument Document { get; }
        public EditImageLayer Source { get; }
        public EditImageLayer Title { get; }
        public EditImageLayer Logo { get; }
        public FakeFileStorage Storage { get; } = new();
        public List<AssistantImageDocumentChange> Changes { get; } = [];
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();

        public World(OrganizationRole role = OrganizationRole.Member, bool verified = true)
        {
            User = new Caller(Me, role, verified);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = Me, OrganizationId = TestTenancy.OrgOf(Me), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, OriginalFileName = "cover.png", MimeType = "image/png", StorageKey = $"projects/{Project.Id}/userFiles/cover.png", Category = "userFiles" };
            Document = new EditImageDocument { Id = Guid.NewGuid(), ProjectId = Project.Id, SourceProjectFileId = file.Id, CanvasWidth = 1280, CanvasHeight = 720, Revision = 1 };
            Source = new EditImageLayer { Id = Guid.NewGuid(), DocumentId = Document.Id, ZOrder = 1, Kind = EditImageLayerKind.Source, Role = "background", Label = "cover.png", StorageKey = file.StorageKey, Width = 1280, Height = 720, TransformJson = "{}" };
            Title = new EditImageLayer
            {
                Id = Guid.NewGuid(),
                DocumentId = Document.Id,
                ZOrder = 2,
                Kind = EditImageLayerKind.Text,
                Role = "overlay",
                Label = "Title",
                MimeType = "text/plain",
                TransformJson = "{\"x\":40,\"y\":60,\"scale\":1}",
                ContentJson = JsonSerializer.Serialize(new TextContentDto("BIG SALE"), EditImageDocumentSupport.Json)
            };
            Logo = new EditImageLayer { Id = Guid.NewGuid(), DocumentId = Document.Id, ZOrder = 3, Kind = EditImageLayerKind.Image, Role = "overlay", Label = "Logo", StorageKey = $"projects/{Project.Id}/userFiles/logo.png", Width = 200, Height = 100, TransformJson = "{\"x\":10,\"y\":10,\"scale\":1}" };
            Db.Projects.Add(Project);
            Db.ProjectFiles.Add(file);
            Db.EditImageDocuments.Add(Document);
            Db.EditImageLayers.AddRange(Source, Title, Logo);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            Db.Reset();
        }

        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();

        public AssistantPictureTools Tools(
            IImageDocumentCommands? commands = null,
            ICurrentUser? as_ = null,
            IChatClientFactory? chatClients = null,
            IWorkflowCostEstimator? costEstimator = null)
        {
            AssistantPictureTools tools = AssistantToolTestFactory.PictureTools(
                Db, as_ ?? User, Tokens, Storage,
                commands ?? AssistantToolTestFactory.PictureCommands(Db, as_ ?? User, Storage, Resolver.Object),
                Resolver.Object,
                costEstimator: costEstimator,
                chatClients: chatClients);
            tools.OnChanged = (change, _) => { Changes.Add(change); return Task.CompletedTask; };
            return tools;
        }

        public AssistantToolContext Context(Guid? documentId = null) => new(Me, Project.Id, null, documentId ?? Document.Id);

        public void Dispose() => Db.Dispose();
    }

    // ------------------------------------------------------------------ LookAtPicture (vision)

    /// <summary>Records what was sent to the vision model, and answers with a fixed description.</summary>
    private sealed class RecordingVision(string answer) : IChatClientFactory
    {
        public List<ChatMessage> Sent { get; } = [];

        public IChatClient Get(ResolvedInferenceProvider provider)
        {
            Mock<IChatClient> client = new();
            client
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, _, _) =>
                {
                    Sent.AddRange(messages);
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
                });
            return client.Object;
        }

        /// <summary>The image part the call carried, or null when it carried none.</summary>
        public DataContent? Image => Sent.SelectMany(m => m.Contents).OfType<DataContent>().FirstOrDefault();
    }

    private static void WithVision(World w, string answer = "A wooden barn at dusk, a red tractor on the left.")
    {
        w.Resolver
            .Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(
                Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible,
                "https://vision.example/v1", "gpt-4o", "key", 30));
    }

    [Fact]
    public async Task LookAtPicture_sends_nothing_to_the_model_when_the_source_is_held()
    {
        using World w = new();
        WithVision(w);
        RecordingVision vision = new("should not be asked");
        w.Storage.Objects[w.Source.StorageKey] = [1, 2, 3];
        ProjectFile source = w.Db.ProjectFiles.Single(f => f.Id == w.Document.SourceProjectFileId);
        source.OutputProducedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        w.Db.Projects.Single(p => p.Id == w.Project.Id).CsamOutputHoldAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await w.Db.SaveChangesAsync();

        JsonElement result = await Invoke(w.Tools(chatClients: vision), w.Context(), "LookAtPicture");

        result.GetRawText().Should().Be(ReelBolt.Inference.Api.Services.Outputs.OutputGate.ToolJson());
        vision.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task LookAtPicture_looks_at_the_base_layer_and_returns_what_the_model_reports()
    {
        using World w = new();
        WithVision(w);
        RecordingVision vision = new("A wooden barn at dusk, a red tractor on the left.");
        byte[] pixels = [1, 2, 3, 4, 5];
        w.Storage.Objects[w.Source.StorageKey] = pixels;

        JsonElement result = await Invoke(w.Tools(chatClients: vision), w.Context(), "LookAtPicture");

        ErrorOf(result).Should().BeNull();
        result.GetProperty("answer").GetString().Should().Be("A wooden barn at dusk, a red tractor on the left.");
        result.GetProperty("layerId").GetGuid().Should().Be(w.Source.Id);
        result.GetProperty("lookedAt").GetString().Should().Be("the picture's base image");
        result.GetProperty("model").GetString().Should().Be("gpt-4o");
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();

        // The model was shown the layer's OWN pixels, as an image part — not a URL, and not the text layer.
        vision.Sent.Should().ContainSingle();
        vision.Image.Should().NotBeNull();
        vision.Image!.Data.ToArray().Should().Equal(pixels);
        vision.Image.MediaType.Should().Be("image/png");
        vision.Sent[0].Contents.OfType<TextContent>().Should().ContainSingle();
    }

    [Fact]
    public async Task LookAtPicture_takes_a_question_and_can_be_pointed_at_one_layer()
    {
        using World w = new();
        WithVision(w);
        RecordingVision vision = new("A blue square logo.");
        w.Storage.Objects[w.Logo.StorageKey] = [9, 9];

        JsonElement result = await Invoke(
            w.Tools(chatClients: vision), w.Context(), "LookAtPicture",
            ("layerId", w.Logo.Id), ("question", "What does this logo show?"));

        ErrorOf(result).Should().BeNull();
        result.GetProperty("layerId").GetGuid().Should().Be(w.Logo.Id);
        result.GetProperty("lookedAt").GetString().Should().Be("the layer asked about");
        vision.Sent[0].Contents.OfType<TextContent>().Single().Text.Should().Be("What does this logo show?");
    }

    [Fact]
    public async Task LookAtPicture_says_so_when_the_installation_has_no_vision_service()
    {
        using World w = new();
        w.Storage.Objects[w.Source.StorageKey] = [1];

        // No ResolveVisionAsync setup: the resolver answers null, which is the "none configured" state.
        JsonElement result = await Invoke(w.Tools(chatClients: new RecordingVision("unused")), w.Context(), "LookAtPicture");

        ErrorOf(result).Should().Contain("No vision service");
    }

    [Fact]
    public async Task LookAtPicture_refuses_a_layer_that_is_not_on_the_picture()
    {
        using World w = new();
        WithVision(w);
        RecordingVision vision = new("unused");

        JsonElement result = await Invoke(
            w.Tools(chatClients: vision), w.Context(), "LookAtPicture", ("layerId", Guid.NewGuid()));

        ErrorOf(result).Should().Contain("has no layer");
        vision.Sent.Should().BeEmpty();
    }

    /// <summary>one look's input is bounded: a layer past MaxLookBytes is never sent to the vision model.</summary>
    [Fact]
    public async Task LookAtPicture_refuses_a_layer_larger_than_the_byte_cap_without_calling_the_model()
    {
        using World w = new();
        WithVision(w);
        RecordingVision vision = new("unused");
        w.Storage.Objects[w.Source.StorageKey] = new byte[AssistantPictureTools.MaxLookBytes + 1];

        JsonElement result = await Invoke(w.Tools(chatClients: vision), w.Context(), "LookAtPicture");

        ErrorOf(result).Should().Contain("too large");
        vision.Sent.Should().BeEmpty();
    }

    /// <summary>a layer whose header says it has more than MaxLookPixels pixels is never sent.</summary>
    [Fact]
    public async Task LookAtPicture_refuses_a_layer_with_more_pixels_than_the_cap_without_calling_the_model()
    {
        using World w = new();
        WithVision(w);
        RecordingVision vision = new("unused");
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(20_000, 20_000);

        JsonElement result = await Invoke(w.Tools(chatClients: vision), w.Context(), "LookAtPicture");

        ErrorOf(result).Should().Contain("too large");
        vision.Sent.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ sizing a call's pre-claim

    /// <summary>
    /// The turn guard's look sizer resolves exactly the layer LookAtPicture would send (the lowest-ZOrder layer with
    /// stored pixels, or the layerId asked for) and answers the larger of its recorded and its header size.
    /// </summary>
    [Fact]
    public async Task SizeLookAsync_sizes_the_layer_LookAtPicture_would_send()
    {
        using World w = new();
        w.Db.EditImageLayers.Add(new EditImageLayer
        {
            Id = Guid.NewGuid(),
            DocumentId = w.Document.Id,
            ZOrder = 0,
            Kind = EditImageLayerKind.Text,
            Role = "overlay",
            Label = "Behind",
            MimeType = "text/plain",
            TransformJson = "{}",
            ContentJson = JsonSerializer.Serialize(new TextContentDto("BEHIND"), EditImageDocumentSupport.Json)
        });
        w.Db.SaveChanges();
        w.Db.ChangeTracker.Clear();
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(6000, 4000);
        w.Storage.Objects[w.Logo.StorageKey] = PngHeader(100, 100);
        AssistantPictureTools tools = w.Tools();

        (await tools.SizeLookAsync(null, w.Context(), CancellationToken.None))
            .Should().Be(new LookSizing(w.Source.Id, 6000L * 4000), "the base layer's header is larger than its recorded 1280 x 720");
        (await tools.SizeLookAsync(
                new Dictionary<string, object?> { ["layerId"] = JsonSerializer.SerializeToElement(w.Logo.Id) }, w.Context(), CancellationToken.None))
            .Should().Be(new LookSizing(w.Logo.Id, 200L * 100), "the logo's recorded 200 x 100 is larger than its 100 x 100 header");
    }

    /// <summary>A picture the caller cannot view is never sized: the sizer answers null (the guard claims the cap) and leaks nothing.</summary>
    [Fact]
    public async Task SizeLookAsync_is_null_for_a_document_the_caller_cannot_view()
    {
        using World w = new();
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(6000, 4000);
        Caller stranger = new(Guid.NewGuid());

        LookSizing? pixels = await w.Tools(as_: stranger).SizeLookAsync(
            null, new AssistantToolContext(stranger.UserId, w.Project.Id, null, w.Document.Id), CancellationToken.None);

        pixels.Should().BeNull();
    }

    /// <summary>
    /// A layer whose header cannot be read is of unknown size whatever its row records (the vision vendor decodes the
    /// bytes, not the row), so the sizer answers no pixels (the guard claims the 40 MP cap), still naming the layer.
    /// </summary>
    [Fact]
    public async Task SizeLookAsync_has_no_pixels_when_the_header_cannot_be_read()
    {
        using World w = new();
        w.Storage.Objects[w.Source.StorageKey] = [1, 2, 3];

        (await w.Tools().SizeLookAsync(null, w.Context(), CancellationToken.None)).Should().Be(new LookSizing(w.Source.Id, null));
    }

    /// <summary>
    /// T-095: the look sends at most what it was claimed at. Once a claim sized the logo at 512 x 512, new
    /// bytes under the same storage key that declare 2000 x 2000 (or that cannot be read) are refused in plain words and
    /// nothing is sent to the vision model; the bytes that were sized still go through.
    /// </summary>
    [Fact]
    public async Task LookAtPicture_refuses_when_the_bytes_declare_more_pixels_than_were_claimed()
    {
        using World w = new();
        WithVision(w);
        RecordingVision vision = new("A logo.");
        AssistantPictureTools tools = w.Tools(chatClients: vision);
        w.Storage.Objects[w.Logo.StorageKey!] = PngHeader(512, 512);
        Dictionary<string, object?> arguments = new() { ["layerId"] = JsonSerializer.SerializeToElement(w.Logo.Id) };
        (await tools.SizeLookAsync(arguments, w.Context(), CancellationToken.None)).Should().Be(new LookSizing(w.Logo.Id, 512L * 512));

        ErrorOf(await Invoke(tools, w.Context(), "LookAtPicture", ("layerId", w.Logo.Id))).Should().BeNull("the sized bytes are within the claim");
        vision.Sent.Should().ContainSingle();
        vision.Sent.Clear();

        w.Storage.Objects[w.Logo.StorageKey!] = PngHeader(2000, 2000);
        string? refused = ErrorOf(await Invoke(tools, w.Context(), "LookAtPicture", ("layerId", w.Logo.Id)));
        refused.Should().Contain("changed");
        vision.Sent.Should().BeEmpty("nothing larger than the claim is sent to the vendor");

        w.Storage.Objects[w.Logo.StorageKey!] = [1, 2, 3];
        ErrorOf(await Invoke(tools, w.Context(), "LookAtPicture", ("layerId", w.Logo.Id))).Should().Contain("changed");
        vision.Sent.Should().BeEmpty("bytes of unknown size were not what the claim sized");
    }

    /// <summary>
    /// T-095: with no claim recorded for the layer (billing off, a turn not charged), or one recorded at an
    /// unknown size (claimed at the pixel cap), a look behaves exactly as before: any size within the caps is sent.
    /// </summary>
    [Fact]
    public async Task LookAtPicture_without_a_recorded_bound_behaves_as_before()
    {
        using World w = new();
        WithVision(w);
        RecordingVision vision = new("A logo.");
        AssistantPictureTools tools = w.Tools(chatClients: vision);
        w.Storage.Objects[w.Logo.StorageKey!] = PngHeader(2000, 2000);

        ErrorOf(await Invoke(tools, w.Context(), "LookAtPicture", ("layerId", w.Logo.Id))).Should().BeNull();
        vision.Sent.Should().ContainSingle();

        w.Storage.Objects[w.Source.StorageKey] = [1, 2, 3];
        (await tools.SizeLookAsync(null, w.Context(), CancellationToken.None)).Should().Be(new LookSizing(w.Source.Id, null));
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(6000, 4000);
        ErrorOf(await Invoke(tools, w.Context(), "LookAtPicture")).Should().BeNull("a claim at the pixel cap covers any size within it");
        vision.Sent.Should().HaveCount(2);
    }

    /// <summary>
    /// T-095: the picture tool's worst case is one request per tier in force of the model the call resolves,
    /// plus the variant-less one (the price service picks the row; the guard claims the dearest estimate), for the most
    /// images the operation returns. A retired tier is not asked about.
    /// </summary>
    [Fact]
    public async Task RunPictureToolWorstCaseAsync_returns_one_request_per_in_force_variant_and_the_variant_less_one()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageErase, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase());
        string endpoint = ImageJobCapabilities.FalDefaultFor(InferenceProviderCapability.ImageErase)!.Id;
        InferenceProvider fal = new() { Id = Guid.NewGuid(), Name = "fal", Kind = InferenceProviderKind.FalAi, Endpoint = "https://queue.fal.run" };
        InferenceModel model = new() { Id = Guid.NewGuid(), ProviderId = fal.Id, ModelCode = endpoint, DisplayName = "erase" };
        ModelCapability capability = new() { Id = Guid.NewGuid(), ModelId = model.Id, Capability = InferenceProviderCapability.ImageErase };
        w.Db.InferenceProviders.Add(fal);
        w.Db.InferenceModels.Add(model);
        w.Db.ModelCapabilities.Add(capability);
        w.Db.ModelCapabilityPrices.AddRange(
            new ModelCapabilityPrice { Id = Guid.NewGuid(), ModelCapabilityId = capability.Id, Unit = UsageKind.ImageEditImages, Variant = null, CostUsdPerUnit = 0.001m, EffectiveFrom = DateTime.UtcNow.AddDays(-1) },
            new ModelCapabilityPrice { Id = Guid.NewGuid(), ModelCapabilityId = capability.Id, Unit = UsageKind.ImageEditImages, Variant = "low", CostUsdPerUnit = 0.006m, EffectiveFrom = DateTime.UtcNow.AddDays(-1) },
            new ModelCapabilityPrice { Id = Guid.NewGuid(), ModelCapabilityId = capability.Id, Unit = UsageKind.ImageEditImages, Variant = "max", CostUsdPerUnit = 0.1584m, EffectiveFrom = DateTime.UtcNow.AddDays(-1) },
            new ModelCapabilityPrice { Id = Guid.NewGuid(), ModelCapabilityId = capability.Id, Unit = UsageKind.ImageEditImages, Variant = "MAX ", CostUsdPerUnit = 0.1584m, EffectiveFrom = DateTime.UtcNow.AddDays(-3), EffectiveTo = DateTime.UtcNow.AddDays(-2) },
            new ModelCapabilityPrice { Id = Guid.NewGuid(), ModelCapabilityId = capability.Id, Unit = UsageKind.ImageEditImages, Variant = "retired", CostUsdPerUnit = 9m, EffectiveFrom = DateTime.UtcNow.AddDays(-9), EffectiveTo = DateTime.UtcNow.AddDays(-2) },
            new ModelCapabilityPrice { Id = Guid.NewGuid(), ModelCapabilityId = capability.Id, Unit = UsageKind.ImageEditImages, Variant = "Low", CostUsdPerUnit = 0.006m, EffectiveFrom = DateTime.UtcNow.AddHours(-1) });
        w.Db.SaveChanges();
        w.Db.ChangeTracker.Clear();

        IReadOnlyList<ReelBolt.Shared.Metering.PriceRequest>? requests = await w.Tools().RunPictureToolWorstCaseAsync(
            new Dictionary<string, object?> { ["tool"] = JsonSerializer.SerializeToElement("erase") }, w.Context(), CancellationToken.None);

        requests.Should().NotBeNull();
        requests!.Select(r => r.Variant?.ToLowerInvariant()).Should().BeEquivalentTo(new string?[] { null, "low", "max" },
            "every distinct tier in force (case-insensitive) and the variant-less request, never a retired tier");
        requests.Should().AllSatisfy(request =>
        {
            request.ModelCode.Should().Be(endpoint);
            request.Kind.Should().Be(InferenceProviderKind.FalAi);
            request.Capability.Should().Be(InferenceProviderCapability.ImageErase);
            request.Unit.Should().Be(UsageKind.ImageEditImages);
            request.BillingMode.Should().Be(UsageBillingMode.Platform);
            request.Quantity.Should().Be(1, "every editor operation returns one image");
        });

        (await w.Tools().RunPictureToolWorstCaseAsync(
                new Dictionary<string, object?> { ["tool"] = "not-a-tool" }, w.Context(), CancellationToken.None))
            .Should().BeNull("an unknown tool cannot be priced, so the guard claims the default");
    }

    /// <summary>A PNG signature and IHDR chunk declaring <paramref name="width"/> x <paramref name="height"/>.</summary>
    /// <summary>Records the source layer's pixel size in the stored row the picture tools read.</summary>
    private static async Task SetSourceSizeAsync(World w, int? width, int? height)
    {
        EditImageLayer row = await w.Db.EditImageLayers.FirstAsync(l => l.Id == w.Source.Id);
        row.Width = width;
        row.Height = height;
        await w.Db.SaveChangesAsync();
    }

    private static byte[] PngHeader(int width, int height)
    {
        byte[] png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        png[24] = 8;
        png[25] = 6;
        return png;
    }

    private static async Task<JsonElement> Invoke(AssistantPictureTools tools, AssistantToolContext context, string name, params (string Key, object? Value)[] args)
    {
        AIFunction tool = tools.GetTools(context).Single(t => t.Name == name);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        object? raw = await tool.InvokeAsync(arguments);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string? ErrorOf(JsonElement result) =>
        result.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string Token(JsonElement proposal) => proposal.GetProperty("confirmationToken").GetString()!;

    // ------------------------------------------------------------------ the closed set

    [Fact]
    public void The_surface_is_three_reads_and_six_propose_apply_pairs()
    {
        using World w = new();
        string[] names = [.. w.Tools().GetTools(w.Context()).Select(t => t.Name)];

        names.Should().HaveCount(15);
        names.Where(n => n.StartsWith("Propose", StringComparison.Ordinal)).Should().HaveCount(6);
        foreach (string propose in names.Where(n => n.StartsWith("Propose", StringComparison.Ordinal)))
            names.Should().Contain("Apply" + propose["Propose".Length..], "every mutation is a propose/apply pair");
        names.Should().Contain(["DescribePictureDocument", "ListPictureTools", "LookAtPicture"]);
    }

    // ------------------------------------------------------------------ authority and absence

    [Fact]
    public async Task With_no_picture_open_every_tool_answers_no_picture_open_and_writes_nothing()
    {
        using World w = new();
        AssistantPictureTools tools = w.Tools();
        AssistantToolContext noPicture = new(w.Me, w.Project.Id);

        foreach (AIFunction tool in tools.GetTools(noPicture))
        {
            JsonElement result = await Invoke(tools, noPicture, tool.Name, ArgumentsFor(tool.Name, w));
            ErrorOf(result).Should().Be(AssistantPictureTools.NoPictureMessage, tool.Name);
        }

        w.Db.SaveCount.Should().Be(0);
        w.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_picture_is_absent_never_forbidden_and_nothing_is_written()
    {
        using World w = new();
        Caller stranger = new(Guid.NewGuid());
        AssistantPictureTools tools = w.Tools(as_: stranger);
        // The stranger names the victim's project and document ids directly.
        AssistantToolContext context = new(stranger.UserId, w.Project.Id, null, w.Document.Id);

        foreach (AIFunction tool in tools.GetTools(context))
        {
            JsonElement result = await Invoke(tools, context, tool.Name, ArgumentsFor(tool.Name, w));
            ErrorOf(result).Should().Be(AssistantPictureTools.PictureNotFoundMessage, tool.Name);
        }

        w.Db.SaveCount.Should().Be(0);
        w.Db.ChangeTracker.Entries().Count(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).Should().Be(0);
    }

    [Fact]
    public async Task A_document_of_another_project_is_absent_even_inside_the_callers_own_organization()
    {
        using World w = new();
        Project other = new() { Id = Guid.NewGuid(), Name = "Other", OwnerId = w.Me, OrganizationId = w.Project.OrganizationId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        w.Db.Projects.Add(other);
        w.Db.SaveChanges();
        w.Db.Reset();

        JsonElement result = await Invoke(w.Tools(), new AssistantToolContext(w.Me, other.Id, null, w.Document.Id), "DescribePictureDocument");

        ErrorOf(result).Should().Be(AssistantPictureTools.PictureNotFoundMessage);
    }

    // ------------------------------------------------------------------ any project, any picture

    [Fact]
    public async Task A_picture_named_by_project_and_document_is_reachable_from_a_page_with_no_project_open()
    {
        using World w = new();
        AssistantPictureTools tools = w.Tools();
        AssistantToolContext home = new(w.Me);

        JsonElement described = await Invoke(tools, home, "DescribePictureDocument",
            ("projectId", w.Project.Id), ("documentId", w.Document.Id));
        ErrorOf(described).Should().BeNull();
        described.GetProperty("projectName").GetString().Should().Be("P");
        described.GetProperty("name").GetString().Should().Be("cover.png", "an unnamed picture is called by the file it was made from");
        described.GetProperty("layers").GetArrayLength().Should().Be(3);

        JsonElement proposal = await Invoke(tools, home, "ProposeAddTextLayer",
            ("text", "50% OFF"), ("projectId", w.Project.Id), ("documentId", w.Document.Id));
        ErrorOf(proposal).Should().BeNull();
        proposal.GetProperty("projectName").GetString().Should().Be("P");
        proposal.GetProperty("pictureName").GetString().Should().Be("cover.png");

        JsonElement applied = await Invoke(tools, home, "ApplyAddTextLayer",
            ("text", "50% OFF"), ("projectId", w.Project.Id), ("documentId", w.Document.Id), ("confirmationToken", Token(proposal)));
        applied.GetProperty("applied").GetBoolean().Should().BeTrue(applied.GetRawText());
        applied.GetProperty("pictureName").GetString().Should().Be("cover.png");
        w.Changes.Should().ContainSingle().Which.ProjectId.Should().Be(w.Project.Id);
    }

    [Fact]
    public async Task With_no_picture_open_Describe_lists_the_pictures_of_the_project_by_name()
    {
        using World w = new();
        AssistantPictureTools tools = w.Tools();

        foreach ((AssistantToolContext context, (string, object?)[] args) in new (AssistantToolContext, (string, object?)[])[]
                 {
                     (new AssistantToolContext(w.Me), [("projectId", w.Project.Id)]),
                     (new AssistantToolContext(w.Me, w.Project.Id), [])
                 })
        {
            JsonElement result = await Invoke(tools, context, "DescribePictureDocument", args);

            ErrorOf(result).Should().Be(AssistantPictureTools.NoPictureMessage, "the refusal and its text are unchanged");
            result.GetProperty("projectName").GetString().Should().Be("P");
            JsonElement picture = result.GetProperty("pictures").EnumerateArray().Single();
            picture.GetProperty("documentId").GetGuid().Should().Be(w.Document.Id);
            picture.GetProperty("name").GetString().Should().Be("cover.png");
        }

        w.Db.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task A_stranger_naming_a_project_gets_the_bare_refusal_and_no_list_of_its_pictures()
    {
        using World w = new();
        Caller stranger = new(Guid.NewGuid());
        AssistantPictureTools tools = w.Tools(as_: stranger);

        JsonElement named = await Invoke(tools, new AssistantToolContext(stranger.UserId), "DescribePictureDocument", ("projectId", w.Project.Id));
        JsonElement missing = await Invoke(tools, new AssistantToolContext(stranger.UserId), "DescribePictureDocument", ("projectId", Guid.NewGuid()));

        named.GetRawText().Should().Be(missing.GetRawText());
        named.EnumerateObject().Select(p => p.Name).Should().Equal("error");
    }

    [Fact]
    public async Task The_picture_open_on_the_page_is_never_defaulted_onto_another_project()
    {
        using World w = new();
        Project other = new() { Id = Guid.NewGuid(), Name = "Other", OwnerId = w.Me, OrganizationId = w.Project.OrganizationId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        w.Db.Projects.Add(other);
        w.Db.SaveChanges();
        w.Db.Reset();
        AssistantPictureTools tools = w.Tools();

        // The page has this project and its picture open; the call names ANOTHER project and no picture.
        JsonElement described = await Invoke(tools, w.Context(), "DescribePictureDocument", ("projectId", other.Id));
        ErrorOf(described).Should().Be(AssistantPictureTools.NoPictureMessage);
        described.GetProperty("pictures").GetArrayLength().Should().Be(0);
        described.GetRawText().Should().NotContain("BIG SALE");

        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeAddTextLayer", ("text", "Hi"), ("projectId", other.Id));
        ErrorOf(proposal).Should().Be(AssistantPictureTools.NoPictureMessage);

        // Naming the page's own picture inside the wrong project is not found, not a silent redirect.
        JsonElement crossed = await Invoke(tools, w.Context(), "DescribePictureDocument", ("projectId", other.Id), ("documentId", w.Document.Id));
        ErrorOf(crossed).Should().Be(AssistantPictureTools.PictureNotFoundMessage);

        w.Db.SaveCount.Should().Be(0);
        w.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_named_picture_of_a_foreign_project_reads_exactly_like_one_that_does_not_exist()
    {
        using World w = new();
        Caller stranger = new(Guid.NewGuid());
        AssistantPictureTools tools = w.Tools(as_: stranger);
        AssistantToolContext home = new(stranger.UserId);

        foreach (string tool in new[] { "DescribePictureDocument", "ListPictureTools" })
        {
            JsonElement foreign = await Invoke(tools, home, tool, ("projectId", w.Project.Id), ("documentId", w.Document.Id));
            JsonElement missing = await Invoke(tools, home, tool, ("projectId", Guid.NewGuid()), ("documentId", Guid.NewGuid()));

            ErrorOf(foreign).Should().Be(AssistantPictureTools.PictureNotFoundMessage, tool);
            foreign.GetRawText().Should().Be(missing.GetRawText(), tool);
        }

        foreach (string tool in new[] { "ProposeAddTextLayer" })
        {
            JsonElement foreign = await Invoke(tools, home, tool, ("text", "x"), ("projectId", w.Project.Id), ("documentId", w.Document.Id));
            ErrorOf(foreign).Should().Be(AssistantPictureTools.PictureNotFoundMessage, tool);
        }

        w.Db.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task A_role_without_project_access_is_refused_on_every_apply_and_nothing_is_written()
    {
        using World w = new();
        // Propose as a Member, then the role changes to Billing before the apply: authority is re-read.
        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeDeletePictureLayers", ("layerIds", new[] { w.Logo.Id }));
        Caller demoted = new(w.Me, OrganizationRole.Billing);
        RecordingCommands commands = new();

        JsonElement result = await Invoke(w.Tools(commands, demoted), w.Context(), "ApplyDeletePictureLayers",
            ("layerIds", new[] { w.Logo.Id }), ("confirmationToken", Token(proposal)));

        ErrorOf(result).Should().NotBeNull();
        commands.Calls.Should().BeEmpty();
        w.Db.SaveCount.Should().Be(0);
        (await w.Db.EditImageLayers.CountAsync()).Should().Be(3);
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task Describe_lists_the_canvas_and_layers_back_to_front()
    {
        using World w = new();

        JsonElement result = await Invoke(w.Tools(), w.Context(), "DescribePictureDocument");

        result.GetProperty("canvas").GetProperty("width").GetInt32().Should().Be(1280);
        JsonElement[] layers = [.. result.GetProperty("layers").EnumerateArray()];
        layers.Select(l => l.GetProperty("name").GetString()).Should().Equal("cover.png", "Title", "Logo");
        layers[1].GetProperty("text").GetProperty("words").GetString().Should().Be("BIG SALE");
        layers[2].GetProperty("widthOnCanvas").GetDouble().Should().Be(200);
        w.Db.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task ListPictureTools_says_which_tools_have_no_provider()
    {
        using World w = new();

        JsonElement result = await Invoke(w.Tools(), w.Context(), "ListPictureTools");

        JsonElement[] tools = [.. result.GetProperty("tools").EnumerateArray()];
        tools.Select(t => t.GetProperty("tool").GetString()).Should().Contain(["erase", "cutOut", "designGraphic"]);
        tools.Single(t => t.GetProperty("tool").GetString() == "erase").GetProperty("available").GetBoolean().Should().BeFalse();
    }

    // ------------------------------------------------------------------ text layers through the editor's own route

    [Fact]
    public async Task A_text_layer_is_added_only_with_the_token_of_the_same_proposal()
    {
        using World w = new();
        AssistantPictureTools tools = w.Tools();
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeAddTextLayer", ("text", "50% OFF"), ("fontSize", 120.0), ("color", "#ff0000"));
        w.Db.SaveCount.Should().Be(0, "a proposal writes nothing");

        // A different text with the same token is refused.
        JsonElement tampered = await Invoke(tools, w.Context(), "ApplyAddTextLayer",
            ("text", "90% OFF"), ("fontSize", 120.0), ("color", "#ff0000"), ("confirmationToken", Token(proposal)));
        ErrorOf(tampered).Should().Be(AssistantPictureTools.StaleTokenMessage);
        w.Db.SaveCount.Should().Be(0);

        JsonElement applied = await Invoke(tools, w.Context(), "ApplyAddTextLayer",
            ("text", "50% OFF"), ("fontSize", 120.0), ("color", "#ff0000"), ("confirmationToken", Token(proposal)));

        applied.GetProperty("applied").GetBoolean().Should().BeTrue(applied.GetRawText());
        EditImageLayer created = await w.Db.EditImageLayers.AsNoTracking().SingleAsync(l => l.Kind == EditImageLayerKind.Text && l.Id != w.Title.Id);
        created.ContentJson.Should().Contain("50% OFF");
        (await w.Db.EditImageDocuments.AsNoTracking().SingleAsync()).Revision.Should().Be(2, "the editor's route bumped the revision");
        w.Changes.Should().ContainSingle().Which.Should().Match<AssistantImageDocumentChange>(c =>
            c.Kind == "layersAdded" && c.LayerIds.Single() == created.Id && c.DocumentId == w.Document.Id);
    }

    [Fact]
    public async Task Another_users_token_never_applies()
    {
        using World w = new();
        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeAddTextLayer", ("text", "Hello"));
        Guid colleague = Guid.NewGuid();

        JsonElement result = await Invoke(w.Tools(), new AssistantToolContext(colleague, w.Project.Id, null, w.Document.Id),
            "ApplyAddTextLayer", ("text", "Hello"), ("confirmationToken", Token(proposal)));

        ErrorOf(result).Should().NotBeNull();
        w.Db.SaveCount.Should().Be(0);
    }

    // ------------------------------------------------------------------ layer edits and deletes bind the target's name

    [Fact]
    public async Task A_layer_edit_is_bound_to_the_layers_current_name_and_applied_through_the_editors_route()
    {
        using World w = new();
        AssistantPictureTools tools = w.Tools();
        PictureLayerChange[] changes = [new(w.Logo.Id, X: 900, Y: 500, Scale: 0.5, Visible: false)];
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeEditPictureLayers", ("changes", changes));

        // Renamed in the editor between the proposal and the apply: the token no longer matches.
        EditImageLayer logo = await w.Db.EditImageLayers.SingleAsync(l => l.Id == w.Logo.Id);
        logo.Label = "Brand mark";
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        w.Db.Reset();

        JsonElement stale = await Invoke(tools, w.Context(), "ApplyEditPictureLayers", ("changes", changes), ("confirmationToken", Token(proposal)));
        ErrorOf(stale).Should().Be(AssistantPictureTools.StaleTokenMessage);
        w.Db.SaveCount.Should().Be(0);

        JsonElement fresh = await Invoke(tools, w.Context(), "ProposeEditPictureLayers", ("changes", changes));
        JsonElement applied = await Invoke(tools, w.Context(), "ApplyEditPictureLayers", ("changes", changes), ("confirmationToken", Token(fresh)));

        applied.GetProperty("applied").GetBoolean().Should().BeTrue(applied.GetRawText());
        EditImageLayer moved = await w.Db.EditImageLayers.AsNoTracking().SingleAsync(l => l.Id == w.Logo.Id);
        moved.Visible.Should().BeFalse();
        EditImageDocumentSupport.ReadTransform(moved.TransformJson).Should().Be(new EditImageLayerTransformDto(900, 500, 0.5));
        w.Changes.Single().LayerIds.Should().Equal(w.Logo.Id);
    }

    [Fact]
    public async Task Text_changes_are_refused_on_a_picture_layer_and_validated_on_a_text_layer()
    {
        using World w = new();
        JsonElement onPicture = await Invoke(w.Tools(), w.Context(), "ProposeEditPictureLayers",
            ("changes", new[] { new PictureLayerChange(w.Logo.Id, Text: "Hi") }));
        ErrorOf(onPicture).Should().Contain("not a text layer");

        JsonElement tooBig = await Invoke(w.Tools(), w.Context(), "ProposeEditPictureLayers",
            ("changes", new[] { new PictureLayerChange(w.Title.Id, FontSize: 5000) }));
        ErrorOf(tooBig).Should().Contain("fontSize");
        w.Db.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Deleting_binds_ids_and_names_and_deletes_through_the_editors_route()
    {
        using World w = new();
        AssistantPictureTools tools = w.Tools();
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeDeletePictureLayers", ("layerIds", new[] { w.Logo.Id }));
        proposal.GetProperty("summary").GetString().Should().Contain("\"Logo\"");

        JsonElement wrongTarget = await Invoke(tools, w.Context(), "ApplyDeletePictureLayers",
            ("layerIds", new[] { w.Title.Id }), ("confirmationToken", Token(proposal)));
        ErrorOf(wrongTarget).Should().Be(AssistantPictureTools.StaleTokenMessage);
        w.Db.SaveCount.Should().Be(0);

        JsonElement applied = await Invoke(tools, w.Context(), "ApplyDeletePictureLayers",
            ("layerIds", new[] { w.Logo.Id }), ("confirmationToken", Token(proposal)));

        applied.GetProperty("applied").GetBoolean().Should().BeTrue(applied.GetRawText());
        (await w.Db.EditImageLayers.CountAsync()).Should().Be(2, "the query filter hides the soft-deleted layer");
        w.Changes.Single().Kind.Should().Be("layersDeleted");
    }

    // ------------------------------------------------------------------ canvas

    [Fact]
    public async Task A_shape_change_resolves_to_exact_pixels_and_is_applied()
    {
        using World w = new();
        AssistantPictureTools tools = w.Tools();
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeChangePictureCanvas", ("aspect", "9:16"), ("backgroundColor", "#101010"));
        proposal.GetProperty("summary").GetString().Should().Contain("720x1280");

        JsonElement applied = await Invoke(tools, w.Context(), "ApplyChangePictureCanvas",
            ("aspect", "9:16"), ("backgroundColor", "#101010"), ("confirmationToken", Token(proposal)));

        applied.GetProperty("applied").GetBoolean().Should().BeTrue(applied.GetRawText());
        EditImageDocument doc = await w.Db.EditImageDocuments.AsNoTracking().SingleAsync();
        (doc.CanvasWidth, doc.CanvasHeight, doc.BackgroundColor).Should().Be((720, 1280, "#101010"));
    }

    // ------------------------------------------------------------------ paid AI tools

    private static ResolvedImageProvider FalErase() =>
        new(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.ImageErase, "https://queue.fal.run", "", "k", 60);

    private static ResolvedImageProvider FalEdit() =>
        new(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit, "https://queue.fal.run", "", "k", 60);

    // ------------------------------------------------------------------ T-030: unpriced Platform models

    private static ModelUnpricedException Unpriced(InferenceProviderCapability capability) => new(capability, "Pricey " + capability);

    private static string CodeOf(JsonElement result) =>
        result.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    [Fact]
    public async Task ListPictureTools_lists_a_tool_whose_model_is_unpriced_as_unavailable_and_says_why()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageErase, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Unpriced(InferenceProviderCapability.ImageErase));
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase() with { Capability = InferenceProviderCapability.ImageUpscale });

        JsonElement result = await Invoke(w.Tools(), w.Context(), "ListPictureTools");

        JsonElement[] tools = [.. result.GetProperty("tools").EnumerateArray()];
        JsonElement erase = tools.Single(t => t.GetProperty("tool").GetString() == "erase");
        erase.GetProperty("available").GetBoolean().Should().BeFalse();
        erase.GetProperty("unavailableBecause").GetString().Should().Contain("has no price set up yet");
        tools.Single(t => t.GetProperty("tool").GetString() == "upscale").GetProperty("available").GetBoolean()
            .Should().BeTrue("one unpriced model never hides the others");
    }

    [Fact]
    public async Task ListPictureTools_lists_removeBackground_unavailable_when_its_birefnet_default_would_be_refused_unpriced()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveSegmentationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedImageAnalysisProvider(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi,
                InferenceProviderCapability.Segmentation, "https://queue.fal.run", "fal-ai/sam2/auto-segment", "k", 60));

        IWorkflowCostEstimator billingOn = Mock.Of<IWorkflowCostEstimator>(e => e.BillingEnabled == true);
        JsonElement refused = await Invoke(w.Tools(costEstimator: billingOn), w.Context(), "ListPictureTools");
        JsonElement removeWhenOn = refused.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("tool").GetString() == "removeBackground");
        removeWhenOn.GetProperty("available").GetBoolean().Should().BeFalse();
        removeWhenOn.GetProperty("unavailableBecause").GetString().Should().Contain("fal-ai/birefnet/v2").And.Contain("has no price set up yet");

        IWorkflowCostEstimator billingOff = Mock.Of<IWorkflowCostEstimator>(e => e.BillingEnabled == false);
        JsonElement listed = await Invoke(w.Tools(costEstimator: billingOff), w.Context(), "ListPictureTools");
        listed.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("tool").GetString() == "removeBackground")
            .GetProperty("available").GetBoolean().Should().BeTrue("billing off refuses nothing");
    }

    [Fact]
    public async Task A_paid_tool_whose_model_is_unpriced_is_refused_in_plain_words_with_its_code()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageErase, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Unpriced(InferenceProviderCapability.ImageErase));

        JsonElement refused = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "erase"), ("layerId", w.Source.Id), ("area", new PictureArea(0.1, 0.1, 0.3, 0.4)));

        ErrorOf(refused).Should().Contain("has no price set up yet");
        CodeOf(refused).Should().Be(ModelUnpricedException.FailureCode);
        refused.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_paid_tool_whose_model_became_unpriced_after_the_proposal_is_refused_by_the_editor_with_its_code()
    {
        using World w = new();
        w.Resolver.SetupSequence(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase() with { Capability = InferenceProviderCapability.ImageUpscale })
            .ThrowsAsync(Unpriced(InferenceProviderCapability.ImageUpscale));
        AssistantPictureTools tools = w.Tools();
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeRunPictureTool", ("tool", "upscale"), ("layerId", w.Source.Id));

        JsonElement applied = await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "upscale"), ("layerId", w.Source.Id), ("confirmationToken", Token(proposal)));

        ErrorOf(applied).Should().Contain("has no price set up yet");
        CodeOf(applied).Should().Be(ModelUnpricedException.FailureCode);
    }

    [Fact]
    public async Task Upscale_factor_above_2_is_refused_because_the_editor_caps_the_factor_at_x2()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase() with { Capability = InferenceProviderCapability.ImageUpscale });

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "upscale"), ("layerId", w.Source.Id), ("factor", 4));

        ErrorOf(proposal).Should().Be("factor must be 2");
    }

    [Theory]
    [InlineData(5041, 2160)]
    [InlineData(6000, 4000)]
    public async Task Upscale_of_a_picture_larger_than_the_priced_preset_is_refused_on_propose_and_apply(int width, int height)
    {
        using World w = new();
        await SetSourceSizeAsync(w, width, height);
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase() with { Capability = InferenceProviderCapability.ImageUpscale });
        const string refusal = "This picture is too large to upscale. The largest picture that can be upscaled is 5040 × 2160.";

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "upscale"), ("layerId", w.Source.Id), ("factor", 2));
        ErrorOf(proposal).Should().Be(refusal);
        w.Resolver.Verify(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()), Times.Never,
            "the refusal comes before a provider is chosen or priced");

        JsonElement applied = await Invoke(w.Tools(), w.Context(), "ApplyRunPictureTool",
            ("tool", "upscale"), ("layerId", w.Source.Id), ("factor", 2), ("confirmationToken", "x"));
        ErrorOf(applied).Should().Be(refusal);
        w.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Upscale_of_a_picture_at_the_priced_preset_of_5040_by_2160_is_not_refused_for_size()
    {
        using World w = new();
        await SetSourceSizeAsync(w, 5040, 2160);
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase() with { Capability = InferenceProviderCapability.ImageUpscale });

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "upscale"), ("layerId", w.Source.Id), ("factor", 2));

        proposal.TryGetProperty("confirmationToken", out _).Should().BeTrue("a picture at the preset is proposed as before");
        w.Resolver.Verify(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private const string UnreadableUpscaleMessage = "This picture's size could not be read, so it cannot be upscaled.";

    private const string TooLargeUpscaleMessage = "This picture is too large to upscale. The largest picture that can be upscaled is 5040 × 2160.";

    /// <summary>Asks for the upscale both ways (propose and apply) and checks each is refused with <paramref name="expected"/> before any provider or charge.</summary>
    private static async Task AssertUpscaleRefusedAsync(World w, string expected)
    {
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase() with { Capability = InferenceProviderCapability.ImageUpscale });

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "upscale"), ("layerId", w.Source.Id), ("factor", 2));
        ErrorOf(proposal).Should().Be(expected);

        JsonElement applied = await Invoke(w.Tools(), w.Context(), "ApplyRunPictureTool",
            ("tool", "upscale"), ("layerId", w.Source.Id), ("factor", 2), ("confirmationToken", "x"));
        ErrorOf(applied).Should().Be(expected);

        w.Resolver.Verify(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()), Times.Never,
            "the refusal comes before a provider is chosen or priced");
        w.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Upscale_of_a_picture_whose_size_cannot_be_established_is_refused_on_propose_and_apply()
    {
        using World w = new();
        await SetSourceSizeAsync(w, null, null);
        w.Storage.Objects[w.Source.StorageKey] = [1, 2, 3];

        await AssertUpscaleRefusedAsync(w, UnreadableUpscaleMessage);
    }

    [Theory]
    [InlineData(5041, 2160)]
    [InlineData(6000, 4000)]
    public async Task Upscale_of_an_unrecorded_picture_whose_header_is_larger_than_the_bound_is_refused(int width, int height)
    {
        using World w = new();
        await SetSourceSizeAsync(w, null, null);
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(width, height);

        await AssertUpscaleRefusedAsync(w, TooLargeUpscaleMessage);
    }

    [Fact]
    public async Task Upscale_with_a_lying_recorded_size_is_refused_when_the_header_declares_more()
    {
        using World w = new();
        await SetSourceSizeAsync(w, 40, 20);
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(6000, 4000);

        await AssertUpscaleRefusedAsync(w, TooLargeUpscaleMessage);
    }

    [Fact]
    public async Task Upscale_with_a_large_recorded_size_is_refused_even_when_the_header_is_small()
    {
        using World w = new();
        await SetSourceSizeAsync(w, 6000, 4000);
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(40, 20);

        await AssertUpscaleRefusedAsync(w, TooLargeUpscaleMessage);
    }

    [Fact]
    public async Task Upscale_with_recorded_and_header_sizes_both_within_the_bound_is_not_refused_for_size()
    {
        using World w = new();
        await SetSourceSizeAsync(w, 40, 20);
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(40, 20);
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase() with { Capability = InferenceProviderCapability.ImageUpscale });

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "upscale"), ("layerId", w.Source.Id), ("factor", 2));

        proposal.TryGetProperty("confirmationToken", out _).Should().BeTrue("a known size within the bound is proposed as before");
        w.Resolver.Verify(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 720)]
    [InlineData(1280, 0)]
    public async Task Upscale_with_a_recorded_size_of_zero_or_negative_dimensions_is_refused(int width, int height)
    {
        using World w = new();
        await SetSourceSizeAsync(w, width, height);
        w.Storage.Objects[w.Source.StorageKey] = PngHeader(40, 20);

        await AssertUpscaleRefusedAsync(w, UnreadableUpscaleMessage);
    }

    [Fact]
    public async Task ListPictureTools_upscale_states_the_x2_cap_and_no_factor_4()
    {
        using World w = new();

        JsonElement result = await Invoke(w.Tools(), w.Context(), "ListPictureTools");

        string needs = result.GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("tool").GetString() == "upscale").GetProperty("needs").GetString()!;
        needs.Should().Contain("factor 2").And.NotContain("or 4");
    }

    [Fact]
    public async Task LookAtPicture_with_an_unpriced_vision_model_is_refused_in_plain_words_with_its_code()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Unpriced(InferenceProviderCapability.Vision));
        w.Storage.Objects[w.Source.StorageKey] = [1, 2, 3];

        JsonElement result = await Invoke(w.Tools(chatClients: new RecordingVision("unused")), w.Context(), "LookAtPicture");

        ErrorOf(result).Should().Contain("has no price set up yet");
        CodeOf(result).Should().Be(ModelUnpricedException.FailureCode);
    }

    [Fact]
    public async Task The_edit_tool_carries_a_reference_picture_and_every_other_tool_refuses_one()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalEdit());
        RecordingCommands commands = new();
        AssistantPictureTools tools = w.Tools(commands);
        Guid reference = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        // A tool that cannot use a second picture refuses it BY NAME before proposing anything: running
        // the edit without it would spend the user's credits on something they did not ask for.
        JsonElement refused = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "fill"), ("layerId", w.Source.Id), ("area", new PictureArea(0.1, 0.1, 0.3, 0.4)),
            ("prompt", "a hat"), ("referenceProjectFileIds", new[] { reference, second }));
        ErrorOf(refused).Should().Contain("cannot use a reference picture");
        commands.Calls.Should().BeEmpty();

        // The edit tool takes it, and it reaches the editor's own operation unchanged.
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "instruct"), ("layerId", w.Source.Id), ("prompt", "put the second man's face on the first"),
            ("referenceProjectFileIds", new[] { reference, second }));
        JsonElement applied = await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "instruct"), ("layerId", w.Source.Id), ("prompt", "put the second man's face on the first"),
            ("referenceProjectFileIds", new[] { reference, second }), ("confirmationToken", Token(proposal)));

        applied.GetProperty("applied").GetBoolean().Should().BeTrue(applied.GetRawText());
        EditImageOperationRequest request = commands.Calls.OfType<EditImageOperationRequest>().Single();
        request.Operation.Should().Be("instruct");
        // The order the user gave is the order the model reads.
        request.ReferenceProjectFileIds.Should().Equal([reference, second]);
    }

    [Fact]
    public async Task A_paid_tool_is_not_proposed_without_a_provider_and_shows_its_cost_with_one()
    {
        using World w = new();
        JsonElement none = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "erase"), ("layerId", w.Source.Id), ("area", new PictureArea(0.1, 0.1, 0.3, 0.4)));
        ErrorOf(none).Should().Contain("No AI service is set up");
        none.TryGetProperty("confirmationToken", out _).Should().BeFalse();

        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageErase, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase());
        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "erase"), ("layerId", w.Source.Id), ("area", new PictureArea(0.1, 0.1, 0.3, 0.4)));

        proposal.GetProperty("paid").GetBoolean().Should().BeTrue();
        proposal.GetProperty("estimatedCost").GetProperty("note").GetString().Should().NotBeNullOrWhiteSpace();
        proposal.GetProperty("summary").GetString().Should().Contain("erase").And.Contain("cover.png");
    }

    [Fact]
    public async Task Erasing_an_area_draws_a_mask_at_the_layers_size_and_runs_the_editors_operation()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageErase, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase());
        RecordingCommands commands = new();
        AssistantPictureTools tools = w.Tools(commands);
        PictureArea area = new(0.5, 0.25, 0.75, 0.5);
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeRunPictureTool", ("tool", "erase"), ("layerId", w.Source.Id), ("area", area));

        JsonElement applied = await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "erase"), ("layerId", w.Source.Id), ("area", area), ("confirmationToken", Token(proposal)));

        applied.GetProperty("applied").GetBoolean().Should().BeTrue(applied.GetRawText());
        EditImageOperationRequest request = commands.Calls.OfType<EditImageOperationRequest>().Single();
        request.Operation.Should().Be("erase");
        request.LayerId.Should().Be(w.Source.Id);
        EditImageDocumentSupport.IsIssuedEditorKey(request.MaskKey, w.Project.Id, w.Document.Id, "mask").Should().BeTrue();
        byte[] mask = commands.Masks[request.MaskKey!];
        PngAlpha.ReadSize(mask).Should().Be((1280, 720), "the mask is drawn at the layer's own pixel size");
        AlphaPlane plane = PngAlpha.DecodeMask(mask)!;
        plane.Values[(270 * 1280) + 700].Should().Be(255, "inside the area is white (act on it)");
        plane.Values[(100 * 1280) + 100].Should().Be(0, "outside is black (keep)");
    }

    [Fact]
    public async Task A_changed_area_invalidates_the_token_and_nothing_runs()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageErase, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FalErase());
        RecordingCommands commands = new();
        AssistantPictureTools tools = w.Tools(commands);
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "erase"), ("layerId", w.Source.Id), ("area", new PictureArea(0.1, 0.1, 0.2, 0.2)));

        JsonElement result = await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "erase"), ("layerId", w.Source.Id), ("area", new PictureArea(0.1, 0.1, 0.9, 0.9)), ("confirmationToken", Token(proposal)));

        ErrorOf(result).Should().Be(AssistantPictureTools.StaleTokenMessage);
        commands.Calls.Should().BeEmpty();
        commands.Masks.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ Design a graphic

    [Fact]
    public async Task Designing_a_graphic_queues_an_assistant_run_with_the_proposed_request_only()
    {
        using World w = new();
        w.Db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "GraphicDesigner", AgentType = AgentType.GraphicDesigner, IsBuiltIn = true });
        w.Db.SaveChanges();
        w.Db.Reset();
        AssistantPictureTools tools = w.Tools();

        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeDesignGraphic", ("description", "A yellow SALE badge"), ("placement", "topRight"));
        proposal.GetProperty("paid").GetBoolean().Should().BeTrue(proposal.GetRawText());
        proposal.GetProperty("estimatedCost").ValueKind.Should().Be(JsonValueKind.Object);
        w.Db.SaveCount.Should().Be(0, "a proposal queues nothing");

        JsonElement moved = await Invoke(tools, w.Context(), "ApplyDesignGraphic",
            ("description", "A yellow SALE badge"), ("placement", "bottomLeft"), ("confirmationToken", Token(proposal)));
        ErrorOf(moved).Should().Be(AssistantPictureTools.StaleTokenMessage);
        (await w.Db.EditImageGraphicRequests.CountAsync()).Should().Be(0);

        JsonElement applied = await Invoke(tools, w.Context(), "ApplyDesignGraphic",
            ("description", "A yellow SALE badge"), ("placement", "topRight"), ("confirmationToken", Token(proposal)));

        applied.GetProperty("applied").GetBoolean().Should().BeTrue(applied.GetRawText());
        EditImageGraphicRequest row = await w.Db.EditImageGraphicRequests.AsNoTracking().SingleAsync();
        row.Description.Should().Be("A yellow SALE badge");
        row.Placement.Should().Be("topRight");
        WorkflowExecution run = await w.Db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == row.ExecutionId);
        run.Initiator.Should().Be(ExecutionInitiator.Assistant);
        w.Changes.Single().Should().Match<AssistantImageDocumentChange>(c => c.Kind == "graphicQueued" && c.GraphicId == row.Id);
    }

    [Fact]
    public async Task An_unverified_email_cannot_start_a_paid_graphic()
    {
        using World w = new(verified: false);
        w.Db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "GraphicDesigner", AgentType = AgentType.GraphicDesigner, IsBuiltIn = true });
        w.Db.SaveChanges();
        w.Db.Reset();
        AssistantPictureTools tools = w.Tools();
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeDesignGraphic", ("description", "Badge"));

        JsonElement result = await Invoke(tools, w.Context(), "ApplyDesignGraphic", ("description", "Badge"), ("confirmationToken", Token(proposal)));

        result.GetProperty("code").GetString().Should().Be("EMAIL_NOT_VERIFIED");
        w.Db.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task The_tool_provider_forwards_each_applied_change_to_the_transport_the_moment_it_is_made()
    {
        using World w = new();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(w.Db, w.User, w.Storage, confirmationTokens: w.Tokens);
        List<AssistantImageDocumentChange> heard = [];
        provider.OnImageDocumentChanged = (change, _) => { heard.Add(change); return Task.CompletedTask; };
        IReadOnlyList<AIFunction> tools = provider.GetTools(w.Context());

        async Task<JsonElement> Call(string name, params (string Key, object? Value)[] args)
        {
            AIFunctionArguments arguments = new();
            foreach ((string key, object? value) in args)
                arguments[key] = value;
            object? raw = await tools.Single(t => t.Name == name).InvokeAsync(arguments);
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        JsonElement proposal = await Call("ProposeAddTextLayer", ("text", "NEW"));
        heard.Should().BeEmpty("a proposal changes nothing, so nothing is announced");

        await Call("ApplyAddTextLayer", ("text", "NEW"), ("confirmationToken", Token(proposal)));

        heard.Should().ContainSingle().Which.Kind.Should().Be("layersAdded");
        heard[0].DocumentId.Should().Be(w.Document.Id);
        heard[0].ProjectId.Should().Be(w.Project.Id);
    }

    /// <summary>A plausible argument set per tool, so the authority sweeps reach each tool's refusal, not its argument binding.</summary>
    private static (string Key, object? Value)[] ArgumentsFor(string tool, World w) => tool switch
    {
        "ProposeAddTextLayer" => [("text", "Hi")],
        "ApplyAddTextLayer" => [("text", "Hi"), ("confirmationToken", "x")],
        "ProposeEditPictureLayers" => [("changes", new[] { new PictureLayerChange(w.Logo.Id, X: 1) })],
        "ApplyEditPictureLayers" => [("changes", new[] { new PictureLayerChange(w.Logo.Id, X: 1) }), ("confirmationToken", "x")],
        "ProposeDeletePictureLayers" => [("layerIds", new[] { w.Logo.Id })],
        "ApplyDeletePictureLayers" => [("layerIds", new[] { w.Logo.Id }), ("confirmationToken", "x")],
        "ProposeChangePictureCanvas" => [("width", 800), ("height", 600)],
        "ApplyChangePictureCanvas" => [("width", 800), ("height", 600), ("confirmationToken", "x")],
        "ProposeRunPictureTool" => [("tool", "upscale"), ("layerId", w.Source.Id)],
        "ApplyRunPictureTool" => [("tool", "upscale"), ("layerId", w.Source.Id), ("confirmationToken", "x")],
        "ProposeDesignGraphic" => [("description", "Badge")],
        "ApplyDesignGraphic" => [("description", "Badge"), ("confirmationToken", "x")],
        _ => []
    };
}
