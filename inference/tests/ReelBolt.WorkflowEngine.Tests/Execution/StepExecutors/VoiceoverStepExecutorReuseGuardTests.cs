using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;
using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;

public partial class VoiceoverStepExecutorTests
{
    private static ProjectVoice VoiceRow(Guid id, string remote, ProjectVoiceSource source = ProjectVoiceSource.Cloned) => new()
    {
        Id = id,
        ProjectId = ProjectId,
        InferenceProviderId = source == ProjectVoiceSource.Cloned ? Guid.NewGuid() : null,
        Name = "v",
        Source = source,
        RemoteVoiceId = remote,
        ConsentAttestedAt = source == ProjectVoiceSource.Cloned ? DateTime.UtcNow.AddDays(-1) : null,
        ConsentAttestedByUserId = source == ProjectVoiceSource.Cloned ? Guid.NewGuid() : null,
        ConsentStatement = source == ProjectVoiceSource.Cloned ? "ok" : null,
        ConsentSubjectName = source == ProjectVoiceSource.Cloned ? "s" : null,
        CreatedAt = DateTime.UtcNow.AddDays(-1)
    };

    private static ResolvedProjectVoice Resolved(ProjectVoice v) =>
        new(v.Id, v.Name, "d", v.Source, v.InferenceProviderId, v.RemoteVoiceId, true, null);

    private static (VoiceReuseGuard Guard, Func<WorkflowEngineDbContext> Db) GuardOver(params ProjectVoice[] rows)
    {
        string name = "vr-" + Guid.NewGuid().ToString("N");
        Func<WorkflowEngineDbContext> db = () => new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(name).Options);
        using (WorkflowEngineDbContext seed = db())
        {
            seed.ProjectVoices.AddRange(rows);
            VoiceReuseGuardTests.SeedProjects(seed, ProjectId, rows);
            seed.SaveChanges();
        }

        return (new VoiceReuseGuard(new VoiceReuseGuardTests.DbScopes(db)), db);
    }

    private static async Task MutateAsync(Func<WorkflowEngineDbContext> db, Guid id, Action<WorkflowEngineDbContext, ProjectVoice> change)
    {
        using WorkflowEngineDbContext c = db();
        change(c, await c.ProjectVoices.SingleAsync(v => v.Id == id));
        await c.SaveChangesAsync();
    }

    private static Task<bool> Allowed(VoiceReuseGuard g, string config, string output) =>
        g.IsReuseAllowedAsync(ProjectId, config, output, CancellationToken.None);

    private static async Task<string> RealOutput(string config, IProjectVoiceResolver resolver, IDecisionGate? gate = null)
    {
        VoiceoverFakes fakes = CreateFakes();
        SetupSuccessfulSynthesis(fakes, []);
        StepExecutionResult r = await CreateExecutorWithVoices(fakes, resolver, gate).ExecuteAsync(CreateContext(config));
        r.Status.Should().Be(StepStatus.Completed, because: r.ErrorDetails ?? r.Output);
        return r.Output;
    }

    [Fact]
    public async Task Reuse_guard_blocks_a_real_project_voice_output_after_revocation_or_deletion()
    {
        ProjectVoice row = VoiceRow(Guid.NewGuid(), "fish-1");
        var resolver = new StubVoiceResolver();
        resolver.Voices[row.Id] = Resolved(row);
        string config = OneLineConfigJson(row.Id);
        string output = await RealOutput(config, resolver);

        var (guard, db) = GuardOver(row);
        (await Allowed(guard, config, output)).Should().BeTrue();

        await MutateAsync(db, row.Id, (_, v) => v.ConsentRevokedAt = DateTime.UtcNow);
        (await Allowed(guard, config, output)).Should().BeFalse();

        await MutateAsync(db, row.Id, (c, v) => { v.ConsentRevokedAt = null; c.ProjectVoices.Remove(v); });
        (await Allowed(guard, config, output)).Should().BeFalse("a deleted voice is not found");
    }

    private static async Task<(string Output, ProjectVoice A, ProjectVoice B)> CastOutput()
    {
        ProjectVoice a = VoiceRow(Guid.NewGuid(), "voice-a"), b = VoiceRow(Guid.NewGuid(), "voice-b");
        var resolver = new StubVoiceResolver();
        resolver.Voices[a.Id] = Resolved(a);
        resolver.Voices[b.Id] = Resolved(b);
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionGateOutcome(true, true,
                new DecisionResult("jev", new Dictionary<string, DecisionAnswer>
                {
                    ["voice"] = new("voice", DecisionKind.Choice, b.Id.ToString("D"), null, null, 0.93, new Dictionary<string, double>())
                }, 10, 2),
                new Dictionary<string, (double Top, double Second)> { ["voice"] = (0.93, 0.07) }));
        return (await RealOutput(OneLineConfigJson(casting: VoiceCastingMode.Decision), resolver, gate.Object), a, b);
    }

    [Fact]
    public async Task Reuse_guard_blocks_a_real_cast_voice_output_when_the_cast_voice_is_revoked_or_deleted()
    {
        string config = OneLineConfigJson(casting: VoiceCastingMode.Decision);
        var (output, a, b) = await CastOutput();
        var (guard, db) = GuardOver(a, b);
        (await Allowed(guard, config, output)).Should().BeTrue();

        await MutateAsync(db, b.Id, (_, v) => v.ConsentRevokedAt = DateTime.UtcNow);
        (await Allowed(guard, config, output)).Should().BeFalse();

        await MutateAsync(db, b.Id, (c, v) => { v.ConsentRevokedAt = null; c.ProjectVoices.Remove(v); });
        (await Allowed(guard, config, output)).Should().BeFalse("the recorded casting.chosenVoiceId no longer resolves");
    }

    [Fact]
    public async Task Reuse_guard_finds_an_old_cast_output_without_a_chosen_id_by_its_remote_voice_id()
    {
        string config = OneLineConfigJson(casting: VoiceCastingMode.Decision);
        var (output, a, b) = await CastOutput();
        JsonNode node = JsonNode.Parse(output)!;
        node["meta"]!["voice"]!["casting"]!.AsObject().Remove("chosenVoiceId");
        string old = node.ToJsonString();

        var (guard, db) = GuardOver(a, b);
        (await Allowed(guard, config, old)).Should().BeTrue();

        await MutateAsync(db, b.Id, (c, v) => c.ProjectVoices.Remove(v));
        (await Allowed(guard, config, old)).Should().BeFalse("a cast voice that matches no row must not read as voice-less");
    }

    [Fact]
    public async Task Reuse_guard_allows_default_voice_and_library_voice_outputs()
    {
        string config = OneLineConfigJson();
        string defaultVoice = await RealOutput(config, new StubVoiceResolver());
        JsonNode.Parse(defaultVoice)!["meta"]!["voice"]!["source"]!.GetValue<string>().Should().Be("default_voice_id");
        (await Allowed(GuardOver().Guard, config, defaultVoice)).Should().BeTrue("a raw vendor id that matches no row is not a project voice");

        ProjectVoice lib = VoiceRow(Guid.NewGuid(), "lib-1", ProjectVoiceSource.Library);
        var resolver = new StubVoiceResolver();
        resolver.Voices[lib.Id] = Resolved(lib);
        string libConfig = OneLineConfigJson(lib.Id);
        string libOutput = await RealOutput(libConfig, resolver);
        (await Allowed(GuardOver(lib).Guard, libConfig, libOutput)).Should().BeTrue();
    }

    [Fact]
    public async Task Reuse_guard_fails_closed_on_an_output_that_is_not_a_voiceover_result()
    {
        var (guard, _) = GuardOver();
        string config = OneLineConfigJson();
        (await Allowed(guard, config, "not json at all")).Should().BeFalse();
        (await Allowed(guard, config, "{\"voice\":{\"source\":\"provider_default\"}}")).Should().BeFalse("no meta, so it cannot be shown voice-free");
        (await Allowed(guard, config, "{\"skipped\":true,\"reason\":\"silent\"}")).Should().BeTrue();
    }

    // ------------------------------------------------------------------ vendor-id consent at run time

    private static string VoiceIdConfig(string? defaultVoiceId, string? lineVoiceId) =>
        SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[] { new VoiceoverLine("Hello there.", 0.0, lineVoiceId) }),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: defaultVoiceId,
            MaxBytes: 2_000_000));

    private static async Task<(StepExecutionResult Result, List<SpeechRequest> Requests, VoiceoverFakes Fakes)> RunWithVendorIds(
        string config, StubVoiceResolver? resolver)
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(config));
        return (result, requests, fakes);
    }

    [Fact]
    public async Task A_DefaultVoiceId_that_is_a_revoked_clones_vendor_id_fails_the_step_before_any_synthesis()
    {
        var resolver = new StubVoiceResolver();
        resolver.Refused.Add("clone-vendor");
        var (result, requests, _) = await RunWithVendorIds(VoiceIdConfig("clone-vendor", null), resolver);

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_per_line_VoiceId_that_is_an_expired_clones_vendor_id_fails_the_step()
    {
        var resolver = new StubVoiceResolver();
        resolver.Refused.Add("old-clone");
        var (result, requests, _) = await RunWithVendorIds(VoiceIdConfig(null, "old-clone"), resolver);

        result.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        requests.Should().BeEmpty();
        resolver.AskedRemote.Should().Contain("old-clone");
    }

    [Fact]
    public async Task Vendor_ids_of_a_usable_clone_a_library_row_and_a_raw_id_all_pass()
    {
        var resolver = new StubVoiceResolver();

        foreach (string id in new[] { "usable-clone", "lib-voice", "raw-vendor-id-with-no-row" })
        {
            var (result, requests, _) = await RunWithVendorIds(VoiceIdConfig(id, null), resolver);
            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            requests.Should().ContainSingle().Which.VoiceId.Should().Be(id);
        }
    }

    [Fact]
    public async Task A_vendor_id_with_no_resolver_fails_closed()
    {
        var (result, requests, _) = await RunWithVendorIds(VoiceIdConfig("some-voice", null), resolver: null);
        result.ErrorDetails.Should().StartWith("VOICE_RESOLVER_UNAVAILABLE");
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_revoked_clones_audio_in_the_object_store_is_not_served()
    {
        var resolver = new StubVoiceResolver();
        resolver.Refused.Add("clone-vendor");
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        fakes.Workspace
            .Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver)
            .ExecuteAsync(CreateContext(VoiceIdConfig("clone-vendor", null)));

        result.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        fakes.Workspace.Verify(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reuse_guard_covers_the_configs_default_and_line_vendor_ids()
    {
        ProjectVoice revoked = VoiceRow(Guid.NewGuid(), "clone-vendor");
        revoked.ConsentRevokedAt = DateTime.UtcNow;
        var (guard, _) = GuardOver(revoked);
        string plain = "{\"view\":{\"lines\":[]},\"meta\":{\"voice\":{\"remoteVoiceId\":\"clone-vendor\",\"source\":\"default_voice_id\",\"casting\":{\"applied\":false}}}}";
        string noVoice = "{\"view\":{\"lines\":[]},\"meta\":{\"totalLines\":0}}";

        (await Allowed(guard, VoiceIdConfig("clone-vendor", null), noVoice)).Should().BeFalse("config defaultVoiceId");
        (await Allowed(guard, VoiceIdConfig(null, "clone-vendor"), noVoice)).Should().BeFalse("config line voiceId");
        (await Allowed(guard, VoiceIdConfig("other", null), plain)).Should().BeFalse("recorded remote id");
        (await Allowed(guard, VoiceIdConfig("other", "raw"), noVoice)).Should().BeTrue();
    }

    [Fact]
    public async Task The_real_resolver_in_the_executor_refuses_a_same_org_sibling_clone_and_ignores_another_orgs()
    {
        ProjectVoice sibling = VoiceRow(Guid.NewGuid(), "sib-vendor");
        sibling.ProjectId = Guid.NewGuid();
        ProjectVoice otherOrg = VoiceRow(Guid.NewGuid(), "alien-vendor");
        otherOrg.ProjectId = Guid.NewGuid();
        ProjectVoice platform = VoiceRow(Guid.NewGuid(), "platform-vendor");
        platform.ProjectId = otherOrg.ProjectId;
        platform.InferenceProviderId = Guid.NewGuid();
        string name = "vr-" + Guid.NewGuid().ToString("N");
        Func<WorkflowEngineDbContext> db = () => new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(name).Options);
        using (WorkflowEngineDbContext seed = db())
        {
            seed.ProjectVoices.AddRange(sibling, otherOrg, platform);
            VoiceReuseGuardTests.SeedProjects(seed, ProjectId, [sibling, otherOrg, platform], new HashSet<Guid> { otherOrg.ProjectId }, new HashSet<Guid> { platform.InferenceProviderId!.Value });
            seed.SaveChanges();
        }

        var resolver = new ProjectVoiceResolver(new VoiceReuseGuardTests.DbScopes(db));
        var (refused, _, _) = await RunWithRealResolver(VoiceIdConfig("sib-vendor", null), resolver);
        refused.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        (await RunWithRealResolver(VoiceIdConfig("platform-vendor", null), resolver)).Result.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        var (allowed, requests, _) = await RunWithRealResolver(VoiceIdConfig("alien-vendor", null), resolver);
        allowed.Status.Should().Be(StepStatus.Completed, because: allowed.ErrorDetails ?? allowed.Output);
        requests.Should().ContainSingle();
    }

    private static async Task<(StepExecutionResult Result, List<SpeechRequest> Requests, VoiceoverFakes Fakes)> RunWithRealResolver(string config, IProjectVoiceResolver resolver)
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        return (await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(config)), requests, fakes);
    }
}

/// <summary>A resolver whose project has no voice rows: every vendor id is a raw id.</summary>
internal sealed class NoRowsVoiceResolver : IProjectVoiceResolver
{
    public Task<ResolvedProjectVoice> ResolveAsync(Guid projectId, Guid projectVoiceId, CancellationToken ct) =>
        Task.FromResult(ResolvedProjectVoice.NotFound(projectVoiceId));

    public Task<IReadOnlyList<string>> FindRefusedVendorVoiceIdsAsync(Guid projectId, IReadOnlyCollection<string> remoteVoiceIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<ResolvedProjectVoice>> ListSelectableAsync(Guid projectId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ResolvedProjectVoice>>([]);
}
