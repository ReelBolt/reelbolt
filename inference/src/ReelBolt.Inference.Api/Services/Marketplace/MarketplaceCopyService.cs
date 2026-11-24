using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>Optional body of the copy route.</summary>
public sealed record CopyRecipeRequest(string? Name);

/// <summary>One thing the copy still needs from the project (the shape the template cards use, plus the step it concerns).</summary>
public sealed record CopyNeedDto(string Need, string Message, string Action, int? StepOrder);

/// <summary>The copy route's answer.</summary>
public sealed record CopyResponse(Guid WorkflowId, string Name, IReadOnlyList<CopyNeedDto> Needs);

/// <summary><see cref="AlreadyCopied"/> is true when an earlier identical request made the workflow (answered 200, not 201).</summary>
public sealed record CopyOutcome(CopyResponse Body, bool AlreadyCopied);

/// <summary>
/// Copy a listed recipe into a project of the caller's organization (docs/design/recipe-marketplace.md). The copy is an ordinary
/// workflow of the project with no live link to the recipe; nothing runs and nothing is charged. Counts one organization per
/// recipe (<c>marketplace_copies</c> unique on recipe and organization) under a per-(recipe, organization) lock.
/// </summary>
public sealed partial class MarketplaceCopyService(
    InferenceApiDbContext db,
    ICurrentUser user,
    IProjectAccessService projectAccess,
    IProjectReadinessFacts readiness,
    StepProviderOwnershipValidator providerOwnership,
    IOptions<MarketplaceOptions> options,
    ILogger<MarketplaceCopyService> logger,
    WorkflowLintGate? lintGate = null,
    IProductEventEmitter? productEvents = null)
{
    /// <summary>The product-event name of a copy (detail = <c>market:</c> + recipe id without dashes).</summary>
    public const string CopiedEvent = "product.marketplace_copied";

    [GeneratedRegex(@" \(\d{1,4}\)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DedupeSuffix();

    public async Task<MarketplaceResult<CopyOutcome>> CopyAsync(Guid projectId, Guid recipeId, CopyRecipeRequest? request, CancellationToken ct)
    {
        static MarketplaceResult<CopyOutcome> Fail(MarketplaceError e) => MarketplaceResult<CopyOutcome>.Fail(e);

        if (!options.Value.Enabled)
            return Fail(new MarketplaceError(404));
        if (!user.EmailVerified)
        {
            return Fail(new MarketplaceError(403, EmailVerificationGate.ErrorCode,
                "Verify your email address before copying a recipe. Check your inbox for the verification link."));
        }

        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (!access.IsFound)
            return Fail(new MarketplaceError(access.Outcome == ProjectAccessOutcome.Forbidden ? 403 : 404));
        Project project = access.Project!;

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is not { Status: RecipeStatus.Listed, HiddenByReports: false, CurrentVersionId: { } versionId })
            return Fail(new MarketplaceError(404));
        MarketplaceRecipeVersion? version = await db.MarketplaceRecipeVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId && v.RecipeId == recipeId, ct);
        if (version is null)
            return Fail(new MarketplaceError(404));
        PortableRecipe? snapshot = PortableRecipeJson.TryParse(version.SnapshotJson);
        if (snapshot is null)
            return await RefuseBrokenAsync(recipeId, version.Id, "unreadable", 1, ct);

        if (snapshot.SchemaVersion != PortableRecipe.CurrentSchemaVersion)
        {
            return Fail(new MarketplaceError(409, "RECIPE_UNSUPPORTED",
                "This recipe was made with a newer version of the format and cannot be copied yet."));
        }

        // A stored snapshot may predate or bypass the publish gate: every publish-time check runs again before anything is written.
        IReadOnlyList<string> broken = PortableRecipeMaterializer.Recheck(snapshot);
        if (broken.Count > 0)
        {
            logger.LogWarning("Marketplace recipe {RecipeId} failed the copy-time recheck: {Reasons}", recipeId, string.Join(" | ", broken.Take(5)));
            return await RefuseBrokenAsync(recipeId, version.Id, "snapshot_recheck", broken.Count, ct);
        }

        string requested = PortableRecipeMaterializer.CleanName(request?.Name);
        if (requested.Length == 0)
            requested = PortableRecipeMaterializer.CleanName(recipe.Name);
        if (requested.Length == 0)
            requested = "Community recipe";

        Guid orgId = project.OrganizationId;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await CopyOnceAsync(project, orgId, recipe, version, snapshot, requested, ct);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // The unique index on (recipe, organization) is the backstop of the lock: the second pass sees the row.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>The stored snapshot cannot be copied: an audit row for the admins, 409 for the caller, nothing written to the project.</summary>
    private async Task<MarketplaceResult<CopyOutcome>> RefuseBrokenAsync(Guid recipeId, Guid versionId, string reason, int problems, CancellationToken ct)
    {
        if (reason == "unreadable")
            logger.LogWarning("Marketplace recipe {RecipeId} has no readable current snapshot", recipeId);
        db.MarketplaceAudit.Add(new MarketplaceAuditEntry
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            VersionId = versionId,
            Action = "copy_snapshot_invalid",
            ActorKind = "system",
            DetailJson = System.Text.Json.JsonSerializer.Serialize(new { reason, problems }),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return MarketplaceResult<CopyOutcome>.Fail(new MarketplaceError(
            409, "RECIPE_UNAVAILABLE", "This recipe is not available to copy right now. Nothing was added to your project."));
    }

    private async Task<MarketplaceResult<CopyOutcome>> CopyOnceAsync(
        Project project, Guid orgId, MarketplaceRecipe recipe, MarketplaceRecipeVersion version, PortableRecipe snapshot, string requested, CancellationToken ct)
    {
        string templateKey = PortableRecipeMaterializer.TemplateKeyOf(recipe.Id);
        WorkflowDefinition workflow;
        await using (MarketplaceKeyLock gate = await MarketplaceKeyLock.AcquireAsync(db, $"market-copy:{recipe.Id:N}:{orgId:N}", ct))
        {
            // Idempotent per (recipe, project, name): a repeat answers with the copy that exists.
            List<WorkflowDefinition> mine = await db.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.ProjectId == project.Id && w.TemplateKey == templateKey && w.Kind == WorkflowDefinitionKind.Standard)
                .ToListAsync(ct);
            WorkflowDefinition? existing = mine.FirstOrDefault(w => w.Name == requested)
                ?? mine.FirstOrDefault(w => w.Name.StartsWith(requested, StringComparison.Ordinal)
                    && w.Name.Length > requested.Length && DedupeSuffix().IsMatch(w.Name[requested.Length..]));
            if (existing is not null)
                return MarketplaceResult<CopyOutcome>.Ok(new CopyOutcome(await ResponseAsync(project, existing.Id, existing.Name, snapshot, ct), true));

            HashSet<string> taken = [.. await db.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard && w.Name.StartsWith(requested))
                .Select(w => w.Name)
                .ToListAsync(ct)];
            string name = requested;
            for (int n = 2; taken.Contains(name); n++)
                name = $"{requested} ({n})";

            IReadOnlySet<AgentType> needed = PortableRecipeMaterializer.AgentTypesOf(snapshot);
            List<AgentDefinition> builtIns = await db.AgentDefinitions.AsNoTracking()
                .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
                .Where(a => a.IsBuiltIn && needed.Contains(a.AgentType))
                .ToListAsync(ct);
            Dictionary<AgentType, AgentDefinition> agents = builtIns
                .GroupBy(a => a.AgentType)
                .ToDictionary(g => g.Key, g => g.OrderBy(a => a.CreatedAt).First());

            MaterializeResult materialized = PortableRecipeMaterializer.Materialize(snapshot, recipe.Id, project.Id, name, agents);
            if (materialized.Value is not { } built)
                return Unavailable(recipe.Id, materialized.Errors);

            // The target project's own checks on the (empty) provider references; the config checks ran in the materializer.
            IReadOnlyList<string> ownership = await providerOwnership.ValidateAsync(
                orgId, project.Id, [.. built.Steps.Select(AssistantStepShapes.FromStep)], ct);
            if (ownership.Count > 0)
                return Unavailable(recipe.Id, ownership);

            workflow = built.Workflow;
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync(ct);

            if (!await db.MarketplaceCopies.AnyAsync(c => c.RecipeId == recipe.Id && c.OrgId == orgId, ct))
            {
                db.MarketplaceCopies.Add(new MarketplaceCopy
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipe.Id,
                    VersionId = version.Id,
                    OrgId = orgId,
                    WorkflowId = workflow.Id,
                    FirstCopiedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);
                await MarketplaceCounters.AddCopyOrgAsync(db, recipe.Id, ct);
            }

            await gate.CommitAsync(ct);
        }

        // Warn-only, like the template path: findings are logged and counted, never a refusal of a copy.
        if (lintGate is not null)
            _ = await lintGate.CheckAsync(project, workflow.Id, null, null, null, ct, atRunStart: false);
        if (productEvents is not null)
            await productEvents.AppendAsync(user.UserId, orgId, CopiedEvent, templateKey, ct);

        return MarketplaceResult<CopyOutcome>.Ok(new CopyOutcome(await ResponseAsync(project, workflow.Id, workflow.Name, snapshot, ct), false));
    }

    private MarketplaceResult<CopyOutcome> Unavailable(Guid recipeId, IReadOnlyList<string> reasons)
    {
        logger.LogWarning("Marketplace recipe {RecipeId} could not be materialized: {Reasons}", recipeId, string.Join(" | ", reasons.Take(5)));
        return MarketplaceResult<CopyOutcome>.Fail(new MarketplaceError(
            422, "RECIPE_NOT_COPYABLE", "This recipe cannot be copied right now. Nothing was added to your project."));
    }

    private async Task<CopyResponse> ResponseAsync(Project project, Guid workflowId, string name, PortableRecipe snapshot, CancellationToken ct) =>
        new(workflowId, name, await NeedsAsync(project, snapshot, ct));

    /// <summary>The open slots of the recipe plus what the project lacks for its steps (<see cref="ProjectReadinessFacts.Evaluate"/>).</summary>
    private async Task<IReadOnlyList<CopyNeedDto>> NeedsAsync(Project project, PortableRecipe snapshot, CancellationToken ct)
    {
        List<CopyNeedDto> needs = [];
        foreach (PortableSlot slot in snapshot.Slots.OrderBy(s => s.StepOrder).ThenBy(s => s.Field, StringComparer.Ordinal))
        {
            (string verb, string action) = slot.Kind switch
            {
                PortableSlotKinds.Provider => ("Choose a provider for", "connect-provider"),
                PortableSlotKinds.Voice => ("Choose a voice for", "choose-voice"),
                _ => ("Pick a file for", "add-files")
            };
            needs.Add(new CopyNeedDto(slot.Kind, $"{verb} {slot.Label.ToLowerInvariant()} (step {slot.StepOrder})", action, slot.StepOrder));
        }

        ProjectReadinessSnapshot facts = await readiness.LoadAsync(project, ct);
        IReadOnlyList<AppStepFacts> stepFacts =
            PortableRecipeValidator.FactsOf(snapshot);
        needs.AddRange(ProjectReadinessFacts.Evaluate(stepFacts, facts).Select(m => new CopyNeedDto(m.Need, m.Message, m.Action, null)));
        return needs;
    }
}
