#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The storage entitlement the one plan catalog sells (docs/design/local-folder-sync-and-storage.md
/// Part 2). The numbers are read through the SAME loader the product seeds the <c>plans</c> table with
/// and the SAME mapper that turns a row into an entitlement set, so a test cannot pass while the
/// product reads something else. The vendored <c>/site</c> copy is checked byte for byte.
/// </summary>
public class PlanCatalogStorageTests
{
    private static IReadOnlyDictionary<string, Plan> ShippedPlans() =>
        PlanCatalogSeeder.Load(File.ReadAllText(PlanCatalogSeeder.DefaultPath)).ToDictionary(p => p.Key);

    [Fact]
    public void Each_plan_grants_the_documented_storage_allowance()
    {
        IReadOnlyDictionary<string, Plan> plans = ShippedPlans();

        PlanEntitlements.ToSet(plans["trial"]).StorageGb.Should().Be(1);
        PlanEntitlements.ToSet(plans["creator"]).StorageGb.Should().Be(15);
        PlanEntitlements.ToSet(plans["pro"]).StorageGb.Should().Be(50);
        PlanEntitlements.ToSet(plans["team"]).StorageGb.Should().Be(100);

        OrganizationEntitlementSet enterprise = PlanEntitlements.ToSet(plans["enterprise"]);
        enterprise.StorageGb.Should().Be(OrganizationEntitlementSet.UnlimitedInt, "Enterprise is unlimited");
        StorageUsageService.LimitBytesOf(enterprise).Should().BeNull("unlimited is a null limit, not a huge number");
        plans["enterprise"].EntitlementsJson.Should().NotContain("storageGb",
            "unlimited is expressed by the ABSENCE of the key, so a future edit cannot mistake a sentinel for a number");

        // The numbers the loader maps are the numbers the quota uses.
        StorageUsageService.LimitBytesOf(PlanEntitlements.ToSet(plans["creator"])).Should().Be(15L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void Billing_summary_plan_cards_carry_storageGb_and_camelCase_entitlements()
    {
        IReadOnlyDictionary<string, Plan> plans = ShippedPlans();
        Dictionary<string, int?> expected = new() { ["trial"] = 1, ["creator"] = 15, ["pro"] = 50, ["team"] = 100, ["enterprise"] = null };
        foreach ((string key, int? gb) in expected)
        {
            (int? storage, object entitlements) = ReelBolt.Inference.Api.Controllers.BillingController.CardEntitlementsOf(plans[key]);
            storage.Should().Be(gb, key);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(entitlements));
            foreach (string name in new[] { "maxOutputHeight", "watermark", "byoAllowed", "roomsAllowed", "videoGenAllowed", "segmentationAllowed", "audioGenAllowed", "audioCleanupAllowed", "maxConcurrentExecutions", "assistantMonthlyCredits", "retentionDays", "runnerAllowed", "priority" })
            {
                doc.RootElement.TryGetProperty(name, out _).Should().BeTrue($"{key} has {name}");
            }
        }
    }

    [Fact]
    public void Billing_summary_plan_cards_state_per_seat_and_unlimited_assistant_allowances_explicitly()
    {
        IReadOnlyDictionary<string, Plan> plans = ShippedPlans();

        (_, object team) = ReelBolt.Inference.Api.Controllers.BillingController.CardEntitlementsOf(plans["team"]);
        using JsonDocument teamDoc = JsonDocument.Parse(JsonSerializer.Serialize(team));
        teamDoc.RootElement.GetProperty("assistantMonthlyCredits").ValueKind.Should().Be(JsonValueKind.Null);
        teamDoc.RootElement.GetProperty("assistantMonthlyCreditsPerSeat").GetInt32().Should().Be(100);
        teamDoc.RootElement.GetProperty("assistantUnlimited").GetBoolean().Should().BeFalse("Team is capped per seat");

        (_, object enterprise) = ReelBolt.Inference.Api.Controllers.BillingController.CardEntitlementsOf(plans["enterprise"]);
        using JsonDocument entDoc = JsonDocument.Parse(JsonSerializer.Serialize(enterprise));
        entDoc.RootElement.GetProperty("assistantUnlimited").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Every_plan_with_a_storage_allowance_advertises_it()
    {
        string json = File.ReadAllText(PlanCatalogSeeder.DefaultPath);
        IReadOnlyDictionary<string, Plan> plans = ShippedPlans();
        using JsonDocument doc = JsonDocument.Parse(json);

        List<JsonElement> priced = doc.RootElement.GetProperty("plans").EnumerateArray()
            .Where(p => p.TryGetProperty("priceMonthly", out _))
            .ToList();
        priced.Should().NotBeEmpty("the catalog sells at least one plan by price");

        foreach (JsonElement plan in doc.RootElement.GetProperty("plans").EnumerateArray())
        {
            string key = plan.GetProperty("id").GetString()!;
            int storageGb = PlanEntitlements.ToSet(plans[key]).StorageGb;
            if (storageGb == OrganizationEntitlementSet.UnlimitedInt)
            {
                continue;
            }

            List<string> highlights = plan.GetProperty("highlights").EnumerateArray().Select(h => h.GetString()!).ToList();
            highlights.Should().Contain(
                h => h.Contains($"{storageGb} GB", StringComparison.Ordinal) && h.Contains("storage", StringComparison.OrdinalIgnoreCase),
                $"{key} must tell the buyer about its {storageGb} GB of storage");
        }

        // Enterprise sells no number, so its copy must not imply one.
        List<string> enterpriseHighlights = doc.RootElement.GetProperty("plans").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "enterprise")
            .GetProperty("highlights").EnumerateArray().Select(h => h.GetString()!).ToList();
        enterpriseHighlights.Should().NotContain(h => h.Contains("GB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_vendored_site_copy_is_byte_identical_to_the_canonical_catalog()
    {
        string? siteCatalog = FindSiteCatalog();

        siteCatalog.Should().NotBeNull("the vendored /site copy is part of this repository");
        File.ReadAllBytes(siteCatalog!).Should().Equal(File.ReadAllBytes(PlanCatalogSeeder.DefaultPath),
            "CI's pricing-catalog drift check runs this diff, so an edit to one copy alone must not reach a green build");
    }

    /// <summary>
    /// Walks up from the test output directory to the repository root: the directory that holds both
    /// <c>inference/ReelBolt.sln</c> and the vendored <c>site</c> catalog.
    /// </summary>
    private static string? FindSiteCatalog()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "site", "lib", "pricing", "plans.catalog.json");
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "inference", "ReelBolt.sln")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
