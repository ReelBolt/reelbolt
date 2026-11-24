using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Turns what the runner gateway's relay answers into a <see cref="LocalRunnerUnavailableException"/> with a
/// plain-language reason. Everything else a model says passes through untouched. There is deliberately no
/// fallback here: a local model that cannot be reached fails the call.
/// </summary>
internal sealed class LocalRunnerChatClient : DelegatingChatClient
{
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public LocalRunnerChatClient(IChatClient inner, Microsoft.Extensions.Logging.ILogger? logger = null) : base(inner)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (Translate(ex, cancellationToken, _logger) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ChatResponseUpdate> enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (Exception ex) when (Translate(ex, cancellationToken, _logger) is { } mapped)
                {
                    throw mapped;
                }

                if (!moved) yield break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>The typed exception for a relay failure, or null when <paramref name="ex"/> is not one.</summary>
    internal static LocalRunnerUnavailableException? Translate(Exception ex, CancellationToken cancellationToken, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        if (ex is not LocalRunnerUnavailableException && HasTlsFailure(ex))
        {
            logger?.LogWarning(ex, "TLS handshake with the local-model gateway failed");
            return new LocalRunnerUnavailableException(
                LocalRunnerUnavailableException.ReasonFor(HttpStatusCode.BadGateway, "tls_handshake_failed"), "tls_handshake_failed", null, ex);
        }

        switch (ex)
        {
            case LocalRunnerUnavailableException:
                return null;
            case ClientResultException { Status: > 0 } failed:
                {
                    HttpStatusCode status = (HttpStatusCode)failed.Status;
                    string? code = GatewayCodeOf(failed);
                    // No gateway code: the answer is the user's own model server's (the relay passes those through
                    // verbatim), for example "model not found". Say that, with the server's own words, instead of
                    // blaming the connection.
                    string reason = code is null
                        ? LocalRunnerUnavailableException.ServerAnswerReason(status, ServerMessageOf(failed))
                        : LocalRunnerUnavailableException.ReasonFor(status, code);
                    return new LocalRunnerUnavailableException(reason, code ?? "local_server_error", status, ex);
                }
            case ClientResultException or HttpRequestException or IOException:
                // IOException covers a relay that drops the connection after it started answering (the gateway aborts the
                // stream rather than ending it cleanly), so a half answer is a failure and never reads as a complete one.
                return new LocalRunnerUnavailableException(
                    "The connection to your local model could not be made or was lost, so it cannot be reached right now. Please try again in a moment.",
                    "gateway_unreachable", null, ex);
            case TaskCanceledException when !cancellationToken.IsCancellationRequested:
            case TimeoutException:
                return new LocalRunnerUnavailableException(
                    LocalRunnerUnavailableException.ReasonFor(HttpStatusCode.GatewayTimeout, "timeout"), "timeout", HttpStatusCode.GatewayTimeout, ex);
            default:
                return null;
        }
    }

    private static bool HasTlsFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException) return true;
        }

        return false;
    }

    /// <summary>The header the gateway puts on every refusal of its own; a relayed server answer never carries it.</summary>
    internal const string RelayErrorHeader = "X-ReelBolt-Relay-Error";

    /// <summary>
    /// The gateway's own error code: the <see cref="RelayErrorHeader"/> is the ONLY authority (the gateway sets it on
    /// every refusal of its own, and a model server's body cannot forge it). Null means the answer came from the server.
    /// </summary>
    private static string? GatewayCodeOf(ClientResultException failed)
    {
        if (failed.GetRawResponse()?.Headers.TryGetValue(RelayErrorHeader, out string? header) == true
            && !string.IsNullOrWhiteSpace(header))
            return header.Trim();
        return null;
    }

    /// <summary>The model server's own error text (<c>error.message</c>, <c>error</c> or <c>message</c>), trimmed and cleaned.</summary>
    private static string? ServerMessageOf(ClientResultException failed)
    {
        try
        {
            string? body = failed.GetRawResponse()?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(body)) return null;
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? text = null;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String) text = error.GetString();
                else if (error.ValueKind == JsonValueKind.Object
                         && error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                    text = m.GetString();
            }

            if (text is null && root.TryGetProperty("message", out JsonElement flat) && flat.ValueKind == JsonValueKind.String)
                text = flat.GetString();
            return LocalRunnerUnavailableException.CleanServerText(text);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
