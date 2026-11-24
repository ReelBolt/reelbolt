using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.WorkflowEngine.Services.Messaging;

/// <summary>
/// Helper methods for interacting with RabbitMQ directly when MassTransit
/// doesn't expose the necessary control. Used primarily to remove a pending
/// workflow execution message from the queue when the corresponding execution
/// has been cancelled by the user.
/// </summary>
public class RabbitMqHelper
{
    private readonly IConfiguration _configuration;

    public RabbitMqHelper(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Attempts to remove a message from the "workflow-execution" queue whose
    /// body contains the given execution id. This scans the queue using
    /// <c>BasicGet</c>, re-queuing messages that don't match so the original
    /// ordering is preserved. The scan stops when the queue is empty or the
    /// target message is found.
    /// </summary>
    /// <param name="executionId">ID of the execution to remove.</param>
    /// <returns>True if a matching message was found and removed, false
    /// otherwise.</returns>
    public virtual async Task<bool> RemoveExecutionMessageAsync(Guid executionId)
    {
        // RabbitMQ.Client is synchronous; wrap in Task.Run so callers can await
        return await Task.Run(async () =>
        {
            RabbitMQ.Client.ConnectionFactory factory = new RabbitMQ.Client.ConnectionFactory()
            {
                HostName = _configuration["RabbitMQ:Host"] ?? "localhost",
                UserName = _configuration["RabbitMQ:Username"] ?? "guest",
                Password = _configuration["RabbitMQ:Password"] ?? "guest",
            };

            // use the async variant to avoid blocking threadpool threads
            await using RabbitMQ.Client.IConnection connection = await factory.CreateConnectionAsync();
            // newer client exposes async channel creation; the returned IAsyncModel
            // still implements IModel so we can call BasicGet synchronously.
            using var channel = await connection.CreateChannelAsync();
            const string queueName = "workflow-execution";

            // Get message count at start to bound the scan and prevent infinite loops
            var queueDeclare = await channel.QueueDeclarePassiveAsync(queueName);
            uint messageCount = queueDeclare.MessageCount;
            uint maxIterations = Math.Min(messageCount, 10000);

            // Collect delivery tags of non-matching messages to requeue after scan
            var toRequeue = new List<ulong>();

            uint iterationCount = 0;
            while (iterationCount < maxIterations)
            {
                // get a single message without acknowledging it
                var result = await channel.BasicGetAsync(queueName, autoAck: false);
                if (result == null)
                {
                    // queue empty
                    break;
                }

                iterationCount++;

                try
                {
                    string json = Encoding.UTF8.GetString(result.Body.ToArray());
                    var msg = DeserializeExecutionRequested(json);
                    if (msg != null && msg.ExecutionId == executionId)
                    {
                        // found the matching message; ack and return
                        await channel.BasicAckAsync(result.DeliveryTag, multiple: false);

                        // Requeue collected messages before returning
                        foreach (var tag in toRequeue)
                        {
                            await channel.BasicNackAsync(tag, multiple: false, requeue: true);
                        }
                        return true;
                    }
                }
                catch
                {
                    // if we can't deserialize, still collect for requeueing
                }

                // not the one we were looking for - collect for requeueing at end of scan
                toRequeue.Add(result.DeliveryTag);
            }

            // Requeue all collected messages in their original relative order
            foreach (var tag in toRequeue)
            {
                await channel.BasicNackAsync(tag, multiple: false, requeue: true);
            }

            return false;
        });
    }

    /// <summary>
    /// MassTransit's default <c>SystemTextJsonMessageSerializer</c> writes camelCase property
    /// names on the wire (the same reason the Go API's <c>extractExecutionID</c> checks both
    /// <c>"executionId"</c> and <c>"ExecutionId"</c>) — case-insensitive matching is required to
    /// read that back into <see cref="WorkflowExecutionRequested"/>'s PascalCase properties.
    /// </summary>
    private static readonly JsonSerializerOptions s_caseInsensitiveOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Deserializes a raw AMQP body into a <see cref="WorkflowExecutionRequested"/>, unwrapping
    /// MassTransit's envelope (<c>{"messageId": ..., "message": {...}, ...}</c>) first. Without
    /// this, deserializing the envelope directly as <see cref="WorkflowExecutionRequested"/> never
    /// throws — <c>System.Text.Json</c> silently leaves every unmatched property at its default —
    /// so <c>ExecutionId</c> always came back <see cref="Guid.Empty"/> and cancelling a still-queued
    /// execution never actually removed its message. Mirrors the Go API's
    /// <c>unwrapMassTransitMessage</c> (api/services/rabbitmq_service.go), including its
    /// fall-back-to-the-raw-body tolerance for a body that isn't enveloped at all.
    /// </summary>
    internal static WorkflowExecutionRequested? DeserializeExecutionRequested(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out JsonElement message))
            {
                return message.Deserialize<WorkflowExecutionRequested>(s_caseInsensitiveOptions);
            }
        }
        catch (JsonException)
        {
            // Not a JSON object at all (or malformed) — fall through to the raw-body attempt
            // below, which will itself throw and be handled by the caller's catch.
        }

        return JsonSerializer.Deserialize<WorkflowExecutionRequested>(json, s_caseInsensitiveOptions);
    }
}