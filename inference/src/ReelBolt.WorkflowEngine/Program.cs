using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data;
using ReelBolt.WorkflowEngine;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Context;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = false;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);

// --migrate-only: apply the migrations and exit, before any engine service is registered, so the control
// host can run it with nothing but a connection string (EngineDatabaseStartup, infra/postgres/engine-role.sql).
if (EngineDatabaseStartup.IsMigrateOnly(args))
{
    using ILoggerFactory migrateLogging = LoggerFactory.Create(logging => logging.AddSimpleConsole(options =>
    {
        options.SingleLine = false;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    }));
    return await EngineDatabaseStartup.RunMigrateOnlyAsync(builder.Configuration, migrateLogging.CreateLogger("WorkflowEngine.Migrate"));
}

// Refuse to start without a signing key: the engine key, or the session key when no engine key is set.
EngineJwtSettings.RequireSigningKeys(builder.Configuration, engineKeyReplacesSessionKey: true);
builder.Services.AddWorkflowEngineServices(builder.Configuration);
// High-confidence camera profiles (an iPhone Apple Log upload) get their converted proxy built with the
// upload's own plan, before the viewer asks for it.
builder.Services.AddMediaDerivativeAutoProfile();

WebApplication app = builder.Build();
ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WorkflowEngine.Startup");
ToolLoopDiagnostics.Configure(app.Services.GetRequiredService<ILoggerFactory>());

startupLogger.LogInformation("Starting WorkflowEngine (Environment={Environment})", app.Environment.EnvironmentName);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
// An MCP OAuth token (scope claim) reaches only the admin decision-calibration read the Inference API forwards.
app.UseMcpScopeGuard(McpScopeRoutes.Allowed);
app.UseAuthorization();
app.MapControllers();

// --- Database: migrate (default), or check that nothing is pending (Database:MigrateAtStartup=false) ---
using (IServiceScope scope = app.Services.CreateScope())
{
    WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
    await EngineDatabaseStartup.PrepareAsync(db, app.Configuration, startupLogger);
}
startupLogger.LogInformation("WorkflowEngine is ready and accepting requests");

app.Run();
return 0;
