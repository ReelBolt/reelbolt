using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

public class ProductEventEmitterTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    private static ProductEventEmitter Emitter(RecordingProductEventLog log) => new(log, NullLogger<ProductEventEmitter>.Instance);

    [Fact]
    public async Task First_writes_one_server_row_per_user_and_event()
    {
        var log = new RecordingProductEventLog();
        ProductEventEmitter e = Emitter(log);
        await e.FirstAsync(User, Org, ProductEventNames.ProjectCreated, CancellationToken.None);
        await e.FirstAsync(User, Org, ProductEventNames.ProjectCreated, CancellationToken.None);
        ProductEvent row = log.Rows.Single();
        row.Event.Should().Be("activation.project_created");
        row.Source.Should().Be(ProductEventSources.Server);
        row.UserId.Should().Be(User);
    }

    [Fact]
    public async Task Append_carries_the_closed_detail_each_time()
    {
        var log = new RecordingProductEventLog();
        ProductEventEmitter e = Emitter(log);
        await e.AppendAsync(User, Org, ProductEventNames.TemplateChosen, "launch-video", CancellationToken.None);
        await e.AppendAsync(User, Org, ProductEventNames.TemplateChosen, "launch-video", CancellationToken.None);
        log.Rows.Should().HaveCount(2).And.OnlyContain(r => r.Detail == "launch-video" && r.Source == "server");
    }

    [Fact]
    public async Task A_failing_log_never_throws_into_the_request()
    {
        var log = new RecordingProductEventLog { Throw = true };
        ProductEventEmitter e = Emitter(log);
        await e.FirstAsync(User, Org, ProductEventNames.OutputDownloaded, CancellationToken.None);
        await e.AppendAsync(User, Org, ProductEventNames.ChangeRequested, null, CancellationToken.None);
    }

    [Fact]
    public async Task An_empty_user_id_writes_nothing()
    {
        var log = new RecordingProductEventLog();
        await Emitter(log).FirstAsync(Guid.Empty, null, ProductEventNames.McpConnected, CancellationToken.None);
        log.Rows.Should().BeEmpty();
    }
}
