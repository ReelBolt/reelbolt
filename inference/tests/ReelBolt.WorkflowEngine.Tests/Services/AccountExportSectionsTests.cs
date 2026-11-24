using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Account;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// C9's drift guard for the C0 GDPR data map (docs/design/saas-accounts.md).
///
/// C0 fixes which tables hold personal data and what happens to each one, and says of the export:
/// "A test asserts the exported table list equals this table, so a new personal-data table cannot be
/// added without a decision here." This is that test. It fails when a table is added to the export,
/// removed from it, or moved into the excluded list without the corresponding entry in C0 and in the
/// hard-coded copy below - which is exactly the moment a decision has to be taken deliberately.
/// </summary>
public class AccountExportSectionsTests
{
    /// <summary>
    /// C0's map, transcribed: every table whose Export column is Yes (or "Manifest only" /
    /// "Metadata only"), in the order the map lists them.
    /// </summary>
    private static readonly string[] C0Exported =
    [
        "application_users",
        "legal_acceptances",
        "organization_memberships",
        "organization_invites",
        "organizations",
        "projects",
        "project_files",
        "media_derivatives",
        "workflow_definitions",
        "workflow_steps",
        "workflow_executions",
        "workflow_step_results",
        "review_scores",
        "external_generation_jobs",
        "assistant_threads",
        "assistant_messages",
        "agent_definitions",
        "editing_styles",
        "project_voices",
        // avatars (docs/design/avatars.md): the avatar with its references and vendor copies, and the audit events.
        "project_avatars",
        // workspace avatar library (docs/design/avatar-objects.md): the items of the personal workspace with the facts of
        // their files inside the same document (no media).
        "avatar_library_items",
        "project_avatar_events",
        "project_pronunciation_entries",
        "edit_timelines",
        "edit_timeline_ops",
        "edit_timeline_snapshots",
        "inference_providers",
        "usage_events",
        "credit_accounts",
        "credit_ledger_entries",
        "subscriptions",
        "billing_customers",
        "billing_webhook_events",
        // saas-promotions T-004: the consent row and its append-only events, as one document.
        "marketing_consents",
        // alpha-launch T-019: the in-product bug/feedback sink — the only place user prose is stored.
        "feedback_reports",
        // new-user funnel (docs/design/new-user-funnel.md section 5): the welcome answers the person gave
        // (persona, material) and the campaign their account is attributed to. Both keyed by the user,
        // both closed tokens only, both removed with the account.
        "user_onboarding_profiles",
        "user_attribution"
    ];

    /// <summary>C0's tables whose Export column is No, each with the reason it must stay out.</summary>
    private static readonly string[] C0NotExported =
    [
        "user_tokens",
        "workflow_step_cache_entries",
        "decision_observations",
        "trial_claims",
        // saas-promotions T-004: only a keyed HMAC of an erased address, kept so it is never mailed again.
        "marketing_suppressions",
        // brevo-marketing-campaigns: a protected address of a purged account, awaiting deletion at Brevo.
        "marketing_brevo_erasures",
        // brevo-contact-sync: single-row worker state, no user data.
        "marketing_contact_sync_state",
        // launch-film-platform-gaps S-12: the hourly run limiter's counters, pruned after a day, purged with the account.
        "run_rate_events"
    ];

    [Fact]
    public void Exported_tables_are_exactly_the_C0_list()
    {
        AccountExportSections.Tables.Should().BeEquivalentTo(C0Exported,
            "the export must contain every table C0 marks as exported, and nothing else: a table added "
            + "here without a C0 entry is data leaving the platform under no agreed rule");
    }

    [Fact]
    public void Excluded_tables_are_exactly_the_C0_list_and_carry_a_reason()
    {
        AccountExportSections.DeliberatelyExcluded.Keys.Should().BeEquivalentTo(C0NotExported,
            "a table C0 says NOT to export must be listed as excluded here, so the exclusions are as "
            + "visible as the inclusions");
        AccountExportSections.DeliberatelyExcluded.Values.Should().OnlyContain(r => r.Length > 20,
            "every exclusion has to say why, not just that it is one");
    }

    [Fact]
    public void No_table_is_both_exported_and_excluded()
    {
        AccountExportSections.Tables.Should().NotIntersectWith(AccountExportSections.DeliberatelyExcluded.Keys);
        AccountExportSections.ExportedWithin.Keys.Should().NotIntersectWith(AccountExportSections.DeliberatelyExcluded.Keys);
    }

    [Fact]
    public void A_table_exported_inside_another_section_names_a_real_section()
    {
        AccountExportSections.ExportedWithin.Should().ContainKey("marketing_consent_events")
            .WhoseValue.Should().Be("marketing_consents", "the consent events ship inside marketing-consent.json");
        AccountExportSections.ExportedWithin.Values.Should().OnlyContain(t => AccountExportSections.Tables.Contains(t));
        AccountExportSections.ExportedWithin.Keys.Should().NotIntersectWith(AccountExportSections.Tables);
    }

    [Fact]
    public void Every_section_has_its_own_file_and_a_description()
    {
        AccountExportSections.All.Select(s => s.FileName).Should().OnlyHaveUniqueItems();
        AccountExportSections.Tables.Should().OnlyHaveUniqueItems();
        AccountExportSections.All.Should().OnlyContain(s => s.Description.Length > 20);
        AccountExportSections.All.Should().OnlyContain(s => s.FileName.EndsWith(".json", StringComparison.Ordinal));
    }

    /// <summary>
    /// The half that reads C0 itself: every table the data map mentions in its "Tables holding
    /// personal data" table is either exported or explicitly excluded here. A new table added to the
    /// map (which C0 requires whenever new personal data appears) therefore fails this test until the
    /// export answers for it.
    /// </summary>
    [Fact]
    public void Every_table_in_the_C0_data_map_is_accounted_for()
    {
        // C0 writes some tables in shorthand; the aliases below are the real table names. Kept here,
        // next to the assertion that needs them, so an alias cannot silently widen the coverage.
        Dictionary<string, string> aliases = new(StringComparer.Ordinal)
        {
            ["organization_members"] = "organization_memberships",
            ["credit_ledger"] = "credit_ledger_entries",
            ["webhook_events"] = "billing_webhook_events"
        };

        HashSet<string> accounted =
        [
            .. AccountExportSections.Tables,
            .. AccountExportSections.ExportedWithin.Keys,
            .. AccountExportSections.DeliberatelyExcluded.Keys
        ];
        List<string> missing = [];
        foreach (string table in TablesFromC0DataMap())
        {
            string resolved = aliases.TryGetValue(table, out string? real) ? real : table;
            if (!accounted.Contains(resolved))
                missing.Add(table);
        }

        missing.Should().BeEmpty(
            "C0's data map names these tables but the export neither exports nor explicitly excludes them");
    }

    /// <summary>The table names C0's "Tables holding personal data" table lists in its first column.</summary>
    private static List<string> TablesFromC0DataMap()
    {
        string docPath = Path.Combine(RepositoryRoot(), "docs", "design", "saas-accounts.md");
        File.Exists(docPath).Should().BeTrue($"the C0 data map must exist at {docPath}");

        List<string> tables = [];
        bool inSection = false;
        foreach (string line in File.ReadAllLines(docPath))
        {
            if (line.StartsWith("### Tables holding personal data", StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }

            if (inSection && line.StartsWith("### ", StringComparison.Ordinal))
                break;

            if (!inSection || !line.StartsWith("| ", StringComparison.Ordinal))
                continue;

            // First cell: the table name(s), each in backticks. Header and separator rows have none.
            // A parenthetical aside can itself contain a backticked column name (C0 writes
            // "agent_definitions (custom, owner_id not null)"), so everything from the first bracket
            // on is dropped before the names are read.
            string first = line.Split('|')[1];
            int bracket = first.IndexOf('(');
            if (bracket >= 0)
                first = first[..bracket];
            foreach (string name in first.Split('`').Where((_, i) => i % 2 == 1))
                tables.Add(name.Trim());
        }

        tables.Should().NotBeEmpty("C0's data map must still have a table of personal-data tables");
        return tables;
    }

    /// <summary>The repository root, found by walking up from the test assembly.</summary>
    private static string RepositoryRoot()
    {
        string assemblyDir = Path.GetDirectoryName(typeof(AccountExportSectionsTests).Assembly.Location)!;
        for (string? candidate = assemblyDir; candidate != null; candidate = Path.GetDirectoryName(candidate))
        {
            if (File.Exists(Path.Combine(candidate, "inference", "ReelBolt.sln")))
                return candidate;
        }

        throw new InvalidOperationException($"Could not find the repository root above {assemblyDir}");
    }
}
