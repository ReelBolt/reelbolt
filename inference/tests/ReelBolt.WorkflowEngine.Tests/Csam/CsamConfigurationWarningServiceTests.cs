using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Csam;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// A credential that is present while the master switch is off would otherwise leave every file
/// silently unscanned. The boot check logs one error and never fails startup.
/// </summary>
public class CsamConfigurationWarningServiceTests
{
    [Fact]
    public async Task StartupError_WhenCredentialsPresentButDisabled()
    {
        CsamOptions options = CsamTestSupport.EnabledOptions(o => o.Enabled = false);
        CsamListLogger<CsamConfigurationWarningService> logger = new();
        CsamConfigurationWarningService service = new(Options.Create(options), logger);

        await service.StartAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle()
            .Which.Should().Be(CsamConfigurationWarningService.DisabledWithCredentialsMessage);
        logger.Messages[0].Should().Contain("Enabled=false");
        logger.Messages[0].Should().NotContain(CsamTestSupport.Password, "credentials are never logged");
    }

    [Fact]
    public async Task StartupWarning_WhenEnabledButCredentialsAreMissing()
    {
        CsamOptions options = CsamTestSupport.EnabledOptions(o =>
        {
            o.Username = null;
            o.Password = null;
        });
        CsamListLogger<CsamConfigurationWarningService> logger = new();

        await new CsamConfigurationWarningService(Options.Create(options), logger)
            .StartAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle()
            .Which.Should().Be(CsamConfigurationWarningService.EnabledWithoutCredentialsMessage);
    }

    [Fact]
    public async Task StartupError_WhenEnabledButTheSweeperIsStopped()
    {
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.IntervalMinutes = 0;
        CsamListLogger<CsamConfigurationWarningService> logger = new();

        await new CsamConfigurationWarningService(Options.Create(options), logger)
            .StartAsync(CancellationToken.None);

        logger.Messages.Should().Contain(CsamConfigurationWarningService.EnabledWithStoppedSweeperMessage);
    }

    [Fact]
    public async Task NoError_WhenEnabled_OrWhenNoCredentialsAreConfigured()
    {
        CsamListLogger<CsamConfigurationWarningService> enabledLogger = new();
        await new CsamConfigurationWarningService(
            Options.Create(CsamTestSupport.EnabledOptions()), enabledLogger).StartAsync(CancellationToken.None);
        enabledLogger.Messages.Should().BeEmpty();

        CsamOptions unconfigured = CsamTestSupport.EnabledOptions(o =>
        {
            o.Enabled = false;
            o.Username = null;
            o.Password = null;
        });
        CsamListLogger<CsamConfigurationWarningService> unconfiguredLogger = new();
        await new CsamConfigurationWarningService(Options.Create(unconfigured), unconfiguredLogger)
            .StartAsync(CancellationToken.None);
        unconfiguredLogger.Messages.Should().BeEmpty();
    }
}
