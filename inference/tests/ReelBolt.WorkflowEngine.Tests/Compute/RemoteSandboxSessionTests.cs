using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// D7b's remote sandbox session. The headline is the plan's acceptance test — a runner is killed
/// mid-execution and the next tool call still succeeds, because the workspace was rehydrated from the
/// journal — and the rest pins the two contracts a caller depends on: the OP TRANSLATION (a member of
/// <see cref="ISandboxSession"/> becomes the protocol op of the same name, with the arguments and the
/// result shape docs/runner-protocol.md defines) and the FAILURE CLASSIFICATION (a lost session is
/// recoverable, a refused op is not, a <c>503</c> is "the fabric is not deployed here").
/// </summary>
public class RemoteSandboxSessionTests
{
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Project = Guid.Parse("9a000000-0000-4000-8000-000000000002");
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-000000000003");

    private sealed record Harness(
        RemoteSandboxSession Session,
        FakeRunnerGateway Gateway,
        FakeSandboxJournalStore Store,
        FixedComputeTarget Selector,
        FakeSandboxScopeResolver Scopes,
        StubHttpMessageHandler LocalHandler,
        LocalSandboxSessionFactory LocalFactory);

    private static Harness Build(
        ComputeDecision? decision = null,
        Action<ComputeOptions>? configureCompute = null,
        HttpMessageHandler? localHandler = null)
    {
        FakeSandboxJournalStore store = new();
        FakeRunnerGateway gateway = new(store);
        FakeSandboxScopeResolver scopes = new(Project, Org);
        FixedComputeTarget selector = new(
            decision ?? ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly));

        ComputeOptions compute = ComputeTestFactory.Options(sessionWaitSeconds: 10);
        configureCompute?.Invoke(compute);

        StubHttpMessageHandler local = (StubHttpMessageHandler)(localHandler ?? new StubHttpMessageHandler(
            _ => Task.FromResult(ComputeTestFactory.Json("{\"ok\":true}"))));

        LocalSandboxSessionFactory localFactory = new(new StubHttpClientFactory(local), new ConfigurationBuilder().Build());

        RunnerGatewayClient client = ComputeTestFactory.Gateway(
            gateway.BuildHandler(),
            compute,
            new ExecutionSessionGate(Options.Create(ComputeTestFactory.Options(sessionWaitSeconds: 10))));

        RemoteSandboxSession session = new(
            Execution,
            selector,
            client,
            scopes,
            new SandboxWorkspaceJournal(store),
            localFactory,
            new StubHttpClientFactory(gateway.BuildHandler(), "http://gateway.test"),
            new FakePresignedUrlService(),
            new FakeRunnerOutputStore(),
            Options.Create(compute),
            Options.Create(new SandboxSessionOptions()),
            NullLogger<RemoteSandboxSession>.Instance);

        return new Harness(session, gateway, store, selector, scopes, local, localFactory);
    }

    private static JsonNode? HydrateManifest(FakeRunnerGateway gateway)
        => gateway.Ops.LastOrDefault(o => o.Op == "hydrate")?.Args?["manifest"];

    // ------------------------------------------------------------------
    // The acceptance test
    // ------------------------------------------------------------------

    [Fact]
    public async Task When_the_runner_is_killed_mid_execution_the_next_tool_call_rehydrates_and_succeeds()
    {
        Harness harness = Build();

        // Earlier steps wrote their work into the workspace — this is exactly the Author -> Render
        // sequence the plan names, and exactly what a sleeping laptop would otherwise destroy.
        await harness.Session.WriteTextAsync("src/root.tsx", "<html>root</html>", CancellationToken.None);
        await harness.Session.WriteTextAsync("src/scene.tsx", "export const Scene = 1;", CancellationToken.None);
        harness.Session.RunnerSessionId.Should().NotBeNull();

        Guid firstSession = harness.Session.RunnerSessionId!.Value;
        harness.Gateway.FilesAsText().Should().ContainKey("src/root.tsx");

        // The laptop closes. The container, and everything the agent wrote into it, is gone.
        harness.Gateway.KillRunner();

        // The next tool call is what discovers it.
        SandboxTextFile read = await harness.Session.ReadTextAsync("src/scene.tsx", CancellationToken.None);

        read.Text.Should().Be("export const Scene = 1;");
        harness.Session.SessionAttempt.Should().Be(2, "a lost session is replaced, not reused");
        harness.Session.RunnerSessionId.Should().NotBe(firstSession);

        // Files written by earlier steps are present on the NEW runner — the whole point.
        harness.Gateway.FilesAsText().Should().ContainKey("src/root.tsx").WhoseValue.Should().Be("<html>root</html>");
        harness.Gateway.FilesAsText().Should().ContainKey("src/scene.tsx").WhoseValue.Should().Be("export const Scene = 1;");

        // ... because the engine hydrated them, from its own journal, with real presigned GETs.
        JsonNode? manifest = HydrateManifest(harness.Gateway);
        manifest.Should().NotBeNull();
        manifest!.AsArray().Select(e => e!["path"]!.GetValue<string>())
            .Should().BeEquivalentTo(["src/root.tsx", "src/scene.tsx"]);
        harness.Store.PresignedKeys.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_replacement_session_is_created_under_a_different_idempotency_key()
    {
        // The key is sha256(executionId|stepId|iteration|purpose), so reusing the purpose would make
        // the gateway hand back the EXPIRED job and no second session would ever exist.
        Harness harness = Build();
        await harness.Session.WriteTextAsync("a.tsx", "a", CancellationToken.None);
        harness.Gateway.KillRunner();
        await harness.Session.ReadTextAsync("a.tsx", CancellationToken.None);

        harness.Gateway.JobPurposes.Should().Equal(
            RemoteSandboxSession.PurposeToken,
            RemoteSandboxSession.PurposeToken + "-2");
    }

    [Fact]
    public async Task A_replacement_session_honours_a_selector_that_now_refuses()
    {
        // "CloudPool fallback only if the selector allows it": an organization set to RunnerOnly whose
        // runner went away gets the coded refusal rather than silently moving to cloud billing.
        Harness harness = Build();
        await harness.Session.WriteTextAsync("a.tsx", "a", CancellationToken.None);
        harness.Gateway.KillRunner();
        harness.Selector.Decision = ComputeDecision.NoRunnerOnline("no runner is online for this workspace");

        Func<Task> act = () => harness.Session.ReadTextAsync("a.tsx", CancellationToken.None);

        ComputeTargetRefusedException refused = (await act.Should().ThrowAsync<ComputeTargetRefusedException>()).Which;
        refused.FailureCode.Should().Be(ComputeFailureCodes.NoRunnerOnline);
    }

    [Fact]
    public async Task A_journal_that_could_not_be_written_is_reported_rather_than_swallowed()
    {
        Harness harness = Build();
        await harness.Session.WriteTextAsync("a.tsx", "a", CancellationToken.None);

        // The blob is stored before the entry, so an entry naming a blob that was never written is
        // impossible; what IS possible is the store going away between the two. The execution has to
        // find out then, not when a laptop closes its lid.
        harness.Store.FailNextPut = true;

        Func<Task> act = () => harness.Session.WriteTextAsync("b.tsx", "b", CancellationToken.None);

        await act.Should().ThrowAsync<SandboxJournalUnavailableException>();
    }

    // ------------------------------------------------------------------
    // Op translation
    // ------------------------------------------------------------------

    [Fact]
    public async Task WriteText_is_the_writeFile_op_with_utf8_encoding()
    {
        Harness harness = Build();

        SandboxActionResult result = await harness.Session.WriteTextAsync("src/root.tsx", "<html/>", CancellationToken.None);

        result.Ok.Should().BeTrue();
        FakeOp op = harness.Gateway.Ops.Last(o => o.Op == "writeFile");
        op.Args!["path"]!.GetValue<string>().Should().Be("src/root.tsx");
        op.Args["content"]!.GetValue<string>().Should().Be("<html/>");
        op.Args["encoding"]!.GetValue<string>().Should().Be("utf8");
    }

    [Fact]
    public async Task WriteBytes_is_the_writeFile_op_with_base64_encoding()
    {
        Harness harness = Build();

        await harness.Session.WriteBytesAsync("assets/logo.png", [0x00, 0xff, 0x10], CancellationToken.None);

        FakeOp op = harness.Gateway.Ops.Last(o => o.Op == "writeFile");
        op.Args!["encoding"]!.GetValue<string>().Should().Be("base64");
        op.Args["content"]!.GetValue<string>().Should().Be(Convert.ToBase64String([0x00, 0xff, 0x10]));
    }

    [Fact]
    public async Task ListFiles_reports_the_engine_entry_shape_from_the_protocols_paths()
    {
        Harness harness = Build();
        await harness.Session.WriteTextAsync("src/root.tsx", "<html/>", CancellationToken.None);

        IReadOnlyList<SandboxFileEntry> entries = await harness.Session.ListFilesAsync("src", CancellationToken.None);

        entries.Should().ContainSingle();
        entries[0].Name.Should().Be("root.tsx", "the engine's callers compare NAMES, and the protocol reports paths");
        entries[0].IsDir.Should().BeFalse();
        entries[0].Size.Should().Be(7);
    }

    [Fact]
    public async Task A_missing_directory_is_an_answer_not_a_failure()
    {
        // The interface's contract, and what the listing helper's own caller relies on: a 404 means
        // "that directory contains no files", which the agent can act on directly.
        Harness harness = Build();

        Func<Task> act = () => harness.Session.ListFilesAsync("src", CancellationToken.None);

        SandboxRequestFailedException error = (await act.Should().ThrowAsync<SandboxRequestFailedException>()).Which;
        error.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_is_the_deletePath_op_and_is_journalled()
    {
        Harness harness = Build();
        await harness.Session.WriteTextAsync("a.tsx", "a", CancellationToken.None);

        await harness.Session.DeletePathAsync("a.tsx", CancellationToken.None);

        harness.Gateway.Ops.Should().Contain(o => o.Op == "deletePath");
        harness.Store.Puts.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Exec_is_the_exec_op_and_reports_the_engine_envelope()
    {
        Harness harness = Build();

        SandboxExecResult result = await harness.Session.ExecAsync("npm", ["run", "build"], 120, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Payload.Output.Should().Be("ran:npm run build");

        // RunNpmScriptInternal parses RawBody ITSELF, so the engine's {output, error} envelope is what
        // has to come back — not the protocol's stdout/stderr pair.
        using JsonDocument document = JsonDocument.Parse(result.RawBody);
        document.RootElement.GetProperty("output").GetString().Should().Be("ran:npm run build");
    }

    [Fact]
    public async Task Exec_reports_a_transport_failure_as_data()
    {
        Harness harness = Build();
        harness.Gateway.Intercept = _ => ComputeTestFactory.Json("{}", HttpStatusCode.ServiceUnavailable);

        SandboxExecResult result = await harness.Session.ExecAsync("npm", ["run", "build"], 120, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.TransportError.Should().NotBeNull("the agent is told to check readiness and retry rather than losing the execution");
    }

    [Fact]
    public async Task Status_probes_the_template_rather_than_guessing_at_it()
    {
        Harness harness = Build();
        await harness.Session.WriteTextAsync("package.json", "{}", CancellationToken.None);
        await harness.Session.WriteTextAsync("node_modules/react/index.js", "module.exports = {}", CancellationToken.None);

        SandboxStatusResponse status = await harness.Session.StatusAsync(CancellationToken.None);

        status.Exists.Should().BeTrue();
        status.Ready.Should().BeTrue();
        status.HasPackageJson.Should().BeTrue();
        status.HasNodeModules.Should().BeTrue();
        harness.Gateway.Ops.Should().Contain(o => o.Op == "status");
    }

    [Fact]
    public async Task InstallPackages_is_refused_with_a_message_the_agent_can_act_on()
    {
        Harness harness = Build();

        SandboxExecResponse response = await harness.Session.InstallPackagesAsync(["framer-motion"], CancellationToken.None);

        response.Error.Should().Contain("no network egress");
        harness.Gateway.Ops.Should().NotContain(o => o.Op == "installPackages");
    }

    [Fact]
    public async Task Streaming_a_raw_file_out_of_a_runner_workspace_publishes_it_into_the_jobs_staging_key_space()
    {
        // D8 replaced D7b's refusal here: reading a workspace file RAW is the read half of `publish`,
        // and it is served by publishing the file into the session job's OWN staging key space and
        // reading the object back — never inline, because the inline cap is 4 MiB. The full round trip
        // is covered in RemoteSandboxStagePublishTests; what this pins is the op and the key space,
        // which is the D8 contract a caller of this session depends on.
        Harness harness = Build();
        await harness.Session.WriteTextAsync("out/promo.mp4", "x", CancellationToken.None);

        Func<Task> act = () => harness.Session.OpenRawFileAsync("out/promo.mp4", 600, CancellationToken.None);

        // This harness has no object store behind the staging URL, so the READ-BACK is what fails.
        await act.Should().ThrowAsync<SandboxRequestFailedException>();

        FakeOp publish = harness.Gateway.Ops.Last(o => o.Op == "publish");
        string jobId = harness.Gateway.JobIds[^1].ToString("D");
        publish.Args!["path"]!.GetValue<string>().Should().Be("out/promo.mp4");
        publish.Args["putUrl"]!.GetValue<string>().Should().Contain(Uri.EscapeDataString("runner-staging/" + jobId + "/"));
    }

    [Fact]
    public async Task Complete_sends_close_and_seals_the_journal()
    {
        Harness harness = Build();
        await harness.Session.WriteTextAsync("a.tsx", "a", CancellationToken.None);

        SandboxActionResult result = await harness.Session.CompleteAsync(CancellationToken.None);

        result.Ok.Should().BeTrue();
        harness.Gateway.Ops.Should().Contain(o => o.Op == "close");
        harness.Store.Keys.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // The D6 boundary, and the fabric's own failure vocabulary
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_local_decision_forwards_every_member_to_the_local_session()
    {
        // Compute:Mode=Fabric can still answer Local — most importantly fabric_unavailable, which is a
        // DEPLOYMENT FACT and never a reason to fail a step. The remote session must therefore BE the
        // local one in that case, not fail on its behalf.
        StubHttpMessageHandler local = new(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return Task.FromResult(ComputeTestFactory.Json(
                path.EndsWith("/status", StringComparison.Ordinal)
                    ? "{\"exists\":true,\"ready\":true,\"hasPackageJson\":true,\"hasNodeModules\":true,\"containerName\":\"local\",\"workspacePath\":\"/workspace\",\"createdAt\":null,\"lastActivity\":null}"
                    : "{\"id\":\"local\",\"workflowExecutionId\":\"" + Execution.ToString("D") + "\",\"containerName\":\"local\",\"workspacePath\":\"/workspace\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"lastActivity\":\"2026-01-01T00:00:00Z\"}"));
        });

        Harness harness = Build(
            ComputeDecision.Local(ComputeDecisionReasons.FabricUnavailable),
            localHandler: local);

        SandboxInfo info = await harness.Session.EnsureAsync(CancellationToken.None);

        info.ContainerName.Should().Be("local");

        SandboxStatusResponse status = await harness.Session.StatusAsync(CancellationToken.None);
        status.HasPackageJson.Should().BeTrue();
        harness.Gateway.JobPurposes.Should().BeEmpty("a local decision must not reach the gateway at all");
        harness.Gateway.Ops.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_from_the_selector_is_thrown_with_its_failure_code()
    {
        Harness harness = Build(ComputeDecision.NoRunnerOnline("no runner is online"));

        Func<Task> act = () => harness.Session.EnsureAsync(CancellationToken.None);

        ComputeTargetRefusedException refused = (await act.Should().ThrowAsync<ComputeTargetRefusedException>()).Which;
        refused.FailureCode.Should().Be(ComputeFailureCodes.NoRunnerOnline);
        harness.Gateway.JobPurposes.Should().BeEmpty("a refused decision must not create a job");
    }

    [Fact]
    public async Task With_no_internal_token_the_session_refuses_rather_than_calling_unauthenticated()
    {
        Harness harness = Build(configureCompute: options => options.InternalToken = string.Empty);

        Func<Task> act = () => harness.Session.EnsureAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ComputeFabricUnavailableException>())
            .WithMessage("*RUNNER_INTERNAL_TOKEN*");
        harness.Gateway.JobPurposes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_op_is_treated_as_session_loss_not_as_a_refusal()
    {
        // "An opId that has aged out of the cache and is repeated returns error op_expired, which the
        // engine treats as session loss."
        Harness harness = Build();
        await harness.Session.WriteTextAsync("a.tsx", "a", CancellationToken.None);
        harness.Gateway.FailingOps["readFile"] = "op_expired";

        Func<Task> act = () => harness.Session.ReadTextAsync("a.tsx", CancellationToken.None);

        // It went down the RECOVERY path rather than being reported as a refusal: the session was
        // replaced (attempt 2) and then failed on the retry too, which is the honest end of the story.
        await act.Should().ThrowAsync<SandboxSessionLostException>();
        harness.Session.SessionAttempt.Should().Be(2);
    }

    [Fact]
    public async Task The_session_slot_is_released_when_a_job_could_not_be_opened()
    {
        // The execution's ONE session slot is held by the job handle, so a failed open that forgot to
        // release it would wedge every later session of that execution.
        FakeSandboxJournalStore store = new();
        FakeRunnerGateway gateway = new(store);
        gateway.Intercept = request => request.RequestUri!.AbsolutePath.EndsWith("/jobs", StringComparison.Ordinal)
            ? ComputeTestFactory.Json("{\"error\":\"bad_request\"}", HttpStatusCode.BadRequest)
            : null;

        ComputeOptions compute = ComputeTestFactory.Options(sessionWaitSeconds: 1);
        ExecutionSessionGate gate = new(Options.Create(compute));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(gateway.BuildHandler(), compute, gate);

        RemoteSandboxSession session = new(
            Execution,
            new FixedComputeTarget(ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly)),
            client,
            new FakeSandboxScopeResolver(Project, Org),
            new SandboxWorkspaceJournal(store),
            new LocalSandboxSessionFactory(
                new StubHttpClientFactory(new StubHttpMessageHandler(_ => Task.FromResult(ComputeTestFactory.Json("{}")))),
                new ConfigurationBuilder().Build()),
            new StubHttpClientFactory(gateway.BuildHandler()),
            new FakePresignedUrlService(),
            new FakeRunnerOutputStore(),
            Options.Create(compute),
            Options.Create(new SandboxSessionOptions()),
            NullLogger<RemoteSandboxSession>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => session.EnsureAsync(CancellationToken.None));

        // The slot is free: a second acquisition does not wait for the configured timeout.
        ExecutionSessionLease lease = await gate.AcquireAsync(Execution, null, CancellationToken.None);
        lease.Acquired.Should().BeTrue();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Closing_the_execution_closes_the_session_and_never_throws()
    {
        Harness harness = Build();
        await harness.Session.WriteTextAsync("a.tsx", "a", CancellationToken.None);

        await harness.Session.CloseForExecutionEndAsync(CancellationToken.None);

        harness.Gateway.Ops.Should().Contain(o => o.Op == "close");
        harness.Store.Keys.Should().BeEmpty();
    }
}
