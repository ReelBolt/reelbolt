using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplacePublishTests
{
    // ---------------------------------------------------------------- fakes

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId { get; set; } = Guid.NewGuid();
        public string Email => "someone@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; set; } = Guid.NewGuid();
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
        public bool EmailVerified { get; set; } = true;
    }

    private sealed class FakeAccess : IProjectAccessService
    {
        public int Calls;
        public ProjectAccessOutcome Outcome = ProjectAccessOutcome.Found;
        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ProjectAccessResult(Outcome, Outcome == ProjectAccessOutcome.Found ? new Project { Id = projectId } : null));
        }

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) => GetAsync(projectId, permission, default).Result;
        /// <summary>The organization-wide answer when it differs from the per-project one (a project-restricted API key).</summary>
        public ProjectAccessOutcome? OrganizationOutcome;
        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) => OrganizationOutcome ?? Outcome;
    }

    private sealed class FakeTextCheck : IMarketplaceTextCheck
    {
        public TextVerdict Verdict = TextVerdict.Allow;
        public Task<TextCheckResult> CheckAsync(IReadOnlyList<TextCheckField> fields, Guid o, Guid p, Guid u, CancellationToken ct) =>
            Task.FromResult(new TextCheckResult(Verdict, [new TextCheckFieldResult("name", Verdict)]));
    }

    private sealed class FakeDispatcher : IMarketplacePreviewDispatcher
    {
        public readonly List<Guid> Versions = [];
        public Task DispatchAsync(Guid recipeId, Guid versionId, CancellationToken ct)
        {
            Versions.Add(versionId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredits : ICreditBalanceReader
    {
        public string? Plan;
        public bool Throw;
        public Func<Task>? OnRead;
        public async Task<CreditBalanceSnapshot?> GetAsync(Guid organizationId, CancellationToken ct)
        {
            // Runs once, in the middle of a finalize: after it read the recipe and before it saves, the window a race needs.
            if (OnRead is { } hook)
            {
                OnRead = null;
                await hook();
            }

            return Throw ? throw new InvalidOperationException("boom") : Plan is null ? null : new CreditBalanceSnapshot(0, 0, Plan, null);
        }
    }

    private sealed class Harness
    {
        public readonly DbContextOptions<InferenceApiDbContext> DbOptions = new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public readonly InferenceApiDbContext Db;
        public readonly FakeUser User = new();
        public readonly FakeAccess Access = new();
        public readonly FakeTextCheck Text = new();
        public readonly FakeDispatcher Dispatcher = new();
        public readonly FakeCredits Credits = new();
        public readonly MarketplaceOptions Options = new() { Enabled = true };
        public readonly Guid ProjectId = Guid.NewGuid();
        public Guid WorkflowId;
        public Guid FileId = Guid.NewGuid();

        public Harness()
        {
            Db = new InferenceApiDbContext(DbOptions);
            WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().First(t =>
            {
                List<WorkflowStep> s = SnapshotFixtures.FromTemplate(t);
                PortableRecipeResult r = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), s, SnapshotFixtures.BuiltIns(), null);
                return r.Recipe is not null && PortableRecipeValidator.Validate(r.Recipe).Count == 0;
            });
            WorkflowDefinition wf = SnapshotFixtures.Workflow();
            wf.ProjectId = ProjectId;
            wf.Steps = SnapshotFixtures.FromTemplate(template);
            WorkflowId = wf.Id;
            Db.Projects.Add(new Project { Id = ProjectId, Name = "P", OwnerId = User.UserId, OrganizationId = User.OrganizationId!.Value });
            Db.WorkflowDefinitions.Add(wf);
            Db.AgentDefinitions.AddRange(SnapshotFixtures.BuiltIns().Values);
            Db.ProjectFiles.Add(new ProjectFile { Id = FileId, ProjectId = ProjectId, StorageKey = "private/author/key.mp4", StorageBucket = "b", MimeType = "video/mp4", CsamStatus = CsamScanStatus.Clean });
            Db.SaveChanges();
        }

        public MarketplacePublishService Service(InferenceApiDbContext? db = null) =>
            new(db ?? Db, User, Access, Text, Dispatcher, Credits, new MarketplaceCapLock(), Microsoft.Extensions.Options.Options.Create(Options), NullLogger<MarketplacePublishService>.Instance);

        public PublishRecipeRequest Request(Action<PublishRecipeRequest>? _ = null, string? name = "My recipe", bool consent = true, string? consentVersion = null, string? label = null, string? outputRef = null) =>
            new(ProjectId, WorkflowId, outputRef ?? $"file:{FileId}", name, "Makes a short promo.", "promo", "en", label, consent, consentVersion ?? Options.ConsentVersion);
    }

    private static T Value<T>(MarketplaceResult<T> r)
    {
        r.Error.Should().BeNull();
        return r.Value!;
    }

    // ---------------------------------------------------------------- flag

    [Fact]
    public void Every_action_sits_behind_the_flag_filter_and_the_filter_answers_a_bare_404_when_off()
    {
        typeof(MarketplaceController).GetCustomAttributes(typeof(TypeFilterAttribute), true)
            .Cast<TypeFilterAttribute>().Should().Contain(a => a.ImplementationType == typeof(MarketplaceEnabledFilter));

        string[] actions = [nameof(MarketplaceController.Consent), nameof(MarketplaceController.PublishPreview), nameof(MarketplaceController.Create),
            nameof(MarketplaceController.Update), nameof(MarketplaceController.Unpublish), nameof(MarketplaceController.Mine)];
        typeof(MarketplaceController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Should().BeEquivalentTo(actions);

        foreach (bool enabled in new[] { false, true })
        {
            ResourceExecutingContext ctx = new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [], []);
            new MarketplaceEnabledFilter(new Snapshot(new MarketplaceOptions { Enabled = enabled })).OnResourceExecuting(ctx);
            if (enabled)
                ctx.Result.Should().BeNull();
            else
                ctx.Result.Should().BeOfType<NotFoundResult>();
        }
    }

    private sealed class Snapshot(MarketplaceOptions value) : IOptionsSnapshot<MarketplaceOptions>
    {
        public MarketplaceOptions Value => value;
        public MarketplaceOptions Get(string? name) => value;
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public async Task Preview_authorizes_once_writes_nothing_and_describes_the_recipe()
    {
        Harness h = new();
        PublishPreviewResponse p = Value(await h.Service().PreviewAsync(h.ProjectId, h.WorkflowId, default));

        p.Publishable.Should().BeTrue();
        p.Steps.Should().NotBeEmpty();
        p.CostClass.Should().NotBeNullOrEmpty();
        h.Access.Calls.Should().Be(1);
        (await h.Db.MarketplaceRecipes.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceAudit.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Preview_of_a_hidden_workflow_kind_is_refused_with_the_reason()
    {
        Harness h = new();
        WorkflowDefinition wf = await h.Db.WorkflowDefinitions.FirstAsync();
        wf.Kind = WorkflowDefinitionKind.CompositionRevision;
        await h.Db.SaveChangesAsync();

        PublishPreviewResponse p = Value(await h.Service().PreviewAsync(h.ProjectId, h.WorkflowId, default));
        p.Publishable.Should().BeFalse();
        p.Refusals.Select(r => r.Code).Should().Contain("HIDDEN_WORKFLOW_KIND");

        MarketplaceResult<PublishedRecipeResponse> created = await h.Service().CreateAsync(h.Request(), default);
        created.Error!.Status.Should().Be(422);
        created.Error.Refusals!.Select(r => r.Code).Should().Contain("HIDDEN_WORKFLOW_KIND");
    }

    [Fact]
    public async Task Preview_of_another_orgs_project_is_404()
    {
        Harness h = new();
        h.Access.Outcome = ProjectAccessOutcome.NotFound;
        (await h.Service().PreviewAsync(h.ProjectId, h.WorkflowId, default)).Error!.Status.Should().Be(404);
    }

    // ---------------------------------------------------------------- create gates

    [Fact]
    public async Task Unverified_email_is_403()
    {
        Harness h = new();
        h.User.EmailVerified = false;
        MarketplaceError e = (await h.Service().CreateAsync(h.Request(), default)).Error!;
        e.Status.Should().Be(403);
        e.Code.Should().Be("EMAIL_NOT_VERIFIED");
    }

    [Fact]
    public async Task Stale_consent_version_is_409_and_unaccepted_is_400()
    {
        Harness h = new();
        MarketplaceError stale = (await h.Service().CreateAsync(h.Request(consentVersion: "1999-01-01"), default)).Error!;
        stale.Status.Should().Be(409);
        stale.Code.Should().Be("CONSENT_VERSION_STALE");
        (await h.Service().CreateAsync(h.Request(consent: false), default)).Error!.Status.Should().Be(400);
        (await h.Db.MarketplaceRecipes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Daily_publish_cap_counts_rows_since_utc_midnight()
    {
        Harness h = new();
        h.Options.MaxPublishesPerUserPerDay = 2;
        Value(await h.Service().CreateAsync(h.Request(), default));
        await SetLabelAsync(h, "second");
        Value(await h.Service().CreateAsync(h.Request(), default));
        MarketplaceError e = (await h.Service().CreateAsync(h.Request(), default)).Error!;
        e.Status.Should().Be(429);
        e.Code.Should().Be("DAILY_PUBLISH_LIMIT");

        foreach (MarketplaceRecipeVersion v in h.Db.MarketplaceRecipeVersions)
            v.CreatedAt = DateTime.UtcNow.Date.AddSeconds(-1);
        await h.Db.SaveChangesAsync();
        await SetLabelAsync(h, "third");
        Value(await h.Service().CreateAsync(h.Request(), default));
    }

    [Fact]
    public async Task Live_cap_counts_listed_and_pending_recipes_of_the_organization()
    {
        Harness h = new();
        h.Options.MaxLivePerOrganization = 1;
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = Guid.NewGuid(), AuthorOrgId = h.User.OrganizationId!.Value, Status = RecipeStatus.Pending });
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = Guid.NewGuid(), AuthorOrgId = h.User.OrganizationId!.Value, Status = RecipeStatus.Unpublished });
        await h.Db.SaveChangesAsync();
        MarketplaceError e = (await h.Service().CreateAsync(h.Request(), default)).Error!;
        e.Code.Should().Be("LIVE_RECIPE_LIMIT");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:not-a-guid")]
    [InlineData("step_result:00000000-0000-0000-0000-000000000001")]
    [InlineData("file:00000000-0000-0000-0000-000000000001")]
    public async Task Missing_or_unresolvable_preview_output_is_PREVIEW_REQUIRED(string? outputRef)
    {
        Harness h = new();
        PublishRecipeRequest r = h.Request() with { OutputRef = outputRef };
        MarketplaceError e = (await h.Service().CreateAsync(r, default)).Error!;
        e.Status.Should().Be(404);
        e.Code.Should().Be("PREVIEW_REQUIRED");
    }

    [Fact]
    public async Task Preview_source_is_stored_server_side_only_and_the_project_is_authorized_once()
    {
        Harness h = new();
        PublishedRecipeResponse r = Value(await h.Service().CreateAsync(h.Request(), default));

        h.Access.Calls.Should().Be(1);
        MarketplaceRecipeVersion v = await h.Db.MarketplaceRecipeVersions.SingleAsync();
        v.PreviewSourceJson.Should().Contain("private/author/key.mp4");
        JsonSerializer.Serialize(r).Should().NotContain("private/author");
        h.Dispatcher.Versions.Should().Equal(v.Id);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("a@b.co", false)]
    [InlineData("Contact me at a@b.co", false)]
    [InlineData("Ada", true)]
    public async Task Author_label_is_1_to_60_plain_text_and_never_an_email(string label, bool ok)
    {
        Harness h = new();
        MarketplaceResult<PublishedRecipeResponse> r = await h.Service().CreateAsync(h.Request(label: label), default);
        if (label == "")
        {
            r.Error.Should().BeNull();
            return;
        }

        (r.Error is null).Should().Be(ok);
        if (!ok)
            r.Error!.Code.Should().Be("INVALID_FIELD");
    }

    [Fact]
    public async Task Default_author_label_is_the_display_name_unless_it_looks_like_an_email()
    {
        Harness h = new();
        h.Db.ApplicationUsers.Add(new ApplicationUser { Id = h.User.UserId, DisplayName = "Grace H", Email = "g@x.io" });
        await h.Db.SaveChangesAsync();
        Value(await h.Service().CreateAsync(h.Request(), default));
        (await h.Db.MarketplaceRecipes.SingleAsync()).AuthorLabel.Should().Be("Grace H");

        h.Db.ApplicationUsers.Single().DisplayName = "g@x.io";
        await h.Db.SaveChangesAsync();
        await SetLabelAsync(h, "second recipe");
        Value(await h.Service().CreateAsync(h.Request(), default));
        (await h.Db.MarketplaceRecipes.OrderByDescending(r => r.CreatedAt).FirstAsync()).AuthorLabel.Should().NotContain("@");
    }

    [Theory]
    [InlineData("", "d", "promo", "en")]
    [InlineData("n", "", "promo", "en")]
    [InlineData("n", "d", "nope", "en")]
    [InlineData("n", "d", "promo", "fr")]
    public async Task Name_description_category_and_language_are_validated(string name, string description, string category, string language)
    {
        Harness h = new();
        PublishRecipeRequest r = h.Request(name: name) with { Description = description, Category = category, Language = language };
        (await h.Service().CreateAsync(r, default)).Error!.Code.Should().Be("INVALID_FIELD");

        (await h.Service().CreateAsync(h.Request(name: new string('x', 81)), default)).Error!.Code.Should().Be("INVALID_FIELD");
    }

    [Fact]
    public async Task Markup_is_stripped_from_the_name()
    {
        Harness h = new();
        Value(await h.Service().CreateAsync(h.Request(name: "<b>Bold</b> promo"), default));
        (await h.Db.MarketplaceRecipes.SingleAsync()).Name.Should().Be("Bold promo");
    }

    // ---------------------------------------------------------------- text check outcomes

    [Fact]
    public async Task Text_block_refuses_and_writes_nothing()
    {
        Harness h = new();
        h.Text.Verdict = TextVerdict.Block;
        MarketplaceError e = (await h.Service().CreateAsync(h.Request(), default)).Error!;
        e.Status.Should().Be(422);
        e.Code.Should().Be("TEXT_BLOCKED");
        (await h.Db.MarketplaceRecipes.CountAsync()).Should().Be(0);
        h.Dispatcher.Versions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(TextVerdict.Review, "text_review")]
    [InlineData(TextVerdict.Unavailable, "moderation_unavailable")]
    public async Task Text_review_or_unavailable_goes_to_a_human_even_for_a_trusted_paid_author(TextVerdict verdict, string reason)
    {
        Harness h = new();
        h.Text.Verdict = verdict;
        h.Credits.Plan = "pro";
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));
        created.Status.Should().Be("draft");
        created.PreviewStatus.Should().Be("preparing");

        ListingResult? result = await h.Service().FinalizeVersionAsync(h.Db.MarketplaceRecipeVersions.Single().Id, PreviewVerdict.Allow, default);
        result!.Outcome.Should().Be(ListingOutcome.Pending);
        result.Reason.Should().Be(reason);
        (await h.Db.MarketplaceRecipes.SingleAsync()).Status.Should().Be(RecipeStatus.Pending);
    }

    private sealed class FakeGate(Func<ModerationDecision> decide) : IContentModerationGate
    {
        public int Calls;
        public Task<ModerationDecision> ModerateAsync(ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Calls++;
            attribution.Surface.Should().Be(ModerationSurface.Publish);
            return Task.FromResult(decide());
        }

        public Task<bool> ScreenGenerationPromptAsync(string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ScreenGeneratedTextAsync(string text, string site, ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ScreenPublishAsync(ModerationInput input, ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
    }

    private static ModerationDecision Decision(ModerationAction a, bool available = true) => new(a, available, false, null, "", "hash");

    [Theory]
    [InlineData(ModerationAction.Allow, true, TextVerdict.Allow)]
    [InlineData(ModerationAction.Allow, false, TextVerdict.Unavailable)]
    [InlineData(ModerationAction.Review, true, TextVerdict.Review)]
    [InlineData(ModerationAction.Block, true, TextVerdict.Block)]
    [InlineData(ModerationAction.Unavailable, true, TextVerdict.Unavailable)]
    public async Task Text_check_maps_the_gate_and_fails_closed_when_it_did_not_run(ModerationAction action, bool available, TextVerdict expected)
    {
        MarketplaceTextCheck check = new(new FakeGate(() => Decision(action, available)), NullLogger<MarketplaceTextCheck>.Instance);
        TextCheckResult r = await check.CheckAsync([new("name", "hello")], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default);
        r.Verdict.Should().Be(expected);
    }

    [Fact]
    public async Task Text_check_treats_a_throwing_gate_as_unavailable_and_checks_every_distinct_field()
    {
        MarketplaceTextCheck failing = new(new FakeGate(() => throw new InvalidOperationException()), NullLogger<MarketplaceTextCheck>.Instance);
        (await failing.CheckAsync([new("a", "x")], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default)).Verdict.Should().Be(TextVerdict.Unavailable);

        FakeGate gate = new(() => Decision(ModerationAction.Allow));
        MarketplaceTextCheck ok = new(gate, NullLogger<MarketplaceTextCheck>.Instance);
        await ok.CheckAsync([new("a", "x"), new("b", "y"), new("c", "x"), new("d", " ")], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default);
        gate.Calls.Should().Be(2);
    }

    // ---------------------------------------------------------------- listing decision

    [Fact]
    public void Listing_decision_matrix_lists_only_when_every_condition_holds()
    {
        foreach (TextVerdict text in Enum.GetValues<TextVerdict>())
            foreach (PreviewVerdict? preview in new PreviewVerdict?[] { null, PreviewVerdict.Allow, PreviewVerdict.Review, PreviewVerdict.Block, PreviewVerdict.Failed })
                foreach (bool trusted in new[] { false, true })
                    foreach (bool paid in new[] { false, true })
                    {
                        ListingResult r = ListingDecision.Decide(text, preview, trusted, paid);
                        bool all = text == TextVerdict.Allow && preview == PreviewVerdict.Allow && (trusted || paid);
                        (r.Outcome == ListingOutcome.Listed).Should().Be(all, $"{text} {preview} {trusted} {paid}");
                        bool refused = text == TextVerdict.Block || preview is PreviewVerdict.Block or PreviewVerdict.Failed;
                        (r.Outcome == ListingOutcome.Rejected).Should().Be(refused, $"{text} {preview} {trusted} {paid}");
                    }
    }

    [Fact]
    public void Listing_decision_without_a_preview_verdict_is_never_listed()
    {
        ListingDecision.Decide(TextVerdict.Allow, null, true, true).Outcome.Should().Be(ListingOutcome.Pending);
    }

    // ---------------------------------------------------------------- finalize / versions

    [Fact]
    public async Task Untrusted_unpaid_author_goes_pending_then_a_paid_one_lists()
    {
        Harness h = new();
        Value(await h.Service().CreateAsync(h.Request(), default));
        Guid v1 = h.Db.MarketplaceRecipeVersions.Single().Id;
        (await h.Service().FinalizeVersionAsync(v1, PreviewVerdict.Allow, default))!.Reason.Should().Be("not_trusted");
        (await h.Db.MarketplaceRecipes.SingleAsync()).Status.Should().Be(RecipeStatus.Pending);

        Harness paid = new();
        paid.Credits.Plan = "creator";
        Value(await paid.Service().CreateAsync(paid.Request(), default));
        (await paid.Service().FinalizeVersionAsync(paid.Db.MarketplaceRecipeVersions.Single().Id, PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Listed);
        MarketplaceRecipe listed = await paid.Db.MarketplaceRecipes.SingleAsync();
        listed.Status.Should().Be(RecipeStatus.Listed);
        listed.EverListedAt.Should().NotBeNull();
        listed.CurrentVersionId.Should().Be(paid.Db.MarketplaceRecipeVersions.Single().Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("trial")]
    public async Task Billing_off_unreadable_or_trial_is_not_paid(string? plan)
    {
        Harness h = new();
        h.Credits.Plan = plan;
        Value(await h.Service().CreateAsync(h.Request(), default));
        (await h.Service().FinalizeVersionAsync(h.Db.MarketplaceRecipeVersions.Single().Id, PreviewVerdict.Allow, default))!.Reason.Should().Be("not_trusted");

        Harness boom = new();
        boom.Credits.Throw = true;
        Value(await boom.Service().CreateAsync(boom.Request(), default));
        (await boom.Service().FinalizeVersionAsync(boom.Db.MarketplaceRecipeVersions.Single().Id, PreviewVerdict.Allow, default))!.Reason.Should().Be("not_trusted");
    }

    [Fact]
    public async Task A_trusted_author_lists_without_a_paid_plan()
    {
        Harness h = new();
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = Guid.NewGuid(), AuthorUserId = h.User.UserId, AuthorOrgId = Guid.NewGuid(), Status = RecipeStatus.Unpublished, EverListedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        Value(await h.Service().CreateAsync(h.Request(), default));
        (await h.Service().FinalizeVersionAsync(h.Db.MarketplaceRecipeVersions.Single().Id, PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Listed);
    }

    [Fact]
    public async Task Finalize_is_idempotent_and_a_blocked_preview_rejects_a_draft()
    {
        Harness h = new();
        Value(await h.Service().CreateAsync(h.Request(), default));
        Guid v = h.Db.MarketplaceRecipeVersions.Single().Id;
        (await h.Service().FinalizeVersionAsync(v, PreviewVerdict.Block, default))!.Outcome.Should().Be(ListingOutcome.Rejected);
        (await h.Service().FinalizeVersionAsync(v, PreviewVerdict.Allow, default)).Should().BeNull();
        (await h.Db.MarketplaceRecipes.SingleAsync()).Status.Should().Be(RecipeStatus.Rejected);
        (await h.Db.MarketplaceRecipeVersions.SingleAsync()).PreviewStatus.Should().Be(PreviewStatus.Ready);
        h.Db.MarketplaceAudit.Select(a => a.Action).Should().Contain("version_rejected");
    }

    /// <summary>MKT-07: update is organization-wide like unpublish, so Edit on one project (a project-restricted key) is not enough.</summary>
    [Fact]
    public async Task Update_needs_organization_wide_edit_like_unpublish_while_create_does_not()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));
        await h.Service().FinalizeVersionAsync(h.Db.MarketplaceRecipeVersions.Single().Id, PreviewVerdict.Allow, default);
        h.Access.OrganizationOutcome = ProjectAccessOutcome.Forbidden;

        (await h.Service().UpdateAsync(created.Id, h.Request(name: "Replaced"), default)).Error.Should().Be(new MarketplaceError(403));
        (await h.Service().UnpublishAsync(created.Id, default)).Error.Should().Be(new MarketplaceError(403));
        h.Db.MarketplaceRecipeVersions.Count().Should().Be(1, "nothing was written");

        h.Access.OrganizationOutcome = null;
        Value(await h.Service().UpdateAsync(created.Id, h.Request(name: "Renamed"), default)).Version.Should().Be(2);
    }

    [Fact]
    public async Task Update_makes_version_2_while_version_1_stays_live_until_its_own_checks_pass()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));
        Guid v1 = h.Db.MarketplaceRecipeVersions.Single().Id;
        await h.Service().FinalizeVersionAsync(v1, PreviewVerdict.Allow, default);

        PublishedRecipeResponse updated = Value(await h.Service().UpdateAsync(created.Id, h.Request(name: "Renamed"), default));
        updated.Version.Should().Be(2);
        updated.Status.Should().Be("listed");
        MarketplaceRecipe live = await h.Db.MarketplaceRecipes.SingleAsync();
        live.CurrentVersionId.Should().Be(v1);
        live.Name.Should().Be("My recipe");

        Guid v2 = h.Db.MarketplaceRecipeVersions.Single(v => v.N == 2).Id;
        await h.Service().FinalizeVersionAsync(v2, PreviewVerdict.Allow, default);
        live = await h.Db.MarketplaceRecipes.SingleAsync();
        live.CurrentVersionId.Should().Be(v2);
        live.Name.Should().Be("Renamed");
        live.Status.Should().Be(RecipeStatus.Listed);
    }

    [Fact]
    public async Task A_version_that_needs_review_leaves_the_live_one_in_place()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));
        Guid v1 = h.Db.MarketplaceRecipeVersions.Single().Id;
        await h.Service().FinalizeVersionAsync(v1, PreviewVerdict.Allow, default);

        h.Text.Verdict = TextVerdict.Review;
        Value(await h.Service().UpdateAsync(created.Id, h.Request(name: "Edited"), default));
        await h.Service().FinalizeVersionAsync(h.Db.MarketplaceRecipeVersions.Single(v => v.N == 2).Id, PreviewVerdict.Allow, default);
        MarketplaceRecipe live = await h.Db.MarketplaceRecipes.SingleAsync();
        live.CurrentVersionId.Should().Be(v1);
        live.Name.Should().Be("My recipe");
        live.Status.Should().Be(RecipeStatus.Listed);
    }

    // ---------------------------------------------------------------- unpublish / org isolation / mine

    [Fact]
    public async Task Unpublish_withdraws_the_recipe_and_audits_it()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));
        await h.Service().FinalizeVersionAsync(h.Db.MarketplaceRecipeVersions.Single().Id, PreviewVerdict.Allow, default);

        PublishedRecipeResponse r = Value(await h.Service().UnpublishAsync(created.Id, default));
        r.Status.Should().Be("unpublished");
        h.Db.MarketplaceAudit.Select(a => a.Action).Should().Contain("unpublished");
        (await h.Service().UnpublishAsync(created.Id, default)).Error!.Status.Should().Be(409);
    }

    [Fact]
    public async Task Another_organization_gets_404_on_update_and_unpublish()
    {
        Harness h = new();
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));

        h.User.OrganizationId = Guid.NewGuid();
        (await h.Service().UpdateAsync(created.Id, h.Request(), default)).Error.Should().Be(new MarketplaceError(404));
        (await h.Service().UnpublishAsync(created.Id, default)).Error.Should().Be(new MarketplaceError(404));
        (await h.Service().UnpublishAsync(Guid.NewGuid(), default)).Error.Should().Be(new MarketplaceError(404));
        Value(await h.Service().MineAsync(default)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Mine_lists_only_the_active_organizations_recipes_with_lowercase_words()
    {
        Harness h = new();
        Value(await h.Service().CreateAsync(h.Request(), default));
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = Guid.NewGuid(), AuthorOrgId = Guid.NewGuid(), Name = "other" });
        await h.Db.SaveChangesAsync();

        MineResponse mine = Value(await h.Service().MineAsync(default));
        MineRecipeDto item = mine.Items.Should().ContainSingle().Subject;
        item.Status.Should().Be("draft");
        item.PreviewStatus.Should().Be("preparing");
        item.Versions.Should().ContainSingle();
        item.ReasonCategory.Should().BeNull("a draft shows no reason");
    }

    [Fact]
    public void Wire_words_are_lowercase_for_every_enum_member()
    {
        foreach (RecipeStatus s in Enum.GetValues<RecipeStatus>())
            MarketplaceWire.Status(s).Should().Be(s.ToString().ToLowerInvariant());
        foreach (PreviewStatus s in Enum.GetValues<PreviewStatus>())
            MarketplaceWire.Preview(s).Should().Be(s.ToString().ToLowerInvariant());
    }

    // ---------------------------------------------------------------- security: human rejection, stale versions, live cap race

    private static async Task<Guid> VersionIdAsync(Harness h, int n) =>
        (await h.Db.MarketplaceRecipeVersions.SingleAsync(v => v.N == n)).Id;

    [Fact]
    public void Listing_decision_never_lists_a_human_rejected_recipe()
    {
        ListingDecision.Decide(TextVerdict.Allow, PreviewVerdict.Allow, true, true, humanRejected: true).Should().Be(new ListingResult(ListingOutcome.Pending, "human_review_required"));
        ListingDecision.Decide(TextVerdict.Block, PreviewVerdict.Allow, true, true, humanRejected: true).Outcome.Should().Be(ListingOutcome.Rejected);
    }

    [Fact]
    public async Task After_an_admin_rejection_every_later_version_waits_for_a_human_but_an_author_unpublish_does_not_count()
    {
        Harness h = new();
        Value(await h.Service().CreateAsync(h.Request(), default));
        await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default);
        Guid recipeId = (await h.Db.MarketplaceRecipes.SingleAsync()).Id;
        Value(await h.Service().RejectVersionAsync(recipeId, await VersionIdAsync(h, 1), Guid.NewGuid(), null, default));
        MarketplaceRecipe recipe = await h.Db.MarketplaceRecipes.SingleAsync();
        recipe.Status.Should().Be(RecipeStatus.Rejected);
        recipe.HumanRejectedAt.Should().NotBeNull();

        h.Credits.Plan = "pro";
        Value(await h.Service().UpdateAsync(recipeId, h.Request(), default));
        (await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 2), PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Pending);
        (await h.Db.MarketplaceRecipes.SingleAsync()).Status.Should().Be(RecipeStatus.Pending);

        Harness u = new();
        u.Credits.Plan = "pro";
        Value(await u.Service().CreateAsync(u.Request(), default));
        await u.Service().FinalizeVersionAsync(await VersionIdAsync(u, 1), PreviewVerdict.Allow, default);
        Guid uid = (await u.Db.MarketplaceRecipes.SingleAsync()).Id;
        Value(await u.Service().UnpublishAsync(uid, default));
        (await u.Db.MarketplaceRecipes.SingleAsync()).HumanRejectedAt.Should().BeNull();
        Value(await u.Service().UpdateAsync(uid, u.Request(), default));
        (await u.Service().FinalizeVersionAsync(await VersionIdAsync(u, 2), PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Listed);
    }

    [Fact]
    public async Task A_new_version_supersedes_the_pending_one_and_only_the_latest_can_be_approved_once()
    {
        Harness h = new();
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));
        await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default);
        Guid v1 = await VersionIdAsync(h, 1);
        string digest1 = MarketplacePublishService.VersionDigest(await h.Db.MarketplaceRecipeVersions.SingleAsync());

        Value(await h.Service().UpdateAsync(created.Id, h.Request(name: "Second"), default));
        Guid v2 = await VersionIdAsync(h, 2);
        h.Db.MarketplaceAudit.Select(a => a.Action).Should().Contain("version_superseded");
        (await h.Service().ApproveVersionAsync(created.Id, v1, Guid.NewGuid(), digest1, default)).Error!.Code.Should().Be("STALE_VERSION");

        await h.Service().FinalizeVersionAsync(v2, PreviewVerdict.Allow, default);
        MarketplaceRecipe pending = await h.Db.MarketplaceRecipes.SingleAsync();
        pending.Status.Should().Be(RecipeStatus.Pending);
        pending.CurrentVersionId.Should().BeNull();
        (await h.Service().ApproveVersionAsync(created.Id, v1, Guid.NewGuid(), null, default)).Error!.Code.Should().Be("STALE_VERSION");
        (await h.Service().ApproveVersionAsync(created.Id, v2, Guid.NewGuid(), "0000", default)).Error!.Code.Should().Be("STALE_VERSION");

        string digest2 = MarketplacePublishService.VersionDigest(await h.Db.MarketplaceRecipeVersions.SingleAsync(v => v.N == 2));
        Guid admin = Guid.NewGuid();
        Value(await h.Service().ApproveVersionAsync(created.Id, v2, admin, digest2, default)).Status.Should().Be("listed");
        Value(await h.Service().ApproveVersionAsync(created.Id, v2, admin, digest2, default)).Status.Should().Be("listed");
        MarketplaceRecipe live = await h.Db.MarketplaceRecipes.SingleAsync();
        live.CurrentVersionId.Should().Be(v2);
        live.Name.Should().Be("Second");
        h.Db.MarketplaceAudit.Count(a => a.Action == "version_approved").Should().Be(1);
    }

    [Fact]
    public async Task Changed_content_after_the_queue_row_was_read_is_stale()
    {
        Harness h = new();
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));
        Guid v = await VersionIdAsync(h, 1);
        await h.Service().FinalizeVersionAsync(v, PreviewVerdict.Allow, default);
        MarketplaceRecipeVersion row = await h.Db.MarketplaceRecipeVersions.SingleAsync();
        string before = MarketplacePublishService.VersionDigest(row);
        row.PreviewSha256 = "changed";
        await h.Db.SaveChangesAsync();

        (await h.Service().ApproveVersionAsync(created.Id, v, Guid.NewGuid(), before, default)).Error!.Code.Should().Be("STALE_VERSION");
        Value(await h.Service().RejectVersionAsync(created.Id, v, Guid.NewGuid(), null, default));
        Value(await h.Service().RejectVersionAsync(created.Id, v, Guid.NewGuid(), null, default));
    }

    [Fact]
    public async Task Preparing_drafts_count_against_the_live_cap_and_finalize_rechecks_it()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        h.Options.MaxLivePerOrganization = 1;
        Value(await h.Service().CreateAsync(h.Request(), default));
        (await h.Service().CreateAsync(h.Request(), default)).Error!.Code.Should().Be("LIVE_RECIPE_LIMIT");

        // Both passed the submit check (a race): the cap was 2 when they were created.
        h.Options.MaxLivePerOrganization = 2;
        await SetLabelAsync(h, "second recipe");
        Value(await h.Service().CreateAsync(h.Request(), default));
        h.Options.MaxLivePerOrganization = 1;
        List<MarketplaceRecipeVersion> versions = await h.Db.MarketplaceRecipeVersions.OrderBy(v => v.CreatedAt).ToListAsync();

        (await h.Service().FinalizeVersionAsync(versions[0].Id, PreviewVerdict.Allow, default))!.Reason.Should().Be("live_limit");
        (await h.Service().FinalizeVersionAsync(versions[1].Id, PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Listed);
        (await h.Db.MarketplaceRecipes.CountAsync(r => r.Status == RecipeStatus.Listed)).Should().Be(1);
        (await h.Db.MarketplaceRecipes.CountAsync(r => r.Status == RecipeStatus.Rejected)).Should().Be(1);
        h.Db.MarketplaceAudit.Count(a => a.Action == "version_rejected" && a.DetailJson!.Contains("live_limit")).Should().Be(1);
    }

    // ---------------------------------------------------------------- publish hardening

    private static async Task SetLabelAsync(Harness h, string label)
    {
        WorkflowDefinition wf = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        wf.Steps.OrderBy(x => x.StepOrder).First().Label = label;
        await h.Db.SaveChangesAsync();
    }

    private static void BecomeOtherAuthor(Harness h)
    {
        h.User.UserId = Guid.NewGuid();
        h.User.OrganizationId = Guid.NewGuid();
    }

    private static async Task<ListingResult> SubmitAndFinalizeAsync(Harness h, Guid? recipeId = null)
    {
        PublishedRecipeResponse r = Value(recipeId is { } id
            ? await h.Service().UpdateAsync(id, h.Request(), default)
            : await h.Service().CreateAsync(h.Request(), default));
        MarketplaceRecipeVersion v = await h.Db.MarketplaceRecipeVersions.Where(x => x.RecipeId == r.Id && x.N == r.Version).SingleAsync();
        return (await h.Service().FinalizeVersionAsync(v.Id, PreviewVerdict.Allow, default))!;
    }

    private static List<WorkflowStep> CleanSteps()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().First(t =>
        {
            PortableRecipeResult r = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), SnapshotFixtures.FromTemplate(t), SnapshotFixtures.BuiltIns(), null);
            return r.Recipe is not null;
        });
        return SnapshotFixtures.FromTemplate(template);
    }

    [Fact]
    public void A_condition_round_trips_unchanged_and_a_bad_expression_is_refused_naming_the_step()
    {
        List<WorkflowStep> steps = CleanSteps();
        steps[0].ConditionExpression = "$.score > 8 && $.view.ok <= 3";
        PortableRecipeResult ok = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), steps, SnapshotFixtures.BuiltIns(), null);
        ok.Recipe!.Steps[0].ConditionExpression.Should().Be("$.score > 8 && $.view.ok <= 3");

        foreach (string bad in new[] { "$.a > 1\n; x", "score > '" + new string('x', 2100) + "'", "$.a == 11111111-2222-3333-4444-555555555555", "$.a > 1; drop", "$.a > 1`x`" })
        {
            steps = CleanSteps();
            steps[0].LoopSourceExpression = bad;
            PortableRecipeResult refused = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), steps, SnapshotFixtures.BuiltIns(), null);
            refused.Recipe.Should().BeNull(bad);
            refused.Refusals.Should().Contain(r => r.Code == "EXPRESSION_INVALID" && r.StepOrder == steps[0].StepOrder);
        }
    }

    [Theory]
    [InlineData(11, null)]
    [InlineData(0, null)]
    [InlineData(3, 0)]
    [InlineData(3, 11)]
    public void Loop_bounds_outside_the_range_are_refused_with_the_step_order(int iterations, int? minScore)
    {
        List<WorkflowStep> steps = CleanSteps();
        steps[0].MaxIterations = iterations;
        steps[0].MinScore = minScore;
        PortableRecipeResult r = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), steps, SnapshotFixtures.BuiltIns(), null);
        r.Refusals.Should().Contain(x => x.Code == "LOOP_BOUNDS" && x.StepOrder == steps[0].StepOrder);
    }

    [Fact]
    public void Limits_show_loop_rounds_and_spend_caps()
    {
        PortableStep step = new(1, StepType.ReviewLoop, null, "x", JsonNode.Parse("{\"maxSpendCredits\":5}"), null, null, null, 4, 8, null, null, null, null);
        MarketplacePublishService.LimitsOf(step).Should().Equal("Review loop, up to 4 rounds", "Pass mark 8 out of 10", "Spend limited to 5 credits");
    }

    [Fact]
    public void Malformed_parallel_agent_json_is_a_refusal_not_an_exception()
    {
        List<WorkflowStep> steps = CleanSteps();
        WorkflowStep parallel = SnapshotFixtures.Step(steps.Max(x => x.StepOrder) + 1, StepType.Parallel);
        parallel.ParallelAgentIdsJson = "{not json";
        steps.Add(parallel);
        PortableRecipeResult r = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), steps, SnapshotFixtures.BuiltIns(), null);
        r.Recipe.Should().BeNull();
        r.Refusals.Should().Contain(x => x.Code == "STEP_TYPE_NOT_SHAREABLE" && x.StepOrder == parallel.StepOrder);
    }

    [Theory]
    [InlineData("a11111111-2222-3333-4444-555555555555", true)]
    [InlineData("11111111-2222-3333-4444-5555555555559", true)]
    [InlineData("sk_abcdefghijklmnop1234567", true)]
    [InlineData("r8_abcdefghijklmnop1234567", true)]
    [InlineData("hf_abcdefghijklmnop1234567", true)]
    [InlineData("key_abcdefghijklmnop1234567", true)]
    [InlineData("see Zx9Qp3Lm7Vt2Rk8Wn4Yb6Hc1Jd5Fg0SaQe", true)]
    [InlineData("A calm product shot with soft light and a slow push in", false)]
    [InlineData("$.score > 8", false)]
    public void Leak_guard_refuses_ids_with_hex_neighbours_provider_keys_and_random_tokens(string text, bool refused)
    {
        (SnapshotLeakGuard.Classify(text) is not null).Should().Be(refused, text);
    }

    [Fact]
    public void Manifest_members_are_the_ones_the_sanitiser_builds_field_by_field()
    {
        typeof(AppManifest).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").Should()
            .BeEquivalentTo("Version", "Title", "Tagline", "Category", "SuggestedStylePreset", "Inputs", "Result", "Advanced", "RequestTemplate");
        typeof(AppManifestInput).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").Should()
            .BeEquivalentTo("Kind", "Field", "Role", "Label", "Default", "Choices", "Min", "Max", "Id", "Type", "Help", "Example", "Required", "MaxLength", "Step", "Unit",
                "Options", "More", "ShowWhen", "Capability", "Job", "Allowed", "Recommended", "Agent", "OnLabel", "OffLabel", "LabelKey", "HelpKey", "ExampleKey",
                "Permission");
    }

    [Fact]
    public async Task Quarantined_or_held_projects_and_unclean_files_cannot_be_a_preview()
    {
        Harness h = new();
        Project project = await h.Db.Projects.SingleAsync();
        ProjectFile file = await h.Db.ProjectFiles.SingleAsync();

        async Task ExpectRefusedAsync(string why)
        {
            MarketplaceResult<PublishedRecipeResponse> r = await h.Service().CreateAsync(h.Request(), default);
            r.Error!.Code.Should().Be("PREVIEW_REQUIRED", why);
        }

        project.ContentQuarantinedAt = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();
        await ExpectRefusedAsync("content quarantine");
        project.ContentQuarantinedAt = null;
        project.CsamOutputHoldAt = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();
        await ExpectRefusedAsync("csam hold");
        project.CsamOutputHoldAt = null;
        file.CsamStatus = CsamScanStatus.Pending;
        await h.Db.SaveChangesAsync();
        await ExpectRefusedAsync("csam scan pending");
        file.CsamStatus = CsamScanStatus.Clean;
        file.ModerationStatus = FileModerationStatus.Quarantined;
        await h.Db.SaveChangesAsync();
        await ExpectRefusedAsync("moderation quarantined");
        file.ModerationStatus = FileModerationStatus.Clean;
        await h.Db.SaveChangesAsync();
        Value(await h.Service().CreateAsync(h.Request(), default));
    }

    [Fact]
    public async Task A_submitter_with_a_csam_matched_version_is_refused_at_publish_with_no_hint()
    {
        Harness h = new();
        Guid recipe = Guid.NewGuid();
        Guid version = Guid.NewGuid();
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = recipe, AuthorUserId = Guid.NewGuid(), AuthorOrgId = Guid.NewGuid(), Status = RecipeStatus.Removed, Name = "Old" });
        h.Db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion { Id = version, RecipeId = recipe, N = 1, SubmittedByUserId = h.User.UserId, PreviewStatus = PreviewStatus.Failed });
        h.Db.MarketplaceAudit.Add(new MarketplaceAuditEntry { Id = Guid.NewGuid(), RecipeId = recipe, VersionId = version, Action = "preview_csam_match", ActorKind = "system" });
        await h.Db.SaveChangesAsync();

        (await h.Service().CreateAsync(h.Request(), default)).Error!.Code.Should().Be("PREVIEW_REQUIRED");
    }

    [Fact]
    public async Task Blocked_publishes_are_counted_per_user_per_day_and_then_limited()
    {
        Harness h = new();
        h.Options.MaxBlockedPublishAttemptsPerUserPerDay = 2;
        h.Text.Verdict = TextVerdict.Block;
        (await h.Service().CreateAsync(h.Request(), default)).Error!.Code.Should().Be("TEXT_BLOCKED");
        (await h.Service().CreateAsync(h.Request(), default)).Error!.Code.Should().Be("TEXT_BLOCKED");
        MarketplaceError e = (await h.Service().CreateAsync(h.Request(), default)).Error!;
        e.Status.Should().Be(429);
        e.Code.Should().Be("PUBLISH_ATTEMPTS_LIMITED");
        h.Db.MarketplaceAudit.Count(a => a.Action == "text_blocked").Should().Be(2);

        h.User.UserId = Guid.NewGuid();
        (await h.Service().CreateAsync(h.Request(), default)).Error!.Code.Should().Be("TEXT_BLOCKED");
    }

    [Fact]
    public async Task The_cap_lock_serialises_one_organization_and_not_another()
    {
        Harness h = new();
        MarketplaceCapLock gate = new();
        Guid org = Guid.NewGuid();
        IAsyncDisposable first = await gate.AcquireAsync(h.Db, org, default);
        Task<IAsyncDisposable> second = gate.AcquireAsync(h.Db, org, default);
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("a second publish of the same organization waits for the first to finish");
        await (await gate.AcquireAsync(h.Db, Guid.NewGuid(), default)).DisposeAsync();
        await first.DisposeAsync();
        await (await second).DisposeAsync();
    }

    [Fact]
    public async Task Trust_is_the_submitters_and_a_takedown_or_rejection_revokes_it()
    {
        Harness h = new();
        Guid author = h.User.UserId;
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = Guid.NewGuid(), AuthorUserId = author, AuthorOrgId = Guid.NewGuid(), Status = RecipeStatus.Unpublished, EverListedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        PublishedRecipeResponse created = Value(await h.Service().CreateAsync(h.Request(), default));
        await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default);
        (await h.Db.MarketplaceRecipes.SingleAsync(r => r.Id == created.Id)).Status.Should().Be(RecipeStatus.Listed);

        // A colleague in the same organization submits a new version: their trust counts, not the author's.
        h.User.UserId = Guid.NewGuid();
        Value(await h.Service().UpdateAsync(created.Id, h.Request(), default));
        (await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 2), PreviewVerdict.Allow, default))!.Reason.Should().Be("not_trusted");

        Harness removed = new();
        removed.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = Guid.NewGuid(), AuthorUserId = removed.User.UserId, AuthorOrgId = Guid.NewGuid(), Status = RecipeStatus.Removed, EverListedAt = DateTime.UtcNow });
        await removed.Db.SaveChangesAsync();
        removed.Credits.Plan = "pro";
        (await SubmitAndFinalizeAsync(removed)).Outcome.Should().Be(ListingOutcome.Pending);
    }

    [Fact]
    public async Task After_a_human_rejection_the_author_other_authors_and_same_content_are_handled_as_specified()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        Guid first = Value(await h.Service().CreateAsync(h.Request(), default)).Id;
        await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default);

        // The listed recipe above was auto-listed, so it cannot be rejected by version; remove it by an admin move instead.
        MarketplaceRecipe r1 = await h.Db.MarketplaceRecipes.SingleAsync();
        RecipeStatusMachine.TryTransition(r1, RecipeStatus.Removed, RecipeActor.Admin, DateTime.UtcNow).Should().BeTrue();
        await h.Db.SaveChangesAsync();
        r1.HumanRejectedAt.Should().NotBeNull();

        string original = (await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync()).Steps.OrderBy(x => x.StepOrder).First().Label!;

        // Fresh content from the same (flagged) author: pending.
        await SetLabelAsync(h, "A different first step");
        ListingResult fresh = await SubmitAndFinalizeAsync(h);
        fresh.Outcome.Should().Be(ListingOutcome.Pending);
        fresh.Reason.Should().Be("human_review_required");

        // The rejected content again as a new recipe: digest match.
        await SetLabelAsync(h, original);
        string removedDigest = (await h.Db.MarketplaceRecipeVersions.SingleAsync(v => v.RecipeId == first)).SnapshotDigest!;
        (await h.Db.MarketplaceRecipeVersions.CountAsync(v => v.SnapshotDigest == removedDigest)).Should().Be(1);
        ListingResult same = await SubmitAndFinalizeAsync(h);
        same.Reason.Should().Be("digest_match");

        // A different author with the same content: pending, digest match. An unrelated author with fresh content: listed.
        BecomeOtherAuthor(h);
        (await SubmitAndFinalizeAsync(h)).Reason.Should().Be("digest_match");
        BecomeOtherAuthor(h);
        await SetLabelAsync(h, "Entirely new work");
        (await SubmitAndFinalizeAsync(h)).Outcome.Should().Be(ListingOutcome.Listed);
    }

    [Fact]
    public async Task An_older_finishing_version_is_recorded_and_ignored()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        Guid id = Value(await h.Service().CreateAsync(h.Request(), default)).Id;
        await SetLabelAsync(h, "version two");
        Value(await h.Service().UpdateAsync(id, h.Request(), default));
        Guid v1 = await VersionIdAsync(h, 1);
        Guid v2 = await VersionIdAsync(h, 2);

        (await h.Service().FinalizeVersionAsync(v2, PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Listed);
        (await h.Service().FinalizeVersionAsync(v1, PreviewVerdict.Allow, default))!.Reason.Should().Be("superseded");
        MarketplaceRecipe recipe = await h.Db.MarketplaceRecipes.SingleAsync();
        recipe.CurrentVersionId.Should().Be(v2);
        recipe.Revision.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Owner_dtos_select_none_of_the_private_columns()
    {
        string[] privateColumns = ["PreviewSource", "CheckJson", "SubmittedBy", "SnapshotDigest", "AuthorUser", "AuthorOrg", "OrgId", "Snapshot"];
        Type[] dtos = [.. typeof(MarketplacePublishService).Assembly.GetTypes()
            .Where(t => t.Namespace == "ReelBolt.Inference.Api.Services.Marketplace" && (t.Name.EndsWith("Dto", StringComparison.Ordinal) || t.Name.EndsWith("Response", StringComparison.Ordinal)))];
        dtos.Should().NotBeEmpty();
        foreach (Type t in dtos)
            t.GetProperties().Select(p => p.Name).Where(n => privateColumns.Any(c => n.Contains(c, StringComparison.OrdinalIgnoreCase))).Should().BeEmpty(t.Name);
    }

    // ---------------------------------------------------------------- races (two contexts on one store)

    private static async Task<Guid> CreatePaidDraftAsync(Harness h)
    {
        h.Credits.Plan = "pro";
        Value(await h.Service().CreateAsync(h.Request(), default));
        return await VersionIdAsync(h, 1);
    }

    private static async Task<MarketplaceRecipe?> FreshRecipeAsync(Harness h)
    {
        await using InferenceApiDbContext fresh = new(h.DbOptions);
        return await fresh.MarketplaceRecipes.AsNoTracking().SingleOrDefaultAsync();
    }

    [Theory]
    [InlineData(RecipeStatus.Removed, RecipeActor.Admin)]
    [InlineData(RecipeStatus.Unpublished, RecipeActor.Author)]
    public async Task A_removal_or_unpublish_during_finalize_is_never_overwritten_with_listed(RecipeStatus to, RecipeActor actor)
    {
        Harness h = new();
        Guid v1 = await CreatePaidDraftAsync(h);
        h.Credits.OnRead = async () =>
        {
            await using InferenceApiDbContext other = new(h.DbOptions);
            MarketplaceRecipe r = await other.MarketplaceRecipes.SingleAsync();
            RecipeStatusMachine.TryTransition(r, to, actor, DateTime.UtcNow).Should().BeTrue();
            await other.SaveChangesAsync();
        };

        // The InMemory provider has no transaction, so the version row of the failed first attempt may already be saved and the
        // retry then reports nothing to do; on PostgreSQL the save is atomic and the retry audits version_ignored. Either way the
        // recipe keeps the status the other writer gave it and never lists.
        await h.Service().FinalizeVersionAsync(v1, PreviewVerdict.Allow, default);

        MarketplaceRecipe after = (await FreshRecipeAsync(h))!;
        after.Status.Should().Be(to);
        after.CurrentVersionId.Should().BeNull();
    }

    [Fact]
    public async Task A_purge_deleting_the_recipe_during_finalize_is_not_resurrected_and_does_not_throw()
    {
        Harness h = new();
        Guid v1 = await CreatePaidDraftAsync(h);
        h.Credits.OnRead = async () =>
        {
            await using InferenceApiDbContext other = new(h.DbOptions);
            other.MarketplaceRecipeVersions.RemoveRange(await other.MarketplaceRecipeVersions.ToListAsync());
            other.MarketplaceRecipes.RemoveRange(await other.MarketplaceRecipes.ToListAsync());
            await other.SaveChangesAsync();
        };

        (await h.Service().FinalizeVersionAsync(v1, PreviewVerdict.Allow, default)).Should().BeNull();
        (await FreshRecipeAsync(h)).Should().BeNull();
    }

    [Fact]
    public async Task Two_finalizes_racing_leave_the_highest_version_current()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        Guid id = Value(await h.Service().CreateAsync(h.Request(), default)).Id;
        await SetLabelAsync(h, "version two");
        Value(await h.Service().UpdateAsync(id, h.Request(), default));
        Guid v1 = await VersionIdAsync(h, 1);
        Guid v2 = await VersionIdAsync(h, 2);
        h.Credits.OnRead = async () =>
        {
            await using InferenceApiDbContext other = new(h.DbOptions);
            (await h.Service(other).FinalizeVersionAsync(v2, PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Listed);
        };

        await h.Service().FinalizeVersionAsync(v1, PreviewVerdict.Allow, default);

        (await FreshRecipeAsync(h))!.CurrentVersionId.Should().Be(v2);
    }

    [Fact]
    public async Task Every_modified_recipe_save_bumps_the_revision_and_an_added_one_does_not()
    {
        Harness h = new();
        Value(await h.Service().CreateAsync(h.Request(), default));
        MarketplaceRecipe recipe = await h.Db.MarketplaceRecipes.SingleAsync();
        int start = recipe.Revision;

        await h.Db.SaveChangesAsync();
        recipe.Revision.Should().Be(start, "an unmodified recipe is not bumped");
        recipe.Name = "renamed";
        await h.Db.SaveChangesAsync();
        recipe.Revision.Should().Be(start + 1);
        recipe.Status = RecipeStatus.Pending;
        await h.Db.SaveChangesAsync();
        recipe.Revision.Should().Be(start + 2);

        MarketplaceRecipe added = new() { Id = Guid.NewGuid(), AuthorOrgId = Guid.NewGuid() };
        h.Db.MarketplaceRecipes.Add(added);
        await h.Db.SaveChangesAsync();
        added.Revision.Should().Be(0);
    }

    [Fact]
    public async Task A_stale_owner_write_answers_409_CONCURRENT_UPDATE()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        Guid id = Value(await h.Service().CreateAsync(h.Request(), default)).Id;
        await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default);

        // The service has the recipe tracked, then an admin removes it before the unpublish saves.
        MarketplacePublishService mine = h.Service();
        h.Credits.OnRead = null;
        MarketplaceRecipe tracked = await h.Db.MarketplaceRecipes.SingleAsync();
        await using (InferenceApiDbContext other = new(h.DbOptions))
        {
            MarketplaceRecipe r = await other.MarketplaceRecipes.SingleAsync();
            RecipeStatusMachine.TryTransition(r, RecipeStatus.Removed, RecipeActor.Admin, DateTime.UtcNow);
            await other.SaveChangesAsync();
        }

        tracked.Status.Should().Be(RecipeStatus.Listed, "the tracked copy is stale");
        MarketplaceError e = (await mine.UnpublishAsync(id, default)).Error!;
        e.Code.Should().Be("CONCURRENT_UPDATE");
        e.Status.Should().Be(409);
        (await FreshRecipeAsync(h))!.Status.Should().Be(RecipeStatus.Removed);
    }

    // ---------------------------------------------------------------- publish limits

    private static async Task HoldProjectAsync(Harness h, Guid owner, bool csam)
    {
        h.Db.Projects.Add(new Project
        {
            Id = Guid.NewGuid(),
            Name = "held",
            OwnerId = owner,
            OrganizationId = h.User.OrganizationId!.Value,
            CsamOutputHoldAt = csam ? DateTime.UtcNow : null,
            ContentQuarantinedAt = csam ? null : DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_submitter_with_a_csam_hold_on_their_own_project_cannot_preview_or_publish()
    {
        Harness h = new();
        await HoldProjectAsync(h, h.User.UserId, csam: true);

        (await h.Service().PreviewAsync(h.ProjectId, h.WorkflowId, default)).Error.Should().Be(new MarketplaceError(404));
        MarketplaceError e = (await h.Service().CreateAsync(h.Request(), default)).Error!;
        e.Status.Should().Be(404);
        e.Code.Should().Be("PREVIEW_REQUIRED");
    }

    [Fact]
    public async Task A_csam_quarantined_file_in_the_submitters_project_holds_them_too()
    {
        Harness h = new();
        h.Db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = h.ProjectId, StorageKey = "k", MimeType = "video/mp4", CsamStatus = CsamScanStatus.Quarantined });
        await h.Db.Projects.Where(p => p.Id == h.ProjectId).ForEachAsync(p => p.OwnerId = h.User.UserId);
        await h.Db.SaveChangesAsync();

        (await h.Service().CreateAsync(h.Request(), default)).Error!.Code.Should().Be("PREVIEW_REQUIRED");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_clean_teammate_can_publish_while_another_members_project_is_held_but_the_version_waits_for_a_human(bool csam)
    {
        Harness h = new();
        h.Db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = h.User.OrganizationId!.Value, UserId = h.User.UserId, Role = OrganizationRole.Member });
        await HoldProjectAsync(h, Guid.NewGuid(), csam);
        h.Credits.Plan = "pro";

        Value(await h.Service().PreviewAsync(h.ProjectId, h.WorkflowId, default)).Publishable.Should().BeTrue();
        Value(await h.Service().CreateAsync(h.Request(), default));
        ListingResult result = (await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default))!;
        result.Outcome.Should().Be(ListingOutcome.Pending);
        result.Reason.Should().Be("submitter_org_held");
    }

    [Fact]
    public async Task An_organization_without_any_hold_lists_normally_and_billing_members_are_not_counted()
    {
        Harness h = new();
        h.Db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = h.User.OrganizationId!.Value, UserId = h.User.UserId, Role = OrganizationRole.Member });
        h.Credits.Plan = "pro";
        Value(await h.Service().CreateAsync(h.Request(), default));
        (await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Listed);

        Harness billing = new();
        billing.Db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = billing.User.OrganizationId!.Value, UserId = billing.User.UserId, Role = OrganizationRole.Billing });
        await HoldProjectAsync(billing, Guid.NewGuid(), csam: true);
        billing.Credits.Plan = "pro";
        Value(await billing.Service().CreateAsync(billing.Request(), default));
        (await billing.Service().FinalizeVersionAsync(await VersionIdAsync(billing, 1), PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Listed);
    }

    [Fact]
    public async Task A_plain_moderation_quarantine_on_the_submitters_own_project_only_routes_to_a_human()
    {
        Harness h = new();
        await HoldProjectAsync(h, h.User.UserId, csam: false);
        h.Credits.Plan = "pro";

        Value(await h.Service().CreateAsync(h.Request(), default));
        ListingResult result = (await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default))!;
        result.Outcome.Should().Be(ListingOutcome.Pending);
        result.Reason.Should().Be("submitter_held");
    }

    [Fact]
    public async Task A_csam_hold_that_appears_before_finalize_sends_a_paid_trusted_submitter_to_a_human()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        Value(await h.Service().CreateAsync(h.Request(), default));
        await HoldProjectAsync(h, h.User.UserId, csam: true);

        ListingResult result = (await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default))!;
        result.Outcome.Should().Be(ListingOutcome.Pending);
        result.Reason.Should().Be("submitter_held");
    }

    [Fact]
    public async Task Content_hidden_by_reports_flags_the_author_and_matches_by_digest()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = Guid.NewGuid(), AuthorUserId = h.User.UserId, AuthorOrgId = Guid.NewGuid(), Status = RecipeStatus.Listed, HiddenByReports = true });
        await h.Db.SaveChangesAsync();
        (await SubmitAndFinalizeAsync(h)).Outcome.Should().Be(ListingOutcome.Pending);

        Harness d = new();
        d.Credits.Plan = "pro";
        Value(await d.Service().CreateAsync(d.Request(), default));
        MarketplaceRecipeVersion mine = await d.Db.MarketplaceRecipeVersions.SingleAsync();
        MarketplaceRecipe hidden = new() { Id = Guid.NewGuid(), AuthorUserId = Guid.NewGuid(), AuthorOrgId = Guid.NewGuid(), Status = RecipeStatus.Listed, HiddenByReports = true };
        d.Db.MarketplaceRecipes.Add(hidden);
        d.Db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion { Id = Guid.NewGuid(), RecipeId = hidden.Id, N = 1, SnapshotDigest = mine.SnapshotDigest });
        await d.Db.SaveChangesAsync();
        (await d.Service().FinalizeVersionAsync(mine.Id, PreviewVerdict.Allow, default))!.Reason.Should().Be("digest_match");
    }

    [Fact]
    public async Task Unpublishing_a_pending_recipe_and_resubmitting_the_same_content_waits_for_a_human_again()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        h.Text.Verdict = TextVerdict.Review;
        Guid id = Value(await h.Service().CreateAsync(h.Request(), default)).Id;
        (await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 1), PreviewVerdict.Allow, default))!.Outcome.Should().Be(ListingOutcome.Pending);
        Value(await h.Service().UnpublishAsync(id, default));

        h.Text.Verdict = TextVerdict.Allow;
        Value(await h.Service().UpdateAsync(id, h.Request(), default));
        ListingResult again = (await h.Service().FinalizeVersionAsync(await VersionIdAsync(h, 2), PreviewVerdict.Allow, default))!;
        again.Outcome.Should().Be(ListingOutcome.Pending);
        again.Reason.Should().Be("digest_match");
    }

    [Theory]
    [InlineData("Ada ada@example.com")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public async Task Name_description_and_label_are_leak_scanned(string text)
    {
        Harness h = new();
        (await h.Service().CreateAsync(h.Request(name: text), default)).Error!.Code.Should().Be("INVALID_FIELD");
        (await h.Service().CreateAsync(h.Request(label: text), default)).Error!.Code.Should().Be("INVALID_FIELD");
        (await h.Service().CreateAsync(h.Request() with { Description = text }, default)).Error!.Code.Should().Be("INVALID_FIELD");
    }

    [Theory]
    [InlineData("write to someone@example.com", true)]
    [InlineData("cdn.example.com/video.mp4?token=abcdefgh12345678", true)]
    [InlineData("https://example.com/x?sig=abcdefgh12345678", true)]
    [InlineData("a plain sentence about a product", false)]
    public void Leak_guard_finds_emails_and_credential_links_without_a_scheme(string text, bool refused)
    {
        (SnapshotLeakGuard.Classify(text) is not null).Should().Be(refused, text);
    }

    [Fact]
    public void An_undefined_integer_step_type_in_a_snapshot_is_unreadable_not_an_exception()
    {
        PortableRecipeJson.TryParse("{\"schemaVersion\":1,\"steps\":[{\"order\":1,\"stepType\":99,\"maxIterations\":3}],\"slots\":[],\"removed\":[]}").Should().BeNull();
        PortableRecipeJson.TryParse("{\"schemaVersion\":1,\"steps\":[{\"order\":1,\"stepType\":\"Agent\",\"maxIterations\":3}],\"slots\":[],\"removed\":[]}").Should().NotBeNull();
    }

    [Fact]
    public void A_numeric_run_bound_source_kind_is_refused_like_the_name()
    {
        WalkResult walk = new();
        ConfigWalker.Walk(JsonNode.Parse("{\"source\":{\"kind\":" + (int)VideoSourceKind.StepResult + "}}"), typeof(VideoAnalyzeStepConfig), 1, walk);
        walk.Refusals.Should().Contain(r => r.Code == "RUN_BOUND_SOURCE");
    }

    [Fact]
    public void Kept_strings_are_listed_for_the_content_check_and_kept_maps_are_capped()
    {
        WalkResult walk = new();
        JsonNode? cleaned = ConfigWalker.Walk(JsonNode.Parse("{\"outputFileName\":\"final cut.mp4\"}"), typeof(VideoCompileStepConfig), 1, walk);
        cleaned!["outputFileName"]!.GetValue<string>().Should().Be("final cut.mp4");
        walk.Texts.Should().Contain(t => t.Text == "final cut.mp4");
    }

    // ---------------------------------------------------------------- digest re-roll guard (global)

    private static async Task<ListingResult> NewRecipeSameContentAsync(Harness h)
    {
        BecomeOtherAuthor(h);
        h.Credits.Plan = "pro";
        return await SubmitAndFinalizeAsync(h);
    }

    [Fact]
    public async Task The_same_content_as_a_new_recipe_after_pending_rejected_or_superseded_goes_to_a_human()
    {
        // after pending
        Harness pending = new();
        pending.Text.Verdict = TextVerdict.Review;
        Value(await pending.Service().CreateAsync(pending.Request(), default));
        await pending.Service().FinalizeVersionAsync(await VersionIdAsync(pending, 1), PreviewVerdict.Allow, default);
        pending.Text.Verdict = TextVerdict.Allow;
        ListingResult afterPending = await NewRecipeSameContentAsync(pending);
        afterPending.Outcome.Should().Be(ListingOutcome.Pending);
        afterPending.Reason.Should().Be("digest_match");

        // after an automatic rejection
        Harness rejected = new();
        Value(await rejected.Service().CreateAsync(rejected.Request(), default));
        await rejected.Service().FinalizeVersionAsync(await VersionIdAsync(rejected, 1), PreviewVerdict.Block, default);
        (await NewRecipeSameContentAsync(rejected)).Reason.Should().Be("digest_match");

        // after the earlier version was superseded
        Harness superseded = new();
        string original = (await superseded.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync()).Steps.OrderBy(x => x.StepOrder).First().Label!;
        Guid id = Value(await superseded.Service().CreateAsync(superseded.Request(), default)).Id;
        await SetLabelAsync(superseded, "changed");
        Value(await superseded.Service().UpdateAsync(id, superseded.Request(), default));
        (await superseded.Service().FinalizeVersionAsync(await VersionIdAsync(superseded, 1), PreviewVerdict.Allow, default))!.Reason.Should().Be("superseded");
        await SetLabelAsync(superseded, original);
        (await NewRecipeSameContentAsync(superseded)).Reason.Should().Be("digest_match");
    }

    [Fact]
    public async Task The_same_content_while_an_earlier_submission_is_still_preparing_is_held_and_costs_no_moderation_call()
    {
        Harness h = new();
        Value(await h.Service().CreateAsync(h.Request(), default));
        int blockedBefore = h.Db.MarketplaceAudit.Count();

        MarketplaceError e = (await h.Service().CreateAsync(h.Request(), default)).Error!;
        e.Status.Should().Be(409);
        e.Code.Should().Be("DUPLICATE_SUBMISSION");
        h.Db.MarketplaceAudit.Count().Should().Be(blockedBefore);
        (await h.Db.MarketplaceRecipes.CountAsync()).Should().Be(1);

        // Another submitter's identical content is not held at submit; it is sent to a human when it finishes.
        BecomeOtherAuthor(h);
        h.Credits.Plan = "pro";
        Guid other = Value(await h.Service().CreateAsync(h.Request(), default)).Id;
        MarketplaceRecipeVersion otherVersion = await h.Db.MarketplaceRecipeVersions.SingleAsync(v => v.RecipeId == other);
        ListingResult result = (await h.Service().FinalizeVersionAsync(otherVersion.Id, PreviewVerdict.Allow, default))!;
        result.Reason.Should().Be("digest_match");
    }

    [Fact]
    public async Task Unrelated_content_is_unaffected_and_the_matched_ids_stay_out_of_every_owner_dto()
    {
        Harness h = new();
        h.Text.Verdict = TextVerdict.Review;
        Guid first = Value(await h.Service().CreateAsync(h.Request(), default)).Id;
        Guid firstVersion = await VersionIdAsync(h, 1);
        await h.Service().FinalizeVersionAsync(firstVersion, PreviewVerdict.Allow, default);
        h.Text.Verdict = TextVerdict.Allow;

        // Same content: matched, and the ids are recorded in check_json only.
        BecomeOtherAuthor(h);
        h.Credits.Plan = "pro";
        PublishedRecipeResponse same = Value(await h.Service().CreateAsync(h.Request(), default));
        MarketplaceRecipeVersion sameVersion = await h.Db.MarketplaceRecipeVersions.SingleAsync(v => v.RecipeId == same.Id);
        (await h.Service().FinalizeVersionAsync(sameVersion.Id, PreviewVerdict.Allow, default))!.Reason.Should().Be("digest_match");
        string check = (await h.Db.MarketplaceRecipeVersions.AsNoTracking().SingleAsync(v => v.Id == sameVersion.Id)).CheckJson!;
        check.Should().Contain(first.ToString()).And.Contain(firstVersion.ToString());
        string mine = JsonSerializer.Serialize(Value(await h.Service().MineAsync(default)));
        mine.Should().NotContain(first.ToString()).And.NotContain(firstVersion.ToString());

        // Unrelated content lists.
        await SetLabelAsync(h, "Completely unrelated");
        BecomeOtherAuthor(h);
        h.Credits.Plan = "pro";
        (await SubmitAndFinalizeAsync(h)).Outcome.Should().Be(ListingOutcome.Listed);
    }

    // ---------------------------------------------------------------- author-visible reasons

    private static string SourceOf(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "inference", "src", "ReelBolt.Inference.Api", relative))
               && !System.IO.File.Exists(System.IO.Path.Combine(dir, "src", "ReelBolt.Inference.Api", relative)))
            dir = System.IO.Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        string a = System.IO.Path.Combine(dir!, "inference", "src", "ReelBolt.Inference.Api", relative);
        return System.IO.File.ReadAllText(System.IO.File.Exists(a) ? a : System.IO.Path.Combine(dir!, "src", "ReelBolt.Inference.Api", relative));
    }

    [Theory]
    [InlineData("text_blocked", "content")]
    [InlineData("content", "content")]
    [InlineData("preview_failed", "preview")]
    [InlineData("preview_blocked", "preview")]
    [InlineData("preview", "preview")]
    [InlineData("copyright", "copyright")]
    [InlineData("quality", "quality")]
    [InlineData("low_quality", "quality")]
    [InlineData("submitter_held", "policy")]
    [InlineData("submitter_org_held", "policy")]
    [InlineData("digest_match", "policy")]
    [InlineData("safety", "policy")]
    [InlineData("csam_match", "policy")]
    [InlineData("live_limit", "policy")]
    [InlineData("something nobody wrote", "policy")]
    public void Internal_reasons_map_to_the_closed_public_set(string reason, string expected)
    {
        MarketplaceAuthorReasons.ToPublicCategory(reason).Should().Be(expected);
        MarketplaceAuthorReasons.PublicCategories.Should().Contain(expected);
    }

    [Fact]
    public void Null_stays_null_and_no_output_is_outside_the_closed_set()
    {
        MarketplaceAuthorReasons.ToPublicCategory(null).Should().BeNull();
        MarketplaceAuthorReasons.ToPublicCategory("  ").Should().BeNull();
        foreach (string reason in ListingDecision.Reasons)
            MarketplaceAuthorReasons.PublicCategories.Should().Contain(MarketplaceAuthorReasons.ToPublicCategory(reason));
    }

    [Fact]
    public void The_reason_list_next_to_the_decision_is_complete()
    {
        System.Text.RegularExpressions.Regex literal = new("new\\(ListingOutcome\\.\\w+, \"([a-z_]+)\"\\)");
        string[] fromDecision = [.. literal.Matches(SourceOf("Services/Marketplace/ListingDecision.cs")).Select(m => m.Groups[1].Value).Distinct()];
        fromDecision.Should().NotBeEmpty();
        ListingDecision.Reasons.Should().Contain(fromDecision);

        string service = SourceOf("Services/Marketplace/MarketplacePublishService.cs");
        System.Text.RegularExpressions.Regex serviceLiteral = new("new ListingResult\\(ListingOutcome\\.\\w+, \"([a-z_]+)\"\\)");
        string[] fromService = [.. serviceLiteral.Matches(service).Select(m => m.Groups[1].Value).Distinct()];
        string[] knownServiceReasons = ["awaiting_review", "live_limit", "recipe_not_active", "superseded"];
        knownServiceReasons.Should().Contain(fromService);
    }

    [Fact]
    public async Task Mine_shows_only_a_public_category_and_only_for_rejected_or_removed_recipes()
    {
        Harness h = new();
        Guid orgId = h.User.OrganizationId!.Value;
        MarketplaceRecipe Recipe(RecipeStatus status) => new() { Id = Guid.NewGuid(), AuthorOrgId = orgId, Status = status, Name = status.ToString() };
        MarketplaceRecipe[] recipes = [.. Enum.GetValues<RecipeStatus>().Select(Recipe)];
        h.Db.MarketplaceRecipes.AddRange(recipes);
        string[] reasons = [.. ListingDecision.Reasons, "live_limit", "safety", "csam_match", "admin_rejected"];
        int i = 0;
        foreach (MarketplaceRecipe r in recipes)
            h.Db.MarketplaceAudit.Add(new MarketplaceAuditEntry { Id = Guid.NewGuid(), RecipeId = r.Id, Action = "x", DetailJson = "{\"reason\":\"" + reasons[i++ % reasons.Length] + "\"}" });
        await h.Db.SaveChangesAsync();

        MineResponse mine = Value(await h.Service().MineAsync(default));
        foreach (MineRecipeDto item in mine.Items)
        {
            if (item.Status is "rejected" or "removed")
                MarketplaceAuthorReasons.PublicCategories.Should().Contain(item.ReasonCategory);
            else
                item.ReasonCategory.Should().BeNull(item.Status);
        }

        string json = JsonSerializer.Serialize(mine);
        foreach (string internalReason in reasons.Where(r => r is not ("text_blocked" or "preview_blocked" or "preview_failed")))
            json.Should().NotContain(internalReason);
    }

    // ---------------------------------------------------------------- no identifiers

    [Fact]
    public async Task No_response_carries_an_author_project_workflow_file_or_storage_identifier()
    {
        Harness h = new();
        h.Credits.Plan = "pro";
        MarketplacePublishService svc = h.Service();
        PublishPreviewResponse preview = Value(await svc.PreviewAsync(h.ProjectId, h.WorkflowId, default));
        PublishedRecipeResponse created = Value(await svc.CreateAsync(h.Request(), default));
        await svc.FinalizeVersionAsync(h.Db.MarketplaceRecipeVersions.Single().Id, PreviewVerdict.Allow, default);
        PublishedRecipeResponse updated = Value(await svc.UpdateAsync(created.Id, h.Request(), default));
        MineResponse mine = Value(await svc.MineAsync(default));
        PublishedRecipeResponse unpublished = Value(await svc.UnpublishAsync(created.Id, default));
        ConsentResponse consent = svc.GetConsent();

        MarketplaceRecipe row = await h.Db.MarketplaceRecipes.SingleAsync();
        string[] planted =
        [
            row.AuthorUserId.ToString(), row.AuthorOrgId.ToString(), h.User.UserId.ToString(), h.User.OrganizationId!.Value.ToString(),
            h.ProjectId.ToString(), h.WorkflowId.ToString(), h.FileId.ToString(), "private/author"
        ];
        string all = string.Join('\n', new object[] { preview, created, updated, mine, unpublished, consent }.Select(o => JsonSerializer.Serialize(o, o.GetType())));
        foreach (string id in planted)
            all.Should().NotContainEquivalentOf(id);

        // The DTO shapes themselves carry no property that could hold one.
        string[] forbidden = ["author", "project", "workflow", "provider", "file", "voice", "stepresult", "storage", "org"];
        foreach (Type t in new[] { typeof(PublishPreviewResponse), typeof(PublishedRecipeResponse), typeof(MineRecipeDto), typeof(MineVersionDto), typeof(MineResponse),
                     typeof(ConsentResponse), typeof(RefusalDto), typeof(RemovedDto), typeof(SlotDto), typeof(StepDto), typeof(PromptDto) })
        {
            t.GetProperties().Select(p => p.Name.ToLowerInvariant())
                .Where(n => forbidden.Any(n.Contains)).Should().BeEmpty(t.Name);
        }
    }
}
