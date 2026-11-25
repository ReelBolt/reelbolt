using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Tenancy;

/// <summary>
/// WP A14, structural half: <b>which tables the tenancy boundary is actually drawn on, and where it
/// is drawn on everything else.</b>
///
/// <para>
/// The plan's A14 acceptance criterion is that a new controller action added without an access check
/// fails a test rather than a review. Two things have to be true for that to be possible: the set of
/// tables carrying the tenant filter must be a knowable, pinned inventory (not folklore), and every
/// project-scoped controller action must be shown to consult
/// <see cref="ReelBolt.Inference.Api.Services.Auth.IProjectAccessService"/>. This file pins both, and
/// deliberately pins the INVERSE too — the owned tables that have no tenant filter, because asserting
/// a filter that does not exist would be a lie that hides the real guard.
/// </para>
///
/// <para>
/// Behavioural coverage lives next door: <c>CrossTenantControllerMatrixTests</c> drives the surfaces,
/// <c>CrossTenantResolverTests</c> drives provider resolution, <c>CrossTenantEngineTests</c> drives the
/// engine's own store. Existing coverage this file deliberately does NOT restate:
/// <c>ProjectAccessServiceTests.TenantFilters_ScopeToTheActiveOrganization_...</c> (the per-table
/// behaviour of five of the six filters) and
/// <c>ProjectAccessServiceTests.A7aControllers_DoNotCompareTheOwnerToTheCaller</c> (the source guard
/// on the six A7a controllers).
/// </para>
/// </summary>
public class TenantBoundaryInventoryTests
{
    // ---------------------------------------------------------------------------------------------------
    // 1. The EF tenant query-filter inventory
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every entity type that carries a query filter named
    /// <see cref="InferenceApiDbContext.TenantFilterName"/>, read off the REAL model — not off a list,
    /// and not off a source grep, so a filter added or dropped anywhere in
    /// <c>InferenceApiDbContext.OnModelCreating</c> is visible here immediately.
    /// </summary>
    private static IReadOnlyList<Type> TenantFilteredEntityTypes()
    {
        // A system-scoped tenant, so building the model never depends on ambient state.
        using var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"tenancy-inventory-{Guid.NewGuid():N}")
                .Options,
            new TestTenancy.MutableTenant());

        return db.Model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters()
                .Any(filter => filter.Key == InferenceApiDbContext.TenantFilterName))
            .Select(e => e.ClrType)
            .Distinct()
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The reviewed inventory. Five of these are the plan's own list (project, project file, agent
    /// definition, assistant thread, inference provider); the sixth is F10's storage-retention mark,
    /// which is organization-owned and therefore filtered too — reporting five here would be wrong.
    ///
    /// <para>
    /// The test is exact in BOTH directions on purpose. A dropped filter is a leak; an added filter
    /// is a behaviour change nobody asked for (for example filtering <c>AssistantMessage</c> by an
    /// organization column it does not have would not compile, but filtering a platform-shared table
    /// would). Either way the author is expected to come here and say so.
    /// </para>
    /// </summary>
    [Fact]
    public void Tenant_query_filter_inventory_is_exactly_the_reviewed_set()
    {
        Type[] expected =
        [
            typeof(AgentDefinition),
            // Public API keys (docs: public-api initiative): organization-owned and addressed by id on the dashboard
            // routes. The ApiKey auth handler ignores ONLY this filter, because the key is what establishes the org.
            typeof(ApiKey),
            typeof(AssistantThread),
            // Workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library"): a library item is
            // addressed by a caller-chosen id on /api/v1/avatar-library/{itemId} and owned by ONE organization, so unlike
            // the project-scoped avatar tables it carries the filter. Its media rows are children (listed below).
            typeof(AvatarLibraryItem),
            // Guest review links (Wave 4, docs/design/guest-review-links.md): addressable by a caller-chosen link id on
            // the owner routes, owned by one organization (denormalised organization_id), so it carries the filter;
            // the public routes ignore ONLY this filter and look the row up by token hash.
            typeof(EditReviewLink),
            // Deliberate P4-03 (product design system, SPEC 16.1): a design-system library is
            // addressed by its own id and owned by one organization, so unlike the design_systems
            // children it carries the tenant filter. This is the initiative that added it; the
            // child-table theory below explains why design_systems itself still does not.
            typeof(DesignSystemLibrary),
            typeof(InferenceProvider),
            // The model catalog (docs/design/model-catalog.md §3): models and capabilities are addressable
            // by caller-chosen ids like their provider; prices and pricing settings carry no org.
            typeof(InferenceModel),
            typeof(ModelCapability),
            typeof(Project),
            typeof(ProjectFile),
            // Project organization: tags and folders are organization-owned and addressed by id on the owner routes.
            typeof(ProjectFolder),
            typeof(ProjectTag),
            typeof(StorageRetentionMark),
            // Social publishing (docs/social-publishing.md): a connected account and a post are addressed by
            // caller-chosen ids on the owner routes and owned by one organization. Posts' metrics and the OAuth
            // tickets are reached through their parent (listed below).
            typeof(SocialAccount),
            typeof(SocialPost),
            // Webhook endpoints (public-api T-009): organization-owned and addressed by id on the public and dashboard
            // webhook routes. Their deliveries are children reached only through the endpoint.
            typeof(ReelBolt.Inference.Api.Data.WebhookEndpoint)
        ];

        TenantFilteredEntityTypes().Should().BeEquivalentTo(expected,
            "these are the tables the tenancy boundary is drawn on with an EF query filter; a change "
            + "here is a change to which data is invisible without an explicit check, so it must be a "
            + "deliberate edit to this list and not a silent side effect");
    }

    // ---------------------------------------------------------------------------------------------------
    // 2. The owned tables that have NO tenant filter — and where their guard actually is
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Owned tables that carry no tenant filter because they are reached <b>through a parent</b> that
    /// does: a message through its thread, an op or snapshot through its timeline, a derivative or a
    /// voice through its project. Asserting a filter on these would be asserting something that does
    /// not exist; what is asserted instead is (a) that they really are unfiltered, and (b) that the
    /// parent they hang off IS filtered, which is what makes the guard real.
    /// </summary>
    [Theory]
    [InlineData(typeof(AssistantMessage))]
    // assistant_run_watches: reached only through its thread (thread_id FK, cascade), which is tenant-filtered.
    [InlineData(typeof(AssistantRunWatch))]
    // edit_review_decisions: append-only, reached only through its link (link_id FK, cascade), which is tenant-filtered.
    [InlineData(typeof(EditReviewDecision))]
    [InlineData(typeof(EditTimelineOp))]
    [InlineData(typeof(EditTimelineSnapshot))]
    [InlineData(typeof(MediaDerivative))]
    [InlineData(typeof(ProjectVoice))]
    // Avatars (docs/design/avatars.md "Tenancy", AV-1 project scope): project children reached only by
    // /projects/{projectId}/avatars/... routes that load the project through IProjectAccessService first and then
    // query WHERE project_id = @p, exactly like ProjectVoice; references and vendor assets hang off the avatar, so the
    // guard is the filtered project one or two FKs up. An org-level avatar library would be caller-addressable by its
    // own id and would have to carry the filter instead.
    [InlineData(typeof(ProjectAvatar))]
    [InlineData(typeof(ProjectAvatarReference))]
    [InlineData(typeof(ProjectAvatarVendorAsset))]
    // project_avatar_events: append-only audit with no FKs (rows outlive the avatar and project); never addressable by
    // a caller id, written by the avatar routes and the retention sweep, read only through an authorized project.
    [InlineData(typeof(ProjectAvatarEvent))]
    // avatar_library_references (workspace avatar library): the library's own copies of an item's media, children of
    // their item (library_item_id FK, cascade), which carries the Tenant filter. No route addresses a row without first
    // loading its item in the caller's organization, and no response carries its storage key.
    [InlineData(typeof(AvatarLibraryReference))]
    // avatar_build_jobs (docs/design/avatar-objects.md "Avatar variants"): a project child (project_id FK, cascade)
    // reached only by /projects/{projectId}/avatars/builds/... routes that load the project through
    // IProjectAccessService first and then query WHERE project_id = @p; the worker reads it by the id it queued.
    [InlineData(typeof(AvatarBuildJob))]
    // project_tag_links: reached through its project (filtered) and its tag (filtered); writes verify both.
    [InlineData(typeof(ProjectTagLink))]
    [InlineData(typeof(ProjectPronunciationEntry))]
    // Saved post texts: project children (project_id FK, cascade) reached only through the project-authorized post-text routes.
    [InlineData(typeof(ReelBolt.Inference.Api.Services.PostTexts.PostTextRow))]
    // Project versioning (contract section 2): the four history tables have no tenant filter because
    // each is reached only through an authorized project. The routes load by project_id after
    // IProjectAccessService, and capture/erase/GC query by a project id they already own; a filter
    // would need the same join back to projects and would still not guard a caller-chosen id.
    [InlineData(typeof(ProjectVersion))]
    [InlineData(typeof(ProjectVersionItem))]
    [InlineData(typeof(ProjectHistoryBlob))]
    [InlineData(typeof(ProjectHistoryDocument))]
    // Stored publish verdicts: project children (project_id FK, cascade) reached only through the
    // project-authorized publish routes and the background moderation worker, which has no request org.
    [InlineData(typeof(ReelBolt.Inference.Api.Services.Moderation.OutputModerationVerdict))]
    // Design systems and their children carry no filter of their own. Project rows are reached only
    // through an authorized project (design_systems.project_id). Library CONTAINER rows
    // (design_systems with project_id NULL) have no project parent, so they are NOT guarded by a parent
    // here: DesignSystemService.FindScopedSystemAsync proves the container belongs to a
    // design_system_libraries row of the scope's organization (that table carries the named Tenant
    // filter) and OrgLibraryTenancyTests / DesignSystemCrossTenantMatrixTests pin it.
    [InlineData(typeof(DesignSystem))]
    [InlineData(typeof(DesignSystemVersion))]
    [InlineData(typeof(DesignSystemSource))]
    [InlineData(typeof(DesignSystemChangeProposal))]
    // Library-owned brand assets: reached only through their <c>design_system_libraries</c> row, which
    // carries the named "Tenant" filter. The asset table has no organization column of its own — the
    // org is proven one FK up, by the library lookup every route performs before touching an asset.
    [InlineData(typeof(DesignSystemLibraryAsset))]
    // Built-in sound-effects library: platform content (no organization column), the same rows for every
    // project; every route is authenticated and returns no storage key.
    [InlineData(typeof(SfxLibraryItem))]
    [InlineData(typeof(ProjectGitHubLink))]
    // Composition workspaces, their versions and revision requests: project children reached only
    // through CompositionRevisionsController's project check (and by the engine, which has no tenant
    // filter at all); OrganizationId on two of them is denormalized for metering, never a guard.
    [InlineData(typeof(CompositionWorkspace))]
    [InlineData(typeof(CompositionWorkspaceVersion))]
    [InlineData(typeof(CompositionRevision))]
    // Step change requests: project children like CompositionRevision, reached only through the
    // project-authorized change-request routes; OrganizationId is denormalized, never a guard.
    [InlineData(typeof(StepChangeRequest))]
    // Designed-graphic requests: project children reached only through EditImageGraphicsController's
    // project check, then constrained to the document; OrganizationId is denormalized, never a guard.
    [InlineData(typeof(EditImageGraphicRequest))]
    // Cloud drive links: project children like ProjectGitHubLink, reached only through
    // ProjectCloudLinksController's project check (or the background sync under a system scope);
    // OrganizationId is audit only, never a guard.
    [InlineData(typeof(ProjectCloudLink))]
    // Cloud drive connections and their OAuth tickets: owner-scoped rather than tenant-filtered, like
    // GitHubAccountLink. Every read is keyed on the caller's own (user id, active organization) pair
    // as well as the id, and the background sync must find a link's connection under a system scope.
    [InlineData(typeof(CloudStorageConnection))]
    [InlineData(typeof(CloudStorageOAuthState))]
    // Social publishing: the OAuth ticket is single-use, hashed and bound to the user and organization that
    // started it (SocialConnectionService); a metric row is reached only through its (tenant-filtered) post.
    [InlineData(typeof(SocialOAuthState))]
    [InlineData(typeof(SocialPostMetric))]
    // The publish ledger is counted by organization id inside the publish engine and never addressed by a caller.
    [InlineData(typeof(SocialPublishLedgerEntry))]
    // Model capability prices: children of a model capability (which IS filtered), reached only through
    // platform-admin endpoints or an authorized parent capability (docs/design/model-catalog.md §3).
    [InlineData(typeof(ModelCapabilityPrice))]
    // run_rate_events (the hourly assistant/MCP run-limit ledger): no tenant filter and no parent to reach it
    // through — it is never addressable by a caller. Rows are written and counted only by
    // AssistantExecutionGuard, always keyed on the authenticated (user id, project id) pair the caller was
    // already authorized for; no controller or tool exposes the table or an id of a row, and the account
    // purge deletes a user's rows.
    [InlineData(typeof(RunRateEvent))]
    // api_run_requests (public API run starts the hourly limit held back, public-api T-007): reached only through the API
    // key that wrote it (api_key_id FK, cascade) by the key-authenticated run-request routes, which also require access to
    // the request's project, and by the queue dispatcher, which re-reads the key's authority; never addressable otherwise.
    [InlineData(typeof(ReelBolt.Inference.Api.Data.ApiRunRequest))]
    // webhook_deliveries (public-api T-009): children of their endpoint (endpoint_id FK, cascade), which carries the Tenant
    // filter; reached only through the endpoint routes (which look the endpoint up in the caller's organization first)
    // and by the delivery worker and run-event consumer under a system scope. OrganizationId is denormalized, never a guard.
    [InlineData(typeof(ReelBolt.Inference.Api.Data.WebhookDelivery))]
    // product_events (the append-only product funnel, docs/product-events.md): no tenant filter and no
    // parent to reach it through. It is keyed by person and/or organization and is never addressable by
    // a caller through any route — the same rule that keeps alpha_funnel_claims unfiltered. A tenant
    // filter would also make the table lie: a server-side row may carry an organization the reading
    // scope has no membership in, and the BI extract reads it with no tenant at all.
    [InlineData(typeof(ReelBolt.Inference.Api.Services.Telemetry.ProductEvent))]
    // admin_audit_events (platform-admin user-account audit, platform-admin-access-incident T-004): platform-wide and
    // append-only, written by the Go API by raw SQL for every admin user write; it belongs to no organization and no
    // tenant route addresses it, so a tenant filter would only hide rows from the platform-admin reader.
    [InlineData(typeof(ReelBolt.Inference.Api.Data.AdminAuditEvent))]
    // user_attribution (first-touch acquisition source, one row per user, product-analytics): keyed by the
    // person, written once at signup by the Go API, never addressable through any route, read only by BI.
    [InlineData(typeof(ReelBolt.Inference.Api.Services.Telemetry.UserAttribution))]
    // user_onboarding_states (first-run tour + checklist state, one row per user): keyed by the JWT sub and
    // only ever read or written for that user by OnboardingController, never per organization.
    [InlineData(typeof(UserOnboardingState))]
    // The new-user funnel tables (docs/design/new-user-funnel.md section 5) are platform-wide or keyed by a
    // person or an anonymous visitor; none belongs to an organization, so none carries the Tenant filter.
    // - marketing_links: the tracked-link registry. Platform-wide; written and listed only by the platform-admin
    //   links routes (IsPlatformAdmin first), and read by code (never by id) by the anonymous redirect.
    // - marketing_link_daily_stats: a click counter per link and UTC day, child of its link (link_id FK, cascade);
    //   no visitor, IP address or user agent, and no route addresses a row.
    // - visitor_attribution: first-touch source of a consented anonymous visitor, keyed by a random visitor id;
    //   there is no account or organization yet, it is written by the visit beacon and read only by BI and the
    //   platform-admin funnel (as a count).
    // - user_onboarding_profiles: one row per user (user_id FK, cascade), read and written only for the JWT sub by
    //   the onboarding routes, never per organization; the same rule as user_onboarding_states.
    [InlineData(typeof(ReelBolt.Shared.Data.Models.Marketing.MarketingLink))]
    [InlineData(typeof(ReelBolt.Shared.Data.Models.Marketing.MarketingLinkDailyStat))]
    [InlineData(typeof(ReelBolt.Inference.Api.Services.Telemetry.VisitorAttribution))]
    [InlineData(typeof(UserOnboardingProfile))]
    // csp_violation_counts (security-headers T-003): an anonymous daily aggregate of browser CSP/COEP violation reports, keyed by
    // closed tokens, a scheme+host and a sanitized path; no person, organization or tenant, written by the anonymous
    // csp-report beacon and read only by BI.
    [InlineData(typeof(ReelBolt.Inference.Api.Services.Telemetry.CspViolationCount))]
    // user_video_spend_limits: one row per user (user_id FK, cascade), read and written only for the JWT sub by
    // VideoSpendLimitController; the engine gate reads the run owner's row. Never per organization.
    [InlineData(typeof(UserVideoSpendLimit))]
    // object_effect_requests (magic blur / mask): a project child (project_id FK, cascade) reached only through the
    // project-authorized object-analysis routes; OrganizationId is denormalized, never a guard. ExecutionId has no FK.
    [InlineData(typeof(ObjectEffectRequest))]
    // project_archive_unpacks (zip upload unpacking): a project child (project_id FK, cascade) reached only through the
    // project-authorized unpack/list routes and the background unpack worker under a system scope; no by-id route.
    // OrganizationId is denormalized, never a guard.
    [InlineData(typeof(ProjectArchiveUnpack))]
    // Recipe marketplace tables are public by design and unfiltered; owner actions check author_org_id (answering 404) and the
    // anonymous reads expose only listed rows. No parent filter exists to hide behind. Per table:
    // - marketplace_recipes: author_user_id / author_org_id are private columns; public DTOs carry authorLabel only.
    // - marketplace_recipe_versions: preview_source_json (storage key of the author's output), check_json (moderation detail and
    //   unpublished metadata), submitted_by_user_id and snapshot_digest are PRIVATE columns in an unfiltered table: no public
    //   or owner projection may select them (MarketplacePublishTests.Owner_dtos_select_none_of_the_private_columns).
    // - marketplace_copies: org_id and workflow_id (the copier's own workflow) are private; only the distinct count is public.
    // - marketplace_ratings: user_id is private; only counts and the average are public.
    // - marketplace_reports: reporter_user_id and note are private; admin-only.
    // - marketplace_audit: append-only trail, admin-only; actor ids are removed on purge.
    [InlineData(typeof(ReelBolt.Inference.Api.Data.Marketplace.MarketplaceRecipe))]
    [InlineData(typeof(ReelBolt.Inference.Api.Data.Marketplace.MarketplaceRecipeVersion))]
    [InlineData(typeof(ReelBolt.Inference.Api.Data.Marketplace.MarketplaceCopy))]
    [InlineData(typeof(ReelBolt.Inference.Api.Data.Marketplace.MarketplaceRating))]
    [InlineData(typeof(ReelBolt.Inference.Api.Data.Marketplace.MarketplaceReport))]
    [InlineData(typeof(ReelBolt.Inference.Api.Data.Marketplace.MarketplaceAuditEntry))]
    // first_run_reminders (one row per user, the at-most-one-reminder claim): keyed by the person, written only by
    // FirstRunReminderSweeper, never addressable through any route, cascades with the account.
    [InlineData(typeof(ReelBolt.Inference.Api.Services.FirstRunReminder.FirstRunReminderRecord))]
    public void Child_tables_carry_no_tenant_filter(Type child)
    {
        TenantFilteredEntityTypes().Should().NotContain(child,
            $"{child.Name} has no organization column of its own, so it cannot carry the tenant "
            + "filter; its guard is its parent, and the parent's filter is asserted below");
    }

    [Fact]
    public void The_table_is_parent_guarded_not_tenant_filtered()
    {
        using var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"tenancy-library-assets-{Guid.NewGuid():N}")
                .Options,
            new TestTenancy.MutableTenant());

        IEntityType? asset = db.Model.FindEntityType(typeof(DesignSystemLibraryAsset));

        asset.Should().NotBeNull();
        asset!.GetTableName().Should().Be("design_system_library_assets");
        asset.GetDeclaredQueryFilters().Should().BeEmpty(
            "the guard is the filtered design_system_libraries parent, not a filter of its own");
    }

    /// <summary>
    /// The parent half of the same property, behaviourally. A child query joined to its filtered
    /// parent yields only the active organization's rows, which is exactly the shape every wired read
    /// path uses — <c>AssistantThreadsController</c> reaches messages through the thread it just
    /// filtered, <c>EditTimelinesController</c> reaches ops through its project check. A bare
    /// <c>db.ProjectVoices</c> is NOT scoped, and that is the reason the child DbSets are never
    /// exposed directly.
    /// </summary>
    [Fact]
    public void Child_rows_are_scoped_through_their_filtered_parent_but_not_by_a_bare_child_query()
    {
        Guid orgA = Guid.NewGuid();
        Guid orgB = Guid.NewGuid();
        var tenant = new TestTenancy.MutableTenant();
        using InferenceApiDbContext db = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"tenancy-parent-{Guid.NewGuid():N}")
                .Options,
            tenant);

        Project projectA = NewProject(orgA, "A");
        Project projectB = NewProject(orgB, "B");
        AssistantThread threadA = NewThread(orgA);
        AssistantThread threadB = NewThread(orgB);
        db.Projects.AddRange(projectA, projectB);
        db.AssistantThreads.AddRange(threadA, threadB);
        db.ProjectVoices.AddRange(
            NewVoice(projectA.Id, "voice-a"),
            NewVoice(projectB.Id, "voice-b"));
        db.AssistantMessages.AddRange(
            NewMessage(threadA.Id, "message-a"),
            NewMessage(threadB.Id, "message-b"));
        db.SaveChanges();

        tenant.ActAsOrganization(orgA);

        // The parent is filtered...
        db.Projects.Select(p => p.Name).Should().Equal("A");
        db.AssistantThreads.Count().Should().Be(1);

        // ...and the child is scoped exactly through it, which is the guard the wired paths use.
        db.Projects
            .SelectMany(p => db.ProjectVoices.Where(v => v.ProjectId == p.Id))
            .Select(v => v.Name)
            .Should().Equal("voice-a");
        db.AssistantThreads
            .SelectMany(t => db.AssistantMessages.Where(m => m.ThreadId == t.Id))
            .Select(m => m.Content)
            .Should().Equal("message-a");

        // But the child DbSet on its own is genuinely unfiltered — which is why "the child is safe"
        // is never asserted, and why every surface reaches it through the parent instead.
        db.ProjectVoices.Count().Should().Be(2,
            "ProjectVoice has no tenant filter of its own; the boundary is the project lookup that "
            + "precedes every read of it (CrossTenantControllerMatrixTests drives that path)");
        db.AssistantMessages.Count().Should().Be(2);
    }

    /// <summary>
    /// The avatar tables (docs/design/avatars.md "Tenancy") have the same shape as voices: an avatar reached through
    /// the project the caller was authorized for is scoped, a bare <c>db.ProjectAvatars</c> is not, which is why no
    /// avatar route may address an avatar by id alone.
    /// </summary>
    [Fact]
    public void Avatars_are_scoped_through_their_project_and_unfiltered_on_their_own()
    {
        Guid orgA = Guid.NewGuid();
        Guid orgB = Guid.NewGuid();
        var tenant = new TestTenancy.MutableTenant();
        using InferenceApiDbContext db = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"tenancy-avatars-{Guid.NewGuid():N}")
                .Options,
            tenant);

        Project projectA = NewProject(orgA, "A");
        Project projectB = NewProject(orgB, "B");
        db.Projects.AddRange(projectA, projectB);
        db.ProjectAvatars.AddRange(NewAvatar(projectA.Id, "avatar-a"), NewAvatar(projectB.Id, "avatar-b"));
        db.SaveChanges();

        tenant.ActAsOrganization(orgA);

        db.Projects
            .SelectMany(p => db.ProjectAvatars.Where(a => a.ProjectId == p.Id))
            .Select(a => a.Name)
            .Should().Equal("avatar-a");
        db.ProjectAvatars.Count().Should().Be(2,
            "ProjectAvatar has no tenant filter of its own; the boundary is the project lookup before every read");
    }

    /// <summary>
    /// Fail-closed, per filtered table, in one place: with no active organization the filter matches
    /// nothing tenant-owned. The two shared tables (built-in agents, platform providers) are the
    /// deliberate exceptions and are asserted as such rather than being left ambiguous.
    /// </summary>
    [Fact]
    public void Filters_fail_closed_without_an_active_organization()
    {
        var tenant = new TestTenancy.MutableTenant();
        using InferenceApiDbContext db = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"tenancy-failclosed-{Guid.NewGuid():N}")
                .Options,
            tenant);

        Guid org = Guid.NewGuid();
        Project project = NewProject(org);
        db.Projects.Add(project);
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "f",
            MimeType = "x"
        });
        db.AgentDefinitions.AddRange(
            new AgentDefinition { Id = Guid.NewGuid(), Name = "builtin", IsBuiltIn = true },
            new AgentDefinition { Id = Guid.NewGuid(), Name = "custom", OrganizationId = org });
        InferenceProvider platformAccount = new() { Id = Guid.NewGuid(), Name = "platform" };
        InferenceProvider byoAccount = new() { Id = Guid.NewGuid(), Name = "byo", OrganizationId = org };
        InferenceModel platformModel = new() { Id = Guid.NewGuid(), ProviderId = platformAccount.Id, DisplayName = "platform", ModelCode = "m" };
        InferenceModel byoModel = new() { Id = Guid.NewGuid(), ProviderId = byoAccount.Id, OrganizationId = org, DisplayName = "byo", ModelCode = "m" };
        db.InferenceProviders.AddRange(platformAccount, byoAccount);
        db.InferenceModels.AddRange(platformModel, byoModel);
        db.ModelCapabilities.AddRange(
            new ModelCapability { Id = Guid.NewGuid(), ModelId = platformModel.Id },
            new ModelCapability { Id = Guid.NewGuid(), ModelId = byoModel.Id, OrganizationId = org });
        db.AssistantThreads.Add(new AssistantThread { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OrganizationId = org });
        db.StorageRetentionMarks.Add(new StorageRetentionMark
        {
            Id = Guid.NewGuid(),
            StorageKey = "k",
            Category = "c",
            OrganizationId = org,
            MarkedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        tenant.ActAsOrganization(null);

        db.Projects.Count().Should().Be(0);
        db.ProjectFiles.Count().Should().Be(0);
        db.AssistantThreads.Count().Should().Be(0);
        db.StorageRetentionMarks.Count().Should().Be(0);
        db.AgentDefinitions.Select(a => a.Name).Should().Equal("builtin");
        db.InferenceProviders.Select(p => p.Name).Should().Equal("platform");
        db.InferenceModels.Select(m => m.DisplayName).Should().Equal("platform");
        db.ModelCapabilities.Select(c => c.ModelId).Should().Equal(platformModel.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // 3. The controller-side regression net: every project-scoped action consults the access service
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The controllers whose class-level route carries <c>{projectId}</c>: every action in them is
    /// project-scoped by construction, so every one of them must authorize. Pinned as a list so that
    /// a NEW project-scoped controller has to be reviewed and added here deliberately.
    /// </summary>
    private static readonly string[] ReviewedProjectRouteControllers =
    [
        // Reviewed with the subject-detection arm: every one of its nine actions resolves the project
        // through IProjectAccessService as its first statement, and the two pixel actions
        // (layer image, layer matte) do so in the ACTION rather than in the helper they share, so
        // adding a third action beside them cannot inherit a check it never makes.
        // The thumbnail editor split this surface across three controllers under ONE route prefix
        // (documents/layers/subjects, the AI operations, and uploads/exports). Each is reviewed on its
        // own terms: every action performs its own access check, and the shared helper class holds
        // none (see EditImageDocumentSupport), so a fourth action cannot inherit a check it never makes.
        // Composition revisions: both actions (history, request) resolve the project through
        // IProjectAccessService first and then look the execution up WITHIN that project; every
        // composition table it reads is scoped by that project's id.
        "CompositionRevisionsController",
        // Segmentation tracks read: its single action resolves the project through IProjectAccessService (View)
        // first and then looks the VideoAnalyze result up WITHIN that project; foreign/missing is the same 404.
        "SegmentationTracksController",
        // Mask preview of one track: the single action resolves the project through IProjectAccessService (View)
        // first and looks the VideoAnalyze result up WITHIN that project; every refusal is the same 404.
        "SegmentationMaskPreviewController",
        // Step change requests: all four actions (capabilities, list, estimate, create) resolve the project
        // through IProjectAccessService first and look the run up WITHIN that project.
        "StepChangeRequestsController",
        // Design systems (SPEC 9): all eight Phase-2 actions resolve the project through
        // IProjectAccessService first — View to read, Edit to write — and every version/source row is
        // reached through its design_systems.project_id.
        "DesignSystemsController",
        // Design-system assets (T-003): the single content action resolves the project through
        // IProjectAccessService with View as its first statement, then reads only a ProjectFile of
        // that project under `design-system/assets/`; an SVG is re-sanitized before it is served.
        "DesignSystemAssetController",
        "EditImageAssetsController",
        "EditImageDocumentsController",
        // Designed graphics: all three actions (list, one, request) resolve the project through
        // IProjectAccessService first, and every request row they read is constrained to that project
        // AND to the document named in the route.
        "EditImageGraphicsController",
        "EditImageOperationsController",
        // Edit timelines: re-reviewed for W0.9 (T-012). DELETE {id} and PUT {id}/name each resolve the
        // project through IProjectAccessService with Edit as their first statement, then look the
        // timeline up within that project under the soft-delete filter (404 for the rest).
        "EditTimelinesController",
        // Guest review links, owner side (Wave 4): create/revoke resolve the project with Edit, list/get with View, in
        // one IProjectAccessService call per action; the timeline and the link are then looked up WITHIN that project,
        // and edit_review_links carries the Tenant filter as the backstop. The anonymous public controller
        // (ReviewLinksPublicController, /api/v1/review-links/{token}) has no {projectId} and is covered by its own tests.
        "ReviewLinksController",
        // Editor media (P2): the pool/derivative reads and the bin CRUD both resolve the project through
        // IProjectAccessService first; every derivative, bin and bin item they touch is constrained to that
        // project's id, and a storage key is prefix-checked against the project before the object store.
        "MediaBinsController",
        "MediaController",
        // Analysis on demand (R09.1): all four actions (start, estimate, states, data) resolve the project through
        // IProjectAccessService first (Edit to start/estimate, View to read) and look the media up only within that
        // project; request rows are filtered by project id, and the digest cache is read with the project's own
        // organization id, never a caller-supplied one.
        "MediaAnalysisController",
        // Audio cleanup on demand: all three actions (start, estimate, status) resolve the project through
        // IProjectAccessService first (Edit to start/estimate, View to poll); the media is looked up only within that
        // project and a run only by (id, project, hidden AudioCleanup kind).
        "AudioCleanupController",
        "OutputsController",
        // Cloud drive links: all five actions (list, link, change, unlink, sync) resolve the project
        // through IProjectAccessService first and look the link up only within that project.
        "ProjectCloudLinksController",
        // Zip unpacking: both actions (re-run an unpack, list recent unpacks) resolve the project through
        // IProjectAccessService once (Edit to queue, View to list); the zip is looked up only within that project and
        // unpack rows only by project id. There is no by-id route.
        "ProjectArchiveUnpacksController",
        // Object-effects consent: get/put/delete each resolve the project through IProjectAccessService once
        // (View to read, Edit to write) and touch only that project's consent timestamp.
        "ObjectEffectsConsentController",
        // Object analysis (magic blur / mask): every action resolves the project through IProjectAccessService once
        // (View to read, Edit to estimate or request) and looks the source and its request rows up WITHIN that project.
        "ObjectAnalysisController",
        // Video enhancement: every action (estimate, request, status, cancel; consent get/put/delete) resolves the project through
        // IProjectAccessService once and looks the media and the hidden run up WITHIN that project.
        "VideoEnhanceController",
        "VideoEnhanceConsentController",
        // Follow subject: SubjectTracksController (start/read the free local tracking run) and EditTimelineFollowController
        // (the computed camera path) each resolve the project through IProjectAccessService once per action (Edit to start or compute,
        // View to read); the source, run, timeline and track are then looked up WITHIN that project.
        "SubjectTracksController",
        "EditTimelineFollowController",
        // Computer folder links (docs/local-folder-sync.md): all four actions (list, pause, resume,
        // remove) resolve the project through IProjectAccessService first — View to read, Edit to
        // change — and look the link up only within that project. The desktop app's own
        // /api/v1/device/** surface is a separate controller that never names a project in its route:
        // the device proves itself with a signed request and the project it names in the BODY is
        // resolved through the same service, as that device's pairing user.
        "ProjectFolderLinksController",
        "ProjectFileMediaController",
        "ProjectFilesController",
        "ProjectGitHubController",
        "PostTextsController",
        // Project history (contract section 5, layer 1): all six actions resolve the project through
        // IProjectAccessService first — View to list/read, Edit to save a named version or rename one,
        // Delete to delete a version or "Delete forever" a file — and every version/file id is
        // looked up WITHIN that project, so foreign or missing is the same 404.
        "ProjectHistoryController",
        "ProjectPronunciationController",
        // Built-in sound library add + starter set: both actions resolve the project through IProjectAccessService (Edit) first;
        // the source key comes only from the platform library table, the destination key from its validated slug.
        "ProjectSfxLibraryController",
        // Public API (T-006): files (list, get, create upload, complete upload, import, download link, delete) and
        // workflows (list, get, create from template, delete) each resolve the project through IProjectAccessService
        // exactly once, as the dashboard controllers they mirror; a key's project list is enforced inside that service.
        "PublicFilesController",
        "PublicWorkflowsController",
        // Avatars (docs/design/avatars.md): every action answers 404 first while the Avatars:Enabled kill switch is off,
        // then resolves the project through IProjectAccessService once (View to read, Edit to change, Delete to delete);
        // avatars, references and files are then looked up only WITHIN that project.
        "ProjectAvatarsController",
        // Clips with avatars from the editor (docs/design/avatar-objects.md): all four actions (estimate, start, status, stop)
        // answer 404 first while neither avatar switch is on, then resolve the project through IProjectAccessService once
        // (Edit to price, start or stop, View to read); avatars, files and the hidden SubjectClip run are looked up WITHIN it.
        "SubjectClipsController",
        "ProjectVoicesController",
        "StepResultArtifactsController",
        "TimelinesController",
        "WorkflowAppsController",
        "WorkflowAssistantController",
        "WorkflowsController"
    ];

    /// <summary>
    /// Controllers that touch a project off the route (a body/query <c>projectId</c>, or an <c>{id}</c>
    /// that names a project) and DO consult the access service, but whose action count is not a
    /// meaningful denominator for it. Explicit, with the reason, rather than silently exempt:
    /// a newly added project-touching controller shows up as a failure here and has to argue for itself.
    /// </summary>
    private static readonly Dictionary<string, string> ReviewedOffRouteControllers = new(StringComparer.Ordinal)
    {
        ["ProjectsController"] = "the {id} route names a project; every action checks it",
        ["AgentsController"] = "organization-scoped rather than project-scoped; shares the same service",
        ["DashboardController"] = "no project at all: the organization-level read",
        ["AssistantChatController"] = "the request BODY carries projectId; a foreign one is treated as absent",
        ["StylesController"] = "an optional ?projectId= pin, checked on list/create/update",
        ["AssistantThreadsController"] = "a thread is caller+organization scoped; projectId appears on create/list only",
        ["PublicProjectsController"] = "public API: the {projectId} route is checked by Get; list narrows to the key's projects and create is organization-level",
        ["AvatarLibraryController"] = "organization-scoped (the workspace avatar library); a ?projectId= on the reads and the body projectId of a publish are checked through the service"
    };

    private static IReadOnlyList<Type> Controllers() =>
        typeof(ReelBolt.Inference.Api.Controllers.HealthController).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    private static string? RouteTemplate(MemberInfo member) =>
        // The controller's OWN route: the public controllers inherit a base [Route] and declare theirs on top.
        member.GetCustomAttributes<RouteAttribute>(inherit: false).FirstOrDefault()?.Template;

    private static string ControllerRoute(Type controller) =>
        RouteTemplate(controller) ?? string.Empty;

    /// <summary>Public instance actions, i.e. the methods MVC can route to.</summary>
    private static IReadOnlyList<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is HttpMethodAttribute))
            .ToList();

    private static string ControllerSource(string controllerName)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReelBolt.Inference.Api")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        dir.Should().NotBeNull("the test must locate the Inference API's source tree");
        string controllers = Path.Combine(dir!, "src", "ReelBolt.Inference.Api", "Controllers");
        // The public API controllers live in a PublicApi subfolder; the file is found wherever it sits.
        string? path = Directory.EnumerateFiles(controllers, $"{controllerName}.cs", SearchOption.AllDirectories).FirstOrDefault();
        path.Should().NotBeNull($"expected the controller source under {controllers}");
        // A partial controller keeps actions in "{name}.{Part}.cs" files beside it; they count as the same controller.
        IEnumerable<string> parts = Directory.GetFiles(Path.GetDirectoryName(path!)!, $"{controllerName}.*.cs").Order(StringComparer.Ordinal);
        return string.Concat(new[] { path! }.Concat(parts).Select(File.ReadAllText));
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [Fact]
    public void The_project_route_controllers_are_exactly_the_reviewed_set()
    {
        string[] actual = Controllers()
            .Where(c => ControllerRoute(c).Contains("{projectId", StringComparison.Ordinal))
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        actual.Should().BeEquivalentTo(ReviewedProjectRouteControllers,
            "a controller added under a {projectId} route must be listed here and must authorize every "
            + "action through IProjectAccessService; a controller that lost its {projectId} route must "
            + "have its guard re-reviewed rather than silently dropped from this net");
    }

    /// <summary>
    /// The regression net itself. Every action in a <c>{projectId}</c> controller performs exactly one
    /// access-service call: the field declaration and the constructor assignment account for two
    /// occurrences of <c>_projectAccess</c> in the file, and the remainder is one per action.
    ///
    /// <para>
    /// <b>Why this shape and not a per-method grep.</b> It is derived from REFLECTION over the routed
    /// actions (so it cannot be fooled by a method renamed in the source) and compared against the
    /// source (so it cannot be fooled by an attribute the routing table skips). Adding an action
    /// without a check makes the right-hand side one larger than the left and fails here, by name,
    /// with the file to look at.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ProjectRouteControllerCases))]
    public void Every_action_in_a_project_scoped_controller_consults_the_project_access_service(string controllerName, int actionCount)
    {
        // GetEditTimeline asks twice by design (View to read, then Edit for the `canEdit` flag the
        // view-only editor needs); every other action asks once.
        // R08.4/R08.6: loudness and audition delegate to QueueRenderAsync (one check, shared with render),
        // and loudness/estimate delegates to EstimateRender (one check, shared with render/estimate), so
        // those three actions each consult the service exactly once at runtime but add no textual
        // occurrence: three fewer in the source.
        int extraChecks = controllerName == "EditTimelinesController" ? 1 : 0;
        // ProjectHistoryController's canEdit/canDelete are pure IProjectAccessService.Allows checks on
        // the project it already loaded for the one per-action GetAsync, not extra lookups.
        int pureChecks = controllerName == "ProjectHistoryController" ? 2 : 0;
        int sharedHelperActions = controllerName == "EditTimelinesController" ? 3 : 0;
        int checks = Occurrences(ControllerSource(controllerName), "_projectAccess") - 2 - extraChecks - pureChecks + sharedHelperActions;

        checks.Should().Be(actionCount,
            $"{controllerName} routes {actionCount} project-scoped actions and must perform one "
            + "IProjectAccessService check in each of them (the -2 accounts for the field declaration "
            + "and the constructor assignment); a mismatch means an action was added without a check, "
            + "or a check was removed");
    }

    public static TheoryData<string, int> ProjectRouteControllerCases()
    {
        var data = new TheoryData<string, int>();
        foreach (Type controller in Controllers()
                     .Where(c => ControllerRoute(c).Contains("{projectId", StringComparison.Ordinal)))
        {
            data.Add(controller.Name, Actions(controller).Count);
        }

        return data;
    }

    /// <summary>
    /// The other half: project-touching controllers that are NOT route-scoped. A count invariant is
    /// meaningless here (only some of their actions touch a project), so the assertion is deliberately
    /// weaker — the file still consults the service at all, and the file is on the reviewed list. The
    /// point is not to prove each action; it is to make a NEW controller appear as a failure.
    /// </summary>
    [Fact]
    public void Off_route_project_controllers_are_a_reviewed_allowlist_that_still_consults_the_service()
    {
        string[] candidates =
        [
            "ProjectsController",
            "AgentsController",
            "DashboardController",
            "AssistantChatController",
            "StylesController",
            "AssistantThreadsController",
            "PublicProjectsController",
            "AvatarLibraryController"
        ];

        candidates.Should().BeEquivalentTo(ReviewedOffRouteControllers.Keys);

        foreach (string controllerName in candidates)
        {
            Occurrences(ControllerSource(controllerName), "_projectAccess")
                .Should().BeGreaterThan(0, $"{controllerName} is on the reviewed list as a project-touching controller, so it must still consult IProjectAccessService");
        }
    }

    // ---------------------------------------------------------------------------------------------------

    private static Project NewProject(Guid organizationId, string name = "P") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OwnerId = Guid.NewGuid(),
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AssistantThread NewThread(Guid organizationId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        OrganizationId = organizationId,
        Title = "t",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AssistantMessage NewMessage(Guid threadId, string content) => new()
    {
        Id = Guid.NewGuid(),
        ThreadId = threadId,
        Role = "user",
        Content = content,
        Sequence = 1,
        CreatedAt = DateTime.UtcNow
    };

    private static ProjectAvatar NewAvatar(Guid projectId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Name = name,
        ConsentAttestedByUserId = Guid.NewGuid(),
        ConsentAttestedAt = DateTime.UtcNow,
        ConsentStatement = "statement",
        ConsentStatementVersion = "avatar-consent-2026-11",
        ConsentLocale = "en",
        ConsentSubjectName = name,
        ConsentSubjectAdult = true,
        RetentionUntil = DateTime.UtcNow.AddDays(365),
        CreatedByUserId = Guid.NewGuid()
    };

    private static ProjectVoice NewVoice(Guid projectId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Name = name,
        RemoteVoiceId = $"remote-{name}",
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow
    };
}
