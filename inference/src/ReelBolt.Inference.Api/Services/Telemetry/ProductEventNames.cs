namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// The product-analytics event vocabulary added on top of the alpha funnel (<c>docs/product-events.md</c>).
/// One class, so a name is spelled in exactly one place. Names are bounded to 64 characters by the
/// column, so a new name costs no migration. The detail column, where used, carries a closed token only.
/// </summary>
public static class ProductEventNames
{
    /// <summary>A consented anonymous site visit (visitor-only row, detail = source label).</summary>
    public const string SiteVisit = "site.visit";

    /// <summary>A consented visitor viewed a page of the marketing site (visitor-only row, detail = page key).</summary>
    public const string SitePageView = "site.page_view";

    /// <summary>A consented visitor clicked a call to action on the marketing site (visitor-only row, detail = CTA location).</summary>
    public const string SiteCtaClick = "site.cta_click";

    /// <summary>An anonymous visitor opened the signup form (visitor-only row, detail = source label).</summary>
    public const string SignupStarted = "signup.started";

    /// <summary>
    /// A consented visitor sent the signup form (visitor-only row, no detail). It says the form was sent,
    /// not that an account was created: the signup answer is the same either way.
    /// </summary>
    public const string SignupSubmitted = "signup.submitted";

    /// <summary>A user's first project (server, first occurrence per user).</summary>
    public const string ProjectCreated = "activation.project_created";

    /// <summary>
    /// A user's first file added to a project by their own upload: the form upload, a direct upload's
    /// completion, an import from a link, or the assistant's upload and import tools (server, first
    /// occurrence per user). Never a background sync, an unpacked archive entry or a restored file.
    /// </summary>
    public const string FileUploaded = "activation.file_uploaded";

    /// <summary>
    /// A user's first connected source: a GitHub repository, a cloud drive folder or a computer folder
    /// linked to a project by the person's own action (server, first occurrence per user). It is the other
    /// way a person adds material: the welcome flow sends somebody who starts from code to the repository,
    /// not to the drop zone, so <see cref="FileUploaded"/> alone would read as "added nothing" for them.
    /// Written when the link is created, never by a sync pass, a re-sync, a change of the selection or an
    /// unlink. The acquisition SQL reads the earlier of this and <see cref="FileUploaded"/> as the
    /// first-material step.
    /// </summary>
    public const string SourceConnected = "activation.source_connected";

    /// <summary>A user's first output download (server, first occurrence per user).</summary>
    public const string OutputDownloaded = "activation.output_downloaded";

    /// <summary>A user's first output share link (server, first occurrence per user).</summary>
    public const string OutputShared = "activation.output_shared";

    /// <summary>A user's first checkout (server, first occurrence per user).</summary>
    public const string CheckoutStarted = "billing.checkout_started";

    /// <summary>A user's first credit shortfall (HTTP 402 INSUFFICIENT_CREDITS; server, once per user).</summary>
    public const string CreditsExhausted = "billing.credits_exhausted";

    /// <summary>A plan refused a feature (PLAN_NOT_ENTITLED; server, plain append, detail = the feature token, e.g. resolution).</summary>
    public const string PlanGateHit = "billing.plan_gate_hit";

    /// <summary>A run ended Failed (server, once per execution, detail = the failing step's closed FailureCode token).</summary>
    public const string RunFailed = "activation.run_failed";

    /// <summary>An upgrade prompt was shown (client, detail = a closed surface token; see <see cref="UpgradeSurfaces"/>).</summary>
    public const string UpgradePromptShown = "billing.upgrade_prompt_shown";

    /// <summary>An upgrade prompt was clicked (client, detail = a closed surface token; see <see cref="UpgradeSurfaces"/>).</summary>
    public const string UpgradeClicked = "billing.upgrade_clicked";

    /// <summary>The closed surface tokens the two client upgrade events accept as detail.</summary>
    public static readonly IReadOnlyList<string> UpgradeSurfaces = ["insufficient_credits_alert", "insufficient_credits_toast", "plan_gate", "pricing"];

    /// <summary>The client upgrade events (the only product names the client ingest accepts with a detail).</summary>
    public static readonly IReadOnlyList<string> UpgradeEvents = [UpgradePromptShown, UpgradeClicked];

    /// <summary>Whether <paramref name="name"/> is a client upgrade event.</summary>
    public static bool IsUpgradeEvent(string? name) => name is not null && UpgradeEvents.Contains(name);

    /// <summary>Whether <paramref name="surface"/> is one of the closed upgrade surface tokens.</summary>
    public static bool IsUpgradeSurface(string? surface) => surface is not null && UpgradeSurfaces.Contains(surface);

    /// <summary>
    /// Reduces a feature or failure code to a closed token: 1-64 characters of <c>[A-Za-z0-9_]</c>, else
    /// <c>other</c>. Nothing free-text can reach the detail column through this.
    /// </summary>
    public static string ClosedToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return "other";
        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return "other";
        }

        return value;
    }

    /// <summary>
    /// A recipe was chosen: the template apply routes, and a project created with a starter recipe (server,
    /// plain append, detail = the catalog's template key). The automatic starter is not a choice and writes none.
    /// </summary>
    public const string TemplateChosen = "product.template_chosen";

    /// <summary>A user's first assistant chat turn (server, first occurrence per user).</summary>
    public const string AssistantUsed = "product.assistant_used";

    /// <summary>A user's first MCP tool call (server, first occurrence per user).</summary>
    public const string McpConnected = "product.mcp_connected";

    /// <summary>A change was requested on a composition (server, plain append).</summary>
    public const string ChangeRequested = "product.change_requested";

    /// <summary>The product tour was started (server, once per user).</summary>
    public const string OnboardingTourStarted = "onboarding.tour_started";

    /// <summary>The product tour was completed (server, once per user).</summary>
    public const string OnboardingTourCompleted = "onboarding.tour_completed";

    /// <summary>The product tour was skipped (server, once per user).</summary>
    public const string OnboardingTourSkipped = "onboarding.tour_skipped";

    /// <summary>The getting-started checklist was dismissed (server, once per user).</summary>
    public const string OnboardingChecklistDismissed = "onboarding.checklist_dismissed";

    /// <summary>A checklist item closed (server, once per user per item; detail = the closed item key).</summary>
    public const string OnboardingChecklistItemDone = "onboarding.checklist_item_done";

    /// <summary>
    /// The welcome questions were answered (server, once per user; detail = the persona). Written by
    /// <c>PUT /api/v1/onboarding/profile</c> itself, for every user: no browser consent or ingest switch
    /// is involved, so unlike the tour and checklist rows it is a server fact.
    /// </summary>
    public const string OnboardingProfileSaved = "onboarding.profile_saved";

    /// <summary>The welcome questions were skipped (server, once per user). A server fact, like <see cref="OnboardingProfileSaved"/>.</summary>
    public const string OnboardingProfileSkipped = "onboarding.profile_skipped";

    /// <summary>The five names the anonymous visit endpoint accepts (docs/design/new-user-funnel.md section 4).</summary>
    public static readonly IReadOnlyList<string> VisitEvents =
        [SiteVisit, SitePageView, SiteCtaClick, SignupStarted, SignupSubmitted];

    /// <summary>Whether <paramref name="name"/> is one of the visit-endpoint events.</summary>
    public static bool IsVisitEvent(string? name) => name is not null && VisitEvents.Contains(name);
}
