#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Test helpers for the model catalog tree API (docs/design/model-catalog.md §6). Most rule tests need "one provider
/// account serving one model with one capability", which is what <see cref="OneModel"/> builds and what
/// <see cref="Flat"/> reads back in the member names the older flat tests were written against.
/// </summary>
internal static class ProviderTree
{
    /// <summary>A flattened view of one capability with its model and provider account.</summary>
    internal sealed record FlatRow(
        Guid Id, Guid ProviderId, Guid ModelId, string Name, string ProviderName, string Kind, string Capability,
        string Endpoint, string ModelName, bool HasApiKey, string? ApiKeyLastFour, bool IsDefault, bool IsEnabled,
        int? TimeoutSeconds, DateTime? LastTestAt, bool? LastTestOk, string? LastTestError, bool PersonalOAuthMode,
        System.Collections.Generic.List<PriceResponse>? Prices);

    /// <summary>
    /// <c>POST /</c> body for a new account named <paramref name="Name"/> holding one model (display name
    /// <paramref name="Name"/>, code <paramref name="ModelName"/>) serving <paramref name="Capability"/> (Chat when null).
    /// </summary>
    public static CreateProviderRequest OneModel(
        string Name, string Kind, string Endpoint, string ModelName, string? ApiKey, bool IsDefault, bool IsEnabled,
        int? TimeoutSeconds, string? Capability = null, bool PersonalOAuthMode = false) =>
        new(Name, Kind, Endpoint, ApiKey, TimeoutSeconds, true, PersonalOAuthMode,
            [new CreateModelRequest(ModelName, Name, true, [new CreateCapabilityRequest(Capability ?? "Chat", IsDefault, IsEnabled)])]);

    /// <summary>The single capability of a provider that has exactly one model with one capability.</summary>
    public static FlatRow Flat(this ProviderResponse p)
    {
        ModelResponse model = p.Models.Single();
        CapabilityResponse c = model.Capabilities.Single();
        return new FlatRow(c.Id, p.Id, model.Id, model.DisplayName, p.Name, p.Kind, c.Capability, p.Endpoint, model.ModelCode,
            p.HasApiKey, p.ApiKeyLastFour, c.IsDefault, c.IsEnabled, p.TimeoutSeconds, c.LastTestAt, c.LastTestOk,
            c.LastTestError, p.PersonalOAuthMode, c.Prices);
    }

    /// <summary>The value of a 2xx result (Created or Ok).</summary>
    public static T Body<T>(this ActionResult<T> result) => result.Result switch
    {
        CreatedAtActionResult created => (T)created.Value!,
        OkObjectResult ok => (T)ok.Value!,
        _ => throw new InvalidOperationException($"Expected a 2xx result, got {result.Result?.GetType().Name ?? "a value"}.")
    };

    /// <summary>The (provider, model) a capability sits under.</summary>
    public static (Guid ProviderId, Guid ModelId) ParentsOf(this DbContext db, Guid capabilityId)
    {
        var parents = db.Set<ModelCapability>().IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == capabilityId)
            .Select(c => new { c.Model.ProviderId, c.ModelId })
            .Single();
        return (parents.ProviderId, parents.ModelId);
    }

    /// <summary>The saved-capability test route, addressed by the capability id alone.</summary>
    public static Task<ActionResult<TestInferenceProviderResponse>> TestSaved(
        this InferenceProvidersController controller, DbContext db, Guid capabilityId, CancellationToken ct = default)
    {
        (Guid providerId, Guid modelId) = db.ParentsOf(capabilityId);
        return controller.TestCapability(providerId, modelId, capabilityId, ct);
    }

    /// <summary>The org twin of <see cref="TestSaved(InferenceProvidersController, DbContext, Guid, CancellationToken)"/>.</summary>
    public static Task<ActionResult<TestInferenceProviderResponse>> TestSaved(
        this OrgInferenceProvidersController controller, DbContext db, Guid capabilityId, CancellationToken ct = default)
    {
        (Guid providerId, Guid modelId) = db.ParentsOf(capabilityId);
        return controller.TestCapability(providerId, modelId, capabilityId, ct);
    }

    /// <summary><c>POST …/models/{modelId}/capabilities</c> on the model that serves <paramref name="siblingCapabilityId"/>.</summary>
    public static Task<ActionResult<CapabilityResponse>> AddCapabilityBeside(
        this InferenceProvidersController controller, DbContext db, Guid siblingCapabilityId, string capability,
        CancellationToken ct = default)
    {
        (Guid providerId, Guid modelId) = db.ParentsOf(siblingCapabilityId);
        return controller.CreateCapability(providerId, modelId, new CreateCapabilityRequest(capability), ct);
    }

    /// <summary><c>PUT …/capabilities/{capId}</c> addressed by the capability id alone.</summary>
    public static Task<ActionResult<CapabilityResponse>> UpdateCapability(
        this InferenceProvidersController controller, DbContext db, Guid capabilityId, UpdateCapabilityRequest request,
        CancellationToken ct = default)
    {
        (Guid providerId, Guid modelId) = db.ParentsOf(capabilityId);
        return controller.UpdateCapability(providerId, modelId, capabilityId, request, ct);
    }
}
