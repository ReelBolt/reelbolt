using System.Text.Json;

namespace ReelBolt.ImportWorker;

/// <summary>
/// Builds the worker's web application (Kestrel limits, the bearer gate, the concurrency gate and the three
/// routes). It lives outside <c>Program.cs</c> so the real pipeline, middleware included, can be started on a
/// loopback port by tests without a second copy of it.
/// </summary>
public static class WorkerHost
{
    public static WebApplication Build(string[] args, WorkerOptions options, IPdfTextRunner? runner = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // The cap applies at the server as well as in each handler, so a body over it is refused early. The server-wide
        // cap is the larger of the two profiles (unpack-zip takes 64 MiB); extract-zip and pdf-text keep their own check.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = Math.Max(options.MaxBodyBytes, ReelBolt.Shared.Archives.UnpackLimits.MaxArchiveBytes));

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            // Web defaults: camelCase, case-insensitive reads.
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new WorkerGate(options.MaxConcurrency));
        builder.Services.AddSingleton<IPdfTextRunner>(runner ?? new PopplerPdfTextRunner());

        WebApplication app = builder.Build();

        // /health is the only public route. Everything else needs a valid bearer; a missing or wrong one is
        // a bare 401 with no body.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/health")
            {
                await next();
                return;
            }

            if (!AuthGate.IsAuthorized(context.Request.Headers.Authorization.ToString(), options.Token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });

        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        app.MapPost("/v1/extract-zip", async (HttpContext context) =>
        {
            using WorkerGate.Lease? lease = context.RequestServices.GetRequiredService<WorkerGate>().TryEnter();
            if (lease is null)
            {
                await WriteAsync(context, WorkerGate.Busy());
                return;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            WorkerResult result = await ExtractZipHandler.HandleAsync(
                context.Request.Body,
                context.Request.ContentLength,
                options,
                cts.Token);

            await WriteAsync(context, result);
        });

        app.MapPost("/v1/unpack-zip", async (HttpContext context) =>
        {
            using WorkerGate.Lease? lease = context.RequestServices.GetRequiredService<WorkerGate>().TryEnter();
            if (lease is null)
            {
                await WriteAsync(context, WorkerGate.Busy());
                return;
            }

            // Per-endpoint body cap, set before the body is touched.
            Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature? bodyLimit =
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false })
                bodyLimit.MaxRequestBodySize = ReelBolt.Shared.Archives.UnpackLimits.MaxArchiveBytes;

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            UnpackOutcome outcome = await UnpackZipHandler.HandleAsync(context.Request.Body, context.Request.ContentLength, cts.Token);
            if (outcome.Response is null)
            {
                context.Response.StatusCode = outcome.Status;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = ReelBolt.Shared.Archives.UnpackLimits.ContentType;
            context.Response.ContentLength = outcome.Response.ContentLength;
            // The answer is already built in memory; the caller may read it slowly while it stores each file and scans it,
            // so the write gets its own budget instead of what is left of the 120 s processing budget.
            using CancellationTokenSource writeCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            writeCts.CancelAfter(TimeSpan.FromMinutes(15));
            await outcome.Response.WriteToAsync(context.Response.Body, writeCts.Token);
        });

        app.MapPost("/v1/pdf-text", async (HttpContext context) =>
        {
            using WorkerGate.Lease? lease = context.RequestServices.GetRequiredService<WorkerGate>().TryEnter();
            if (lease is null)
            {
                await WriteAsync(context, WorkerGate.Busy());
                return;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            IPdfTextRunner pdfRunner = context.RequestServices.GetRequiredService<IPdfTextRunner>();
            WorkerResult result = await PdfTextHandler.HandleAsync(
                context.Request.Body,
                context.Request.ContentLength,
                options,
                pdfRunner,
                cts.Token);

            await WriteAsync(context, result);
        });

        return app;
    }

    private static async Task WriteAsync(HttpContext context, WorkerResult result)
    {
        context.Response.StatusCode = result.Status;
        if (result.Status == StatusCodes.Status503ServiceUnavailable)
            context.Response.Headers.RetryAfter = WorkerGate.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (result.Body is not null)
            await context.Response.WriteAsJsonAsync(result.Body);
    }
}
