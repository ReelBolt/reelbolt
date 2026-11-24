using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// D6: the engine's typed client for the Go <c>runner-gateway</c>'s internal API (WP D5b).
/// <para>
/// Four routes, and this client is the only engine code that knows their shape:
/// </para>
/// <list type="table">
/// <item><description><c>POST /internal/v1/jobs</c> — create a job, idempotent on <c>sha256(executionId|stepId|iteration|purpose)</c>. 201 when created, 200 when the same key already existed.</description></item>
/// <item><description><c>GET /internal/v1/jobs/{id}?wait=30s</c> — read a job, optionally long-polling until it is terminal.</description></item>
/// <item><description><c>DELETE /internal/v1/jobs/{id}</c> — cancel.</description></item>
/// <item><description><c>GET /internal/v1/runners?org=&amp;caps=</c> — is a runner online with the capabilities a job needs?</description></item>
/// </list>
/// <para>
/// <b>The 503 is not an error.</b> Every route sits behind a REQUIRED shared token; a gateway with
/// no <c>RUNNER_INTERNAL_TOKEN</c> configured answers <c>503 internal_api_disabled</c> for all of
/// them. That is the answer a deployment which never installed the fabric gives, it is stable and it
/// is expected, so it surfaces as <see cref="ComputeFabricUnavailableException"/> — a value the
/// selector degrades on — and never as a raw <c>HttpRequestException</c> that would turn "the fabric
/// is not deployed here" into a failed step. The same exception covers a gateway that is simply not
/// reachable (DNS, connection refused, timeout), for the same reason.
/// </para>
/// <para>
/// <b>Per-execution session serialisation is enforced here, not by convention.</b>
/// <see cref="OpenJobAsync"/> takes the execution's single <c>runner_sessions</c> slot from
/// <see cref="IExecutionSessionGate"/> and hands it to the returned <see cref="RunnerJobSession"/>,
/// which releases it when the job ends. The gate cannot be bypassed by forgetting to call it,
/// because creating the job and taking the slot are one call. See
/// <see cref="IExecutionSessionGate"/> for why the slot is per EXECUTION.
/// </para>
/// <para>
/// <b>No credential of any kind is ever sent or received.</b> The internal token is the gateway's
/// own service token; job specs carry paths, argv, composition ids and object keys and nothing else
/// (docs/runner-protocol.md §"Security invariants"). The client logs the request line and the job
/// id, never the token and never a presigned URL.
/// </para>
/// </summary>
public sealed class RunnerGatewayClient
{
    /// <summary>The named <see cref="IHttpClientFactory"/> client this uses, so its base address, timeout and pipeline are configured in one place.</summary>
    public const string HttpClientName = "RunnerGateway";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IExecutionSessionGate _gate;
    private readonly ILogger<RunnerGatewayClient> _logger;
    private readonly string? _token;
    private readonly TimeSpan _timeout;
    private readonly int _jobWaitSeconds;

    public RunnerGatewayClient(
        IHttpClientFactory httpClientFactory,
        IOptions<ComputeOptions> options,
        IExecutionSessionGate gate,
        ILogger<RunnerGatewayClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _gate = gate;
        _logger = logger;
        ComputeOptions value = options.Value;
        _token = value.ResolveInternalToken();
        _timeout = TimeSpan.FromSeconds(Math.Max(1, value.RequestTimeoutSeconds));
        _jobWaitSeconds = Math.Clamp(value.JobWaitSeconds, 0, 60);
    }

    /// <summary>
    /// The specification's idempotency key, byte for byte:
    /// <c>sha256("&lt;executionId&gt;|&lt;stepId&gt;|&lt;iteration&gt;|&lt;purpose&gt;")</c>, lowercase hex.
    /// <para>
    /// The gateway computes the same digest when a caller supplies none, so the two must agree
    /// exactly; <c>TestIdempotencyKeyIsTheSpecifiedDigest</c> in
    /// <c>sandbox/cmd/runner-gateway/queue_unit_test.go</c> pins the framing on the Go side and
    /// <c>RunnerGatewayContractTests</c> pins the same golden vector on this one. A null or empty
    /// <paramref name="stepId"/> is the empty component, which is what the gateway receives when the
    /// field is omitted.
    /// </para>
    /// </summary>
    public static string BuildIdempotencyKey(string executionId, string? stepId, int iteration, string purpose)
    {
        string framed = string.Concat(
            executionId ?? string.Empty, "|",
            stepId ?? string.Empty, "|",
            iteration.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            purpose ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(framed)));
    }

    /// <summary>Convenience over <see cref="BuildIdempotencyKey"/> for the engine's own components.</summary>
    public static string BuildIdempotencyKey(Guid executionId, Guid? stepId, int iteration, string purpose)
        => BuildIdempotencyKey(
            executionId.ToString("D"),
            stepId?.ToString("D"),
            iteration,
            purpose);

    /// <summary>
    /// Is a runner online that could take this job? <paramref name="capabilities"/> are the job types
    /// the job needs (<see cref="ComputeJobTypes"/>).
    /// <para>
    /// Per gateway INSTANCE, which the gateway's own comment calls out: a "no" never means "nobody
    /// anywhere", it means "not on this replica". The selector therefore treats it as a hint in the
    /// direction that is safe — it is what decides whether to route an OrgRunner job AT ALL — and
    /// never as a promise that a job will be claimed.
    /// </para>
    /// </summary>
    public async Task<RunnerFleet> ListRunnersAsync(
        Guid? organizationId,
        IReadOnlyCollection<string>? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        StringBuilder query = new();
        if (organizationId is { } org)
        {
            query.Append("?org=").Append(org.ToString("D"));
        }

        if (capabilities is { Count: > 0 })
        {
            query.Append(query.Length == 0 ? '?' : '&')
                .Append("caps=")
                .Append(Uri.EscapeDataString(string.Join(',', capabilities)));
        }

        string what = $"GET /internal/v1/runners{query}";
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await CallAsync(
            () => client.GetAsync($"internal/v1/runners{query}", cancellationToken),
            what,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);

        RunnersResponse? body = await ReadAsync<RunnersResponse>(response, cancellationToken).ConfigureAwait(false);
        return new RunnerFleet(
            body?.Online ?? 0,
            body?.Runners?.Select(ToPresence).ToArray() ?? [],
            body?.Pool?.WakerHealthy ?? false);
    }

    /// <summary>
    /// Create a job and take the execution's session slot.
    /// <para>
    /// A job already created under the same idempotency key returns the SAME job
    /// (<see cref="RunnerJobView.Created"/> false) and creates no second row, so an engine retry
    /// after a timeout cannot double-enqueue. The session slot is still taken: the existing job may
    /// well be the one running right now.
    /// </para>
    /// </summary>
    /// <exception cref="ComputeSessionBusyException">Another session of this execution still holds the slot after the configured wait.</exception>
    /// <exception cref="ComputeFabricUnavailableException">The fabric is not deployed, or the gateway is unreachable.</exception>
    /// <exception cref="RunnerGatewayRequestException">The gateway refused the request.</exception>
    public async Task<RunnerJobSession> OpenJobAsync(RunnerJobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ExecutionSessionLease lease = await _gate.AcquireAsync(request.ExecutionId, null, cancellationToken).ConfigureAwait(false);
        if (!lease.Acquired)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new ComputeSessionBusyException(request.ExecutionId, request.Purpose);
        }

        try
        {
            RunnerJobView view = await CreateJobAsync(request, cancellationToken).ConfigureAwait(false);
            return new RunnerJobSession(this, lease, view);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Create the job. <see cref="OpenJobAsync"/> is the caller-facing entry point; this is the raw call.</summary>
    private async Task<RunnerJobView> CreateJobAsync(RunnerJobRequest request, CancellationToken cancellationToken)
    {
        CreateJobPayload payload = new(
            IdempotencyKey: request.IdempotencyKey ?? BuildIdempotencyKey(request.ExecutionId, request.StepId, request.Iteration, request.Purpose),
            OrgId: request.OrganizationId.ToString("D"),
            ExecutionId: request.ExecutionId.ToString("D"),
            StepId: request.StepId?.ToString("D"),
            JobType: ComputeJobTypes.ToWire(request.JobType),
            Target: ComputeTargets.ToWire(request.Target),
            Priority: request.Priority,
            MaxAttempts: request.MaxAttempts,
            Iteration: request.Iteration,
            Purpose: request.Purpose,
            RequiredCaps: request.RequiredCapabilities,
            Spec: request.Spec);

        const string What = "POST /internal/v1/jobs";
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await CallAsync(
            () => client.PostAsJsonAsync("internal/v1/jobs", payload, Json, cancellationToken),
            What,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, What, cancellationToken).ConfigureAwait(false);

        JobPayload? body = await ReadAsync<JobPayload>(response, cancellationToken).ConfigureAwait(false)
            ?? throw new RunnerGatewayRequestException((int)response.StatusCode, "POST /internal/v1/jobs", "the gateway returned an empty job body");
        RunnerJobView view = ToView(body);
        _logger.LogInformation(
            "compute: {Action} runner job {JobId} for execution {ExecutionId} (target {Target}, type {JobType}, attempt {Attempt})",
            view.Created ? "created" : "reused",
            view.JobId, request.ExecutionId, request.Target, request.JobType, view.Attempt);
        return view;
    }

    /// <summary>Read one job. Null when the gateway does not know it (404).</summary>
    public async Task<RunnerJobView?> GetJobAsync(Guid jobId, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        string path = $"internal/v1/jobs/{jobId:D}{WaitQuery(wait)}";
        using HttpClient client = CreateClient(WaitTimeout(wait));
        using HttpResponseMessage response = await CallAsync(
            () => client.GetAsync(path, cancellationToken),
            path,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
        JobPayload? body = await ReadAsync<JobPayload>(response, cancellationToken).ConfigureAwait(false);
        return body is null ? null : ToView(body);
    }

    /// <summary>
    /// Long-poll a job until it is terminal, or until <paramref name="wait"/> (default
    /// <c>Compute:JobWaitSeconds</c>, 30 s) elapses. A timed-out read is not a failure — the caller
    /// re-polls — which is what <see cref="RunnerJobView.TimedOut"/> reports.
    /// </summary>
    public Task<RunnerJobView?> WaitForJobAsync(Guid jobId, TimeSpan? wait = null, CancellationToken cancellationToken = default)
        => GetJobAsync(jobId, wait ?? TimeSpan.FromSeconds(_jobWaitSeconds), cancellationToken);

    /// <summary>Cancel a job. A queued job is <c>Cancelled</c> at once; a leased one becomes <c>Cancelled</c> when the runner acknowledges or the grace window closes. Null when the job is unknown.</summary>
    public async Task<RunnerJobView?> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        string path = $"internal/v1/jobs/{jobId:D}";
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await CallAsync(
            () => client.DeleteAsync(path, cancellationToken),
            path,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
        JobPayload? body = await ReadAsync<JobPayload>(response, cancellationToken).ConfigureAwait(false);
        return body is null ? null : ToView(body);
    }

    /// <summary>
    /// Run ONE op on a media session and wait for its answer (WP D5c's
    /// <c>POST /internal/v1/sessions/{id}/ops</c>). This is the transport D11's
    /// <see cref="RemoteMediaWorkspace"/> is built on, and the only place in the engine that knows
    /// the route's shape.
    /// <para>
    /// <b>A failed op is not an exception here.</b> The route answers <c>200</c> with
    /// <c>{ok:false, error}</c> when the WORKER refused the op (a bad path, a rejected argv, a size
    /// cap), and that is a value the caller maps onto its own domain failure — an ffmpeg exit code, a
    /// missing file. Only the four SESSION-level problems (<c>404 session_not_found</c>,
    /// <c>410 session_lost</c>, <c>409 op_in_flight</c>/<c>op_cancelled</c>,
    /// <c>504 op_timeout</c>, <c>502 session_owner_unreachable</c>) and a transport failure are
    /// reported as <see cref="RunnerSessionOpException"/>, because those are the ones a caller must
    /// act on differently (<see cref="RunnerSessionOpException.SessionLost"/> and
    /// <see cref="RunnerSessionOpException.Retryable"/>).
    /// </para>
    /// <para>
    /// <b>Idempotency.</b> Passing the same <paramref name="opId"/> for a retry is what the protocol
    /// asks for: "the worker MUST keep the results of the last 256 ops per session. A repeated
    /// <c>opId</c> MUST return the cached <c>op.response</c> without executing the op again". The
    /// gateway mints an id when none is supplied, which is the right default for a first attempt.
    /// </para>
    /// </summary>
    /// <param name="sessionId">The gateway's session id (<c>runner_sessions.id</c>), from <see cref="RunnerJobView.SessionId"/>.</param>
    /// <param name="op">One of <see cref="MediaSessionOps"/>. The gateway passes it through verbatim.</param>
    /// <param name="args">The op's payload. The gateway never reads it — only the worker may interpret an op's arguments.</param>
    /// <param name="timeout">Bounds the op. Null means the job's own timeout, which is what an <c>exec</c>/<c>ffmpeg</c> needs.</param>
    /// <param name="opId">A stable id, to make a retry safe. Null lets the gateway mint one.</param>
    /// <exception cref="ComputeFabricUnavailableException">The fabric is not deployed, or the gateway is unreachable.</exception>
    /// <exception cref="RunnerSessionOpException">The session could not serve the op.</exception>
    public async Task<RunnerSessionOpResult> RunSessionOpAsync(
        Guid sessionId,
        string op,
        JsonNode? args = null,
        TimeSpan? timeout = null,
        string? opId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(op);

        int timeoutSeconds = timeout is { } span && span > TimeSpan.Zero
            ? (int)Math.Clamp(Math.Ceiling(span.TotalSeconds), 1, int.MaxValue - _timeout.TotalSeconds)
            : 0;

        SessionOpPayload payload = new(
            OpId: string.IsNullOrWhiteSpace(opId) ? null : opId,
            Op: op,
            Args: args ?? new JsonObject(),
            TimeoutSec: timeoutSeconds);

        string path = $"internal/v1/sessions/{sessionId:D}/ops";

        // The HTTP timeout must outlast the op's own deadline: the gateway answers 504 at that
        // deadline, and a client that gave up first would report a transport failure for an op the
        // gateway was still willing to serve.
        using HttpClient client = CreateClient(
            timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) + _timeout : null);
        HttpResponseMessage response;
        try
        {
            response = await CallAsync(
                () => client.PostAsJsonAsync(path, payload, Json, cancellationToken),
                path,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ComputeFabricUnavailableException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Our request ran out of time. For a session op that is NOT "the fabric is not deployed": the
            // gateway has the op, and the worker may be running it. Reported as a retryable session-level
            // outcome so the caller asks again under the same opId instead of abandoning the op and then
            // meeting 409 op_in_flight on the next one.
            throw new RunnerSessionOpException(
                SessionOpErrorCodes.GatewayTimeout,
                $"the engine's request for {path} timed out after {client.Timeout.TotalSeconds:0}s; the op may still be running",
                sessionLost: false,
                retryable: true);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                throw new ComputeFabricUnavailableException(
                    (int)response.StatusCode, path, "the runner gateway has its internal API closed (RUNNER_INTERNAL_TOKEN is not configured)");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                SessionOpFailure? failure = await ReadAsync<SessionOpFailure>(response, cancellationToken).ConfigureAwait(false);
                throw SessionOpExceptionFor((int)response.StatusCode, failure, path);
            }

            SessionOpPayloadResponse? body = await ReadAsync<SessionOpPayloadResponse>(response, cancellationToken).ConfigureAwait(false)
                ?? throw new RunnerGatewayRequestException((int)response.StatusCode, path, "the gateway returned an empty op body");

            if (!body.Ok)
            {
                _logger.LogDebug(
                    "compute: session op {Op} on session {SessionId} was refused by the worker ({Code})",
                    op, sessionId, body.Error?.Code ?? "unknown");
            }

            return new RunnerSessionOpResult(
                SessionId: body.SessionId,
                JobId: body.JobId,
                OpId: body.OpId ?? string.Empty,
                Ok: body.Ok,
                Result: body.Result,
                Error: body.Error is null ? null : new RunnerSessionOpError(body.Error.Code, body.Error.Message, body.Error.Retryable),
                DurationMs: body.DurationMs);
        }
    }

    /// <summary>
    /// Maps a session-op refusal onto <see cref="RunnerSessionOpException"/>, preserving the two
    /// distinctions the protocol draws: whether the SESSION is gone, and whether repeating the op is
    /// safe. An unrecognised status or code is the conservative reading — not lost, not retryable —
    /// so a caller cannot loop on a refusal this build does not understand.
    /// </summary>
    private static RunnerSessionOpException SessionOpExceptionFor(int status, SessionOpFailure? failure, string path)
    {
        string code = string.IsNullOrWhiteSpace(failure?.Error) ? $"http_{status}" : failure!.Error!;
        string detail = failure?.Message ?? failure?.Reason ?? "no detail";
        string reason = failure?.Reason ?? string.Empty;

        bool sessionLost = code is SessionOpErrorCodes.SessionNotFound or SessionOpErrorCodes.SessionLost;
        bool retryable = code is SessionOpErrorCodes.OpTimeout
            or SessionOpErrorCodes.SessionOwnerUnreachable
            or SessionOpErrorCodes.GatewayTimeout
            or SessionOpErrorCodes.OpInFlight;

        return new RunnerSessionOpException(
            code,
            $"the gateway refused {path} ({status} {code}): {detail}",
            sessionLost,
            retryable,
            status,
            string.IsNullOrWhiteSpace(reason) ? null : reason);
    }

    private string WaitQuery(TimeSpan? wait)
        => wait is { } value && value > TimeSpan.Zero
            ? $"?wait={(int)Math.Clamp(value.TotalSeconds, 1, 60)}s"
            : string.Empty;

    /// <summary>
    /// The request timeout a long-poll needs: the wait window plus the ordinary request timeout, so
    /// the client never gives up before the gateway's own answer would arrive.
    /// </summary>
    private TimeSpan WaitTimeout(TimeSpan? wait)
        => wait is { } value && value > TimeSpan.Zero
            ? _timeout + TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 1, 60))
            : _timeout;

    private HttpClient CreateClient(TimeSpan? timeout = null)
    {
        // No configured token means the engine cannot authenticate at all. Sending the request anyway
        // would be an unauthenticated call to an endpoint that creates compute on other people's
        // machines, so it is refused here — and classified the same way a closed gateway is, because
        // the caller's recovery (keep the step local) is identical.
        if (_token is null)
        {
            throw new ComputeFabricUnavailableException(
                "the compute fabric is switched on (Compute:Mode=Fabric) but neither Compute:InternalToken nor RUNNER_INTERNAL_TOKEN is configured, so the runner gateway cannot be called");
        }

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = timeout ?? _timeout;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    /// <summary>
    /// Runs one gateway call, mapping "there is no usable gateway here" onto
    /// <see cref="ComputeFabricUnavailableException"/>: DNS, connection refused, TLS and a timeout
    /// are all the same thing to the caller as D5b's 503 — a deployment fact it degrades on. An
    /// <see cref="OperationCanceledException"/> the CALLER asked for is re-thrown untouched.
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
            throw new ComputeFabricUnavailableException($"the runner gateway timed out ({what})", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ComputeFabricUnavailableException($"the runner gateway is unreachable ({what}): {ex.Message}", ex);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        int status = (int)response.StatusCode;

        // 503 = the gateway has no RUNNER_INTERNAL_TOKEN configured, so the whole internal surface is
        // closed. That is "the fabric is not deployed", a state the caller degrades on, and never an
        // error worth failing a step for. A transport failure (no gateway at all) reads the same way,
        // which is why both surface as ComputeFabricUnavailableException.
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new ComputeFabricUnavailableException(
                status, what, "the runner gateway has its internal API closed (RUNNER_INTERNAL_TOKEN is not configured)");
        }

        string detail = await SafeBodyAsync(response, cancellationToken).ConfigureAwait(false);
        throw new RunnerGatewayRequestException(status, what, detail);
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length <= 512 ? body : body[..512];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // An unparseable body is a gateway bug, not a caller mistake. Reporting it as a protocol
            // error keeps the caller's own recovery logic (retry, fall back) operable.
            throw new RunnerGatewayRequestException(
                (int)response.StatusCode, response.RequestMessage?.RequestUri?.AbsolutePath ?? "internal/v1", "the gateway returned a body this client could not read");
        }
    }

    private RunnerJobView ToView(JobPayload payload)
    {
        RunnerJobType? jobType = ComputeJobTypes.TryParseWire(payload.JobType);
        RunnerJobTarget? target = ComputeTargets.TryParseWire(payload.Target, out RunnerJobTarget parsed) ? parsed : null;
        if (jobType is null)
        {
            _logger.LogWarning("compute: the gateway reported job type {JobType}, which this engine build does not know", payload.JobType);
        }

        return new RunnerJobView(
            JobId: payload.JobId,
            SessionId: payload.SessionId,
            Status: ParseStatus(payload.Status),
            JobType: jobType,
            Target: target,
            Attempt: payload.Attempt,
            MaxAttempts: payload.MaxAttempts,
            LeaseEpoch: payload.LeaseEpoch,
            Created: payload.Created,
            ResultJson: payload.Result,
            ErrorClass: payload.ErrorClass,
            SessionLost: payload.SessionLost,
            TimedOut: payload.TimedOut);
    }

    /// <summary>
    /// Maps the gateway's status word. An unrecognised value is an unknown case and NOT a parse
    /// error ("Enum-like string values are open sets"), so it becomes null; a null status is never
    /// terminal, which keeps a poller polling rather than concluding a job it cannot read has ended.
    /// </summary>
    private static RunnerJobStatus? ParseStatus(string? wire) => wire switch
    {
        "Queued" => RunnerJobStatus.Queued,
        "Assigned" => RunnerJobStatus.Assigned,
        "Running" => RunnerJobStatus.Running,
        "Succeeded" => RunnerJobStatus.Succeeded,
        "Failed" => RunnerJobStatus.Failed,
        "Cancelled" => RunnerJobStatus.Cancelled,
        "Expired" => RunnerJobStatus.Expired,
        _ => null
    };

    private static RunnerPresence ToPresence(RunnerView runner) => new(
        RunnerId: Guid.TryParse(runner.RunnerId, out Guid id) ? id : Guid.Empty,
        Kind: string.Equals(runner.Kind, "CloudPool", StringComparison.OrdinalIgnoreCase) ? RunnerKind.CloudPool : RunnerKind.Desktop,
        OrganizationId: Guid.TryParse(runner.OrgId, out Guid org) ? org : null,
        Name: runner.Name ?? string.Empty,
        FreeSlots: runner.FreeSlots,
        Running: runner.Running,
        Draining: runner.Draining,
        ConnectedAt: runner.ConnectedAt,
        JobTypes: runner.JobTypes ?? [],
        Container: runner.Container ?? "none",
        AgentVersion: runner.AgentVersion ?? string.Empty);

    // --- wire DTOs. Names are the gateway's, and RunnerGatewayContractTests fails if they drift. ---

    private sealed record CreateJobPayload(
        string IdempotencyKey,
        string OrgId,
        string ExecutionId,
        string? StepId,
        string JobType,
        string Target,
        int Priority,
        int MaxAttempts,
        int Iteration,
        string? Purpose,
        RequiredCapsPayload? RequiredCaps,
        JsonNode? Spec);

    private sealed record JobPayload(
        Guid JobId,
        Guid? SessionId,
        string? Status,
        string? JobType,
        string? Target,
        int Attempt,
        int MaxAttempts,
        long LeaseEpoch,
        bool Created,
        string? Result,
        string? ErrorClass,
        bool SessionLost,
        bool TimedOut);

    private sealed record RunnersResponse(int Online, RunnerView[]? Runners, PoolView? Pool = null);

    /// <summary>The gateway's <c>pool</c> block; absent on a gateway that predates the waker (read as not healthy).</summary>
    private sealed record PoolView(bool WakerHealthy);

    private sealed record RunnerView(
        string RunnerId,
        string? Kind,
        string? OrgId,
        string? Name,
        int FreeSlots,
        int Running,
        bool Draining,
        DateTimeOffset ConnectedAt,
        string[]? JobTypes,
        string? Container,
        string? AgentVersion);

    /// <summary>One POST /internal/v1/sessions/{id}/ops. The gateway mints an <c>opId</c> when this is null.</summary>
    private sealed record SessionOpPayload(string? OpId, string Op, JsonNode Args, int TimeoutSec);

    /// <summary>The route's answer: the runner's <c>op.response</c>, passed through.</summary>
    private sealed record SessionOpPayloadResponse(
        Guid SessionId,
        Guid JobId,
        string? OpId,
        bool Ok,
        JsonNode? Result,
        OpErrorPayload? Error,
        long DurationMs);

    /// <summary>The worker's own reporting of a refused op.</summary>
    private sealed record OpErrorPayload(string? Code, string? Message, bool Retryable);

    /// <summary>The gateway's refusals, which differ in <c>error</c>/<c>reason</c>/<c>message</c> per case — all optional.</summary>
    private sealed record SessionOpFailure(string? Error, string? Reason, string? Message);
}

/// <summary>
/// The session-op error codes of <c>POST /internal/v1/sessions/{id}/ops</c>
/// (<c>docs/runner-protocol.md</c> §"Session ops"). Only the ones the ENGINE branches on live here;
/// the worker's own op-level codes (<c>invalid_path</c>, <c>argv_not_allowed</c>) are read as opaque
/// strings, because they are an open set.
/// </summary>
public static class SessionOpErrorCodes
{
    /// <summary>404: no such session was ever opened. Not retryable, and not a lost session to re-open.</summary>
    public const string SessionNotFound = "session_not_found";

    /// <summary>410: the session existed and cannot serve an op. Open a new one or fail the step.</summary>
    public const string SessionLost = "session_lost";

    /// <summary>409: the session already has an op in flight.</summary>
    public const string OpInFlight = "op_in_flight";

    /// <summary>409: the job was cancelled, or the execution ended, while this op ran.</summary>
    public const string OpCancelled = "op_cancelled";

    /// <summary>504: the runner did not answer in time. A retry with the same <c>opId</c> is safe.</summary>
    public const string OpTimeout = "op_timeout";

    /// <summary>502: the replica the session is pinned to could not be reached.</summary>
    public const string SessionOwnerUnreachable = "session_owner_unreachable";

    /// <summary>
    /// Client-side, never sent by the gateway: the engine's own HTTP request for the op ran out of time
    /// with no answer. The op may still be running on the worker, so it is retryable under the SAME
    /// <c>opId</c> and the next, different op would be <c>409 op_in_flight</c>.
    /// </summary>
    public const string GatewayTimeout = "gateway_timeout";
}

/// <summary>
/// The pins a job declares (docs/runner-protocol.md §"Capabilities"): the gateway MUST NOT assign a
/// job whose requirements the runner does not meet. An unset member is no requirement at all, which
/// is why every member is optional and the whole document is omitted when empty.
/// </summary>
public sealed record RequiredCapsPayload(
    [property: JsonPropertyName("container")] string? Container = null,
    [property: JsonPropertyName("runtimeImageDigest")] string? RuntimeImageDigest = null,
    [property: JsonPropertyName("fontsetVersion")] string? FontsetVersion = null,
    [property: JsonPropertyName("ffmpegMajor")] int? FfmpegMajor = null,
    [property: JsonPropertyName("minOutputBytes")] long? MinOutputBytes = null);

/// <summary>
/// One POST /internal/v1/jobs. <see cref="Spec"/> is the job-type-specific body, owned by the
/// executor that builds it (D8's render, D11's media sessions) and opaque to this client, which
/// sends it through unchanged.
/// </summary>
public sealed record RunnerJobRequest(
    Guid ExecutionId,
    Guid OrganizationId,
    RunnerJobType JobType,
    RunnerJobTarget Target,
    Guid? StepId = null,
    int Iteration = 0,
    string Purpose = "",
    string? IdempotencyKey = null,
    int Priority = 0,
    int MaxAttempts = 2,
    RequiredCapsPayload? RequiredCapabilities = null,
    JsonNode? Spec = null);

/// <summary>
/// The engine's view of one gateway job. It carries no token, no presigned URL and no credential:
/// the engine's own copy of the spec is the authority for those.
/// </summary>
/// <param name="Status">Null when the gateway reported a status word this build does not know — an open set, handled as an unknown case.</param>
/// <param name="SessionLost">True on an <c>Expired</c> job: the runner's session was lost, and the engine decides whether to open a new one or fail the step.</param>
/// <param name="TimedOut">True when a long-poll returned without reaching a terminal state. Not a failure: the caller re-polls.</param>
public sealed record RunnerJobView(
    Guid JobId,
    Guid? SessionId,
    RunnerJobStatus? Status,
    RunnerJobType? JobType,
    RunnerJobTarget? Target,
    int Attempt,
    int MaxAttempts,
    long LeaseEpoch,
    bool Created,
    string? ResultJson,
    string? ErrorClass,
    bool SessionLost,
    bool TimedOut)
{
    /// <summary>Exactly one terminal state per job (docs/runner-protocol.md §"Job lifecycle, leases and fencing").</summary>
    public bool IsTerminal => Status is RunnerJobStatus.Succeeded or RunnerJobStatus.Failed or RunnerJobStatus.Cancelled or RunnerJobStatus.Expired;
}

/// <summary>One live runner connection, as much as the engine may know.</summary>
public sealed record RunnerPresence(
    Guid RunnerId,
    RunnerKind Kind,
    Guid? OrganizationId,
    string Name,
    int FreeSlots,
    int Running,
    bool Draining,
    DateTimeOffset ConnectedAt,
    IReadOnlyList<string> JobTypes,
    string Container,
    string AgentVersion);

/// <summary>
/// The answer to "is a runner online?". <see cref="Online"/> is what the selector reads; the list is
/// kept because a log line that names the runner is worth more than one that names a count.
/// <see cref="WakerHealthy"/> is the gateway's <c>pool.wakerHealthy</c>; a gateway that does not send
/// the block reads as <c>false</c>, so an old gateway can never turn on the wake-and-wait path.
/// </summary>
public sealed record RunnerFleet(int Online, IReadOnlyList<RunnerPresence> Runners, bool WakerHealthy = false)
{
    /// <summary>Whether any listed runner advertises every job type in <paramref name="jobTypes"/> and, when a kind is given, is of that kind.</summary>
    public bool HasEligible(RunnerKind? kind, IReadOnlyList<string> jobTypes)
        => Runners.Any(r =>
            (kind is null || r.Kind == kind)
            && !r.Draining
            && jobTypes.All(t => r.JobTypes.Contains(t, StringComparer.Ordinal)));
}

/// <summary>
/// A created job plus the execution's session slot, held until the job ends. Disposing it is what
/// lets the next runner session of the same execution open — see <see cref="IExecutionSessionGate"/>.
/// <para>
/// The job is opened and the slot released by the same object on purpose: an engine that forgot to
/// release would wedge an execution's sessions forever, and one that released early would let a
/// second session open while the first still runs, which is the schema violation this whole design
/// exists to prevent.
/// </para>
/// </summary>
public sealed class RunnerJobSession : IAsyncDisposable
{
    private readonly RunnerGatewayClient _client;
    private readonly ExecutionSessionLease _lease;
    private int _disposed;

    internal RunnerJobSession(RunnerGatewayClient client, ExecutionSessionLease lease, RunnerJobView job)
    {
        _client = client;
        _lease = lease;
        Job = job;
    }

    /// <summary>The job as the gateway last reported it.</summary>
    public RunnerJobView Job { get; private set; }

    /// <summary>The job's id.</summary>
    public Guid JobId => Job.JobId;

    /// <summary>The gateway's session id (<c>runner_sessions.id</c>), null until a runner is assigned.</summary>
    public Guid? SessionId => Job.SessionId;

    /// <summary>The execution whose session slot this holds.</summary>
    public Guid ExecutionId => _lease.ExecutionId;

    /// <summary>Re-read the job, long-polling for up to <paramref name="wait"/>. Null when the gateway no longer knows the job.</summary>
    public async Task<RunnerJobView?> RefreshAsync(TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        RunnerJobView? view = await _client.WaitForJobAsync(JobId, wait, cancellationToken).ConfigureAwait(false);
        if (view is not null)
        {
            Job = view;
        }

        return view;
    }

    /// <summary>Cancel the job and re-read it.</summary>
    public async Task<RunnerJobView?> CancelAsync(CancellationToken cancellationToken = default)
    {
        RunnerJobView? view = await _client.CancelJobAsync(JobId, cancellationToken).ConfigureAwait(false);
        if (view is not null)
        {
            Job = view;
        }

        return view;
    }

    /// <summary>Release the execution's session slot. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lease.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The compute fabric is not there: the gateway is unreachable, or it answered
/// <c>503 internal_api_disabled</c> because no <c>RUNNER_INTERNAL_TOKEN</c> is configured on its
/// side. A DEPLOYMENT FACT, not a failure — the caller degrades on it (the selector keeps the step
/// local) and never fails a step because of it.
/// </summary>
public sealed class ComputeFabricUnavailableException : InvalidOperationException
{
    public ComputeFabricUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>With the status the gateway answered and the request that produced it.</summary>
    public ComputeFabricUnavailableException(int statusCode, string request, string detail)
        : base($"the runner gateway is unavailable ({statusCode} {request}): {detail}")
    {
        StatusCode = statusCode;
        Request = request;
    }

    /// <summary>The HTTP status, when there was one (null for a transport failure).</summary>
    public int? StatusCode { get; }

    /// <summary>The request line that failed.</summary>
    public string? Request { get; }
}

/// <summary>
/// The gateway refused a request it understood — a 400 on a malformed job, a 401 on a wrong token —
/// or answered with a body this client cannot read. A real error, and reported as one.
/// </summary>
public sealed class RunnerGatewayRequestException : InvalidOperationException
{
    public RunnerGatewayRequestException(int statusCode, string request, string detail)
        : base($"the runner gateway refused {request} ({statusCode}): {detail}")
    {
        StatusCode = statusCode;
        Request = request;
    }

    /// <summary>The HTTP status the gateway answered with.</summary>
    public int StatusCode { get; }

    /// <summary>The request line that failed.</summary>
    public string Request { get; }
}

/// <summary>
/// Another unit of this execution still holds its single runner-session slot. Carries the code the
/// step fails with, so the reason reaches the user rather than only the log.
/// </summary>
public sealed class ComputeSessionBusyException : InvalidOperationException
{
    public ComputeSessionBusyException(Guid executionId, string purpose)
        : base($"execution {executionId} already has an open runner session; the session for '{purpose}' could not start before the wait expired")
    {
        ExecutionId = executionId;
    }

    /// <summary>The execution whose slot is taken.</summary>
    public Guid ExecutionId { get; }

    /// <summary>The failure code the step reports.</summary>
    public string FailureCode => ComputeFailureCodes.SessionBusy;
}
