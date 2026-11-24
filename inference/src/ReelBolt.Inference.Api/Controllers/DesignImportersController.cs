using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>The capability matrix as the route publishes it, with enum names as strings.</summary>
public sealed record ImportCapabilitiesResponse(
    string Schema, IReadOnlyList<ImporterCapabilityResponse> Importers);

/// <summary>One importer's row in the published matrix.</summary>
public sealed record ImporterCapabilityResponse(
    string Id, string Label, IReadOnlyList<ImportAxisCapabilityResponse> Axes);

/// <summary>One axis cell in the published matrix.</summary>
public sealed record ImportAxisCapabilityResponse(string Axis, string Support, string Note);

/// <summary>
/// T-008: publishes the static importer capability matrix at
/// <c>GET /api/v1/design-system/importers</c>. The table is project-free static data, so the
/// route takes no project id and answers the same payload for every authenticated caller.
/// </summary>
[ApiController]
[Route("api/v1/design-system")]
[Authorize]
public sealed class DesignImportersController : ControllerBase
{
    /// <summary>Returns the whole matrix: <c>{ schema, importers: [{ id, label, axes }] }</c>.</summary>
    [HttpGet("importers")]
    public Task<ActionResult<ImportCapabilitiesResponse>> List(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ActionResult<ImportCapabilitiesResponse> result = Ok(new ImportCapabilitiesResponse(
            DesignImportCapabilities.Schema,
            DesignImportCapabilities.Matrix.Select(ToResponse).ToList()));

        return Task.FromResult(result);
    }

    private static ImporterCapabilityResponse ToResponse(DesignImporterCapabilities importer) =>
        new(
            importer.ImporterId,
            importer.Label,
            importer.Axes
                .Select(axis => new ImportAxisCapabilityResponse(
                    axis.Axis.ToString(), axis.Support.ToString(), axis.Note))
                .ToList());
}
