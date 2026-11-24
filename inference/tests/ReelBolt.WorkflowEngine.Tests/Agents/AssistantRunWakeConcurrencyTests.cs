#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>Wakes run in parallel on their own contexts: a slow turn delays neither housekeeping nor another thread's wake. File-backed SQLite (several connections).</summary>
public sealed class AssistantRunWakeConcurrencyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wake-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_path};Pooling=False";

    private InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(ConnectionString).Options);

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private sealed class GatedRunner(Guid blockedThread, TaskCompletionSource started, TaskCompletionSource release) : IAssistantWakeTurnRunner
    {
        public async Task<WakeOutcome> RunAsync(AssistantRunWatch watch, WakeIdentity identity, string wakeText, AssistantWakeRun run, CancellationToken ct)
        {
            if (watch.ThreadId == blockedThread)
            {
                started.SetResult();
                await release.Task;
            }

            return WakeOutcome.Fired(Guid.NewGuid());
        }
    }

    [Fact]
    public async Task A_blocked_wake_delays_neither_expiry_nor_another_threads_wake()
    {
        using (InferenceApiDbContext setup = NewDb())
        {
            setup.Database.EnsureCreated();
            var engineOptions = new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>().UseSqlite(ConnectionString).Options;
            using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(engineOptions);
            engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
        }

        Guid user = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(user);
        var project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = user, OrganizationId = org };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Film", ProjectId = project.Id };
        var threadA = new AssistantThread { Id = Guid.NewGuid(), UserId = user, OrganizationId = org, ProjectId = project.Id };
        var threadB = new AssistantThread { Id = Guid.NewGuid(), UserId = user, OrganizationId = org, ProjectId = project.Id };
        WorkflowExecution Run(ExecutionStatus s) => new() { Id = Guid.NewGuid(), ProjectId = project.Id, WorkflowDefinitionId = workflow.Id, Status = s };
        WorkflowExecution runA = Run(ExecutionStatus.Passed), runB = Run(ExecutionStatus.Passed), runC = Run(ExecutionStatus.Running);
        DateTime now = DateTime.UtcNow;
        AssistantRunWatch Watch(Guid thread, Guid exec, DateTime created, DateTime expires) => new()
        {
            Id = Guid.NewGuid(),
            ThreadId = thread,
            ExecutionId = exec,
            UserId = user,
            OrganizationId = org,
            ProjectId = project.Id,
            WorkflowName = "Film",
            CreatedAt = created,
            ExpiresAt = expires
        };
        AssistantRunWatch wa = Watch(threadA.Id, runA.Id, now.AddMinutes(-3), now.AddHours(1));
        AssistantRunWatch wb = Watch(threadB.Id, runB.Id, now.AddMinutes(-2), now.AddHours(1));
        AssistantRunWatch wc = Watch(threadB.Id, runC.Id, now.AddMinutes(-1), now.AddMinutes(-1)); // past its ttl
        using (InferenceApiDbContext seed = NewDb())
        {
            seed.Organizations.Add(new Organization { Id = org, Name = "O", Slug = "o-" + org.ToString("N")[..8] });
            seed.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "u@example.com", DisplayName = "U", MustChangePassword = false, EmailVerifiedAt = now });
            seed.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org, UserId = user, Role = OrganizationRole.Member });
            seed.Projects.Add(project);
            seed.WorkflowDefinitions.Add(workflow);
            seed.WorkflowExecutions.AddRange(runA, runB, runC);
            seed.AssistantThreads.AddRange(threadA, threadB);
            seed.AssistantRunWatches.AddRange(wa, wb, wc);
            seed.SaveChanges();
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new GatedRunner(threadA.Id, started, release);
        var config = new ConfigurationBuilder().Build();
        AssistantRunWakeSweeper Sweeper(InferenceApiDbContext db) => new(db, runner, NullLogger<AssistantRunWakeSweeper>.Instance, null, new AssistantTurnHub(), config, null, null,
            () => { InferenceApiDbContext worker = NewDb(); return (worker, worker); });

        using InferenceApiDbContext db1 = NewDb();
        Task<int> sweep1 = Task.Run(() => Sweeper(db1).SweepOnceAsync(default));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Thread A's turn is stuck. Thread B's wake and the expiry still land.
        using InferenceApiDbContext probe = NewDb();
        for (int i = 0; i < 300 && probe.AssistantRunWatches.AsNoTracking().Single(w => w.Id == wb.Id).Status != AssistantRunWatchStatus.Fired; i++)
            await Task.Delay(50);
        probe.AssistantRunWatches.AsNoTracking().Single(w => w.Id == wb.Id).Status.Should().Be(AssistantRunWatchStatus.Fired);
        probe.AssistantRunWatches.AsNoTracking().Single(w => w.Id == wc.Id).Status.Should().Be(AssistantRunWatchStatus.Expired);
        probe.AssistantRunWatches.AsNoTracking().Single(w => w.Id == wa.Id).Status.Should().Be(AssistantRunWatchStatus.Claimed);
        sweep1.IsCompleted.Should().BeFalse();

        // A second sweep (the next tick) does not wait for A either and does not touch its claim.
        using InferenceApiDbContext db2 = NewDb();
        (await Sweeper(db2).SweepOnceAsync(default)).Should().Be(0);

        release.SetResult();
        (await sweep1).Should().Be(2);
        probe.AssistantRunWatches.AsNoTracking().Single(w => w.Id == wa.Id).Status.Should().Be(AssistantRunWatchStatus.Fired);
    }
}
