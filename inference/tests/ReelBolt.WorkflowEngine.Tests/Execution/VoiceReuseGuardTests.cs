using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using ReelBolt.WorkflowEngine.Tests.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution;

/// <summary>
/// A cloned voice's consent is re-checked whenever a recorded Voiceover result is REUSED (step-result cache hit,
/// resume, composition replay copy), not only when the step runs live (docs/voiceover.md "Voices and consent").
/// </summary>
public class VoiceReuseGuardTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Func<WorkflowEngineDbContext> NewDb() => NewDb("voice-reuse-" + Guid.NewGuid().ToString("N"));

    private static Func<WorkflowEngineDbContext> NewDb(string name) =>
        () => new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(name).Options);

    internal sealed class DbScopes(Func<WorkflowEngineDbContext> newDb, IStepResultCache? cache = null, IVoiceReuseGuard? guard = null) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(newDb(), cache, guard);

        private sealed class Scope(WorkflowEngineDbContext db, IStepResultCache? cache, IVoiceReuseGuard? guard) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public void Dispose() => db.Dispose();

            public object? GetService(Type serviceType)
                => serviceType == typeof(WorkflowEngineDbContext) ? db
                : serviceType == typeof(IStepResultCache) ? cache
                : serviceType == typeof(IProjectFileFingerprintProvider) && cache is not null ? new Fingerprint()
                : serviceType == typeof(IVoiceReuseGuard) ? guard
                : null;
        }

        private sealed class Fingerprint : IProjectFileFingerprintProvider
        {
            public Task<string> GetFingerprintAsync(Guid projectId, CancellationToken ct) => Task.FromResult(new string('f', 64));
        }
    }

    private static ProjectVoice Cloned(Action<ProjectVoice>? tweak = null, Guid? projectId = null)
    {
        var v = new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId ?? ProjectId,
            InferenceProviderId = Guid.NewGuid(),
            Name = "clone",
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = "remote-" + Guid.NewGuid().ToString("N"),
            ConsentAttestedAt = Now.AddDays(-1),
            ConsentAttestedByUserId = Guid.NewGuid(),
            ConsentStatement = "I consent",
            ConsentSubjectName = "Subject",
            CreatedAt = Now.AddDays(-1)
        };
        tweak?.Invoke(v);
        return v;
    }

    private static string ConfigFor(Guid voiceId) => JsonSerializer.Serialize(new { projectVoiceId = voiceId });

    // The shape VoiceoverStepExecutor writes: meta is a sibling of view at the root, the voice node is meta.voice
    // (VoiceoverStepExecutorReuseGuardTests feeds the guard the executor's real output as well).
    private static string OutputFor(Guid? projectVoiceId, string? remoteVoiceId) =>
        JsonSerializer.Serialize(new
        {
            view = new { lines = new[] { new { id = "vo0", status = "ok" } } },
            meta = new
            {
                totalLines = 1,
                voice = new
                {
                    projectVoiceId,
                    remoteVoiceId,
                    source = projectVoiceId is not null ? "project_voice" : remoteVoiceId is not null ? "cast" : "provider_default",
                    casting = new { mode = "Off", applied = false }
                }
            }
        });

    internal static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    internal static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    /// <summary>Projects of the test: the execution's project and every project a voice belongs to, all in OrgA except <paramref name="otherOrgProjects"/> (OrgB).</summary>
    internal static void SeedProjects(
        WorkflowEngineDbContext db, Guid executionProjectId, IEnumerable<ProjectVoice> voices,
        ISet<Guid>? otherOrgProjects = null, ISet<Guid>? platformProviders = null, ISet<Guid>? unresolvableCapabilities = null)
    {
        List<ProjectVoice> list = [.. voices];
        foreach (Guid id in list.Select(v => v.ProjectId).Append(executionProjectId).Distinct())
            db.Projects.Add(new Project { Id = id, Name = "p", OrganizationId = otherOrgProjects?.Contains(id) == true ? OrgB : OrgA });

        // InferenceProviderId is a model CAPABILITY id. Each clone gets a real chain capability -> model -> provider account,
        // with three DIFFERENT ids, the account being a PRIVATE one of the organization of the project that made the clone
        // unless the capability is listed as platform. A capability listed in unresolvable has no row at all.
        foreach (ProjectVoice v in list.Where(v => v.InferenceProviderId is not null).DistinctBy(v => v.InferenceProviderId))
        {
            Guid capabilityId = v.InferenceProviderId!.Value;
            if (unresolvableCapabilities?.Contains(capabilityId) == true)
                continue;
            bool platform = platformProviders?.Contains(capabilityId) == true;
            Guid org = otherOrgProjects?.Contains(v.ProjectId) == true ? OrgB : OrgA;
            var account = new InferenceProvider { Id = Guid.NewGuid(), Name = "account", OrganizationId = platform ? null : org, Endpoint = "https://example.invalid" };
            var model = new InferenceModel { Id = Guid.NewGuid(), ProviderId = account.Id, ModelCode = "m", DisplayName = "m" };
            db.InferenceProviders.Add(account);
            db.InferenceModels.Add(model);
            db.ModelCapabilities.Add(new ModelCapability { Id = capabilityId, ModelId = model.Id, OrganizationId = platform ? null : org });
        }
    }

    private static Task<(VoiceReuseGuard Guard, Func<WorkflowEngineDbContext> Db)> GuardWith(params ProjectVoice[] voices) =>
        GuardWithOrgs(null, null, null, voices);

    private static async Task<(VoiceReuseGuard Guard, Func<WorkflowEngineDbContext> Db)> GuardWithOrgs(
        ISet<Guid>? otherOrgProjects, ISet<Guid>? platformProviders, ISet<Guid>? unresolvableCapabilities, params ProjectVoice[] voices)
    {
        Func<WorkflowEngineDbContext> db = NewDb();
        using (WorkflowEngineDbContext seed = db())
        {
            seed.ProjectVoices.AddRange(voices);
            SeedProjects(seed, ProjectId, voices, otherOrgProjects, platformProviders, unresolvableCapabilities);
            await seed.SaveChangesAsync();
        }

        return (new VoiceReuseGuard(new DbScopes(db)), db);
    }

    [Fact]
    public async Task A_selectable_cloned_voice_is_reusable()
    {
        ProjectVoice voice = Cloned();
        var (guard, _) = await GuardWith(voice);
        (await guard.IsReuseAllowedAsync(ProjectId, ConfigFor(voice.Id), OutputFor(voice.Id, voice.RemoteVoiceId), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task A_revoked_voice_blocks_reuse()
    {
        ProjectVoice voice = Cloned(v => v.ConsentRevokedAt = Now.AddHours(-1));
        var (guard, _) = await GuardWith(voice);
        (await guard.IsReuseAllowedAsync(ProjectId, ConfigFor(voice.Id), OutputFor(voice.Id, voice.RemoteVoiceId), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task An_expired_retention_blocks_reuse()
    {
        ProjectVoice voice = Cloned(v => v.RetentionUntil = Now.AddDays(-1));
        var (guard, _) = await GuardWith(voice);
        (await guard.IsReuseAllowedAsync(ProjectId, null, OutputFor(voice.Id, voice.RemoteVoiceId), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_deleted_voice_blocks_reuse_from_the_config_and_from_the_output()
    {
        var (guard, _) = await GuardWith();
        Guid gone = Guid.NewGuid();
        (await guard.IsReuseAllowedAsync(ProjectId, ConfigFor(gone), null, CancellationToken.None)).Should().BeFalse();
        (await guard.IsReuseAllowedAsync(ProjectId, null, OutputFor(gone, "r"), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cloned_voice_whose_provider_row_was_deleted_blocks_reuse()
    {
        ProjectVoice voice = Cloned(v => v.InferenceProviderId = null);
        var (guard, _) = await GuardWith(voice);
        (await guard.IsReuseAllowedAsync(ProjectId, ConfigFor(voice.Id), null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cast_voice_is_matched_by_its_remote_id_and_a_revoked_match_blocks_reuse()
    {
        ProjectVoice revoked = Cloned(v => v.ConsentRevokedAt = Now.AddHours(-1));
        ProjectVoice fine = Cloned();
        var (guard, _) = await GuardWith(revoked, fine);

        // No ProjectVoiceId anywhere: the Decision-cast output only records the vendor id.
        (await guard.IsReuseAllowedAsync(ProjectId, "{\"voiceCasting\":\"Decision\"}", OutputFor(null, revoked.RemoteVoiceId), CancellationToken.None)).Should().BeFalse();
        (await guard.IsReuseAllowedAsync(ProjectId, "{\"voiceCasting\":\"Decision\"}", OutputFor(null, fine.RemoteVoiceId), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task A_library_voice_is_unaffected()
    {
        ProjectVoice lib = Cloned(v =>
        {
            v.Source = ProjectVoiceSource.Library;
            v.InferenceProviderId = null;
            v.ConsentAttestedAt = null;
            v.ConsentStatement = null;
        });
        var (guard, _) = await GuardWith(lib);
        (await guard.IsReuseAllowedAsync(ProjectId, ConfigFor(lib.Id), OutputFor(lib.Id, lib.RemoteVoiceId), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task A_step_with_no_project_voice_is_unaffected_and_asks_nothing_of_the_database()
    {
        var guard = new VoiceReuseGuard(new ThrowingScopes());
        (await guard.IsReuseAllowedAsync(ProjectId, "{\"defaultVoiceId\":null}", OutputFor(null, null), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task A_voice_of_another_project_resolves_as_not_found()
    {
        ProjectVoice foreign = Cloned(projectId: Guid.NewGuid());
        var (guard, _) = await GuardWith(foreign);
        (await guard.IsReuseAllowedAsync(ProjectId, ConfigFor(foreign.Id), null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_recheck_blocks_reuse_but_cancellation_propagates()
    {
        var guard = new VoiceReuseGuard(new ThrowingScopes());
        (await guard.IsReuseAllowedAsync(ProjectId, ConfigFor(Guid.NewGuid()), null, CancellationToken.None)).Should().BeFalse();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelling = new VoiceReuseGuard(new ThrowingScopes(new OperationCanceledException(cts.Token)));
        Func<Task> act = () => cancelling.IsReuseAllowedAsync(ProjectId, ConfigFor(Guid.NewGuid()), null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ThrowingScopes(Exception? error = null) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw (error ?? new InvalidOperationException("db down"));
    }

    // ------------------------------------------------------------------ the three reuse paths

    private sealed class HitCache(string output) : IStepResultCache
    {
        public Task<StepCacheLookupResult> TryGetAsync(StepCacheKeyInputs inputs, CancellationToken ct) =>
            Task.FromResult(new StepCacheLookupResult(true, new WorkflowStepCacheEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = inputs.ProjectId,
                CacheKey = new string('a', 64),
                StepType = StepType.Voiceover,
                Output = output
            }));

        public Task StoreAsync(StepCacheKeyInputs inputs, StepExecutionResult result, CancellationToken ct) => Task.CompletedTask;
    }

    private static ExecutionResumeTests.StepSpec VoiceStep(Guid voiceId) =>
        new(StepType.Voiceover, "VO", s => s.VoiceoverConfigJson = ConfigFor(voiceId));

    private static async Task<(List<string> Ran, WorkflowExecution Run)> RunCached(ProjectVoice voice, string cachedOutput)
    {
        Func<WorkflowEngineDbContext> db = NewDb();
        ExecutionResumeTests.Seeded s;
        using (WorkflowEngineDbContext seed = db())
        {
            s = ExecutionResumeTests.Seed(seed, VoiceStep(voice.Id));
            voice.ProjectId = s.ProjectId;
            seed.ProjectVoices.Add(voice);
            await seed.SaveChangesAsync();
        }

        var scopes = new DbScopes(db, new HitCache(cachedOutput), new VoiceReuseGuard(new DbScopes(db)));
        List<string> ran = [];
        await ExecutionResumeTests.Service(scopes, null, null, null, cached: true,
            new ExecutionResumeTests.Executor(StepType.Voiceover, c =>
            {
                ran.Add("live");
                return Task.FromResult(ExecutionResumeTests.Done(c, "{\"live\":true}"));
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        using WorkflowEngineDbContext read = db();
        return (ran, await read.WorkflowExecutions.SingleAsync(e => e.Id == s.ExecutionId));
    }

    [Fact]
    public async Task Cache_hit_with_a_selectable_voice_is_still_served()
    {
        ProjectVoice voice = Cloned();
        var (ran, run) = await RunCached(voice, OutputFor(voice.Id, voice.RemoteVoiceId));
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        ran.Should().BeEmpty("a hit with a consented voice is served from the cache");
    }

    [Fact]
    public async Task Cache_hit_with_a_revoked_voice_is_a_miss_and_the_step_runs_live()
    {
        ProjectVoice voice = Cloned(v => v.ConsentRevokedAt = Now.AddMinutes(-5));
        var (ran, _) = await RunCached(voice, OutputFor(voice.Id, voice.RemoteVoiceId));
        ran.Should().Equal("live");
    }

    private static async Task<(List<string> Ran, WorkflowExecution Run)> RunResumed(bool revokeBeforeResume)
    {
        ProjectVoice voice = Cloned();
        Func<WorkflowEngineDbContext> db = NewDb();
        ExecutionResumeTests.Seeded s;
        using (WorkflowEngineDbContext seed = db())
        {
            s = ExecutionResumeTests.Seed(seed, VoiceStep(voice.Id), new(StepType.Agent, "A2"));
            voice.ProjectId = s.ProjectId;
            seed.ProjectVoices.Add(voice);
            await seed.SaveChangesAsync();
        }

        var scopes = new DbScopes(db, null, new VoiceReuseGuard(new DbScopes(db)));
        string recorded = OutputFor(voice.Id, voice.RemoteVoiceId);

        // First dispatch: Voiceover completes, step 2 fails, so a resume has a recorded Voiceover result to reuse.
        Func<Task> first = () => ExecutionResumeTests.Service(scopes, null, null, null,
            new ExecutionResumeTests.Executor(StepType.Voiceover, c => Task.FromResult(ExecutionResumeTests.Done(c, recorded))),
            new ExecutionResumeTests.Executor(StepType.Agent, c => throw new InvalidOperationException("provider down")))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        await first.Should().ThrowAsync<Exception>();

        if (revokeBeforeResume)
        {
            using WorkflowEngineDbContext rev = db();
            (await rev.ProjectVoices.SingleAsync()).ConsentRevokedAt = DateTime.UtcNow;
            await rev.SaveChangesAsync();
        }

        await ExecutionResumeTests.RequeueAsync(db, s.ExecutionId, resume: true);
        List<string> ran = [];
        await ExecutionResumeTests.Service(scopes, null, null, null,
            new ExecutionResumeTests.Executor(StepType.Voiceover, c =>
            {
                ran.Add("live");
                return Task.FromResult(ExecutionResumeTests.Done(c, recorded));
            }),
            new ExecutionResumeTests.Executor(StepType.Agent, c => Task.FromResult(ExecutionResumeTests.Done(c, "{}"))))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        using WorkflowEngineDbContext read = db();
        return (ran, await read.WorkflowExecutions.SingleAsync(e => e.Id == s.ExecutionId));
    }

    [Fact]
    public async Task Resume_reuses_the_recorded_voiceover_while_the_voice_is_selectable()
    {
        var (ran, run) = await RunResumed(revokeBeforeResume: false);
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        ran.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_does_not_reuse_the_recorded_voiceover_after_consent_was_revoked()
        => (await RunResumed(revokeBeforeResume: true)).Ran.Should().Equal("live");

    private static async Task<List<string>> RunReplay(bool revoked)
    {
        ProjectVoice voice = Cloned();
        Func<WorkflowEngineDbContext> db = NewDb();
        ExecutionResumeTests.Seeded s;
        using (WorkflowEngineDbContext seed = db())
        {
            s = ExecutionResumeTests.Seed(seed, VoiceStep(voice.Id), new(StepType.VideoCompile, "C"), new(StepType.Agent, "Unrelated"));
            voice.ProjectId = s.ProjectId;
            if (revoked)
                voice.ConsentRevokedAt = DateTime.UtcNow.AddMinutes(-1);
            seed.ProjectVoices.Add(voice);

            // The run being changed: a finished execution with a recorded result for every step.
            WorkflowExecution run = await seed.WorkflowExecutions.Include(e => e.WorkflowDefinition).SingleAsync(e => e.Id == s.ExecutionId);
            List<WorkflowStep> steps = await seed.WorkflowSteps.Where(x => x.WorkflowDefinitionId == run.WorkflowDefinitionId).OrderBy(x => x.StepOrder).ToListAsync();
            var source = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = run.WorkflowDefinitionId,
                ProjectId = s.ProjectId,
                OrganizationId = s.OrganizationId,
                Status = ExecutionStatus.Passed,
                QueuedAt = DateTime.UtcNow
            };
            seed.Add(source);
            List<ReplayStepPlan> planned = [];
            foreach (WorkflowStep step in steps)
            {
                var row = new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = source.Id,
                    WorkflowStepId = step.Id,
                    Status = StepStatus.Completed,
                    Output = step.StepOrder == 1 ? OutputFor(voice.Id, voice.RemoteVoiceId) : "{\"recorded\":true}",
                    ExecutedAt = DateTime.UtcNow
                };
                seed.Add(row);
                // The compile mixes the narration, so it depends on step 1; the third step depends on nothing.
                planned.Add(new ReplayStepPlan(step.StepOrder, ReplayStepDisposition.Reuse, row.Id, step.StepOrder == 2 ? [1] : [], "does not depend on the film"));
            }

            run.WorkflowDefinition.Kind = WorkflowDefinitionKind.CompositionReplay;
            var plan = new CompositionReplayPlan(
                CompositionReplayPlan.CurrentVersion, source.Id, null, 99, CompositionCutPolicy.KeepCut, planned, []);
            seed.Add(new CompositionRevision
            {
                Id = Guid.NewGuid(),
                ProjectId = s.ProjectId,
                OrganizationId = s.OrganizationId,
                SourceExecutionId = source.Id,
                ExecutionId = run.Id,
                WorkflowDefinitionId = run.WorkflowDefinitionId,
                ReplayPlanJson = plan.Serialize()
            });
            await seed.SaveChangesAsync();
        }

        List<string> ran = [];
        await ExecutionResumeTests.Service(new DbScopes(db, null, new VoiceReuseGuard(new DbScopes(db))), null, null, null,
            new ExecutionResumeTests.Executor(StepType.Voiceover, c => { ran.Add("voiceover"); return Task.FromResult(ExecutionResumeTests.Done(c, OutputFor(null, null))); }),
            new ExecutionResumeTests.Executor(StepType.VideoCompile, c => { ran.Add("compile"); return Task.FromResult(ExecutionResumeTests.Done(c, "{\"live\":true}")); }),
            new ExecutionResumeTests.Executor(StepType.Agent, c => { ran.Add("agent"); return Task.FromResult(ExecutionResumeTests.Done(c, "{}")); }))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        return ran;
    }

    [Fact]
    public async Task Replay_still_copies_every_step_while_the_voice_is_selectable()
        => (await RunReplay(revoked: false)).Should().BeEmpty();

    [Fact]
    public async Task Replay_downgrades_the_voiceover_and_re_makes_the_compile_that_mixes_it_but_not_an_unrelated_step()
        => (await RunReplay(revoked: true)).Should().Equal("voiceover", "compile");

    [Fact]
    public void An_invalidated_step_re_makes_its_transitive_dependents_only()
    {
        WorkflowStep Step(int order) => new() { Id = Guid.NewGuid(), StepOrder = order, StepType = StepType.Agent };
        List<WorkflowStep> steps = [Step(1), Step(2), Step(3), Step(4)];
        Guid src = Guid.NewGuid();
        var rows = steps.Select(st => new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowStepId = st.Id, Output = "{}" }).ToList();
        var plan = new CompositionReplayPlan(
            CompositionReplayPlan.CurrentVersion, src, null, 99, CompositionCutPolicy.KeepCut,
            [
                new ReplayStepPlan(1, ReplayStepDisposition.Reuse, rows[0].Id, [], "r"),
                new ReplayStepPlan(2, ReplayStepDisposition.Reuse, rows[1].Id, [1], "r"),
                new ReplayStepPlan(3, ReplayStepDisposition.Reuse, rows[2].Id, [2], "r"),
                new ReplayStepPlan(4, ReplayStepDisposition.Reuse, rows[3].Id, [], "r")
            ], []);
        var session = new CompositionReplaySession(plan, steps, rows);

        session.Decide(steps[1]).Kind.Should().Be(ReplayActionKind.Reuse);
        session.Invalidate(1);
        ReplayAction two = session.Decide(steps[1]);
        two.Kind.Should().Be(ReplayActionKind.Execute);
        two.Reason.Should().Contain("step 1");
        session.Decide(steps[2]).Kind.Should().Be(ReplayActionKind.Execute, "it depends on step 2, which is now re-made");
        session.Decide(steps[3]).Kind.Should().Be(ReplayActionKind.Reuse);
    }

    [Fact]
    public async Task A_missing_output_does_not_excuse_a_voice_the_config_names()
    {
        ProjectVoice revoked = Cloned(v => v.ConsentRevokedAt = Now.AddHours(-1));
        var (guard, _) = await GuardWith(revoked);
        (await guard.IsReuseAllowedAsync(ProjectId, ConfigFor(revoked.Id), null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task The_cache_hit_site_asks_the_guard_with_the_cached_output_for_a_voiceover_and_never_for_another_step()
    {
        foreach (StepType type in new[] { StepType.Voiceover, StepType.VideoCompile })
        {
            Func<WorkflowEngineDbContext> db = NewDb();
            ExecutionResumeTests.Seeded s;
            using (WorkflowEngineDbContext seed = db())
                s = ExecutionResumeTests.Seed(seed, new ExecutionResumeTests.StepSpec(type, "S", st => st.VoiceoverConfigJson = ConfigFor(Guid.NewGuid())));
            string cached = OutputFor(Guid.NewGuid(), "r");
            var guard = new Mock<IVoiceReuseGuard>();
            guard.Setup(g => g.IsReuseAllowedAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

            List<string> ran = [];
            await ExecutionResumeTests.Service(new DbScopes(db, new HitCache(cached), guard.Object), null, null, null, cached: true,
                new ExecutionResumeTests.Executor(type, c => { ran.Add("live"); return Task.FromResult(ExecutionResumeTests.Done(c, "{}")); }))
                .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

            if (type == StepType.Voiceover)
            {
                guard.Verify(g => g.IsReuseAllowedAsync(s.ProjectId, It.Is<string?>(c => c != null), cached, It.IsAny<CancellationToken>()), Times.Once);
                ran.Should().Equal(["live"], "a refused hit is a miss");
            }
            else
            {
                guard.Verify(g => g.IsReuseAllowedAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
                ran.Should().BeEmpty("another step type is served from the cache whatever the guard would say");
            }
        }
    }

    [Fact]
    public async Task A_resume_whose_voiceover_reuse_is_refused_supersedes_the_stale_rows_and_runs_live_from_there()
    {
        ProjectVoice voice = Cloned();
        Func<WorkflowEngineDbContext> db = NewDb();
        ExecutionResumeTests.Seeded s;
        using (WorkflowEngineDbContext seed = db())
        {
            s = ExecutionResumeTests.Seed(seed, VoiceStep(voice.Id), new(StepType.Agent, "A2"));
            voice.ProjectId = s.ProjectId;
            seed.ProjectVoices.Add(voice);
            await seed.SaveChangesAsync();
        }

        var scopes = new DbScopes(db, null, new VoiceReuseGuard(new DbScopes(db)));
        string recorded = OutputFor(voice.Id, voice.RemoteVoiceId);
        Func<Task> first = () => ExecutionResumeTests.Service(scopes, null, null, null,
            new ExecutionResumeTests.Executor(StepType.Voiceover, c => Task.FromResult(ExecutionResumeTests.Done(c, recorded))),
            new ExecutionResumeTests.Executor(StepType.Agent, c => throw new InvalidOperationException("provider down")))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        await first.Should().ThrowAsync<Exception>();
        List<Guid> staleIds;
        using (WorkflowEngineDbContext rev = db())
        {
            (await rev.ProjectVoices.SingleAsync()).ConsentRevokedAt = DateTime.UtcNow;
            await rev.SaveChangesAsync();
            staleIds = await rev.WorkflowStepResults.Where(r => r.WorkflowExecutionId == s.ExecutionId).Select(r => r.Id).ToListAsync();
        }

        await ExecutionResumeTests.RequeueAsync(db, s.ExecutionId, resume: true);
        await ExecutionResumeTests.Service(scopes, null, null, null,
            new ExecutionResumeTests.Executor(StepType.Voiceover, c => Task.FromResult(ExecutionResumeTests.Done(c, recorded))),
            new ExecutionResumeTests.Executor(StepType.Agent, c => Task.FromResult(ExecutionResumeTests.Done(c, "{}"))))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        using WorkflowEngineDbContext read = db();
        List<WorkflowStepResult> stale = await read.WorkflowStepResults.Where(r => staleIds.Contains(r.Id)).ToListAsync();
        stale.Should().NotBeEmpty().And.OnlyContain(r => r.SupersededAt != null, "the live run replaces the old Voiceover row");
    }

    // The executor reads the config as VoiceoverStepConfig.Parse (last duplicate key wins, case-insensitive); the guard must reach the same decision.
    private static string RawConfig(string voiceKeys) =>
        "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"Hi\",\"startSec\":0}]},\"providerId\":\"" + Guid.NewGuid() + "\",\"model\":\"m\"," + voiceKeys + "}";

    [Fact]
    public async Task Duplicate_and_mixed_case_config_keys_are_read_the_way_the_executor_reads_them()
    {
        ProjectVoice usable = Cloned();
        ProjectVoice revoked = Cloned(v => v.ConsentRevokedAt = Now.AddHours(-1));
        var (guard, _) = await GuardWith(usable, revoked);
        string none = OutputFor(null, null);
        async Task<bool> Ok(string keys) => await guard.IsReuseAllowedAsync(ProjectId, RawConfig(keys), none, CancellationToken.None);

        // Last duplicate wins (what the executor resolves), whichever order they are in.
        (await Ok($"\"projectVoiceId\":\"{usable.Id}\",\"projectVoiceId\":\"{revoked.Id}\"")).Should().BeFalse();
        (await Ok($"\"projectVoiceId\":\"{revoked.Id}\",\"projectVoiceId\":\"{usable.Id}\"")).Should().BeTrue();
        // Differently-cased keys bind to the same property.
        (await Ok($"\"ProjectVoiceId\":\"{revoked.Id}\"")).Should().BeFalse();
        (await Ok($"\"projectvoiceid\":\"{revoked.Id}\"")).Should().BeFalse();
        (await Ok($"\"projectVoiceId\":\"{usable.Id}\",\"PROJECTVOICEID\":\"{revoked.Id}\"")).Should().BeFalse();
    }

    [Fact]
    public async Task A_config_the_executor_cannot_parse_blocks_reuse()
    {
        var (guard, _) = await GuardWith();
        string none = OutputFor(null, null);
        (await guard.IsReuseAllowedAsync(ProjectId, "{not json", none, CancellationToken.None)).Should().BeFalse();
        (await guard.IsReuseAllowedAsync(ProjectId, "null", none, CancellationToken.None)).Should().BeFalse();
        (await guard.IsReuseAllowedAsync(ProjectId, "{\"projectVoiceId\":\"not-a-guid\"}", none, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Vendor_ids_follow_the_rule_across_projects_in_the_guard_and_the_resolver()
    {
        ProjectVoice foreignClone = Cloned(projectId: Guid.NewGuid());
        ProjectVoice foreignLib = Cloned(v => { v.Source = ProjectVoiceSource.Library; v.InferenceProviderId = null; }, Guid.NewGuid());
        ProjectVoice own = Cloned(v => v.RemoteVoiceId = "shared");
        ProjectVoice foreignRevoked = Cloned(v => { v.RemoteVoiceId = "shared"; v.ConsentRevokedAt = Now.AddHours(-1); }, Guid.NewGuid());
        ProjectVoice otherOrgClone = Cloned(projectId: Guid.NewGuid());
        var (guard, db) = await GuardWithOrgs(new HashSet<Guid> { otherOrgClone.ProjectId }, null, null, foreignClone, foreignLib, own, foreignRevoked, otherOrgClone);
        string none = OutputFor(null, null);
        Task<bool> Ok(string vendor) => guard.IsReuseAllowedAsync(ProjectId, RawConfig($"\"defaultVoiceId\":\"{vendor}\""), none, CancellationToken.None);

        (await Ok(foreignClone.RemoteVoiceId)).Should().BeFalse("a clone's consent belongs to the project that attested it");
        (await Ok(foreignLib.RemoteVoiceId)).Should().BeTrue();
        (await Ok("shared")).Should().BeTrue("this project's own usable row decides");
        (await Ok("raw")).Should().BeTrue();
        (await Ok(otherOrgClone.RemoteVoiceId)).Should().BeTrue("a clone in another organization is never consulted, so its id cannot refuse this tenant's voice");

        var resolver = new ProjectVoiceResolver(new DbScopes(db));
        (await resolver.FindRefusedVendorVoiceIdsAsync(ProjectId, [foreignClone.RemoteVoiceId, foreignLib.RemoteVoiceId, otherOrgClone.RemoteVoiceId, "shared", "raw"], CancellationToken.None))
            .Should().Equal([foreignClone.RemoteVoiceId]);
    }

    // An own-project clone referenced ONLY by a config vendor id (DefaultVoiceId / a line's VoiceId): output voice-free, no ProjectVoiceId.
    private static string VendorConfig(string? defaultVoiceId, string? lineVoiceId) =>
        "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"Hi\",\"startSec\":0"
        + (lineVoiceId is null ? "" : $",\"voiceId\":\"{lineVoiceId}\"") + "}]},\"providerId\":\"" + Guid.NewGuid() + "\",\"model\":\"m\""
        + (defaultVoiceId is null ? "" : $",\"defaultVoiceId\":\"{defaultVoiceId}\"") + "}";

    [Fact]
    public async Task A_default_or_line_vendor_id_alone_blocks_reuse_when_the_own_clone_is_unusable_and_allows_it_otherwise()
    {
        ProjectVoice revoked = Cloned(v => { v.RemoteVoiceId = "rev"; v.ConsentRevokedAt = Now.AddHours(-1); });
        ProjectVoice expired = Cloned(v => { v.RemoteVoiceId = "exp"; v.RetentionUntil = Now.AddDays(-1); });
        ProjectVoice usable = Cloned(v => v.RemoteVoiceId = "ok");
        ProjectVoice library = Cloned(v => { v.RemoteVoiceId = "lib"; v.Source = ProjectVoiceSource.Library; v.InferenceProviderId = null; });
        var (guard, _) = await GuardWith(revoked, expired, usable, library);
        string voiceFree = OutputFor(null, null);
        Task<bool> Ok(string? def, string? line) => guard.IsReuseAllowedAsync(ProjectId, VendorConfig(def, line), voiceFree, CancellationToken.None);

        (await Ok("rev", null)).Should().BeFalse();
        (await Ok("exp", null)).Should().BeFalse();
        (await Ok(null, "rev")).Should().BeFalse();
        (await Ok(null, "exp")).Should().BeFalse();
        foreach (string id in new[] { "ok", "lib", "raw-unknown" })
        {
            (await Ok(id, null)).Should().BeTrue(id);
            (await Ok(null, id)).Should().BeTrue(id);
        }

        (await Ok("ok", "rev")).Should().BeFalse("only the line id is unusable, and it is enough");
        (await Ok("rev", "ok")).Should().BeFalse();
        (await Ok("ok", "lib")).Should().BeTrue();
    }

    [Fact]
    public async Task Foreign_clones_are_judged_by_organization_and_by_the_account_behind_their_capability()
    {
        // Another organization: (a) on a PLATFORM account -> refused; (b) on its own private account -> ignored;
        // (d) with no provider, or a capability that no longer resolves -> ignored (else an org could delete its provider to refuse an id for everyone).
        ProjectVoice onPlatform = Cloned(v => v.RemoteVoiceId = "plat", Guid.NewGuid());
        ProjectVoice onPrivate = Cloned(v => v.RemoteVoiceId = "priv", Guid.NewGuid());
        ProjectVoice foreignNull = Cloned(v => { v.RemoteVoiceId = "fnull"; v.InferenceProviderId = null; }, Guid.NewGuid());
        ProjectVoice foreignGone = Cloned(v => v.RemoteVoiceId = "fgone", Guid.NewGuid());
        // (c) The SAME organization: refused whatever the provider lookup returns.
        ProjectVoice sibNull = Cloned(v => { v.RemoteVoiceId = "snull"; v.InferenceProviderId = null; }, Guid.NewGuid());
        ProjectVoice sibGone = Cloned(v => v.RemoteVoiceId = "sgone", Guid.NewGuid());
        ProjectVoice sibLive = Cloned(v => v.RemoteVoiceId = "slive", Guid.NewGuid());
        var other = new HashSet<Guid> { onPlatform.ProjectId, onPrivate.ProjectId, foreignNull.ProjectId, foreignGone.ProjectId };
        var platform = new HashSet<Guid> { onPlatform.InferenceProviderId!.Value };
        var gone = new HashSet<Guid> { foreignGone.InferenceProviderId!.Value, sibGone.InferenceProviderId!.Value };
        var (guard, db) = await GuardWithOrgs(other, platform, gone, onPlatform, onPrivate, foreignNull, foreignGone, sibNull, sibGone, sibLive);
        var resolver = new ProjectVoiceResolver(new DbScopes(db));
        string none = OutputFor(null, null);
        async Task<bool> Ok(string vendor) => await guard.IsReuseAllowedAsync(ProjectId, RawConfig($"\"defaultVoiceId\":\"{vendor}\""), none, CancellationToken.None);

        (await Ok("plat")).Should().BeFalse();
        (await Ok("priv")).Should().BeTrue();
        (await Ok("fnull")).Should().BeTrue();
        (await Ok("fgone")).Should().BeTrue();
        foreach (string sib in new[] { "snull", "sgone", "slive" })
            (await Ok(sib)).Should().BeFalse(sib);

        string[] all = ["plat", "priv", "fnull", "fgone", "snull", "sgone", "slive"];
        (await resolver.FindRefusedVendorVoiceIdsAsync(ProjectId, all, CancellationToken.None))
            .Should().BeEquivalentTo(["plat", "snull", "sgone", "slive"]);

        // (e) A project the engine cannot resolve: every foreign clone whose account resolves is refused (conservative); the rest stay ignored.
        (await resolver.FindRefusedVendorVoiceIdsAsync(Guid.NewGuid(), all, CancellationToken.None))
            .Should().BeEquivalentTo(["plat", "priv", "slive"]);
    }
}
