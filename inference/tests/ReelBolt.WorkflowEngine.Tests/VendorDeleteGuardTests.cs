using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Local staging runs on a copy of production data with the real vendor key (tools/local-staging).
/// These pin the two switches that keep it from deleting a live voice at the vendor.
/// </summary>
[Collection("VendorDeleteEnv")]
public sealed class VendorDeleteGuardTests
{
    private const string Flag = "REELBOLT_VENDOR_DELETES_DISABLED";

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task Fish_delete_sends_no_request_when_vendor_deletes_are_disabled()
    {
        string? old = Environment.GetEnvironmentVariable(Flag);
        try
        {
            Environment.SetEnvironmentVariable(Flag, "true");
            var handler = new CountingHandler();
            var client = new FishAudioSpeechSynthesisClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://api.fish.audio") }, null);

            bool deleted = await client.DeleteVoiceAsync("some-real-model", CancellationToken.None);

            deleted.Should().BeFalse();
            handler.Requests.Should().Be(0, "no probe and no DELETE may leave the process");
        }
        finally { Environment.SetEnvironmentVariable(Flag, old); }
    }

    [Fact]
    public async Task Fish_delete_still_calls_the_vendor_when_the_flag_is_unset()
    {
        string? old = Environment.GetEnvironmentVariable(Flag);
        try
        {
            Environment.SetEnvironmentVariable(Flag, null);
            var handler = new CountingHandler();
            var client = new FishAudioSpeechSynthesisClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://api.fish.audio") }, null);

            await client.DeleteVoiceAsync("some-model", CancellationToken.None);

            handler.Requests.Should().BeGreaterThan(0);
        }
        finally { Environment.SetEnvironmentVariable(Flag, old); }
    }

    [Fact]
    public async Task Retention_service_does_not_run_when_the_sweep_is_disabled()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["ProjectVoices:RetentionSweepEnabled"] = "false" })
            .Build();
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        var sweep = new ProjectVoiceRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ProjectVoiceRetentionService>.Instance, config);

        await sweep.StartAsync(CancellationToken.None);
        // Disabled: ExecuteAsync returns at once, so the service is complete without waiting its 2-minute delay.
        await Task.WhenAny(sweep.ExecuteTask ?? Task.CompletedTask, Task.Delay(TimeSpan.FromSeconds(5)));
        (sweep.ExecuteTask?.IsCompleted ?? true).Should().BeTrue();
        await sweep.StopAsync(CancellationToken.None);
    }
}

[CollectionDefinition("VendorDeleteEnv", DisableParallelization = true)]
public sealed class VendorDeleteEnvCollection { }
