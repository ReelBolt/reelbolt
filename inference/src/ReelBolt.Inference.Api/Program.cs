using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api;
using ReelBolt.Shared.Auth;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Middleware;
using ReelBolt.Inference.Api.Observability;
using ReelBolt.Inference.Api.Services.Auth;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);

// Refuse to start without a valid session signing key (compose no longer guards the variable).
EngineJwtSettings.RequireSigningKeys(builder.Configuration, engineKeyReplacesSessionKey: false);
builder.Services.AddInferenceApiServices(builder.Configuration);

WebApplication app = builder.Build();

// Drain mode (zero-downtime deploy): counts in-flight requests for the drain status file; no behaviour change.
app.UseMiddleware<ReelBolt.Inference.Api.Middleware.InFlightRequestMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Error tracking (alpha-launch T-018): records an unhandled exception as an error signal and then
// rethrows it, so the caller sees exactly the response it saw before.
app.UseErrorTracking();
app.UseHttpsRedirection();
app.UsePublicApiFlag();
app.UseAuthentication();
// A WorkflowEngine token (EngineJwt:SigningKey set) reaches only the routes the engine calls; a no-op otherwise.
app.UseEngineTokenGuard();
app.UseMcpScopeGuard(McpScopeRoutes.Allowed);
app.UseAuthorization();
// Terminal middleware: reports successful thumbnail-graphic edits to the notification
// pipeline (docs/notifications.md). Registered in the chain so it sees the finished request.
app.UseUserActivityNotifications();
app.MapControllers();
// The public API document is served anonymously in every environment (404 when PublicApi:Enabled is off,
// answered by PublicApiFlagMiddleware before this runs).
app.MapGet("/api/public/v1/openapi.json", async (Swashbuckle.AspNetCore.Swagger.ISwaggerProvider swagger) =>
    Results.Text(await ReelBolt.Inference.Api.Services.PublicApi.PublicApiOpenApi.RenderAsync(swagger), "application/json")).AllowAnonymous().ExcludeFromDescription();


// --- Seed database ---
await DatabaseSeeder.SeedAsync(app.Services);

app.Run();

