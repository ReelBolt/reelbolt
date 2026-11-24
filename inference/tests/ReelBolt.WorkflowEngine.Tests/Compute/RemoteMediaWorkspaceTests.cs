using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// D11's remote media session, op by op. Every test drives a REAL
/// <see cref="RunnerGatewayClient"/> over a scripted HTTP handler, so what is asserted is the actual
/// wire traffic - the route, the op name, the args, the opId, the timeout - and not a mock's idea of
/// it. The route and the JSON field names are pinned against the Go gateway's own source by
/// MediaSessionOpContractTests.
/// </summary>
public class RemoteMediaWorkspaceTests
{
    private static readonly Guid Project = MediaSessionTestData.Project;
    private static readonly Guid Job = MediaSessionTestData.Job;
    private static readonly Guid Session = MediaSessionTestData.Session;

    private static RemoteMediaOptions Options(long maxStage = 1L << 30, long maxPublish = 1L << 30)
        => new() { MaxStageBytes = maxStage, MaxPublishBytes = maxPublish };

    private sealed record Fixture(
        RemoteMediaWorkspace Workspace,
        ScriptedSessionGateway Gateway,
        FakeProjectFileWorkspace Files,
        FakePresignedUrlService Presigner,
        FakeOutputVerifier Verifier,
        FakeRunnerOutputStore Store);

    private static Fixture Build(
        Action<ScriptedSessionGateway> script,
        RemoteMediaOptions? options = null,
        RunnerJobTarget target = RunnerJobTarget.CloudPool)
    {
        ScriptedSessionGateway gateway = new();
        script(gateway);

        FakeProjectFileWorkspace files = new();
        FakePresignedUrlService presigner = new();
        FakeOutputVerifier verifier = new();
        FakeRunnerOutputStore store = new();

        RemoteMediaWorkspace workspace = new(
            Project,
            Job,
            Session,
            target,
            ComputeTestFactory.Gateway(gateway.Handler),
            files,
            presigner,
            verifier,
            store,
            options ?? Options(),
            NullLogger.Instance);

        return new Fixture(workspace, gateway, files, presigner, verifier, store);
    }

    private static JsonObject ArgsOf(Fixture fixture, int index) => fixture.Gateway.Ops[index].Args;

    // --- The route ---------------------------------------------------------

    [Fact]
    public async Task EveryOpGoesToTheSessionsOpsRouteWithTheInternalToken()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":9}"""));

        await fixture.Workspace.StatAsync("clip.mp4", CancellationToken.None);

        fixture.Gateway.Handler.Requests.Should().ContainSingle();
        fixture.Gateway.Handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        fixture.Gateway.Handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be("/internal/v1/sessions/" + Session.ToString("D") + "/ops");
        fixture.Gateway.Handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("test-internal-token");
    }

    // --- stage / materialize ----------------------------------------------

    [Fact]
    public async Task Stage_mintsAPresignedGetAndIssuesTheStageOp()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stage, """{"bytes":12,"sha256":"ab"}"""));

        await fixture.Workspace.StageAsync("projects/p/outputFiles/clip.mp4", "source-0.mp4", CancellationToken.None);

        fixture.Presigner.Gets.Should().ContainSingle();
        fixture.Presigner.Gets[0].Key.Should().Be("projects/p/outputFiles/clip.mp4");

        fixture.Gateway.Ops.Should().ContainSingle();
        fixture.Gateway.Ops[0].Op.Should().Be("stage");
        ArgsOf(fixture, 0)["url"]!.GetValue<string>().Should().Be("https://objects.test/get?key=projects%2Fp%2FoutputFiles%2Fclip.mp4");
        ArgsOf(fixture, 0)["path"]!.GetValue<string>().Should().Be("source-0.mp4");
        ArgsOf(fixture, 0)["maxBytes"]!.GetValue<long>().Should().Be(1L << 30);
    }

    [Fact]
    public async Task Stage_withAByteCap_asksTheWorkerToEnforceTheSmallerCapWhileCopying()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stage, """{"bytes":12,"sha256":"ab"}"""));

        await ((IMediaWorkspace)fixture.Workspace).StageAsync("projects/p/userFiles/brand.ttf", "brand-font", 10L * 1024 * 1024, CancellationToken.None);

        fixture.Gateway.Ops.Should().ContainSingle("the worker enforces maxBytes while streaming; no stat afterwards is needed");
        ArgsOf(fixture, 0)["maxBytes"]!.GetValue<long>().Should().Be(10L * 1024 * 1024);
    }

    [Fact]
    public async Task Stage_overTheCap_isAStageLimitFailureNotAGenericOpFailure()
    {
        Fixture fixture = Build(g => g.OnOpError(MediaSessionOps.Stage, "file_too_large", "past maxBytes"));

        Func<Task> act = () => ((IMediaWorkspace)fixture.Workspace).StageAsync("projects/p/userFiles/brand.ttf", "brand-font", 1024, CancellationToken.None);

        await act.Should().ThrowAsync<MediaStageLimitExceededException>();
    }

    [Fact]
    public async Task Materialize_returnsTheVirtualPathUnchangedBecauseTheWorkerResolvesIt()
    {
        // The interface's own rule for a remote workspace, and the property every argv in the compile
        // path depends on: what goes into the argv is the session-relative name, never a host path.
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":1}"""));

        string resolved = await fixture.Workspace.MaterializeAsync("nested/grid.rgb", CancellationToken.None);

        resolved.Should().Be("nested/grid.rgb");
        fixture.Gateway.Ops.Should().BeEmpty("materializing a path is not an op");
    }

    // --- ffmpeg / ffprobe --------------------------------------------------

    [Fact]
    public async Task Ffmpeg_sendsTheArgvAndReadsTheExitCodeBack()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Ffmpeg, """{"exitCode":0,"stderrTail":"x","durationMs":42}"""));

        VideoToolResult result = await fixture.Workspace.RunFfmpegAsync(
            ["-nostdin", "-y", "-i", "source-0.mp4", "out.mp4"], TimeSpan.FromSeconds(120), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ArgsOf(fixture, 0)["argv"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("-nostdin", "-y", "-i", "source-0.mp4", "out.mp4");
        ArgsOf(fixture, 0)["timeoutSec"]!.GetValue<int>().Should().Be(120);
    }

    [Fact]
    public async Task Ffmpeg_refusesAnAbsolutePathInTheArgvBeforeItReachesARunner()
    {
        // The worker would answer argv_not_allowed, which says only that something was wrong. Catching
        // it here names the argument, and - more importantly - proves nothing was sent at all.
        Fixture fixture = Build(g => g.On(MediaSessionOps.Ffmpeg, """{"exitCode":0}"""));

        Func<Task> act = () => fixture.Workspace.RunFfmpegAsync(
            ["-i", "/tmp/reelbolt/scratch/exec/source-0.mp4", "out.mp4"], TimeSpan.FromSeconds(10), CancellationToken.None);

        (await act.Should().ThrowAsync<MediaWorkspaceException>()).Which.Code.Should().Be("argv_absolute_path");
        fixture.Gateway.Ops.Should().BeEmpty("nothing may leave the engine when the argv is not session-relative");
    }

    [Fact]
    public async Task Ffmpeg_refusesATraversalSegmentInTheArgv()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Ffmpeg, """{"exitCode":0}"""));

        Func<Task> act = () => fixture.Workspace.RunFfmpegAsync(
            ["-i", "../../etc/passwd", "out.mp4"], TimeSpan.FromSeconds(10), CancellationToken.None);

        (await act.Should().ThrowAsync<MediaWorkspaceException>()).Which.Code.Should().Be("argv_traversal");
        fixture.Gateway.Ops.Should().BeEmpty();
    }

    [Fact]
    public async Task Ffmpeg_mapsAWorkerRefusalOntoANonZeroResultRatherThanAThrow()
    {
        // The executor branches on the exit code and on TimedOut; an argv the worker refused is the
        // same "the process produced nothing" answer a killed process gives, and must not become an
        // exception that the step's catch-all would classify as a platform fault.
        Fixture fixture = Build(g => g.OnOpError(MediaSessionOps.Ffmpeg, "timeout", "the encode exceeded its deadline", retryable: true));

        VideoToolResult result = await fixture.Workspace.RunFfmpegAsync(["out.mp4"], TimeSpan.FromSeconds(90), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
        result.StdErr.Should().Contain("timeout").And.Contain("deadline");
        fixture.Gateway.Count("ffmpeg").Should().Be(1, "an op the WORKER refused is a value to map, not something to resend");
    }

    [Fact]
    public async Task ASessionLevelTimeoutIsRetriedOnceUnderTheSameOpId()
    {
        // The protocol's retry rule is about the 504/session-level refusals, not about a worker that
        // answered: "a retry with the same opId is safe, because the worker answers a repeated opId
        // from its own cache". Reusing the id is precisely what makes the second attempt a replay
        // instead of a second encode.
        Fixture fixture = Build(_ => { });
        int attempts = 0;
        fixture.Gateway.On(MediaSessionOps.Stat, _ =>
        {
            attempts++;
            return attempts == 1
                ? ComputeTestFactory.Json(MediaSessionTestData.RefusalBody("op_timeout"), HttpStatusCode.GatewayTimeout)
                : ComputeTestFactory.Json(MediaSessionTestData.OpBody("""{"exists":true,"bytes":3}"""));
        });

        MediaWorkspaceStat stat = await fixture.Workspace.StatAsync("out.mp4", CancellationToken.None);

        stat.Bytes.Should().Be(3);
        fixture.Gateway.Count("stat").Should().Be(2);
        fixture.Gateway.OpIds.Distinct().Should().ContainSingle("the retry reuses the opId, which is what makes it safe");
    }

    [Fact]
    public async Task Ffprobe_readsStdoutOutOfTheOpResult()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Ffprobe, """{"exitCode":0,"stdout":"now","stderrTail":"x"}"""));

        VideoToolResult result = await fixture.Workspace.RunFfprobeAsync(
            ["-v", "error", "-print_format", "json", "clip.mp4"], TimeSpan.FromSeconds(30), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.StdOut.Should().Be("now");
    }

    [Fact]
    public async Task Ffmpeg_feedsTheReportedTailToTheStreamingCallbackSoProgressStillReports()
    {
        // The protocol's ffmpeg op returns only a tail of stderr, so a remote run cannot replay every
        // line. It must still feed the last block to the caller's callback, or a -progress parser
        // would silently stop reporting.
        Fixture fixture = Build(g => g.On(MediaSessionOps.Ffmpeg, """{"exitCode":0,"stderrTail":"out_time_ms=1000\nout_time_ms=2000"}"""));

        List<string> lines = [];
        await fixture.Workspace.RunFfmpegAsync(["out.mp4"], TimeSpan.FromSeconds(10), CancellationToken.None, lines.Add);

        lines.Should().Equal("out_time_ms=1000", "out_time_ms=2000");
    }

    // --- read --------------------------------------------------------------

    [Fact]
    public async Task Read_returnsTheInlineBytes()
    {
        Fixture fixture = Build(g =>
        {
            g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":5}""");
            g.On(MediaSessionOps.Read, """{"content":"aGVsbG8=","encoding":"base64","bytes":5}""");
        });

        byte[] bytes = await fixture.Workspace.ReadAsync("analysis.json", CancellationToken.None);

        Encoding.UTF8.GetString(bytes).Should().Be("hello");
        fixture.Gateway.Ops.Select(o => o.Op).Should().Equal("stat", "read");
        ArgsOf(fixture, 1)["maxBytes"]!.GetValue<int>().Should().Be(MediaSessionOps.SafeInlineReadBytes);
    }

    [Fact]
    public async Task Read_mapsFileTooLargeOntoTheSameExceptionTheLocalWorkspaceThrows()
    {
        Fixture fixture = Build(g => g.OnOpError(MediaSessionOps.Read, "file_too_large", "over the cap"));

        Func<Task> act = () => fixture.Workspace.ReadAsync("grid.rgb", maxBytes: 4096, CancellationToken.None);

        (await act.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>())
            .Which.MaxBytes.Should().Be(4096);
    }

    [Fact]
    public async Task Read_overTheInlineCapTakesThePublishThenFetchArm()
    {
        // "larger content is read by publish then fetched from object storage by the engine".
        Fixture fixture = Build(g =>
        {
            g.On(MediaSessionOps.Stat, $$"""{"exists":true,"bytes":{{MediaSessionOps.MaxInlineReadBytes + 1}}}""");
            g.On(MediaSessionOps.Publish, """{"bytes":5,"sha256":"cd"}""");
        });
        fixture.Store.Sizes[S3RunnerOutputStore.StagingKeyFor(Job, 1)] = MediaSessionOps.MaxInlineReadBytes + 2;

        Func<Task> act = () => fixture.Workspace.ReadAsync("big.bin", maxBytes: MediaSessionOps.MaxInlineReadBytes + 1, CancellationToken.None);

        await act.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>();
        fixture.Gateway.Ops[0].Op.Should().Be("stat");
        fixture.Gateway.Ops[1].Op.Should().Be("publish", "an oversized read is a publish plus an engine-side fetch");
        fixture.Presigner.Puts.Should().ContainSingle();
        fixture.Presigner.Puts[0].Index.Should().Be(1);
        fixture.Store.Discarded.Should().ContainSingle("the staging object is a transport buffer");
    }

    [Fact]
    public async Task Read_ofA386MbWav_statsThenPublishes_neverAnInlineReadOverTheSafeCap()
    {
        Fixture fixture = Build(g =>
        {
            g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":3860000}""");
            g.On(MediaSessionOps.Publish, """{"bytes":3860000,"sha256":"cd"}""");
            g.On(MediaSessionOps.Read, """{"content":"","encoding":"base64","bytes":0}""");
        });
        fixture.Store.Sizes[S3RunnerOutputStore.StagingKeyFor(Job, 1)] = 3860000;

        // The fetch itself is a real HTTP GET against a fake URL and fails; the ops are what matter.
        try { await fixture.Workspace.ReadAsync("music.wav", CancellationToken.None); } catch (Exception) { }

        fixture.Gateway.Ops.Select(o => o.Op).Should().Equal("stat", "publish");
    }

    [Fact]
    public async Task Read_ofA1MbFile_goesInlineUnderTheSafeCap()
    {
        Fixture fixture = Build(g =>
        {
            g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":1000000}""");
            g.On(MediaSessionOps.Read, """{"content":"aGVsbG8=","encoding":"base64","bytes":5}""");
        });

        await fixture.Workspace.ReadAsync("a.wav", CancellationToken.None);

        fixture.Gateway.Ops.Select(o => o.Op).Should().Equal("stat", "read");
        ArgsOf(fixture, 1)["maxBytes"]!.GetValue<int>().Should().BeLessThanOrEqualTo(MediaSessionOps.SafeInlineReadBytes);
    }

    [Fact]
    public async Task Read_whoseStatRoutedInlineReadIsRefusedAsTooLarge_fallsBackToObjectStorage_whenTheCallerAllows()
    {
        // Escape inflation ("<a>"-style text) or growth after stat: the stat said it fits, the worker says no.
        Fixture fixture = Build(g =>
        {
            g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":2883584}""");
            g.OnOpError(MediaSessionOps.Read, "file_too_large", "inflated past the frame");
            g.On(MediaSessionOps.Publish, """{"bytes":9000000,"sha256":"cd"}""");
        });
        fixture.Store.Sizes[S3RunnerOutputStore.StagingKeyFor(Job, 1)] = 9_000_000;

        // The fetch is a real HTTP GET against a fake URL and fails; the op order is what matters.
        try { await fixture.Workspace.ReadAsync("a.txt", maxBytes: 16 * 1024 * 1024, CancellationToken.None); } catch (Exception) { }

        fixture.Gateway.Ops.Select(o => o.Op).Should().Equal("stat", "read", "publish");
    }

    [Fact]
    public async Task Read_thatGrewAfterStat_andIsPastTheCallersCap_stillThrowsFileTooLarge()
    {
        Fixture fixture = Build(g =>
        {
            g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":2883584}""");
            g.OnOpError(MediaSessionOps.Read, "file_too_large", "grew");
            g.On(MediaSessionOps.Publish, """{"bytes":9000000,"sha256":"cd"}""");
        });
        fixture.Store.Sizes[S3RunnerOutputStore.StagingKeyFor(Job, 1)] = 9_000_000;

        Func<Task> act = () => fixture.Workspace.ReadAsync("a.txt", maxBytes: 4_000_000, CancellationToken.None);

        await act.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>();
    }

    [Fact]
    public async Task Read_ofAFileOverTheCallersMaxBytes_throwsFileTooLargeWithoutReadingOrPublishing()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":3860000}"""));

        Func<Task> act = () => fixture.Workspace.ReadAsync("music.wav", maxBytes: 3_500_000, CancellationToken.None);

        await act.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>();
        fixture.Gateway.Ops.Select(o => o.Op).Should().Equal("stat");
    }

    [Fact]
    public async Task Read_withNoCap_ofAFileStattedAboveTheEngineDefault_throwsFileTooLargeWithoutPublishOrDownload()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stat, $$"""{"exists":true,"bytes":{{(long)MediaSessionOps.MaxEngineReadBytes + 1}}}"""));

        Func<Task> act = () => fixture.Workspace.ReadAsync("huge.wav", CancellationToken.None);

        await act.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>();
        fixture.Gateway.Ops.Select(o => o.Op).Should().Equal("stat");
        fixture.Presigner.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task BoundedDownload_abortsAtTheLimit_whateverContentLengthSays()
    {
        // Absent Content-Length (chunked body).
        using var chunked = new StreamContent(new MemoryStream(new byte[2000]));
        chunked.Headers.ContentLength = null;
        Func<Task> overChunked = () => RemoteMediaWorkspace.ReadBoundedAsync(chunked, "a.bin", 1000, CancellationToken.None);
        await overChunked.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>();

        // A header that lies low while the body is longer.
        using var lying = new StreamContent(new MemoryStream(new byte[2000]));
        lying.Headers.ContentLength = 10;
        Func<Task> overLying = () => RemoteMediaWorkspace.ReadBoundedAsync(lying, "a.bin", 1000, CancellationToken.None);
        await overLying.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>();

        // A declared length over the cap is refused before any read.
        using var declared = new StreamContent(new MemoryStream(new byte[10]));
        declared.Headers.ContentLength = 5000;
        Func<Task> overDeclared = () => RemoteMediaWorkspace.ReadBoundedAsync(declared, "a.bin", 1000, CancellationToken.None);
        await overDeclared.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>();

        // Within the cap it returns the bytes.
        using var ok = new StreamContent(new MemoryStream(new byte[500]));
        (await RemoteMediaWorkspace.ReadBoundedAsync(ok, "a.bin", 1000, CancellationToken.None)).Length.Should().Be(500);
    }

    // --- stat --------------------------------------------------------------

    [Fact]
    public async Task Stat_reportsExistenceAndSize()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":4096}"""));

        MediaWorkspaceStat stat = await fixture.Workspace.StatAsync("out.mp4", CancellationToken.None);

        stat.Exists.Should().BeTrue();
        stat.Bytes.Should().Be(4096);
    }

    [Fact]
    public async Task Stat_ofAMissingFileIsAnAnswerNotAFailure()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stat, """{"exists":false,"bytes":0}"""));

        (await fixture.Workspace.StatAsync("nope.mp4", CancellationToken.None)).Exists.Should().BeFalse();
    }

    // --- publish + D12 verification ---------------------------------------

    [Fact]
    public async Task Publish_putsToStagingAndOnlyReturnsTheFinalKeyAfterVerification()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Publish, """{"bytes":2048,"sha256":"deadbeef"}"""));

        string key = await fixture.Workspace.PublishAsync("out.mp4", "projects/p/outputFiles/x/out.mp4", "video/mp4", CancellationToken.None);

        key.Should().Be("projects/p/outputFiles/x/out.mp4");

        ArgsOf(fixture, 0)["path"]!.GetValue<string>().Should().Be("out.mp4");
        ArgsOf(fixture, 0)["contentType"]!.GetValue<string>().Should().Be("video/mp4");
        ArgsOf(fixture, 0)["putUrl"]!.GetValue<string>()
            .Should().Be("https://objects.test/put?key=" + Uri.EscapeDataString(S3RunnerOutputStore.StagingKeyFor(Job, 1)));

        fixture.Verifier.Requests.Should().ContainSingle("no runner output becomes a result unverified (D12)");
        RunnerOutputRequest verification = fixture.Verifier.Requests[0];
        verification.FinalKey.Should().Be("projects/p/outputFiles/x/out.mp4");
        verification.JobId.Should().Be(Job);
        verification.StagingIndex.Should().Be(1);
        verification.Target.Should().Be(RunnerJobTarget.CloudPool);
        verification.Spec.Sha256.Should().Be("deadbeef", "the runner's digest is recorded as a HINT for the verifier to check independently");
        verification.Spec.MaxBytes.Should().Be(1L << 30);
    }

    [Fact]
    public async Task Publish_throwsWithTheVerifiersCodeAndClassWhenTheOutputIsRejected()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Publish, """{"bytes":9,"sha256":"x"}"""));
        fixture.Verifier.Verdict = new OutputVerificationResult(
            Verified: false,
            FailureCode: "RUNNER_OUTPUT_DURATION_MISMATCH",
            FailureClass: FailureClass.Runner,
            Message: "the runner's output is 4.0 s, not the 12.0 s the job specified",
            SizeBytes: 9,
            Sha256: null,
            FinalKey: null);

        Func<Task> act = () => fixture.Workspace.PublishAsync("out.mp4", "projects/p/outputFiles/x/out.mp4", "video/mp4", CancellationToken.None);

        RunnerOutputRejectedException rejected = (await act.Should().ThrowAsync<RunnerOutputRejectedException>()).Which;
        rejected.Code.Should().Be("RUNNER_OUTPUT_DURATION_MISMATCH");
        rejected.FailureClass.Should().Be(FailureClass.Runner, "the runner's fault is not refunded");
    }

    [Fact]
    public async Task UploadArtifact_verifiesTheDocumentsObjectTooBecauseItCrossedAMachineBoundary()
    {
        // The engine's own EDL/timeline document. Locally the caller takes the object store's returned
        // key at face value (that is the contract IProjectFileWorkspace.UploadArtifactAsync has always
        // had); a runner-produced object is verified and promoted first, exactly like a compiled
        // program, because it came from a machine the platform does not control.
        Fixture fixture = Build(g => g.On(MediaSessionOps.Publish, """{"bytes":40,"sha256":"ab"}"""));

        string key = await fixture.Workspace.UploadArtifactAsync(
            "edl.json", "projects/p/agentFiles/video-analysis/e/step-3-edl.json", "application/json", CancellationToken.None);

        key.Should().Be("projects/p/agentFiles/video-analysis/e/step-3-edl.json");
        fixture.Verifier.Requests.Should().ContainSingle();
        fixture.Verifier.Requests[0].Spec.IsMedia.Should().BeFalse("a JSON document has no video stream to probe");
        fixture.Verifier.Requests[0].Spec.IsJson.Should().BeTrue("it is validated as JSON instead");
    }

    // --- writes (D11's answer to the missing media write op) ---------------

    [Fact]
    public async Task WriteText_stagesTheBytesIntoTheSessionThroughObjectStorage()
    {
        // The media op set has no write op. The engine uploads the bytes as a bare artifact, presigns a
        // GET of it and runs stage - the documented op set used as written, so the runner's filtergraph
        // finds the slot file exactly where the argv says it is.
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stage, """{"bytes":10,"sha256":"ef"}"""));

        await fixture.Workspace.WriteTextAsync("caption-1.txt", "hello", MediaWorkspaceTexts.Utf8NoBom, CancellationToken.None);

        fixture.Files.Uploads.Should().ContainSingle();
        fixture.Files.Uploads[0].Should().StartWith(
            "projects/" + Project.ToString("D") + "/" + RemoteMediaWorkspace.WriteKeyPrefix + "/" + Job.ToString("D") + "/");
        fixture.Files.Objects[fixture.Files.Uploads[0]].Should().Equal(Encoding.UTF8.GetBytes("hello"));

        fixture.Gateway.Ops.Should().ContainSingle();
        fixture.Gateway.Ops[0].Op.Should().Be("stage");
        ArgsOf(fixture, 0)["path"]!.GetValue<string>().Should().Be("caption-1.txt");
    }

    [Fact]
    public async Task WriteText_presignsTheExactKeyTheStoreHolds_soTheRunnerNeverSeesA404()
    {
        // Regression: the engine uploaded under projects/{p}/agentFiles/media-session/{job}/... but
        // presigned projects/{p}/{job}/... (the category segment was dropped), so the pool node's GET
        // answered 404 for an object that did not exist at that key. The fake store is keyed exactly
        // like the real one, so a presigned key that is not a stored key is the failure.
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stage, """{"bytes":10,"sha256":"ef"}"""));

        await fixture.Workspace.WriteTextAsync("analysis.json", "{}", MediaWorkspaceTexts.Utf8NoBom, CancellationToken.None);

        fixture.Presigner.Gets.Should().ContainSingle();
        string presigned = fixture.Presigner.Gets[0].Key;
        fixture.Files.Objects.Should().ContainKey(presigned);
        presigned.Should().Be(fixture.Files.Uploads[0]);
        fixture.Presigner.Gets[0].ProjectId.Should().Be(Project);
    }

    [Fact]
    public async Task WriteText_withEncodingUtf8_writesTheByteOrderMarkTheLocalWriterWouldHaveWritten()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stage, """{"bytes":5,"sha256":"ef"}"""));

        await fixture.Workspace.WriteTextAsync("subs.ass", "x", Encoding.UTF8, CancellationToken.None);

        fixture.Files.Objects[fixture.Files.Uploads[0]].Should().Equal(0xEF, 0xBB, 0xBF, (byte)'x');
    }

    [Fact]
    public void TheWriteKeyIsDerivedFromTheJobAndTheVirtualPathSoARetryReusesTheSameObject()
    {
        Fixture fixture = Build(g => g.On(MediaSessionOps.Stage, """{"bytes":1}"""));

        fixture.Workspace.WriteKeyFor("caption-1.txt").Should().Be(fixture.Workspace.WriteKeyFor("caption-1.txt"));
        fixture.Workspace.WriteKeyFor("caption-1.txt").Should().NotBe(fixture.Workspace.WriteKeyFor("caption-2.txt"));
    }

    // --- session failure, cancellation and counting ------------------------

    [Fact]
    public async Task ALostSessionThrowsAndIsNeverRetried()
    {
        Fixture fixture = Build(g => g.OnStatus(MediaSessionOps.Stat, HttpStatusCode.Gone,
            MediaSessionTestData.RefusalBody("session_lost", reason: "lease_lost")));

        Func<Task> act = () => fixture.Workspace.StatAsync("out.mp4", CancellationToken.None);

        RunnerSessionOpException ex = (await act.Should().ThrowAsync<RunnerSessionOpException>()).Which;
        ex.SessionLost.Should().BeTrue();
        ex.Retryable.Should().BeFalse();
        ex.Reason.Should().Be("lease_lost");
        fixture.Gateway.Ops.Should().ContainSingle("a lost session is an answer, not something to retry");
    }

    [Fact]
    public async Task CancellingTheCallerCancelsTheOpCall()
    {
        // "Cancel during an encode stops the worker's ffmpeg": the op's HTTP call carries the step's
        // token, so cancelling the step releases the gateway's waiter, which is what forwards cancel to
        // the runner. Nothing else in the engine has to know how that works.
        using CancellationTokenSource cts = new();
        ScriptedSessionGateway gateway = new();
        gateway.On(MediaSessionOps.Ffmpeg, _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return ComputeTestFactory.Json(MediaSessionTestData.OpBody("""{"exitCode":0}"""));
        });

        RemoteMediaWorkspace workspace = new(
            Project, Job, Session, RunnerJobTarget.CloudPool,
            ComputeTestFactory.Gateway(gateway.Handler),
            new FakeProjectFileWorkspace(),
            new FakePresignedUrlService(),
            new FakeOutputVerifier(),
            new FakeRunnerOutputStore(),
            Options(),
            NullLogger.Instance);

        Func<Task> act = () => workspace.RunFfmpegAsync(["out.mp4"], TimeSpan.FromSeconds(10), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TheOpCountIsThePerCompileFigureThePlanAsksToLog()
    {
        Fixture fixture = Build(g =>
        {
            g.On(MediaSessionOps.Stat, """{"exists":true,"bytes":1}""");
            g.On(MediaSessionOps.Close, "{}");
        });

        await fixture.Workspace.StatAsync("a", CancellationToken.None);
        await fixture.Workspace.StatAsync("b", CancellationToken.None);
        await fixture.Workspace.CloseAsync(CancellationToken.None);

        fixture.Workspace.OpCount.Should().Be(3);
    }

    [Fact]
    public async Task Close_isBestEffortAndNeverFailsAStep()
    {
        Fixture fixture = Build(g => g.OnStatus(MediaSessionOps.Close, HttpStatusCode.Gone,
            MediaSessionTestData.RefusalBody("session_lost", "session_closed")));

        await fixture.Workspace.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailedOpIsASuccessfulRoundTripAndNotAThrownRequestError()
    {
        // 200 with ok:false is the worker refusing the op; only the transport failing is an HTTP error.
        Fixture fixture = Build(g => g.OnOpError(MediaSessionOps.Stage, "publish_failed", "the PUT was rejected"));

        Func<Task> act = () => fixture.Workspace.StageAsync("projects/p/a.mp4", "a.mp4", CancellationToken.None);

        RunnerSessionOpException ex = (await act.Should().ThrowAsync<RunnerSessionOpException>()).Which;
        ex.Code.Should().Be("publish_failed");
        ex.SessionLost.Should().BeFalse();
    }

    [Fact]
    public void TheOpNamesAreTheProtocolsOwnAndAreAnOpenSet()
    {
        MediaSessionOps.All.Should().Equal("stage", "ffmpeg", "ffprobe", "read", "publish", "stat", "close");
        MediaSessionOps.MaxInlineReadBytes.Should().Be(4 * 1024 * 1024, "the protocol caps an inline read at 4 MiB");
    }
}