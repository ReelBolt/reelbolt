using Microsoft.Extensions.Options;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Startup validation of the gateway-link TLS settings. File existence is deliberately NOT checked (the control host
/// may not hold certificates yet; they load lazily and fail closed with a typed unavailable error). With none of the
/// TLS settings set and RequireTls off, nothing is checked.
/// </summary>
public sealed class LocalRunnerGatewayOptionsValidator : IValidateOptions<LocalRunnerGatewayOptions>
{
    /// <summary>All rule violations for <paramref name="o"/> (empty when valid). Messages never contain secrets or paths.</summary>
    public static IReadOnlyList<string> Check(LocalRunnerGatewayOptions o)
    {
        List<string> errors = [];
        bool caSet = !string.IsNullOrWhiteSpace(o.GatewayCaFile);
        bool certSet = !string.IsNullOrWhiteSpace(o.GatewayClientCertFile);
        bool keySet = !string.IsNullOrWhiteSpace(o.GatewayClientKeyFile);
        bool pwSet = !string.IsNullOrEmpty(o.GatewayClientKeyPassword);
        string url = (string.IsNullOrWhiteSpace(o.GatewayBaseUrl) ? "http://runner-gateway:8090" : o.GatewayBaseUrl).Trim();
        bool https = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (o.GatewayRequireTls && !https)
            errors.Add("Compute:GatewayRequireTls is true but Compute:GatewayBaseUrl is not an https URL.");
        if (o.GatewayRequireTls && !caSet)
            errors.Add("Compute:GatewayRequireTls is true but Compute:GatewayCaFile (the private CA) is not set.");
        if (certSet != keySet)
            errors.Add("Compute:GatewayClientCertFile and Compute:GatewayClientKeyFile must be set together (exactly one is set).");
        if (pwSet && !keySet)
            errors.Add("Compute:GatewayClientKeyPassword is set but Compute:GatewayClientKeyFile is not.");
        if ((caSet || certSet || keySet || pwSet) && !https)
            errors.Add("A Compute:Gateway TLS setting (CA, client certificate, key or key password) is set but Compute:GatewayBaseUrl is http, so it would be ignored; use an https URL or remove the setting.");
        return errors;
    }

    public ValidateOptionsResult Validate(string? name, LocalRunnerGatewayOptions options)
    {
        IReadOnlyList<string> errors = Check(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
