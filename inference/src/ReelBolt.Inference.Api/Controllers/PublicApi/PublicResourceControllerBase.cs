using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.PublicApi;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

/// <summary>Shared helpers of the resource controllers (T-006): envelope refusals and ISO UTC timestamps.</summary>
public abstract class PublicResourceControllerBase : PublicApiControllerBase
{
    protected ObjectResult NotFoundError(string message = "The resource was not found.") =>
        PublicError(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, message);

    protected ObjectResult InvalidRequest(string message) =>
        PublicError(StatusCodes.Status400BadRequest, PublicApiErrorCodes.InvalidRequest, message);

    /// <summary>A stored timestamp as an unambiguous UTC instant (it serializes with a trailing Z).</summary>
    protected static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    protected static string Id(Guid id) => id.ToString("D");

    /// <summary>The wire spelling of an enum member: camelCase.</summary>
    protected static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
