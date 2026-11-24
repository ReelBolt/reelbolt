using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Services.Background;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class StartupCleanupPurgeOffTests
{
    [Fact]
    public async Task With_purge_off_it_touches_neither_rows_nor_queues()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StartupCleanup:PurgeQueues"] = "false" }).Build();
        // Null service provider: any database or broker access would throw NullReferenceException.
        var svc = new StartupCleanupService(null!, config);
        (await svc.CancelOrphanedExecutionsAsync(CancellationToken.None)).Should().Be(0);
        await svc.StartAsync(CancellationToken.None);
    }
}
