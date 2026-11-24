#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.ImportWorker;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// The real worker pipeline (Kestrel, bearer middleware, concurrency gate, routes) on a loopback ephemeral
/// port, built through <see cref="WorkerHost.Build"/> exactly like <c>Program.cs</c> does.
/// </summary>
public sealed class WorkerServer : IAsyncDisposable
{
    public const string Token = "test-worker-token";

    private readonly WebApplication _app;

    private WorkerServer(WebApplication app, HttpClient client, Uri baseAddress)
    {
        _app = app;
        Client = client;
        BaseAddress = baseAddress;
    }

    public HttpClient Client { get; }

    public Uri BaseAddress { get; }

    public static async Task<WorkerServer> StartAsync(WorkerOptions? options = null, IPdfTextRunner? runner = null)
    {
        WebApplication app = WorkerHost.Build(
            new[] { "--urls", "http://127.0.0.1:0" },
            options ?? new WorkerOptions(Token),
            runner);
        await app.StartAsync();

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Uri baseAddress = new(address);
        HttpClient client = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(120) };
        return new WorkerServer(app, client, baseAddress);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
        await _app.StopAsync(stop.Token);
        await _app.DisposeAsync();
    }
}
