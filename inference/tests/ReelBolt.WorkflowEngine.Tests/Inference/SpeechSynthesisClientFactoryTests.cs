using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// Mirrors <see cref="TranscriptionClientFactoryTests"/> exactly, for the speech-synthesis-side factory.
/// </summary>
public class SpeechSynthesisClientFactoryTests
{
    [Fact]
    public void Get_rejects_an_AzureOpenAI_provider_with_NotSupportedException()
    {
        // Fish Audio is the only supported speech synthesis provider.
        SpeechSynthesisClientFactory factory = new();
        ResolvedSpeechSynthesisProvider provider = MakeProvider(InferenceProviderKind.AzureOpenAI, apiKey: "some-key");

        Action act = () => factory.Get(provider);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Get_constructs_a_FishAudio_client_without_throwing()
    {
        SpeechSynthesisClientFactory factory = new();
        ResolvedSpeechSynthesisProvider provider = MakeProvider(InferenceProviderKind.FishAudio, apiKey: "fish-api-key");

        Action act = () => factory.Get(provider);

        act.Should().NotThrow();
    }

    [Fact]
    public void Get_returns_a_FishAudioSpeechSynthesisClient_instance()
    {
        SpeechSynthesisClientFactory factory = new();
        ResolvedSpeechSynthesisProvider provider = MakeProvider(InferenceProviderKind.FishAudio, apiKey: "fish-api-key");

        ISpeechSynthesisClient client = factory.Get(provider);

        client.Should().BeOfType<FishAudioSpeechSynthesisClient>();
    }

    [Fact]
    public void Get_returns_the_same_cached_instance_for_identical_configuration()
    {
        SpeechSynthesisClientFactory factory = new();
        ResolvedSpeechSynthesisProvider provider1 = MakeProvider(InferenceProviderKind.FishAudio, apiKey: "key", modelName: "fish-v1");
        ResolvedSpeechSynthesisProvider provider2 = MakeProvider(InferenceProviderKind.FishAudio, apiKey: "key", modelName: "fish-v1");

        ISpeechSynthesisClient client1 = factory.Get(provider1);
        ISpeechSynthesisClient client2 = factory.Get(provider2);

        client1.Should().BeSameAs(client2);
    }

    [Fact]
    public void Get_returns_a_different_instance_when_only_the_model_name_changes()
    {
        SpeechSynthesisClientFactory factory = new();
        ResolvedSpeechSynthesisProvider provider1 = MakeProvider(InferenceProviderKind.FishAudio, apiKey: "key", modelName: "fish-v1");
        ResolvedSpeechSynthesisProvider provider2 = MakeProvider(InferenceProviderKind.FishAudio, apiKey: "key", modelName: "fish-v2");

        ISpeechSynthesisClient client1 = factory.Get(provider1);
        ISpeechSynthesisClient client2 = factory.Get(provider2);

        client1.Should().NotBeSameAs(client2);
        provider1.CacheKey.Should().NotBe(provider2.CacheKey);
    }

    /// <summary>
    /// The endpoint is operator-supplied and validated only once, at write time, so the client the
    /// factory builds must not follow a 3xx out of it: the redirect target is a second, entirely
    /// unvalidated host, and 169.254.169.254 is the canonical thing to point such a redirect at.
    /// </summary>
    /// <remarks>
    /// The factory builds its own transport, so the only way to observe the auto-redirect setting
    /// is to let it talk to a real server. This one answers with the hostile redirect; a client
    /// that follows it either leaves the process or fails on a connect to the metadata address,
    /// neither of which produces the "302" this asserts on.
    /// </remarks>
    [Fact]
    public async Task Get_produces_a_client_that_does_not_follow_a_302_redirect()
    {
        int port = ReserveFreeLoopbackPort();
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        Task server = Task.Run(async () =>
        {
            HttpListenerContext context = await listener.GetContextAsync();
            context.Response.StatusCode = 302;
            context.Response.Headers["Location"] = "http://169.254.169.254/";
            context.Response.Close();
        });

        try
        {
            SpeechSynthesisClientFactory factory = new();
            ResolvedSpeechSynthesisProvider provider = MakeProvider(
                InferenceProviderKind.FishAudio,
                apiKey: "fish-api-key",
                endpoint: $"http://127.0.0.1:{port}",
                timeoutSeconds: 15);

            ISpeechSynthesisClient client = factory.Get(provider);

            Func<Task> act = () => client.SynthesizeAsync(
                new SpeechRequest("Hello world", null, null), CancellationToken.None);

            // An unfollowed redirect surfaces as an ordinary non-success response and is reported
            // verbatim; a followed one never reaches this assertion.
            await act.Should().ThrowAsync<HttpRequestException>()
                .Where(ex => ex.Message.Contains("302"));

            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Binds an ephemeral loopback port and releases it, so HttpListener can be given a port the
    /// OS is not otherwise handing out. The window between release and rebind is a few
    /// microseconds and the port is on the loopback interface only.
    /// </summary>
    private static int ReserveFreeLoopbackPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static ResolvedSpeechSynthesisProvider MakeProvider(
        InferenceProviderKind kind,
        string apiKey,
        string modelName = "fish-speech-1",
        int timeoutSeconds = 300,
        string endpoint = "https://api.fish.audio") => new(
            ProviderId: Guid.NewGuid(),
            Name: "test-speech-synthesis-provider",
            Kind: kind,
            Endpoint: endpoint,
            ModelName: modelName,
            ApiKey: apiKey,
            TimeoutSeconds: timeoutSeconds);
}
