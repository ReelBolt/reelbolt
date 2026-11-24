#nullable enable
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Test seeding and reading of the model catalog in the shape the suite was written against: one flat
/// "provider row" (a <see cref="ModelCapabilityView"/>, which carries the old entity's member names) becomes
/// one provider account + one model + one capability, the capability keeping <see cref="ModelCapabilityView.Id"/>
/// — exactly what the <c>AddModelCatalog</c> migration does to a pre-catalog row. Works on either DbContext.
/// </summary>
internal static class CatalogTestRows
{
    /// <summary>
    /// Adds (does not save) one provider account + model + capability per row. The account id is
    /// <see cref="ModelCapabilityView.ProviderId"/> when set (so several rows can share one account), else new;
    /// the model id is <see cref="ModelCapabilityView.ModelId"/> when set, else new.
    /// </summary>
    public static ModelCapability AddFlat(this DbContext db, ModelCapabilityView row)
    {
        DateTime now = DateTime.UtcNow;
        DateTime created = row.CreatedAt == default ? now : row.CreatedAt;
        DateTime updated = row.UpdatedAt == default ? now : row.UpdatedAt;

        Guid providerId = row.ProviderId == Guid.Empty ? Guid.NewGuid() : row.ProviderId;
        InferenceProvider? account = db.ChangeTracker.Entries<InferenceProvider>()
            .Select(e => e.Entity).FirstOrDefault(p => p.Id == providerId);
        if (account is null)
        {
            account = new InferenceProvider
            {
                Id = providerId,
                OrganizationId = row.OrganizationId,
                Name = string.IsNullOrEmpty(row.ProviderName) ? row.Name : row.ProviderName,
                Kind = row.Kind,
                Endpoint = row.Endpoint,
                ApiKeyEncrypted = row.ApiKeyEncrypted,
                ApiKeyLastFour = row.ApiKeyLastFour,
                IsEnabled = true,
                IsSystem = row.IsSystem,
                PersonalOAuthMode = row.PersonalOAuthMode,
                TimeoutSeconds = row.TimeoutSeconds,
                ExtraHeadersJson = row.ExtraHeadersJson,
                CreatedAt = created,
                UpdatedAt = updated
            };
            db.Add(account);
        }

        InferenceModel model = new()
        {
            Id = row.ModelId == Guid.Empty ? Guid.NewGuid() : row.ModelId,
            ProviderId = account.Id,
            OrganizationId = row.OrganizationId,
            ModelCode = row.ModelName,
            DisplayName = row.Name,
            IsEnabled = true,
            CreatedAt = created,
            UpdatedAt = updated,
            Provider = account
        };
        db.Add(model);

        ModelCapability capability = new()
        {
            Id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id,
            ModelId = model.Id,
            OrganizationId = row.OrganizationId,
            Capability = row.Capability,
            IsDefault = row.IsDefault,
            IsEnabled = row.IsEnabled,
            PriceSource = row.PriceSource,
            LastTestAt = row.LastTestAt,
            LastTestOk = row.LastTestOk,
            LastTestError = row.LastTestError,
            CreatedAt = created,
            UpdatedAt = updated,
            Model = model
        };
        db.Add(capability);
        return capability;
    }

    /// <summary>Adds (does not save) every row; see <see cref="AddFlat(DbContext, ModelCapabilityView)"/>.</summary>
    public static void AddFlat(this DbContext db, params ModelCapabilityView[] rows)
    {
        foreach (ModelCapabilityView row in rows)
        {
            db.AddFlat(row);
        }
    }

    /// <summary>Every capability flattened, unfiltered and untracked: what the old <c>InferenceProviders</c> set read.</summary>
    public static IQueryable<ModelCapabilityView> FlatProviders(this DbContext db) =>
        db.Set<ModelCapability>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(ModelCapabilityView.Projection);

    /// <summary>The tracked capability <paramref name="id"/> with its model and provider account loaded (for in-test edits).</summary>
    public static ModelCapability TrackedCapability(this DbContext db, Guid id) =>
        db.Set<ModelCapability>()
            .IgnoreQueryFilters()
            .Include(c => c.Model)
            .ThenInclude(m => m.Provider)
            .Single(c => c.Id == id);
}
