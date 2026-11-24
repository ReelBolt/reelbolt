using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>
/// Startup check for the master switch: credentials configured while <c>Csam:Arachnid:Enabled</c> is
/// false is a silent mismatch that would leave every file unscanned, so it is logged as an error once
/// at boot. It never fails startup — the feature is fail-open by design.
/// </summary>
public sealed class CsamConfigurationWarningService : IHostedService
{
    /// <summary>The message a test asserts on; it names the switch and the consequence.</summary>
    public const string DisabledWithCredentialsMessage =
        "CSAM credentials are configured but CSAM is disabled (Csam:Arachnid:Enabled=false); no file will be scanned.";

    /// <summary>The inverse mismatch: the cloud overlay turns the control on, but a credential is missing.</summary>
    public const string EnabledWithoutCredentialsMessage =
        "CSAM is enabled (Csam:Arachnid:Enabled=true) but its username or password is missing; every file will fail open as not-scanned.";

    /// <summary>
    /// An enabled control with a stopped sweeper can never settle background-written rows: they stay
    /// Pending and ingest moderation waits for a verdict that will not arrive.
    /// </summary>
    public const string EnabledWithStoppedSweeperMessage =
        "CSAM is enabled but its sweeper is stopped (Csam:Sweeper:IntervalMinutes<=0); background-written rows stay Pending and ingest moderation will wait. Set an interval or disable CSAM.";

    private readonly IOptions<CsamOptions> _options;
    private readonly ILogger<CsamConfigurationWarningService> _logger;

    public CsamConfigurationWarningService(
        IOptions<CsamOptions> options,
        ILogger<CsamConfigurationWarningService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ArachnidOptions arachnid = _options.Value.Arachnid;
        bool hasCredentials = !string.IsNullOrWhiteSpace(arachnid.Username)
            && !string.IsNullOrWhiteSpace(arachnid.Password);
        if (!arachnid.Enabled && hasCredentials)
        {
            _logger.LogError(DisabledWithCredentialsMessage);
        }
        else if (arachnid.Enabled && !hasCredentials)
        {
            // The cloud control overlay forces Enabled=true; a blank /opt/reelbolt/.env credential
            // would otherwise fail open silently for every upload.
            _logger.LogWarning(EnabledWithoutCredentialsMessage);
        }

        if (arachnid.Enabled && _options.Value.Sweeper.IntervalMinutes <= 0)
        {
            // The one-shot startup normalization cannot cover rows written later, so this is a
            // permanent availability fault until an interval is set.
            _logger.LogError(EnabledWithStoppedSweeperMessage);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
