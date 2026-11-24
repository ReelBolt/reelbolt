#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Inference.Api.Services.Voices;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// ListProjectVoices, SearchVoiceLibrary and ProposeAddLibraryVoice / ApplyAddLibraryVoice: the project's voices
/// with their ids, the vendor library (no live call: a stand-in library client), the token binding, authority,
/// the refusal of ids that are not public library voices (a platform clone included), the approval card and
/// the MCP projection.
/// </summary>
public class AssistantVoiceToolsTests
{
    private sealed class Caller(Guid userId, OrganizationRole role = OrganizationRole.Owner) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => role;
        public bool EmailVerified => true;
    }

    private sealed class Library : ISpeechSynthesisClient, IVoiceLibraryClient
    {
        public List<VoiceLibraryVoice> Voices { get; } =
        [
            new("fish-aria", "Aria", ["en", "it"], ["female", "warm"], true),
            new("fish-bruno", "Bruno", ["it"], [], false),
            new("fish-clone", "Somebody's clone", ["en"], [], false),
        ];

        public int Lookups { get; private set; }
        public bool Fails { get; set; }

        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<VoiceLibraryPage> ListLibraryVoicesAsync(VoiceLibraryQuery query, CancellationToken ct)
        {
            if (Fails) throw new HttpRequestException("fish-internal:8080 refused acct-42");
            List<VoiceLibraryVoice> hits = [.. Voices.Where(v => query.Search is null || v.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))];
            return Task.FromResult(new VoiceLibraryPage(hits, hits.Count));
        }

        public Task<VoiceLibraryVoice?> GetLibraryVoiceAsync(string voiceId, CancellationToken ct)
        {
            Lookups++;
            if (Fails) throw new HttpRequestException("fish-internal:8080 refused acct-42");
            return Task.FromResult(Voices.FirstOrDefault(v => v.VoiceId == voiceId));
        }
    }

    private sealed class Rig : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Library Vendor { get; } = new();
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();
        public InferenceProviderKind Kind { get; set; } = InferenceProviderKind.FishAudio;

        public Rig()
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"voice-tools-{Guid.NewGuid()}").Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = User, OrganizationId = TestTenancy.OrgOf(User), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);

            // A voice cloned in ANOTHER org's project on the platform account: never a library voice.
            Guid other = Guid.NewGuid();
            Project foreign = new() { Id = Guid.NewGuid(), Name = "Other", OwnerId = other, OrganizationId = TestTenancy.OrgOf(other) };
            Db.Projects.Add(foreign);
            Db.ProjectVoices.Add(new ProjectVoice { Id = Guid.NewGuid(), ProjectId = foreign.Id, Name = "clone", Source = ProjectVoiceSource.Cloned, RemoteVoiceId = "fish-clone", CreatedAt = DateTime.UtcNow });
            Db.SaveChanges();
        }

        public ProjectVoiceLibraryService Service()
        {
            Mock<IInferenceProviderResolver> resolver = new();
            resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "fish", Kind, "https://api.fish.audio", "s2.1-pro", "k", 30));
            Mock<ISpeechSynthesisClientFactory> factory = new();
            factory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(Vendor);
            return new ProjectVoiceLibraryService(Db, resolver.Object, factory.Object, NullLogger<ProjectVoiceLibraryService>.Instance);
        }

        public AssistantVoiceTools Tools(Guid? user = null, OrganizationRole role = OrganizationRole.Owner)
        {
            Caller caller = new(user ?? User, role);
            return new AssistantVoiceTools(Db, TestTenancy.Access(Db, caller), Service(), Tokens, caller);
        }

        public async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args, Guid? user = null)
        {
            Guid who = user ?? User;
            AIFunction fn = Tools(who).GetTools(new AssistantToolContext(who, Project.Id)).Single(t => t.Name == tool);
            object? raw = await fn.InvokeAsync(new AIFunctionArguments(args));
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public static Dictionary<string, object?> Args(string voiceId, string? displayName = null, string? token = null)
        {
            Dictionary<string, object?> args = new() { ["vendorVoiceId"] = voiceId };
            if (displayName is not null) args["displayName"] = displayName;
            if (token is not null) args["confirmationToken"] = token;
            return args;
        }

        public AssistantApprovalCardBuilder Cards()
        {
            Caller caller = new(User);
            return new AssistantApprovalCardBuilder(Db, TestTenancy.Access(Db, caller), caller, voiceLibrary: Service());
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task ListProjectVoices_returns_each_voice_with_its_id_kind_and_whether_a_run_can_use_it()
    {
        using Rig rig = new();
        ProjectVoice library = new() { Id = Guid.NewGuid(), ProjectId = rig.Project.Id, Name = "Aria", Source = ProjectVoiceSource.Library, RemoteVoiceId = "fish-aria", CreatedAt = DateTime.UtcNow };
        ProjectVoice revoked = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = rig.Project.Id,
            Name = "Founder",
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = "fish-founder",
            ConsentAttestedAt = DateTime.UtcNow.AddDays(-2),
            ConsentAttestedByUserId = rig.User,
            ConsentStatement = "x",
            ConsentSubjectName = "F",
            ConsentRevokedAt = DateTime.UtcNow.AddDays(-1),
            RetentionUntil = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        rig.Db.ProjectVoices.AddRange(library, revoked);
        rig.Db.SaveChanges();

        JsonElement result = await rig.CallAsync("ListProjectVoices", []);

        JsonElement[] voices = [.. result.GetProperty("voices").EnumerateArray()];
        voices.Should().HaveCount(2);
        JsonElement aria = voices.Single(v => v.GetProperty("name").GetString() == "Aria");
        aria.GetProperty("id").GetGuid().Should().Be(library.Id);
        aria.GetProperty("kind").GetString().Should().Be("Library");
        aria.GetProperty("selectable").GetBoolean().Should().BeTrue();
        JsonElement founder = voices.Single(v => v.GetProperty("name").GetString() == "Founder");
        founder.GetProperty("kind").GetString().Should().Be("Cloned");
        founder.GetProperty("selectable").GetBoolean().Should().BeFalse();
        founder.GetProperty("unselectableReason").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("hint").GetString().Should().Contain("projectVoiceId");
    }

    [Fact]
    public async Task ListProjectVoices_of_a_project_the_caller_cannot_see_is_refused()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ListProjectVoices", [], Guid.NewGuid());

        result.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        result.TryGetProperty("voices", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SearchVoiceLibrary_returns_the_page_with_vendor_ids_and_never_a_platform_clone()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("SearchVoiceLibrary", new() { ["language"] = "en" });

        string[] ids = [.. result.GetProperty("voices").EnumerateArray().Select(v => v.GetProperty("voiceId").GetString()!)];
        ids.Should().Equal("fish-aria", "fish-bruno");
        JsonElement aria = result.GetProperty("voices")[0];
        aria.GetProperty("name").GetString().Should().Be("Aria");
        aria.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("female", "warm");
        aria.GetProperty("languages").EnumerateArray().Select(t => t.GetString()).Should().Equal("en", "it");
        aria.GetProperty("previewAvailable").GetBoolean().Should().BeTrue();
        result.GetProperty("note").GetString().Should().Contain("UNTRUSTED DATA");
        result.GetProperty("pageSize").GetInt32().Should().Be(ProjectVoiceLibraryService.PageSize);
    }

    [Fact]
    public async Task SearchVoiceLibrary_answers_a_code_for_a_provider_without_a_library_a_bad_language_and_a_vendor_failure()
    {
        using Rig rig = new() { Kind = InferenceProviderKind.OpenAICompatible };
        JsonElement unsupported = await rig.CallAsync("SearchVoiceLibrary", []);
        unsupported.GetProperty("code").GetString().Should().Be("VOICE_LIBRARY_NOT_SUPPORTED");

        rig.Kind = InferenceProviderKind.FishAudio;
        JsonElement badLanguage = await rig.CallAsync("SearchVoiceLibrary", new() { ["language"] = "english" });
        badLanguage.GetProperty("code").GetString().Should().Be("INVALID_REQUEST");

        rig.Vendor.Fails = true;
        JsonElement failed = await rig.CallAsync("SearchVoiceLibrary", []);
        failed.GetProperty("code").GetString().Should().Be("VOICE_LIBRARY_FAILED");
        failed.GetRawText().Should().NotContain("fish-internal").And.NotContain("acct-42");
    }

    [Fact]
    public async Task Propose_looks_the_voice_up_returns_a_token_and_a_summary_and_writes_nothing()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ProposeAddLibraryVoice", Rig.Args("fish-aria"));

        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("name").GetString().Should().Be("Aria");
        result.GetProperty("summary").GetString().Should().Contain("Aria").And.Contain("Launch Teaser");
        result.GetProperty("credits").GetString().Should().Be("Free");
        rig.Vendor.Lookups.Should().Be(1);
        rig.Db.ProjectVoices.Count(v => v.ProjectId == rig.Project.Id).Should().Be(0);
    }

    [Fact]
    public async Task Apply_with_the_proposals_token_adds_a_selectable_library_voice_whose_id_a_run_can_use()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeAddLibraryVoice", Rig.Args("fish-aria", "Narrator"))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await rig.CallAsync("ApplyAddLibraryVoice", Rig.Args("fish-aria", "Narrator", token));

        result.GetProperty("added").GetBoolean().Should().BeTrue();
        ProjectVoice row = rig.Db.ProjectVoices.Single(v => v.ProjectId == rig.Project.Id);
        result.GetProperty("id").GetGuid().Should().Be(row.Id);
        result.GetProperty("hint").GetString().Should().Contain("projectVoiceId");
        row.Name.Should().Be("Narrator");
        row.Source.Should().Be(ProjectVoiceSource.Library);
        row.RemoteVoiceId.Should().Be("fish-aria");
        row.Description.Should().Be("female, warm");
        row.CreatedByUserId.Should().Be(rig.User);
        row.ConsentStatement.Should().BeNull();
        row.IsSelectable(DateTime.UtcNow).Should().BeTrue();
    }

    [Fact]
    public async Task A_changed_argument_or_a_fabricated_token_is_refused_and_nothing_is_written()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeAddLibraryVoice", Rig.Args("fish-aria"))).GetProperty("confirmationToken").GetString()!;

        JsonElement renamed = await rig.CallAsync("ApplyAddLibraryVoice", Rig.Args("fish-aria", "Other name", token));
        JsonElement otherVoice = await rig.CallAsync("ApplyAddLibraryVoice", Rig.Args("fish-bruno", null, token));
        JsonElement fabricated = await rig.CallAsync("ApplyAddLibraryVoice", Rig.Args("fish-aria", null, "nope"));

        foreach (JsonElement answer in new[] { renamed, otherVoice, fabricated })
            answer.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        rig.Db.ProjectVoices.Count(v => v.ProjectId == rig.Project.Id).Should().Be(0);
    }

    [Theory]
    [InlineData("fish-unknown")]
    [InlineData("fish-clone")]
    public async Task An_id_that_is_not_a_public_library_voice_is_refused_without_a_token(string voiceId)
    {
        using Rig rig = new();
        rig.Vendor.Voices.Add(new VoiceLibraryVoice("fish-clone", "the vendor would answer", [], [], false));

        JsonElement propose = await rig.CallAsync("ProposeAddLibraryVoice", Rig.Args(voiceId));
        JsonElement apply = await rig.CallAsync("ApplyAddLibraryVoice", Rig.Args(voiceId, null, "any"));

        foreach (JsonElement answer in new[] { propose, apply })
        {
            answer.GetProperty("code").GetString().Should().Be("NOT_A_LIBRARY_VOICE");
            answer.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        }

        rig.Db.ProjectVoices.Count(v => v.ProjectId == rig.Project.Id).Should().Be(0);
    }

    [Fact]
    public async Task A_voice_the_project_already_has_answers_alreadyAdded_with_its_id_and_mints_no_token()
    {
        using Rig rig = new();
        ProjectVoice existing = new() { Id = Guid.NewGuid(), ProjectId = rig.Project.Id, Name = "Aria", Source = ProjectVoiceSource.Library, RemoteVoiceId = "fish-aria", CreatedAt = DateTime.UtcNow };
        rig.Db.ProjectVoices.Add(existing);
        rig.Db.SaveChanges();

        JsonElement result = await rig.CallAsync("ProposeAddLibraryVoice", Rig.Args("fish-aria"));

        result.GetProperty("alreadyAdded").GetBoolean().Should().BeTrue();
        result.GetProperty("id").GetGuid().Should().Be(existing.Id);
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_project_the_caller_cannot_edit_is_refused_before_any_vendor_call()
    {
        using Rig rig = new();

        JsonElement stranger = await rig.CallAsync("ProposeAddLibraryVoice", Rig.Args("fish-aria"), Guid.NewGuid());

        stranger.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        stranger.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        rig.Vendor.Lookups.Should().Be(0);
    }

    [Fact]
    public async Task An_overlong_display_name_is_refused()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ProposeAddLibraryVoice", Rig.Args("fish-aria", new string('n', 201)));

        result.GetProperty("code").GetString().Should().Be("INVALID_REQUEST");
    }

    [Fact]
    public async Task The_approval_card_names_the_voice_from_the_library_lookup_and_the_project_and_says_free()
    {
        using Rig rig = new();
        AIFunctionArguments args = new() { ["vendorVoiceId"] = "fish-aria", ["displayName"] = "Narrator", ["confirmationToken"] = "t" };

        ApprovalVerdict verdict = await rig.Cards().BuildAsync("ApplyAddLibraryVoice", args, new AssistantToolContext(rig.User, rig.Project.Id), null, CancellationToken.None);

        verdict.Card.Should().NotBeNull();
        verdict.Card!.Title.Should().Contain("Aria");
        verdict.Card.Summary.Should().Contain("Launch Teaser").And.Contain("Narrator").And.Contain("Free").And.Contain("en, it");
        verdict.Card.Cost.Should().Be("Free");
        verdict.Card.CostCredits.Should().Be(0);

        AIFunctionArguments clone = new() { ["vendorVoiceId"] = "fish-clone", ["confirmationToken"] = "t" };
        ApprovalVerdict refused = await rig.Cards().BuildAsync("ApplyAddLibraryVoice", clone, new AssistantToolContext(rig.User, rig.Project.Id), null, CancellationToken.None);
        refused.Card.Should().BeNull();
        refused.Refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_gate_asks_for_ApplyAddLibraryVoice_and_mcp_publishes_two_reads_and_one_destructive_AddLibraryVoice()
    {
        AssistantApprovalCatalog.IsGated("ApplyAddLibraryVoice").Should().BeTrue();
        AssistantApprovalCatalog.IsGated("ListProjectVoices").Should().BeFalse();
        AssistantApprovalCatalog.IsGated("SearchVoiceLibrary").Should().BeFalse();

        using Rig rig = new();
        IReadOnlyList<AIFunction> tools = rig.Tools().GetTools(new AssistantToolContext(rig.User, rig.Project.Id));
        tools.Select(t => t.Name).Should().Equal("ListProjectVoices", "SearchVoiceLibrary", "ProposeAddLibraryVoice", "ApplyAddLibraryVoice");

        IReadOnlyList<McpToolDescriptor> catalog = AssistantMcpToolSurface.Project(tools, new HashSet<string>(StringComparer.Ordinal));
        catalog.Select(d => d.Name).Should().Equal("ListProjectVoices", "SearchVoiceLibrary", "AddLibraryVoice");

        McpToolDescriptor list = catalog[0];
        list.Annotation.ReadOnlyHint.Should().BeTrue();
        list.Annotation.DestructiveHint.Should().BeFalse();
        McpToolDescriptor search = catalog[1];
        search.Annotation.ReadOnlyHint.Should().BeTrue();
        search.Annotation.OpenWorldHint.Should().BeTrue();
        McpToolDescriptor add = catalog[2];
        add.Annotation.Title.Should().Be("Add a library voice");
        add.Annotation.DestructiveHint.Should().BeTrue();
        add.Annotation.ReadOnlyHint.Should().BeFalse();
        add.Description.Should().Contain("projectVoiceId");
    }

    [Fact]
    public async Task Over_mcp_AddLibraryVoice_runs_propose_then_apply_in_one_call()
    {
        using Rig rig = new();
        IReadOnlyList<AIFunction> tools = rig.Tools().GetTools(new AssistantToolContext(rig.User, rig.Project.Id));
        AssistantMcpToolSurface.TryResolve("AddLibraryVoice", tools, out McpToolCallPlan plan).Should().BeTrue();
        AssistantMcpToolSurface.TryResolve("ProposeAddLibraryVoice", tools, out _).Should().BeFalse();

        object? raw = await AssistantMcpToolSurface.InvokeAsync(plan, new AIFunctionArguments(Rig.Args("fish-bruno")), CancellationToken.None);
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;

        JsonDocument.Parse(text).RootElement.GetProperty("added").GetBoolean().Should().BeTrue();
        rig.Db.ProjectVoices.Single(v => v.ProjectId == rig.Project.Id).Name.Should().Be("Bruno");
    }
}
