using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.FolderSync;

/// <summary>
/// Puts <see cref="DeviceRequestAuthenticator"/> in front of every action of the device controller.
/// A resource filter, so it runs before model binding and can hash the exact body bytes. On success
/// the <see cref="DeviceContext"/> is stored in <c>HttpContext.Items</c> and the action runs inside the
/// device's organization scope (the tenant query filters then see exactly that organization); on any
/// refusal the answer is <c>401 {"error":"device_unauthorized"}</c> with no detail, and a body over
/// 4 MiB is <c>413 body_too_large</c>.
/// </summary>
public sealed class DeviceAuthenticationFilter(DeviceRequestAuthenticator authenticator, ITenantContext? tenant = null) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        DeviceAuthResult result = await authenticator.AuthenticateAsync(context.HttpContext.Request, context.HttpContext.RequestAborted);
        if (result.Device is not { } device)
        {
            context.Result = result.Failure == DeviceAuthFailure.BodyTooLarge
                ? new ObjectResult(new { error = "body_too_large", message = "A request to this endpoint is at most 4 MiB." }) { StatusCode = StatusCodes.Status413PayloadTooLarge }
                : new UnauthorizedObjectResult(new { error = "device_unauthorized" });
            return;
        }

        context.HttpContext.Items[DeviceRequestAuthenticator.ItemKey] = device;
        using (tenant.Organization(device.OrganizationId))
        {
            await next();
        }
    }
}

/// <summary>Reads the authenticated device of the current request.</summary>
public static class DeviceHttpContextExtensions
{
    /// <summary>The device the filter authenticated. Throws when the filter did not run (a wiring mistake).</summary>
    public static DeviceContext GetDevice(this HttpContext context) =>
        context.Items[DeviceRequestAuthenticator.ItemKey] as DeviceContext
        ?? throw new InvalidOperationException("The device authentication filter did not run for this request.");
}
