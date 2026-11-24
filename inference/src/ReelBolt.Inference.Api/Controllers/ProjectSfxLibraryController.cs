using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.SfxLibrary;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

public sealed record SfxLibraryAddResponse(string MediaRef, Guid FileId, string Name);

public sealed record SfxLibraryStarterSetResponse(IReadOnlyList<SfxLibraryAddResponse> Items, int Added);

/// <summary>
/// Puts built-in library sounds into a project (docs/sfx-library.md). Edit permission on the project, one consult of
/// <see cref="IProjectAccessService"/> per action; an unknown or disabled sound and a foreign project are the same 404.
/// Free of charge and of quota; the copy is an ordinary audio project file.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/sfx-library")]
[Authorize]
public class ProjectSfxLibraryController : ControllerBase
{
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser currentUser;
    private readonly SfxLibraryProjectAdder adder;

    public ProjectSfxLibraryController(
        IProjectAccessService projectAccess, ICurrentUser currentUser, SfxLibraryProjectAdder adder)
    {
        _projectAccess = projectAccess;
        this.currentUser = currentUser;
        this.adder = adder;
    }

    public const string CapCode = "SFX_LIBRARY_PROJECT_CAP";

    [HttpPost("{id:guid}/add")]
    public async Task<IActionResult> Add(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } failure) return failure;
        SfxAddResult result = await adder.AddAsync(projectId, id, currentUser.UserId, ct);
        return result.Outcome switch
        {
            SfxAddOutcome.Added or SfxAddOutcome.AlreadyThere => Ok(ToResponse(result.File!)),
            SfxAddOutcome.CapReached => Cap(),
            SfxAddOutcome.RateLimited => TooMany(),
            _ => NotFound()
        };
    }

    [HttpPost("starter-set")]
    public async Task<IActionResult> StarterSet(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } failure) return failure;
        IReadOnlyList<SfxAddResult> results = await adder.AddStarterSetAsync(projectId, currentUser.UserId, ct);
        if (results.Any(r => r.Outcome == SfxAddOutcome.RateLimited)) return TooMany();
        if (results.Any(r => r.Outcome == SfxAddOutcome.CapReached)) return Cap();
        List<SfxLibraryAddResponse> items = results.Where(r => r.File is not null).Select(r => ToResponse(r.File!)).ToList();
        return Ok(new SfxLibraryStarterSetResponse(items, results.Count(r => r.Outcome == SfxAddOutcome.Added)));
    }

    private static SfxLibraryAddResponse ToResponse(ProjectFile f) => new($"file:{f.Id:D}", f.Id, f.OriginalFileName);

    private ObjectResult Cap() => Conflict(new { code = CapCode, message = "This project already holds the maximum number of library sounds." });

    private ObjectResult TooMany() => StatusCode(StatusCodes.Status429TooManyRequests, new { code = "SFX_LIBRARY_RATE_LIMIT", message = "Too many sound additions; try again in a minute." });
}
