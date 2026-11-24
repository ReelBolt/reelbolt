using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>The assistant's ProposeSavePronunciation / ApplySavePronunciation pair and its approval card.</summary>
public class AssistantSavePronunciationTests
{
    private sealed class User(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Project SeedProject(InferenceApiDbContext db, Guid owner)
    {
        var p = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Novara tour",
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(p);
        db.SaveChanges();
        return p;
    }

    private static readonly PronunciationTestGate Gate = new();

    private static AssistantProjectTools Tools(InferenceApiDbContext db, Guid me)
    {
        var user = new User(me);
        return new AssistantProjectTools(db, ConfirmationTokenService.CreateEphemeral(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            user, TestTenancy.Access(db, user), new ReelBolt.Inference.Api.Services.Pronunciation.ProjectPronunciationService(db, Gate));
    }

    private static async Task<JsonElement> InvokeAsync(
        AssistantProjectTools tools, AssistantToolContext context, string name, params (string Key, object? Value)[] args)
    {
        AIFunction tool = tools.GetTools(context).Single(t => t.Name == name);
        var arguments = new AIFunctionArguments();
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

    [Fact]
    public async Task Apply_needs_the_exact_token_and_upserts_created_updated_unchanged()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project project = SeedProject(db, me);
        AssistantProjectTools tools = Tools(db, me);
        var ctx = new AssistantToolContext(me, project.Id);

        JsonElement proposal = await InvokeAsync(tools, ctx, "ProposeSavePronunciation", ("term", "Novara"), ("spelling", "Novàra"));
        db.ProjectPronunciationEntries.Should().BeEmpty("propose writes nothing");
        string token = proposal.GetProperty("confirmationToken").GetString()!;

        (await InvokeAsync(tools, ctx, "ApplySavePronunciation", ("term", "Novara"), ("spelling", "Novàra"), ("confirmationToken", "forged")))
            .GetProperty("error").GetString().Should().Contain("stale");
        (await InvokeAsync(tools, ctx, "ApplySavePronunciation", ("term", "Novara"), ("spelling", "Novara"), ("confirmationToken", token)))
            .GetProperty("error").GetString().Should().Contain("stale", "a changed argument voids the token");
        db.ProjectPronunciationEntries.Should().BeEmpty();

        JsonElement created = await InvokeAsync(tools, ctx, "ApplySavePronunciation", ("term", "Novara"), ("spelling", "Novàra"), ("confirmationToken", token));
        created.GetProperty("status").GetString().Should().Be("Created");
        db.ProjectPronunciationEntries.Single().Spelling.Should().Be("Novàra");

        JsonElement same = await InvokeAsync(tools, ctx, "ApplySavePronunciation", ("term", "novara"), ("spelling", "Novàra"),
            ("confirmationToken", (await InvokeAsync(tools, ctx, "ProposeSavePronunciation", ("term", "novara"), ("spelling", "Novàra"))).GetProperty("confirmationToken").GetString()!));
        same.GetProperty("status").GetString().Should().Be("Unchanged");

        JsonElement updated = await InvokeAsync(tools, ctx, "ApplySavePronunciation", ("term", "Novara"), ("spelling", "Nòvara"),
            ("confirmationToken", (await InvokeAsync(tools, ctx, "ProposeSavePronunciation", ("term", "Novara"), ("spelling", "Nòvara"))).GetProperty("confirmationToken").GetString()!));
        updated.GetProperty("status").GetString().Should().Be("Updated");
        db.ProjectPronunciationEntries.Single().Spelling.Should().Be("Nòvara");
    }

    [Fact]
    public async Task A_project_the_caller_cannot_edit_is_refused_and_nothing_is_written()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project theirs = SeedProject(db, Guid.NewGuid());
        AssistantProjectTools tools = Tools(db, me);
        var ctx = new AssistantToolContext(me);

        JsonElement propose = await InvokeAsync(tools, ctx, "ProposeSavePronunciation",
            ("term", "Novara"), ("spelling", "Novàra"), ("projectId", theirs.Id));
        propose.GetProperty("error").GetString().Should().Contain("not found");

        JsonElement apply = await InvokeAsync(tools, ctx, "ApplySavePronunciation",
            ("term", "Novara"), ("spelling", "Novàra"), ("projectId", theirs.Id), ("confirmationToken", "x"));
        apply.TryGetProperty("error", out _).Should().BeTrue();
        db.ProjectPronunciationEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_spelling_is_refused_at_propose()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project project = SeedProject(db, me);
        JsonElement r = await InvokeAsync(Tools(db, me), new AssistantToolContext(me, project.Id),
            "ProposeSavePronunciation", ("term", "Novara"), ("spelling", ""));
        r.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Approval_card_says_what_is_said_and_that_subtitles_are_unchanged()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project project = SeedProject(db, me);
        var user = new User(me);
        var builder = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, user), user);
        var args = new AIFunctionArguments { ["term"] = "Novara", ["spelling"] = "Novàra" };

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ApplySavePronunciation", args, new AssistantToolContext(me, project.Id), null, CancellationToken.None);

        verdict.Card!.Title.Should().Contain("Novara").And.Contain("Novàra").And.Contain("narration");
        verdict.Card.Summary.Should().Contain("Subtitles and on-screen text are unchanged");
        AssistantApprovalCatalog.IsGated("ApplySavePronunciation").Should().BeTrue();
    }

    [Fact]
    public async Task A_flagged_spelling_is_refused_at_apply_and_nothing_is_written()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project project = SeedProject(db, me);
        var ctx = new AssistantToolContext(me, project.Id);
        AssistantProjectTools tools = Tools(db, me);
        // A separate gate so the other tests in this class keep an allow-everything double.
        var blocking = new PronunciationTestGate { BlockWhenContains = "badword" };
        var user = new User(me);
        var blockingTools = new AssistantProjectTools(db, ConfirmationTokenService.CreateEphemeral(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            user, TestTenancy.Access(db, user), new ReelBolt.Inference.Api.Services.Pronunciation.ProjectPronunciationService(db, blocking));

        // Both calls share the ephemeral token service of THIS instance, so propose and apply on the same tools.
        JsonElement proposal = await InvokeAsync(blockingTools, ctx, "ProposeSavePronunciation", ("term", "Novara"), ("spelling", "badword"));
        string token = proposal.GetProperty("confirmationToken").GetString()!;
        JsonElement r = await InvokeAsync(blockingTools, ctx, "ApplySavePronunciation",
            ("term", "Novara"), ("spelling", "badword"), ("confirmationToken", token));

        r.GetProperty("error").GetString().Should().Contain("Blocked");
        db.ProjectPronunciationEntries.Should().BeEmpty();
        blocking.Seen.Should().ContainSingle().Which.Attribution.Surface.Should().Be(ReelBolt.Shared.Moderation.ModerationSurface.GeneratedText);
    }
}
