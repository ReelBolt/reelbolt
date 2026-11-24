using System.Net;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// A call to a user's own model through their desktop runner could not be served: the runner is offline, the local
/// model is switched off, the device is gone, the local server is busy or unreachable, or the relay is not
/// configured. The message is plain language for the person who started the step. This is the customer's own
/// machine, so the engine classifies it as a bring-your-own provider error; the call is NEVER retried against, or
/// replaced by, a cloud model.
/// </summary>
public sealed class LocalRunnerUnavailableException : InvalidOperationException
{
    /// <summary>The stable failure code.</summary>
    public const string FailureCode = "LOCAL_RUNNER_UNAVAILABLE";

    public LocalRunnerUnavailableException(string reason, string? gatewayCode = null, HttpStatusCode? status = null, Exception? inner = null)
        : base($"{reason} ({FailureCode})", inner)
    {
        GatewayCode = gatewayCode;
        Status = status;
    }

    /// <summary>The gateway's machine-readable error code (for example <c>runner_offline</c>), when it sent one.</summary>
    public string? GatewayCode { get; }

    /// <summary>The HTTP status the gateway answered with, when it answered.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>Always <see cref="FailureCode"/>.</summary>
    public string Code => FailureCode;

    private static readonly HashSet<string> GatewayCodes = new(StringComparer.Ordinal)
    {
        "runner_offline", "local_llm_disabled", "device_not_found", "busy", "local_server_unreachable", "timeout",
        "internal_api_disabled", "unauthorized", "runner_rejected_request", "local_response_too_large",
        "request_too_large", "bad_request", "registry_unavailable", "runner_connection_lost", "cancelled", "tls_handshake_failed", "internal_tls_required"
    };

    /// <summary>Whether <paramref name="code"/> is one the runner gateway itself produces.</summary>
    public static bool IsGatewayCode(string code) => GatewayCodes.Contains(code);

    /// <summary>The model server's text, single line, control characters removed, at most 300 characters; null when empty.</summary>
    public static string? CleanServerText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        char[] kept = [.. text.Where(c => !char.IsControl(c) || c == ' ')];
        string clean = new string(kept).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= 300 ? clean : clean[..300] + "...";
    }

    /// <summary>Plain-language reason for an error answer that came from the user's own model server.</summary>
    public static string ServerAnswerReason(HttpStatusCode status, string? serverMessage) =>
        serverMessage is null
            ? $"Your local model server answered with an error (HTTP {(int)status}). Please check the model name and that the server is healthy."
            : $"Your local model server answered with an error (HTTP {(int)status}): {serverMessage}";

    /// <summary>Plain-language reason for a gateway answer.</summary>
    public static string ReasonFor(HttpStatusCode status, string? gatewayCode)
    {
        const string desktop = "open the ReelBolt desktop app on that computer and make sure sharing a local model is switched on";
        return gatewayCode switch
        {
            "runner_offline" => $"Your local model is offline: the computer it runs on is not connected to ReelBolt. Please {desktop}, then run this again.",
            "local_llm_disabled" => $"Sharing your local model is switched off in the ReelBolt desktop app. Please {desktop}, then run this again.",
            "device_not_found" => "The desktop app this model was set up with is no longer available in your workspace. Pair it again, or choose another model.",
            "busy" => "Your local model is busy with other requests right now. Please try again in a moment.",
            "local_server_unreachable" => "The desktop app could not reach your local model server. Please check that it is running (for example Ollama or LM Studio) on that computer.",
            "timeout" => "Your local model did not answer in time. It may still be loading or the request was too large for it.",
            "internal_api_disabled" => "The connection to local models is not set up on this ReelBolt installation, so your local model cannot be reached.",
            "unauthorized" => "The connection to local models is not set up correctly on this ReelBolt installation (the relay refused its credentials), so your local model cannot be reached.",
            "request_too_large" => "The request is too large for the local model relay. Please send less text or a smaller image.",
            "local_response_too_large" => "Your local model's answer was too large for the local model relay. Please ask for a shorter answer.",
            "runner_connection_lost" => "The connection to the desktop app was lost while your local model was answering. Please check that the app is running, then run this again.",
            "tls_handshake_failed" => "The secure connection to the local-model gateway could not be established. Please ask your administrator to check the certificates.",
            "internal_tls_required" => "The connection to local models must use the secure address. Please ask your administrator to fix the connection settings.",
            "cancelled" => "The request to your local model was cancelled before it finished. Please run this again.",
            "runner_rejected_request" => "The desktop app refused this request to your local model. Please make sure the app is up to date, then run this again.",
            "bad_request" => "The request to your local model was not valid, so the desktop app could not pass it on.",
            "registry_unavailable" => "ReelBolt could not look up your desktop app just now. Please try again in a moment.",
            _ => status switch
            {
                HttpStatusCode.TooManyRequests => "Your local model is busy with other requests right now. Please try again in a moment.",
                HttpStatusCode.GatewayTimeout => "Your local model did not answer in time. It may still be loading or the request was too large for it.",
                HttpStatusCode.NotFound => "The desktop app this model was set up with is no longer available in your workspace. Pair it again, or choose another model.",
                HttpStatusCode.Conflict => $"Sharing your local model is switched off in the ReelBolt desktop app. Please {desktop}, then run this again.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "The connection to local models is not set up on this ReelBolt installation, so your local model cannot be reached.",
                HttpStatusCode.ServiceUnavailable => $"Your local model is offline. Please {desktop}, then run this again.",
                _ => $"Your local model could not be reached through the ReelBolt desktop app (the connection answered {(int)status}). Please {desktop}."
            }
        };
    }
}
