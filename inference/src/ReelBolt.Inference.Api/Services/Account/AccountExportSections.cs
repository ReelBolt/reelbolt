namespace ReelBolt.Inference.Api.Services.Account;

/// <summary>
/// One section of an account export: one table (or one tightly-coupled pair the C0 data map lists
/// together) written as one JSON file inside the archive, under <c>data/</c>.
/// </summary>
/// <param name="Table">The database table this section comes from, exactly as C0 names it.</param>
/// <param name="FileName">The file name inside the archive's <c>data/</c> folder.</param>
/// <param name="Description">Shown in the archive's manifest, in plain language.</param>
public sealed record AccountExportSection(string Table, string FileName, string Description);

/// <summary>
/// The exported table list, and the reason this file exists at all: C0
/// (<c>docs/design/saas-accounts.md</c>) fixes which tables hold personal data and what happens to
/// each one, and its closing note requires that "a test asserts the exported table list equals this
/// table, so a new personal-data table cannot be added without a decision here". That test is
/// <c>AccountExportSectionsTests</c>: it pins <see cref="All"/> and <see cref="DeliberatelyExcluded"/>
/// against a hard-coded copy of C0's map, so adding a table to the schema and forgetting it here (or
/// exporting a table C0 says to retain) fails the build.
/// <para>
/// <b>Scope of the data.</b> An export covers the account itself and the <b>personal workspace's</b>
/// content: the projects of the caller's Personal organization(s), everything hanging off them, and
/// every row keyed by the user id. Content inside a Team workspace belongs to the team and to its
/// other members, so it is deliberately NOT exported (a team owner can ask us for it); rows that are
/// org-scoped rather than user-scoped (credits, billing) are exported as they are already visible to
/// the caller in the dashboard.
/// </para>
/// </summary>
public static class AccountExportSections
{
    /// <summary>Folder inside the archive that holds one JSON document per section.</summary>
    public const string DataFolder = "data";

    /// <summary>Folder inside the archive that holds the user's original files.</summary>
    public const string FilesFolder = "files";

    /// <summary>The archive's manifest.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Every table C0 marks as exported. Order follows C0's map, so a reader can diff them by eye.
    /// </summary>
    public static readonly IReadOnlyList<AccountExportSection> All =
    [
        new("application_users", "profile.json",
            "Your account: email, display name, when it was created and verified. Never the password hash."),
        new("legal_acceptances", "legal-acceptances.json",
            "The terms and privacy versions you accepted, with the timestamp and IP address we recorded."),
        new("organization_memberships", "memberships.json", "The workspaces you belong to and your role in each."),
        new("organization_invites", "invites.json",
            "Invitations sent to your email address, and the ones you sent to other people."),
        new("organizations", "organizations.json", "The workspaces themselves: name, kind and creation time."),
        new("projects", "projects.json", "Your projects: name, description and status."),
        new("project_files", "project-files.json",
            "File metadata, including what the agents summarised about each file. The files themselves are in the files/ folder."),
        new("media_derivatives", "media-derivatives.json",
            "Proxies, waveforms and thumbnails derived from your media. Regenerable, so listed rather than packed."),
        new("workflow_definitions", "workflow-definitions.json", "Your workflows: name, creation time and the project each belongs to."),
        new("workflow_steps", "workflow-steps.json", "Every step of every workflow, with its configuration (which can contain your prompts)."),
        new("workflow_executions", "workflow-executions.json", "Every run, including the request you typed."),
        new("workflow_step_results", "workflow-step-results.json", "What each step produced: prompts, scripts, transcripts, captions."),
        new("review_scores", "review-scores.json", "Reviewer feedback on your runs."),
        new("external_generation_jobs", "external-generation-jobs.json",
            "Video-generation jobs sent to a vendor: clip id, remote job id, cost and the clip's storage key."),
        new("assistant_threads", "assistant-threads.json", "Your chats with the assistant."),
        new("assistant_messages", "assistant-messages.json", "Every message in those chats."),
        new("agent_definitions", "agent-definitions.json", "The custom agents you created, with their prompts and tool grants."),
        new("editing_styles", "editing-styles.json", "Your saved editing styles."),
        new("project_voices", "project-voices.json",
            "Voices in your projects, including a cloned voice's consent record. The reference recordings are in the files/ folder."),
        new("project_avatars", "project-avatars.json",
            "Avatars in your projects with their consent record, their reference files (which are in the files/ folder) and what happened to any vendor-side copy."),
        new("avatar_library_items", "avatar-library.json",
            "The workspace avatar library of your personal workspace: each item with its consent record and the facts of its files (not the files themselves)."),
        new("project_avatar_events", "project-avatar-events.json",
            "The audit trail of those avatars and of every avatar action you took: what happened and when, as counts and outcome words."),
        new("project_pronunciation_entries", "project-pronunciation-entries.json",
            "Your projects' pronunciation glossaries: the words and how they should be spoken."),
        new("edit_timelines", "edit-timelines.json", "Your editor documents."),
        new("edit_timeline_ops", "edit-timeline-ops.json", "The edit history behind those documents."),
        new("edit_timeline_snapshots", "edit-timeline-snapshots.json", "The periodic snapshots of those documents."),
        new("inference_providers", "inference-providers.json",
            "Your workspace's own AI service rows: name, endpoint and model. The API key is never exported."),
        new("usage_events", "usage-events.json", "What you used, when, and what it cost, per run and per step."),
        new("credit_accounts", "credit-accounts.json", "The credit balance of your workspaces."),
        new("credit_ledger_entries", "credit-ledger.json", "Every credit grant, hold, charge and refund on those balances."),
        new("subscriptions", "subscriptions.json", "Your plan: status, seats, period and when it renews or ends."),
        new("billing_customers", "billing-customers.json", "The billing-provider customer record for your workspaces."),
        new("billing_webhook_events", "billing-webhook-events.json",
            "The billing events we received, without the provider's raw payload: id, type, timestamps and outcome."),
        new("marketing_consents", "marketing-consent.json",
            "Your marketing-email choice: status, the wording you agreed to, when and from where, and every change since (the consent events)."),
        // T-019: the in-product feedback channel. It is the only place user prose is stored, so the
        // user gets it back (GDPR 15/20) and the account purge deletes it.
        new("feedback_reports", "feedback-reports.json",
            "The bug reports and feedback you sent from inside the product, with the context you chose to attach."),
        // New-user funnel (docs/design/new-user-funnel.md section 5): the two welcome answers are things
        // the person told us about themselves, so they get them back (GDPR 15/20). Removed with the account.
        new("user_onboarding_profiles", "onboarding-profile.json",
            "What you told us when you started: who you are and what you start from, the audience page you signed up from, and when you answered or skipped."),
        // The campaign a person's account is attributed to is recorded about them (from the consented
        // first-touch cookie, or from the parameters of their own signup link), so it is theirs to see.
        new("user_attribution", "signup-attribution.json",
            "How you first reached ReelBolt, as recorded on your account: the source, the campaign values of the link you followed, the page you landed on, and how we learned it.")
    ];

    /// <summary>Every table this export covers.</summary>
    public static IReadOnlyList<string> Tables => All.Select(s => s.Table).ToArray();

    /// <summary>
    /// Tables C0 lists that are exported, but inside another table's section rather than in a file of
    /// their own: table → the section that carries its rows.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExportedWithin = new Dictionary<string, string>
    {
        ["marketing_consent_events"] = "marketing_consents",
        ["project_avatar_references"] = "project_avatars",
        ["project_avatar_vendor_assets"] = "project_avatars",
        ["avatar_library_references"] = "avatar_library_items"
    };

    /// <summary>
    /// Tables C0 lists that are deliberately NOT exported, with the reason. Pinned by the same test:
    /// a table that moves between the two lists is a decision, not an accident.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DeliberatelyExcluded = new Dictionary<string, string>
    {
        ["user_tokens"] = "Security artefacts: SHA-256 hashes of verification and reset tokens, no user content.",
        ["workflow_step_cache_entries"] = "Derived cache of step outputs; the outputs themselves are exported.",
        ["decision_observations"] = "Decision-model telemetry with no user column; reachable only through an execution.",
        ["trial_claims"] = "Only a SHA-256 of a normalised address, kept for fraud prevention (C0: retained).",
        ["marketing_suppressions"] =
            "Only a keyed HMAC of an erased address, kept so a deleted account is never mailed again; not linkable to you.",
        ["marketing_brevo_erasures"] =
            "Outbox of a purged account's address (stored protected) until Brevo confirms deletion; the account no longer exists, so there is nothing to export.",
        ["marketing_contact_sync_state"] =
            "Single-row state of the Brevo contact sync worker (last run, last error code); no user data.",
        // S-12: user-keyed, but only limiter bookkeeping: the runs it counts are exported as workflow_executions.
        ["run_rate_events"] =
            "The hourly run limiter's counters (which bucket, when, which run): pruned after a day and deleted with the "
            + "account; the runs they count are exported in workflow-executions."
    };

    /// <summary>The file name for a table, or null when the table is not part of the export.</summary>
    public static string? FileNameFor(string table) =>
        All.FirstOrDefault(s => string.Equals(s.Table, table, StringComparison.Ordinal))?.FileName;

    /// <summary>The section for a table.</summary>
    public static AccountExportSection? For(string table) =>
        All.FirstOrDefault(s => string.Equals(s.Table, table, StringComparison.Ordinal));
}
