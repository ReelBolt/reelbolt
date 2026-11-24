using ReelBolt.ImportWorker;

// Refuse to start without the one secret. This throws before the host is built, so the process
// exits non-zero and can never serve an unauthenticated extraction endpoint.
string token = WorkerStartup.RequireToken(Environment.GetEnvironmentVariable("IMPORT_WORKER_TOKEN"));
WorkerOptions options = WorkerStartup.OptionsFromEnvironment(token, Environment.GetEnvironmentVariable);

WebApplication app = WorkerHost.Build(args, options);
app.Run();
