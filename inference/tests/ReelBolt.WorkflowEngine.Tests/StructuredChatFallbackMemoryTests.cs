#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="StructuredChatFallback"/> remembers, per provider, which response format the provider
/// accepted after rejecting a richer one, so a provider without JSON-schema output (DeepSeek) pays
/// the rejection round trip once rather than on every vision call. Every test uses its own
/// provider key, so the process-wide memory never leaks between tests.
/// </summary>
public class StructuredChatFallbackMemoryTests
{
    /// <summary>Records the format of every call; rejects any format whose index is in <see cref="Rejected"/>.</summary>
    private sealed class FormatRecordingClient : IChatClient
    {
        public HashSet<int> Rejected { get; } = [];
        public List<int> Calls { get; } = [];
        public Exception? NonFormatFailure { get; set; }

        private static int IndexOf(ChatResponseFormat? format) => format switch
        {
            ChatResponseFormatJson { Schema: not null } => 0,
            ChatResponseFormatJson => 1,
            _ => 2
        };

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int index = IndexOf(options?.ResponseFormat);
            lock (Calls) Calls.Add(index);
            if (NonFormatFailure is not null)
                throw NonFormatFailure;
            if (Rejected.Contains(index))
                throw new InvalidOperationException("HTTP 400 (invalid_request_error) This response_format type is unavailable now");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"objects\": []}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed record Reply(string Value);

    private static Task<ChatResponse> Call(IChatClient client, string? key) =>
        StructuredChatFallback.GetResponseAsync(
            client, [new ChatMessage(ChatRole.User, "find")], ChatResponseFormat.ForJsonSchema<Reply>(), 100,
            NullLogger.Instance, key, CancellationToken.None);

    private static string NewKey() => "test:" + Guid.NewGuid().ToString("N") + "|model";

    [Fact]
    public async Task A_provider_that_rejects_the_schema_format_pays_the_rejection_once()
    {
        var client = new FormatRecordingClient { Rejected = { 0 } };
        string key = NewKey();

        await Call(client, key);
        await Call(client, key);
        await Call(client, key);

        client.Calls.Should().Equal([0, 1, 1, 1], "the second and later calls start at the format the provider accepted");
    }

    [Fact]
    public async Task A_provider_that_accepts_the_schema_format_is_unchanged_and_leaves_no_entry()
    {
        var client = new FormatRecordingClient();
        string key = NewKey();

        await Call(client, key);
        await Call(client, key);

        client.Calls.Should().Equal([0, 0]);
        StructuredChatFallback.RememberedStart(key, 3).Should().Be(0);
    }

    [Fact]
    public async Task A_later_rejection_of_the_remembered_format_falls_back_and_is_remembered()
    {
        var client = new FormatRecordingClient { Rejected = { 0 } };
        string key = NewKey();
        await Call(client, key);                      // learns #1

        client.Rejected.Add(1);                       // the provider stops accepting plain JSON mode too
        ChatResponse response = await Call(client, key);
        await Call(client, key);

        response.Text.Should().Contain("objects");
        client.Calls.Should().Equal([0, 1, 1, 2, 2]);
        StructuredChatFallback.RememberedStart(key, 3).Should().Be(2);
    }

    [Fact]
    public async Task Memory_is_per_provider_and_absent_without_a_key()
    {
        var rejecting = new FormatRecordingClient { Rejected = { 0 } };
        var accepting = new FormatRecordingClient();
        string rejectingKey = NewKey();
        string acceptingKey = NewKey();

        await Call(rejecting, rejectingKey);
        await Call(accepting, acceptingKey);
        await Call(rejecting, null);
        await Call(rejecting, null);

        accepting.Calls.Should().Equal([0], "another provider's downgrade is not this provider's");
        rejecting.Calls.Should().Equal([0, 1, 0, 1, 0, 1], "a keyless call never consults or writes the memory");
    }

    [Fact]
    public async Task An_expired_entry_tries_the_richer_format_again()
    {
        var client = new FormatRecordingClient { Rejected = { 0 } };
        string key = NewKey();
        await Call(client, key);

        StructuredChatFallback.BackdateForTests(key, StructuredChatFallback.MemoryTtl + TimeSpan.FromMinutes(1));
        client.Rejected.Clear();                       // the provider gained schema support meanwhile
        await Call(client, key);
        await Call(client, key);

        client.Calls.Should().Equal([0, 1, 0, 0]);
        StructuredChatFallback.RememberedStart(key, 3).Should().Be(0);
    }

    [Fact]
    public async Task A_non_format_failure_propagates_and_teaches_nothing()
    {
        var client = new FormatRecordingClient { NonFormatFailure = new InvalidOperationException("HTTP 401 unauthorized") };
        string key = NewKey();

        Func<Task> act = () => Call(client, key);

        await act.Should().ThrowAsync<InvalidOperationException>();
        client.Calls.Should().Equal([0]);
        StructuredChatFallback.RememberedStart(key, 3).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_callers_converge_on_the_accepted_format()
    {
        var client = new FormatRecordingClient { Rejected = { 0 } };
        string key = NewKey();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => Call(client, key))));
        int before = client.Calls.Count;
        await Call(client, key);

        StructuredChatFallback.RememberedStart(key, 3).Should().Be(1);
        client.Calls.Skip(before).Should().Equal([1]);
    }

    [Fact]
    public void The_provider_key_is_the_row_id_plus_the_model()
    {
        var id = Guid.NewGuid();
        var a = new ResolvedInferenceProvider(id, "ds", InferenceProviderKind.DeepSeek, "https://api.deepseek.com/", "deepseek-vl", "k1", 30);
        var sameRowNewKey = a with { ApiKey = "k2", TimeoutSeconds = 60 };
        var otherModel = a with { ModelName = "deepseek-chat" };
        var configFallback = a with { ProviderId = null };

        StructuredChatFallback.ProviderKey(a).Should().Be(StructuredChatFallback.ProviderKey(sameRowNewKey));
        StructuredChatFallback.ProviderKey(a).Should().NotBe(StructuredChatFallback.ProviderKey(otherModel));
        StructuredChatFallback.ProviderKey(configFallback).Should().Contain("api.deepseek.com");
        StructuredChatFallback.ProviderKey(a).Should().NotContain("k1");
    }
}
