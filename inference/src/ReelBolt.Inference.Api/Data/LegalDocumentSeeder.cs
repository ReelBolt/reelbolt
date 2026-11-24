using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Data;

/// <summary>
/// First-boot seed for <c>legal_documents</c> (F-7): the version of each legal document this
/// deployment serves.
///
/// <para>
/// <b>Seeded only while a document has no row, never overwritten.</b> The row is the server's single
/// source of truth for "what version did this user accept", so a restart must not silently roll a
/// published version back — the same rule the rate cards and the built-in agents follow. Publishing a
/// NEW version is an operator act through <c>PUT /api/v1/legal/documents/{document}</c>, which stamps
/// <c>updated_at</c>/<c>updated_by</c>; configuration only decides what a deployment starts with.
/// </para>
///
/// <para>
/// The Terms default is <c>2026-10-22</c>: the CSAM hash-matching control added a content-safety
/// section to the Terms. The <c>AddCsamScanning</c> migration bumps an existing install's Terms row
/// only while it still holds the previous <c>2026-10</c> default, so an operator-published version
/// is never touched.
/// </para>
///
/// <para>
/// Both defaults moved to <c>2026-10-26</c> with the launch-alpha legal rewrite (named contract
/// party, 14-day refund promise, forum clause); the <c>LaunchAlphaLegalVersions</c> migration moves
/// an existing install's rows only while they still hold the previous shipped defaults.
/// </para>
///
/// <para>
/// Both defaults moved to <c>2026-11-05</c> for avatars: Terms 9.1 became a consent-based likeness
/// rule (a re-acceptance) and the Privacy Policy describes avatar data. The <c>AvatarsLegalVersions</c>
/// migration moves an existing install's rows only while they still hold the previously shipped
/// defaults; it ships in the release that turns the <c>avatars</c> plan feature on.
/// </para>
///
/// <para>
/// Both defaults moved to <c>2026-11-17</c> for avatars of objects, pictures with avatars and the workspace
/// avatar library (Terms 9.1 and 11, Privacy providers and avatar paragraph; published on 2026-11-17 by the
/// owner's decision). A Terms bump asks every user to re-accept. The <c>AvatarLibraryLegalVersions</c>
/// migration moves an existing install's rows only while they still hold the previously shipped defaults.
/// </para>
///
/// <para>
/// Both defaults moved to <c>2026-11-10</c> for the recipe marketplace (Community recipes text in the
/// Terms, a re-acceptance); the <c>MarketplaceLegalVersions</c> migration moves existing rows the same way.
/// Both defaults have moved on since.
/// </para>
///
/// <para>
/// The Privacy default moved to <c>2026-11-11</c> for the new-user funnel: the policy now describes the
/// tracked-link click counts, the campaign labels on visit records and in the sign-up link, the welcome
/// questions and the first-step records. The <c>AcquisitionPrivacyVersion</c> migration moves an existing
/// install's row only while it still holds the previously shipped default. Privacy only: the Terms stay
/// at <c>2026-11-10</c>, so this is an acknowledgement and nobody is asked to re-accept the Terms.
/// </para>
///
/// <para>
/// The Privacy default moved to <c>2026-10-31</c>: the policy now states that uploads and created
/// videos are automatically safety-screened. The <c>PrivacySafetyScreeningVersion</c> migration moves an
/// existing install's row only while it still holds the previously shipped default. Privacy only, so
/// nobody is asked to re-accept the Terms.
/// </para>
///
/// <para>
/// The Privacy default was <c>2026-10-12</c> (saas-promotions: the policy now covers marketing
/// consent). The migration <c>AddPromotionsAndMarketingConsent</c> bumps an existing install's row
/// only while it still holds the shipped <c>2026-10</c> default; an operator-set version is never touched.
/// </para>
/// </summary>
public static class LegalDocumentSeeder
{
    /// <summary>Configuration key for the default Terms version.</summary>
    public const string TermsVersionKey = "Legal:TermsVersion";

    /// <summary>Configuration key for the default Privacy version.</summary>
    public const string PrivacyVersionKey = "Legal:PrivacyVersion";

    /// <summary>The Terms version a deployment that configures nothing starts with.</summary>
    public const string DefaultTermsVersion = "2026-11-17";

    /// <summary>The Privacy version a deployment that configures nothing starts with.</summary>
    public const string DefaultPrivacyVersion = "2026-11-17";

    /// <summary>Recorded in <see cref="LegalDocumentVersion.UpdatedBy"/> for a seeded row.</summary>
    public const string SeedActor = "config";

    /// <summary>Inserts a row for every document that has none, and touches nothing that exists.</summary>
    public static async Task SeedAsync(InferenceApiDbContext db, IConfiguration configuration, ILogger logger)
    {
        string terms = Resolve(configuration[TermsVersionKey], DefaultTermsVersion);
        string privacy = Resolve(configuration[PrivacyVersionKey], DefaultPrivacyVersion);

        foreach (LegalDocument document in new[] { LegalDocument.Terms, LegalDocument.Privacy })
        {
            if (await db.LegalDocumentVersions.AnyAsync(d => d.Document == document))
                continue;

            string version = document == LegalDocument.Terms ? terms : privacy;
            DateTime now = DateTime.UtcNow;
            db.LegalDocumentVersions.Add(new LegalDocumentVersion
            {
                Document = document,
                Version = version,
                EffectiveAt = now,
                UpdatedAt = now,
                UpdatedBy = SeedActor,
            });
            logger.LogInformation(
                "Seeded the current {Document} version as {Version} (from {Key}).",
                document, version, document == LegalDocument.Terms ? TermsVersionKey : PrivacyVersionKey);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Reads a configured version, falling back to <paramref name="fallback"/> when it is absent or
    /// blank. Anything else is used verbatim: an operator who sets an over-long version gets the
    /// column's own refusal at insert rather than a silently substituted, wrong version.
    /// </summary>
    private static string Resolve(string? configured, string fallback)
    {
        string value = (configured ?? string.Empty).Trim();
        return value.Length == 0 ? fallback : value;
    }
}
