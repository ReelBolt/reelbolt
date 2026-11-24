using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The wire-level proof for Anthropic prompt caching: the markers this repo places end up as
/// <c>cache_control</c> blocks on the serialized request, and a repeated identical prefix comes
/// back as a cache read the SDK surfaces to metering. No live call — a recording
/// <see cref="HttpMessageHandler"/> feeds the SDK a canned Messages response.
/// </summary>
public class AnthropicPromptCachingWireTests
{
    [Fact]
    public async Task Two_identical_calls_send_two_breakpoints_and_the_second_reads_the_cache()
    {
        RecordingHandler handler = new(
            MessageResponse(uncachedInput: 20, cacheCreation: 1200, cacheRead: 0),
            MessageResponse(uncachedInput: 20, cacheCreation: 0, cacheRead: 1200));
        using HttpClient http = new(handler);
        using AnthropicClient sdk = new(new ClientOptions
        {
            ApiKey = "sk-ant-test",
            BaseUrl = "https://api.anthropic.com",
            HttpClient = http,
            MaxRetries = 0,
        });
        using IChatClient client = new AnthropicChatOptionsAdapter(
            sdk.AsIChatClient("claude-sonnet-5", 1024));

        ChatOptions options = new ChatOptions { Instructions = "STABLE" }
            .WithMode(AnthropicPromptCacheMode.SystemAndTurn);
        ChatMessage user = new(ChatRole.User, "hi");

        ChatResponse first = await client.GetResponseAsync([user], options);
        ChatResponse second = await client.GetResponseAsync([user], options);

        handler.Bodies.Should().HaveCount(2);
        string firstBody = handler.Bodies[0];
        string secondBody = handler.Bodies[1];

        // Exactly two breakpoints, at the two stability boundaries: the promoted system block
        // (which caches tools + system) and the last content block of the newest turn.
        CountCacheControl(firstBody).Should().Be(2);
        CountCacheControl(secondBody).Should().Be(2);
        MarkedSystemBlock(firstBody).Should().Be("STABLE");
        MarkedSystemBlock(secondBody).Should().Be("STABLE");
        LastMessageBlockHasBreakpoint(firstBody).Should().BeTrue();
        LastMessageBlockHasBreakpoint(secondBody).Should().BeTrue();

        // Same bytes in, same bytes out: the prefix a cache reads has to be byte-identical.
        secondBody.Should().Be(firstBody);

        // The vendor's usage lines up with what metering sees: the read is a subset of the input.
        first.Usage!.CachedInputTokenCount.Should().Be(0, "the first call creates the entry");
        first.Usage!.InputTokenCount.Should().Be(1220);
        second.Usage!.CachedInputTokenCount.Should().Be(1200);
        second.Usage!.InputTokenCount.Should().Be(1220);
    }

    private static int CountCacheControl(string body) =>
        JsonDocument.Parse(body).RootElement.GetRawText().Split("cache_control").Length - 1;

    private static string? MarkedSystemBlock(string body)
    {
        JsonElement root = JsonDocument.Parse(body).RootElement;
        if (!root.TryGetProperty("system", out JsonElement system) || system.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement block in system.EnumerateArray())
        {
            if (block.TryGetProperty("cache_control", out _) && block.TryGetProperty("text", out JsonElement text))
            {
                return text.GetString();
            }
        }

        return null;
    }

    private static bool LastMessageBlockHasBreakpoint(string body)
    {
        JsonElement messages = JsonDocument.Parse(body).RootElement.GetProperty("messages");
        JsonElement last = messages[messages.GetArrayLength() - 1];
        JsonElement content = last.GetProperty("content");
        JsonElement lastBlock = content[content.GetArrayLength() - 1];
        return lastBlock.TryGetProperty("cache_control", out _);
    }

    private static string MessageResponse(int uncachedInput, int cacheCreation, int cacheRead) =>
        $$"""
        {
          "id": "msg_01TestCase",
          "type": "message",
          "role": "assistant",
          "model": "claude-sonnet-5",
          "content": [{ "type": "text", "text": "hello" }],
          "stop_reason": "end_turn",
          "stop_sequence": null,
          "usage": {
            "input_tokens": {{uncachedInput}},
            "output_tokens": 1,
            "cache_creation_input_tokens": {{cacheCreation}},
            "cache_read_input_tokens": {{cacheRead}}
          }
        }
        """;

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public RecordingHandler(params string[] responses) => _responses = new(responses);

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            string body = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
