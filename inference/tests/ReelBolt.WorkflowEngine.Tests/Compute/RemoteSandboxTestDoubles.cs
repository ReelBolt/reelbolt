using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// An in-memory object store standing in for the bucket the workspace journal lives in. It is
/// deliberately dumb: bytes in, bytes out, keys listed by prefix. Everything the journal's correctness
/// depends on — the fold from history to final state, the key layout — is therefore exercised for
/// real, while nothing leaves the process.
/// </summary>
internal sealed class FakeSandboxJournalStore : ISandboxJournalStore
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>Every key this store was asked for a presigned GET of, in order. The make-believe URL encodes the key.</summary>
    public List<string> PresignedKeys { get; } = [];

    public int Puts { get; private set; }

    /// <summary>When true, the NEXT put throws — the object store going away mid-write.</summary>
    public bool FailNextPut { get; set; }

    public IReadOnlyCollection<string> Keys => _objects.Keys;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        Puts++;
        if (FailNextPut)
        {
            FailNextPut = false;
            throw new InvalidOperationException("the object store is unavailable");
        }

        _objects[key] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]?> TryGetAsync(string key, CancellationToken cancellationToken)
        => Task.FromResult(_objects.TryGetValue(key, out byte[]? value) ? value : null);

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(
            _objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList());

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _objects.Remove(key);
        return Task.CompletedTask;
    }

    public Task<string> CreateGetUrlAsync(Guid projectId, string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (!key.StartsWith($"projects/{projectId:D}/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A journal GET may only sign a key under the project prefix.", nameof(key));
        }

        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "The D2 project GET cap is 15 minutes.");
        }

        PresignedKeys.Add(key);
        return Task.FromResult("fake://objects/" + key);
    }

    /// <summary>Fetch by the URL <see cref="CreateGetUrlAsync"/> minted — a stand-in for the runner's own presigned GET.</summary>
    public byte[]? FetchByUrl(string url)
    {
        const string Prefix = "fake://objects/";
        return url.StartsWith(Prefix, StringComparison.Ordinal) && _objects.TryGetValue(url[Prefix.Length..], out byte[]? value)
            ? value
            : null;
    }
}

/// <summary>The project and organization of the executions the D7b tests run.</summary>
internal sealed class FakeSandboxScopeResolver : ISandboxExecutionScopeResolver
{
    public FakeSandboxScopeResolver(Guid projectId, Guid organizationId)
    {
        ProjectId = projectId;
        OrganizationId = organizationId;
    }

    public Guid ProjectId { get; }

    public Guid OrganizationId { get; }

    public int Calls { get; private set; }

    public ValueTask<SandboxExecutionScope> ResolveAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return ValueTask.FromResult(new SandboxExecutionScope(executionId, ProjectId, OrganizationId));
    }
}

/// <summary>A selector that answers from a constant, so a test states only the decision it is about.</summary>
internal sealed class FixedComputeTarget(ComputeDecision decision) : IComputeTarget
{
    public ComputeDecision Decision { get; set; } = decision;

    public int Calls { get; private set; }

    public ValueTask<ComputeDecision> ResolveAsync(ComputeTargetRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        return ValueTask.FromResult(Decision);
    }

    public ComputeTargetRequest? LastRequest { get; private set; }
}

/// <summary>One op the fake runner was asked to run, with the arguments it arrived with.</summary>
internal sealed record FakeOp(string OpId, string Op, JsonNode? Args);

/// <summary>
/// A runner, and the gateway in front of it, as the engine sees them: the D5c session-op route plus
/// the D5b job route. It models the ONE fact this work package exists for — a workspace lives on a
/// machine, and when that machine goes away the workspace goes with it.
/// </summary>
internal sealed class FakeRunnerGateway
{
    private readonly FakeSandboxJournalStore _store;
    private readonly object _lock = new();

    public FakeRunnerGateway(FakeSandboxJournalStore store)
    {
        _store = store;
        SessionId = Guid.Parse("5e000000-0000-4000-8000-0000000000a1");
    }

    /// <summary>The workspace as it currently exists on the runner. Cleared by <see cref="KillRunner"/>.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Every op the runner executed, in order — including the ones a hydrate replay drives.</summary>
    public List<FakeOp> Ops { get; } = [];

    /// <summary>The session ids the engine opened, in order. Two entries means a session was replaced.</summary>
    public List<Guid> OpenedSessions { get; } = [];

    /// <summary>The job purposes the engine created, in order — the idempotency-key half of opening a replacement session.</summary>
    public List<string> JobPurposes { get; } = [];

    /// <summary>The job targets the engine asked for, in order.</summary>
    public List<string> JobTargets { get; } = [];

    public Guid SessionId { get; private set; }

    /// <summary>True once the machine is gone: every op answers 410 session_lost until a new session is opened.</summary>
    public bool RunnerGone { get; private set; }

    /// <summary>Op names that should answer <c>ok:false</c> with <paramref name="code"/>.</summary>
    public Dictionary<string, string> FailingOps { get; } = new(StringComparer.Ordinal);

    /// <summary>Job ids the gateway minted, in order.</summary>
    public List<Guid> JobIds { get; } = [];

    /// <summary>Answers the request a test wants instead of the default job body. Null means "use the default".</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>
    /// Awaited before an op is executed, with the op's name - so a test can hold an op IN FLIGHT and
    /// observe what the engine does (or does not do) meanwhile.
    /// </summary>
    public Func<string, Task>? BeforeOp { get; set; }

    /// <summary>Every job the gateway holds, by id: its type, its session and its status as the job routes report it.</summary>
    public Dictionary<Guid, FakeJob> Jobs { get; } = [];

    /// <summary>
    /// What happened to jobs, in order: <c>create:{jobType}</c> and <c>end:{jobType}:{status}</c>. The
    /// evidence for "the parked session was ended BEFORE the media job was created".
    /// </summary>
    public List<string> Events { get; } = [];

    /// <summary>
    /// The largest number of non-terminal jobs that existed at once, measured each time a job is
    /// created. <c>runner_sessions</c> allows ONE open session per execution, and every job here is
    /// one execution's, so anything above 1 is the schema violation the session gate exists to prevent.
    /// </summary>
    public int MaxOpenJobs { get; private set; }

    /// <summary>
    /// The runner answers <c>close</c> but the job is NOT finished - the gateway's
    /// <c>finishClosedSession</c> failing, or losing the race with the engine's next read. The engine
    /// must not take that as "the session is closed".
    /// </summary>
    public bool CloseLeavesJobRunning { get; set; }

    /// <summary>
    /// How many job reads after a <c>DELETE</c> still report <c>Running</c>: the cancel grace window,
    /// during which a runner that never acknowledges still holds the job. 0 cancels at once.
    /// </summary>
    public int CancelGracePolls { get; set; }

    /// <summary>A job that never reaches a terminal state, whatever the engine does: a gateway that lost the cancel deadline.</summary>
    public bool JobsNeverEnd { get; set; }

    /// <summary>
    /// The laptop closes. The workspace — every file the agent wrote into it — dies with the container,
    /// and the session can no longer serve an op.
    /// </summary>
    public void KillRunner()
    {
        lock (_lock)
        {
            RunnerGone = true;
            Files.Clear();
        }
    }

    /// <summary>The files the runner's workspace holds right now. The acceptance test's own evidence.</summary>
    public IReadOnlyDictionary<string, string> FilesAsText()
        => Files.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.Ordinal);

    public HttpMessageHandler BuildHandler() => new StubHttpMessageHandler(HandleAsync);

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
    {
        if (Intercept?.Invoke(request) is { } intercepted)
        {
            return intercepted;
        }

        string path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path.EndsWith("/jobs", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            return CreateJob(request);
        }

        if (path.Contains("/jobs/", StringComparison.Ordinal) && Guid.TryParse(path.Split('/')[^1], out Guid jobId))
        {
            return request.Method == HttpMethod.Delete ? CancelJob(jobId) : ReadJob(jobId);
        }

        if (path.Contains("/sessions/", StringComparison.Ordinal) && path.EndsWith("/ops", StringComparison.Ordinal))
        {
            string session = path.Split('/')[^2];
            string body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync();
            if (BeforeOp is { } beforeOp)
            {
                using JsonDocument peek = JsonDocument.Parse(body);
                await beforeOp(peek.RootElement.GetProperty("op").GetString() ?? string.Empty);
            }

            return RunOp(Guid.Parse(session), body);
        }

        return ComputeTestFactory.Json("{}", HttpStatusCode.NotFound);
    }

    private HttpResponseMessage CreateJob(HttpRequestMessage request)
    {
        string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        string purpose = root.TryGetProperty("purpose", out JsonElement purposeElement) ? purposeElement.GetString() ?? string.Empty : string.Empty;
        string target = root.TryGetProperty("target", out JsonElement targetElement) ? targetElement.GetString() ?? string.Empty : string.Empty;
        string jobType = root.TryGetProperty("jobType", out JsonElement typeElement)
            ? typeElement.GetString() ?? "remotion.workspace-session"
            : "remotion.workspace-session";

        lock (_lock)
        {
            JobPurposes.Add(purpose);
            JobTargets.Add(target);

            Guid jobId = Guid.NewGuid();
            JobIds.Add(jobId);

            // A new job means a new session, and a new session means the machine question is open
            // again: the engine has to hydrate what it wants to see.
            SessionId = Guid.NewGuid();
            RunnerGone = false;
            OpenedSessions.Add(SessionId);

            FakeJob job = new(jobId, jobType, SessionId, string.IsNullOrEmpty(target) ? "CloudPool" : target);
            Jobs[jobId] = job;
            Events.Add("create:" + jobType);
            MaxOpenJobs = Math.Max(MaxOpenJobs, Jobs.Values.Count(j => !j.IsTerminal));

            return ComputeTestFactory.Json(Body(job));
        }
    }

    private static string Body(FakeJob job)
        => ComputeTestFactory.JobBody(job.Id, sessionId: job.SessionId, status: job.Status, jobType: job.JobType, target: job.Target);

    private HttpResponseMessage ReadJob(Guid jobId)
    {
        lock (_lock)
        {
            if (!Jobs.TryGetValue(jobId, out FakeJob? job))
            {
                return ComputeTestFactory.Json("""{"error":"job_not_found"}""", HttpStatusCode.NotFound);
            }

            if (job.CancelRequested && !job.IsTerminal && !JobsNeverEnd)
            {
                // The grace window: the runner has been told to stop and has not said it did.
                if (job.GracePollsLeft > 0)
                {
                    job.GracePollsLeft--;
                }
                else
                {
                    End(job, "Cancelled");
                }
            }

            return ComputeTestFactory.Json(Body(job));
        }
    }

    private HttpResponseMessage CancelJob(Guid jobId)
    {
        lock (_lock)
        {
            if (!Jobs.TryGetValue(jobId, out FakeJob? job))
            {
                return ComputeTestFactory.Json("""{"error":"job_not_found"}""", HttpStatusCode.NotFound);
            }

            if (!job.IsTerminal && !JobsNeverEnd)
            {
                job.CancelRequested = true;
                job.GracePollsLeft = CancelGracePolls;
                if (CancelGracePolls <= 0)
                {
                    End(job, "Cancelled");
                }
            }

            return ComputeTestFactory.Json(Body(job), HttpStatusCode.Accepted);
        }
    }

    private void End(FakeJob job, string status)
    {
        job.Status = status;
        Events.Add("end:" + job.JobType + ":" + status);
    }

    private HttpResponseMessage RunOp(Guid sessionId, string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string opId = root.TryGetProperty("opId", out JsonElement opIdElement) ? opIdElement.GetString() ?? string.Empty : string.Empty;
        string op = root.GetProperty("op").GetString() ?? string.Empty;
        JsonNode? args = root.TryGetProperty("args", out JsonElement argsElement)
            ? JsonNode.Parse(argsElement.GetRawText())
            : null;

        lock (_lock)
        {
            Ops.Add(new FakeOp(opId, op, args));

            if (RunnerGone || sessionId != SessionId || Jobs.Values.Any(j => j.SessionId == sessionId && j.IsTerminal))
            {
                return Lost();
            }

            if (FailingOps.TryGetValue(op, out string? code))
            {
                return ComputeTestFactory.Json(
                    JsonSerializer.Serialize(new { sessionId, jobId = JobIds[^1], opId, ok = false, error = new { code, message = "refused by the test", retryable = false } }));
            }

            return Execute(op, args);
        }
    }

    private static HttpResponseMessage Lost()
        => ComputeTestFactory.Json(
            JsonSerializer.Serialize(new { error = "session_lost", reason = "runner_offline", message = "the machine is gone" }),
            HttpStatusCode.Gone);

    private HttpResponseMessage Execute(string op, JsonNode? args)
    {
        JsonObject result;
        switch (op)
        {
            case "writeFile":
                {
                    string path = args!["path"]!.GetValue<string>();
                    string encoding = args["encoding"]?.GetValue<string>() ?? "utf8";
                    byte[] data = string.Equals(encoding, "base64", StringComparison.Ordinal)
                        ? Convert.FromBase64String(args["content"]!.GetValue<string>())
                        : Encoding.UTF8.GetBytes(args["content"]!.GetValue<string>());
                    Files[path] = data;
                    result = new JsonObject { ["bytes"] = data.LongLength, ["sha256"] = Sha256(data) };
                    break;
                }

            case "readFile":
                {
                    string path = args!["path"]!.GetValue<string>();
                    if (!Files.TryGetValue(path, out byte[]? data))
                    {
                        return ComputeTestFactory.Json(
                            JsonSerializer.Serialize(new { sessionId = SessionId, jobId = JobIds.Count > 0 ? JobIds[^1] : Guid.Empty, opId = "x", ok = false, error = new { code = "not_found", message = "no such file", retryable = false } }));
                    }

                    result = new JsonObject
                    {
                        ["content"] = Encoding.UTF8.GetString(data),
                        ["encoding"] = "utf8",
                        ["bytes"] = data.LongLength,
                        ["truncated"] = false
                    };
                    break;
                }

            case "listFiles":
                {
                    string listed = args?["path"]?.GetValue<string>() ?? ".";
                    string prefix = listed is "" or "." ? string.Empty : listed.TrimEnd('/') + "/";
                    JsonArray entries = new();
                    SortedSet<string> directories = new(StringComparer.Ordinal);

                    // The listing the runner produces is a real directory walk, so a directory that only
                    // exists because a file lives under it (node_modules) is reported as a directory.
                    // Deriving it here rather than storing it keeps the fake's state honest: it says what
                    // the workspace ACTUALLY contains.
                    foreach (KeyValuePair<string, byte[]> file in Files)
                    {
                        if (!file.Key.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string remainder = file.Key[prefix.Length..];
                        int slash = remainder.IndexOf('/');
                        if (slash >= 0)
                        {
                            directories.Add(prefix + remainder[..slash]);
                            continue;
                        }

                        if (remainder.Length == 0)
                        {
                            continue;
                        }

                        entries.Add(new JsonObject { ["path"] = file.Key, ["kind"] = "file", ["bytes"] = file.Value.LongLength });
                    }

                    foreach (string directory in directories)
                    {
                        entries.Add(new JsonObject { ["path"] = directory, ["kind"] = "dir", ["bytes"] = 0 });
                    }

                    // os.Root.Open on a directory that is not there fails, and the runner maps that to
                    // "not_found" — the answer the engine's listing helper treats as an empty directory.
                    if (prefix.Length > 0 && entries.Count == 0)
                    {
                        return ComputeTestFactory.Json(
                            JsonSerializer.Serialize(new { sessionId = SessionId, jobId = JobIds[^1], opId = "x", ok = false, error = new { code = "not_found", message = "no such directory", retryable = false } }));
                    }

                    result = new JsonObject { ["entries"] = entries };
                    break;
                }

            case "deletePath":
                {
                    string path = args!["path"]!.GetValue<string>();
                    Files.Remove(path);
                    result = new JsonObject { ["deleted"] = true };
                    break;
                }

            case "status":
                result = new JsonObject { ["workspaceReady"] = true, ["diskUsedBytes"] = 0, ["openProcesses"] = 0 };
                break;

            case "exec":
                {
                    JsonArray argv = args?["args"]?.AsArray() ?? new JsonArray();
                    string rendered = (args?["command"]?.GetValue<string>() ?? string.Empty) + " " + string.Join(' ', argv.Select(a => a?.GetValue<string>() ?? string.Empty));
                    result = new JsonObject
                    {
                        ["exitCode"] = 0,
                        ["stdout"] = "ran:" + rendered.Trim(),
                        ["stderr"] = string.Empty,
                        ["stdoutTruncated"] = false,
                        ["stderrTruncated"] = false,
                        ["durationMs"] = 1
                    };
                    break;
                }

            case "hydrate":
                {
                    JsonArray restored = new();
                    JsonArray skipped = new();
                    foreach (JsonNode? item in args?["manifest"]?.AsArray() ?? new JsonArray())
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        string path = item["path"]!.GetValue<string>();
                        string sha = item["sha256"]!.GetValue<string>();
                        string url = item["url"]!.GetValue<string>();

                        if (Files.TryGetValue(path, out byte[]? present) && string.Equals(Sha256(present), sha, StringComparison.Ordinal))
                        {
                            skipped.Add(path);
                            continue;
                        }

                        // The runner GETs the presigned URL. The fake resolves it against the same store,
                        // so a manifest that names a blob which was never written FAILS here rather than
                        // quietly succeeding — which is what makes this test evidence rather than theatre.
                        byte[]? fetched = _store.FetchByUrl(url);
                        if (fetched is null || !string.Equals(Sha256(fetched), sha, StringComparison.Ordinal))
                        {
                            return ComputeTestFactory.Json(
                                JsonSerializer.Serialize(new { sessionId = SessionId, jobId = JobIds[^1], opId = "x", ok = false, error = new { code = "stage_failed", message = "the journal blob could not be fetched: " + path, retryable = false } }));
                        }

                        Files[path] = fetched;
                        restored.Add(path);
                    }

                    result = new JsonObject { ["restored"] = restored, ["skipped"] = skipped };
                    break;
                }

            case "close":
                {
                    // The runner tears the workspace down, and the gateway ends the session's job
                    // (finishClosedSession) - unless the test says that second half did not happen.
                    Files.Clear();
                    FakeJob? closed = Jobs.Values.FirstOrDefault(j => j.SessionId == SessionId);
                    if (closed is not null && !closed.IsTerminal && !CloseLeavesJobRunning && !JobsNeverEnd)
                    {
                        End(closed, "Succeeded");
                    }

                    result = new JsonObject();
                    break;
                }

            default:
                return ComputeTestFactory.Json(
                    JsonSerializer.Serialize(new { sessionId = SessionId, jobId = JobIds[^1], opId = "x", ok = false, error = new { code = "unsupported_op", message = op, retryable = false } }));
        }

        return ComputeTestFactory.Json(JsonSerializer.Serialize(new
        {
            sessionId = SessionId,
            jobId = JobIds[^1],
            opId = "x",
            ok = true,
            result
        }));
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

/// <summary>One job as <see cref="FakeRunnerGateway"/> holds it.</summary>
internal sealed class FakeJob(Guid id, string jobType, Guid sessionId, string target)
{
    public Guid Id { get; } = id;

    public string JobType { get; } = jobType;

    public Guid SessionId { get; } = sessionId;

    public string Target { get; } = target;

    public string Status { get; set; } = "Running";

    public bool CancelRequested { get; set; }

    public int GracePollsLeft { get; set; }

    public bool IsTerminal => Status is "Succeeded" or "Failed" or "Cancelled" or "Expired";
}
