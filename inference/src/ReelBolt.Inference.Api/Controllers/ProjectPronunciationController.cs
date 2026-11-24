using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Pronunciation;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// A project's pronunciation glossary: how names and words are spoken in every Voiceover step of the
/// project. The project is loaded first (404 when missing or another tenant's; a viewer cannot write).
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/pronunciation")]
[Authorize]
public class ProjectPronunciationController : ControllerBase
{
    private readonly IProjectAccessService _projectAccess;
    private readonly ProjectPronunciationService service;

    public ProjectPronunciationController(IProjectAccessService projectAccess, ProjectPronunciationService service)
    {
        _projectAccess = projectAccess;
        this.service = service;
    }

    [HttpGet]
    public async Task<ActionResult<PronunciationListResponse>> List(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        List<ProjectPronunciationEntry> entries = await service.ListAsync(projectId, ct);
        return new PronunciationListResponse(entries.Select(ToResponse).ToList(), ProjectPronunciationEntry.MaxEntriesPerProject);
    }

    [HttpPost]
    public async Task<ActionResult<PronunciationEntryResponse>> Create(
        Guid projectId, [FromBody] PronunciationEntryRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        PronunciationSaveResult result = await service.CreateAsync(
            projectId, request.Term, request.Spelling, request.CaseSensitive ?? false, AttributionFor(access, projectId), ct);
        return Map(result, created: true);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PronunciationEntryResponse>> Update(
        Guid projectId, Guid id, [FromBody] PronunciationEntryRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        PronunciationSaveResult result = await service.UpdateAsync(
            projectId, id, request.Term, request.Spelling, request.CaseSensitive ?? false, AttributionFor(access, projectId), ct);
        return Map(result, created: false);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        return await service.DeleteAsync(projectId, id, ct) ? NoContent() : NotFound();
    }

    private ModerationAttribution AttributionFor(ProjectAccessResult access, Guid projectId) =>
        ProjectPronunciationService.Attribution(access.Project!.OrganizationId, projectId, EditImageDocumentSupport.CallerId(HttpContext));

    private ActionResult<PronunciationEntryResponse> Map(PronunciationSaveResult r, bool created) => r.Status switch
    {
        PronunciationSaveStatus.Invalid => BadRequest(new
        {
            message = "That pronunciation entry is not valid.",
            errors = r.Errors ?? []
        }),
        PronunciationSaveStatus.Full => Conflict(new
        {
            code = "GLOSSARY_FULL",
            message = $"A project can hold at most {ProjectPronunciationEntry.MaxEntriesPerProject} pronunciations. Remove one first."
        }),
        PronunciationSaveStatus.TermExists => Conflict(new
        {
            code = "GLOSSARY_TERM_EXISTS",
            message = "That word is already in the glossary. Edit the existing entry instead."
        }),
        PronunciationSaveStatus.Blocked => BadRequest(new
        {
            code = "CONTENT_BLOCKED",
            message = r.Errors?.FirstOrDefault() ?? "That pronunciation may violate the content policy."
        }),
        PronunciationSaveStatus.NotFound => NotFound(),
        _ when created => StatusCode(StatusCodes.Status201Created, ToResponse(r.Entry!)),
        _ => ToResponse(r.Entry!)
    };

    private static PronunciationEntryResponse ToResponse(ProjectPronunciationEntry e) =>
        new(e.Id, e.Term, e.Spelling, e.CaseSensitive, e.UpdatedAt);
}
