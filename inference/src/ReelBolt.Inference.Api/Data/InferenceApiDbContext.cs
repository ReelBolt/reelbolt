using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Shared;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Data;

/// <summary>
/// EF Core context for the Inference API service.
/// Owns: application_users, projects, project_files, agent_definitions, inference_providers,
/// inference_models, model_capabilities, model_capability_prices, pricing_settings
/// (the model catalog, docs/design/model-catalog.md), assistant_threads, assistant_messages, organizations, organization_memberships,
/// organization_invites, organization_agent_provider_overrides, user_tokens, legal_acceptances,
/// legal_documents, data_protection_keys, platform_secrets, admin_audit_events,
/// storage_retention_marks, runner_devices, runner_pairing_requests, platform_settings,
/// project_github_links, github_app_installations, github_webhook_deliveries (docs/github-app.md),
/// user_notification_preferences, user_notifications (docs/notifications.md),
/// assistant_allowance_holds (the assistant/MCP allowance counter, docs/metering-and-billing.md).
/// Promotions and marketing consent: promotions, promotion_redemptions, admin_credit_grants,
/// marketing_consents, marketing_consent_events, marketing_suppressions, marketing_consent_exports.
/// New-user funnel (docs/design/new-user-funnel.md): marketing_links, marketing_link_daily_stats,
/// visitor_attribution, user_onboarding_profiles.
/// Also owns the editor-v2 tables: edit_timelines, edit_timeline_ops,
/// edit_timeline_snapshots, media_derivatives, media_bins, media_bin_items,
/// feedback_reports (the in-product bug/feedback sink, T-019),
/// alpha_funnel_claims, product_events (the append-only product funnel, docs/product-events.md).
/// References (ExcludeFromMigrations): workflow_* tables, review_scores, external_generation_jobs,
/// workflow_step_cache_entries, decision_observations (C9 reads the last three to export and purge them).
/// </summary>
public class InferenceApiDbContext : DbContext
{
    /// <summary>Name of the tenant filter (EF named filters coexist with the soft-delete filters).</summary>
    public const string TenantFilterName = "Tenant";

    private readonly ITenantContext? _tenant;

    /// <summary>
    /// <paramref name="tenant"/> drives the tenant query filters. DI always supplies it, so every
    /// request and background path is filtered (and fails closed with no org and no system scope).
    /// A context built by hand with no tenant accessor (design-time tooling, unit tests that are not
    /// about tenancy) is unfiltered: there is nothing to scope by.
    /// </summary>
    public InferenceApiDbContext(DbContextOptions<InferenceApiDbContext> options, ITenantContext? tenant = null)
        : base(options)
    {
        _tenant = tenant;
    }

    // Marketplace recipe Revision: the one concurrency mechanism. Every Modified (not Added) recipe row is bumped here, in every
    // SaveChanges, so no writer can be silently overwritten by another (a removed or unpublished recipe cannot end Listed by a
    // late finalize). RULE: a set-based ExecuteUpdate/ExecuteDelete on marketplace_recipes bypasses this hook and must either bump
    // revision itself (SET revision = revision + 1) or run inside the same per-organization cap lock / transaction as the writers
    // it could race.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpMarketplaceRevisions();
        Services.Avatars.Library.AvatarLibraryHoldMarker.StampAtVerdict(this);
        Services.Avatars.Library.AvatarLibraryHoldMarker.StampBeforeRemoval(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpMarketplaceRevisions();
        // A held source row is never removed without the durable hold marker on its library copies, in the same save.
        await Services.Avatars.Library.AvatarLibraryHoldMarker.StampAtVerdictAsync(this, cancellationToken);
        await Services.Avatars.Library.AvatarLibraryHoldMarker.StampBeforeRemovalAsync(this, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BumpMarketplaceRevisions()
    {
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<MarketplaceRecipe> entry in ChangeTracker.Entries<MarketplaceRecipe>())
        {
            if (entry.State == EntityState.Modified)
                entry.Property(r => r.Revision).CurrentValue = entry.Property(r => r.Revision).OriginalValue + 1;
        }
    }

    // Referenced from the tenant filters below. EF reads these per query (they are members of the
    // context instance), so a filter follows the scope that is current when the query runs.
    private bool TenantUnfiltered => _tenant is null || _tenant.IsSystem;
    private Guid? TenantOrgId => _tenant?.OrganizationId;

    // Owned tables
    public DbSet<ApplicationUser> ApplicationUsers => Set<ApplicationUser>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTag> ProjectTags => Set<ProjectTag>();
    public DbSet<ProjectTagLink> ProjectTagLinks => Set<ProjectTagLink>();
    public DbSet<ProjectFolder> ProjectFolders => Set<ProjectFolder>();
    public DbSet<ProjectFile> ProjectFiles => Set<ProjectFile>();
    public DbSet<CsamScanAudit> CsamScanAudits => Set<CsamScanAudit>();

    // Platform-admin user-account audit (written by the Go API by raw SQL), platform-wide, no Tenant filter.
    public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();

    // Recipe marketplace: public by design, no Tenant filter (owner actions check author_org_id).
    public DbSet<MarketplaceRecipe> MarketplaceRecipes => Set<MarketplaceRecipe>();
    public DbSet<MarketplaceRecipeVersion> MarketplaceRecipeVersions => Set<MarketplaceRecipeVersion>();
    public DbSet<MarketplaceCopy> MarketplaceCopies => Set<MarketplaceCopy>();
    public DbSet<MarketplaceRating> MarketplaceRatings => Set<MarketplaceRating>();
    public DbSet<MarketplaceReport> MarketplaceReports => Set<MarketplaceReport>();
    public DbSet<MarketplaceAuditEntry> MarketplaceAudit => Set<MarketplaceAuditEntry>();
    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();
    public DbSet<InferenceProvider> InferenceProviders => Set<InferenceProvider>();
    public DbSet<InferenceModel> InferenceModels => Set<InferenceModel>();
    public DbSet<ModelCapability> ModelCapabilities => Set<ModelCapability>();
    public DbSet<ModelCapabilityPrice> ModelCapabilityPrices => Set<ModelCapabilityPrice>();
    public DbSet<PricingSettings> PricingSettings => Set<PricingSettings>();
    public DbSet<AssistantThread> AssistantThreads => Set<AssistantThread>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<ApiKeySpend> ApiKeySpends => Set<ApiKeySpend>();
    public DbSet<ApiKeyAuditEvent> ApiKeyAuditEvents => Set<ApiKeyAuditEvent>();
    public DbSet<ApiKeyRateCounter> ApiKeyRateCounters => Set<ApiKeyRateCounter>();
    public DbSet<ApiIdempotencyRecord> ApiIdempotencyRecords => Set<ApiIdempotencyRecord>();
    public DbSet<ApiRunRequest> ApiRunRequests => Set<ApiRunRequest>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<AssistantMessage> AssistantMessages => Set<AssistantMessage>();
    public DbSet<AssistantRunWatch> AssistantRunWatches => Set<AssistantRunWatch>();
    public DbSet<EditTimelineEntity> EditTimelines => Set<EditTimelineEntity>();
    public DbSet<EditTimelineOp> EditTimelineOps => Set<EditTimelineOp>();
    public DbSet<EditTimelineSnapshot> EditTimelineSnapshots => Set<EditTimelineSnapshot>();
    public DbSet<MediaDerivative> MediaDerivatives => Set<MediaDerivative>();
    public DbSet<EditImageDocument> EditImageDocuments => Set<EditImageDocument>();
    public DbSet<EditImageLayer> EditImageLayers => Set<EditImageLayer>();
    public DbSet<EditImageExport> EditImageExports => Set<EditImageExport>();
    public DbSet<MediaBin> MediaBins => Set<MediaBin>();
    public DbSet<MediaBinItem> MediaBinItems => Set<MediaBinItem>();
    public DbSet<MediaAnnotation> MediaAnnotations => Set<MediaAnnotation>();
    public DbSet<MediaAnalysisResult> MediaAnalysisResults => Set<MediaAnalysisResult>();
    public DbSet<MediaAnalysisRequest> MediaAnalysisRequests => Set<MediaAnalysisRequest>();
    public DbSet<EditorUserPreference> EditorUserPreferences => Set<EditorUserPreference>();
    public DbSet<UserOnboardingState> UserOnboardingStates => Set<UserOnboardingState>();
    public DbSet<EditorPreset> EditorPresets => Set<EditorPreset>();
    public DbSet<EditTimelineComment> EditTimelineComments => Set<EditTimelineComment>();
    public DbSet<EditReviewLink> EditReviewLinks => Set<EditReviewLink>();
    public DbSet<EditReviewDecision> EditReviewDecisions => Set<EditReviewDecision>();
    public DbSet<ProjectVoice> ProjectVoices => Set<ProjectVoice>();
    // Avatars (docs/design/avatars.md): a consented likeness, project-scoped like voices; migration AddProjectAvatars.
    public DbSet<ProjectAvatar> ProjectAvatars => Set<ProjectAvatar>();
    public DbSet<ProjectAvatarReference> ProjectAvatarReferences => Set<ProjectAvatarReference>();
    public DbSet<ProjectAvatarVendorAsset> ProjectAvatarVendorAssets => Set<ProjectAvatarVendorAsset>();
    public DbSet<ProjectAvatarEvent> ProjectAvatarEvents => Set<ProjectAvatarEvent>();
    // Workspace avatar library (docs/design/avatar-objects.md): organization-owned items (Tenant filter) and their media; migration AddAvatarLibrary.
    public DbSet<AvatarLibraryItem> AvatarLibraryItems => Set<AvatarLibraryItem>();
    public DbSet<AvatarLibraryReference> AvatarLibraryReferences => Set<AvatarLibraryReference>();
    public DbSet<AvatarBuildJob> AvatarBuildJobs => Set<AvatarBuildJob>();

    public DbSet<ProjectPronunciationEntry> ProjectPronunciationEntries => Set<ProjectPronunciationEntry>();
    public DbSet<ProjectVersion> ProjectVersions => Set<ProjectVersion>();
    public DbSet<ProjectVersionItem> ProjectVersionItems => Set<ProjectVersionItem>();
    public DbSet<ProjectHistoryBlob> ProjectHistoryBlobs => Set<ProjectHistoryBlob>();
    public DbSet<ProjectHistoryDocument> ProjectHistoryDocuments => Set<ProjectHistoryDocument>();
    public DbSet<DesignSystem> DesignSystems => Set<DesignSystem>();
    public DbSet<DesignSystemVersion> DesignSystemVersions => Set<DesignSystemVersion>();
    public DbSet<DesignSystemSource> DesignSystemSources => Set<DesignSystemSource>();
    public DbSet<DesignSystemChangeProposal> DesignSystemChangeProposals => Set<DesignSystemChangeProposal>();
    public DbSet<DesignSystemLibrary> DesignSystemLibraries => Set<DesignSystemLibrary>();
    public DbSet<DesignSystemLibraryAsset> DesignSystemLibraryAssets => Set<DesignSystemLibraryAsset>();
    public DbSet<SfxLibraryItem> SfxLibraryItems => Set<SfxLibraryItem>();
    public DbSet<CompositionWorkspace> CompositionWorkspaces => Set<CompositionWorkspace>();
    public DbSet<CompositionWorkspaceVersion> CompositionWorkspaceVersions => Set<CompositionWorkspaceVersion>();
    public DbSet<CompositionRevision> CompositionRevisions => Set<CompositionRevision>();
    public DbSet<StepChangeRequest> StepChangeRequests => Set<StepChangeRequest>();
    public DbSet<ObjectEffectRequest> ObjectEffectRequests => Set<ObjectEffectRequest>();
    public DbSet<ProjectArchiveUnpack> ProjectArchiveUnpacks => Set<ProjectArchiveUnpack>();
    public DbSet<EditImageGraphicRequest> EditImageGraphicRequests => Set<EditImageGraphicRequest>();
    public DbSet<EditingStyle> EditingStyles => Set<EditingStyle>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<OrganizationInvite> OrganizationInvites => Set<OrganizationInvite>();
    public DbSet<OrganizationAgentProviderOverride> OrganizationAgentProviderOverrides => Set<OrganizationAgentProviderOverride>();
    public DbSet<UserToken> UserTokens => Set<UserToken>();
    public DbSet<LegalAcceptance> LegalAcceptances => Set<LegalAcceptance>();
    public DbSet<LegalDocumentVersion> LegalDocumentVersions => Set<LegalDocumentVersion>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<PlatformSecret> PlatformSecrets => Set<PlatformSecret>();
    public DbSet<RunnerDevice> RunnerDevices => Set<RunnerDevice>();
    public DbSet<RunnerPairingRequest> RunnerPairingRequests => Set<RunnerPairingRequest>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<RateCard> RateCards => Set<RateCard>();
    public DbSet<RateMultiplier> RateMultipliers => Set<RateMultiplier>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<CreditAccount> CreditAccounts => Set<CreditAccount>();
    public DbSet<CreditLedgerEntry> CreditLedgerEntries => Set<CreditLedgerEntry>();
    public DbSet<CreditGrant> CreditGrants => Set<CreditGrant>();
    public DbSet<AssistantAllowanceHold> AssistantAllowanceHolds => Set<AssistantAllowanceHold>();
    public DbSet<TrialClaim> TrialClaims => Set<TrialClaim>();
    public DbSet<BillingCustomer> BillingCustomers => Set<BillingCustomer>();
    public DbSet<BillingWebhookEvent> BillingWebhookEvents => Set<BillingWebhookEvent>();
    public DbSet<BillingPayment> BillingPayments => Set<BillingPayment>();
    public DbSet<BillingRefund> BillingRefunds => Set<BillingRefund>();
    public DbSet<BillingPendingUpgradeGrant> BillingPendingUpgradeGrants => Set<BillingPendingUpgradeGrant>();
    public DbSet<BillingReconcileWatch> BillingReconcileWatches => Set<BillingReconcileWatch>();
    public DbSet<BillingAdminAction> BillingAdminActions => Set<BillingAdminAction>();
    public DbSet<StorageRetentionMark> StorageRetentionMarks => Set<StorageRetentionMark>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<PromotionRedemption> PromotionRedemptions => Set<PromotionRedemption>();
    public DbSet<AdminCreditGrant> AdminCreditGrants => Set<AdminCreditGrant>();
    public DbSet<AdminSubscriptionGrant> AdminSubscriptionGrants => Set<AdminSubscriptionGrant>();
    public DbSet<PromotionCheckout> PromotionCheckouts => Set<PromotionCheckout>();
    public DbSet<MarketingConsent> MarketingConsents => Set<MarketingConsent>();
    public DbSet<MarketingConsentEvent> MarketingConsentEvents => Set<MarketingConsentEvent>();
    public DbSet<MarketingSuppression> MarketingSuppressions => Set<MarketingSuppression>();
    public DbSet<MarketingConsentExport> MarketingConsentExports => Set<MarketingConsentExport>();
    public DbSet<MarketingCampaign> MarketingCampaigns => Set<MarketingCampaign>();
    public DbSet<MarketingBrevoErasure> MarketingBrevoErasures => Set<MarketingBrevoErasure>();
    public DbSet<MarketingContactSyncState> MarketingContactSyncStates => Set<MarketingContactSyncState>();

    /// <summary>Tracked links (<c>/l/{code}</c>) and their daily click counters (docs/design/new-user-funnel.md).</summary>
    public DbSet<MarketingLink> MarketingLinks => Set<MarketingLink>();
    public DbSet<MarketingLinkDailyStat> MarketingLinkDailyStats => Set<MarketingLinkDailyStat>();
    public DbSet<MailRateCounter> MailRateCounters => Set<MailRateCounter>();
    public DbSet<Services.FirstRunReminder.FirstRunReminderRecord> FirstRunReminders => Set<Services.FirstRunReminder.FirstRunReminderRecord>();
    // alpha-launch T-018: the durable funnel claim marker (one row per person per event).
    public DbSet<Services.Telemetry.AlphaFunnelClaim> AlphaFunnelClaims => Set<Services.Telemetry.AlphaFunnelClaim>();
    // product-events-funnel T-001: the append-only product funnel (docs/product-events.md). Written by
    // the Inference API and the Go API (which maps it with GORM and never migrates); read by powerbi_ro.
    public DbSet<Services.Telemetry.ProductEvent> ProductEvents => Set<Services.Telemetry.ProductEvent>();

    /// <summary>First-touch acquisition attribution, one row per user (written by the Go API).</summary>
    public DbSet<Services.Telemetry.UserAttribution> UserAttributions => Set<Services.Telemetry.UserAttribution>();

    /// <summary>First-touch attribution of a consented anonymous visitor (written by the visit beacon).</summary>
    public DbSet<Services.Telemetry.VisitorAttribution> VisitorAttributions => Set<Services.Telemetry.VisitorAttribution>();

    /// <summary>Who a person said they are and what they start from, one row per user (new-user funnel).</summary>
    public DbSet<UserOnboardingProfile> UserOnboardingProfiles => Set<UserOnboardingProfile>();
    /// <summary>A person's own daily video spend limit and alert level, one row per user.</summary>
    public DbSet<UserVideoSpendLimit> UserVideoSpendLimits => Set<UserVideoSpendLimit>();
    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();
    public DbSet<ProjectGitHubLink> ProjectGitHubLinks => Set<ProjectGitHubLink>();
    public DbSet<GitHubAppInstallation> GitHubAppInstallations => Set<GitHubAppInstallation>();
    public DbSet<GitHubWebhookDelivery> GitHubWebhookDeliveries => Set<GitHubWebhookDelivery>();
    public DbSet<GitHubAccountLink> GitHubAccountLinks => Set<GitHubAccountLink>();
    public DbSet<GitHubOAuthState> GitHubOAuthStates => Set<GitHubOAuthState>();
    public DbSet<ProjectLocalFolderLink> ProjectLocalFolderLinks => Set<ProjectLocalFolderLink>();
    public DbSet<CloudStorageConnection> CloudStorageConnections => Set<CloudStorageConnection>();
    public DbSet<CloudStorageOAuthState> CloudStorageOAuthStates => Set<CloudStorageOAuthState>();
    public DbSet<SocialAccount> SocialAccounts => Set<SocialAccount>();
    public DbSet<SocialOAuthState> SocialOAuthStates => Set<SocialOAuthState>();
    public DbSet<SocialPost> SocialPosts => Set<SocialPost>();
    public DbSet<SocialPostMetric> SocialPostMetrics => Set<SocialPostMetric>();
    public DbSet<SocialPublishLedgerEntry> SocialPublishLedger => Set<SocialPublishLedgerEntry>();
    public DbSet<ProjectCloudLink> ProjectCloudLinks => Set<ProjectCloudLink>();
    public DbSet<UserNotificationPreferences> UserNotificationPreferences => Set<UserNotificationPreferences>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();

    // MCP OAuth (docs/design/mcp-oauth.md §5-§6). The Go API is the authorization server and
    // reads/writes all four by GORM/raw SQL; it never migrates, so the schema is owned here.
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();
    public DbSet<OAuthAuthorizationCode> OAuthAuthorizationCodes => Set<OAuthAuthorizationCode>();
    public DbSet<OAuthRefreshToken> OAuthRefreshTokens => Set<OAuthRefreshToken>();
    public DbSet<McpToolCall> McpToolCalls => Set<McpToolCall>();

    /// <summary>The hourly run-limit ledger (see <see cref="RunRateEvent"/>).</summary>
    public DbSet<RunRateEvent> RunRateEvents => Set<RunRateEvent>();

    /// <summary>In-product bug reports and feedback (T-019); the alpha's primary signal channel.</summary>
    public DbSet<FeedbackReport> FeedbackReports => Set<FeedbackReport>();

    /// <summary>
    /// The append-only moderation audit log (no raw content). The API migrates it and both the API gate
    /// and the WorkflowEngine gate insert rows; the platform-admin review queue reads it.
    /// </summary>
    public DbSet<ContentModerationDecision> ContentModerationDecisions => Set<ContentModerationDecision>();
    public DbSet<Services.PostTexts.PostTextRow> PostTexts => Set<Services.PostTexts.PostTextRow>();
    public DbSet<Services.Moderation.OutputModerationVerdict> OutputModerationVerdicts => Set<Services.Moderation.OutputModerationVerdict>();

    // Referenced tables (for navigation properties, not migrations)
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowExecution> WorkflowExecutions => Set<WorkflowExecution>();
    public DbSet<WorkflowStepResult> WorkflowStepResults => Set<WorkflowStepResult>();
    public DbSet<ReviewScore> ReviewScores => Set<ReviewScore>();
    public DbSet<ExternalGenerationJob> ExternalGenerationJobs => Set<ExternalGenerationJob>();
    public DbSet<WorkflowStepCacheEntry> WorkflowStepCacheEntries => Set<WorkflowStepCacheEntry>();
    public DbSet<DecisionObservation> DecisionObservations => Set<DecisionObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply snake_case naming convention
        SnakeCaseNamingHelper.ApplySnakeCaseNaming(modelBuilder);

        // --- Owned tables ---

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.IsServiceAccount).HasDefaultValue(false);
            entity.HasIndex(e => e.Email).IsUnique();
            // Plain scalar (no FK): organizations.created_by_user_id already points the other way,
            // and a stale value is harmless — login falls back to the Personal org.
            entity.Property(e => e.PasswordHash).IsRequired(false);
            entity.Property(e => e.SignupIp).HasMaxLength(64);
            entity.Property(e => e.AuthProvider).HasMaxLength(50).HasDefaultValue("Password");
            // Off for every existing and new account; the MCP admin opt-in is set explicitly from the
            // web portal (docs/design/mcp-oauth.md §6).
            entity.Property(e => e.McpAdminToolsEnabled).HasDefaultValue(false);
            // Off for every existing and new account; set only from the app's settings page.
            entity.Property(e => e.McpMediaEgressEnabled).HasDefaultValue(false);
            // Off for every account until the user turns auto mode on in the assistant panel.
            entity.Property(e => e.AssistantAutoApprove).HasDefaultValue(false);
            // Saved UI language; nullable, no default, no list-dependent constraint (Go API validates).
            entity.Property(e => e.Locale).HasMaxLength(16);
        });

        // Data Protection key ring (DataProtection:Store=Database). This context is the only writer.
        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("data_protection_keys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FriendlyName).HasMaxLength(200);
        });

        // F11 platform kill switches. A platform-wide table by nature, so it carries neither a tenant
        // query filter nor an organisation-owned row: an org row is expressed as a key
        // (org:<guid>), and the table is read by the engine and the Go runner gateway, which are not
        // scoped to a caller's organisation at all.
        modelBuilder.Entity<PlatformSetting>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(100);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.UpdatedBy).HasMaxLength(320);
        });

        modelBuilder.Entity<PlatformSecret>(entity =>
        {
            entity.ToTable("platform_secrets");
            entity.HasKey(e => e.Name);
            entity.Property(e => e.Name).HasMaxLength(100);
        });

        // alpha-launch T-018: the durable "once per user, per event" funnel marker. Keyed by person,
        // so it carries no tenant filter; the unique key IS the de-duplication.
        modelBuilder.Entity<Services.Telemetry.AlphaFunnelClaim>(entity =>
        {
            entity.ToTable("alpha_funnel_claims");
            entity.HasKey(e => new { e.UserId, e.Event });
            entity.Property(e => e.Event).HasMaxLength(64);
        });

        // product-events-funnel T-001: the append-only product funnel (docs/product-events.md). Keyed by
        // person and/or organization, so it carries no tenant filter - the same reachability rule as
        // alpha_funnel_claims. The two CHECK constraints are the schema's statement of the funnel's
        // honesty: a source outside the three-value vocabulary, or a row that names neither a person nor
        // an organization, is refused by the database rather than by a reader's convention. The index
        // names are explicit and lower-case (the convention would emit IX_*) because the BI extract's
        // funnel query and the docs name them.
        modelBuilder.Entity<Services.Telemetry.ProductEvent>(entity =>
        {
            entity.ToTable("product_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Event).HasMaxLength(64);
            entity.Property(e => e.Source).HasMaxLength(16);
            entity.Property(e => e.Detail).HasMaxLength(64);
            entity.HasIndex(e => new { e.UserId, e.OccurredAt })
                .HasDatabaseName("ix_product_events_user_id_occurred_at");
            entity.HasIndex(e => new { e.Event, e.OccurredAt })
                .HasDatabaseName("ix_product_events_event_occurred_at");
            entity.HasIndex(e => e.OrganizationId)
                .HasDatabaseName("ix_product_events_organization_id");
            // product-analytics: the visit endpoint's dedupe lookup (same visitor + event within 30 minutes).
            entity.HasIndex(e => new { e.VisitorId, e.Event, e.OccurredAt })
                .HasDatabaseName("ix_product_events_visitor_event_occurred_at")
                .HasFilter("visitor_id IS NOT NULL");
            // product-events-funnel T-004: alpha.first_payment is the organization's ACTIVATION, at most
            // one row per organization. The partial unique index is the structural claim - not a
            // convention and not an advisory lock (the billing locks are per-subscription and per
            // transaction, so they cannot serialise two subscriptions of one organization) - and it is
            // the conflict target the payment step's ON CONFLICT DO NOTHING write names.
            entity.HasIndex(e => new { e.OrganizationId, e.Event })
                .IsUnique()
                .HasDatabaseName("ux_product_events_first_payment_organization")
                .HasFilter("event = 'alpha.first_payment'");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "ck_product_events_subject",
                    "user_id IS NOT NULL OR organization_id IS NOT NULL OR visitor_id IS NOT NULL");
                t.HasCheckConstraint(
                    "ck_product_events_source",
                    "source IN ('server', 'client', 'consent')");
            });
        });

        modelBuilder.Entity<Services.Telemetry.UserAttribution>(entity =>
        {
            entity.ToTable("user_attribution");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.SourceLabel).HasMaxLength(64);
            entity.Property(e => e.UtmSource).HasMaxLength(64);
            entity.Property(e => e.UtmMedium).HasMaxLength(64);
            entity.Property(e => e.UtmCampaign).HasMaxLength(64);
            entity.Property(e => e.UtmContent).HasMaxLength(64);
            entity.Property(e => e.UtmTerm).HasMaxLength(64);
            entity.Property(e => e.ReferrerHost).HasMaxLength(100);
            entity.Property(e => e.LandingPath).HasMaxLength(120);
            // new-user funnel: how the row was learned. Null on rows older than the column.
            entity.Property(e => e.Capture).HasMaxLength(16);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_user_attribution_capture",
                $"capture IS NULL OR capture IN ({AcquisitionVocabulary.SqlInList(AcquisitionVocabulary.AttributionCaptures)})"));
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // new-user funnel (docs/design/new-user-funnel.md section 5): first-touch attribution of a consented
        // anonymous visitor. Keyed by a random visitor id that exists only after cookie consent; there is
        // no account and no organization yet, so it carries no tenant filter and no foreign key. The two
        // indexes serve the per-link visitor count (utm_content is the link code) and the retention sweep.
        modelBuilder.Entity<Services.Telemetry.VisitorAttribution>(entity =>
        {
            entity.ToTable("visitor_attribution");
            entity.HasKey(e => e.VisitorId).HasName("pk_visitor_attribution");
            entity.Property(e => e.VisitorId).ValueGeneratedNever();
            entity.Property(e => e.SourceLabel).HasMaxLength(64);
            entity.Property(e => e.UtmSource).HasMaxLength(64);
            entity.Property(e => e.UtmMedium).HasMaxLength(64);
            entity.Property(e => e.UtmCampaign).HasMaxLength(64);
            entity.Property(e => e.UtmContent).HasMaxLength(64);
            entity.Property(e => e.UtmTerm).HasMaxLength(64);
            entity.Property(e => e.ReferrerHost).HasMaxLength(100);
            entity.Property(e => e.LandingPath).HasMaxLength(120);
            entity.HasIndex(e => new { e.UtmContent, e.FirstTouchAt })
                .HasDatabaseName("ix_visitor_attribution_utm_content_first_touch_at")
                .HasFilter("utm_content IS NOT NULL");
            entity.HasIndex(e => e.FirstTouchAt)
                .HasDatabaseName("ix_visitor_attribution_first_touch_at");
        });

        // new-user funnel: who a person said they are and what they start from. One row per user, removed
        // with the account. The Go API inserts the row at account creation with only suggested_persona; the
        // onboarding endpoints write the answer. The three CHECKs are the closed sets of
        // AcquisitionVocabulary: a token outside them is refused by the database, not by a reader's convention.
        // A person's own daily video spend limit (docs/design/video-spend-limit.md): one row per user, API-owned,
        // read by the engine's budget gate. Both amounts are credits (precision as the ledger's cost_estimate).
        modelBuilder.Entity<UserVideoSpendLimit>(entity =>
        {
            entity.ToTable("user_video_spend_limits", t =>
            {
                t.HasCheckConstraint("ck_user_video_spend_limits_limit", "daily_limit_credits IS NULL OR daily_limit_credits >= 0");
                t.HasCheckConstraint("ck_user_video_spend_limits_alert", "alert_threshold_credits IS NULL OR alert_threshold_credits >= 0");
            });
            entity.HasKey(e => e.UserId).HasName("pk_user_video_spend_limits");
            entity.Property(e => e.UserId).ValueGeneratedNever();
            entity.Property(e => e.DailyLimitCredits).HasPrecision(18, 4);
            entity.Property(e => e.AlertThresholdCredits).HasPrecision(18, 4);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserOnboardingProfile>(entity =>
        {
            entity.ToTable("user_onboarding_profiles", t =>
            {
                string personas = AcquisitionVocabulary.SqlInList(AcquisitionVocabulary.Personas);
                t.HasCheckConstraint("ck_user_onboarding_profiles_persona", $"persona IS NULL OR persona IN ({personas})");
                t.HasCheckConstraint(
                    "ck_user_onboarding_profiles_suggested_persona",
                    $"suggested_persona IS NULL OR suggested_persona IN ({personas})");
                t.HasCheckConstraint(
                    "ck_user_onboarding_profiles_material",
                    $"material IS NULL OR material IN ({AcquisitionVocabulary.SqlInList(AcquisitionVocabulary.Materials)})");
            });
            entity.HasKey(e => e.UserId).HasName("pk_user_onboarding_profiles");
            entity.Property(e => e.UserId).ValueGeneratedNever();
            entity.Property(e => e.Persona).HasMaxLength(32);
            entity.Property(e => e.Material).HasMaxLength(32);
            entity.Property(e => e.SuggestedPersona).HasMaxLength(32);
            entity.Property(e => e.PickedPlan).HasMaxLength(32);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.Purpose });
            entity.Property(e => e.TokenHash).HasMaxLength(128);
            entity.Property(e => e.Purpose).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.SentToEmail).HasMaxLength(320);
            entity.Property(e => e.PendingEmail).HasMaxLength(320);
            // At most one open email-change token per user (admin-email-pending-address).
            entity.HasIndex(e => e.UserId)
                .HasDatabaseName("IX_user_tokens_one_open_email_change")
                .IsUnique()
                .HasFilter("purpose = 'EmailChange' AND used_at IS NULL");
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // The per-user notification switches (docs/notifications.md). Written by the Go API by
        // raw SQL, which never migrates; the Inference API owns the schema. No tenant filter:
        // a caller addresses a row only through their own user id.
        modelBuilder.Entity<UserNotificationPreferences>(entity =>
        {
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.ReviewActivity).HasDefaultValue(true);
            entity.Property(e => e.AvatarRetention).HasDefaultValue(true);
            entity.Property(e => e.SocialPublishing).HasDefaultValue(true);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // The per-user notification delivery log (docs/notifications.md). Written and read by the
        // Go API; same reachability rule as the switches, so no tenant query filter.
        modelBuilder.Entity<UserNotification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Category).HasMaxLength(50);
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Error).HasMaxLength(500);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LegalAcceptance>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.Document });
            entity.Property(e => e.Document).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Version).HasMaxLength(50);
            entity.Property(e => e.Ip).HasMaxLength(64);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Which version of each legal document this deployment currently serves (F-7). A
        // platform-wide table read by the Go API by raw SQL to pin an acceptance to a version the
        // server — not the caller — named, so it carries no tenant query filter: the document name
        // is the key and no caller addresses a row by an id of their own choosing.
        modelBuilder.Entity<LegalDocumentVersion>(entity =>
        {
            entity.ToTable("legal_documents");
            entity.HasKey(e => e.Document);
            entity.Property(e => e.Document).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Version).HasMaxLength(50);
            entity.Property(e => e.UpdatedBy).HasMaxLength(320);
        });

        modelBuilder.Entity<RunnerDevice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.CapabilitiesJson).HasColumnType("jsonb");
            entity.Property(e => e.PublicKeyFingerprint).HasMaxLength(128);
            // A key can pair only once; nullable (pool workers have none) and NULLs are distinct.
            entity.HasIndex(e => e.PublicKeyFingerprint).IsUnique();
            entity.HasIndex(e => e.OrganizationId);
            // Organization deletion is a soft delete, so this cascade only fires on a hard purge.
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.PairedByUserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.RevokedByUserId).OnDelete(DeleteBehavior.SetNull);
            // Desktop devices belong to an org; only pool workers may have none.
            entity.ToTable(t => t.HasCheckConstraint("ck_runner_devices_org_or_pool", "kind = 'CloudPool' OR organization_id IS NOT NULL"));
        });

        // The unauthenticated half of pairing (WP E4a). No tenant filter: the row is addressed by a
        // device-code hash that only its own device knows, never by an id a caller chose — the same
        // rule that keeps runner_devices out of the filtered set (docs/multi-tenancy.md).
        modelBuilder.Entity<RunnerPairingRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeviceCodeHash).HasMaxLength(128);
            entity.HasIndex(e => e.DeviceCodeHash).IsUnique();
            entity.Property(e => e.UserCode).HasMaxLength(16);
            entity.Property(e => e.PublicKey).HasMaxLength(200);
            entity.Property(e => e.PublicKeyFingerprint).HasMaxLength(128);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Os).HasMaxLength(50);
            entity.Property(e => e.Arch).HasMaxLength(50);
            entity.Property(e => e.RunnerVersion).HasMaxLength(50);
            entity.Property(e => e.RequestIp).HasMaxLength(64);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            // One live request per user code. The filter ends the constraint the moment a request
            // settles, so codes are recycled rather than exhausted over the table's lifetime.
            entity.HasIndex(e => e.UserCode)
                .IsUnique()
                .HasFilter("status = 'Pending'")
                .HasDatabaseName("ix_runner_pairing_requests_user_code_pending");
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasOne<RunnerDevice>().WithMany()
                .HasForeignKey(e => e.ApprovedDeviceId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Organization>().WithMany()
                .HasForeignKey(e => e.ApprovedOrganizationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(e => e.ApprovedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UsageEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ProviderKind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.BillingMode).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ModelName).HasMaxLength(200);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(200);
            entity.Property(e => e.Quantity).HasPrecision(28, 6);
            entity.Property(e => e.CostUsd).HasPrecision(18, 8);
            entity.Property(e => e.Variant).HasMaxLength(64);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.OrganizationId, e.OccurredAt });
            entity.HasIndex(e => e.ExecutionId);
            // Ledger rows are append-only; only a hard organization purge may remove them.
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RateCard>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProviderKind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.UsageKind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ModelPattern).HasMaxLength(200);
            entity.Property(e => e.CreditsMillisPerUnit).HasPrecision(28, 10);
            entity.Property(e => e.CostUsdPerUnit).HasPrecision(28, 12);
            entity.HasIndex(e => new { e.ProviderKind, e.UsageKind, e.ModelPattern, e.EffectiveFrom }).IsUnique();
        });

        modelBuilder.Entity<RateMultiplier>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BillingMode).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.UsageKind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Multiplier).HasPrecision(12, 6);
            entity.HasIndex(e => new { e.BillingMode, e.UsageKind, e.EffectiveFrom }).IsUnique().AreNullsDistinct(false);
        });

        modelBuilder.Entity<Plan>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).HasMaxLength(50);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.PriceMonthlyUsd).HasPrecision(12, 2);
            entity.Property(e => e.EntitlementsJson).HasColumnType("jsonb");
            entity.Property(e => e.ProviderPriceIdsJson).HasColumnType("jsonb");
            entity.HasIndex(e => e.Key).IsUnique();
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.ProviderSubscriptionId).HasMaxLength(200);
            entity.HasIndex(e => new { e.OrganizationId, e.Status });
            entity.HasIndex(e => new { e.Provider, e.ProviderSubscriptionId }).IsUnique();
            entity.Property(e => e.TermOpeningTransactionId).HasColumnType("text");
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Plan>().WithMany().HasForeignKey(e => e.PlanId).OnDelete(DeleteBehavior.Restrict);
            // A downgrade held until the period ends (BillingEventProcessor); restricted like PlanId.
            entity.HasOne<Plan>().WithMany().HasForeignKey(e => e.ScheduledPlanId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CreditAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OrganizationId).IsUnique();
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        // The ledger is append-only; only a hard organization purge removes rows (cascade).
        modelBuilder.Entity<CreditLedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(200);
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.AllocationsJson).HasColumnType("jsonb");
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.AccountId, e.CreatedAt });
            entity.HasIndex(e => e.RefEntryId);
            entity.HasIndex(e => e.ExecutionId);
            entity.HasOne<CreditAccount>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CreditGrant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);
            entity.HasIndex(e => new { e.AccountId, e.ExpiresAt });
            entity.HasOne<CreditAccount>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        // The authoritative assistant/MCP allowance counter (decision 9): one row per turn or tool call, read and
        // written only under the org's ledger advisory lock. Removed with the organization (cascade).
        modelBuilder.Entity<AssistantAllowanceHold>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(200);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TrialClaim>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmailSha256).HasMaxLength(64);
            entity.Property(e => e.FirstVideoDeniedReason).HasMaxLength(32);
            entity.HasIndex(e => e.EmailSha256).IsUnique();
        });

        modelBuilder.Entity<BillingCustomer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.ProviderCustomerId).HasMaxLength(200);
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.HasIndex(e => new { e.OrganizationId, e.Provider }).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.ProviderCustomerId }).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BillingWebhookEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.EventId).HasMaxLength(200);
            entity.Property(e => e.EventType).HasMaxLength(100);
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.Outcome).HasMaxLength(32);
            entity.HasIndex(e => new { e.Provider, e.EventId }).IsUnique();
            entity.HasIndex(e => e.ProcessedAt);
        });

        // saas-billing: the money actually received, promoted out of the retained webhook payload.
        // Written by BillingEventProcessor for every completed transaction and backfilled once from the
        // payloads already stored. No credential, no personal data, and no FK to organizations: the
        // revenue trail outlives an account purge, exactly like billing_webhook_events.
        modelBuilder.Entity<BillingPayment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.ProviderTransactionId).HasMaxLength(200);
            entity.Property(e => e.ProviderSubscriptionId).HasMaxLength(200);
            entity.Property(e => e.CurrencyCode).HasMaxLength(10);
            entity.HasIndex(e => new { e.Provider, e.ProviderTransactionId }).IsUnique();
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.OrganizationId);
        });

        // Approved refunds and chargebacks, one row per provider adjustment (idempotent). Same posture as
        // billing_payments: no personal data, no FK to organizations.
        modelBuilder.Entity<BillingRefund>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.ProviderAdjustmentId).HasMaxLength(200);
            entity.Property(e => e.ProviderTransactionId).HasMaxLength(200);
            entity.Property(e => e.CurrencyCode).HasMaxLength(10);
            entity.HasIndex(e => new { e.Provider, e.ProviderAdjustmentId }).IsUnique();
            entity.HasIndex(e => e.OccurredAt);
        });

        // saas-promotions: admin-run promotions, their redemptions and manual credit grants. Access
        // is platform-admin or org-scoped in code, so none of these carries the Tenant filter.
        modelBuilder.Entity<Promotion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Name).HasMaxLength(120);
            entity.Property(e => e.PublicHeadline).HasMaxLength(120);
            entity.Property(e => e.Code).HasMaxLength(32);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.DiscountType).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.DiscountAmount).HasMaxLength(32);
            entity.Property(e => e.CurrencyCode).HasMaxLength(3);
            entity.Property(e => e.DurationKind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.TargetPlanKeysJson).HasColumnType("jsonb");
            entity.Property(e => e.TargetPackCreditsJson).HasColumnType("jsonb");
            entity.Property(e => e.TargetPriceIdsJson).HasColumnType("jsonb");
            entity.Property(e => e.ProviderDiscountId).HasMaxLength(64);
            // Null means automatic; several automatic promotions coexist.
            entity.HasIndex(e => e.Code).IsUnique().HasFilter("code IS NOT NULL");
            entity.HasIndex(e => e.ProviderDiscountId);
            entity.HasIndex(e => new { e.Kind, e.Status });
        });

        modelBuilder.Entity<PromotionRedemption>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ProviderTransactionId).HasMaxLength(200);
            entity.Property(e => e.DiscountTotal).HasMaxLength(32);
            entity.Property(e => e.CurrencyCode).HasMaxLength(3);
            entity.Property(e => e.ProviderSubscriptionId).HasMaxLength(64);
            // A webhook replay records a discount application once.
            entity.HasIndex(e => new { e.PromotionId, e.ProviderTransactionId })
                .IsUnique()
                .HasFilter("provider_transaction_id IS NOT NULL");
            // S-11: a plan discount is recorded once per (promotion, subscription), even under a race.
            entity.HasIndex(e => new { e.PromotionId, e.ProviderSubscriptionId })
                .IsUnique()
                .HasFilter("provider_subscription_id IS NOT NULL");
            entity.HasIndex(e => new { e.PromotionId, e.OrganizationId });
            entity.HasIndex(e => new { e.PromotionId, e.UserId });
            entity.Property(e => e.Overshoot).HasDefaultValue(false);
            // C10: existing rows are uses; a refused grant or count is NotGranted with its reason.
            entity.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(32).HasDefaultValue(PromotionRedemptionOutcome.Granted);
            entity.Property(e => e.NotGrantedReason).HasConversion<string>().HasMaxLength(50);
            // a plan change outside the promotion's targets removed the subscription's discount.
            entity.Property(e => e.DiscountRemovedForPriceId).HasMaxLength(64);
            entity.HasOne<Promotion>().WithMany().HasForeignKey(e => e.PromotionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PromotionCheckout>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProviderTransactionId).HasMaxLength(200);
            entity.Property(e => e.PriceId).HasMaxLength(200);
            entity.HasIndex(e => e.ProviderTransactionId).IsUnique();
            entity.HasIndex(e => new { e.DiscountPromotionId, e.OrganizationId });
            entity.HasIndex(e => new { e.BonusPromotionId, e.OrganizationId });
            entity.HasIndex(e => new { e.DiscountPromotionId, e.UserId });
            entity.HasIndex(e => new { e.BonusPromotionId, e.UserId });
            // C10: reservations created before the flag existed are never honoured past a cap.
            entity.Property(e => e.WithinCapsAtReservation).HasDefaultValue(false);
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminCreditGrant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            entity.HasIndex(e => e.RequestId).IsUnique();
            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        // A manual plan grant's audit trail. SubscriptionId is deliberately not a foreign key: the
        // audit must outlive the row it describes, exactly like admin_credit_grants' ledger entry id.
        modelBuilder.Entity<AdminSubscriptionGrant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PlanKey).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            entity.Property(e => e.RevokeReason).HasMaxLength(500);
            entity.HasIndex(e => e.RequestId).IsUnique();
            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
            entity.HasIndex(e => e.SubscriptionId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        // Lost-webhook recovery: "a change was sent, its webhook is due", one row per org. The reconciler
        // writes only this row itself; every subscription/ledger change goes through the webhook processor.
        modelBuilder.Entity<BillingReconcileWatch>(entity =>
        {
            entity.HasKey(e => e.OrganizationId);
            entity.Property(e => e.ChangeKind).HasMaxLength(32);
            entity.Property(e => e.LastOutcome).HasMaxLength(32);
            entity.Property(e => e.LastError).HasMaxLength(500);
            entity.HasIndex(e => e.ChangeRequestedAt);
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        // Audit of platform-admin billing actions (a forced resync from the provider). An audit trail must
        // outlive the organization it is about: organization_id is a plain indexed column, no foreign key.
        modelBuilder.Entity<BillingAdminAction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(32);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Outcome).HasMaxLength(32);
            entity.Property(e => e.DetailJson).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
        });

        // Content moderation audit (content-moderation T-002): append-only, no raw content, and
        // deliberately NO tenant query filter — the platform-admin review queue reads across
        // organizations and the WorkflowEngine writes rows outside a tenant scope. organization_id
        // is a plain indexed column, like billing_admin_actions: an audit row outlives the org.
        modelBuilder.Entity<ContentModerationDecision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Surface).HasMaxLength(32);
            entity.Property(e => e.Action).HasMaxLength(16);
            entity.Property(e => e.ContentKind).HasMaxLength(16);
            entity.Property(e => e.ContentSha256).HasMaxLength(64);
            entity.Property(e => e.SourceVersion).HasMaxLength(128);
            entity.Property(e => e.ProviderKind).HasMaxLength(32);
            entity.Property(e => e.Model).HasMaxLength(200);
            entity.Property(e => e.ModelSnapshot).HasMaxLength(200);
            entity.Property(e => e.TopCategory).HasMaxLength(64);
            entity.Property(e => e.ReasonCode).HasMaxLength(64);
            entity.Property(e => e.ReasonMessage).HasMaxLength(300);
            entity.Property(e => e.ReviewState).HasMaxLength(16);
            entity.Property(e => e.FlaggedCategoriesJson).HasColumnType("jsonb");
            entity.Property(e => e.ScoresJson).HasColumnType("jsonb");
            entity.Property(e => e.InputTypesJson).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
            entity.HasIndex(e => new { e.Action, e.ReviewState });
            entity.HasIndex(e => e.ContentSha256);
        });

        // Output moderation verdicts: the one stored publish verdict per (project, storage key), bound to
        // the object's version and never expiring. A project child reached only through an authorized
        // project (or the background worker), so no tenant filter; cascade-deleted with the project.
        modelBuilder.Entity<Services.Moderation.OutputModerationVerdict>(entity =>
        {
            entity.ToTable("output_moderation_verdicts");
            entity.HasKey(e => e.Id);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.StorageKey).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.SourceVersion).HasMaxLength(128).IsRequired();
            entity.Property(e => e.State).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ReasonCode).HasMaxLength(64);
            entity.Property(e => e.ReasonMessage).HasMaxLength(300);
            entity.Property(e => e.ModelSnapshot).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.StorageKey }).IsUnique();
            // The sweep's "due retry" lookup.
            entity.HasIndex(e => e.NextAttemptAt)
                .HasDatabaseName("ix_output_moderation_verdicts_next_attempt_at")
                .HasFilter("next_attempt_at IS NOT NULL");
        });

        // Saved post texts: one row per (project, output, platform, language). A project child reached only through the
        // project-authorized post-text routes, so no tenant filter; cascade-deleted with the project.
        modelBuilder.Entity<Services.PostTexts.PostTextRow>(entity =>
        {
            entity.ToTable("post_texts");
            entity.HasKey(e => e.Id);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Platform).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Language).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Body).HasMaxLength(6000).IsRequired();
            entity.Property(e => e.HashtagsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Tone).HasMaxLength(60);
            entity.HasIndex(e => new { e.ProjectId, e.OutputId, e.Platform, e.Language })
                .HasDatabaseName("ux_post_texts_project_output_platform_language")
                .IsUnique();
        });
        // saas-promotions: marketing consent, one current-state row per normalized email plus an
        // append-only event log. Per individual email, never per org: no Tenant filter. The Go API
        // writes the signup opt-in into these tables inside its registration transaction.
        modelBuilder.Entity<MarketingConsent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.WordingVersion).HasMaxLength(64);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Ip).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(256);
            entity.Property(e => e.UndeliverableReason).HasMaxLength(100);
            entity.Property(e => e.FirstName).HasMaxLength(100);
            entity.Property(e => e.LastName).HasMaxLength(100);
            entity.Property(e => e.NameSource).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.BrevoPushErrorCode).HasMaxLength(100);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.Status, e.RequestedAt });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MarketingConsentEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.Property(e => e.Action).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.WordingVersion).HasMaxLength(64);
            entity.Property(e => e.Ip).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(256);
            entity.HasIndex(e => new { e.ConsentId, e.OccurredAt });
            entity.HasOne<MarketingConsent>().WithMany().HasForeignKey(e => e.ConsentId).OnDelete(DeleteBehavior.Cascade);
        });

        // brevo-marketing-campaigns: platform-wide, no tenant filter. The "one active campaign" partial
        // unique index is raw SQL in the migration (an expression index EF cannot model); the service
        // checks first and maps a violation to 409.
        modelBuilder.Entity<MarketingCampaign>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Subject).HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.TestContentHash).HasMaxLength(64);
            entity.Property(e => e.FailureCode).HasMaxLength(100);
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => e.CreatedAt);
        });

        // new-user funnel (docs/design/new-user-funnel.md section 5): tracked links, platform-wide, no tenant
        // filter. The CHECKs are the closed sets of AcquisitionVocabulary plus the open-redirect floor on
        // the destination (an own-origin path: one leading slash, never two in a row); the admin API
        // validates the full shape before a row is written and the redirect re-checks it before it answers.
        modelBuilder.Entity<MarketingLink>(entity =>
        {
            entity.ToTable("marketing_links", t =>
            {
                t.HasCheckConstraint(
                    "ck_marketing_links_channel",
                    $"channel IN ({AcquisitionVocabulary.SqlInList(AcquisitionVocabulary.LinkChannels)})");
                t.HasCheckConstraint(
                    "ck_marketing_links_medium",
                    $"medium IN ({AcquisitionVocabulary.SqlInList(AcquisitionVocabulary.LinkMedia)})");
                t.HasCheckConstraint(
                    "ck_marketing_links_persona",
                    $"persona IS NULL OR persona IN ({AcquisitionVocabulary.SqlInList(AcquisitionVocabulary.Personas)})");
                t.HasCheckConstraint(
                    "ck_marketing_links_destination_path",
                    "destination_path LIKE '/%' AND destination_path NOT LIKE '%//%'");
                t.HasCheckConstraint(
                    "ck_marketing_links_cost_pair",
                    "(cost_minor IS NULL) = (cost_currency IS NULL) AND (cost_minor IS NULL OR cost_minor >= 0)");
            });
            entity.HasKey(e => e.Id).HasName("pk_marketing_links");
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Code).HasMaxLength(40);
            entity.Property(e => e.Channel).HasMaxLength(32);
            entity.Property(e => e.Medium).HasMaxLength(32);
            entity.Property(e => e.Campaign).HasMaxLength(64);
            entity.Property(e => e.Placement).HasMaxLength(160);
            entity.Property(e => e.DestinationPath).HasMaxLength(200);
            entity.Property(e => e.Persona).HasMaxLength(32);
            entity.Property(e => e.CostCurrency).HasMaxLength(3);
            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("ux_marketing_links_code");
            // The creator is kept for the record only; the link outlives the admin's account.
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // One counter row per link and UTC day. No visitor, IP address or user agent: the redirect's upsert
        // (INSERT ... ON CONFLICT (link_id, day) DO UPDATE) adds one and stores nothing else.
        modelBuilder.Entity<MarketingLinkDailyStat>(entity =>
        {
            entity.ToTable("marketing_link_daily_stats", t => t.HasCheckConstraint(
                "ck_marketing_link_daily_stats_non_negative", "clicks >= 0 AND bot_hits >= 0"));
            entity.HasKey(e => new { e.LinkId, e.Day }).HasName("pk_marketing_link_daily_stats");
            entity.HasOne<MarketingLink>()
                .WithMany()
                .HasForeignKey(e => e.LinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Single-row state of the Brevo contact sync worker.
        modelBuilder.Entity<MarketingContactSyncState>(entity =>
        {
            entity.ToTable("marketing_contact_sync_state");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.LastErrorCode).HasMaxLength(100);
        });

        // Outbox of Brevo contacts to delete after an account purge; the address is protected, never plain.
        modelBuilder.Entity<MarketingBrevoErasure>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProtectedEmail).HasColumnType("text");
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<MarketingSuppression>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmailHmac).HasMaxLength(64);
            entity.HasIndex(e => e.EmailHmac).IsUnique();
        });

        modelBuilder.Entity<MarketingConsentExport>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExportedAt);
        });

        // saas-promotions C2: persistent fixed-window mail caps, shared with the Go mail relay, which
        // writes it with raw SQL (the C2 upsert) and never migrates. Buckets hold hashes or domains,
        // no personal data; no Tenant filter (the caps are platform-wide).
        modelBuilder.Entity<MailRateCounter>(entity =>
        {
            entity.ToTable("mail_rate_counters");
            entity.HasKey(e => new { e.Bucket, e.WindowStart }).HasName("pk_mail_rate_counters");
            entity.Property(e => e.Bucket).HasColumnType("text");
        });

        // funnel-first-run-help: one row per user, ever (the at-most-one-reminder claim). Keyed by the person,
        // never addressable through a route, so no tenant filter.
        modelBuilder.Entity<Services.FirstRunReminder.FirstRunReminderRecord>(entity =>
        {
            entity.ToTable("first_run_reminders");
            entity.HasKey(e => e.UserId).HasName("pk_first_run_reminders");
            entity.Property(e => e.State).HasMaxLength(16);
            entity.Property(e => e.Locale).HasMaxLength(8);
            entity.Property(e => e.ConsentMode).HasMaxLength(32);
            entity.Property(e => e.ErrorCode).HasMaxLength(64);
            entity.HasIndex(e => e.ClaimedAt);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // F-1: a same-period upgrade grants its credit difference only once its proration transaction is
        // paid. The upgrade and the payment meet on one row, whichever webhook lands first; a transaction
        // is stored at most once per provider, so a redelivered payment cannot match a second upgrade.
        modelBuilder.Entity<BillingPendingUpgradeGrant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.ProviderSubscriptionId).HasMaxLength(200);
            entity.Property(e => e.TransactionId).HasMaxLength(200);
            // Security R-2: an upgrade and its payment pair only on exactly equal items (canonical text, unbounded).
            entity.Property(e => e.TargetItems).HasColumnType("text");
            entity.Property(e => e.ChargedItems).HasColumnType("text");
            entity.HasIndex(e => new { e.Provider, e.ProviderSubscriptionId });
            entity.HasIndex(e => new { e.Provider, e.TransactionId }).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        // F10 retention marks: the durable half of the two-phase delete (soft-mark, then purge
        // after a grace period). Written by the API's retention sweep, stamped purged by its purge
        // pass, and garbage-collected once nothing references the key any more.
        modelBuilder.Entity<StorageRetentionMark>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StorageKey).HasMaxLength(1000);
            entity.Property(e => e.Category).HasMaxLength(50);
            // An object is marked at most once, so a re-run of the sweep is idempotent.
            entity.HasIndex(e => e.StorageKey).IsUnique();
            entity.HasIndex(e => new { e.OrganizationId, e.MarkedAt });
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Slug).IsUnique();
            // One Personal workspace per creator.
            entity.HasIndex(e => e.CreatedByUserId)
                .IsUnique()
                .HasFilter("kind = 'Personal'")
                .HasDatabaseName("ix_organizations_created_by_user_id_personal");
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Slug).HasMaxLength(100);
            entity.Property(e => e.Kind)
                .HasConversion<string>()
                .HasMaxLength(50);
            entity.Property(e => e.ComputePreference)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ComputePreference.Auto);
            // SetNull: deleting a user must not delete a Team org they created. A Personal org is
            // removed explicitly by whatever deletes the user (A3b).
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OrganizationMembership>(entity =>
        {
            entity.HasKey(e => new { e.OrganizationId, e.UserId });
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Role)
                .HasConversion<string>()
                .HasMaxLength(50);
            entity.HasOne(e => e.Organization)
                .WithMany(o => o.Memberships)
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrganizationInvite>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.OrganizationId);
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.Property(e => e.TokenHash).HasMaxLength(128);
            entity.Property(e => e.Role)
                .HasConversion<string>()
                .HasMaxLength(50);
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.InvitedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OrganizationAgentProviderOverride>(entity =>
        {
            entity.HasKey(e => new { e.OrganizationId, e.AgentType });
            entity.Property(e => e.AgentType)
                .HasConversion<string>()
                .HasMaxLength(100);
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            // InferenceProviderId names a model capability (docs/design/model-catalog.md §4.1 step 4).
            entity.HasOne(e => e.InferenceProvider)
                .WithMany()
                .HasForeignKey(e => e.InferenceProviderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Owner)
                .WithMany(u => u.Projects)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Status)
                .HasConversion<string>();
            entity.HasIndex(e => e.OrganizationId);
            entity.Property(e => e.ObjectEffectsConsentAt).HasColumnName("object_effects_consent_at");
            entity.Property(e => e.VideoEnhanceConsentAt).HasColumnName("video_enhance_consent_at");
            // The avatar media consent (docs/design/avatar-objects.md): given once in the app, never by the assistant.
            entity.Property(e => e.SubjectMediaConsentAt).HasColumnName("subject_media_consent_at");
            entity.Property(e => e.SubjectMediaConsentByUserId).HasColumnName("subject_media_consent_by_user_id");
            entity.Property(e => e.AnalyzeOnUpload).HasColumnName("analyze_on_upload").HasDefaultValue(false);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.FolderId);
            entity.HasOne(e => e.Folder)
                .WithMany()
                .HasForeignKey(e => e.FolderId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // Project organization: free tags (unique per organization, case-insensitively via NameKey), one level of folders.
        modelBuilder.Entity<ProjectTag>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(40).IsRequired();
            entity.Property(e => e.NameKey).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Color).HasMaxLength(16).IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.NameKey }).IsUnique();
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable("project_tags");
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // No tenant filter: guarded at the project (parent IS filtered) and the tag; every write verifies both ids.
        modelBuilder.Entity<ProjectTagLink>(entity =>
        {
            entity.HasKey(e => new { e.ProjectId, e.TagId });
            entity.HasIndex(e => e.TagId);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.TagLinks)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Tag)
                .WithMany(t => t.Links)
                .HasForeignKey(e => e.TagId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable("project_tag_links");
        });

        modelBuilder.Entity<ProjectFolder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(60).IsRequired();
            entity.Property(e => e.NameKey).HasMaxLength(60).IsRequired();
            // NULL parents never collide in a plain unique index, so v1's only case (parent NULL) gets a filtered one.
            entity.HasIndex(e => new { e.OrganizationId, e.NameKey })
                .IsUnique()
                .HasFilter("parent_folder_id IS NULL");
            entity.HasIndex(e => new { e.OrganizationId, e.ParentFolderId, e.NameKey })
                .IsUnique()
                .HasFilter("parent_folder_id IS NOT NULL");
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable("project_folders");
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // Object-analysis requests (magic blur / mask in the editor): the intent behind a hidden ObjectAnalysis run.
        // Owned here. No tenant filter: a project child reached only through an authorized project (TenantBoundaryInventoryTests).
        // ExecutionId has no FK (engine-owned table, absent when this context migrates).
        modelBuilder.Entity<ObjectEffectRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExecutionId);
            entity.HasIndex(e => new { e.ProjectId, e.SourceId, e.Prompt });
            // The in-flight identity index ux_object_effect_requests_inflight is an EXPRESSION index (coalesce(timeline_id, zero guid)),
            // which the model cannot declare: it is created by migration AddObjectEffects with raw SQL, like the repo's other expression indexes.
            // One in-flight analysis per identical (source, prompt, scope, timeline intent); partial, so finished rows never block a repeat.
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.SourceKind).HasMaxLength(16);
            entity.Property(e => e.SourceId).HasMaxLength(64);
            entity.Property(e => e.Prompt).HasMaxLength(ObjectEffectRequest.MaxPromptLength);
            entity.Property(e => e.MatchScope).HasMaxLength(32);
            entity.Property(e => e.Effect).HasMaxLength(32);
            entity.Property(e => e.TimelineItemIdsJson).HasColumnType("jsonb");
            entity.Property(e => e.Status).HasMaxLength(16);
            entity.Property(e => e.FailureCode).HasMaxLength(64);
            entity.Property(e => e.FailureMessage).HasMaxLength(1000);
            entity.ToTable("object_effect_requests");
        });

        // Zip unpack requests: a project child reached only through an authorized project or the background
        // worker (TenantBoundaryInventoryTests). The zip row is a nullable FK so removing it keeps the history.
        modelBuilder.Entity<ProjectArchiveUnpack>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
            // One active unpack per zip; finished rows never block a retry.
            entity.HasIndex(e => e.SourceFileId)
                .IsUnique()
                .HasFilter("status IN ('Pending', 'Running')");
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectFile>()
                .WithMany()
                .HasForeignKey(e => e.SourceFileId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.ArchiveName).HasMaxLength(255);
            entity.Property(e => e.TargetDirectory).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.FailureCode).HasMaxLength(64);
            entity.Property(e => e.SkippedJson).HasColumnType("jsonb");
            entity.ToTable("project_archive_unpacks");
        });

        modelBuilder.Entity<ProjectFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Category, e.DirectoryPath });
            entity.HasIndex(e => new { e.ProjectId, e.Category, e.UploadedAt });
            // The repository-pull reconciliation key: "which rows of this project did a pull write,
            // and what revision is each at". Null for every user upload and agent write.
            entity.HasIndex(e => new { e.ProjectId, e.SourceKind });
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Files)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.SummaryStatus)
                .HasConversion<string>();
            entity.Property(e => e.IndexingStatus)
                .HasConversion<string>();
            // CSAM scan verdict. New rows default to Pending in the entity initializer; the DATABASE
            // default is NotScanned so a row written before this control existed is not swept as work.
            entity.Property(e => e.CsamStatus)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(CsamScanStatus.NotScanned);
            entity.Property(e => e.CsamClassification).HasMaxLength(100);
            entity.Property(e => e.CsamMatchType).HasMaxLength(50);
            entity.Property(e => e.CsamQuarantineKey).HasMaxLength(1000);
            entity.HasIndex(e => new { e.ProjectId, e.CsamStatus });
            entity.HasIndex(e => new { e.CsamStatus, e.CsamScanClaimedAt });

            entity.Property(e => e.ModerationStatus)
                .HasConversion<string>()
                .HasDefaultValue(FileModerationStatus.Unmoderated);
            entity.Property(e => e.StorageMetadataJson)
                .HasColumnType("jsonb");

            // New fields for folder structure and categorization
            entity.Property(e => e.OriginalPath)
                .HasMaxLength(1000);
            entity.Property(e => e.DirectoryPath)
                .HasMaxLength(1000);
            entity.Property(e => e.Category)
                .HasMaxLength(50)
                .HasDefaultValue("userFiles");
            entity.Property(e => e.StorageFileName)
                .HasMaxLength(260);
            // Project-history content identity (AddProjectVersioning): a lowercase hex SHA-256 of the
            // stored bytes and the ETag it was taken at. Both null until the hash sweeper (or a writer)
            // fills them; both are cleared when the object is overwritten or moved.
            entity.Property(e => e.ContentSha256)
                .HasMaxLength(64);
            entity.Property(e => e.ContentSha256ETag)
                .HasMaxLength(200);
            entity.Property(e => e.SourceKind)
                .HasMaxLength(50);
            entity.Property(e => e.SourceRevision)
                .HasMaxLength(100);
            // The computer-folder link that wrote the row (docs/design/local-folder-sync-and-storage.md).
            // Unlinking keeps the files: the column is nulled and they become ordinary files.
            entity.HasIndex(e => e.SourceLinkId);
            entity.HasOne<ProjectLocalFolderLink>()
                .WithMany()
                .HasForeignKey(e => e.SourceLinkId)
                .OnDelete(DeleteBehavior.SetNull);
            // A file is visible exactly when its project is; written out (not via the Project filter)
            // so the predicate stays a single translatable expression.
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.Project.OrganizationId == TenantOrgId));
        });

        // CSAM scan audits (Projects Arachnid Shield). API-owned, and deliberately NOT tenant-filtered:
        // a row has no caller-chosen id of its own — it is reached only through a project file (the
        // admin-only query joins the file, and the migration is a schema file), so the file is the
        // tenant boundary. It stores hashes and verdicts, never content.
        // Platform-admin user-account audit: append-only, written by the Go API by raw SQL, platform-wide (no
        // tenant filter) and with no FK on either user id so deleting an account keeps its audit.
        modelBuilder.Entity<AdminAuditEvent>(entity =>
        {
            entity.ToTable("admin_audit_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ChangedFields).HasColumnType("jsonb");
            entity.Property(e => e.ClientIp).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.TargetUserId, e.CreatedAt });
            entity.HasIndex(e => new { e.ActorUserId, e.CreatedAt });
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<CsamScanAudit>(entity =>
        {
            entity.ToTable("csam_scan_audits");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectFileId, e.CreatedAt });
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => e.ProjectId);
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.Classification).HasMaxLength(100);
            entity.Property(e => e.MatchType).HasMaxLength(50);
            entity.Property(e => e.PdqHashBase64).HasMaxLength(64);
            entity.Property(e => e.ContentSha256Hex).HasMaxLength(64);
            entity.Property(e => e.Source).HasMaxLength(20);
            entity.Property(e => e.ProviderVersion).HasMaxLength(100);
            entity.Property(e => e.ErrorCode).HasMaxLength(50);
            entity.Property(e => e.ProjectFileName).HasMaxLength(260);
            // Evidence outlives the file: SET NULL, never cascade. ProjectId has no FK at all.
            entity.HasOne(e => e.ProjectFile)
                .WithMany()
                .HasForeignKey(e => e.ProjectFileId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Recipe marketplace (docs/design/recipe-marketplace.md). Published recipes are public by design, so none of
        // these tables carries the Tenant filter; owner actions check author_org_id in the service. No FK to
        // engine-owned tables (workflow_id is a plain column) and none to users or organizations: the account
        // purge deletes authored recipes and the user's ratings/reports explicitly.
        modelBuilder.Entity<MarketplaceRecipe>(entity =>
        {
            entity.ToTable("marketplace_recipes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Name).HasMaxLength(80);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Category).HasMaxLength(64);
            entity.Property(e => e.Language).HasMaxLength(16);
            entity.Property(e => e.CostClass).HasMaxLength(32);
            entity.Property(e => e.StepTypes).HasColumnType("text[]");
            entity.Property(e => e.RemovedReason).HasMaxLength(500);
            entity.Property(e => e.ConsentVersion).HasMaxLength(32);
            entity.Property(e => e.AuthorLabel).HasMaxLength(120);
            entity.Property(e => e.Revision).IsConcurrencyToken();
            if (Database.IsNpgsql())
            {
                entity.HasGeneratedTsVectorColumn(e => e.SearchVector, "simple", e => new { e.Name, e.Description, e.Category })
                    .HasIndex(e => e.SearchVector)
                    .HasMethod("GIN");
            }
            else
            {
                // The InMemory test provider has no tsvector type; the column exists only on PostgreSQL.
                entity.Ignore(e => e.SearchVector);
            }
            entity.HasIndex(e => new { e.Status, e.ListedAt, e.Id });
            entity.HasIndex(e => new { e.Category, e.Status });
            entity.HasIndex(e => new { e.AuthorOrgId, e.Status });
            entity.HasIndex(e => e.AuthorUserId);
        });

        modelBuilder.Entity<MarketplaceRecipeVersion>(entity =>
        {
            entity.ToTable("marketplace_recipe_versions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SnapshotJson).HasColumnType("jsonb");
            entity.Property(e => e.ManifestJson).HasColumnType("jsonb");
            entity.Property(e => e.PreviewSourceJson).HasColumnType("jsonb");
            entity.Property(e => e.CheckJson).HasColumnType("jsonb");
            entity.Property(e => e.PreviewKey).HasMaxLength(300);
            entity.Property(e => e.PosterKey).HasMaxLength(300);
            entity.Property(e => e.PreviewSha256).HasMaxLength(64);
            entity.Property(e => e.PosterSha256).HasColumnName("poster_sha256").HasMaxLength(64);
            entity.Property(e => e.PreviewRunNonce).HasColumnName("preview_run_nonce").HasMaxLength(32);
            entity.Property(e => e.PreviewStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.ConsentVersion).HasMaxLength(32);
            entity.HasIndex(e => new { e.RecipeId, e.N }).IsUnique();
            entity.HasIndex(e => new { e.SubmittedByUserId, e.CreatedAt });
            entity.Property(e => e.SnapshotDigest).HasMaxLength(64);
            entity.HasIndex(e => e.SnapshotDigest);
            entity.HasOne(e => e.Recipe).WithMany(r => r.Versions).HasForeignKey(e => e.RecipeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MarketplaceCopy>(entity =>
        {
            entity.ToTable("marketplace_copies");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.RecipeId, e.OrgId }).IsUnique();
            entity.HasIndex(e => e.OrgId);
            entity.HasIndex(e => e.WorkflowId);
            entity.HasOne(e => e.Recipe).WithMany().HasForeignKey(e => e.RecipeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MarketplaceRating>(entity =>
        {
            entity.ToTable("marketplace_ratings");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.RecipeId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasOne(e => e.Recipe).WithMany().HasForeignKey(e => e.RecipeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MarketplaceReport>(entity =>
        {
            entity.ToTable("marketplace_reports");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Note).HasMaxLength(500);
            // One OPEN report per (recipe, reporter): a closed one does not block a later report.
            entity.HasIndex(e => new { e.RecipeId, e.ReporterUserId })
                .IsUnique()
                .HasFilter("state = 'Open'");
            entity.HasIndex(e => new { e.ReporterUserId, e.CreatedAt });
            entity.HasOne(e => e.Recipe).WithMany().HasForeignKey(e => e.RecipeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MarketplaceAuditEntry>(entity =>
        {
            entity.ToTable("marketplace_audit");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(50);
            entity.Property(e => e.ActorKind).HasMaxLength(20);
            entity.Property(e => e.DetailJson).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.RecipeId, e.CreatedAt });
            entity.HasIndex(e => e.ActorUserId);
        });

        modelBuilder.Entity<AgentDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Owner)
                .WithMany(u => u.AgentDefinitions)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.AgentType)
                .HasConversion<string>();
            entity.Property(e => e.ContextMode)
                .HasConversion<string>()
                .HasDefaultValue(ContextMode.LastStep);
            entity.Property(e => e.ConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.OutputSchemaJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.AssignedSkillsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ReasoningEffort)
                .HasMaxLength(16);
            // InferenceProviderId names a model capability (docs/design/model-catalog.md §4.1 step 4).
            entity.HasOne(e => e.InferenceProvider)
                .WithMany(c => c.AgentDefinitions)
                .HasForeignKey(e => e.InferenceProviderId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.OrganizationId);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            // Built-ins are shared (no org); every custom agent belongs to one.
            entity.ToTable(t => t.HasCheckConstraint("ck_agent_definitions_builtin_or_org", "is_built_in OR organization_id IS NOT NULL"));
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || e.IsBuiltIn || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // The model catalog (docs/design/model-catalog.md §2): provider account -> models -> capabilities
        // -> prices, plus the one pricing settings row. Platform rows carry a null organization on all
        // three filtered levels; BYO rows carry the org's id on all three (kept equal by the admin service).
        modelBuilder.Entity<InferenceProvider>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Name is unique PER owning org; NULL (platform-managed) counts as one scope, so two
            // platform rows still cannot share a name (NULLS NOT DISTINCT, PG15+).
            entity.HasIndex(e => new { e.OrganizationId, e.Name }).IsUnique().AreNullsDistinct(false);
            entity.HasIndex(e => e.OrganizationId);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Kind)
                .HasConversion<string>();
            entity.Property(e => e.ExtraHeadersJson)
                .HasColumnType("jsonb");
            // Platform-managed rows (null org) are visible to every tenant; BYO rows only to their org.
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || e.OrganizationId == null || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        modelBuilder.Entity<InferenceModel>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Unbounded text, like inference_providers.name and the pre-catalog model_name they are copied from (a
            // legacy value of any length migrates); the admin API and the assistant tools cap NEW values at 200.
            // A picker label is never ambiguous inside one scope; NULL (platform) is one scope.
            entity.HasIndex(e => new { e.OrganizationId, e.DisplayName }).IsUnique().AreNullsDistinct(false);
            entity.HasIndex(e => e.ProviderId);
            entity.HasOne(e => e.Provider)
                .WithMany(p => p.Models)
                .HasForeignKey(e => e.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || e.OrganizationId == null || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        modelBuilder.Entity<ModelCapability>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Capability)
                .HasConversion<string>()
                .HasDefaultValue(InferenceProviderCapability.Chat);
            entity.Property(e => e.PriceSource)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(ModelPriceSource.Table);
            entity.HasIndex(e => new { e.ModelId, e.Capability }).IsUnique();
            // One default PER (org, capability), the platform's NULL org being one scope (the rule the
            // old inference_providers default index enforced, moved here).
            entity.HasIndex(e => new { e.OrganizationId, e.Capability }).IsUnique().HasFilter("is_default").AreNullsDistinct(false);
            entity.HasOne(e => e.Model)
                .WithMany(m => m.Capabilities)
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || e.OrganizationId == null || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // No organization and no tenant filter: reached only through platform-admin endpoints or an
        // authorized parent capability (docs/design/model-catalog.md §3).
        modelBuilder.Entity<ModelCapabilityPrice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Unit).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Variant).HasMaxLength(64);
            entity.Property(e => e.CostUsdPerUnit).HasPrecision(28, 12);
            entity.Property(e => e.CreditsMillisPerUnitOverride).HasPrecision(28, 10);
            entity.HasIndex(e => new { e.ModelCapabilityId, e.Unit, e.Variant, e.EffectiveFrom }).IsUnique().AreNullsDistinct(false);
            entity.HasOne(e => e.ModelCapability)
                .WithMany(c => c.Prices)
                .HasForeignKey(e => e.ModelCapabilityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PricingSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Markup).HasPrecision(12, 6).HasDefaultValue(1.2m);
            entity.Property(e => e.CreditUsdValue).HasPrecision(18, 8).HasDefaultValue(0.006m);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Name).HasMaxLength(80).IsRequired();
            entity.Property(e => e.KeyId).HasMaxLength(12).IsRequired();
            entity.Property(e => e.SecretHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.LastFour).HasMaxLength(4).IsRequired();
            entity.Property(e => e.Scopes).HasColumnType("text[]");
            entity.Property(e => e.ProjectIds).HasColumnType("uuid[]");
            // Explicit: the snake_case convention would split "7d"/"1d" differently.
            entity.Property(e => e.ExpiryWarned7dAt).HasColumnName("expiry_warned_7d_at");
            entity.Property(e => e.ExpiryWarned1dAt).HasColumnName("expiry_warned_1d_at");
            entity.HasIndex(e => e.KeyId).IsUnique();
            entity.HasIndex(e => e.ServiceUserId).IsUnique();
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.ServiceUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.OrganizationId, e.RevokedAt });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            // created_by_user_id is audit only: a plain scalar, so a creator leaving never deletes the key.
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // One row per credit hold an API key made; written by the ledger (API and engine share the table),
        // reached only through the key, so no tenant filter.
        modelBuilder.Entity<ApiKeySpend>(entity =>
        {
            entity.ToTable("api_key_spend");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(e => e.ReservationEntryId).IsUnique();
            entity.HasIndex(e => new { e.ApiKeyId, e.CreatedAt });
            entity.HasOne<ApiKey>()
                .WithMany()
                .HasForeignKey(e => e.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Public API children (public-api T-002): reached only through the key that wrote them, so none of
        // them carries the tenant filter. The key id in the row (or in the composite primary key) is the guard.
        modelBuilder.Entity<ApiKeyAuditEvent>(entity =>
        {
            entity.ToTable("api_key_audit_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Method).HasMaxLength(10).IsRequired();
            // A route template, not the raw path or query: path values never reach the audit trail.
            entity.Property(e => e.Route).HasMaxLength(500).IsRequired();
            entity.Property(e => e.RequestId).HasMaxLength(100);
            entity.HasIndex(e => new { e.OrganizationId, e.OccurredAt });
            entity.HasIndex(e => new { e.ApiKeyId, e.OccurredAt });
            entity.HasIndex(e => e.OccurredAt);
        });

        modelBuilder.Entity<ApiKeyRateCounter>(entity =>
        {
            entity.ToTable("api_key_rate_counters");
            entity.HasKey(e => new { e.ApiKeyId, e.WindowStart }).HasName("pk_api_key_rate_counters");
            entity.HasIndex(e => e.WindowStart);
        });

        modelBuilder.Entity<ApiIdempotencyRecord>(entity =>
        {
            entity.ToTable("api_idempotency_records");
            entity.HasKey(e => new { e.ApiKeyId, e.Key }).HasName("pk_api_idempotency_records");
            entity.Property(e => e.Key).HasMaxLength(200).IsRequired();
            entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ResponseBody).HasColumnType("jsonb");
            // The sweeper's "older than the TTL" lookup.
            entity.HasIndex(e => e.CreatedAt);
        });

        // Queued public-API run starts (public-api T-007): reached only through the key, so no tenant filter.
        modelBuilder.Entity<ApiRunRequest>(entity =>
        {
            entity.ToTable("api_run_requests");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired();
            entity.Property(e => e.RequestJson).HasColumnType("jsonb");
            entity.Property(e => e.ErrorCode).HasMaxLength(100);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.HasIndex(e => new { e.ApiKeyId, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.Status, e.NotBefore });
            entity.HasOne<ApiKey>()
                .WithMany()
                .HasForeignKey(e => e.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Webhooks (public-api T-009). The endpoint is organization-owned and addressed by id (tenant filter); its
        // deliveries are reached only through it (no filter of their own); the worker runs under a system scope.
        modelBuilder.Entity<WebhookEndpoint>(entity =>
        {
            entity.ToTable("webhook_endpoints");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Url).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(200);
            entity.Property(e => e.Events).HasColumnType("text[]");
            entity.Property(e => e.ProjectIds).HasColumnType("uuid[]");
            entity.Property(e => e.SecretEncrypted).IsRequired();
            entity.Property(e => e.DisabledReason).HasMaxLength(200);
            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.ToTable("webhook_deliveries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.ProjectId);
            entity.Property(e => e.EventType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EventKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(300);
            entity.HasIndex(e => new { e.EndpointId, e.EventKey }).IsUnique();
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
            entity.HasIndex(e => new { e.EndpointId, e.CreatedAt });
            entity.HasOne<WebhookEndpoint>()
                .WithMany()
                .HasForeignKey(e => e.EndpointId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantThread>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.OrganizationId);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Messages)
                .WithOne()
                .HasForeignKey(e => e.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            // ProjectId is intentionally left as a plain scalar with no relationship configured:
            // a thread survives the deletion of the project it was started from.
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        modelBuilder.Entity<AssistantMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ThreadId, e.Sequence }).IsUnique();
            entity.Property(e => e.Content)
                .HasColumnType("text");
            entity.Property(e => e.MetadataJson)
                .HasColumnType("jsonb");
        });

        modelBuilder.Entity<AssistantRunWatch>(entity =>
        {
            entity.ToTable("assistant_run_watches");
            entity.HasKey(e => e.Id);
            // Reached only through its thread (tenant-filtered), so no filter of its own.
            entity.HasOne<AssistantThread>()
                .WithMany()
                .HasForeignKey(e => e.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.WorkflowName).HasMaxLength(AssistantRunWatch.WorkflowNameMaxLength);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.EndReason).HasMaxLength(64);
            entity.HasIndex(e => new { e.ThreadId, e.ExecutionId })
                .IsUnique()
                .HasFilter("status IN ('Pending','Claimed')");
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
        });

        modelBuilder.Entity<EditTimelineEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Name)
                .HasMaxLength(200);
            entity.Property(e => e.DocumentJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        modelBuilder.Entity<EditTimelineOp>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TimelineId, e.Revision }).IsUnique();
            entity.HasOne<EditTimelineEntity>()
                .WithMany()
                .HasForeignKey(e => e.TimelineId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.OpsJson)
                .HasColumnType("jsonb");
        });

        modelBuilder.Entity<EditTimelineSnapshot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TimelineId, e.Revision }).IsUnique();
            entity.HasOne<EditTimelineEntity>()
                .WithMany()
                .HasForeignKey(e => e.TimelineId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.DocumentJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.Name).HasMaxLength(80);
            entity.Property(e => e.Cause).HasMaxLength(32);
            entity.HasIndex(e => e.ExpiresAt);
        });

        // The image/layer document (docs/thumbnails-and-image-editing.md section P3). Owned here and
        // NOT by the engine: unlike edit_timelines, nothing in a workflow run reads a layer document —
        // it is an interactive editor's state — so the engine has no reason to map it, and giving it
        // read-only access would invite a step to depend on a document a user is mid-edit on.
        modelBuilder.Entity<EditImageDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            // SET NULL, not cascade (decision D10): deleting the source photo must not delete the picture.
            // The delete paths first give the picture its own copy (PictureSourceCopies); the FK action is
            // the backstop for any other path.
            entity.HasOne<ProjectFile>()
                .WithMany()
                .HasForeignKey(e => e.SourceProjectFileId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.Name)
                .HasMaxLength(200);
            entity.Property(e => e.BackgroundColor)
                .HasMaxLength(64);
            // Same concurrency rule as edit_timelines.Revision: two editors of one document must
            // collide rather than silently overwrite each other's layer list.
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        modelBuilder.Entity<EditImageLayer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DocumentId, e.ZOrder });
            entity.HasOne<EditImageDocument>()
                .WithMany(d => d.Layers)
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deliberately NO cascade from the source project file: deleting the image a subject was
            // cut from must not silently delete the cut-out the user has arranged. The FK is optional
            // and set to null instead, so the layer survives its source. The DOCUMENT keeps its
            // cascade, because a document with no source image cannot be re-segmented at all.
            entity.HasOne<ProjectFile>()
                .WithMany()
                .HasForeignKey(e => e.SourceProjectFileId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.Role)
                .HasMaxLength(32);
            entity.Property(e => e.Kind)
                .HasConversion<string>()
                .HasMaxLength(32);
            entity.Property(e => e.Label)
                .HasMaxLength(300);
            entity.Property(e => e.StorageKey)
                .HasMaxLength(1000);
            entity.Property(e => e.StorageBucket)
                .HasMaxLength(200);
            entity.Property(e => e.MatteStorageKey)
                .HasMaxLength(1000);
            entity.Property(e => e.MimeType)
                .HasMaxLength(200);
            entity.Property(e => e.TransformJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.SegmentationModel)
                .HasMaxLength(200);
            entity.Property(e => e.SegmentationPrompt)
                .HasMaxLength(500);
            // The thumbnail editor's columns (docs/design/thumbnail-editor.md section 2).
            // Deliberately NO HasDefaultValue here: for a bool, EF treats the CLR default (false) as
            // "unset" once a store default exists, so a layer saved with Visible = false would be
            // inserted as TRUE. The entity initialisers supply the values on every insert instead, and
            // the AddThumbnailEditor migration backfills existing rows to visible / unlocked / normal
            // with its own one-shot column defaults.
            entity.Property(e => e.BlendMode)
                .HasMaxLength(32);
            entity.Property(e => e.ContentJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ProvenanceJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.CropJson)
                .HasColumnType("jsonb");
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        // One flattened render of a document. A project file is the PNG itself; this row only makes
        // the exports of a document listable. The file link is SET NULL so tidying the user's files
        // never deletes the history, and the document link cascades like every other child row.
        modelBuilder.Entity<EditImageExport>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DocumentId, e.CreatedAt });
            entity.HasOne<EditImageDocument>()
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectFile>()
                .WithMany()
                .HasForeignKey(e => e.ProjectFileId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MediaDerivative>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            // Optional relationship (SourceProjectFileId is nullable): a derivative of an
            // unregistered step-result render has no ProjectFile row to point at. Cascade is kept
            // from the creating migration, so deleting a source file still takes its derivatives
            // with it while every step-result-only row stays untouched.
            entity.HasOne<ProjectFile>()
                .WithMany()
                .HasForeignKey(e => e.SourceProjectFileId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Kind)
                .HasConversion<string>();
            // The source discriminator, stored as a string for the same reason as Kind: a future
            // member is then a code-only change rather than a data migration.
            entity.Property(e => e.SourceKind)
                .HasConversion<string>();
            // The cache identity. Source lengths mirror the creating migration's own bounds
            // (character varying(N)); ParamsHash and CacheKey are exactly one lowercase hex SHA-256
            // each — see MediaDerivative's XML docs for why each column exists.
            entity.Property(e => e.SourceStorageKey)
                .HasMaxLength(1000);
            entity.Property(e => e.SourceETag)
                .HasMaxLength(200);
            entity.Property(e => e.ParamsHash)
                .HasMaxLength(64);
            entity.Property(e => e.ParamsJson)
                .HasColumnType("jsonb");
            // The whole identity, as one value: MediaDerivativeIdentity.Compute's output. 64 is
            // exactly a lowercase hex SHA-256, the same algorithm and case the consumer's
            // MediaDerivativeCacheKey.ComputeCacheKey emits.
            entity.Property(e => e.CacheKey)
                .HasMaxLength(64);
            entity.Property(e => e.StorageKey)
                .HasMaxLength(1000);
            entity.Property(e => e.StorageBucket)
                .HasMaxLength(200);
            // The filmstrip index sidecar pointer: same 1000 bound as StorageKey, because it holds
            // the same kind of value (an object key). Deliberately left nullable and deliberately
            // absent from the cache-key index — it names an index ABOUT a derivative, not the
            // derivative's content identity, so two runs of one spec share a CacheKey while only
            // the filmstrip SHEET rows carry this pointer. See MediaDerivative's XML docs.
            entity.Property(e => e.IndexStorageKey)
                .HasMaxLength(1000);
            entity.Property(e => e.MimeType)
                .HasMaxLength(200);
            // The SOURCE's probe as the derivative run measured it (MediaSourceProbe, written by the
            // engine; read by MediaController.ListMedia). Nullable: rows from before probes were
            // stored carry none until a later run backfills them.
            entity.Property(e => e.SourceProbeJson)
                .HasColumnType("jsonb");
            // Filtered (partial) unique index over the identity, following inference_providers'
            // filtered unique index: two different sources — and, because the kind and the variant
            // are components of the key, two different kinds or variants of ONE source — can never
            // collide onto one row, while a soft-deleted row never blocks re-producing its
            // derivative. Named explicitly so the constraint is greppable from an error message.
            //
            // The predecessor (ux_media_derivatives_identity) was over
            // (source_storage_key, source_etag, params_hash) — a triple that is not an identity: the
            // parameter hash covers the spec's knobs and builder version but neither the kind nor the
            // variant, so two kinds of one source sharing parameter JSON collided on it and the
            // second derivative could never be persisted at all.
            entity.HasIndex(e => e.CacheKey)
                .IsUnique()
                .HasDatabaseName("ux_media_derivatives_cache_key")
                .HasFilter("deleted_at IS NULL");
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        // GitHub repository links (docs/github-app.md). No tenant query filter, and that is the
        // reviewed decision rather than an omission: this is a child of a project, reached either
        // through the project access check on every /api/v1/projects/{projectId}/github action or,
        // from the webhook, under an explicit system scope from a signature-verified repository id.
        // Adding a filter here would only make the webhook path lie about what it can see.
        // A repository is linked at most once, and a project holds at most one link.
        modelBuilder.Entity<ProjectGitHubLink>(entity =>
        {
            // Explicit: the snake_case convention would split the product name into "git_hub".
            entity.ToTable("project_github_links");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.HasIndex(e => e.RepositoryId).IsUnique();
            entity.HasIndex(e => e.OrganizationId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.RepositoryFullName).HasMaxLength(300);
            entity.Property(e => e.RepositoryDefaultBranch).HasMaxLength(300);
            entity.Property(e => e.Branch).HasMaxLength(300);
            entity.Property(e => e.PathPrefix).HasMaxLength(1000);
            entity.Property(e => e.IncludePathsJson).HasColumnType("jsonb");
            entity.Property(e => e.LastPulledCommit).HasMaxLength(100);
            entity.Property(e => e.LastPulledDeliveryId).HasMaxLength(100);
            entity.Property(e => e.LastError).HasMaxLength(2000);
        });

        // Which organization owns a GitHub App installation. Unique per installation: that uniqueness
        // is the whole mechanism — see GitHubAppInstallation for why the claim exists at all.
        modelBuilder.Entity<GitHubAppInstallation>(entity =>
        {
            entity.ToTable("github_app_installations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.InstallationId).IsUnique();
            entity.HasIndex(e => e.OrganizationId);
            entity.Property(e => e.AccountLogin).HasMaxLength(300);
            entity.Property(e => e.RepositorySelection).HasMaxLength(50);
        });

        // Received webhook deliveries: the idempotency ledger and the pull work queue in one table.
        // Organization-less by design (the tenant is not known until the repository is resolved, and a
        // repository that is not linked still has to be recorded so its redelivery is a no-op).
        modelBuilder.Entity<GitHubWebhookDelivery>(entity =>
        {
            entity.ToTable("github_webhook_deliveries");
            entity.HasKey(e => e.Id);
            // GitHub's idempotency key. A redelivery collides here and does nothing.
            entity.HasIndex(e => e.DeliveryId).IsUnique();
            entity.HasIndex(e => new { e.ProcessedAt, e.ReceivedAt });
            entity.Property(e => e.DeliveryId).HasMaxLength(200);
            entity.Property(e => e.Event).HasMaxLength(100);
            entity.Property(e => e.RepositoryFullName).HasMaxLength(300);
            entity.Property(e => e.Outcome).HasMaxLength(50);
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
            entity.Property(e => e.Error).HasMaxLength(2000);
        });

        // A person's connected GitHub account, per workspace (docs/github-app.md, "Connecting a GitHub
        // account"). No tenant query filter, deliberately and for a stronger reason than usual: the row
        // is never looked up by an id a caller supplied. Every read here is keyed on the session's own
        // (user id, active organization) pair, so a filter could only ever restate what the query
        // already guarantees — while making the revocation webhook, which has no tenant at all, unable
        // to find the row it must delete.
        modelBuilder.Entity<GitHubAccountLink>(entity =>
        {
            entity.ToTable("github_account_links");
            entity.HasKey(e => e.Id);

            // One connection per person per workspace: reconnecting replaces it rather than stacking up
            // tokens that nothing can reach and nobody can revoke.
            entity.HasIndex(e => new { e.UserId, e.OrganizationId }).IsUnique();

            // The revocation webhook looks the row up by GitHub's numeric account id, and it arrives
            // with no tenant, so this index is what makes revocation work at all.
            entity.HasIndex(e => e.GitHubUserId);

            // Explicit, like the table name above: the snake_case convention splits the product name
            // into "git_hub", and a column called git_hub_user_id is a thing a reader has to decode.
            entity.Property(e => e.GitHubUserId).HasColumnName("github_user_id");
            entity.Property(e => e.Login).HasMaxLength(200);
            entity.Property(e => e.AvatarUrl).HasMaxLength(1000);
            entity.Property(e => e.RevokedReason).HasMaxLength(100);

            // Data Protection ciphertext, not a token. Sized for the base64 of a protected payload.
            entity.Property(e => e.ProtectedAccessToken).HasMaxLength(4000);
            entity.Property(e => e.ProtectedRefreshToken).HasMaxLength(4000);
        });

        // Short-lived, single-use OAuth tickets. Rows are consumed by setting ConsumedAt, never by
        // being read twice, so the hash columns are what a lookup uses.
        modelBuilder.Entity<GitHubOAuthState>(entity =>
        {
            entity.ToTable("github_oauth_states");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.StateHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.Property(e => e.StateHash).HasMaxLength(64);
            entity.Property(e => e.BindingHash).HasMaxLength(64);
            entity.Property(e => e.ProtectedCodeVerifier).HasMaxLength(4000);
        });

        // Editor v2 media bins (docs/editor.md "Media"): owned here, mapped read-only by the engine.
        modelBuilder.Entity<MediaBin>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Name)
                .HasMaxLength(120);
            // Self-reference. Restrict, never cascade: the subtree delete is an explicit, bounded
            // walk in the controller (a self-cascade would be unbounded and invisible), and bins are
            // soft-deleted, so a physical delete of an interior bin should fail loudly rather than
            // take children with it.
            entity.HasOne<MediaBin>()
                .WithMany()
                .HasForeignKey(e => e.ParentBinId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.Property(e => e.SmartFilterJson)
                .HasColumnType("jsonb");
        });

        // Editor v2, P9: per-user preferences (keymap, layouts), project presets and timecoded review
        // comments. Migration AddEditorP9Tables (hand-written, like the P6 one).
        modelBuilder.Entity<EditorUserPreference>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("pk_editor_user_preferences");
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.KeymapJson).HasColumnType("jsonb");
            entity.Property(e => e.LayoutsJson).HasColumnType("jsonb");
        });

        // onboarding-and-guides T-001: per-user tour + checklist state (migration AddUserOnboardingState).
        modelBuilder.Entity<UserOnboardingState>(entity =>
        {
            entity.ToTable("user_onboarding_states");
            entity.HasKey(e => e.UserId).HasName("pk_user_onboarding_states");
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.TourStatus).HasMaxLength(16).HasDefaultValue("pending");
            entity.Property(e => e.MarksJson).HasColumnType("jsonb");
            entity.Property(e => e.TourUpdatedAt);
        });

        modelBuilder.Entity<EditorPreset>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Kind });
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Kind).HasMaxLength(32);
            entity.Property(e => e.Name).HasMaxLength(120);
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<EditTimelineComment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TimelineId, e.Frame });
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne<EditTimelineEntity>()
                .WithMany()
                .HasForeignKey(e => e.TimelineId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.AuthorName).HasMaxLength(200);
            entity.Property(e => e.Body).HasMaxLength(4000);
            entity.Property(e => e.GuestName).HasMaxLength(80);
            entity.HasIndex(e => e.GuestLinkId);
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        // Guest review links (Wave 4, docs/design/guest-review-links.md). Addressable by a caller-chosen id
        // on the owner routes, so this table carries the named Tenant filter (TenantBoundaryInventoryTests).
        // The public routes are anonymous: they ignore ONLY that filter and look the row up by token hash.
        // render_execution_id has no foreign key (the workflow tables are ExcludeFromMigrations).
        modelBuilder.Entity<EditReviewLink>(entity =>
        {
            entity.ToTable("edit_review_links");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TokenHash).HasColumnType("bytea");
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => new { e.TimelineId, e.CreatedAt });
            entity.HasIndex(e => e.ProjectId);
            entity.Property(e => e.Label).HasMaxLength(80);
            entity.HasOne<EditTimelineEntity>()
                .WithMany()
                .HasForeignKey(e => e.TimelineId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // Append-only decisions, reached only through their link (no filter of their own).
        modelBuilder.Entity<EditReviewDecision>(entity =>
        {
            entity.ToTable("edit_review_decisions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.LinkId, e.CreatedAt });
            entity.Property(e => e.Decision).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.GuestName).HasMaxLength(80);
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.HasOne<EditReviewLink>()
                .WithMany()
                .HasForeignKey(e => e.LinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MediaAnnotation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            // One row per source: the kind plus whichever id column the kind uses. Both id columns
            // take part so a file and a step result sharing a Guid stay two rows.
            entity.HasIndex(e => new { e.ProjectId, e.SourceKind, e.SourceProjectFileId, e.SourceStepResultId })
                .IsUnique()
                .HasDatabaseName("ux_media_annotations_source");
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.SourceKind)
                .HasConversion<string>();
            entity.Property(e => e.KeywordsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.Note)
                .HasMaxLength(2000);
            entity.Property(e => e.ColorLabel)
                .HasMaxLength(32);
            // No FK on either source column, on purpose — see MediaAnnotation's docs.
        });

        // R09.1 analysis on demand. media_analysis_results is the digest cache (same bytes, same organization, any
        // project); it is reached only with a server-supplied organization id, never a caller-chosen row id, so it
        // carries no query filter. media_analysis_requests is reached only through the project's media routes
        // (guarded at the project). The engine maps the results table read-only.
        modelBuilder.Entity<MediaAnalysisResult>(entity =>
        {
            entity.ToTable("media_analysis_results");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OrganizationId, e.Digest, e.Kind, e.Language })
                .IsUnique()
                .HasDatabaseName("ux_media_analysis_results_key");
            entity.Property(e => e.Digest).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Language).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ResultJson).HasColumnType("jsonb");
            entity.Property(e => e.SummaryJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<MediaAnalysisRequest>(entity =>
        {
            entity.ToTable("media_analysis_requests");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.MediaRef, e.Kind, e.Language })
                .HasDatabaseName("ix_media_analysis_requests_media");
            entity.HasIndex(e => e.RequestId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.MediaRef).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Language).HasMaxLength(16).IsRequired();
            entity.Property(e => e.State).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Digest).HasMaxLength(64);
        });

        modelBuilder.Entity<MediaBinItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.BinId);
            entity.HasOne<MediaBin>()
                .WithMany()
                .HasForeignKey(e => e.BinId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.SourceKind)
                .HasConversion<string>();
            // No FK on either source column, on purpose — a bin item is a UI pin over media that
            // another feature owns, and a StepResult source is deletable. See MediaBinItem's docs.
        });

        // Computer folder sync (docs/design/local-folder-sync-and-storage.md, Part 1): one folder on a
        // paired desktop computer mirrored into a project. A child of the project like
        // project_github_links: no tenant query filter, reached only through the project access check
        // (dashboard) or the signed device's own organization (desktop app). The device FK restricts,
        // so a device cannot vanish from under its links; revoking a device only stops it signing.
        modelBuilder.Entity<ProjectLocalFolderLink>(entity =>
        {
            entity.ToTable("project_local_folder_links");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.TargetFolder }).IsUnique();
            entity.HasIndex(e => e.DeviceId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<RunnerDevice>()
                .WithMany()
                .HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.TargetFolder).HasMaxLength(512);
            entity.Property(e => e.DisplayPath).HasMaxLength(512);
            entity.Property(e => e.PauseReason).HasMaxLength(20);
            entity.Property(e => e.LastError).HasMaxLength(2000);
        });

        // Cloud drive sync (docs/design/cloud-drive-sync.md). A person's connected Google Drive /
        // OneDrive / Dropbox account in one workspace. No tenant query filter, for the same reason as
        // github_account_links: every read is keyed on the session's own (user id, active organization)
        // pair as well as the id, so a filter could only restate what the query already guarantees,
        // while the background sync (system scope) must still find the row a link points at.
        modelBuilder.Entity<CloudStorageConnection>(entity =>
        {
            entity.ToTable("cloud_storage_connections");
            entity.HasKey(e => e.Id);

            // A person may connect several accounts of one provider, but the same account only once
            // per workspace: reconnecting it updates the row rather than stacking up tokens.
            entity.HasIndex(e => new { e.UserId, e.OrganizationId, e.Provider, e.AccountId }).IsUnique();
            entity.Property(e => e.Provider).HasConversion<int>();
            entity.Property(e => e.AccountId).HasMaxLength(300);
            entity.Property(e => e.AccountEmail).HasMaxLength(320);
            entity.Property(e => e.AccountDisplayName).HasMaxLength(300);
            entity.Property(e => e.GrantedScopes).HasMaxLength(2000);
            entity.Property(e => e.LastError).HasMaxLength(2000);

            // Data Protection ciphertext, not a token. Sized for the base64 of a protected payload
            // (Microsoft access tokens are long JWTs).
            entity.Property(e => e.ProtectedAccessToken).HasMaxLength(16000);
            entity.Property(e => e.ProtectedRefreshToken).HasMaxLength(16000);
        });

        // Short-lived, single-use OAuth tickets of the cloud drive connections; the same shape and
        // rules as github_oauth_states.
        modelBuilder.Entity<CloudStorageOAuthState>(entity =>
        {
            entity.ToTable("cloud_storage_oauth_states");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.StateHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Provider).HasConversion<int>();
            entity.Property(e => e.StateHash).HasMaxLength(64);
            entity.Property(e => e.ProtectedCodeVerifier).HasMaxLength(4000);
            entity.Property(e => e.ReturnPath).HasMaxLength(512);
        });

        // A project mirroring one folder of one provider. A child of the project like
        // project_github_links: no tenant query filter, reached only through the project access check
        // (or by the background sync under a system scope). One link per (project, provider), which is
        // what lets project_files.source_kind alone name the link a synced file belongs to.
        modelBuilder.Entity<ProjectCloudLink>(entity =>
        {
            entity.ToTable("project_cloud_links");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Provider }).IsUnique();
            entity.HasIndex(e => e.ConnectionId);
            entity.HasIndex(e => e.LastSyncedAt);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            // Disconnecting an account keeps the link (and its files); the next sync says "link it again".
            entity.HasOne<CloudStorageConnection>()
                .WithMany()
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.Provider).HasConversion<int>();
            entity.Property(e => e.DriveId).HasMaxLength(1024);
            entity.Property(e => e.FolderId).HasMaxLength(1024);
            entity.Property(e => e.FolderDisplayPath).HasMaxLength(2000);
            entity.Property(e => e.IncludePathsJson).HasColumnType("jsonb");
            entity.Property(e => e.LastError).HasMaxLength(2000);
        });

        // Social publishing (docs/social-publishing.md): an organization's connected accounts and the posts
        // made to them. Both are addressed by caller-chosen ids on the routes, so both carry the named
        // Tenant filter. Org deletion cascades to accounts and posts; project deletion cascades to posts.
        modelBuilder.Entity<SocialAccount>(entity =>
        {
            entity.ToTable("social_accounts");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OrganizationId, e.Platform, e.RemoteAccountId }).IsUnique();
            entity.Property(e => e.Platform).HasConversion<int>();
            entity.Property(e => e.RemoteAccountId).HasMaxLength(300);
            entity.Property(e => e.DisplayName).HasMaxLength(300);
            entity.Property(e => e.AvatarUrl).HasMaxLength(2000);
            entity.Property(e => e.GrantedScopes).HasMaxLength(2000);
            entity.Property(e => e.Status).HasMaxLength(32);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            // Data Protection ciphertext, not a token.
            entity.Property(e => e.ProtectedAccessToken).HasMaxLength(16000);
            entity.Property(e => e.ProtectedRefreshToken).HasMaxLength(16000);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // Single-use OAuth tickets; the same shape and rules as cloud_storage_oauth_states.
        modelBuilder.Entity<SocialOAuthState>(entity =>
        {
            entity.ToTable("social_oauth_states");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.StateHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Platform).HasConversion<int>();
            entity.Property(e => e.StateHash).HasMaxLength(64);
            entity.Property(e => e.ProtectedCodeVerifier).HasMaxLength(4000);
            entity.Property(e => e.ReturnPath).HasMaxLength(512);
        });

        modelBuilder.Entity<SocialPost>(entity =>
        {
            entity.ToTable("social_posts");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.SocialAccountId);
            // The sweeper's claim query: due scheduled rows, and expired leases.
            entity.HasIndex(e => new { e.Status, e.ScheduledAt });
            entity.Property(e => e.Platform).HasConversion<int>();
            entity.Property(e => e.OutputKind).HasMaxLength(32);
            entity.Property(e => e.Format).HasMaxLength(32);
            entity.Property(e => e.Title).HasMaxLength(500);
            entity.Property(e => e.Body).HasMaxLength(20000);
            entity.Property(e => e.HashtagsJson).HasColumnType("jsonb");
            entity.Property(e => e.OptionsJson).HasColumnType("jsonb");
            entity.Property(e => e.TimeZone).HasMaxLength(64);
            entity.Property(e => e.Status).HasMaxLength(32);
            entity.Property(e => e.LeaseOwner).HasMaxLength(128);
            entity.Property(e => e.RemotePostId).HasMaxLength(300);
            entity.Property(e => e.RemotePostUrl).HasMaxLength(2000);
            entity.Property(e => e.FailureCode).HasMaxLength(64);
            entity.Property(e => e.FailureMessage).HasMaxLength(2000);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SocialAccount>()
                .WithMany()
                .HasForeignKey(e => e.SocialAccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // Readings of a published post's numbers; reached only through the (tenant-filtered) post.
        modelBuilder.Entity<SocialPostMetric>(entity =>
        {
            entity.ToTable("social_post_metrics");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PostId, e.CapturedAt });
            entity.Property(e => e.Source).HasMaxLength(64);
            entity.HasOne<SocialPost>()
                .WithMany()
                .HasForeignKey(e => e.PostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // The publish ledger: one row per post that took a publish slot. Counted for the monthly cap and the daily
        // budgets. No foreign key to posts or projects on purpose (deleting those must not reset the counts); it goes
        // with the organization.
        modelBuilder.Entity<SocialPublishLedgerEntry>(entity =>
        {
            entity.ToTable("social_publish_ledger");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PostId).IsUnique();
            entity.HasIndex(e => new { e.OrganizationId, e.Day });
            entity.HasIndex(e => new { e.Platform, e.Day });
            entity.Property(e => e.Platform).HasConversion<int>();
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // --- MCP OAuth (docs/design/mcp-oauth.md §5-§6) ---
        // The Go API is the authorization server and reads/writes these four tables by GORM/raw SQL;
        // it never migrates, so the schema is owned and created here. Column names and types are an
        // interface with that side: do not rename or retype them.

        modelBuilder.Entity<OAuthClient>(entity =>
        {
            entity.ToTable("oauth_clients");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClientId).IsUnique();
            entity.Property(e => e.ClientId).HasMaxLength(128);
            // A public (PKCE-only) client has no secret; a confidential one stores only its hash.
            entity.Property(e => e.ClientSecretHash).HasMaxLength(200);
            entity.Property(e => e.ClientName).HasMaxLength(200);
            entity.Property(e => e.RedirectUris).HasColumnType("jsonb");
            entity.Property(e => e.GrantTypes).HasColumnType("jsonb");
            entity.Property(e => e.ResponseTypes).HasColumnType("jsonb");
            entity.Property(e => e.TokenEndpointAuthMethod).HasMaxLength(50);
        });

        // Single-use authorization codes. The code itself is never stored: CodeHash is the lookup key
        // and the token exchange consumes the row by setting ConsumedAt.
        modelBuilder.Entity<OAuthAuthorizationCode>(entity =>
        {
            entity.ToTable("oauth_authorization_codes");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CodeHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.CodeHash).HasMaxLength(64);
            entity.Property(e => e.ClientId).HasMaxLength(128);
            entity.Property(e => e.RedirectUri).HasMaxLength(2048);
            entity.Property(e => e.CodeChallenge).HasMaxLength(128);
            entity.Property(e => e.Scope).HasMaxLength(200);
        });

        // Rotating refresh tokens. TokenHash is the lookup key; RotatedFromId records the chain so a
        // reused (already rotated) token can revoke the whole grant.
        modelBuilder.Entity<OAuthRefreshToken>(entity =>
        {
            entity.ToTable("oauth_refresh_tokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.TokenHash).HasMaxLength(64);
            entity.Property(e => e.ClientId).HasMaxLength(128);
            entity.Property(e => e.Scope).HasMaxLength(200);
        });

        // One row per bridged MCP tool call, success or refusal (docs/design/mcp-oauth.md §6). A
        // record for attribution and abuse review — never a charge, and nothing is written to
        // usage_events (that ledger is provider cost, not tool usage).
        modelBuilder.Entity<McpToolCall>(entity =>
        {
            entity.ToTable("mcp_tool_calls");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.CalledAt });
            entity.HasIndex(e => new { e.OrganizationId, e.CalledAt });
            entity.Property(e => e.ToolName).HasMaxLength(128);
            entity.Property(e => e.ErrorCode).HasMaxLength(64);
        });


        // Run-limit ledger: one row per started run/revision, counted by the assistant execution guard.
        // Never addressed by a caller-supplied id, so it carries no tenant filter (TenantBoundaryInventoryTests).
        modelBuilder.Entity<RunRateEvent>(entity =>
        {
            entity.ToTable("run_rate_events");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.ProjectId, e.Bucket, e.CreatedAt });
            entity.Property(e => e.Bucket).HasMaxLength(8);
            entity.Property(e => e.Source).HasMaxLength(16);
        });

        // T-019: in-product bug reports and feedback. The only place user prose is stored, so the row
        // is registered in the C0 data map (docs/design/saas-accounts.md): exported in the account
        // archive and deleted by the account purge. Never addressed by a caller-supplied id, so it
        // carries no tenant filter (TenantBoundaryInventoryTests), exactly like run_rate_events.
        modelBuilder.Entity<FeedbackReport>(entity =>
        {
            entity.ToTable("feedback_reports");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
            entity.Property(e => e.Kind).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Message).IsRequired();
            entity.Property(e => e.ContextJson).HasColumnType("jsonb").IsRequired();
        });

        modelBuilder.Entity<ProjectVoice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            // SetNull, not Restrict: deleting a model capability must not be blocked by voices, and a
            // voice whose capability is gone is simply unusable (its vendor id has no backend).
            entity.HasOne<ModelCapability>()
                .WithMany()
                .HasForeignKey(e => e.InferenceProviderId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.Name)
                .HasMaxLength(200);
            entity.Property(e => e.Description)
                .HasMaxLength(2000);
            entity.Property(e => e.Source)
                .HasConversion<string>()
                .HasMaxLength(50);
            entity.Property(e => e.RemoteVoiceId)
                .HasMaxLength(500);
            entity.Property(e => e.ReferenceProjectFileIdsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ConsentStatement)
                .HasMaxLength(4000);
            entity.Property(e => e.ConsentSubjectName)
                .HasMaxLength(200);
            entity.ToTable("project_voices");
        });

        // Avatars (docs/design/avatars.md "Data model"): four API-owned tables, migration AddProjectAvatars; objects
        // (subject_kind, the object roles) by AddAvatarObjects, docs/design/avatar-objects.md. No tenant
        // query filter on any of them: every route is /projects/{projectId}/avatars/... and loads the project through
        // IProjectAccessService first, then queries WHERE project_id = @p (TenantBoundaryInventoryTests lists them as
        // parent-guarded). The engine maps project_avatars and project_avatar_references read-only.
        modelBuilder.Entity<ProjectAvatar>(entity =>
        {
            ProjectAvatarModel.ConfigureAvatar(entity);
            entity.ToTable("project_avatars", t =>
            {
                t.HasCheckConstraint("ck_project_avatars_subject_kind",
                    "subject_kind IN ('Person', 'Object')");
                t.HasCheckConstraint("ck_project_avatars_consent_basis",
                    "consent_basis IN ('Self', 'WrittenPermission')");
                t.HasCheckConstraint("ck_project_avatars_disclosure_label",
                    "disclosure_label IN ('Visible', 'MetadataOnly')");
                t.HasCheckConstraint("ck_project_avatars_revoked_pair",
                    "(consent_revoked_at IS NULL) = (consent_revoked_by_user_id IS NULL)");
            });
        });

        modelBuilder.Entity<ProjectAvatarReference>(entity =>
        {
            ProjectAvatarModel.ConfigureReference(entity);
            entity.ToTable("project_avatar_references", t =>
            {
                t.HasCheckConstraint("ck_project_avatar_references_kind", "kind IN ('Image', 'Video', 'Audio')");
                // Widened by AddAvatarObjects with the object roles (Front, Angle, Detail); the person roles are unchanged.
                t.HasCheckConstraint("ck_project_avatar_references_role",
                    "role IN ('Face', 'HalfBody', 'FullBody', 'Motion', 'Speech', 'Front', 'Angle', 'Detail')");
            });
        });

        // Shape fixed in phase 1, written from phase 2 (vendor-side copies, e.g. MiniMax mm_file ids), so delete and
        // retention are written once against the final shape.
        modelBuilder.Entity<ProjectAvatarVendorAsset>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AvatarId);
            entity.HasOne<ProjectAvatar>()
                .WithMany()
                .HasForeignKey(e => e.AvatarId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectAvatarReference>()
                .WithMany()
                .HasForeignKey(e => e.ReferenceId)
                .OnDelete(DeleteBehavior.Cascade);
            // SetNull, as project_voices: deleting a capability must not be blocked by an upload record.
            entity.HasOne<ModelCapability>()
                .WithMany()
                .HasForeignKey(e => e.ModelCapabilityId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.RemoteAssetId).HasMaxLength(ProjectAvatarLimits.MaxRemoteAssetIdLength).IsRequired();
            entity.Property(e => e.DeleteOutcome).HasConversion<string>().HasMaxLength(50);
            entity.ToTable("project_avatar_vendor_assets");
        });

        // Append-only audit. Deliberately NO foreign keys (rows survive the avatar and the project, so a deletion can
        // be proven); counts and outcome words only, never media, URLs, storage keys or the subject's name. Purged
        // after ProjectAvatarEvent.RetentionYears by the avatar retention sweep (hence the occurred_at index).
        modelBuilder.Entity<ProjectAvatarEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.AvatarId, e.OccurredAt });
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.OccurredAt);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ConsentStatementVersion).HasMaxLength(ProjectAvatarLimits.MaxConsentStatementVersionLength);
            entity.Property(e => e.OutcomesJson).HasColumnType("jsonb");
            entity.ToTable("project_avatar_events");
        });

        // Workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library (global avatars)"),
        // migration AddAvatarLibrary. A library item is addressable by a caller-chosen id and owned by ONE
        // organization, so avatar_library_items carries the named "Tenant" filter (TenantBoundaryInventoryTests);
        // avatar_library_references is reached only through its item and carries none. project_avatars.library_item_id
        // is the link an import carries (NO ACTION: the library delete routine removes the imports first).
        modelBuilder.Entity<AvatarLibraryItem>(entity =>
        {
            AvatarLibraryModel.ConfigureItem(entity);
            entity.ToTable("avatar_library_items", t =>
            {
                t.HasCheckConstraint("ck_avatar_library_items_subject_kind",
                    "subject_kind IN ('Person', 'Object')");
                t.HasCheckConstraint("ck_avatar_library_items_consent_basis",
                    "consent_basis IN ('Self', 'WrittenPermission')");
                t.HasCheckConstraint("ck_avatar_library_items_disclosure_label",
                    "disclosure_label IN ('Visible', 'MetadataOnly')");
                t.HasCheckConstraint("ck_avatar_library_items_revoked_pair",
                    "(consent_revoked_at IS NULL) = (consent_revoked_by_user_id IS NULL)");
            });
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        modelBuilder.Entity<AvatarLibraryReference>(entity =>
        {
            AvatarLibraryModel.ConfigureReference(entity);
            entity.ToTable("avatar_library_references", t =>
            {
                t.HasCheckConstraint("ck_avatar_library_references_kind", "kind IN ('Image', 'Video', 'Audio')");
                t.HasCheckConstraint("ck_avatar_library_references_role",
                    "role IN ('Face', 'HalfBody', 'FullBody', 'Motion', 'Speech', 'Front', 'Angle', 'Detail')");
            });
        });

        modelBuilder.Entity<ProjectAvatar>(AvatarLibraryModel.ConfigureImportLink);

        // Avatar builds (docs/design/avatar-objects.md "Avatar variants", "Pulling an object out of a video"; migration
        // AddAvatarVariants): one background job per variant made by the picture model or object pulled out of a video.
        // No tenant query filter: reached only through an authorized project (the build routes load the project through
        // IProjectAccessService, then read WHERE project_id = @p), listed as parent-guarded in TenantBoundaryInventoryTests.
        modelBuilder.Entity<AvatarBuildJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            // The sweeper's read: open jobs by heartbeat.
            entity.HasIndex(e => new { e.State, e.UpdatedAt });
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            // SetNull both ways: deleting the original, or the avatar a job made, keeps the job row as history.
            entity.HasOne<ProjectAvatar>()
                .WithMany()
                .HasForeignKey(e => e.SourceAvatarId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ProjectAvatar>()
                .WithMany()
                .HasForeignKey(e => e.AvatarId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.SourceMediaRef).HasMaxLength(AvatarBuildJob.MaxMediaRefLength);
            entity.Property(e => e.Instruction).HasMaxLength(ProjectAvatarLimits.MaxVariantInstructionLength);
            entity.Property(e => e.ObjectDescription).HasMaxLength(AvatarBuildJob.MaxObjectDescriptionLength);
            entity.Property(e => e.Name).HasMaxLength(ProjectAvatarLimits.MaxNameLength).IsRequired();
            entity.Property(e => e.VariantLabel).HasMaxLength(ProjectAvatarLimits.MaxVariantLabelLength);
            entity.Property(e => e.ParamsJson).HasColumnType("jsonb");
            entity.Property(e => e.State).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Stage).HasMaxLength(32);
            entity.Property(e => e.StatementLocale).HasMaxLength(ProjectAvatarLimits.MaxConsentLocaleLength).IsRequired();
            entity.Property(e => e.AllowedBy).HasMaxLength(32);
            entity.Property(e => e.FailureCode).HasMaxLength(AvatarBuildJob.MaxFailureCodeLength);
            entity.Property(e => e.Message).HasMaxLength(AvatarBuildJob.MaxMessageLength);
            // PostgreSQL's xmin system column: no real column; two workers cannot both claim one job.
            entity.Property(e => e.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            entity.ToTable("avatar_build_jobs", t =>
            {
                t.HasCheckConstraint("ck_avatar_build_jobs_kind", "kind IN ('variant', 'extraction')");
                t.HasCheckConstraint("ck_avatar_build_jobs_state",
                    "state IN ('queued', 'pictures', 'videos', 'finding', 'cutting', 'creating', 'ready', 'failed', 'cancelled')");
                t.HasCheckConstraint("ck_avatar_build_jobs_allowed_by",
                    "allowed_by IS NULL OR allowed_by IN ('project_consent', 'call')");
            });
        });

        modelBuilder.Entity<ProjectPronunciationEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Term).HasMaxLength(60).IsRequired();
            entity.Property(e => e.Spelling).HasMaxLength(120).IsRequired();
            // Exact-term uniqueness per project; the migration adds the case-insensitive
            // (project_id, lower(term)) unique index in SQL, and the service compares case-insensitively.
            entity.HasIndex(e => new { e.ProjectId, e.Term })
                .HasDatabaseName("ux_project_pronunciation_entries_project_term")
                .IsUnique();
            entity.ToTable("project_pronunciation_entries");
        });

        // Project versioning (initiative project-versioning, contract section 2). These four tables
        // are API-owned and deliberately NOT tenant-filtered: each member is reached only through an
        // authorized project — the routes load by project_id after IProjectAccessService, and the
        // capture/erase/GC services query by a project id they already own. A filter here would need
        // a join back to projects on every item/blob/document read and would still not guard a
        // caller that names another project's id. TenantBoundaryInventoryTests lists all four as
        // unfiltered for exactly this reason.
        modelBuilder.Entity<ProjectVersion>(entity =>
        {
            entity.ToTable("project_versions");
            entity.HasKey(e => e.Id);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Name).HasMaxLength(ProjectVersionName.MaxLength);
            entity.Property(e => e.Cause).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ManifestDigest).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Number }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
        });

        modelBuilder.Entity<ProjectVersionItem>(entity =>
        {
            entity.ToTable("project_version_items");
            entity.HasKey(e => e.Id);
            entity.HasOne<ProjectVersion>()
                .WithMany(v => v.Items)
                .HasForeignKey(e => e.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Kind)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.ItemKey).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(1024);
            entity.Property(e => e.Category).HasMaxLength(50);
            entity.Property(e => e.MimeType).HasMaxLength(255);
            entity.Property(e => e.ContentId).HasMaxLength(ContentId.MaxLength);
            entity.Property(e => e.StorageKey).HasMaxLength(1000);
            entity.Property(e => e.StorageETag).HasMaxLength(200);
            entity.Property(e => e.DocumentSha256).HasMaxLength(64);
            // Output-hold provenance: not part of the manifest digest.
            entity.Property(e => e.OutputProducedAt);
            entity.Property(e => e.PlatformMade).HasDefaultValue(false);
            entity.HasIndex(e => new { e.VersionId, e.Kind, e.ItemKey }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.StorageKey });
        });

        modelBuilder.Entity<ProjectHistoryBlob>(entity =>
        {
            entity.ToTable("project_history_blobs");
            entity.HasKey(e => new { e.ProjectId, e.ContentId });
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.ContentId).HasMaxLength(ContentId.MaxLength);
            entity.Property(e => e.StorageKey).HasMaxLength(1000).IsRequired();
        });

        modelBuilder.Entity<ProjectHistoryDocument>(entity =>
        {
            entity.ToTable("project_history_documents");
            entity.HasKey(e => new { e.ProjectId, e.Sha256 });
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Sha256).HasMaxLength(64);
            entity.Property(e => e.Kind)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.Json).HasColumnType("text").IsRequired();
        });

        // Design systems (SPEC 3.1-3.4 / 16.1): project children reached only through an authorized
        // parent project, so no tenant filter (TenantBoundaryInventoryTests lists them as child
        // tables). Phase 4 adds parent_library_id, a nullable FK to design_system_libraries valid on
        // project rows only (the CHECK); library document rows themselves keep project_id NULL.
        modelBuilder.Entity<DesignSystem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => new { e.ProjectId, e.Kind }).IsUnique();
            entity.HasOne<Project>().WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            // Circular FK: current_version_id -> design_system_versions(id), emitted by the migrator
            // AFTER the versions table exists because the two tables reference each other.
            entity.HasOne<DesignSystemVersion>().WithMany()
                .HasForeignKey(e => e.CurrentVersionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Phase 4: a project row may descend from one org library; deleting the library unsets the
            // link rather than deleting the project row.
            entity.HasOne<DesignSystemLibrary>().WithMany()
                .HasForeignKey(e => e.ParentLibraryId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.Kind).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.WatchJson).HasColumnType("jsonb");
            // Approvals race on which version is current: the loser of two concurrent decisions
            // fails its UPDATE (0 rows) instead of leaving two Approved versions.
            entity.Property(e => e.CurrentVersionId).IsConcurrencyToken();
            entity.ToTable("design_systems", t =>
            {
                t.HasCheckConstraint("ck_design_systems_kind", "kind IN ('design','product_map')");
                t.HasCheckConstraint("ck_design_systems_parent_library_project",
                    "parent_library_id IS NULL OR project_id IS NOT NULL");
            });
        });

        // Design-system libraries (SPEC 16.1): organization-owned and addressed by their own id, so
        // the tenancy boundary is drawn on them with the named "Tenant" filter; there are no platform
        // rows. The document container is the design_systems row named by design_system_id (a row
        // with project_id NULL and kind 'design'), so deleting the library cascades to it.
        modelBuilder.Entity<DesignSystemLibrary>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OrganizationId);
            entity.HasIndex(e => new { e.OrganizationId, e.Name }).IsUnique();
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<DesignSystem>()
                .WithMany()
                .HasForeignKey(e => e.DesignSystemId)
                .OnDelete(DeleteBehavior.Cascade);
            // One library per document container.
            entity.HasIndex(e => e.DesignSystemId).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.ToTable("design_system_libraries");
            entity.HasQueryFilter(TenantFilterName,
                e => TenantUnfiltered || (TenantOrgId != null && e.OrganizationId == TenantOrgId));
        });

        // Library-owned brand assets (T-010): inert SVG/font bytes hanging off a design_system_libraries
        // row. NO tenant filter — the table has no organization column; every route proves the library
        // belongs to the active organization first (the parent IS filtered), and TenantBoundaryInventory
        // lists the table as parent-guarded. Deleting the library cascades these rows.
        modelBuilder.Entity<DesignSystemLibraryAsset>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LibraryId);
            entity.HasIndex(e => new { e.LibraryId, e.Name }).IsUnique();
            entity.HasOne<DesignSystemLibrary>().WithMany()
                .HasForeignKey(e => e.LibraryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.MimeType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Sha256).HasColumnType("char(64)").IsRequired();
            entity.Property(e => e.StorageKey).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(20);
            entity.Property(e => e.Variant).HasMaxLength(40);
            entity.ToTable("design_system_library_assets");
        });

        // Built-in sound-effects library: PLATFORM content, no organization and no Tenant filter (the same
        // rows for every project); TenantBoundaryInventoryTests lists it as unfiltered. API-owned; the
        // engine maps it read-only.
        modelBuilder.Entity<SfxLibraryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.HasIndex(e => new { e.IsEnabled, e.Category, e.SortOrder });
            entity.Property(e => e.Slug).HasMaxLength(80).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Tags).HasColumnType("jsonb");
            entity.Property(e => e.Sha256).HasColumnType("char(64)").IsRequired();
            entity.Property(e => e.StorageKey).HasMaxLength(300).IsRequired();
            entity.Property(e => e.MimeType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Licence).HasMaxLength(120).IsRequired();
            entity.Property(e => e.LicenceUrl).HasMaxLength(500);
            entity.Property(e => e.Author).HasMaxLength(200);
            entity.Property(e => e.SourceUrl).HasMaxLength(500);
            entity.Property(e => e.RecipeVersion).HasMaxLength(40);
            entity.ToTable("sfx_library_items");
        });

        modelBuilder.Entity<DesignSystemVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DesignSystemId, e.VersionNumber }).IsUnique();
            entity.HasIndex(e => new { e.DesignSystemId, e.State });
            entity.HasOne<DesignSystem>().WithMany()
                .HasForeignKey(e => e.DesignSystemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.DocumentJson).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.DocumentDigest).HasColumnType("char(64)").IsRequired();
            entity.Property(e => e.Origin).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.CoverageJson).HasColumnType("jsonb");
            entity.ToTable("design_system_versions");
        });

        modelBuilder.Entity<DesignSystemSource>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DesignSystemId, e.Path });
            entity.HasIndex(e => e.ProjectFileId);
            entity.HasOne<DesignSystem>().WithMany()
                .HasForeignKey(e => e.DesignSystemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<DesignSystemVersion>().WithMany()
                .HasForeignKey(e => e.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectFile>().WithMany()
                .HasForeignKey(e => e.ProjectFileId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.SourceKind).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Path).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.Importer).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ContentSha256).HasColumnType("char(64)").IsRequired();
            entity.Property(e => e.SourceRevision).HasMaxLength(128);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.ToTable("design_system_sources");
        });

        modelBuilder.Entity<DesignSystemChangeProposal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DesignSystemId, e.Status });
            entity.HasIndex(e => new { e.DesignSystemId, e.ProposedDigest });
            entity.HasOne<DesignSystem>().WithMany()
                .HasForeignKey(e => e.DesignSystemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Trigger).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.DiffJson).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.ProposedDigest).HasColumnType("char(64)").IsRequired();
            entity.Property(e => e.DecisionNote).HasMaxLength(1000);
            entity.ToTable("design_system_change_proposals");
        });

        // Composition workspaces (docs/design/composition-revisions.md). Owned here; the engine maps
        // all three with ExcludeFromMigrations and inserts versions at capture time. No tenant filter
        // on any of them: they hang off a project, and every read path resolves the project through
        // IProjectAccessService first (TenantBoundaryInventoryTests). No FK to workflow_executions on
        // purpose — that table is engine-owned and does not exist yet when this context migrates.
        modelBuilder.Entity<CompositionWorkspace>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.OriginExecutionId).IsUnique();
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable("composition_workspaces");
        });

        modelBuilder.Entity<CompositionWorkspaceVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.WorkspaceId, e.VersionNumber }).IsUnique();
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.CreatedByExecutionId);
            entity.HasOne<CompositionWorkspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.StorageKey).HasMaxLength(1000);
            entity.Property(e => e.Sha256).HasMaxLength(64);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.CompositionId).HasMaxLength(200);
            entity.Property(e => e.SceneManifestJson).HasColumnType("jsonb");
            entity.Property(e => e.RenderOutputStorageKey).HasMaxLength(1000);
            entity.Property(e => e.Instruction).HasMaxLength(4000);
            entity.ToTable("composition_workspace_versions");
        });

        modelBuilder.Entity<CompositionRevision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExecutionId).IsUnique();
            entity.HasIndex(e => e.WorkspaceId);
            entity.HasIndex(e => new { e.ProjectId, e.RequestedByUserId, e.CreatedAt });
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CompositionWorkspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Instruction).HasMaxLength(4000);
            entity.Property(e => e.TargetsJson).HasColumnType("jsonb");
            entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.CutPolicy).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ReplayPlanJson).HasColumnType("jsonb");
            entity.ToTable("composition_revisions");
        });

        modelBuilder.Entity<StepChangeRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            // One row per target step: a request with several targets shares one replay run (ExecutionId).
            entity.HasIndex(e => e.ExecutionId);
            entity.HasIndex(e => new { e.ExecutionId, e.TargetStepOrder }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.SourceExecutionId });
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.StepType).HasMaxLength(50);
            entity.Property(e => e.Instruction).HasMaxLength(4000);
            entity.Property(e => e.ScopeJson).HasColumnType("jsonb");
            entity.Property(e => e.OverridesJson).HasColumnType("jsonb");
            entity.Property(e => e.ReplayPlanJson).HasColumnType("jsonb");
            entity.ToTable("step_change_requests");
        });

        // Designed-graphic requests for the picture editor (docs/editor.md "Designed graphics"). Owned
        // here; the engine maps the table with ExcludeFromMigrations and writes the Result* columns. No
        // tenant filter: a project child, reached through EditImageGraphicsController's project check
        // and constrained to the document (TenantBoundaryInventoryTests). No FK to workflow_executions
        // (engine-owned, absent when this context migrates). The document cascade removes a request
        // with its document; ResultLayerId is a concurrency token so two readers settling the same
        // request at once insert one layer.
        modelBuilder.Entity<EditImageGraphicRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExecutionId).IsUnique();
            entity.HasIndex(e => e.DocumentId);
            entity.HasIndex(e => new { e.ProjectId, e.RequestedByUserId, e.CreatedAt });
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<EditImageDocument>()
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Placement).HasMaxLength(32);
            entity.Property(e => e.ResultStorageKey).HasMaxLength(1000);
            entity.Property(e => e.ResultCompositionId).HasMaxLength(200);
            entity.Property(e => e.ResultSummary).HasMaxLength(1000);
            entity.Property(e => e.ResultModelName).HasMaxLength(200);
            entity.Property(e => e.ResultLayerId).IsConcurrencyToken();
            entity.ToTable("edit_image_graphic_requests");
        });

        modelBuilder.Entity<EditingStyle>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Name)
                .HasMaxLength(100);
            entity.Property(e => e.Description)
                .HasMaxLength(500);
            entity.Property(e => e.ConfigJson)
                .HasColumnType("jsonb");
            entity.ToTable("editing_styles");
        });
        // --- Referenced tables (excluded from migrations) ---

        modelBuilder.Entity<WorkflowDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.WorkflowDefinitions)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            // The app manifest and who created the workflow (docs/workflow-apps.md). Engine migration AddWorkflowAppManifest owns the columns.
            entity.Property(e => e.AppManifestJson).HasColumnType("jsonb");
            entity.Property(e => e.CreatedVia).HasMaxLength(16);
            entity.Property(e => e.TemplateKey).HasMaxLength(64);
            entity.ToTable("workflow_definitions", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<WorkflowStep>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.WorkflowDefinition)
                .WithMany(w => w.Steps)
                .HasForeignKey(e => e.WorkflowDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AgentDefinition)
                .WithMany(a => a.WorkflowSteps)
                .HasForeignKey(e => e.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.EdgeConditionJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.InputMappingJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.SelectedPriorStepOrdersJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ParallelAgentIdsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ExtractConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.VideoAnalyzeConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.VideoCompileConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.VideoGenerateConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.EditRoomConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.GraphicsRoomConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ColorGradeRoomConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ConditionalConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ReviewLoopConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.VoiceoverConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ThumbnailGenerateConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.DailiesRoomConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.TimelineRenderConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.DesignSystemConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ExtractFrameConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.AudioGenerateConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.PinInsertConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.AudioCleanupConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.VideoEnhanceConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ImageGenerateConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ContinueCompositionJson)
                .HasColumnType("jsonb");
            // Non-null, false for every existing row (the migration's column default).
            entity.Property(e => e.RegisterOutputAsProjectFile);
            entity.Property(e => e.StepType)
                .HasConversion<string>();
            entity.Property(e => e.AgentInputContextMode)
                .HasConversion<string>();
            entity.Property(e => e.CacheMode)
                .HasConversion<string>();
            entity.ToTable("workflow_steps", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<WorkflowExecution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.WorkflowDefinition)
                .WithMany(w => w.Executions)
                .HasForeignKey(e => e.WorkflowDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.WorkflowExecutions)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CurrentStep)
                .WithMany()
                .HasForeignKey(e => e.CurrentStepId)
                .OnDelete(DeleteBehavior.SetNull);
            // The run's engine-taken ExecutionSnapshot and resume state (engine migration
            // AddExecutionSnapshots). Mapped here so the API reads and writes them; this table stays
            // ExcludeFromMigrations, so the schema is the engine's.
            entity.HasOne<WorkflowDefinition>()
                .WithMany()
                .HasForeignKey(e => e.SnapshotDefinitionId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.AgentSnapshotJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.PendingResume)
                .HasDefaultValue(false);
            entity.Property(e => e.ResumeCount)
                .HasDefaultValue(0);
            entity.Property(e => e.AutoResumeCount)
                .HasDefaultValue(0);
            entity.Property(e => e.Status)
                .HasConversion<string>();
            entity.Property(e => e.ResultJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.StyleConfigJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.DesignSystemPinJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.OutputFormatOverride)
                .HasConversion<string>()
                .HasMaxLength(50);
            // T-015: the run-rate bucket ("paid"/"free") an assistant/MCP start was admitted under; the engine's
            // RUN_CLASS_CHANGED re-check reads it. Engine migration AddExecutionAdmittedRunRateBucket owns the column.
            entity.Property(e => e.AdmittedRunRateBucket)
                .HasMaxLength(16);
            // The typed run brief's stored part. Engine migration AddExecutionRunBrief owns the column.
            entity.Property(e => e.RunBriefJson)
                .HasColumnType("jsonb");
            // Set when a Passed run's review loop stopped below its minimum. Engine migration AddExecutionAttention owns the column.
            entity.Property(e => e.AttentionJson)
                .HasColumnType("jsonb");
            // When the run first started; never reset. Engine migration AddExecutionFirstStartedAt owns the column.
            entity.Property(e => e.FirstStartedAt)
                .HasColumnType("timestamp with time zone");
            entity.ToTable("workflow_executions", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<WorkflowStepResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.WorkflowExecution)
                .WithMany(e => e.StepResults)
                .HasForeignKey(e => e.WorkflowExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.WorkflowStep)
                .WithMany(s => s.Results)
                .HasForeignKey(e => e.WorkflowStepId)
                // allow steps to be removed even if historic results exist; we clean them up automatically
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.InputJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.OutputJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.TimelineStorageKey);
            entity.Property(e => e.ToolCallsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ReasoningJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ChatTranscriptJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.Status)
                .HasConversion<string>();
            // B6b columns. Read-only here (the WorkflowEngine owns and writes this table), but the
            // string conversion must match the engine's or this context would read a varchar column
            // as an integer enum.
            entity.Property(e => e.FailureClass)
                .HasConversion<string>()
                .HasDefaultValue(FailureClass.None);
            entity.Property(e => e.FailureCode)
                .HasMaxLength(64);
            entity.ToTable("workflow_step_results", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<ReviewScore>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.WorkflowExecution)
                .WithMany(e => e.ReviewScores)
                .HasForeignKey(e => e.WorkflowExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable("review_scores", t => t.ExcludeFromMigrations());
        });

        // C9: mapped read-only so the account export can reach the VendorGeneration records of a
        // project. The WorkflowEngine owns and writes the table; the API only reads it (and deletes
        // by project on a purge). No query filter: the row has no user column of its own and is
        // reachable only through a project, which IS filtered (same rule as media_derivatives).
        modelBuilder.Entity<ExternalGenerationJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.ExecutionId);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ClipId).HasMaxLength(200);
            entity.Property(e => e.RequestHash).HasMaxLength(200);
            entity.Property(e => e.RemoteJobId).HasMaxLength(200);
            entity.Property(e => e.StorageKey).HasMaxLength(1000);
            entity.Property(e => e.CostEstimate).HasPrecision(18, 8);
            entity.Property(e => e.ActualCost).HasPrecision(18, 8);
            entity.Property(e => e.SubjectIdsJson).HasColumnType("jsonb");
            entity.ToTable("external_generation_jobs", t => t.ExcludeFromMigrations());
        });

        // C9: mapped read-only so the account purge can delete a purged project's cached step outputs
        // (which hold transcripts and scripts and have NO foreign key to the project). Column
        // configuration mirrors the engine's, because a mismatched enum conversion would read a
        // varchar column as an integer enum.
        modelBuilder.Entity<WorkflowStepCacheEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CacheKey).HasMaxLength(64);
            entity.Property(e => e.StepType).HasConversion<string>();
            entity.Property(e => e.AgentType).HasConversion<string>();
            entity.Property(e => e.Output).HasColumnType("text");
            entity.Property(e => e.ChatTranscriptJson).HasColumnType("jsonb");
            entity.ToTable("workflow_step_cache_entries", t => t.ExcludeFromMigrations());
        });

        // C9: mapped read-only for the same reason — a decision question can echo the user's content
        // and the row has no foreign key to the execution it belongs to.
        modelBuilder.Entity<DecisionObservation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.AgentType).HasConversion<string>();
            entity.Property(e => e.AcceptMetric);
            entity.Property(e => e.ProbabilitiesJson).HasColumnType("jsonb");
            entity.Property(e => e.DownstreamOutcomeJson).HasColumnType("jsonb");
            entity.ToTable("decision_observations", t => t.ExcludeFromMigrations());
        });
    }
}
