#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ReelBolt.Shared.Observability;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// S-07 / S-08: what the OTel pipeline exports never carries a live output-link token (server spans for
/// <c>/api/v1/output-links/*</c>) nor the path or query of a user's import URL (client spans of requests
/// marked <see cref="ReelBoltTelemetry.OriginOnlyUrl"/>). Real instrumentation over loopback only: no
/// live network. The OTLP endpoint is a closed local port; nothing is ever delivered there.
/// </summary>
public sealed class ReelBoltTelemetryRedactionTests
{
    // An obvious placeholder in the token's position; what matters is that it never reaches a span.
    private const string Token = "placeholder-output-link-token";

    private sealed class Capture(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            lock (sink) sink.Add(data);
        }
    }

    private static IConfiguration ExportOn() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1")])
            .Build();

    private static IEnumerable<string> EverythingExported(Activity a) =>
        a.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(a.DisplayName).Append(a.OperationName);

    [Fact]
    public void The_processor_redacts_an_output_link_path_and_leaves_other_paths_alone()
    {
        SensitiveUrlRedactionProcessor processor = new();
        using Activity link = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").SetTag("url.path", $"/api/v1/output-links/{Token}");
        link.DisplayName = $"GET /api/v1/output-links/{Token}";
        using Activity other = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").SetTag("url.path", "/api/v1/projects/7/files");

        processor.OnEnd(link);
        processor.OnEnd(other);

        EverythingExported(link).Should().NotContain(s => s.Contains(Token));
        link.GetTagItem("url.path").Should().Be(SensitiveUrlRedactionProcessor.OutputLinkPathRedacted);
        other.GetTagItem("url.path").Should().Be("/api/v1/projects/7/files");
    }

    [Fact]
    public void The_processor_reduces_a_marked_client_span_to_its_origin()
    {
        SensitiveUrlRedactionProcessor processor = new();
        using Activity span = new Activity("System.Net.Http.HttpRequestOut")
            .SetTag("url.full", "https://cdn.example.com/media/clip.mp4?sig=placeholder")
            .SetTag("url.path", "/media/clip.mp4")
            .SetTag("url.query", "sig=placeholder");
        span.SetCustomProperty(SensitiveUrlRedactionProcessor.OriginOnlyProperty, "https://cdn.example.com");

        processor.OnEnd(span);

        span.GetTagItem("url.full").Should().Be("https://cdn.example.com");
        EverythingExported(span).Should().NotContain(s => s.Contains("/media/") || s.Contains("placeholder"));
    }

    [Fact]
    public async Task An_HttpClient_request_marked_origin_only_exports_only_its_origin_and_an_unmarked_one_is_unchanged()
    {
        List<Activity> exported = new();
        ServiceCollection services = new();
        services.AddReelBoltTelemetry(ExportOn(), "Test.Redaction", _ => null).Should().BeTrue();
        services.ConfigureOpenTelemetryTracerProvider(b => b.AddProcessor(new Capture(exported)));
        await using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<TracerProvider>();

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = ServeAsync(listener, 2);
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });

        using (HttpRequestMessage marked = new(HttpMethod.Get, $"http://127.0.0.1:{port}/private-path/clip.mp4?sig=placeholder"))
        {
            marked.Options.Set(ReelBoltTelemetry.OriginOnlyUrl, "http://127.0.0.1");
            (await client.SendAsync(marked)).Dispose();
        }
        using (HttpRequestMessage plain = new(HttpMethod.Get, $"http://127.0.0.1:{port}/plain-path"))
            (await client.SendAsync(plain)).Dispose();
        await server;

        Activity[] spans;
        // Listeners are process-wide: keep only the spans of requests to THIS listener (parallel tests send others).
        lock (exported) spans = exported.Where(a => a.Kind == ActivityKind.Client && OnPort(a, port)).ToArray();
        spans.Should().HaveCount(2);
        Activity markedSpan = spans.Single(a => !EverythingExported(a).Any(s => s.Contains("plain-path")));
        markedSpan.GetTagItem("url.full").Should().Be("http://127.0.0.1");
        EverythingExported(markedSpan).Should().NotContain(s => s.Contains("private-path") || s.Contains("placeholder"));
        spans.Should().Contain(a => (a.GetTagItem("url.full") as string ?? string.Empty).Contains("/plain-path"));
    }

    [Fact]
    public async Task An_output_link_request_is_exported_without_its_token()
    {
        List<Activity> exported = new();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddReelBoltTelemetry(ExportOn(), "Test.Redaction.Api", _ => null);
        builder.Services.ConfigureOpenTelemetryTracerProvider(b => b.AddProcessor(new Capture(exported)));
        await using WebApplication app = builder.Build();
        app.MapGet("/api/v1/output-links/{token}", (string token) => Results.NotFound());
        await app.StartAsync();
        string address = app.Urls.First();
        int port = new Uri(address).Port;

        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });
        (await client.GetAsync($"{address}/api/v1/output-links/{Token}")).Dispose();
        await app.StopAsync();

        Activity[] server;
        lock (exported) server = exported.Where(a => a.Kind == ActivityKind.Server && OnPort(a, port)).ToArray();
        server.Should().ContainSingle();
        server[0].GetTagItem("url.path").Should().Be(SensitiveUrlRedactionProcessor.OutputLinkPathRedacted);
        EverythingExported(server[0]).Should().NotContain(s => s.Contains(Token));
    }

    private static bool OnPort(Activity a, int port) =>
        a.GetTagItem("server.port") is { } p && Convert.ToInt32(p, System.Globalization.CultureInfo.InvariantCulture) == port;

    private static async Task ServeAsync(TcpListener listener, int requests)
    {
        byte[] answer = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        for (int i = 0; i < requests; i++)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync();
            NetworkStream stream = connection.GetStream();
            byte[] buffer = new byte[4096];
            StringBuilder head = new();
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            await stream.WriteAsync(answer);
        }
    }
}
