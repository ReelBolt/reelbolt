using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// D7b settings, bound from the <c>Compute:SandboxSession</c> section. Everything here is a
/// fabric-only tunable: with <c>Compute:Mode=Local</c> — the default and every self-host install —
/// nothing in this file is reached at all.
/// </summary>
public sealed class SandboxSessionOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Compute:SandboxSession";

    /// <summary>
    /// The template a runner copies into a workspace (<c>assign.spec.template</c>,
    /// docs/runner-protocol.md §"Job types"). Informational for the v1 executor — the runtime image
    /// carries the template — but the protocol requires the member, so it is configuration rather
    /// than a constant.
    /// </summary>
    public string TemplateName { get; set; } = "remotion-template";

    /// <summary>The template version, sent beside <see cref="TemplateName"/>.</summary>
    public string TemplateVersion { get; set; } = "1";

    /// <summary>
    /// How long the first op waits for a runner to be ASSIGNED to a session job before giving up.
    /// The job is <c>Queued</c> until a runner claims it, and <c>run.sessionId</c> is what every op
    /// needs, so this is the bound on "the fabric exists but has no capacity right now".
    /// </summary>
    public int AssignmentWaitSeconds { get; set; } = 120;

    /// <summary>
    /// Lifetime of the presigned GET a hydrate manifest carries. The protocol and
    /// <c>PresignedUrlService</c> both cap a project GET at 15 minutes, and the URL is consumed
    /// within one hydrate op, so the default is the cap.
    /// </summary>
    public int HydrateUrlTtlSeconds { get; set; } = 900;

    /// <summary>The deadline an op with no explicit one is given, for sizing the client's own HTTP timeout.</summary>
    public int DefaultOpDeadlineSeconds { get; set; } = 600;

    /// <summary>
    /// D8: the cap a <c>publish</c> declares when the session is publishing a workspace file purely
    /// to read it back (<see cref="RemoteSandboxSession.OpenRawFileAsync"/>). The protocol's
    /// render-output cap, because that is the object this arm exists for. Default 2 GiB.
    /// </summary>
    public long MaxRawReadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// D8: lifetime of the PUT URL a raw read publishes into. Consumed within one op, so the
    /// protocol's 30-minute PUT cap is the default.
    /// </summary>
    public int StagingPutTtlSeconds { get; set; } = 1800;

    /// <summary>D8: lifetime of the GET URL a raw read is served from. Consumed within one read; the protocol caps a GET at 15 minutes.</summary>
    public int StagingGetTtlSeconds { get; set; } = 900;

    /// <summary>
    /// How long a park (<see cref="RemoteSandboxSession.ParkAsync"/>) waits, per phase, for the
    /// session's job to reach a terminal state - once after the <c>close</c> op, and once more after a
    /// cancel when the close did not end it. The gateway marks a cancelled leased job
    /// <c>Cancelled</c> when the runner acknowledges or when its 10-second grace window closes
    /// (<c>RUNNER_CANCEL_GRACE_SEC</c>), so the default leaves that window three times over.
    /// </summary>
    public int ParkWaitSeconds { get; set; } = 30;
}

/// <summary>
/// The project and organization one execution belongs to. A sandbox session needs both: the
/// organization is what the D6 selector decides eligibility from and what the gateway's job carries,
/// and the project is the object-storage prefix the workspace journal lives under.
/// </summary>
public sealed record SandboxExecutionScope(Guid ExecutionId, Guid ProjectId, Guid OrganizationId);

/// <summary>
/// D7b: resolves the scope of an execution. Its own seam rather than a direct
/// <see cref="WorkflowEngineDbContext"/> dependency so the remote session is testable without a
/// database, exactly as D6 kept <c>IComputeEntitlements</c> separate from
/// <c>DbComputeEntitlements</c>.
/// </summary>
public interface ISandboxExecutionScopeResolver
{
    /// <exception cref="SandboxExecutionUnknownException">The engine has no such execution row.</exception>
    ValueTask<SandboxExecutionScope> ResolveAsync(Guid executionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The production <see cref="ISandboxExecutionScopeResolver"/>: one read of <c>workflow_executions</c>
/// through a short-lived DI scope (the DbContext is scoped, this resolver is not).
/// </summary>
public sealed class DbSandboxExecutionScopeResolver(IServiceScopeFactory scopeFactory) : ISandboxExecutionScopeResolver
{
    /// <inheritdoc />
    public async ValueTask<SandboxExecutionScope> ResolveAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        var row = await db.WorkflowExecutions
            .AsNoTracking()
            .Where(e => e.Id == executionId)
            .Select(e => new { e.ProjectId, e.OrganizationId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? throw new SandboxExecutionUnknownException(executionId)
            : new SandboxExecutionScope(executionId, row.ProjectId, row.OrganizationId);
    }
}

/// <summary>
/// D7b: the workspace session a runner serves, and the journal that lets it survive the runner being
/// lost.
///
/// <para>
/// <b>One session per execution, opened lazily and pinned.</b> The plan's step 1. The engine's sandbox
/// tool is a singleton and resolves a session per call, so the pinning lives in
/// <see cref="ComputeSandboxSessionFactory"/>, which hands out ONE instance per execution; nothing
/// touches the gateway until the first op, so an execution that never writes a file costs nothing.
/// </para>
///
/// <para>
/// <b>Local is a first-class answer, not a fallback bolted on.</b> The D6 selector can answer
/// <see cref="ComputeTargetKind.Local"/> even with <c>Compute:Mode=Fabric</c> — most importantly
/// <c>fabric_unavailable</c>, which is a DEPLOYMENT FACT (a gateway that is not deployed, or answers
/// <c>503 internal_api_disabled</c>) and never a reason to fail a step. So the first op resolves the
/// target once and, when the answer is Local, every subsequent member forwards to a plain
/// <see cref="LocalSandboxSession"/> — byte-identical behaviour to a self-host install.
/// </para>
///
/// <para>
/// <b>Session loss, and why the journal is not optional.</b> A runner's lease expires (a laptop that
/// slept), its session goes <c>Expired</c>, and the next op answers <c>410 session_lost</c>. A
/// reconnected runner answers a repeated <c>opId</c> from its own cache, but that cache dies with the
/// process — so what tells a NEW session which files it must have is
/// <see cref="SandboxWorkspaceJournal"/>. Recovery is: release the execution's session slot, re-resolve
/// the target (a <c>RunnerOnly</c> organization whose runner went away gets its coded refusal here and
/// nothing silently moves to cloud billing), open a new job under a NEW idempotency key, wait for
/// assignment, replay the journal into a <c>hydrate</c> op, and retry the failed op exactly once.
/// </para>
///
/// <para>
/// <b>The byte-moving ops (D8).</b> <c>stage</c>, <c>publish</c> and the raw read a publish implies
/// are implemented here through the presigned-URL transport the protocol defines:
/// <see cref="StageFromUrlAsync"/> and <see cref="PublishToUrlAsync"/> are the two ops, and
/// <see cref="OpenRawFileAsync"/> is the read half of <c>publish</c> — it publishes into the job's
/// staging key space and serves the read from a presigned GET, deleting the staged copy when the
/// caller is done, because a workspace file's bytes never travel inline (the cap is 4 MiB).
/// <see cref="ResolvePublishRouteAsync"/> tells a caller where its published bytes may go, which on
/// this arm is always the staging key space: D12 verifies the object and only then promotes it.
/// ONE member is still refused rather than faked: <see cref="InstallPackagesAsync"/> answers with a
/// refusal the agent can read, because v1 has no package-installation op — the runtime container has
/// no network egress.
/// </para>
/// </summary>
public sealed class RemoteSandboxSession : ISandboxSession
{
    /// <summary>The purpose token the session job's idempotency key is built from.</summary>
    public const string PurposeToken = "workspace-session";

    /// <summary>How many times one op is re-attempted across a session loss. The plan's "retry the failed op once".</summary>
    private const int MaxAttempts = 2;

    /// <summary>The protocol's inline read cap ("<c>maxBytes</c> (at most 4 MiB)").</summary>
    private const long MaxInlineReadBytes = MediaSessionOps.SafeInlineReadBytes;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Guid _executionId;
    private readonly IComputeTarget _compute;
    private readonly RunnerGatewayClient _gateway;
    private readonly ISandboxExecutionScopeResolver _scopes;
    private readonly SandboxWorkspaceJournal _journal;
    private readonly LocalSandboxSessionFactory _localFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPresignedUrlService _presignedUrls;
    private readonly IRunnerOutputStore _outputStore;
    private readonly SandboxSessionOptions _options;
    private readonly TimeSpan _requestTimeout;
    private readonly int _jobWaitSeconds;
    private readonly ILogger _logger;

    /// <summary>
    /// D8: how many staging objects this session has published for raw reads. Part of the staging
    /// key, so two concurrent reads of one session can never address the same object — and the
    /// protocol allows only one op in flight per session anyway.
    ///
    /// <para>
    /// A caller that ALSO publishes (the render path) keeps its own counter, and the two can name the
    /// same integer. That is safe rather than lucky: a raw read DISCARDS the object it published
    /// before it hands the stream back, so by the time the caller publishes under its own index the
    /// session's slot is already gone — and a collision would anyway only overwrite one staging
    /// object of the same job, which nothing reads afterwards.
    /// </para>
    /// </summary>
    private int _rawReadIndex;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Serialises opening a session within this process. The protocol allows ONE op in flight per
    /// session, so two tool calls that both found no session must not both create a job — the second
    /// would block on the execution's own session slot until the first finished.
    /// </summary>
    private readonly SemaphoreSlim _openGate = new(1, 1);

    /// <summary>
    /// Serialises the ops themselves - an op, its session-loss recovery, and the journal entry that
    /// records it - against each other and against <see cref="ParkAsync"/>. The protocol allows one op
    /// in flight per session anyway (a second one is <c>409 op_in_flight</c>), so nothing that worked
    /// before is slowed down; what it adds is that a park can never close the session underneath an op
    /// the runner is still executing, nor between a write and the journal entry that makes the write
    /// survive the park. Always taken BEFORE <see cref="_openGate"/>, never after it.
    /// </summary>
    private readonly SemaphoreSlim _opLock = new(1, 1);

    /// <summary>How long a park waits for an in-flight op: the execution's own session wait, so a park that cannot happen fails exactly as the gate would have.</summary>
    private readonly TimeSpan _sessionWait;

    /// <summary>
    /// True once the session has been PARKED (<see cref="ParkAsync"/>): its runner job is ended and
    /// the execution's slot given back, while the journal stays unsealed. The next op re-opens a
    /// session through the same path a session loss takes - re-resolve the target, a new job under a
    /// new purpose, hydrate from the journal - and clears it.
    /// </summary>
    private bool _parked;

    private SandboxExecutionScope? _scope;
    private ISandboxSession? _local;
    private RunnerJobSession? _job;
    private Guid? _sessionId;
    private int _attempt;
    private DateTime _createdAt;
    private DateTime _lastActivity;

    public RemoteSandboxSession(
        Guid executionId,
        IComputeTarget compute,
        RunnerGatewayClient gateway,
        ISandboxExecutionScopeResolver scopes,
        SandboxWorkspaceJournal journal,
        LocalSandboxSessionFactory localFactory,
        IHttpClientFactory httpClientFactory,
        IPresignedUrlService presignedUrls,
        IRunnerOutputStore outputStore,
        IOptions<ComputeOptions> computeOptions,
        IOptions<SandboxSessionOptions> options,
        ILogger<RemoteSandboxSession> logger)
    {
        _executionId = executionId;
        _compute = compute;
        _gateway = gateway;
        _scopes = scopes;
        _journal = journal;
        _localFactory = localFactory;
        _httpClientFactory = httpClientFactory;
        _presignedUrls = presignedUrls;
        _outputStore = outputStore;
        _options = options.Value;
        _logger = logger;

        ComputeOptions computeSettings = computeOptions.Value;
        _requestTimeout = TimeSpan.FromSeconds(Math.Max(1, computeSettings.RequestTimeoutSeconds));
        _jobWaitSeconds = Math.Clamp(computeSettings.JobWaitSeconds, 0, 60);
        _sessionWait = TimeSpan.FromSeconds(Math.Max(1, computeSettings.SessionWaitSeconds));
        _token = computeSettings.ResolveInternalToken();
    }

    /// <summary>
    /// The gateway's shared secret. Read through <see cref="ComputeOptions.ResolveInternalToken"/> —
    /// the same call <see cref="RunnerGatewayClient"/> makes — so one value configures both, and a
    /// deployment with none refuses rather than calling the internal API unauthenticated.
    /// </summary>
    private readonly string? _token;

    /// <inheritdoc />
    public Guid ExecutionId => _executionId;

    /// <summary>
    /// The runner-side session id, once a runner has been assigned. Null before the first op and
    /// between a session loss and its replacement. Diagnostics only.
    /// </summary>
    public Guid? RunnerSessionId => _sessionId;

    /// <summary>How many runner sessions this execution has needed. 1 for a healthy run. Diagnostics and tests.</summary>
    public int SessionAttempt => _attempt;

    // ------------------------------------------------------------------
    // ISandboxSession
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.EnsureAsync(cancellationToken).ConfigureAwait(false);
        }

        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return DescribeScope();
    }

    /// <inheritdoc />
    public async Task<SandboxInfo> GetAsync(CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.GetAsync(cancellationToken).ConfigureAwait(false);
        }

        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return DescribeScope();
    }

    /// <inheritdoc />
    public async Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.StatusAsync(cancellationToken).ConfigureAwait(false);
        }

        JsonNode result = await RunOpAsync(OpStatus, null, timeoutSeconds: 0, cancellationToken).ConfigureAwait(false);
        string? containerName = _sessionId is { } sessionId
            ? "runner-session-" + sessionId.ToString("D")
            : null;

        // The protocol's status is {workspaceReady, diskUsedBytes, openProcesses} and says nothing
        // about the two facts the engine's own status record carries, so they are PROBED rather than
        // guessed: reporting "no package.json" for a workspace that has one would send the agent
        // looking for a problem that is not there.
        (bool hasPackageJson, bool hasNodeModules) = await ProbeTemplateAsync(cancellationToken).ConfigureAwait(false);

        return new SandboxStatusResponse(
            Exists: true,
            Ready: ReadBool(result, "workspaceReady"),
            HasPackageJson: hasPackageJson,
            HasNodeModules: hasNodeModules,
            ContainerName: containerName,
            WorkspacePath: WorkspaceRoot,
            CreatedAt: _createdAt,
            LastActivity: _lastActivity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.ListFilesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        JsonNode result = await RunOpAsync(
            OpListFiles,
            new JsonObject { ["path"] = path, ["depth"] = 1 },
            timeoutSeconds: 0,
            cancellationToken).ConfigureAwait(false);

        List<SandboxFileEntry> entries = new();
        if (result["entries"] is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is null)
                {
                    continue;
                }

                string entryPath = item["path"]?.GetValue<string>() ?? string.Empty;
                string kind = item["kind"]?.GetValue<string>() ?? "file";
                long bytes = item["bytes"]?.GetValue<long>() ?? 0;

                // The engine's own entry record is (Name, IsDir, Size, ModTime) and its callers read
                // the NAME; the protocol reports a workspace-relative path and no timestamp at all.
                entries.Add(new SandboxFileEntry(
                    Name: LastSegment(entryPath),
                    IsDir: string.Equals(kind, "dir", StringComparison.Ordinal),
                    Size: bytes,
                    ModTime: DateTime.MinValue));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        JsonNode result = await RunOpAsync(
            OpReadFile,
            new JsonObject { ["path"] = path, ["maxBytes"] = MaxInlineReadBytes },
            timeoutSeconds: 0,
            cancellationToken).ConfigureAwait(false);

        string content = result["content"]?.GetValue<string>() ?? string.Empty;
        string encoding = result["encoding"]?.GetValue<string>() ?? "utf8";

        // Binary content comes back base64 (the protocol picks the transport encoding, so a file read
        // is never lossy) but this member's contract is TEXT, so it is decoded rather than mangled.
        string text = string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(content))
            : content;

        return new SandboxTextFile(path, text);
    }

    /// <inheritdoc />
    public async Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.WriteTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await WriteAsync(path, bytes, "utf8", content, cancellationToken).ConfigureAwait(false);
        return new SandboxActionResult(true);
    }

    /// <inheritdoc />
    public async Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            await local.WriteBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteAsync(path, content, "base64", Convert.ToBase64String(content), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.DeletePathAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await SerializedAsync(
            async () =>
            {
                await RunOpCoreAsync(
                    OpDeletePath,
                    new JsonObject { ["path"] = path },
                    timeoutSeconds: 0,
                    cancellationToken).ConfigureAwait(false);

                // Journalled AFTER the op, and after it succeeded: the journal's job is to say what a
                // NEW session must restore, and recording a delete the runner never performed would make
                // a hydrated workspace lose a file the agent still has. Under the same op lock, so a
                // park cannot land between the delete and its entry.
                await RecordAsync(
                    cancellationToken,
                    (projectId, ct) => _journal.RecordDeleteAsync(projectId, _executionId, path, ct)).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return new SandboxActionResult(true);
    }

    /// <inheritdoc />
    public async Task<SandboxExecResult> ExecAsync(
        string command,
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.ExecAsync(command, args, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        JsonArray argumentArray = new();
        foreach (string arg in args)
        {
            argumentArray.Add(arg);
        }

        try
        {
            JsonNode result = await RunOpAsync(
                OpExec,
                new JsonObject { ["command"] = command, ["args"] = argumentArray, ["timeoutSec"] = timeoutSeconds },
                timeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            int exitCode = result["exitCode"]?.GetValue<int>() ?? 0;
            string stdout = result["stdout"]?.GetValue<string>() ?? string.Empty;
            string stderr = result["stderr"]?.GetValue<string>() ?? string.Empty;

            // The engine's exec envelope is the sandbox-executor's {output, error} pair, and the npm
            // helper parses RawBody DIRECTLY rather than through the payload (that is how a body which
            // is not JSON at all has always behaved), so the translation is emitted in that shape
            // instead of leaking the protocol's stdout/stderr pair into a caller that never asked for
            // it. A non-zero exit is a failure with the command's own stderr as its explanation.
            string raw = SerializeExecEnvelope(stdout, stderr);
            bool failed = exitCode != 0;
            return new SandboxExecResult(
                Succeeded: !failed,
                Payload: new SandboxExecResponse(stdout, failed ? stderr : null),
                StatusCode: null,
                RawBody: raw,
                RawError: failed ? stderr : raw,
                TransportError: null);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            // Mirrors LocalSandboxSession: a sandbox that cannot be reached is DATA, because the agent
            // is told to check readiness and retry, whereas a thrown exception escapes the
            // function-invocation layer and takes the whole execution with it.
            return new SandboxExecResult(
                Succeeded: false,
                Payload: new SandboxExecResponse(null, null),
                StatusCode: null,
                RawBody: string.Empty,
                RawError: ex.Message,
                TransportError: ex);
        }
        catch (SandboxRequestFailedException ex)
        {
            // A refused op is DATA here, exactly as a non-zero exit is: the exec tool turns it into a
            // self-correcting answer for the agent rather than an exception that escapes the
            // function-invocation layer and takes the whole execution with it.
            string raw = SerializeExecEnvelope(null, ex.Message);
            return new SandboxExecResult(
                Succeeded: false,
                Payload: new SandboxExecResponse(null, ex.Message),
                StatusCode: (int)ex.StatusCode,
                RawBody: raw,
                RawError: ex.Message,
                TransportError: null);
        }
    }

    /// <summary>
    /// Refused, deliberately: package installation is not an op in v1 ("the runtime container has no
    /// network egress"), so a runner's dependencies are the ones its runtime image carries. The
    /// refusal is a VALUE rather than a throw because this member's existing callers serialize its
    /// result straight back to the agent, which is then free to carry on with what the image has.
    /// </summary>
    public async Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.InstallPackagesAsync(packages, cancellationToken).ConfigureAwait(false);
        }

        return new SandboxExecResponse(
            Output: null,
            Error: "Installing packages is not available on a runner-backed workspace: v1 has no package-installation op "
                   + "because the runtime container has no network egress (docs/runner-protocol.md, \"Remotion workspace ops\"). "
                   + "Use only the packages the runner's runtime image already provides.");
    }

    /// <summary>
    /// D8. A base64 round trip through <c>readFile</c> caps at the protocol's 4 MiB inline limit and
    /// could not carry a render, so the bytes take the road the protocol actually provides: this IS
    /// the read half of <c>publish</c>. The engine mints a PUT into the job's own staging key space,
    /// asks the runner to publish the file into it, serves the read from a presigned GET of that
    /// object, and deletes it when the caller is done.
    ///
    /// <para>
    /// The staging key is <c>runner-staging/{jobId}/{n}</c> and nothing else — the same key space
    /// <see cref="IPresignedUrlService.CreatePutAsync"/> signs and D12's verifier reads, addressed by
    /// a key derived here from the job rather than by anything a caller supplies.
    /// </para>
    /// </summary>
    public async Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.OpenRawFileAsync(path, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        // The job id names the staging key the runner publishes into, so reading it and running the
        // publish are one serialised unit: a park between them would re-open under a different job.
        (Guid jobId, int index) = await SerializedAsync(
            async () =>
            {
                await OpenAsync(cancellationToken).ConfigureAwait(false);
                Guid job = _job?.JobId
                    ?? throw new SandboxSessionUnavailableException(
                        _executionId, "the session has no runner job, so there is no staging key space to publish " + path + " into");

                int n = Interlocked.Increment(ref _rawReadIndex);
                PresignedPut put = await _presignedUrls
                    .CreatePutAsync(job, n, "application/octet-stream", StagingPutTtl())
                    .ConfigureAwait(false);

                await RunOpCoreAsync(
                    OpPublish,
                    new JsonObject
                    {
                        ["path"] = path,
                        ["putUrl"] = put.Url,
                        ["maxBytes"] = _options.MaxRawReadBytes,
                        ["contentType"] = "application/octet-stream"
                    },
                    commandTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                return (job, n);
            },
            cancellationToken).ConfigureAwait(false);

        string stagingKey = S3RunnerOutputStore.StagingKeyFor(jobId, index);
        string url = await _outputStore
            .CreateStagingGetUrlAsync(stagingKey, StagingGetTtl(), cancellationToken)
            .ConfigureAwait(false);

        HttpClient client = _httpClientFactory.CreateClient();
        client.Timeout = _requestTimeout + TimeSpan.FromSeconds(Math.Clamp(commandTimeoutSeconds, 0, LocalSandboxSession.MaxRequestedCommandSeconds) + 30);

        HttpResponseMessage response;
        try
        {
            response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            await DiscardStagingAsync(stagingKey, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            client.Dispose();
            await DiscardStagingAsync(stagingKey, cancellationToken).ConfigureAwait(false);
            throw new SandboxRequestFailedException(
                status,
                "Sandbox API request failed (" + (int)status + "): the staged copy of " + path + " could not be read back");
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new SandboxRawFile(
            client,
            response,
            stream,
            response.Content.Headers.ContentLength,
            () => new ValueTask(DiscardStagingAsync(stagingKey, CancellationToken.None)));
    }

    /// <inheritdoc />
    public async Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.StageFromUrlAsync(request, cancellationToken).ConfigureAwait(false);
        }

        JsonObject args = new()
        {
            ["url"] = request.Url,
            ["path"] = request.Path,
            ["maxBytes"] = request.MaxBytes
        };

        // Omitted when absent rather than sent as null: the protocol's sha256 is optional, and a
        // null a runner might read as a digest is worse than no member at all.
        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            args["sha256"] = request.Sha256;
        }

        JsonNode result = await RunOpAsync(OpStage, args, request.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        return new SandboxTransfer(ReadLong(result, "bytes"), result["sha256"]?.GetValue<string>());
    }

    /// <inheritdoc />
    public async Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.PublishToUrlAsync(request, cancellationToken).ConfigureAwait(false);
        }

        JsonNode result = await RunOpAsync(
            OpPublish,
            new JsonObject
            {
                ["path"] = request.Path,
                ["putUrl"] = request.Url,
                ["maxBytes"] = request.MaxBytes,
                ["contentType"] = request.ContentType
            },
            request.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        // The digest is the RUNNER's claim about what it uploaded. It is returned as a hint and is
        // compared against an engine-computed one by D12 — never treated as the answer.
        return new SandboxTransfer(ReadLong(result, "bytes"), result["sha256"]?.GetValue<string>());
    }

    /// <inheritdoc />
    public async Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.ResolvePublishRouteAsync(cancellationToken).ConfigureAwait(false);
        }

        // Opening first, because the route names the JOB — the object a put URL must target — and a
        // caller asks for the route precisely so it can publish something.
        await OpenAsync(cancellationToken).ConfigureAwait(false);

        Guid? jobId = _job?.JobId;
        RunnerJobTarget? target = ComputeTargets.ToJobTarget(_target);
        return jobId is null || target is null
            ? new SandboxPublishRoute(false, null, null)
            : new SandboxPublishRoute(true, jobId, target);
    }

    /// <summary>
    /// Composition revisions: a session that bound locally (the selector answered Local, or the fabric
    /// is not deployed here) moves the workspace exactly as the local session does. A RUNNER-backed
    /// session refuses with the typed <see cref="SandboxOperationNotSupportedException"/>: the protocol
    /// has no whole-workspace op yet. The intended shape is two ops on presigned URLs, mirroring
    /// <c>publish</c>/<c>stage</c> — the runner tars its workspace (same exclusions, same determinism)
    /// and PUTs it into the job's staging key space, where D12 verifies it before the store promotes it;
    /// a restore is a presigned GET the runner untars with the same validation the sandbox-executor
    /// applies. See docs/design/composition-revisions.md "The Fabric path".
    /// </summary>
    public async Task<SandboxWorkspaceArchive> ExportWorkspaceAsync(int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.ExportWorkspaceAsync(commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        throw new SandboxOperationNotSupportedException(nameof(ExportWorkspaceAsync), nameof(RemoteSandboxSession));
    }

    /// <inheritdoc cref="ExportWorkspaceAsync"/>
    public async Task<SandboxWorkspaceImport> ImportWorkspaceAsync(
        Stream archive,
        long length,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.ImportWorkspaceAsync(archive, length, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        throw new SandboxOperationNotSupportedException(nameof(ImportWorkspaceAsync), nameof(RemoteSandboxSession));
    }

    /// <summary>Delete a staging object this session published for a raw read. Best effort by design.</summary>
    private async Task DiscardStagingAsync(string stagingKey, CancellationToken cancellationToken)
    {
        try
        {
            await _outputStore.DiscardAsync(stagingKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "sandbox: the staging object of execution {ExecutionId} could not be discarded", _executionId);
        }
    }

    private TimeSpan StagingPutTtl()
        => TimeSpan.FromSeconds(Math.Clamp(_options.StagingPutTtlSeconds, 1, (int)PresignedUrlService.MaxPutTtl.TotalSeconds));

    private TimeSpan StagingGetTtl()
        => TimeSpan.FromSeconds(Math.Clamp(_options.StagingGetTtlSeconds, 1, (int)S3RunnerOutputStore.MaxStagingGetTtl.TotalSeconds));

    /// <inheritdoc />
    public async Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken)
    {
        if (await BindAsync(cancellationToken).ConfigureAwait(false) is { } local)
        {
            return await local.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await CloseAsync(releaseSlot: true, cancellationToken).ConfigureAwait(false);
        return new SandboxActionResult(true);
    }

    // ------------------------------------------------------------------
    // Pinning, opening and recovery
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolve the target once — the plan's "opens lazily on the first tool call". Returns the local
    /// session when the selector answered <see cref="ComputeTargetKind.Local"/> (which under
    /// <c>Compute:Mode=Fabric</c> most often means "there is no usable gateway here"), and null when
    /// the work really does go to a runner.
    /// </summary>
    private async Task<ISandboxSession?> BindAsync(CancellationToken cancellationToken)
    {
        if (_local is not null)
        {
            return _local;
        }

        if (_scope is not null || _bound)
        {
            return _local;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_scope is not null || _bound)
            {
                return _local;
            }

            SandboxExecutionScope scope = await _scopes.ResolveAsync(_executionId, cancellationToken).ConfigureAwait(false);
            _scope = scope;

            ComputeDecision decision = await ResolveTargetAsync(scope, cancellationToken).ConfigureAwait(false);
            if (!decision.IsRunnerBacked)
            {
                _local = _localFactory.Create(_executionId);
                _bound = true;
                _logger.LogInformation(
                    "sandbox: execution {ExecutionId} runs its workspace session locally ({Reason})",
                    _executionId, decision.Reason);
                return _local;
            }

            _bound = true;
            _target = decision.Target;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool _bound;
    private ComputeTargetKind _target;

    /// <summary>
    /// Ask the D6 selector, and FAIL the step when it refuses — never re-decide eligibility here.
    /// <para>
    /// The refusal is thrown rather than returned because a sandbox tool call has no step-level result
    /// channel: the tools are invoked by an agent, deep inside a step, so the closest thing to "the
    /// step fails with this code" is an exception that carries it. The executor-level callers D8 and
    /// D11 add consume <c>IsFailure</c> directly, where a real <c>StepExecutionResult</c> is available.
    /// </para>
    /// </summary>
    private async ValueTask<ComputeDecision> ResolveTargetAsync(SandboxExecutionScope scope, CancellationToken cancellationToken)
    {
        ComputeDecision decision = await _compute.ResolveAsync(
            new ComputeTargetRequest(
                ExecutionId: _executionId,
                OrganizationId: scope.OrganizationId,
                JobType: RunnerJobType.RemotionWorkspaceSession,
                StepId: null,
                StepType: "RemotionWorkspace",
                Iteration: 0,
                Purpose: PurposeToken),
            cancellationToken).ConfigureAwait(false);

        if (decision.IsFailure)
        {
            throw new ComputeTargetRefusedException(decision.FailureCode!, decision.Message, _executionId);
        }

        return decision;
    }

    /// <summary>Open a runner session if this execution has none, and return its id.</summary>
    private async Task<Guid> OpenAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is { } existing)
        {
            return existing;
        }

        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task<Guid> OpenCoreAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is { } already)
        {
            return already;
        }

        if (!_bound || _scope is null)
        {
            throw new InvalidOperationException(
                "The sandbox session was opened before its target was resolved; call EnsureAsync or any op first.");
        }

        SandboxExecutionScope scope = _scope;

        if (_job is not null)
        {
            // A park (or a failed re-open) that could not confirm its job ended still holds the slot
            // and left no session to run on. Try to end it again rather than open a second one beside
            // it: two open sessions in one execution is exactly what runner_sessions forbids.
            if (!await EndJobAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new SandboxSessionUnavailableException(
                    _executionId,
                    "the previous runner session of this execution has not ended yet, so a new one cannot be opened beside it");
            }
        }

        bool reopening = _parked;
        if (reopening)
        {
            // A parked workspace re-opens the way a lost one is rebuilt: ask the selector AGAIN,
            // because the runner the session was on may be gone by now - a RunnerOnly organization
            // gets its coded refusal here, and nothing silently moves to cloud billing.
            ComputeDecision decision = await ResolveTargetAsync(scope, cancellationToken).ConfigureAwait(false);
            if (!decision.IsRunnerBacked)
            {
                throw new SandboxSessionUnavailableException(
                    _executionId,
                    "the workspace was parked on a runner and the selector now answers " + decision.Target + " (" + decision.Reason
                    + "); a parked workspace is restored only onto a runner, from its journal");
            }

            _target = decision.Target;
        }

        _attempt++;
        _createdAt = DateTime.UtcNow;

        // The idempotency key is sha256(executionId|stepId|iteration|purpose) — so a REPLACEMENT
        // session needs a different purpose, or the gateway would hand back the Expired job this
        // recovery exists to get away from. That is the whole reason the attempt is in the token.
        string purpose = _attempt == 1
            ? PurposeToken
            : PurposeToken + "-" + _attempt.ToString(CultureInfo.InvariantCulture);

        RunnerJobTarget? target = ComputeTargets.ToJobTarget(_target);
        if (target is null)
        {
            throw new InvalidOperationException("A runner-backed sandbox session needs a runner job target.");
        }

        RunnerJobSession job = await _gateway.OpenJobAsync(
            new RunnerJobRequest(
                ExecutionId: _executionId,
                OrganizationId: scope.OrganizationId,
                JobType: RunnerJobType.RemotionWorkspaceSession,
                Target: target.Value,
                StepId: null,
                Iteration: _attempt - 1,
                Purpose: purpose,
                Spec: BuildSpec()),
            cancellationToken).ConfigureAwait(false);

        _job = job;

        Guid sessionId;
        try
        {
            sessionId = await AwaitAssignmentAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The execution's ONE session slot is held by the job handle, so a failed open must give
            // it back or every later session of this execution waits for a job that will never run.
            await ReleaseJobAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        _sessionId = sessionId;
        _lastActivity = DateTime.UtcNow;
        _logger.LogInformation(
            "sandbox: execution {ExecutionId} opened runner session {SessionId} (job {JobId}, attempt {Attempt}, target {Target})",
            _executionId, sessionId, job.JobId, _attempt, _target);

        if (reopening)
        {
            bool hydrated = false;
            try
            {
                hydrated = await HydrateFromJournalAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!hydrated)
                {
                    // An un-hydrated session must never serve an op - the agent would see a workspace
                    // missing everything it wrote. It is ended (best effort) and the workspace stays
                    // parked, so the next op tries the whole re-open again.
                    await EndJobQuietlyAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (!hydrated)
            {
                throw new SandboxSessionUnavailableException(
                    _executionId, "the parked workspace could not be restored from its journal into the new runner session");
            }

            _parked = false;
        }

        return sessionId;
    }

    /// <summary>
    /// The <c>remotion.workspace-session</c> spec (docs/runner-protocol.md §"Job types"):
    /// <c>{executionId, template: {name, version}}</c>.
    /// </summary>
    private JsonNode BuildSpec() => new JsonObject
    {
        ["executionId"] = _executionId.ToString("D"),
        ["template"] = new JsonObject
        {
            ["name"] = _options.TemplateName,
            ["version"] = _options.TemplateVersion
        }
    };

    /// <summary>
    /// Wait for a runner to be assigned. The job is <c>Queued</c> until then and the ops route is
    /// addressed by SESSION id, so there is nothing to call before this answers.
    /// </summary>
    private async Task<Guid> AwaitAssignmentAsync(RunnerJobSession job, CancellationToken cancellationToken)
    {
        if (job.SessionId is { } assigned)
        {
            return assigned;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.AssignmentWaitSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            RunnerJobView? view = await job
                .RefreshAsync(TimeSpan.FromSeconds(_jobWaitSeconds > 0 ? _jobWaitSeconds : 5), cancellationToken)
                .ConfigureAwait(false);

            if (view is null)
            {
                throw new SandboxSessionUnavailableException(
                    _executionId, "the runner gateway no longer knows job " + job.JobId.ToString("D"));
            }

            if (view.SessionId is { } sessionId)
            {
                return sessionId;
            }

            if (view.IsTerminal)
            {
                throw new SandboxSessionUnavailableException(
                    _executionId,
                    "the session job ended as " + (view.Status?.ToString() ?? "an unknown status")
                    + " before a runner took it" + (view.ErrorClass is null ? string.Empty : " (" + view.ErrorClass + ")"));
            }
        }

        throw new SandboxSessionUnavailableException(
            _executionId,
            "no runner was assigned to the session within " + _options.AssignmentWaitSeconds.ToString(CultureInfo.InvariantCulture) + " seconds");
    }

    /// <summary>
    /// Run one op, recovering from a lost session once (the plan's step 3).
    /// <para>
    /// A REPEATED <c>opId</c> is what makes a retry safe on the SAME session — the worker answers from
    /// its own 256-entry cache — so a timeout or an unreachable owner replica is retried with the id it
    /// already had. A NEW session is a different thing entirely: nothing is deduplicated across
    /// epochs ("an op executed under epoch 1 and replayed under epoch 2 is a new session on a new runner
    /// and is not deduplicated"), so a recovery mints a fresh id.
    /// </para>
    /// </summary>
    private Task<JsonNode> RunOpAsync(string op, JsonNode? args, int timeoutSeconds, CancellationToken cancellationToken)
        => SerializedAsync(() => RunOpCoreAsync(op, args, timeoutSeconds, cancellationToken), cancellationToken);

    /// <summary>Run <paramref name="body"/> under <see cref="_opLock"/>. The lock is not re-entrant, so a body calls <see cref="RunOpCoreAsync"/>, never <see cref="RunOpAsync"/>.</summary>
    private async Task<T> SerializedAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary><see cref="RunOpAsync"/> without the op lock, for a caller that already holds it.</summary>
    private async Task<JsonNode> RunOpCoreAsync(string op, JsonNode? args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        string opId = op + "-" + Guid.NewGuid().ToString("N");
        Exception? lastError = null;

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                opId = op + "-" + Guid.NewGuid().ToString("N");
            }

            Guid sessionId = await OpenAsync(cancellationToken).ConfigureAwait(false);
            OpOutcome outcome = await PostOpAsync(sessionId, opId, op, args, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            switch (outcome.Kind)
            {
                case OpOutcomeKind.Ok:
                    _lastActivity = DateTime.UtcNow;
                    return outcome.Result ?? new JsonObject();

                case OpOutcomeKind.Refused:
                    // The round trip succeeded and the runner (or the gateway) said no. Kept in the
                    // ENGINE's own failure shape — SandboxRequestFailedException carrying an HTTP
                    // status — because that is the contract every existing caller branches on: the
                    // sandbox tools read StatusCode.NotFound to mean "that directory contains no
                    // files", which is an answer rather than a failure.
                    _lastActivity = DateTime.UtcNow;
                    throw new SandboxRequestFailedException(
                        outcome.StatusCode,
                        "Sandbox API request failed (" + (int)outcome.StatusCode + "): "
                        + (string.IsNullOrEmpty(outcome.ErrorCode) ? string.Empty : outcome.ErrorCode + ": ")
                        + outcome.ErrorMessage);

                case OpOutcomeKind.Timeout:
                case OpOutcomeKind.OwnerUnreachable:
                    // The session may well still be alive and may well have executed the op; the same
                    // opId is exactly what makes asking again safe.
                    lastError = new SandboxSessionUnavailableException(_executionId, outcome.ErrorCode + ": " + outcome.ErrorMessage);
                    if (attempt + 1 < MaxAttempts)
                    {
                        // The opId is deliberately NOT regenerated: the retry is against the same
                        // session, and the worker's cache is what makes that safe.
                        continue;
                    }

                    throw lastError;

                case OpOutcomeKind.SessionLost:
                    lastError = new SandboxSessionLostException(_executionId, outcome.ErrorCode + ": " + outcome.ErrorMessage);
                    if (attempt + 1 < MaxAttempts &&
                        await TryRecoverAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    throw lastError;

                case OpOutcomeKind.SessionNotFound:
                    // Deliberately NOT recovered, and the protocol says why: "404 session_not_found —
                    // no such session was ever opened. The engine must not retry it." Our session id
                    // came from the gateway, so a 404 means the id is not one the gateway knows, and
                    // opening a replacement would paper over whatever produced that.
                    throw new SandboxSessionLostException(
                        _executionId,
                        "the gateway does not know session " + (_sessionId?.ToString("D") ?? "<none>") + ": " + outcome.ErrorMessage);

                default:
                    throw new SandboxRequestFailedException(
                        HttpStatusCode.InternalServerError,
                        "Sandbox API request failed: the sandbox session of execution " + _executionId.ToString("D") + " answered in a way this build does not understand");
            }
        }

        throw lastError ?? new SandboxSessionUnavailableException(_executionId, "the op could not be run");
    }

    /// <summary>
    /// Replace a lost session: give back the execution's slot, re-resolve the target (the plan's "open
    /// a new session (CloudPool fallback only if the selector allows it)"), open a new job, and replay
    /// the journal into it. False means "there is no second chance", and the caller rethrows.
    /// </summary>
    private async Task<bool> TryRecoverAsync(CancellationToken cancellationToken)
    {
        if (_scope is null)
        {
            return false;
        }

        try
        {
            _logger.LogWarning(
                "sandbox: execution {ExecutionId} lost runner session {SessionId}; rebuilding it from the workspace journal",
                _executionId, _sessionId);

            // The slot FIRST: runner_sessions allows exactly one open session per execution, so a
            // replacement cannot be created while the expired handle still holds it.
            await ReleaseJobAsync(cancellationToken).ConfigureAwait(false);

            ComputeDecision decision = await ResolveTargetAsync(_scope, cancellationToken).ConfigureAwait(false);
            if (!decision.IsRunnerBacked)
            {
                _logger.LogWarning(
                    "sandbox: execution {ExecutionId} cannot reopen on a runner ({Reason}); the op fails",
                    _executionId, decision.Reason);
                return false;
            }

            _target = decision.Target;
            Guid sessionId = await OpenAsync(cancellationToken).ConfigureAwait(false);

            return await HydrateFromJournalAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (ComputeTargetRefusedException)
        {
            // A refusal from the selector is a DECISION — "this organization may not use the pool" —
            // not an infrastructure hiccup, so it goes to the caller with its own failure code rather
            // than being flattened into "the session could not be rebuilt".
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "sandbox: rebuilding the runner session for execution {ExecutionId} failed", _executionId);
            return false;
        }
    }

    /// <summary>
    /// Replay the journal into a NEW session with one <c>hydrate</c> op - the single restore path,
    /// shared by session-loss recovery (<see cref="TryRecoverAsync"/>) and the re-open of a parked
    /// workspace (<see cref="OpenCoreAsync"/>). False when the runner refused the hydrate.
    /// </summary>
    private async Task<bool> HydrateFromJournalAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_scope is null)
        {
            return false;
        }

        IReadOnlyList<SandboxHydrateEntry> manifest = await _journal.BuildManifestAsync(
            _scope.ProjectId,
            _executionId,
            TimeSpan.FromSeconds(Math.Clamp(_options.HydrateUrlTtlSeconds, 1, (int)SandboxWorkspaceJournal.MaxHydrateUrlTtl.TotalSeconds)),
            cancellationToken).ConfigureAwait(false);

        if (manifest.Count == 0)
        {
            // Nothing was written, so there is nothing to restore and no reason to spend a round
            // trip on an empty hydrate.
            return true;
        }

        JsonArray entries = new();
        foreach (SandboxHydrateEntry entry in manifest)
        {
            entries.Add(new JsonObject
            {
                ["path"] = entry.Path,
                ["sha256"] = entry.Sha256,
                ["url"] = entry.Url
            });
        }

        // Through PostOpAsync rather than RunOpAsync: a hydrate that itself fails must NOT trigger
        // another recovery, or two runners alternating failures would loop forever.
        OpOutcome outcome = await PostOpAsync(
            sessionId,
            OpHydrate + "-" + Guid.NewGuid().ToString("N"),
            OpHydrate,
            new JsonObject { ["manifest"] = entries },
            _options.DefaultOpDeadlineSeconds,
            cancellationToken).ConfigureAwait(false);

        if (outcome.Kind != OpOutcomeKind.Ok)
        {
            _logger.LogWarning(
                "sandbox: hydration of execution {ExecutionId} into session {SessionId} failed ({Code}: {Message})",
                _executionId, sessionId, outcome.ErrorCode, outcome.ErrorMessage);
            return false;
        }

        _logger.LogInformation(
            "sandbox: execution {ExecutionId} hydrated {Count} file(s) into runner session {SessionId}",
            _executionId, manifest.Count, sessionId);
        return true;
    }

    // ------------------------------------------------------------------
    // Parking
    // ------------------------------------------------------------------

    /// <summary>
    /// Give the execution's ONE runner-session slot back WITHOUT ending the workspace, so another unit
    /// of the same execution - a runner-backed <c>VideoAnalyze</c>/<c>VideoCompile</c> step - can open
    /// its own session.
    ///
    /// <para>
    /// <b>Why this exists.</b> The session pins its job, and with it the slot
    /// <see cref="IExecutionSessionGate"/> hands out, for the whole execution; until this existed the
    /// only things that let go of it were the execution ending and the session's OWN ops losing their
    /// runner. A live run with an Author step (sandbox tools) at step 3 and a <c>VideoAnalyze</c> at
    /// step 5, both sent to the org's runner, sat with exactly one job - the Author's, still
    /// <c>Running</c>, its <c>runner_sessions</c> row open - while the analyze step waited for a slot
    /// whose owner had finished its step minutes earlier, and would have failed
    /// <c>RUNNER_SESSION_BUSY</c> after <c>Compute:SessionWaitSeconds</c>. Killing the runner did not
    /// help: nothing watches the job, so the in-process lease stayed held.
    /// </para>
    ///
    /// <para>
    /// <b>What a park does.</b> Under the op lock (so never while an op, or a write and its journal
    /// entry, is in flight) and the open gate: the session is closed with the protocol's own
    /// <c>close</c> op - which is how the gateway ends a session's job (<c>finishClosedSession</c>:
    /// "session X closed by the engine: job Y is Succeeded") - and the job is read until it is terminal,
    /// because the gateway finishes the job AFTER it has answered the op. A close that fails, or a job
    /// that does not end, is followed by a cancel and a second wait that covers the gateway's cancel
    /// grace window (a runner that never acknowledges is marked <c>Cancelled</c> when it closes). Only a
    /// job confirmed terminal - which closes its <c>runner_sessions</c> row in the same transaction - lets
    /// the slot go. The journal is NOT sealed: the next op re-opens a session exactly as a session loss
    /// is rebuilt (<see cref="OpenCoreAsync"/>, <see cref="HydrateFromJournalAsync"/>).
    /// </para>
    ///
    /// <para>
    /// A session that holds no slot (never opened, bound locally, already parked) is a no-op that makes
    /// no gateway call at all. A job that cannot be confirmed ended keeps the slot and answers
    /// <see cref="SandboxParkOutcome.StillOpen"/>: holding a slot that is not needed costs one step's
    /// coded failure, while releasing one that IS still in use would put two sessions in the execution.
    /// </para>
    /// </summary>
    /// <exception cref="ComputeFabricUnavailableException">The gateway is unreachable; the session is left as it was.</exception>
    public async Task<SandboxParkOutcome> ParkAsync(CancellationToken cancellationToken = default)
    {
        if (_local is not null)
        {
            // A local workspace never took the execution's runner slot.
            return SandboxParkOutcome.NothingToPark;
        }

        if (!await _opLock.WaitAsync(_sessionWait, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "sandbox: execution {ExecutionId} could not park its runner session: an op on it was still running after {Seconds}s",
                _executionId, (int)_sessionWait.TotalSeconds);
            return SandboxParkOutcome.StillOpen;
        }

        try
        {
            await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_job is not { } job)
                {
                    return SandboxParkOutcome.NothingToPark;
                }

                Guid? sessionId = _sessionId;
                if (!await EndJobAsync(cancellationToken).ConfigureAwait(false))
                {
                    return SandboxParkOutcome.StillOpen;
                }

                _logger.LogInformation(
                    "sandbox: execution {ExecutionId} parked runner session {SessionId} (job {JobId}) so another step can take the execution's slot; the next sandbox op re-opens it from the workspace journal",
                    _executionId, sessionId, job.JobId);
                return SandboxParkOutcome.Parked;
            }
            finally
            {
                _openGate.Release();
            }
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>
    /// End the current job on the gateway and, once it is confirmed terminal, release the slot. Marks
    /// the workspace parked whatever the outcome: from the first call on, the runner's copy of the
    /// workspace is being torn down, so no op may run on that session again and the next session has to
    /// be hydrated. True when there is no job left holding the slot.
    /// </summary>
    private async Task<bool> EndJobAsync(CancellationToken cancellationToken)
    {
        if (_job is not { } job)
        {
            return true;
        }

        Guid? sessionId = _sessionId;
        bool wasParked = _parked;
        _sessionId = null;
        _parked = true;

        TimeSpan wait = TimeSpan.FromSeconds(Math.Max(1, _options.ParkWaitSeconds));
        bool closed = false;
        bool ended = false;

        try
        {
            if (sessionId is { } open)
            {
                closed = await TryCloseRunnerSessionAsync(open, cancellationToken).ConfigureAwait(false);
            }

            if (closed)
            {
                ended = await AwaitJobEndAsync(job, wait, cancellationToken).ConfigureAwait(false);
            }

            if (!ended)
            {
                // The close did not end the job (it failed, or the gateway did not finish the job):
                // cancel it. A leased job becomes Cancelled when its runner acknowledges or when the
                // gateway's grace window closes, so a runner that never answers still lets this end.
                RunnerJobView? cancelled = await job.CancelAsync(cancellationToken).ConfigureAwait(false);
                ended = cancelled is null
                    || cancelled.IsTerminal
                    || await AwaitJobEndAsync(job, wait, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ComputeFabricUnavailableException) when (!closed)
        {
            // The gateway was unreachable before the runner accepted the close, so the workspace is
            // untouched: put the session back exactly as it was for the caller's fallback.
            _sessionId = sessionId;
            _parked = wasParked;
            throw;
        }

        if (!ended)
        {
            _logger.LogWarning(
                "sandbox: the runner job {JobId} of execution {ExecutionId} did not end within {Seconds}s of being closed and cancelled; the session slot stays held",
                job.JobId, _executionId, (int)wait.TotalSeconds);
            return false;
        }

        await ReleaseJobAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary><see cref="EndJobAsync"/> for a path that is already failing: its own failure is logged, never thrown over the original one.</summary>
    private async Task EndJobQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EndJobAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "sandbox: ending the un-hydrated runner session of execution {ExecutionId} failed", _executionId);
        }
    }

    /// <summary>
    /// The <c>close</c> op, for a park. True only when the runner accepted it. Unlike the execution-end
    /// close in <see cref="CloseAsync"/>, an unreachable gateway is NOT swallowed: a caller that cannot
    /// reach the gateway cannot open its own session either, and must hear so rather than wait.
    /// </summary>
    private async Task<bool> TryCloseRunnerSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            OpOutcome outcome = await PostOpAsync(
                sessionId,
                OpClose + "-" + Guid.NewGuid().ToString("N"),
                OpClose,
                null,
                timeoutSeconds: 0,
                cancellationToken).ConfigureAwait(false);
            return outcome.Kind == OpOutcomeKind.Ok;
        }
        catch (Exception ex) when (ex is not ComputeFabricUnavailableException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "sandbox: closing runner session {SessionId} for a park failed; cancelling its job instead", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Read the job until it is terminal or <paramref name="wait"/> runs out. A job the gateway no
    /// longer knows has no session either, so it counts as ended. The read long-polls on a real
    /// gateway; the pause between reads only matters to one that answers at once.
    /// </summary>
    private async Task<bool> AwaitJobEndAsync(RunnerJobSession job, TimeSpan wait, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            double remaining = Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalSeconds);
            int poll = (int)Math.Min(remaining, _jobWaitSeconds > 0 ? _jobWaitSeconds : 5);
            RunnerJobView? view = await job
                .RefreshAsync(TimeSpan.FromSeconds(Math.Max(1, poll)), cancellationToken)
                .ConfigureAwait(false);

            if (view is null || view.IsTerminal)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(ParkPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan ParkPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// POST the op and classify the answer. The distinction the protocol is built around: a failed op
    /// is a <c>200</c> with <c>ok:false</c>, and only the TRANSPORT failing is an HTTP error.
    /// </summary>
    private async Task<OpOutcome> PostOpAsync(
        Guid sessionId,
        string opId,
        string op,
        JsonNode? args,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        string what = "POST internal/v1/sessions/{id}/ops (" + op + ")";
        using HttpClient client = CreateClient(timeoutSeconds);
        using HttpResponseMessage response = await CallAsync(
            () => client.PostAsJsonAsync(
                "internal/v1/sessions/" + sessionId.ToString("D") + "/ops",
                new SessionOpPayload(opId, op, args, timeoutSeconds),
                Json,
                cancellationToken),
            what,
            cancellationToken).ConfigureAwait(false);

        SessionOpResult? body = null;
        try
        {
            body = await response.Content
                .ReadFromJsonAsync<SessionOpResult>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // An unparseable body is a gateway bug, not a caller mistake, and the status below still
            // says which failure it was, so this is not fatal on its own.
        }

        string code = body?.Error?.Code ?? string.Empty;
        string message = body?.Error?.Message ?? string.Empty;

        if (response.IsSuccessStatusCode)
        {
            if (body is null)
            {
                throw new RunnerGatewayRequestException((int)response.StatusCode, what, "the gateway returned a body this client could not read");
            }

            if (!body.Ok)
            {
                // "An opId that has aged out of the cache and is repeated returns error op_expired,
                // which the engine treats as session loss." The one op-level error that is not a
                // refusal.
                return string.Equals(code, "op_expired", StringComparison.Ordinal)
                    ? new OpOutcome(OpOutcomeKind.SessionLost, null, code, message, HttpStatusCode.Gone)
                    : new OpOutcome(OpOutcomeKind.Refused, null, code, message, StatusForOpCode(code));
            }

            return new OpOutcome(OpOutcomeKind.Ok, body.Result, null, null);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Gone => new OpOutcome(OpOutcomeKind.SessionLost, null, "session_lost", message),

            // The gateway lost the session row itself. The protocol is explicit that the engine must
            // not retry this one, so it is a distinct outcome from session_lost rather than folded in.
            HttpStatusCode.NotFound => new OpOutcome(OpOutcomeKind.SessionNotFound, null, "session_not_found", message),

            HttpStatusCode.GatewayTimeout => new OpOutcome(OpOutcomeKind.Timeout, null, "op_timeout", message),

            HttpStatusCode.BadGateway => new OpOutcome(OpOutcomeKind.OwnerUnreachable, null, "session_owner_unreachable", message),

            // op_in_flight means the engine put two ops on one session at once; op_cancelled means the
            // job was cancelled or the execution ended while this op ran. Neither is fixed by opening
            // a new session, so both are refusals and neither triggers a recovery.
            HttpStatusCode.Conflict => new OpOutcome(
                OpOutcomeKind.Refused,
                null,
                string.IsNullOrEmpty(code) ? "conflict" : code,
                message,
                HttpStatusCode.Conflict),

            HttpStatusCode.ServiceUnavailable => throw new ComputeFabricUnavailableException(
                (int)response.StatusCode,
                what,
                "the runner gateway has its internal API closed (RUNNER_INTERNAL_TOKEN is not configured)"),

            _ => throw new RunnerGatewayRequestException((int)response.StatusCode, what, code + " " + message)
        };
    }

    /// <summary>
    /// The client for one op. Its timeout has to outlive the op's OWN deadline, for the reason
    /// <see cref="LocalSandboxSession.ResolveHttpTimeout"/> exists: a client that gives up first turns a
    /// long render into a phantom transport error while the runner is still working.
    /// </summary>
    private HttpClient CreateClient(int timeoutSeconds)
    {
        if (_token is null)
        {
            throw new ComputeFabricUnavailableException(
                "the compute fabric is switched on (Compute:Mode=Fabric) but neither Compute:InternalToken nor RUNNER_INTERNAL_TOKEN is configured, so the runner gateway cannot be called");
        }

        int deadline = timeoutSeconds > 0 ? timeoutSeconds : _options.DefaultOpDeadlineSeconds;
        HttpClient client = _httpClientFactory.CreateClient(RunnerGatewayClient.HttpClientName);
        client.Timeout = _requestTimeout + TimeSpan.FromSeconds(Math.Clamp(deadline, 1, LocalSandboxSession.MaxRequestedCommandSeconds) + 30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    /// <summary>
    /// Runs one gateway call, mapping "there is no usable gateway here" onto
    /// <see cref="ComputeFabricUnavailableException"/> — the same rule, and therefore the same
    /// exception, as <see cref="RunnerGatewayClient"/>, so a caller degrades identically whichever
    /// member of the fabric it called.
    /// </summary>
    private static async Task<T> CallAsync<T>(Func<Task<T>> call, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new ComputeFabricUnavailableException("the runner gateway timed out (" + what + ")", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ComputeFabricUnavailableException("the runner gateway is unreachable (" + what + "): " + ex.Message, ex);
        }
    }

    private static bool IsTransportFailure(Exception ex)
        => ex is ComputeFabricUnavailableException or HttpRequestException or IOException or TaskCanceledException;

    /// <summary>
    /// The HTTP status the engine's own failure contract reports for a protocol error code. Only
    /// <c>not_found</c> and <c>file_too_large</c> carry meaning beyond "refused", and both do: the
    /// sandbox tools treat a missing directory as an ANSWER (an empty listing) rather than a failure.
    /// An unrecognised code is an unknown case — the protocol's open-set rule — and reads as a plain
    /// bad request rather than as a silent success.
    /// </summary>
    private static HttpStatusCode StatusForOpCode(string? code) => code switch
    {
        "not_found" => HttpStatusCode.NotFound,
        "file_too_large" => HttpStatusCode.RequestEntityTooLarge,
        "disk_full" => HttpStatusCode.RequestEntityTooLarge,
        _ => HttpStatusCode.BadRequest
    };

    /// <summary>The engine's exec envelope, so the npm helper's own parsing keeps working unchanged.</summary>
    private static string SerializeExecEnvelope(string? output, string? error)
        => JsonSerializer.Serialize(new SandboxExecResponse(output, error), Json);

    /// <summary>Write text or bytes and journal the content. The op is journalled only after it succeeded.</summary>
    private async Task WriteAsync(
        string path,
        byte[] bytes,
        string encoding,
        string content,
        CancellationToken cancellationToken)
    {
        // The op and its journal entry are one unit under the op lock: a park that landed between
        // them would hydrate the next session from a journal that does not yet know about this write.
        await SerializedAsync(
            async () =>
            {
                await RunOpCoreAsync(
                    OpWriteFile,
                    new JsonObject { ["path"] = path, ["content"] = content, ["encoding"] = encoding },
                    timeoutSeconds: 0,
                    cancellationToken).ConfigureAwait(false);

                await RecordAsync(
                    cancellationToken,
                    (projectId, ct) => _journal.RecordWriteAsync(projectId, _executionId, path, bytes, ct)).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Journal one mutation. A journal that cannot be written is reported rather than swallowed: the
    /// workspace's durability is the entire point of this work package, and an execution that quietly
    /// lost it would only find out when a laptop closed its lid.
    /// </summary>
    private async Task RecordAsync(
        CancellationToken cancellationToken,
        Func<Guid, CancellationToken, Task<SandboxJournalEntry>> record)
    {
        if (_scope is null)
        {
            return;
        }

        try
        {
            await record(_scope.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "sandbox: execution {ExecutionId} wrote to its workspace but could not journal the change; a later session loss would lose it",
                _executionId);
            throw new SandboxJournalUnavailableException(_executionId, ex);
        }
    }

    /// <summary>
    /// <c>workspaceReady</c> plus the two template facts the engine's status record carries, which the
    /// protocol does not report. A listing that fails is not a status failure: the ready flag above is
    /// the answer, and "no package.json" is the safe reading of an unreadable listing for a caller
    /// deciding whether to write sources.
    /// </summary>
    private async Task<(bool HasPackageJson, bool HasNodeModules)> ProbeTemplateAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<SandboxFileEntry> root = await ListFilesAsync(".", cancellationToken).ConfigureAwait(false);
            bool packageJson = root.Any(e => !e.IsDir && string.Equals(e.Name, "package.json", StringComparison.Ordinal));
            bool nodeModules = root.Any(e => e.IsDir && string.Equals(e.Name, "node_modules", StringComparison.Ordinal));
            return (packageJson, nodeModules);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "sandbox: the workspace listing behind execution {ExecutionId}'s status probe failed", _executionId);
            return (false, false);
        }
    }

    /// <summary>Our own session record. The protocol reports no container name or host path, and inventing one would be worse than saying what is true.</summary>
    private SandboxInfo DescribeScope() => new(
        Id: _sessionId?.ToString("D") ?? _executionId.ToString("D"),
        WorkflowExecutionId: _executionId.ToString("D"),
        ContainerName: _sessionId is { } sessionId ? "runner-session-" + sessionId.ToString("D") : "runner",
        WorkspacePath: WorkspaceRoot,
        CreatedAt: _createdAt,
        LastActivity: _lastActivity);

    private static bool ReadBool(JsonNode node, string name) => node[name]?.GetValue<bool>() ?? false;

    private static long ReadLong(JsonNode node, string name) => node[name]?.GetValue<long>() ?? 0;

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Release the execution's session slot. Idempotent, and it never throws.</summary>
    private async Task ReleaseJobAsync(CancellationToken cancellationToken)
    {
        RunnerJobSession? job = _job;
        _job = null;
        _sessionId = null;

        if (job is null)
        {
            return;
        }

        try
        {
            // Disposing IS the release (RunnerJobSession's own contract). The job itself is left to the
            // gateway: a session job whose lease lapses goes Expired on its own, and cancelling it here
            // would race the very assignment this handle may be waiting on.
            await job.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "sandbox: releasing the session slot of execution {ExecutionId} failed", _executionId);
        }
    }

    /// <summary>
    /// End the session: close the workspace on the runner (best effort — a runner that is gone has
    /// nothing left to close), release the execution's slot, and seal the journal.
    /// </summary>
    private async Task CloseAsync(bool releaseSlot, CancellationToken cancellationToken)
    {
        if (_sessionId is { } sessionId)
        {
            try
            {
                await PostOpAsync(
                    sessionId,
                    OpClose + "-" + Guid.NewGuid().ToString("N"),
                    OpClose,
                    null,
                    timeoutSeconds: 0,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "sandbox: closing runner session {SessionId} failed; the runner will reap it", sessionId);
            }
        }

        if (releaseSlot)
        {
            await ReleaseJobAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_scope is { } scope)
        {
            await _journal.SealAsync(scope.ProjectId, _executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called by the session registry when the execution ends, whatever the outcome. It must not
    /// throw: it runs in the workflow runner's <c>finally</c>, where an exception would replace the
    /// execution's real result.
    /// </summary>
    public async Task CloseForExecutionEndAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_bound)
                {
                    // Never used: nothing was opened, nothing was journalled, nothing to release.
                    return;
                }

                await CloseAsync(releaseSlot: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "sandbox: closing the session of execution {ExecutionId} failed", _executionId);
        }
    }

    // --- op names. The gateway's session-op route takes them as strings, and
    // RunnerSandboxContractTests reads the Go source and fails when one drifts. ---

    private const string OpWriteFile = "writeFile";
    private const string OpReadFile = "readFile";
    private const string OpListFiles = "listFiles";
    private const string OpDeletePath = "deletePath";
    private const string OpExec = "exec";
    private const string OpStatus = "status";
    private const string OpHydrate = "hydrate";
    private const string OpClose = "close";
    private const string OpStage = "stage";
    private const string OpPublish = "publish";

    /// <summary>Where a workspace root is, as far as this implementation can say: everywhere the protocol names a path, it is relative to it.</summary>
    private const string WorkspaceRoot = ".";

    private enum OpOutcomeKind
    {
        Ok,
        Refused,
        SessionLost,
        SessionNotFound,
        Timeout,
        OwnerUnreachable
    }

    private sealed record OpOutcome(
        OpOutcomeKind Kind,
        JsonNode? Result,
        string? ErrorCode,
        string? ErrorMessage,
        HttpStatusCode StatusCode = HttpStatusCode.BadRequest);

    // --- wire DTOs. Names are the gateway's. ---

    private sealed record SessionOpPayload(
        string OpId,
        string Op,
        JsonNode? Args,
        int TimeoutSec);

    private sealed record SessionOpResult(
        Guid SessionId,
        Guid JobId,
        string OpId,
        bool Ok,
        JsonNode? Result,
        SessionOpError? Error,
        long DurationMs,
        string? ServedBy);

    private sealed record SessionOpError(string? Code, string? Message, bool Retryable);
}

/// <summary>
/// D7b: the session a caller gets for an execution — pinned, so the plan's "opens lazily on the first
/// tool call and is pinned per execution" holds no matter how many times a tool resolves one — plus
/// the execution-endhook the workflow runner calls from its <c>finally</c>.
/// </summary>
public interface ISandboxSessionRegistry
{
    /// <summary>
    /// End the execution's sandbox session and seal its journal. Called once per execution, whatever
    /// its outcome; it never throws, because it runs after the execution has already decided its result.
    /// </summary>
    Task CloseExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Make the execution's runner-session slot available to another unit of the same execution by
    /// PARKING its sandbox session (<see cref="RemoteSandboxSession.ParkAsync"/>): the runner job is
    /// ended and the slot released, the journal is left open, and the next sandbox op re-opens a session
    /// and hydrates it. <see cref="IMediaWorkspaceFactory"/> calls this on its runner path, right before
    /// it takes the slot for a media job.
    /// <para>
    /// Answers <see cref="SandboxParkOutcome.NothingToPark"/> without a single gateway call under
    /// <c>Compute:Mode=Local</c>, for an execution with no sandbox session, and for a session that holds
    /// no slot.
    /// </para>
    /// </summary>
    Task<SandboxParkOutcome> ParkExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="ISandboxSessionRegistry.ParkExecutionAsync"/> did.</summary>
public enum SandboxParkOutcome
{
    /// <summary>The execution's sandbox session held no runner slot; nothing was called.</summary>
    NothingToPark = 0,

    /// <summary>The session's job is confirmed ended and the slot is free; the workspace re-opens from its journal on the next op.</summary>
    Parked = 1,

    /// <summary>
    /// The slot could not be freed: an op was still running on the session, or its job did not reach a
    /// terminal state after a close and a cancel. The slot stays held, and a caller that needs it must
    /// fail with <c>RUNNER_SESSION_BUSY</c> rather than wait for it.
    /// </summary>
    StillOpen = 2
}

/// <summary>
/// The composition-root factory for <see cref="ISandboxSession"/>.
/// <para>
/// <b><c>Compute:Mode=Local</c> is untouched by construction.</b> The mode is read from options — no
/// database, no gateway, no dictionary entry — and a Local deployment gets exactly the
/// <see cref="LocalSandboxSession"/> it got before this work package existed. Only a fabric deployment
/// pays for the pinning, and even then nothing reaches the network until the first op.
/// </para>
/// </summary>
public sealed class ComputeSandboxSessionFactory : ISandboxSessionFactory, ISandboxSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, RemoteSandboxSession> _sessions = new();
    private readonly LocalSandboxSessionFactory _localFactory;
    private readonly IServiceProvider _services;
    private readonly ComputeOptions _computeOptions;

    public ComputeSandboxSessionFactory(
        LocalSandboxSessionFactory localFactory,
        IServiceProvider services,
        IOptions<ComputeOptions> computeOptions)
    {
        _localFactory = localFactory;
        _services = services;
        _computeOptions = computeOptions.Value;
    }

    /// <inheritdoc />
    public ISandboxSession Create(Guid executionId)
    {
        if (_computeOptions.Mode != ComputeMode.Fabric)
        {
            return _localFactory.Create(executionId);
        }

        return GetOrAdd(executionId);
    }

    /// <summary>How many executions currently hold a pinned session. Diagnostics and tests.</summary>
    public int TrackedExecutions => _sessions.Count;

    /// <inheritdoc />
    public async Task CloseExecutionAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(executionId, out RemoteSandboxSession? session))
        {
            return;
        }

        await session.CloseForExecutionEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SandboxParkOutcome> ParkExecutionAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        // Read from options and the dictionary only: a Local deployment, or an execution that never
        // resolved a sandbox session, answers without touching anything.
        if (_computeOptions.Mode != ComputeMode.Fabric ||
            !_sessions.TryGetValue(executionId, out RemoteSandboxSession? session))
        {
            return Task.FromResult(SandboxParkOutcome.NothingToPark);
        }

        return session.ParkAsync(cancellationToken);
    }

    /// <summary>
    /// The pinned session for one execution. Built through the service provider rather than by hand so
    /// that a test can substitute any of its collaborators with the ordinary DI registrations.
    /// </summary>
    private RemoteSandboxSession GetOrAdd(Guid executionId)
        => _sessions.GetOrAdd(
            executionId,
            id => ActivatorUtilities.CreateInstance<RemoteSandboxSession>(_services, id));
}

/// <summary>The engine could not open a usable runner session for this execution.</summary>
public sealed class SandboxSessionUnavailableException : InvalidOperationException
{
    public SandboxSessionUnavailableException(Guid executionId, string reason)
        : base("No sandbox session could be opened for execution " + executionId.ToString("D") + ": " + reason)
    {
        ExecutionId = executionId;
    }

    public Guid ExecutionId { get; }
}

/// <summary>
/// The session this execution had is gone, and replacing it did not work either. Distinct from
/// <see cref="SandboxSessionUnavailableException"/> because the recovery path — journal replay — has
/// already been tried and reported, which is what a reader of a log needs to know.
/// </summary>
public sealed class SandboxSessionLostException : InvalidOperationException
{
    public SandboxSessionLostException(Guid executionId, string reason)
        : base("The sandbox session of execution " + executionId.ToString("D") + " was lost and could not be rebuilt: " + reason)
    {
        ExecutionId = executionId;
    }

    public Guid ExecutionId { get; }
}

/// <summary>
/// The workspace journal could not be written. Thrown rather than swallowed so an execution that has
/// lost its durability finds out at the moment it happens rather than at the moment a runner
/// disappears — the mutation itself DID succeed on the runner.
/// </summary>
public sealed class SandboxJournalUnavailableException : InvalidOperationException
{
    public SandboxJournalUnavailableException(Guid executionId, Exception inner)
        : base("The sandbox workspace of execution " + executionId.ToString("D") + " was changed but the engine could not journal it; a later session loss would lose that change", inner)
    {
        ExecutionId = executionId;
    }

    public Guid ExecutionId { get; }
}

/// <summary>
/// The D6 selector refused the target — for example <c>NO_RUNNER_ONLINE</c> for a
/// <c>RunnerOnly</c> organization whose runner is away. The code is carried so the refusal reaches a
/// user rather than only a log, and nothing falls back to another target locally.
/// </summary>
public sealed class ComputeTargetRefusedException : InvalidOperationException
{
    public ComputeTargetRefusedException(string failureCode, string? message, Guid executionId)
        : base("The compute fabric refused execution " + executionId.ToString("D") + "'s workspace session (" + failureCode + "): " + message)
    {
        FailureCode = failureCode;
    }

    /// <summary>One of <see cref="ComputeFailureCodes"/>, verbatim.</summary>
    public string FailureCode { get; }
}

/// <summary>The engine has no <c>workflow_executions</c> row for this id, so no project and no organization can be resolved.</summary>
public sealed class SandboxExecutionUnknownException : InvalidOperationException
{
    public SandboxExecutionUnknownException(Guid executionId)
        : base("The workflow execution " + executionId.ToString("D") + " does not exist, so its sandbox session has no project or organization.")
    {
        ExecutionId = executionId;
    }

    public Guid ExecutionId { get; }
}
