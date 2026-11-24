using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>One document as the deployment currently serves it.</summary>
public sealed record LegalDocumentResponse(
    string Document,
    string Version,
    DateTime EffectiveAt,
    DateTime UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// The versions this deployment is serving, in the flat shape every caller actually needs: the web
/// proxy compares one string, and the signup and accept-terms pages display two.
/// </summary>
public sealed record LegalDocumentsResponse(
    string TermsVersion,
    string PrivacyVersion,
    IReadOnlyList<LegalDocumentResponse> Documents);

/// <summary>Publish a new version of one document.</summary>
public sealed record PublishLegalDocumentRequest(string Version);

/// <summary>
/// The versions of the legal documents this deployment serves (F-7).
///
/// <para>
/// <b>Why the versions live here.</b> A recorded acceptance is evidence only if the version it names
/// is one the server actually served. Signup and <c>/auth/accept-terms</c> used to take the version
/// from the request body, so a caller could record any string, including a version that never
/// existed. These rows are the server's own statement of what is current; the Go API reads them (by
/// SQL, on every acceptance) and pins the row it writes to them.
/// </para>
///
/// <para>
/// <b>Authority.</b> <c>GET</c> is anonymous on purpose — it is what a visitor is about to agree to,
/// it is already printed on the signup page, and the proxy gate needs it before any session exists.
/// <c>PUT</c> publishes a new version and is the platform claim only: it changes what every tenant
/// is asked to accept, so no organisation role may do it. As everywhere else in this service the
/// check is the first statement of the action and is never cached in a field.
/// </para>
///
/// <para>
/// <b>Route.</b> <c>/api/v1/legal/</c> is NOT under <c>/api/v1/admin/*</c>, which nginx sends to the
/// Go API, so a publication route there would never reach this service — the same reason the
/// platform switches live at <c>/api/v1/platform/settings</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/legal/documents")]
public class LegalDocumentsController(InferenceApiDbContext db, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>The column width; a longer version cannot be stored, so it is refused as input.</summary>
    private const int MaxVersionLength = 50;

    /// <summary>
    /// The document, its version, and when that version became current.
    ///
    /// <para>
    /// <b>503 when a document has no row.</b> "The server cannot state what it is serving" is not the
    /// same answer as "the server serves nothing", and the two callers must not confuse them: a
    /// missing row means the Go API refuses to record an acceptance rather than inventing a version,
    /// and the web proxy leaves the session alone rather than re-prompting against a guess.
    /// </para>
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<LegalDocumentsResponse>> Get(CancellationToken ct)
    {
        List<LegalDocumentVersion> rows = await db.LegalDocumentVersions.AsNoTracking()
            .OrderBy(d => d.Document)
            .ToListAsync(ct);

        string? terms = rows.FirstOrDefault(r => r.Document == LegalDocument.Terms)?.Version;
        string? privacy = rows.FirstOrDefault(r => r.Document == LegalDocument.Privacy)?.Version;
        if (string.IsNullOrWhiteSpace(terms) || string.IsNullOrWhiteSpace(privacy))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "the legal documents this deployment serves are not configured",
            });
        }

        return new LegalDocumentsResponse(
            terms,
            privacy,
            rows.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Publishes <paramref name="request"/>'s version of <paramref name="document"/>, stamped with
    /// who published it and when — which is the moment acceptances of the previous version stopped
    /// being current.
    ///
    /// <para>
    /// Publishing the version already current writes nothing: a retried or scripted publication must
    /// not move <c>effective_at</c>, because that instant is the boundary the audit trail is read
    /// against.
    /// </para>
    /// </summary>
    [HttpPut("{document}")]
    public async Task<ActionResult<LegalDocumentResponse>> Publish(
        string document, PublishLegalDocumentRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        if (!TryParseDocument(document, out LegalDocument parsed))
            return BadRequest(new { message = "document must be 'Terms' or 'Privacy'." });

        string version = (request?.Version ?? string.Empty).Trim();
        if (version.Length == 0)
            return BadRequest(new { message = "version is required." });
        if (version.Length > MaxVersionLength)
            return BadRequest(new { message = $"version is at most {MaxVersionLength} characters." });

        LegalDocumentVersion? row = await db.LegalDocumentVersions
            .FirstOrDefaultAsync(d => d.Document == parsed, ct);
        if (row is null)
            return NotFound(new { message = "this deployment has no row for that document; it was never seeded." });

        if (!string.Equals(row.Version, version, StringComparison.Ordinal))
        {
            DateTime now = DateTime.UtcNow;
            row.Version = version;
            row.EffectiveAt = now;
            row.UpdatedAt = now;
            row.UpdatedBy = currentUser.Email;
            await db.SaveChangesAsync(ct);
        }

        return ToResponse(row);
    }

    /// <summary>
    /// Accepts the enum's own name, case-insensitively, and refuses anything else rather than
    /// defaulting: a typo must not publish over the wrong document.
    /// </summary>
    private static bool TryParseDocument(string? name, out LegalDocument document)
        => Enum.TryParse(name, ignoreCase: true, out document) && Enum.IsDefined(document);

    private static LegalDocumentResponse ToResponse(LegalDocumentVersion row) =>
        new(row.Document.ToString(), row.Version, row.EffectiveAt, row.UpdatedAt, row.UpdatedBy);
}
