using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The approval cards for the two design-system Apply tools: built from the database and the exact arguments,
/// so a person clicking Allow sees what is being approved (project, approve or reject, the change and the
/// token paths it touches), and a call the builder cannot place never gets a card at all.
/// </summary>
public class AssistantDesignSystemCardTests : IDisposable
{
    private static readonly Regex AnyGuid = new(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly Guid _me = Guid.NewGuid();
    private readonly Project _project;
    private readonly Project _foreign;

    public AssistantDesignSystemCardTests()
    {
        _project = SeedProject(_me, "Alpha");
        _foreign = SeedProject(Guid.NewGuid(), "Secret Foreign");
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Project SeedProject(Guid owner, string name)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Projects.Add(project);
        return project;
    }

    private AssistantApprovalCardBuilder Builder()
    {
        var user = new FakeCurrentUser(_me);
        return new AssistantApprovalCardBuilder(_db, TestTenancy.Access(_db, user), user);
    }

    private void SeedPendingChange(string kind, string diffJson, DateTime createdAt)
    {
        DesignSystem? system = _db.DesignSystems.Local.FirstOrDefault(s => s.ProjectId == _project.Id && s.Kind == kind)
            ?? _db.DesignSystems.FirstOrDefault(s => s.ProjectId == _project.Id && s.Kind == kind);
        if (system is null)
        {
            system = new DesignSystem { Id = Guid.NewGuid(), ProjectId = _project.Id, Kind = kind, Name = "Alpha", CreatedByUserId = _me };
            _db.DesignSystems.Add(system);
        }

        string doc = CanonicalJson.Serialize(DesignDocument.New("Alpha"));
        var baseVersion = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = DesignVersionState.Approved,
            DocumentJson = doc,
            DocumentDigest = CanonicalJson.Sha256Hex(doc),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = _me
        };
        var draft = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            VersionNumber = 2,
            State = DesignVersionState.Draft,
            DocumentJson = doc,
            DocumentDigest = CanonicalJson.Sha256Hex(doc),
            Origin = DesignVersionOrigin.StaticExtract,
            BaseVersionId = baseVersion.Id,
            CreatedByUserId = _me
        };
        system.CurrentVersionId = baseVersion.Id;
        _db.DesignSystemVersions.AddRange(baseVersion, draft);
        _db.DesignSystemChangeProposals.Add(new DesignSystemChangeProposal
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            BaseVersionId = baseVersion.Id,
            ProposedVersionId = draft.Id,
            Trigger = DesignProposalTrigger.SourceChanged,
            DriftScore = 0.4,
            DiffJson = diffJson,
            Status = DesignProposalStatus.Pending,
            ProposedDigest = draft.DocumentDigest,
            CreatedAt = createdAt
        });
        _db.SaveChanges();
    }

    private const string TwoPathDiff =
        "{\"changed\":[{\"path\":\"color.brand.primary\"},{\"path\":\"color.text\"}],\"added\":[{\"path\":\"typography.family.body\"}]}";

    private static AIFunctionArguments Args(params (string Key, object? Value)[] args)
    {
        var arguments = new AIFunctionArguments();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        return arguments;
    }

    private Task<ApprovalVerdict> DecideCardAsync(string decision, string changeName, Guid? projectId = null) =>
        Builder().BuildAsync("ApplyDecideDesignChange",
            Args(("decision", decision), ("changeName", changeName), ("confirmationToken", "t"), ("projectId", (projectId ?? _project.Id).ToString())),
            new AssistantToolContext(_me, _project.Id), proposal: null, default);

    [Fact]
    public async Task The_approve_card_names_the_project_the_change_and_the_tokens_it_touches()
    {
        SeedPendingChange(DesignSystemKinds.Design, TwoPathDiff, DateTime.UtcNow);

        ApprovalVerdict verdict = await DecideCardAsync("approve", "design system: color.brand.primary, color.text, typography.family.body");

        ApprovalCard card = verdict.Card.Should().NotBeNull(verdict.Refusal).And.Subject.As<ApprovalCard>();
        card.Title.Should().StartWith("Approve").And.Contain("color.brand.primary");
        card.Title.Should().NotBe(AssistantApprovalCatalog.Describe("ApplyDecideDesignChange").FallbackTitle);
        card.ProjectName.Should().Be("Alpha");
        card.Summary.Should().Contain("3 tokens");
        card.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("color.text"));
        AnyGuid.IsMatch(card.Title + card.Summary + string.Join(' ', card.Changes!.Select(c => c.Text))).Should().BeFalse("a card names things, never ids");
    }

    [Fact]
    public async Task The_reject_card_says_reject_not_approve()
    {
        SeedPendingChange(DesignSystemKinds.Design, TwoPathDiff, DateTime.UtcNow);

        ApprovalVerdict verdict = await DecideCardAsync("reject", "design system: color.brand.primary, color.text, typography.family.body");

        verdict.Card!.Title.Should().StartWith("Reject");
        verdict.Card.Summary.Should().Contain("not become the live");
    }

    [Fact]
    public async Task A_change_the_builder_cannot_place_gets_no_card()
    {
        SeedPendingChange(DesignSystemKinds.Design, TwoPathDiff, DateTime.UtcNow);

        (await DecideCardAsync("approve", "design system: nothing like this")).Refusal.Should().NotBeNull();
        (await DecideCardAsync("maybe", "design system: color.brand.primary, color.text, typography.family.body")).Refusal.Should().NotBeNull();
        (await DecideCardAsync("approve", "design system: color.brand.primary, color.text, typography.family.body", _foreign.Id))
            .Refusal.Should().Be("project not found");
    }

    [Fact]
    public async Task Two_changes_sharing_a_name_get_no_card_for_the_shared_name()
    {
        SeedPendingChange(DesignSystemKinds.ProductMap, "{}", DateTime.UtcNow.AddMinutes(-2));
        SeedPendingChange(DesignSystemKinds.ProductMap, "{}", DateTime.UtcNow.AddMinutes(-1));

        (await DecideCardAsync("approve", "product profile change")).Refusal.Should().Contain("more than one");
        (await DecideCardAsync("approve", "product profile change (#2)")).Card.Should().NotBeNull();
    }

    [Fact]
    public async Task The_save_card_shows_the_project_and_the_exact_brand_values()
    {
        ApprovalVerdict verdict = await Builder().BuildAsync("ApplySaveDesignSystem",
            Args(("primaryColor", "#112233"), ("secondaryColor", "#445566"), ("backgroundColor", "#ffffff"), ("textColor", "#000000"),
                ("headingFont", "Inter"), ("bodyFont", "Montserrat"), ("tagline", "Promo videos, fast."),
                ("confirmationToken", "t"), ("projectId", _project.Id.ToString())),
            new AssistantToolContext(_me, _project.Id), proposal: null, default);

        ApprovalCard card = verdict.Card.Should().NotBeNull(verdict.Refusal).And.Subject.As<ApprovalCard>();
        card.Title.Should().Contain("Alpha").And.NotBe(AssistantApprovalCatalog.Describe("ApplySaveDesignSystem").FallbackTitle);
        card.ProjectName.Should().Be("Alpha");
        string changes = string.Join('\n', card.Changes!.Select(c => c.Text));
        changes.Should().Contain("#112233").And.Contain("Montserrat").And.Contain("Promo videos, fast.");
    }

    [Fact]
    public async Task The_save_card_lists_the_logo_by_name()
    {
        var primary = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            OriginalFileName = "nexusflow-logo.png",
            MimeType = "image/png",
            StorageKey = "k",
            StorageBucket = "b"
        };
        _db.ProjectFiles.Add(primary);
        _db.SaveChanges();

        ApprovalVerdict verdict = await Builder().BuildAsync("ApplySaveDesignSystem",
            Args(("logoProjectFileIds", new[] { primary.Id.ToString(), Guid.NewGuid().ToString() }),
                ("confirmationToken", "t"), ("projectId", _project.Id.ToString())),
            new AssistantToolContext(_me, _project.Id), proposal: null, default);

        ApprovalCard card = verdict.Card.Should().NotBeNull(verdict.Refusal).And.Subject.As<ApprovalCard>();
        string changes = string.Join('\n', card.Changes!.Select(c => c.Text));
        changes.Should().Contain("Logo: nexusflow-logo.png").And.Contain("1 more logo variant");
        AnyGuid.IsMatch(card.Title + card.Summary + changes).Should().BeFalse("a card names things, never ids");
    }

    [Fact]
    public async Task The_save_card_for_a_call_with_no_values_still_gets_a_title()
    {
        ApprovalVerdict verdict = await Builder().BuildAsync("ApplySaveDesignSystem",
            Args(("confirmationToken", "t"), ("projectId", _project.Id.ToString())),
            new AssistantToolContext(_me, _project.Id), proposal: null, default);

        ApprovalCard card = verdict.Card.Should().NotBeNull(verdict.Refusal).And.Subject.As<ApprovalCard>();
        card.Title.Should().Contain("Alpha").And.NotBe(AssistantApprovalCatalog.Describe("ApplySaveDesignSystem").FallbackTitle);
    }

    [Fact]
    public async Task The_save_card_for_a_foreign_project_is_refused()
    {
        ApprovalVerdict verdict = await Builder().BuildAsync("ApplySaveDesignSystem",
            Args(("primaryColor", "#112233"), ("confirmationToken", "t"), ("projectId", _foreign.Id.ToString())),
            new AssistantToolContext(_me, _project.Id), proposal: null, default);

        verdict.Refusal.Should().Be("project not found");
    }
}
