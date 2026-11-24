using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Projects;

/// <summary>
/// What a caller asks a new project to be. The tag and folder members are the dashboard's own options, and so
/// is <see cref="StarterTemplateKey"/>: the listed recipe the project starts with instead of the automatic
/// starter (null = the automatic starter, as always).
/// </summary>
public sealed record ProjectCreationInput(
    string Name,
    string? Description,
    Guid? FolderId = null,
    IReadOnlyList<Guid>? TagIds = null,
    string? StarterTemplateKey = null);

/// <summary>The new project, or the refusal the dashboard has always answered with (a 404 or a 409 envelope).</summary>
public sealed record ProjectCreationResult(Project? Project, ActionResult? Failure);

/// <summary>
/// The one project-creation path: the dashboard's <c>POST /api/v1/projects</c> and the public API's
/// <c>POST /api/public/v1/projects</c> both call it, so the tag/folder validation, the owner, the
/// auto-provisioned templates and the product event cannot drift. Authority is NOT checked here —
/// both callers check it first with <c>IProjectAccessService</c>.
/// <para>
/// A project starts with exactly one app. Without a starter key that is the automatic starter
/// (<see cref="WorkflowTemplateProvisioningService.EnsureAutoTemplatesAsync"/>). With one
/// (docs/design/new-user-funnel.md section 6) it is that recipe and the automatic starter is NOT created;
/// the key must be a recipe this deployment lists, checked before anything is written, so an unknown,
/// hidden or switched-off key answers 400 and creates nothing.
/// </para>
/// </summary>
public interface IProjectCreationService
{
    Task<ProjectCreationResult> CreateAsync(ProjectCreationInput input, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ProjectCreationService(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    WorkflowTemplateProvisioningService templates,
    IProductEventEmitter? productEvents = null) : IProjectCreationService
{
    /// <summary>The refusal code of a <c>starterTemplateKey</c> that is not a recipe this deployment lists.</summary>
    public const string UnknownStarterTemplateCode = "STARTER_TEMPLATE_UNKNOWN";

    public async Task<ProjectCreationResult> CreateAsync(ProjectCreationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The starter is resolved first: a key that cannot be honoured must leave nothing behind.
        WorkflowTemplateDefinition? starter = null;
        if (input.StarterTemplateKey is not null)
        {
            starter = await templates.FindStarterRecipeAsync(input.StarterTemplateKey, ct);
            if (starter is null)
                return Refuse(new BadRequestObjectResult(new
                {
                    code = UnknownStarterTemplateCode,
                    message = "That starting recipe is not available."
                }));
        }

        Guid[] tagIds = (input.TagIds ?? []).Distinct().ToArray();
        if (tagIds.Length > ProjectOrganizationRules.MaxTagsPerProject)
            return Refuse(new ConflictObjectResult(new
            {
                code = "PROJECT_TAG_LIMIT_REACHED",
                message = "A project can carry at most 20 tags."
            }));
        if (tagIds.Length > 0
            && await db.ProjectTags.CountAsync(
                t => tagIds.Contains(t.Id) && t.OrganizationId == currentUser.OrganizationId, ct) != tagIds.Length)
            return Refuse(new NotFoundResult());
        if (input.FolderId is { } folderId
            && !await db.ProjectFolders.AnyAsync(
                f => f.Id == folderId && f.OrganizationId == currentUser.OrganizationId, ct))
            return Refuse(new NotFoundResult());

        ApplicationUser? user = await db.ApplicationUsers.FirstOrDefaultAsync(u => u.Id == currentUser.UserId, ct);
        if (user == null)
        {
            user = new ApplicationUser
            {
                Id = currentUser.UserId,
                Email = currentUser.Email,
                DisplayName = currentUser.Email,
                CreatedAt = DateTime.UtcNow
            };
            db.ApplicationUsers.Add(user);
        }

        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = input.Description,
            OwnerId = currentUser.UserId,
            OrganizationId = currentUser.OrganizationId!.Value,
            Status = ProjectStatus.Draft,
            FolderId = input.FolderId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Projects.Add(project);
        foreach (Guid tagId in tagIds) db.ProjectTagLinks.Add(new ProjectTagLink { ProjectId = project.Id, TagId = tagId });

        if (starter is null)
        {
            await db.SaveChangesAsync(ct);
            await templates.EnsureAutoTemplatesAsync(project.Id, ct);
        }
        else
        {
            // The project and its chosen app are one unit: if the recipe cannot be created (a deployment
            // without its built-in agents), the project is not left behind without the app that was asked
            // for. One transaction on a relational database, unless the caller already holds one.
            await using IDbContextTransaction? tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(ct)
                : null;
            await db.SaveChangesAsync(ct);
            await templates.CreateFromTemplateAsync(project.Id, starter.Key, skipIfExists: true, ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }

        if (productEvents is not null)
        {
            await productEvents.FirstAsync(currentUser.UserId, project.OrganizationId, ProductEventNames.ProjectCreated, ct);

            // The recipe chosen for the project. The detail is the CATALOG's key (the request value only
            // selected it), so the row can never carry caller-chosen text. The automatic starter is not a
            // choice and writes nothing, as before.
            if (starter is not null)
                await productEvents.AppendAsync(currentUser.UserId, project.OrganizationId, ProductEventNames.TemplateChosen, starter.Key, ct);
        }

        return new ProjectCreationResult(project, null);
    }

    private static ProjectCreationResult Refuse(ActionResult failure) => new(null, failure);
}
