using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Editor v2, P9: the caller's own editor preferences — a custom keymap and saved panel layouts. Per
/// USER and never per project, so there is no project to authorize: the row is keyed by the JWT's
/// <c>sub</c> and only ever read or written for that user. Shape and size are bounded by
/// <see cref="EditorPreferencesValidator"/>; what an action id or a layout means is the client's.
/// </summary>
[ApiController]
[Route("api/v1/editor/preferences")]
[Authorize]
public class EditorPreferencesController : ControllerBase
{
    /// <summary>The PUT body ceiling (two bounded blobs plus envelope).</summary>
    public const int MaxBodyBytes = 512 * 1024;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;

    public EditorPreferencesController(InferenceApiDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>The caller's preferences; empty defaults when none were saved yet.</summary>
    [HttpGet]
    public async Task<ActionResult<EditorPreferencesResponse>> Get(CancellationToken ct)
    {
        EditorUserPreference? row = await _db.EditorUserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == _currentUser.UserId, ct);
        return Ok(row is null
            ? new EditorPreferencesResponse(Parse("{}"), Parse("[]"), null)
            : new EditorPreferencesResponse(Parse(row.KeymapJson), Parse(row.LayoutsJson), row.UpdatedAt));
    }

    /// <summary>Replaces both blobs. <c>400</c> with every problem; nothing is written on a refusal.</summary>
    [HttpPut]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult<EditorPreferencesResponse>> Put([FromBody] SaveEditorPreferencesRequest request, CancellationToken ct)
    {
        IReadOnlyList<string> errors = EditorPreferencesValidator.Validate(request.Keymap, request.Layouts);
        if (errors.Count > 0) return BadRequest(new { message = "The preferences are not valid.", errors });

        EditorUserPreference? row = await _db.EditorUserPreferences.FirstOrDefaultAsync(p => p.UserId == _currentUser.UserId, ct);
        if (row is null)
        {
            row = new EditorUserPreference { UserId = _currentUser.UserId };
            _db.EditorUserPreferences.Add(row);
        }

        row.KeymapJson = request.Keymap.GetRawText();
        row.LayoutsJson = request.Layouts.GetRawText();
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new EditorPreferencesResponse(request.Keymap.Clone(), request.Layouts.Clone(), row.UpdatedAt));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
