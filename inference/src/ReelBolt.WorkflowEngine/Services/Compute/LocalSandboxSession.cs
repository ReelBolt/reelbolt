using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// The self-host implementation of <see cref="ISandboxSession"/>: the engine's direct HTTP client for
/// the sandbox-executor (<c>sandbox/cmd/sandbox-executor</c>, <c>/api/v1/sandboxes/**</c>), extracted
/// verbatim from <c>ReactRemotionSandboxTools</c> by WP D7a. Every request, path, header, body shape,
/// timeout and error message here is the one that tool used to build inline, so this is a move and
/// not a rewrite.
/// </summary>
public sealed class LocalSandboxSession : ISandboxSession
{
    /// <summary>
    /// Headroom added to a sandbox command's OWN deadline when sizing the HTTP client timeout, so the
    /// sandbox always wins the race and reports a structured timeout the agent can act on, instead of
    /// the client aborting the connection first and surfacing a bare transport error.
    /// </summary>
    public const int CommandTimeoutHeadroomSeconds = 60;

    /// <summary>
    /// The ceiling on an agent-requested command's deadline. Also stands in for the sandbox's configured
    /// default (<c>SANDBOX_EXEC_TIMEOUT</c>, 5 minutes out of the box) when a caller requests 0 — the
    /// engine cannot read that env var, so it sizes to the ceiling that bounds it.
    /// </summary>
    public const int MaxCommandSeconds = 900;

    /// <summary>
    /// The largest deadline the engine ever sends (a long film's render, sized by
    /// <c>RenderTimeoutPolicy</c>); the sandbox clamps to its own <c>SANDBOX_EXEC_MAX_TIMEOUT</c>,
    /// 6 hours by default.
    /// </summary>
    public const int MaxRequestedCommandSeconds = 6 * 3600;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _apiToken;

    public LocalSandboxSession(IHttpClientFactory httpClientFactory, Guid executionId, string baseUrl, string apiToken)
    {
        _httpClientFactory = httpClientFactory;
        ExecutionId = executionId;
        _baseUrl = baseUrl;
        _apiToken = apiToken;
    }

    /// <inheritdoc />
    public Guid ExecutionId { get; }

    /// <summary>
    /// Client-side HTTP timeout for a sandbox exec whose server-side deadline is
    /// <paramref name="commandTimeoutSeconds"/> (0 = "let the sandbox pick its default").
    ///
    /// <para>
    /// Load-bearing, and the fix for a real outage: <see cref="IHttpClientFactory"/> hands back a
    /// client on .NET's DEFAULT 100-second timeout, which is SHORTER than every exec deadline the
    /// engine sends — a Remotion render asks for 600s, an agent-driven command for up to the
    /// sandbox's 900s ceiling, and even the sandbox's own no-op default is 300s. The client therefore
    /// aborted the POST at exactly 100s no matter what the sandbox had been told, so any render
    /// slower than that — i.e. essentially every real one — died with a spurious "Sandbox command
    /// network error" while the sandbox was still rendering happily, and the agent saw a transport
    /// failure rather than a timeout it could retry with a longer deadline. Mirrors the server's own
    /// clamp so the two stay ordered.
    /// </para>
    /// </summary>
    public static TimeSpan ResolveHttpTimeout(int commandTimeoutSeconds)
    {
        int effective = commandTimeoutSeconds > 0
            ? Math.Min(commandTimeoutSeconds, MaxRequestedCommandSeconds)
            : MaxCommandSeconds;
        return TimeSpan.FromSeconds(effective + CommandTimeoutHeadroomSeconds);
    }

    /// <inheritdoc />
    public async Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/sandboxes",
            new { workflowExecutionId = ExecutionId.ToString() },
            cancellationToken);

        return await ReadJsonAsync<SandboxInfo>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SandboxInfo> GetAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.GetAsync($"/api/v1/sandboxes/{ExecutionId}", cancellationToken);
        return await ReadJsonAsync<SandboxInfo>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.GetAsync($"/api/v1/sandboxes/{ExecutionId}/status", cancellationToken);
        return await ReadJsonAsync<SandboxStatusResponse>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
    {
        string encodedPath = Uri.EscapeDataString(path);
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/sandboxes/{ExecutionId}/files?path={encodedPath}",
            cancellationToken);

        return await ReadJsonAsync<List<SandboxFileEntry>>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        string encodedPath = Uri.EscapeDataString(path);
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/sandboxes/{ExecutionId}/files/content?path={encodedPath}",
            cancellationToken);

        SandboxFileContent payload = await ReadJsonAsync<SandboxFileContent>(response, cancellationToken);
        byte[] data = Convert.FromBase64String(payload.ContentBase64);
        return new SandboxTextFile(payload.Path, Encoding.UTF8.GetString(data));
    }

    /// <inheritdoc />
    public async Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        string encodedPath = Uri.EscapeDataString(path);
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/sandboxes/{ExecutionId}/files/content?path={encodedPath}",
            new { contentBase64 = Convert.ToBase64String(bytes) },
            cancellationToken);

        return await ReadJsonAsync<SandboxActionResult>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        string encodedPath = Uri.EscapeDataString(path);
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/sandboxes/{ExecutionId}/files/content?path={encodedPath}",
            new { contentBase64 = Convert.ToBase64String(content) },
            cancellationToken);

        // Status only — see the interface's remark. The body is read for the failure message alone.
        if (response.IsSuccessStatusCode)
            return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        SandboxError? error = TryDeserialize<SandboxError>(body);
        throw new SandboxRequestFailedException(
            response.StatusCode,
            $"Sandbox API request failed ({(int)response.StatusCode}): {error?.Error ?? body}");
    }

    /// <inheritdoc />
    public async Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken)
    {
        string encodedPath = Uri.EscapeDataString(path);
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.DeleteAsync(
            $"/api/v1/sandboxes/{ExecutionId}/files?path={encodedPath}",
            cancellationToken);

        return await ReadJsonAsync<SandboxActionResult>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/sandboxes/{ExecutionId}/complete",
            content: null,
            cancellationToken);

        return await ReadJsonAsync<SandboxActionResult>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SandboxExecResult> ExecAsync(
        string command,
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        string[] argArray = args as string[] ?? [.. args];

        using HttpClient client = CreateClient(timeoutSeconds);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                $"/api/v1/sandboxes/{ExecutionId}/exec",
                new SandboxExecRequest(command, argArray, timeoutSeconds),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // A sandbox that cannot be reached is reported as DATA, not as an exception: the agent is
            // told to check readiness and retry, which it can do, whereas a thrown exception escapes
            // the function-invocation layer and takes the whole execution with it.
            return new SandboxExecResult(
                Succeeded: false,
                Payload: new SandboxExecResponse(null, null),
                StatusCode: null,
                RawBody: string.Empty,
                RawError: ex.Message,
                TransportError: ex);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        SandboxExecResponse payload = TryDeserialize<SandboxExecResponse>(body) ?? new SandboxExecResponse(null, null);

        bool failed = !response.IsSuccessStatusCode || !string.IsNullOrWhiteSpace(payload.Error);
        if (!failed)
            return new SandboxExecResult(true, payload, (int)response.StatusCode, body, body, null);

        return new SandboxExecResult(
            Succeeded: false,
            Payload: payload,
            StatusCode: (int)response.StatusCode,
            RawBody: body,
            RawError: ExtractError(body, payload.Error),
            TransportError: null);
    }

    /// <inheritdoc />
    public async Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/sandboxes/{ExecutionId}/packages",
            new { packages },
            cancellationToken);

        return await ReadJsonAsync<SandboxExecResponse>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        string encodedPath = Uri.EscapeDataString(path);
        HttpClient client = CreateClient(commandTimeoutSeconds);
        try
        {
            using HttpRequestMessage rawRequest = new(
                HttpMethod.Get,
                $"/api/v1/sandboxes/{ExecutionId}/files/raw?path={encodedPath}");
            HttpResponseMessage fileResponse = await client.SendAsync(
                rawRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!fileResponse.IsSuccessStatusCode)
            {
                HttpStatusCode statusCode = fileResponse.StatusCode;
                string errBody = await fileResponse.Content.ReadAsStringAsync(cancellationToken);
                SandboxError? error = TryDeserialize<SandboxError>(errBody);
                fileResponse.Dispose();
                throw new SandboxRequestFailedException(
                    statusCode,
                    $"Sandbox API request failed ({(int)statusCode}): {error?.Error ?? errBody}");
            }

            Stream rawStream = await fileResponse.Content.ReadAsStreamAsync(cancellationToken);
            return new SandboxRawFile(client, fileResponse, rawStream, fileResponse.Content.Headers.ContentLength);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------
    // D8: the byte-moving ops
    // ------------------------------------------------------------------

    /// <summary>
    /// D8, the local arm of <c>stage</c>. The sandbox-executor has no way to fetch a URL — its
    /// container has no egress — so the engine performs the GET and pushes the bytes with the
    /// <c>writeFile</c> call this feature has always used. The URL is still the transport: it is
    /// minted per call by the caller, consumed here, and never stored.
    /// </summary>
    public async Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateStandaloneClient(request.CommandTimeoutSeconds);
        using HttpResponseMessage response = await client
            .GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // A missing object is an ANSWER on this arm, exactly as it is on a runner: the voiceover
            // tool reports it as object_not_found rather than failing the step.
            throw new SandboxRequestFailedException(
                response.StatusCode,
                $"Sandbox API request failed ({(int)response.StatusCode}): the object behind '{request.Path}' is not available to stage");
        }

        using MemoryStream buffer = new();
        await response.Content.CopyToAsync(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();

        if (request.MaxBytes > 0 && bytes.LongLength > request.MaxBytes)
        {
            throw new SandboxRequestFailedException(
                HttpStatusCode.RequestEntityTooLarge,
                $"Sandbox API request failed (413): '{request.Path}' is {bytes.LongLength} bytes, over the {request.MaxBytes}-byte staging cap");
        }

        await WriteBytesAsync(request.Path, bytes, cancellationToken).ConfigureAwait(false);
        return new SandboxTransfer(bytes.LongLength, null);
    }

    /// <summary>
    /// D8, the local arm of <c>publish</c> — and it is the D0 streaming path: the workspace file is
    /// read with <c>/files/raw</c> (no base64, no 64 MiB cap) and streamed straight into the PUT,
    /// never through a <c>byte[]</c>.
    /// </summary>
    public async Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken)
    {
        await using SandboxRawFile raw = await OpenRawFileAsync(
            request.Path, request.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        long? declaredLength = raw.ContentLength;
        using HttpClient client = CreateStandaloneClient(request.CommandTimeoutSeconds);
        using HttpContent content = new StreamContent(raw.Content);
        if (declaredLength is { } length)
        {
            content.Headers.ContentLength = length;
        }

        if (!string.IsNullOrWhiteSpace(request.ContentType))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
        }

        using HttpResponseMessage response = await client
            .PutAsync(request.Url, content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SandboxRequestFailedException(
                response.StatusCode,
                $"Sandbox API request failed ({(int)response.StatusCode}): publishing '{request.Path}' failed: " + body);
        }

        return new SandboxTransfer(declaredLength ?? 0, null);
    }

    /// <summary>
    /// The local route, always: a workspace on this host's own sandbox-executor is not a machine
    /// boundary, so there is nothing to verify and the caller writes its key directly — which is
    /// exactly what this tool did before D8.
    /// </summary>
    public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken)
        => Task.FromResult(new SandboxPublishRoute(false, null, null));

    // ------------------------------------------------------------------
    // Composition revisions: the whole workspace as one archive
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>GET /workspace/archive</c>: the sandbox-executor streams a deterministic tar.gz of the
    /// workspace (no base64, no buffering — the same streaming rule as <c>/files/raw</c>). Its size is
    /// unknown up front (it is compressed on the fly), so the caller spools it to scratch, which is also
    /// where it computes the digest the store records.
    /// </summary>
    public async Task<SandboxWorkspaceArchive> ExportWorkspaceAsync(int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        HttpClient client = CreateClient(commandTimeoutSeconds);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/sandboxes/{ExecutionId}/workspace/archive");
            HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                HttpStatusCode statusCode = response.StatusCode;
                string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
                SandboxError? error = TryDeserialize<SandboxError>(errBody);
                response.Dispose();
                throw new SandboxRequestFailedException(
                    statusCode,
                    $"Sandbox API request failed ({(int)statusCode}): {error?.Error ?? errBody}");
            }

            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new SandboxWorkspaceArchive(
                content,
                entries: ReadIntHeader(response, "X-Workspace-Entries"),
                uncompressedBytes: ReadLongHeader(response, "X-Workspace-Bytes"),
                skipped: ReadIntHeader(response, "X-Workspace-Skipped"),
                response: response,
                client: client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <c>PUT /workspace/archive?replace=true</c>: the archive is streamed as the request body with an
    /// exact <c>Content-Length</c>; the sandbox validates every entry (os.Root, no links, caps) before it
    /// writes anything outside its own scratch.
    /// </summary>
    public async Task<SandboxWorkspaceImport> ImportWorkspaceAsync(
        Stream archive,
        long length,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient(commandTimeoutSeconds);
        using StreamContent content = new(archive);
        content.Headers.ContentLength = length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");

        using HttpResponseMessage response = await client.PutAsync(
            $"/api/v1/sandboxes/{ExecutionId}/workspace/archive?replace=true",
            content,
            cancellationToken);

        return await ReadJsonAsync<SandboxWorkspaceImport>(response, cancellationToken);
    }

    private static int? ReadIntHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
        && int.TryParse(values.FirstOrDefault(), out int parsed)
            ? parsed
            : null;

    private static long? ReadLongHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
        && long.TryParse(values.FirstOrDefault(), out long parsed)
            ? parsed
            : null;

    /// <summary>
    /// A client for a presigned URL, which is absolute and therefore deliberately NOT the
    /// sandbox-executor's own client: that one carries the executor's bearer secret in a default
    /// header, and attaching it to a request aimed at the object store would send that secret to a
    /// third party's bucket. Sized off the caller's deadline for the same reason
    /// <see cref="ResolveHttpTimeout"/> exists.
    /// </summary>
    private HttpClient CreateStandaloneClient(int commandTimeoutSeconds)
    {
        HttpClient client = _httpClientFactory.CreateClient();
        client.Timeout = ResolveHttpTimeout(commandTimeoutSeconds);
        return client;
    }

    /// <param name="commandTimeoutSeconds">
    /// The server-side deadline of the sandbox exec this client is about to issue, when there is one
    /// (0 = the sandbox's own default). Pass null for plain API calls — file read/write/list — which
    /// are not execs and keep the framework default. See <see cref="ResolveHttpTimeout"/>.
    /// </param>
    private HttpClient CreateClient(int? commandTimeoutSeconds = null)
    {
        HttpClient client = _httpClientFactory.CreateClient();
        if (commandTimeoutSeconds is int seconds)
        {
            client.Timeout = ResolveHttpTimeout(seconds);
        }

        client.BaseAddress = new Uri(_baseUrl, UriKind.Absolute);
        // The sandbox API is unauthenticated apart from this shared secret, and it
        // rejects every request without it. It is never proxied through nginx, so
        // this header is the only credential involved.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiToken);
        return client;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            SandboxError? error = JsonSerializer.Deserialize<SandboxError>(body, JsonOptions);
            throw new SandboxRequestFailedException(
                response.StatusCode,
                $"Sandbox API request failed ({(int)response.StatusCode}): {error?.Error ?? body}");
        }

        T? result = JsonSerializer.Deserialize<T>(body, JsonOptions);
        if (result is null)
            throw new InvalidOperationException("Sandbox API response body was empty.");

        return result;
    }

    private static T? TryDeserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static string ExtractError(string body, string? payloadError)
    {
        if (!string.IsNullOrWhiteSpace(payloadError))
            return payloadError;

        SandboxError? error = TryDeserialize<SandboxError>(body);
        if (!string.IsNullOrWhiteSpace(error?.Error))
            return error.Error;

        return string.IsNullOrWhiteSpace(body)
            ? "Sandbox execution failed without an error message."
            : body;
    }

    /// <summary>The sandbox-executor's exec request body.</summary>
    private sealed record SandboxExecRequest(string Command, string[] Args, int TimeoutSeconds);

    /// <summary>The sandbox-executor's file-content response body.</summary>
    private sealed record SandboxFileContent(string Path, string ContentBase64);
}

/// <summary>
/// <see cref="ISandboxSessionFactory"/> for the self-host path: reads the sandbox-executor's address
/// and shared secret once (they are configuration, not per-execution state) and hands out a
/// stateless <see cref="LocalSandboxSession"/> per execution.
/// </summary>
public sealed class LocalSandboxSessionFactory : ISandboxSessionFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _apiToken;

    public LocalSandboxSessionFactory(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = configuration["Sandbox:BaseUrl"] ?? "http://sandbox-executor:8080";
        _apiToken = configuration["Sandbox:ApiToken"] ?? string.Empty;
    }

    /// <inheritdoc />
    public ISandboxSession Create(Guid executionId) =>
        new LocalSandboxSession(_httpClientFactory, executionId, _baseUrl, _apiToken);
}
