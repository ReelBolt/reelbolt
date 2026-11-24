using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared;
using ReelBolt.Shared.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Data;

/// <summary>
/// EF Core context for the Workflow Engine service.
/// Owns: workflow_definitions, workflow_steps, workflow_executions, workflow_step_results, review_scores, workflow_step_cache_entries, decision_observations, external_generation_jobs, runner_jobs, runner_sessions.
/// References (ExcludeFromMigrations): application_users, projects, project_files, agent_definitions,
/// edit_timelines, edit_timeline_ops, edit_timeline_snapshots, media_derivatives, media_bins, media_bin_items, organizations,
/// organization_memberships, runner_devices, usage_events, rate_multipliers, content_moderation_decisions,
/// platform_settings.
/// </summary>
public class WorkflowEngineDbContext : DbContext
{
    public WorkflowEngineDbContext(DbContextOptions<WorkflowEngineDbContext> options) : base(options) { }

    /// <summary>
    /// Shadow property on <see cref="WorkflowDefinition"/> (<c>workflow_definitions.snapshot_of_execution_id</c>):
    /// the run an <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/> belongs to, ON DELETE CASCADE.
    /// </summary>
    public const string SnapshotOfExecutionIdColumn = "SnapshotOfExecutionId";

    // Owned tables
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowExecution> WorkflowExecutions => Set<WorkflowExecution>();
    public DbSet<WorkflowStepResult> WorkflowStepResults => Set<WorkflowStepResult>();
    public DbSet<ReviewScore> ReviewScores => Set<ReviewScore>();
    public DbSet<WorkflowStepCacheEntry> WorkflowStepCacheEntries => Set<WorkflowStepCacheEntry>();
    public DbSet<DecisionObservation> DecisionObservations => Set<DecisionObservation>();
    public DbSet<ExternalGenerationJob> ExternalGenerationJobs => Set<ExternalGenerationJob>();
    public DbSet<RunnerJob> RunnerJobs => Set<RunnerJob>();
    public DbSet<RunnerSession> RunnerSessions => Set<RunnerSession>();

    // Referenced tables (for navigation properties, not migrations)
    public DbSet<ApplicationUser> ApplicationUsers => Set<ApplicationUser>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectFile> ProjectFiles => Set<ProjectFile>();
    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();
    public DbSet<InferenceProvider> InferenceProviders => Set<InferenceProvider>();
    public DbSet<InferenceModel> InferenceModels => Set<InferenceModel>();
    public DbSet<ModelCapability> ModelCapabilities => Set<ModelCapability>();
    public DbSet<ModelCapabilityPrice> ModelCapabilityPrices => Set<ModelCapabilityPrice>();
    public DbSet<PricingSettings> PricingSettings => Set<PricingSettings>();
    public DbSet<OrganizationAgentProviderOverride> OrganizationAgentProviderOverrides => Set<OrganizationAgentProviderOverride>();
    public DbSet<EditTimelineEntity> EditTimelines => Set<EditTimelineEntity>();
    public DbSet<EditTimelineOp> EditTimelineOps => Set<EditTimelineOp>();
    public DbSet<EditTimelineSnapshot> EditTimelineSnapshots => Set<EditTimelineSnapshot>();
    public DbSet<MediaDerivative> MediaDerivatives => Set<MediaDerivative>();
    public DbSet<MediaBin> MediaBins => Set<MediaBin>();
    public DbSet<MediaBinItem> MediaBinItems => Set<MediaBinItem>();
    /// <summary>API-owned digest cache of media analyses (R09.1): the engine only reads it, to skip work for bytes already analysed.</summary>
    public DbSet<MediaAnalysisResult> MediaAnalysisResults => Set<MediaAnalysisResult>();
    public DbSet<ProjectVoice> ProjectVoices => Set<ProjectVoice>();
    /// <summary>Read-only (ExcludeFromMigrations): the Inference API owns and seeds the built-in sound-effects library.</summary>
    public DbSet<SfxLibraryItem> SfxLibraryItems => Set<SfxLibraryItem>();

    /// <summary>Read-only here: a person's own daily video spend limit (the Inference API owns the table).</summary>
    public DbSet<UserVideoSpendLimit> UserVideoSpendLimits => Set<UserVideoSpendLimit>();
    // Read-only (ExcludeFromMigrations): the Inference API owns the avatar tables (docs/design/avatars.md).
    public DbSet<ProjectAvatar> ProjectAvatars => Set<ProjectAvatar>();
    public DbSet<ProjectAvatarReference> ProjectAvatarReferences => Set<ProjectAvatarReference>();

    public DbSet<ProjectPronunciationEntry> ProjectPronunciationEntries => Set<ProjectPronunciationEntry>();
    public DbSet<DesignSystem> DesignSystems => Set<DesignSystem>();
    public DbSet<DesignSystemVersion> DesignSystemVersions => Set<DesignSystemVersion>();
    public DbSet<DesignSystemLibrary> DesignSystemLibraries => Set<DesignSystemLibrary>();
    public DbSet<CompositionWorkspace> CompositionWorkspaces => Set<CompositionWorkspace>();
    public DbSet<CompositionWorkspaceVersion> CompositionWorkspaceVersions => Set<CompositionWorkspaceVersion>();
    public DbSet<CompositionRevision> CompositionRevisions => Set<CompositionRevision>();
    public DbSet<StepChangeRequest> StepChangeRequests => Set<StepChangeRequest>();
    public DbSet<EditImageGraphicRequest> EditImageGraphicRequests => Set<EditImageGraphicRequest>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<RunnerDevice> RunnerDevices => Set<RunnerDevice>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    /// <summary>API-owned audit table (content moderation): the engine appends rows, never migrates it.</summary>
    public DbSet<ContentModerationDecision> ContentModerationDecisions => Set<ContentModerationDecision>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<CreditAccount> CreditAccounts => Set<CreditAccount>();
    public DbSet<CreditLedgerEntry> CreditLedgerEntries => Set<CreditLedgerEntry>();

    public DbSet<ApiKeySpend> ApiKeySpends => Set<ApiKeySpend>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<CreditGrant> CreditGrants => Set<CreditGrant>();
    public DbSet<RateMultiplier> RateMultipliers => Set<RateMultiplier>();
    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        SnakeCaseNamingHelper.ApplySnakeCaseNaming(modelBuilder);

        // --- Referenced tables (excluded from migrations) ---

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.ToTable("application_users", t => t.ExcludeFromMigrations());
        });

        // Read-only here: the Inference API owns and writes the Data Protection key ring.
        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.ToTable("data_protection_keys", t => t.ExcludeFromMigrations());
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
            // Organization data (folder, tags) is API-owned and not read by the engine.
            entity.Ignore(e => e.Folder);
            entity.Ignore(e => e.TagLinks);
            entity.Ignore(e => e.FolderId);
            // The upload-analysis choice is API-owned and read only by the API when it publishes an upload.
            entity.Ignore(e => e.AnalyzeOnUpload);
            // The avatar media consent (docs/design/avatar-objects.md) is API-owned and read only by the API's editor
            // and assistant routes; a workflow step carries its own allowSourceMediaEgress.
            entity.Ignore(e => e.SubjectMediaConsentAt);
            entity.Ignore(e => e.SubjectMediaConsentByUserId);
            entity.ToTable("projects", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<ProjectFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Category, e.DirectoryPath });
            entity.HasIndex(e => new { e.ProjectId, e.Category, e.UploadedAt });
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Files)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.SummaryStatus)
                .HasConversion<string>();
            entity.Property(e => e.IndexingStatus)
                .HasConversion<string>();
            // CSAM quarantine: the engine never sees a quarantined file. One filter here covers
            // every engine reader — workspaces, agent tools, VideoAnalyze/Compile, timeline,
            // thumbnails and derivatives — so no executor can read the bytes.
            entity.Property(e => e.CsamStatus)
                .HasConversion<string>();
            // The API stores the ingest moderation state as text (`moderation_status`); without this the
            // engine reads the column as an integer and every project-file query throws InvalidCastException
            // (media derivatives, VideoAnalyze, renders: nothing that touches a file could run).
            entity.Property(e => e.ModerationStatus)
                .HasConversion<string>();
            // Ingest moderation quarantine (API: ContentModerationWorker) hides a blocked file from the engine
            // exactly like CSAM. Only Quarantined: Error and Scanning rows stay readable, as in the API.
            //
            // An engine output row (a registered render, a generated take, an assistant save's copy) whose output was
            // produced at or before the project's quarantine marker plus the skew margin is hidden too: it may have been
            // made from a file quarantined since, and the marker outlives the rows. Judged by output_produced_at alone,
            // written once when the row is created and untouched by a move or rename (which rewrite the upload time,
            // category and key). Rows without it (uploads, agent files) are unaffected. One filter, so every engine reader
            // (workspaces, listing, resolution by id, timeline, compile, derivatives) is covered. A project without a
            // marker is untouched.
            entity.HasQueryFilter(e => e.CsamStatus != CsamScanStatus.Quarantined && e.ModerationStatus != FileModerationStatus.Quarantined
                && !(e.OutputProducedAt != null
                    && Projects.Any(p => p.Id == e.ProjectId && p.ContentQuarantinedAt != null
                        && e.OutputProducedAt <= p.ContentQuarantinedAt.Value + ContentQuarantineRule.ClockSkewMargin)));
            entity.Property(e => e.StorageMetadataJson)
                .HasColumnType("jsonb");

            // Project-history content identity columns are API-owned (AddProjectVersioning), and the
            // engine never reads or writes them, so the engine model ignores them. ProjectFile lives
            // in ReelBolt.Shared and is shared by both contexts; ignoring here keeps the engine's
            // read-only project_files mapping unchanged (no engine migration).
            entity.Ignore(e => e.ContentSha256);
            entity.Ignore(e => e.ContentSha256ETag);

            // mirror the fields added to the main API context so agents can see them
            entity.Property(e => e.OriginalPath)
                .HasMaxLength(1000);
            entity.Property(e => e.DirectoryPath)
                .HasMaxLength(1000);
            entity.Property(e => e.Category)
                .HasMaxLength(50)
                .HasDefaultValue("userFiles");
            entity.Property(e => e.StorageFileName)
                .HasMaxLength(260);

            entity.ToTable("project_files", t => t.ExcludeFromMigrations());
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
            entity.Property(e => e.AssignedSkillsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ReasoningEffort)
                .HasMaxLength(16);
            entity.ToTable("agent_definitions", t => t.ExcludeFromMigrations());
        });

        // Tenancy tables. Owned by InferenceApiDbContext (A1); mapped here read-only so engine-side
        // code can resolve an execution's organization and the user's role in it.
        modelBuilder.Entity<Organization>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind)
                .HasConversion<string>();
            entity.Property(e => e.ComputePreference)
                .HasConversion<string>()
                .HasDefaultValue(ComputePreference.Auto);
            entity.ToTable("organizations", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<OrganizationMembership>(entity =>
        {
            entity.HasKey(e => new { e.OrganizationId, e.UserId });
            entity.Property(e => e.Role)
                .HasConversion<string>();
            entity.HasOne(e => e.Organization)
                .WithMany(o => o.Memberships)
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable("organization_memberships", t => t.ExcludeFromMigrations());
        });

        // Owned by InferenceApiDbContext (A1); read-only here so provider resolution can honour an
        // organization's per-built-in-agent provider choice.
        modelBuilder.Entity<OrganizationAgentProviderOverride>(entity =>
        {
            entity.HasKey(e => new { e.OrganizationId, e.AgentType });
            entity.Property(e => e.AgentType)
                .HasConversion<string>()
                .HasMaxLength(100);
            entity.Ignore(e => e.Organization);
            // InferenceProviderId names a model capability (docs/design/model-catalog.md §4.1 step 4).
            entity.HasOne(e => e.InferenceProvider)
                .WithMany()
                .HasForeignKey(e => e.InferenceProviderId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.ToTable("organization_agent_provider_overrides", t => t.ExcludeFromMigrations());
        });

        // NOTE (Risk R2): the model catalog (inference_providers, inference_models, model_capabilities,
        // model_capability_prices, pricing_settings; docs/design/model-catalog.md §2) is owned by
        // InferenceApiDbContext. These mappings are read-only and MUST stay excluded from this context's
        // migrations, or EF will try to create tables the Inference API already owns.
        modelBuilder.Entity<InferenceProvider>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind)
                .HasConversion<string>();
            entity.Property(e => e.ExtraHeadersJson)
                .HasColumnType("jsonb");
            entity.ToTable("inference_providers", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<InferenceModel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Provider)
                .WithMany(p => p.Models)
                .HasForeignKey(e => e.ProviderId);
            entity.ToTable("inference_models", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<ModelCapability>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Capability)
                .HasConversion<string>();
            entity.Property(e => e.PriceSource)
                .HasConversion<string>();
            entity.HasOne(e => e.Model)
                .WithMany(m => m.Capabilities)
                .HasForeignKey(e => e.ModelId);
            entity.ToTable("model_capabilities", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<ModelCapabilityPrice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Unit)
                .HasConversion<string>();
            entity.HasOne(e => e.ModelCapability)
                .WithMany(c => c.Prices)
                .HasForeignKey(e => e.ModelCapabilityId);
            entity.ToTable("model_capability_prices", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<PricingSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.ToTable("pricing_settings", t => t.ExcludeFromMigrations());
        });

        // Editor-v2 tables. Owned by InferenceApiDbContext; mapped here read-only (with the same
        // soft-delete filters, for read parity) so engine-side code can query them.
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
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.ToTable("edit_timelines", t => t.ExcludeFromMigrations());
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
            entity.ToTable("edit_timeline_ops", t => t.ExcludeFromMigrations());
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
            entity.ToTable("edit_timeline_snapshots", t => t.ExcludeFromMigrations());
        });

        // Mirrors InferenceApiDbContext's MediaDerivative mapping column for column, including the
        // filtered unique index — the engine is the writer (P2-M1), so it must see exactly the shape
        // the API migrated, and a miss in either mapping would only surface as a runtime error.
        // ExcludeFromMigrations still applies: the table is API-owned.
        modelBuilder.Entity<MediaDerivative>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectFile>()
                .WithMany()
                .HasForeignKey(e => e.SourceProjectFileId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Kind)
                .HasConversion<string>();
            entity.Property(e => e.SourceKind)
                .HasConversion<string>();
            entity.Property(e => e.SourceStorageKey)
                .HasMaxLength(1000);
            entity.Property(e => e.SourceETag)
                .HasMaxLength(200);
            entity.Property(e => e.ParamsHash)
                .HasMaxLength(64);
            entity.Property(e => e.ParamsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.CacheKey)
                .HasMaxLength(64);
            entity.Property(e => e.StorageKey)
                .HasMaxLength(1000);
            entity.Property(e => e.StorageBucket)
                .HasMaxLength(200);
            // The filmstrip index pointer, at the same 1000-character bound the API's mapping and the
            // migration declare. The engine is the writer, so its model must describe the column the
            // API created rather than an unbounded string of its own — deliberately not a CacheKey
            // component and not indexed: it names an index ABOUT a derivative, and several sheet rows
            // of one density legitimately share the one sidecar object.
            entity.Property(e => e.IndexStorageKey)
                .HasMaxLength(1000);
            entity.Property(e => e.MimeType)
                .HasMaxLength(200);
            // The source's stored probe (MediaSourceProbe). The engine is its only writer.
            entity.Property(e => e.SourceProbeJson)
                .HasColumnType("jsonb");
            // Same name, same single column, same partial filter as the API's mapping: the engine
            // computes MediaDerivativeIdentity.Compute's value and writes it here, so an index that
            // disagreed with the API's would be a constraint neither side could predict.
            entity.HasIndex(e => e.CacheKey)
                .IsUnique()
                .HasDatabaseName("ux_media_derivatives_cache_key")
                .HasFilter("deleted_at IS NULL");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.ToTable("media_derivatives", t => t.ExcludeFromMigrations());
        });

        // Media panel tables. Owned by InferenceApiDbContext; mapped here read-only so engine-side
        // code can resolve what a project's media pool holds without a second connection.
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
            entity.HasOne<MediaBin>()
                .WithMany()
                .HasForeignKey(e => e.ParentBinId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.Property(e => e.SmartFilterJson)
                .HasColumnType("jsonb");
            entity.ToTable("media_bins", t => t.ExcludeFromMigrations());
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
            entity.ToTable("media_bin_items", t => t.ExcludeFromMigrations());
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
            // Read-only here: the Inference API owns project_voices (see docs/voiceover.md
            // "Voices and consent"); the engine only resolves a step's ProjectVoiceId through the
            // same consent guard the API applies.
            entity.ToTable("project_voices", t => t.ExcludeFromMigrations());
        });

        // Read-only here: the Inference API owns project_avatars and project_avatar_references (migration
        // AddProjectAvatars, docs/design/avatars.md). The engine only resolves an avatar through the same
        // ProjectAvatar.IsSelectable rule the API applies; the column configuration is shared so it cannot drift.
        modelBuilder.Entity<ProjectAvatar>(entity =>
        {
            ProjectAvatarModel.ConfigureAvatar(entity);
            entity.ToTable("project_avatars", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<ProjectAvatarReference>(entity =>
        {
            ProjectAvatarModel.ConfigureReference(entity);
            entity.ToTable("project_avatar_references", t => t.ExcludeFromMigrations());
        });

        // R09.1: read-only here. The Inference API owns media_analysis_results (migration AddMediaAnalysis) and
        // writes it when a run finishes; the engine reads it by (organization, digest, kind, language).
        modelBuilder.Entity<MediaAnalysisResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Digest).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Language).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ResultJson).HasColumnType("jsonb");
            entity.Property(e => e.SummaryJson).HasColumnType("jsonb");
            entity.ToTable("media_analysis_results", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<UserVideoSpendLimit>(entity =>
        {
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.DailyLimitCredits).HasPrecision(18, 4);
            entity.Property(e => e.AlertThresholdCredits).HasPrecision(18, 4);
            entity.ToTable("user_video_spend_limits", t => t.ExcludeFromMigrations());
        });

        // Built-in sound-effects library: platform content, read-only here (the API owns table and seeding).
        modelBuilder.Entity<SfxLibraryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Tags).HasColumnType("jsonb");
            entity.ToTable("sfx_library_items", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<ProjectPronunciationEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.Property(e => e.Term).HasMaxLength(60);
            entity.Property(e => e.Spelling).HasMaxLength(120);
            // Read-only here: the Inference API owns project_pronunciation_entries.
            entity.ToTable("project_pronunciation_entries", t => t.ExcludeFromMigrations());
        });

        // Design systems (SPEC 3.7 / 16.1): read-only here. The Inference API owns design_systems,
        // design_system_versions and design_system_libraries (migrations AddDesignSystems and
        // AddDesignSystemLibraries); design_system_sources and design_system_change_proposals are not
        // mapped by the engine at all. Phase 4 maps parent_library_id and the library table.
        modelBuilder.Entity<DesignSystem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => new { e.ProjectId, e.Kind }).IsUnique();
            entity.HasOne<Project>().WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<DesignSystemVersion>().WithMany()
                .HasForeignKey(e => e.CurrentVersionId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<DesignSystemLibrary>().WithMany()
                .HasForeignKey(e => e.ParentLibraryId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.Kind).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.WatchJson).HasColumnType("jsonb");
            entity.ToTable("design_systems", t => t.ExcludeFromMigrations());
        });

        // Read-only: the Inference API owns design_system_libraries (migration
        // AddDesignSystemLibraries). The engine has no tenant filters, so none is declared here.
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
            entity.HasIndex(e => e.DesignSystemId).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.ToTable("design_system_libraries", t => t.ExcludeFromMigrations());
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
            entity.ToTable("design_system_versions", t => t.ExcludeFromMigrations());
        });

        // Composition workspaces (docs/design/composition-revisions.md). Owned by InferenceApiDbContext
        // (migration AddCompositionWorkspaces); mapped here with ExcludeFromMigrations because the engine
        // is where versions are CAPTURED (a render just finished in its sandbox) and where a revision
        // links its result — the same cross-service write the API does into workflow_executions.
        modelBuilder.Entity<CompositionWorkspace>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.OriginExecutionId).IsUnique();
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable("composition_workspaces", t => t.ExcludeFromMigrations());
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
            entity.ToTable("composition_workspace_versions", t => t.ExcludeFromMigrations());
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
            entity.ToTable("composition_revisions", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<StepChangeRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            // One row per target step: a request with several targets shares one replay run (ExecutionId).
            entity.HasIndex(e => e.ExecutionId);
            entity.HasIndex(e => new { e.ExecutionId, e.TargetStepOrder }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.SourceExecutionId });
            entity.Property(e => e.StepType).HasMaxLength(50);
            entity.Property(e => e.Instruction).HasMaxLength(4000);
            entity.Property(e => e.ScopeJson).HasColumnType("jsonb");
            entity.Property(e => e.OverridesJson).HasColumnType("jsonb");
            entity.Property(e => e.ReplayPlanJson).HasColumnType("jsonb");
            entity.ToTable("step_change_requests", t => t.ExcludeFromMigrations());
        });

        // Designed-graphic requests (docs/editor.md "Designed graphics"). Owned by InferenceApiDbContext
        // (migration AddEditImageGraphicRequests); mapped here with ExcludeFromMigrations because the
        // engine reads a request by its execution and writes the Result* columns once the PNG is
        // uploaded. Deliberately NO relationship to edit_image_documents: the engine does not map the
        // layer document at all (an interactive editor's state), only this request row.
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
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Placement).HasMaxLength(32);
            entity.Property(e => e.ResultStorageKey).HasMaxLength(1000);
            entity.Property(e => e.ResultCompositionId).HasMaxLength(200);
            entity.Property(e => e.ResultSummary).HasMaxLength(1000);
            entity.Property(e => e.ResultModelName).HasMaxLength(200);
            entity.ToTable("edit_image_graphic_requests", t => t.ExcludeFromMigrations());
        });

        // --- Owned tables ---

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
            // A run's ExecutionSnapshot belongs to that run: deleting the execution deletes it (and,
            // through the steps cascade, nothing of any other run). Shadow column, engine-only: no
            // other code reads it, and every other kind leaves it null.
            entity.Property<Guid?>(SnapshotOfExecutionIdColumn).HasColumnName("snapshot_of_execution_id");
            entity.HasIndex(SnapshotOfExecutionIdColumn);
            entity.HasOne<WorkflowExecution>()
                .WithMany()
                .HasForeignKey(SnapshotOfExecutionIdColumn)
                .OnDelete(DeleteBehavior.Cascade);
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
        });

        modelBuilder.Entity<WorkflowExecution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OrganizationId);
            // S-M1b (public-api T-022): one run per public API start correlation id. The id is deterministic for an
            // Idempotency-Key (`apirun:idem:...`) and per queue request (`apirun:req:...`), so this is what makes a
            // start committed before its response was lost findable instead of runnable a second time. Partial: only
            // public starts carry the prefix; every other run keeps whatever correlation id it has. ENGINE migration
            // (the table is engine-owned): the lead generates it.
            entity.HasIndex(e => e.CorrelationId)
                .IsUnique()
                .HasDatabaseName("ux_workflow_executions_api_run_correlation")
                .HasFilter("correlation_id LIKE 'apirun:%'");
            // The recovery sweeper's two scans: lapsed leases on Running rows, stale Queued rows.
            entity.HasIndex(e => e.LeaseExpiresAt).HasFilter("status = 'Running'");
            entity.HasIndex(e => e.QueuedAt).HasFilter("status = 'Queued'");
            entity.Property(e => e.LeaseOwner).HasMaxLength(200);
            entity.HasOne(e => e.WorkflowDefinition)
                .WithMany(w => w.Executions)
                .HasForeignKey(e => e.WorkflowDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.WorkflowExecutions)
                .HasForeignKey(e => e.ProjectId)
                // allow project deletion to cascade into executions so that the inferencing
                // API (which doesn't track executions) can remove a project without hitting
                // a foreign key violation. StepResults/ReviewScores already cascade off
                // the execution.
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.CurrentStep)
                .WithMany()
                .HasForeignKey(e => e.CurrentStepId)
                .OnDelete(DeleteBehavior.SetNull);
            // The run's own ExecutionSnapshot (see WorkflowExecution.SnapshotDefinitionId). SET NULL:
            // the snapshot is removed WITH the run (the cascade on workflow_definitions above), never
            // the other way round.
            entity.HasOne<WorkflowDefinition>()
                .WithMany()
                .HasForeignKey(e => e.SnapshotDefinitionId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.SnapshotDefinitionId);
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
            // The run's editing style snapshot and output-format override (see
            // docs/editing-styles.md). Both nullable: a run without either is unchanged.
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
            // The typed run brief's stored part (docs/run-brief.md). Engine migration AddExecutionRunBrief owns the column.
            entity.Property(e => e.RunBriefJson)
                .HasColumnType("jsonb");
            // Set when a Passed run's review loop stopped below its minimum. Engine migration AddExecutionAttention owns the column.
            entity.Property(e => e.AttentionJson)
                .HasColumnType("jsonb");
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
                // historically results no longer needed when a step definition is removed
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.TimelineStorageKey);
            // The daily operator digest's window scan over FAILED steps only: rows that completed in the window, and the
            // (rare) failed rows that never recorded a completion time, matched on their execution time. Partial, so they
            // stay small and cost writes of non-failed rows nothing.
            entity.HasIndex(e => e.CompletedAt)
                .HasDatabaseName("ix_workflow_step_results_failed_completed_at")
                .HasFilter("status = 'Failed'");
            entity.HasIndex(e => e.ExecutedAt)
                .HasDatabaseName("ix_workflow_step_results_failed_uncompleted_executed_at")
                .HasFilter("status = 'Failed' AND completed_at IS NULL");
            entity.Property(e => e.PrimaryModelName).HasMaxLength(200);
            entity.Property(e => e.InputJson)
                .HasColumnType("text");
            entity.Property(e => e.OutputJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ToolCallsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ReasoningJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.ChatTranscriptJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.Status)
                .HasConversion<string>();
            // B6b: the class/code a failed step was charged (and refunded) by, and what it actually
            // cost. Stored by name, like every other enum column here — the Inference API maps this
            // table read-only and applies the same conversion, so both contexts agree on the schema.
            entity.Property(e => e.FailureClass)
                .HasConversion<string>()
                .HasDefaultValue(FailureClass.None);
            entity.Property(e => e.FailureCode)
                .HasMaxLength(64);
        });

        modelBuilder.Entity<ReviewScore>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.WorkflowExecution)
                .WithMany(e => e.ReviewScores)
                .HasForeignKey(e => e.WorkflowExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
            // The ReviewLoop result that produced the score (ReviewScoreLineage). SET NULL: a score
            // outlives its row only as a legacy-shaped score, never blocks the row's deletion.
            entity.HasOne<WorkflowStepResult>()
                .WithMany()
                .HasForeignKey(e => e.StepResultId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.StepResultId);
        });

        // WorkflowStepCacheEntry: deliberately NO HasOne/foreign key to Project or
        // WorkflowDefinition — see WorkflowStepCacheEntry's own doc comment for why (cache rows
        // must survive workflow-definition edits and are pruned by TTL policy, not by cascade).
        modelBuilder.Entity<WorkflowStepCacheEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.CacheKey }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.ExpiresAt });
            entity.Property(e => e.CacheKey)
                .HasMaxLength(64);
            entity.Property(e => e.StepType)
                .HasConversion<string>();
            entity.Property(e => e.AgentType)
                .HasConversion<string>();
            entity.Property(e => e.Output)
                .HasColumnType("text");
            entity.Property(e => e.ChatTranscriptJson)
                .HasColumnType("jsonb");
        });

        // DecisionObservation: deliberately NO HasOne/foreign key to WorkflowExecution or
        // WorkflowStepResult — see DecisionObservation's own doc comment for why (observations
        // must survive execution/step-result deletion and can be queried independently).
        modelBuilder.Entity<DecisionObservation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExecutionId);
            entity.HasIndex(e => e.OrganizationId);
            entity.HasIndex(e => new { e.Site, e.CreatedAt });
            entity.Property(e => e.Kind)
                .HasConversion<string>();
            entity.Property(e => e.AgentType)
                .HasConversion<string>();
            // AcceptMetric is stored as text rather than as a converted enum, deliberately: an
            // unrecognised value written by hand or by a future version must survive the round-trip
            // so the calibration aggregation can exclude that row, instead of throwing on read and
            // 500-ing the endpoint. Both columns are nullable and stay that way — a null AcceptMetric
            // is a historic row (read as Top, which is what wrote it) and a null ScoreCutoff is every
            // metric except AtOrAbove. See DecisionObservation's own doc comments.
            entity.Property(e => e.AcceptMetric);
            entity.Property(e => e.ProbabilitiesJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.DownstreamOutcomeJson)
                .HasColumnType("jsonb");
        });

        // ExternalGenerationJob: deliberately NO HasOne/foreign key to WorkflowExecution,
        // WorkflowStepResult, or Project — see ExternalGenerationJob's own doc comment for why
        // (jobs must survive execution/step-result/project deletion and can be queried independently).
        modelBuilder.Entity<ExternalGenerationJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
            entity.Property(e => e.Status)
                .HasConversion<string>();
            // The column default is "credits": the ExternalGenerationJobsCostCredits migration
            // converted the legacy USD rows and moved the default with the meaning.
            entity.Property(e => e.CostUnit)
                .HasDefaultValue(ExternalGenerationJob.CreditsUnit);
            // Avatar usage (docs/design/avatars.md "Engine column", migration AddExternalGenerationJobAvatar): no FK,
            // the engine does not own avatars. Partial: almost every row has no avatar.
            entity.HasIndex(e => e.AvatarId)
                .HasFilter("avatar_id IS NOT NULL")
                .HasDatabaseName("ix_external_generation_jobs_avatar_id");
            // Every avatar a take showed (docs/design/avatar-objects.md, migration AddExternalGenerationJobSubjects):
            // a small JSON array of ids, read back per project by the avatar usage report. No index: it is only ever
            // read through the project index above.
            entity.Property(e => e.SubjectIdsJson).HasColumnType("jsonb");
        });

        // Read-only here (schema ownership, Risk R2): the Inference API owns usage_events, rate_cards and
        // rate_multipliers and their migrations; the engine's metering recorder only INSERTS usage_events
        // rows through this mapping and prices from the two rate tables. Never add a migration for them here.
        modelBuilder.Entity<UsageEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).HasConversion<string>();
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.ProviderKind).HasConversion<string>();
            entity.Property(e => e.BillingMode).HasConversion<string>();
            entity.ToTable("usage_events", t => t.ExcludeFromMigrations());
        });

        // Content moderation audit (content-moderation T-005): the Inference API owns the table and
        // its migration; the engine appends its own rows through this mapping. Deliberately NO tenant
        // query filter — a row is attributed by OrganizationId and outlives the org, like the API's
        // own mapping. Never add a migration for this table here.
        modelBuilder.Entity<ContentModerationDecision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Surface).HasMaxLength(32);
            entity.Property(e => e.Action).HasMaxLength(16);
            entity.Property(e => e.ContentKind).HasMaxLength(16);
            entity.Property(e => e.ContentSha256).HasMaxLength(64);
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
            entity.ToTable("content_moderation_decisions", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<RateMultiplier>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BillingMode).HasConversion<string>();
            entity.Property(e => e.UsageKind).HasConversion<string>();
            entity.ToTable("rate_multipliers", t => t.ExcludeFromMigrations());
        });

        // Billing tables (B5): the Inference API owns them and their migrations; the engine's credit gate
        // (B6) reads and writes them through CreditLedger on this mapping. Never add a migration for them here.
        modelBuilder.Entity<Plan>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.EntitlementsJson).HasColumnType("jsonb");
            entity.Property(e => e.ProviderPriceIdsJson).HasColumnType("jsonb");
            entity.ToTable("plans", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.ToTable("subscriptions", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<CreditAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
            entity.ToTable("credit_accounts", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<CreditLedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.AllocationsJson).HasColumnType("jsonb");
            entity.ToTable("credit_ledger_entries", t => t.ExcludeFromMigrations());
        });

        // The ledger writes api_key_spend inside the reserve transaction and reads api_keys for caps and status;
        // the Inference API owns both tables, so the engine never migrates them.
        modelBuilder.Entity<ApiKeySpend>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.ToTable("api_key_spend", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Scopes).HasColumnType("text[]");
            entity.Property(e => e.ProjectIds).HasColumnType("uuid[]");
            entity.Property(e => e.ExpiryWarned7dAt).HasColumnName("expiry_warned_7d_at");
            entity.Property(e => e.ExpiryWarned1dAt).HasColumnName("expiry_warned_1d_at");
            entity.ToTable("api_keys", t => t.ExcludeFromMigrations());
        });

        modelBuilder.Entity<CreditGrant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).HasConversion<string>();
            entity.ToTable("credit_grants", t => t.ExcludeFromMigrations());
        });

        // Read-only here (F11): the Inference API owns platform_settings and is its only writer; the
        // Go runner gateway reads the same rows by raw SQL. The engine reads it through
        // PlatformSwitches and must never migrate it, or the two services would race on the schema.
        modelBuilder.Entity<PlatformSetting>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(100);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.UpdatedBy).HasMaxLength(320);
            entity.ToTable("platform_settings", t => t.ExcludeFromMigrations());
        });

        // Read-only here: the Inference API owns runner_devices; the Go gateway writes it by raw SQL.
        modelBuilder.Entity<RunnerDevice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.CapabilitiesJson).HasColumnType("jsonb");
            entity.ToTable("runner_devices", t => t.ExcludeFromMigrations());
        });

        // RunnerJob/RunnerSession: written by the Go gateway through raw SQL (it never migrates).
        // Only the execution/job links are real FKs; runner and step ids are plain columns because
        // the gateway owns their lifecycle and a revoked device row is never deleted.
        modelBuilder.Entity<RunnerJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(200);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Target).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.RequiredCapsJson).HasColumnType("jsonb");
            entity.Property(e => e.SpecJson).HasColumnType("jsonb");
            entity.Property(e => e.ResultJson).HasColumnType("jsonb");
            entity.Property(e => e.ErrorClass).HasMaxLength(50);
            entity.Property(e => e.GatewayInstance).HasMaxLength(200);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.Status, e.Target, e.OrganizationId, e.CreatedAt });
            entity.HasIndex(e => e.ExecutionId);
            entity.HasIndex(e => e.LeaseExpiresAt)
                .HasFilter("status IN ('Assigned', 'Running')");
            entity.HasOne(e => e.Execution)
                .WithMany()
                .HasForeignKey(e => e.ExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunnerSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GatewayInstance).HasMaxLength(200);
            entity.HasIndex(e => e.JobId).IsUnique();
            // At most one open session per execution.
            entity.HasIndex(e => e.ExecutionId).IsUnique().HasFilter("closed_at IS NULL");
            entity.HasOne(e => e.Job)
                .WithMany()
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
