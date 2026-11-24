using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The desktop apps of the caller's workspace that offer a local model, so the provider form can offer them as the
/// endpoint of a <c>LocalRunner</c> provider. Org Owner/Admin only (the same authority as
/// <c>/api/v1/org/inference-providers</c>): another organization's devices are never listed, a removed device is not
/// listed, and only the fields below ever leave this endpoint (never the public key, address or the rest of the
/// capability document).
/// </summary>
[ApiController]
[Route("api/v1/org/runner-llm")]
[Authorize]
public sealed class OrgRunnerLlmController : ControllerBase
{
    /// <summary>A device seen within this window counts as online.</summary>
    internal static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    private const int MaxModels = 100;
    private const int MaxModelNameChars = 200;
    private const int MaxServerKindChars = 50;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _time;

    public OrgRunnerLlmController(InferenceApiDbContext db, ICurrentUser currentUser, TimeProvider? time = null)
    {
        _db = db;
        _currentUser = currentUser;
        _time = time ?? TimeProvider.System;
    }

    [HttpGet]
    public async Task<ActionResult<RunnerLlmResponse>> List(CancellationToken ct)
    {
        if (OrgAdminGate.Check(this, _currentUser, out ProviderScope scope) is { } denied) return denied;
        Guid orgId = scope.OrganizationId!.Value;

        var rows = await _db.RunnerDevices.AsNoTracking()
            .Where(d => d.OrganizationId == orgId && d.Kind == RunnerKind.Desktop && d.RevokedAt == null && d.CapabilitiesJson != null)
            .OrderBy(d => d.Name).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Name, d.LastSeenAt, d.CapabilitiesJson })
            .ToListAsync(ct);

        DateTime now = _time.GetUtcNow().UtcDateTime;
        List<RunnerLlmDevice> devices = [];
        foreach (var row in rows)
        {
            if (!TryReadLocalLlm(row.CapabilitiesJson, out bool enabled, out string serverKind, out List<string> models)) continue;
            bool online = row.LastSeenAt is { } seen && now - DateTime.SpecifyKind(seen, DateTimeKind.Utc) <= OnlineWindow;
            devices.Add(new RunnerLlmDevice(row.Id, row.Name, online, enabled, serverKind, models));
        }

        return Ok(new RunnerLlmResponse(devices));
    }

    /// <summary>
    /// Reads <c>localLlm</c> out of a runner's capability document, defensively (the runner reports it about itself and
    /// it is untrusted). False when the document does not advertise one.
    /// </summary>
    internal static bool TryReadLocalLlm(string? capabilitiesJson, out bool enabled, out string serverKind, out List<string> models)
    {
        enabled = false;
        serverKind = string.Empty;
        models = [];
        if (string.IsNullOrWhiteSpace(capabilitiesJson)) return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(capabilitiesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            JsonElement local = default;
            bool found = false;
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "localLlm", StringComparison.OrdinalIgnoreCase)) continue;
                local = property.Value;
                found = true;
                break;
            }

            if (!found || local.ValueKind != JsonValueKind.Object) return false;

            foreach (JsonProperty property in local.EnumerateObject())
            {
                if (string.Equals(property.Name, "enabled", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = property.Value.ValueKind == JsonValueKind.True;
                }
                else if (string.Equals(property.Name, "serverKind", StringComparison.OrdinalIgnoreCase)
                         && property.Value.ValueKind == JsonValueKind.String)
                {
                    serverKind = Clip(property.Value.GetString(), MaxServerKindChars);
                }
                else if (string.Equals(property.Name, "models", StringComparison.OrdinalIgnoreCase)
                         && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement model in property.Value.EnumerateArray())
                    {
                        if (models.Count >= MaxModels) break;
                        if (model.ValueKind != JsonValueKind.String) continue;
                        string name = Clip(model.GetString(), MaxModelNameChars);
                        if (name.Length > 0) models.Add(name);
                    }
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Clip(string? value, int max)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary><c>{devices:[...]}</c>.</summary>
public sealed record RunnerLlmResponse(IReadOnlyList<RunnerLlmDevice> Devices);

/// <summary>One desktop app offering a local model. <c>DeviceId</c> is what a LocalRunner provider stores as its endpoint.</summary>
public sealed record RunnerLlmDevice(Guid DeviceId, string Name, bool Online, bool Enabled, string ServerKind, IReadOnlyList<string> Models);
