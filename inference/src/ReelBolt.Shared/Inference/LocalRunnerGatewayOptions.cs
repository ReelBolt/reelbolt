namespace ReelBolt.Shared.Inference;

/// <summary>
/// How a service reaches the runner gateway's internal relay for a <see cref="ReelBolt.Shared.Data.Models.InferenceProviderKind.LocalRunner"/>
/// provider. Bound from the same <c>Compute</c> configuration section the compute fabric uses (the keys below are read), so the Inference API and the WorkflowEngine carry one setting each, not two.
/// </summary>
public sealed class LocalRunnerGatewayOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Compute";

    /// <summary>Environment variable the shared service token falls back to.</summary>
    public const string InternalTokenEnvironmentVariable = "RUNNER_INTERNAL_TOKEN";

    /// <summary>Base address of the gateway's internal API (cluster-internal, never published).</summary>
    public string GatewayBaseUrl { get; set; } = "http://runner-gateway:8090";

    /// <summary>The gateway's shared service token. Never logged.</summary>
    public string InternalToken { get; set; } = string.Empty;

    /// <summary>PEM file with one or more CA certificates. When set, the gateway's server certificate is trusted ONLY if it chains to one of these (system roots are not used).</summary>
    public string GatewayCaFile { get; set; } = string.Empty;

    /// <summary>PEM client certificate presented to the gateway for mutual TLS (needs <see cref="GatewayClientKeyFile"/>).</summary>
    public string GatewayClientCertFile { get; set; } = string.Empty;

    /// <summary>PEM private key of the client certificate.</summary>
    public string GatewayClientKeyFile { get; set; } = string.Empty;

    /// <summary>Password of an encrypted client key. Config or environment only; never logged.</summary>
    public string GatewayClientKeyPassword { get; set; } = string.Empty;

    /// <summary>When true, a non-https <see cref="GatewayBaseUrl"/> is refused (never silently sent in plain HTTP).</summary>
    public bool GatewayRequireTls { get; set; }

    internal LocalRunnerTlsHandler Tls => _tls ??= new LocalRunnerTlsHandler(this);
    private LocalRunnerTlsHandler? _tls;

    /// <summary>The configured token when present, else <c>RUNNER_INTERNAL_TOKEN</c>, else null.</summary>
    public string? ResolveInternalToken()
        => !string.IsNullOrWhiteSpace(InternalToken)
            ? InternalToken.Trim()
            : NullIfBlank(Environment.GetEnvironmentVariable(InternalTokenEnvironmentVariable));

    /// <summary>
    /// The relay base address for one device of one organization:
    /// <c>{GatewayBaseUrl}/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1</c>. The OpenAI client appends
    /// <c>/chat/completions</c> (and <c>/models</c>) to it.
    /// </summary>
    public Uri RelayBaseAddress(Guid organizationId, Guid deviceId)
    {
        string root = (string.IsNullOrWhiteSpace(GatewayBaseUrl) ? "http://runner-gateway:8090" : GatewayBaseUrl).Trim().TrimEnd('/');
        return new Uri($"{root}/internal/v1/llm/orgs/{organizationId:D}/devices/{deviceId:D}/v1", UriKind.Absolute);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
