using System;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers <see cref="RabbitMqHelper.DeserializeExecutionRequested"/> directly — pure JSON-unwrap
/// logic with no dependency on a live broker.
/// </summary>
public class RabbitMqHelperTests
{
    [Fact]
    public void DeserializeExecutionRequested_unwraps_a_masstransit_envelope()
    {
        // Regression test: a real AMQP body from MassTransit is never the bare DTO — it's wrapped
        // in an envelope with messageId/message/etc. Deserializing the envelope directly as
        // WorkflowExecutionRequested never throws (System.Text.Json leaves unmatched properties at
        // their default), so ExecutionId silently came back Guid.Empty and RemoveExecutionMessageAsync
        // never matched a real execution.
        Guid executionId = Guid.NewGuid();
        string envelope = $$"""
            {
              "messageId": "11111111-1111-1111-1111-111111111111",
              "messageType": ["urn:message:ReelBolt.Shared.IntegrationEvents:WorkflowExecutionRequested"],
              "message": {
                "executionId": "{{executionId}}",
                "workflowDefinitionId": "22222222-2222-2222-2222-222222222222",
                "projectId": "33333333-3333-3333-3333-333333333333",
                "initiatedByUserId": "44444444-4444-4444-4444-444444444444",
                "correlationId": "corr-1",
                "requestedAt": "2026-01-01T00:00:00Z"
              }
            }
            """;

        var result = RabbitMqHelper.DeserializeExecutionRequested(envelope);

        result.Should().NotBeNull();
        result!.ExecutionId.Should().Be(executionId);
    }

    [Fact]
    public void DeserializeExecutionRequested_falls_back_to_the_raw_body_when_unenveloped()
    {
        Guid executionId = Guid.NewGuid();
        string raw = $$"""{"executionId": "{{executionId}}"}""";

        var result = RabbitMqHelper.DeserializeExecutionRequested(raw);

        result.Should().NotBeNull();
        result!.ExecutionId.Should().Be(executionId);
    }

    [Fact]
    public void DeserializeExecutionRequested_throws_on_malformed_json()
    {
        Action act = () => RabbitMqHelper.DeserializeExecutionRequested("not json at all");

        act.Should().Throw<Exception>();
    }
}
