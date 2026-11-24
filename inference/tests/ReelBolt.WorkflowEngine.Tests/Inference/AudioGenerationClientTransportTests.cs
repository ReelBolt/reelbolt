#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// S-51: the audio factory's transport. A hostile loopback server (never the live network) answers a 3xx that
/// points at an "internal" path; the client must not follow it. An org-owned row must not reach a non-public
/// address at all, whatever its endpoint resolves to at connect time (the write-time public-endpoint gate can
/// be bypassed by DNS rebinding).
/// </summary>
public class AudioGenerationClientTransportTests
{
    private static int FreeLoopbackPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Serves until stopped: every path is recorded; anything but /internal answers a 302 to /internal.</summary>
    private static (HttpListener Listener, ConcurrentQueue<string> Paths, Task Loop) RedirectingServer(int port)
    {
        HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        ConcurrentQueue<string> paths = new();
        Task loop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                string path = context.Request.Url!.AbsolutePath;
                paths.Enqueue(path);
                if (path == "/internal")
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    byte[] body = "{\"status\":\"COMPLETED\",\"secret\":\"INTERNAL\"}"u8.ToArray();
                    await context.Response.OutputStream.WriteAsync(body);
                }
                else
                {
                    context.Response.StatusCode = 302;
                    context.Response.Headers["Location"] = $"http://127.0.0.1:{port}/internal";
                }

                context.Response.Close();
            }
        });
        return (listener, paths, loop);
    }

    private static ResolvedAudioGenerationProvider Row(InferenceProviderKind kind, string endpoint, Guid? org) =>
        new(Guid.NewGuid(), "row", kind, InferenceProviderCapability.MusicGeneration, endpoint, "", "test-key", 10,
            OrganizationId: org, IsPlatformManaged: org is null);

    [Theory]
    [InlineData(InferenceProviderKind.FalAi)]
    // ElevenLabs is dormant (decision 7): the factory refuses to build its client at all.
    public async Task A_platform_row_client_does_not_follow_a_redirect(InferenceProviderKind kind)
    {
        int port = FreeLoopbackPort();
        (HttpListener listener, ConcurrentQueue<string> paths, Task loop) = RedirectingServer(port);
        try
        {
            IAudioGenerationClient client = new AudioGenerationClientFactory().Get(Row(kind, $"http://127.0.0.1:{port}", org: null));

            AudioGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

            paths.Should().NotBeEmpty("the probe reached the row endpoint");
            paths.Should().NotContain("/internal", "a 3xx from the row endpoint must never be followed");
            result.Ok.Should().BeFalse();
            (result.Reason ?? string.Empty).Should().NotContain("INTERNAL");
        }
        finally
        {
            listener.Stop();
            await loop;
        }
    }

    [Theory]
    [InlineData(InferenceProviderKind.FalAi)]
    // ElevenLabs is dormant (decision 7): the factory refuses to build its client at all.
    public async Task An_org_row_client_never_connects_to_a_non_public_address(InferenceProviderKind kind)
    {
        int port = FreeLoopbackPort();
        (HttpListener listener, ConcurrentQueue<string> paths, Task loop) = RedirectingServer(port);
        try
        {
            IAudioGenerationClient client = new AudioGenerationClientFactory().Get(
                Row(kind, $"http://127.0.0.1:{port}", org: Guid.NewGuid()));

            AudioGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

            result.Ok.Should().BeFalse();
            paths.Should().BeEmpty("an org row's connection is checked against the public-only rule at connect time");
        }
        finally
        {
            listener.Stop();
            await loop;
        }
    }
}
