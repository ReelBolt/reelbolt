using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// D6's acceptance test, exactly as the plan words it: "Table-driven selector tests in
/// ReelBolt.WorkflowEngine.Tests. With <c>Compute:Mode=Local</c> every decision is Local and existing
/// tests are unchanged."
/// <para>
/// The table below is the whole decision function. Each row fixes the deployment mode, the
/// organization's preference, the plan entitlement and what the gateway says is online, and asserts
/// which target comes out — including the two rows that must FAIL the step rather than route it.
/// </para>
/// </summary>
public class ComputeTargetSelectorTests
{
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-000000000002");

    private static ComputeTargetRequest Request(
        RunnerJobType jobType = RunnerJobType.VideoCompile,
        bool runnerEligible = true)
        => new(Execution, Org, jobType, StepId: Guid.Parse("5e000000-0000-4000-8000-000000000003"), StepType: "VideoCompile", Iteration: 0, Purpose: "video.compile", RunnerEligible: runnerEligible);

    /// <summary>A gateway that answers "one desktop runner, all job types" for the runner query and never sees anything else.</summary>
    private static StubHttpMessageHandler RunnerOnlineHandler()
        => new(_ => Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.RunnersBody(("Desktop", Org, ComputeTestFactory.AllJobTypes)))));

    /// <summary>A gateway whose internal surface is closed: D5b's 503 when no RUNNER_INTERNAL_TOKEN is configured.</summary>
    private static StubHttpMessageHandler FabricNotDeployedHandler()
        => new(_ => Task.FromResult(ComputeTestFactory.Json("""{"error":"internal_api_disabled"}""", HttpStatusCode.ServiceUnavailable)));

    /// <summary>A gateway that is not listening at all.</summary>
    private static StubHttpMessageHandler UnreachableHandler()
        => new(_ => throw new HttpRequestException("connection refused"));

    [Fact]
    public async Task LocalMode_IsLocalForEveryPreference_WithoutReadingAnything()
    {
        // The zero-config self-host contract, asserted in the strongest available form: the
        // entitlements port throws if it is asked, and the gateway handler throws if it is reached.
        StubHttpMessageHandler gateway = new(_ => throw new InvalidOperationException("Compute:Mode=Local must not call the gateway"));
        ComputeOptions options = ComputeTestFactory.Options(ComputeMode.Local);

        RunnerGatewayClient client = ComputeTestFactory.Gateway(gateway, options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(new ForbiddenComputeEntitlements(), client, options);

        foreach (ComputePreference preference in Enum.GetValues<ComputePreference>())
        {
            _ = preference;
            ComputeDecision decision = await selector.ResolveAsync(Request());

            decision.Target.Should().Be(ComputeTargetKind.Local);
            decision.Reason.Should().Be(ComputeDecisionReasons.LocalMode);
            decision.IsFailure.Should().BeFalse();
        }

        gateway.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task LocalMode_IgnoresARunnerOnlyPreferenceThatWouldOtherwiseFailTheStep()
    {
        // The single most important row of the table: turning the fabric on is what makes
        // "runner or nothing" meaningful. Leaving it off must never fail a step.
        ComputeOptions options = ComputeTestFactory.Options(ComputeMode.Local);
        RunnerGatewayClient client = ComputeTestFactory.Gateway(UnreachableHandler(), options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.RunnerOnly, RunnerAllowed: true, CloudPoolAllowed: false)),
            client,
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.Target.Should().Be(ComputeTargetKind.Local);
        decision.IsFailure.Should().BeFalse();
    }

    public static TheoryData<ComputePreference, bool, bool, bool, ComputeTargetKind, string, string?> DecisionTable => new()
    {
        // preference,                       runnerAllowed, cloudAllowed, runnerOnline -> target,        reason,                                          failureCode
        { ComputePreference.CloudOnly,       true,  true,  true,  ComputeTargetKind.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly, null },
        { ComputePreference.CloudOnly,       false, true,  true,  ComputeTargetKind.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly, null },
        { ComputePreference.PreferRunner,    true,  true,  true,  ComputeTargetKind.OrgRunner, ComputeDecisionReasons.PreferenceRunner, null },
        { ComputePreference.PreferRunner,    true,  true,  false, ComputeTargetKind.CloudPool, ComputeDecisionReasons.RunnerUnavailableCloudFallback, null },
        { ComputePreference.Auto,            true,  true,  true,  ComputeTargetKind.OrgRunner, ComputeDecisionReasons.AutoWithRunner, null },
        { ComputePreference.Auto,            true,  true,  false, ComputeTargetKind.CloudPool, ComputeDecisionReasons.AutoWithoutRunner, null },
        { ComputePreference.Auto,            false, true,  true,  ComputeTargetKind.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly, null },

        // RunnerOnly with no runner online: the step FAILS. Nothing falls back to cloud billing silently.
        { ComputePreference.RunnerOnly,      true,  true,  false, ComputeTargetKind.Local, ComputeDecisionReasons.NoRunnerOnline, ComputeFailureCodes.NoRunnerOnline },
        { ComputePreference.RunnerOnly,      true,  false, false, ComputeTargetKind.Local, ComputeDecisionReasons.NoRunnerOnline, ComputeFailureCodes.NoRunnerOnline },
        { ComputePreference.RunnerOnly,      true,  true,  true,  ComputeTargetKind.OrgRunner, ComputeDecisionReasons.PreferenceRunner, null },

        // PreferRunner with neither a runner nor a pool: fail, with the runner code (that is what was asked for).
        { ComputePreference.PreferRunner,    true,  false, false, ComputeTargetKind.Local, ComputeDecisionReasons.NoRunnerOnline, ComputeFailureCodes.NoRunnerOnline },

        // Entitled to nothing at all.
        { ComputePreference.CloudOnly,       false, false, false, ComputeTargetKind.Local, ComputeDecisionReasons.NotAvailable, ComputeFailureCodes.ComputeNotAvailable },
        { ComputePreference.Auto,            false, false, true,  ComputeTargetKind.Local, ComputeDecisionReasons.NotAvailable, ComputeFailureCodes.ComputeNotAvailable }
    };

    [Theory]
    [MemberData(nameof(DecisionTable))]
    public async Task Selector_FollowsTheDecisionTable(
        ComputePreference preference,
        bool runnerAllowed,
        bool cloudAllowed,
        bool runnerOnline,
        ComputeTargetKind expectedTarget,
        string expectedReason,
        string? expectedFailureCode)
    {
        // The table is the pure decision function, so a pool worker is always online here: the
        // "pool has no worker" rows are the PoolEmpty_* tests below.
        ComputeOptions options = ComputeTestFactory.Options();
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(
            runnerOnline
                ? ComputeTestFactory.RunnersBody(("Desktop", Org, ComputeTestFactory.AllJobTypes), ("CloudPool", null, ComputeTestFactory.AllJobTypes))
                : ComputeTestFactory.RunnersBody(("CloudPool", null, ComputeTestFactory.AllJobTypes)))));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(preference, runnerAllowed, cloudAllowed)),
            client,
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.Target.Should().Be(expectedTarget);
        decision.Reason.Should().Be(expectedReason);
        decision.FailureCode.Should().Be(expectedFailureCode);
        decision.IsFailure.Should().Be(expectedFailureCode is not null);
    }

    [Fact]
    public async Task StepThatIsNotRunnerEligible_RunsLocally_AndDoesNotFail()
    {
        // The plan's one named case: VideoAnalyze with TrackObjects is platform-only in v1, so the
        // step runs where it always has rather than failing a workflow a self-hoster can run today.
        ComputeOptions options = ComputeTestFactory.Options();
        RunnerGatewayClient client = ComputeTestFactory.Gateway(RunnerOnlineHandler(), options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.RunnerOnly, true, false)),
            client,
            options);

        ComputeDecision decision = await selector.ResolveAsync(
            Request(RunnerJobType.VideoAnalyzeExtract, runnerEligible: false));

        decision.Target.Should().Be(ComputeTargetKind.Local);
        decision.Reason.Should().Be(ComputeDecisionReasons.StepNotEligible);
        decision.IsFailure.Should().BeFalse();
    }

    [Fact]
    public async Task FabricNotDeployed_FallsBackToLocal_ForPreferRunnerAndAuto()
    {
        // D5b: with no RUNNER_INTERNAL_TOKEN the whole internal surface answers 503. That is a
        // deployment fact, not an error, so the step still runs rather than failing the workflow.
        foreach (ComputePreference preference in new[] { ComputePreference.Auto, ComputePreference.PreferRunner, ComputePreference.CloudOnly })
        {
            ComputeOptions options = ComputeTestFactory.Options();
            RunnerGatewayClient client = ComputeTestFactory.Gateway(FabricNotDeployedHandler(), options);
            ComputeTargetSelector selector = ComputeTestFactory.Selector(
                new FixedComputeEntitlements(new ComputeEntitlements(preference, true, true)),
                client,
                options);

            ComputeDecision decision = await selector.ResolveAsync(Request());

            // CloudOnly asks the gateway too now (it must know whether the pool has a worker), so an
            // absent gateway reads the same for every preference: no pool to send the job to, so Local.
            decision.Target.Should().Be(ComputeTargetKind.Local);
            decision.Reason.Should().Be(ComputeDecisionReasons.FabricUnavailable);
            decision.IsFailure.Should().BeFalse();
        }
    }

    [Fact]
    public async Task FabricNotDeployed_StillFailsRunnerOnly()
    {
        // "DesktopOnly with no runner online fails the step": a gateway we cannot reach has no runner
        // online, so the promise the user asked for is kept instead of quietly running the work here.
        ComputeOptions options = ComputeTestFactory.Options();
        RunnerGatewayClient client = ComputeTestFactory.Gateway(FabricNotDeployedHandler(), options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.RunnerOnly, true, true)),
            client,
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.IsFailure.Should().BeTrue();
        decision.FailureCode.Should().Be(ComputeFailureCodes.NoRunnerOnline);
    }

    [Fact]
    public async Task AnUnreachableGateway_ReadsTheSameAsADeploymentWithoutOne()
    {
        ComputeOptions options = ComputeTestFactory.Options();
        RunnerGatewayClient client = ComputeTestFactory.Gateway(UnreachableHandler(), options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(ComputeEntitlements.Unlimited),
            client,
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.Target.Should().Be(ComputeTargetKind.Local);
        decision.Reason.Should().Be(ComputeDecisionReasons.FabricUnavailable);
    }

    [Fact]
    public async Task ARefusedRunnerQuery_IsTreatedAsNoRunnerOnline_NotAsAFailure()
    {
        // A 401 from a token mismatch is a misconfiguration worth a log line, but it must not fail a
        // user's workflow: the conservative reading is "no runner confirmed", which for RunnerOnly is
        // the coded NO_RUNNER_ONLINE and for Auto is the engine's own host (the same query that could
        // not confirm a runner could not confirm a pool worker either).
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized)));
        ComputeOptions options = ComputeTestFactory.Options();
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.Auto, true, true)),
            client,
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.Target.Should().Be(ComputeTargetKind.Local);
        decision.Reason.Should().Be(ComputeDecisionReasons.PoolEmptyLocalFallback);
        decision.IsFailure.Should().BeFalse();
    }

    [Fact]
    public async Task ACloudPoolRunnerReturnedByAnOrgScopedQuery_IsNotTheOrgsRunner()
    {
        // The gateway's org filter keeps entries whose OrgID is EMPTY, and a pool worker's org is
        // null. Counting one as "this org has a runner" would send the org's work to our own pool
        // while reporting it as the customer's own compute.
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(
            ComputeTestFactory.RunnersBody(("CloudPool", null, ComputeTestFactory.AllJobTypes)))));
        ComputeOptions options = ComputeTestFactory.Options();
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.Auto, true, true)),
            client,
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.Target.Should().Be(ComputeTargetKind.CloudPool);
        decision.Reason.Should().Be(ComputeDecisionReasons.AutoWithoutRunner);
    }

    [Fact]
    public async Task ARunnerWithoutTheJobsCapability_IsNotEligible()
    {
        // The capability document is what decides: a runner that cannot do video.compile is not an
        // eligible runner for a video.compile step, however online it is.
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(
            ComputeTestFactory.RunnersBody(("Desktop", Org, ["video.analyze.extract"]), ("CloudPool", null, ComputeTestFactory.AllJobTypes)))));
        ComputeOptions options = ComputeTestFactory.Options();
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.Auto, true, true)),
            client,
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request(RunnerJobType.VideoCompile));

        decision.Target.Should().Be(ComputeTargetKind.CloudPool);
        decision.Reason.Should().Be(ComputeDecisionReasons.AutoWithoutRunner);
    }

    [Fact]
    public async Task RunnerPresence_IsCachedForFiveSeconds_AndReReadAfterwards()
    {
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.RunnersBody())));
        ComputeOptions options = ComputeTestFactory.Options(runnerQueryCacheSeconds: 5);
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options);
        TestTimeProvider time = new(DateTimeOffset.UnixEpoch);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(ComputeEntitlements.Unlimited), client, options, time);

        await selector.ResolveAsync(Request());
        await selector.ResolveAsync(Request());
        handler.Requests.Count.Should().Be(1, "the plan's cache window is 5 s");

        time.Advance(TimeSpan.FromSeconds(4));
        await selector.ResolveAsync(Request());
        handler.Requests.Count.Should().Be(1, "still inside the window");

        time.Advance(TimeSpan.FromSeconds(2));
        await selector.ResolveAsync(Request());
        handler.Requests.Count.Should().Be(2, "past the window, so the gateway is asked again");
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Contain("org=").And.Contain("caps=video.compile");
    }

    [Fact]
    public async Task TheEntitlementsAreReadPerDecision_NotCached()
    {
        // A downgrade takes effect on the next read (IOrganizationEntitlements' own contract), so the
        // selector must not memoise it: the only cache in D6 is the 5-second runner presence one.
        ComputeOptions options = ComputeTestFactory.Options();
        FixedComputeEntitlements entitlements = new(ComputeEntitlements.Unlimited);
        RunnerGatewayClient client = ComputeTestFactory.Gateway(RunnerOnlineHandler(), options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(entitlements, client, options);

        await selector.ResolveAsync(Request());
        await selector.ResolveAsync(Request());

        entitlements.Calls.Should().Be(2);
    }

    [Fact]
    public void EffectivePreference_DowngradesRunnerOnlyWhenThePlanForbidsRunners()
    {
        new ComputeEntitlements(ComputePreference.RunnerOnly, RunnerAllowed: false, CloudPoolAllowed: true)
            .EffectivePreference.Should().Be(ComputePreference.CloudOnly);

        new ComputeEntitlements(ComputePreference.RunnerOnly, RunnerAllowed: true, CloudPoolAllowed: false)
            .EffectivePreference.Should().Be(ComputePreference.RunnerOnly);

        ComputeEntitlements.Unlimited.EffectivePreference.Should().Be(ComputePreference.Auto);
    }

    [Fact]
    public async Task ModeIsTheDeploymentSwitch_NotThePreference()
    {
        // Turning the mode on with the default preference must be enough to reach a paired runner:
        // Auto + a runner online is OrgRunner, with no per-org configuration at all.
        ComputeOptions options = ComputeTestFactory.Options();
        RunnerGatewayClient client = ComputeTestFactory.Gateway(RunnerOnlineHandler(), options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(ComputeEntitlements.Unlimited), client, options);

        selector.Mode.Should().Be(ComputeMode.Fabric);
        (await selector.ResolveAsync(Request())).Target.Should().Be(ComputeTargetKind.OrgRunner);
    }

    [Theory]
    [InlineData(ComputePreference.Auto)]
    [InlineData(ComputePreference.PreferRunner)]
    [InlineData(ComputePreference.CloudOnly)]
    public async Task PoolEmpty_RunsLocally_InsteadOfQueueingAJobNobodyCanTake(ComputePreference preference)
    {
        // The cloud deployment's real situation: Compute:Mode=Fabric, the gateway is up, no desktop
        // runner is paired and no pool worker was ever enrolled. A CloudPool job here would wait out
        // SessionWaitSeconds and fail the step ("no runner was assigned"); the engine's own host,
        // which already runs the sandbox, is the machine that can do the work.
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.RunnersBody())));
        ComputeOptions options = ComputeTestFactory.Options();
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(preference, RunnerAllowed: true, CloudPoolAllowed: true)),
            ComputeTestFactory.Gateway(handler, options),
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.Target.Should().Be(ComputeTargetKind.Local);
        decision.Reason.Should().Be(ComputeDecisionReasons.PoolEmptyLocalFallback);
        decision.IsFailure.Should().BeFalse();
    }

    [Fact]
    public async Task PoolEmpty_StillFailsRunnerOnly_WithNoRunner()
    {
        // The fallback is for work the platform runs. RunnerOnly asked for the user's own machine, so
        // an empty pool changes nothing: the step still fails with the coded error.
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.RunnersBody())));
        ComputeOptions options = ComputeTestFactory.Options();
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.RunnerOnly, RunnerAllowed: true, CloudPoolAllowed: true)),
            ComputeTestFactory.Gateway(handler, options),
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.IsFailure.Should().BeTrue();
        decision.FailureCode.Should().Be(ComputeFailureCodes.NoRunnerOnline);
    }

    [Fact]
    public async Task PoolEmpty_DoesNotStopAnOnlineDesktopRunnerFromTakingTheWork()
    {
        ComputeOptions options = ComputeTestFactory.Options();
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(ComputeEntitlements.Unlimited),
            ComputeTestFactory.Gateway(RunnerOnlineHandler(), options),
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.Target.Should().Be(ComputeTargetKind.OrgRunner);
        decision.Reason.Should().Be(ComputeDecisionReasons.AutoWithRunner);
    }

    [Fact]
    public async Task APoolWorkerThatCannotDoTheJobType_DoesNotCountAsAPool()
    {
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(
            ComputeTestFactory.RunnersBody(("CloudPool", null, ["video.analyze.extract"])))));
        ComputeOptions options = ComputeTestFactory.Options();
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(ComputeEntitlements.Unlimited),
            ComputeTestFactory.Gateway(handler, options),
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request(RunnerJobType.VideoCompile));

        decision.Target.Should().Be(ComputeTargetKind.Local);
        decision.Reason.Should().Be(ComputeDecisionReasons.PoolEmptyLocalFallback);
    }

    [Fact]
    public async Task WithLocalWhenPoolEmptyOff_AnEmptyPoolStaysACloudPoolJob_AndCloudOnlyNeverAsksTheGateway()
    {
        // The strict behaviour, for a deployment whose pool is expected to have workers.
        StubHttpMessageHandler handler = new(_ => throw new InvalidOperationException("CloudOnly with the fallback off must not call the gateway"));
        ComputeOptions options = ComputeTestFactory.Options();
        options.LocalWhenPoolEmpty = false;
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.CloudOnly, RunnerAllowed: true, CloudPoolAllowed: true)),
            ComputeTestFactory.Gateway(handler, options),
            options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.Target.Should().Be(ComputeTargetKind.CloudPool);
        decision.Reason.Should().Be(ComputeDecisionReasons.PreferenceCloudOnly);
        handler.Requests.Should().BeEmpty();

        ComputeTargetSelector auto = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(ComputeEntitlements.Unlimited),
            ComputeTestFactory.Gateway(new StubHttpMessageHandler(_ => Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.RunnersBody()))), options),
            options);
        (await auto.ResolveAsync(Request())).Target.Should().Be(ComputeTargetKind.CloudPool);
    }

    [Theory]
    [InlineData(RunnerJobType.VideoCompile, "video.compile")]
    [InlineData(RunnerJobType.VideoAnalyzeExtract, "video.analyze.extract")]
    [InlineData(RunnerJobType.RemotionWorkspaceSession, "remotion.workspace-session")]
    public void JobTypesMapToTheProtocolVocabulary(RunnerJobType jobType, string wire)
    {
        ComputeJobTypes.ToWire(jobType).Should().Be(wire);
        ComputeJobTypes.TryParseWire(wire).Should().Be(jobType);
        ComputeJobTypes.TryParseWire("something.new").Should().BeNull("an unknown value is an unknown case, never a parse error");
    }

    [Fact]
    public void TargetsRoundTripBetweenTheTwoVocabularies()
    {
        foreach (RunnerJobTarget target in Enum.GetValues<RunnerJobTarget>())
        {
            ComputeTargets.ToKind(target).Should().Be(target == RunnerJobTarget.CloudPool ? ComputeTargetKind.CloudPool : ComputeTargetKind.OrgRunner);
            ComputeTargets.TryParseWire(ComputeTargets.ToWire(target), out RunnerJobTarget parsed).Should().BeTrue();
            parsed.Should().Be(target);
        }

        ComputeTargets.ToJobTarget(ComputeTargetKind.Local).Should().BeNull("Local has no protocol representation");
        ComputeTargetKind.Local.Should().Be(ComputeTargetKind.Local);
    }

    [Fact]
    public void ComputeModeIsAppendOnlyWithLocalAsItsDefault()
    {
        ((int)ComputeMode.Local).Should().Be(0);
        new ComputeOptions().Mode.Should().Be(ComputeMode.Local, "self-host must keep working with no new configuration");
    }

    [Fact]
    public void TheInternalTokenPrefersConfigurationOverTheEnvironmentVariable()
    {
        new ComputeOptions { InternalToken = "  from-config  " }.ResolveInternalToken().Should().Be("from-config");
        new ComputeOptions { InternalToken = "   " }.ResolveInternalToken()
            .Should().Be(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RUNNER_INTERNAL_TOKEN"))
                ? null
                : Environment.GetEnvironmentVariable("RUNNER_INTERNAL_TOKEN")!.Trim());
    }

    [Fact]
    public async Task TheDecisionCarriesNoTargetForALocalAnswerAndNeverAUrlOrAToken()
    {
        // A decision is a value the executor records; it must not become a place a secret can leak
        // into a step result.
        ComputeOptions options = ComputeTestFactory.Options();
        RunnerGatewayClient client = ComputeTestFactory.Gateway(RunnerOnlineHandler(), options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(ComputeEntitlements.Unlimited), client, options);

        ComputeDecision decision = await selector.ResolveAsync(Request());

        decision.IsRunnerBacked.Should().BeTrue();
        ComputeTargets.ToJobTarget(decision.Target).Should().Be(RunnerJobTarget.OrgRunner);
        decision.Reason.Should().NotContain("token").And.NotContain("http");
        decision.Message.Should().BeNull();

        List<string> reasons =
        [
            ComputeDecisionReasons.LocalMode, ComputeDecisionReasons.StepNotEligible, ComputeDecisionReasons.PreferenceCloudOnly,
            ComputeDecisionReasons.PreferenceRunner, ComputeDecisionReasons.AutoWithRunner, ComputeDecisionReasons.AutoWithoutRunner,
            ComputeDecisionReasons.RunnerUnavailableCloudFallback, ComputeDecisionReasons.NoRunnerOnline,
            ComputeDecisionReasons.FabricUnavailable, ComputeDecisionReasons.NotAvailable,
            ComputeDecisionReasons.PoolEmptyLocalFallback
        ];
        reasons.Should().OnlyHaveUniqueItems();
    }
}
