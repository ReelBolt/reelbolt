using System.Net;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// The engine's sandbox workspace seam (WP D7a). Every sandbox operation the engine performs goes
/// through this interface, so that a later work package can put a REMOTE implementation behind it
/// (D7b's <c>RemoteSandboxSession</c>, speaking <c>remotion.workspace-session</c> ops to a runner
/// through the gateway) without any agent tool or executor changing.
///
/// <para>
/// <b>Shape.</b> One instance is one workspace session — the unit <c>docs/runner-protocol.md</c>
/// §"Job types" defines ("the unit that the engine opens once per workflow execution") — so
/// <see cref="ExecutionId"/> is a property, not an argument. A session is obtained from
/// <see cref="ISandboxSessionFactory"/>; D7b is what makes it long-lived and pinned, the local
/// implementation is stateless and safe to recreate per call.
/// </para>
///
/// <para>
/// <b>Op mapping.</b> Method names follow the op set agreed in <c>docs/runner-protocol.md</c>
/// §"Remotion workspace ops", so the remote implementation is a translation rather than a redesign:
/// </para>
/// <list type="table">
/// <item><description><c>writeFile</c> — <see cref="WriteTextAsync"/> (encoding <c>utf8</c>) and <see cref="WriteBytesAsync"/> (encoding <c>base64</c>).</description></item>
/// <item><description><c>readFile</c> — <see cref="ReadTextAsync"/>.</description></item>
/// <item><description><c>listFiles</c> — <see cref="ListFilesAsync"/>.</description></item>
/// <item><description><c>deletePath</c> — <see cref="DeletePathAsync"/>.</description></item>
/// <item><description><c>exec</c> — <see cref="ExecAsync"/>.</description></item>
/// <item><description><c>status</c> — <see cref="StatusAsync"/>.</description></item>
/// <item><description><c>close</c> — <see cref="CompleteAsync"/>.</description></item>
/// </list>
///
/// <para>
/// <b>The byte-moving ops (D8).</b> <c>stage</c> and <c>publish</c> are on the seam as
/// <see cref="StageFromUrlAsync"/> and <see cref="PublishToUrlAsync"/>, and both take a
/// <em>presigned URL</em> rather than an object key: a URL grants one object and one method
/// (<c>docs/runner-protocol.md</c> §"Presigned URLs and object storage"), the engine mints it,
/// consumes it inside the call and never stores it. Keeping the key space, the TTL and the
/// verification on the caller's side of the seam is deliberate — the local implementation talks to a
/// sandbox-executor with no object store at all, and it must be able to answer the same two ops by
/// moving the bytes itself, which is exactly the pre-D8 behaviour the self-host install keeps.
/// <see cref="ResolvePublishRouteAsync"/> is the counterpart that tells a caller where its bytes may
/// go: straight to the final key on a local session, or into the runner job's staging key space
/// where the platform verifies and promotes them (D12).
/// </para>
/// <para>
/// <c>render</c> and <c>hydrate</c> are deliberately still not members. <c>hydrate</c> is D7b's
/// workspace journal and is issued inside <c>RemoteSandboxSession</c>'s own recovery; <c>render</c>
/// is <c>publish</c> with the CLI invocation in front of it, and the engine's render path already
/// issues the exec itself (it has to: it applies the output policy to what comes back).
/// </para>
///
/// <para>
/// <b>Failure contract.</b> A sandbox call that answered with a non-success status throws
/// <see cref="SandboxRequestFailedException"/> — an <see cref="InvalidOperationException"/> carrying
/// the status code and the identical message the engine has always surfaced — so a caller that used
/// to branch on a response status (a missing directory is an ANSWER, not a failure) still can, and a
/// caller that used to let it escape still gets the same exception type and text. Transport failures
/// (DNS, connect, timeout) are NOT wrapped: they propagate as what the HTTP stack threw, except in
/// <see cref="ExecAsync"/>, which reports them as data for the agent to act on
/// (<see cref="SandboxExecResult.TransportError"/>) exactly as the exec tool always has.
/// </para>
/// </summary>
public interface ISandboxSession
{
    /// <summary>The workflow execution this session's workspace belongs to. Rendered into every request path.</summary>
    Guid ExecutionId { get; }

    /// <summary>
    /// Create or reuse the workspace bound to <see cref="ExecutionId"/>.
    /// </summary>
    /// <remarks>
    /// No protocol op of its own: opening the session IS this call in the remote case (D7b opens
    /// lazily on the first op). Kept explicitly because the engine's <c>ensure_sandbox</c> tool
    /// reports the created workspace's metadata back to the agent.
    /// </remarks>
    Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken);

    /// <summary>Read the current workspace's metadata (container/workspace/activity timestamps).</summary>
    /// <remarks>
    /// Legacy sandbox-executor resource view; the protocol reports nothing equivalent, so a remote
    /// implementation answers from its own session record.
    /// </remarks>
    Task<SandboxInfo> GetAsync(CancellationToken cancellationToken);

    /// <summary>Protocol op <c>status</c>: whether the workspace is ready, plus its container and timestamps.</summary>
    Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken);

    /// <summary>Protocol op <c>listFiles</c>.</summary>
    /// <exception cref="SandboxRequestFailedException">With <see cref="SandboxRequestFailedException.StatusCode"/> <see cref="HttpStatusCode.NotFound"/> when the path does not exist.</exception>
    Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken);

    /// <summary>Protocol op <c>readFile</c>, decoded to text.</summary>
    Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Protocol op <c>writeFile</c> with <c>encoding: utf8</c>.</summary>
    Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken);

    /// <summary>Protocol op <c>writeFile</c> with <c>encoding: base64</c> — the arm binary payloads (staged audio, images) take.</summary>
    /// <remarks>
    /// Status-only, deliberately, and unlike <see cref="WriteTextAsync"/>: the call site that writes
    /// bytes has never read the acknowledgement body (it branches on the status alone), so
    /// <see cref="SandboxRequestFailedException"/> is the whole answer here.
    /// </remarks>
    Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken);

    /// <summary>Protocol op <c>deletePath</c>.</summary>
    Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Protocol op <c>exec</c>. The worker re-validates the argv against the allowlist in
    /// <c>docs/runner-protocol.md</c> §"Remotion workspace ops"; the engine-side allowlists the
    /// sandbox tools already apply stay where they are, in the tool methods that declare them.
    /// </summary>
    Task<SandboxExecResult> ExecAsync(
        string command,
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Install npm packages into the workspace.
    /// </summary>
    /// <remarks>
    /// Not a protocol op — "Package installation is not an op in v1, because the runtime container
    /// has no network egress". It is on the seam because the engine's <c>install_npm_packages</c>
    /// tool issues it and a remote implementation must therefore answer it (from the template image,
    /// or by refusing).
    /// </remarks>
    Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken);

    /// <summary>
    /// Stream a workspace file's raw bytes out of the sandbox without base64 or an inline cap — the
    /// arm a render takes.
    /// </summary>
    /// <param name="commandTimeoutSeconds">
    /// The sized deadline the caller would otherwise have put on its own HTTP client. The local
    /// implementation sizes the raw GET off it so a long render download is not cut off at
    /// <see cref="TimeSpan"/>'s framework default — see <see cref="LocalSandboxSession.ResolveHttpTimeout"/>.
    /// </param>
    /// <remarks>
    /// This is the read half of protocol op <c>publish</c>: on a runner-backed session the file is
    /// published into the job's staging key space and read back over a presigned GET, because a
    /// workspace file's bytes can never travel inline (the inline cap is 4 MiB).
    /// </remarks>
    Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken);

    /// <summary>Protocol op <c>close</c>: destroy the workspace and end the session.</summary>
    Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Protocol op <c>stage</c>: fetch a presigned GET URL into the workspace at
    /// <see cref="SandboxStageRequest.Path"/>. The URL is the only thing that names an object, and it
    /// is re-minted per call — never cached, never logged with its query string.
    /// </summary>
    /// <exception cref="SandboxRequestFailedException">
    /// With <see cref="HttpStatusCode.NotFound"/> when the object is not there: that is the answer
    /// the voiceover tool reports as <c>object_not_found</c>, not a step failure.
    /// </exception>
    Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Protocol op <c>publish</c>: PUT the workspace file at <see cref="SandboxPublishRequest.Path"/>
    /// to a presigned PUT URL. The result's <see cref="SandboxTransfer.Sha256"/> is the runner's own
    /// digest — a HINT the platform compares against its own, never an answer (D12).
    /// </summary>
    Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Where a publish out of this session must be routed (D8). Resolved lazily, and the answer is
    /// final for the session's lifetime in every case that matters: a session that degraded to a
    /// local one (the fabric is not deployed here, or the selector answered Local) reports the local
    /// route, because that is genuinely where its bytes go.
    /// </summary>
    Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Composition revisions: stream the whole workspace out as ONE gzip-compressed tar (sources,
    /// manifest/lockfile, public assets, config — never <c>node_modules</c>, <c>out/</c> or caches),
    /// so it can be stored as a composition workspace version. The archive is deterministic for an
    /// unchanged workspace, which is what lets the caller dedupe by SHA-256.
    /// </summary>
    /// <remarks>
    /// A default member so every pre-existing implementation (and test fake) keeps compiling: a
    /// session that cannot move a workspace says so with a TYPED
    /// <see cref="SandboxOperationNotSupportedException"/>, which every caller treats as "no snapshot",
    /// never as a step failure. The local session overrides it (sandbox-executor
    /// <c>GET /workspace/archive</c>); a runner-backed one is the documented follow-up
    /// (docs/design/composition-revisions.md "The Fabric path").
    /// </remarks>
    Task<SandboxWorkspaceArchive> ExportWorkspaceAsync(int commandTimeoutSeconds, CancellationToken cancellationToken)
        => throw new SandboxOperationNotSupportedException(nameof(ExportWorkspaceAsync), GetType().Name);

    /// <summary>
    /// Composition revisions: replace the workspace's source tree with the archive's contents (the
    /// template's <c>node_modules</c> is kept) and report how the archive's dependencies compare to the
    /// sandbox image's template. The archive is validated on the far side (no links, no traversal,
    /// size and entry caps); this side only streams it.
    /// </summary>
    /// <param name="archive">The archive bytes; read to the end, never buffered whole.</param>
    /// <param name="length">The exact byte length (sent as <c>Content-Length</c>).</param>
    /// <param name="commandTimeoutSeconds">Deadline for the transfer, sized by the caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>Default member for the same reason as <see cref="ExportWorkspaceAsync"/>.</remarks>
    Task<SandboxWorkspaceImport> ImportWorkspaceAsync(
        Stream archive,
        long length,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
        => throw new SandboxOperationNotSupportedException(nameof(ImportWorkspaceAsync), GetType().Name);
}

/// <summary>
/// A sandbox operation this session cannot perform — today only the workspace archive transfer on a
/// runner-backed session. Distinct from <see cref="SandboxRequestFailedException"/> on purpose: it is
/// not a failed request but a capability the transport lacks, so a caller can degrade cleanly
/// ("no snapshot") instead of reporting a sandbox fault.
/// </summary>
public sealed class SandboxOperationNotSupportedException : NotSupportedException
{
    public SandboxOperationNotSupportedException(string operation, string sessionKind)
        : base($"The sandbox session '{sessionKind}' cannot perform '{operation}'. Moving a whole workspace in or out "
               + "is implemented for the local sandbox-executor only; see docs/design/composition-revisions.md \"The Fabric path\".")
    {
        Operation = operation;
    }

    /// <summary>The member that was not supported.</summary>
    public string Operation { get; }
}

/// <summary>What the sandbox reported after restoring a workspace archive.</summary>
/// <param name="Files">Regular files written.</param>
/// <param name="Directories">Directories created.</param>
/// <param name="Bytes">Uncompressed bytes written.</param>
/// <param name="TemplateCompared">Whether the sandbox could read its template's <c>package.json</c> to compare against.</param>
/// <param name="DependenciesMatchTemplate">True when every dependency the archive declares is the template's, at the same spec.</param>
/// <param name="ExtraDependencies"><c>name@spec</c> entries the archive declares that the template lacks or pins differently.</param>
public sealed record SandboxWorkspaceImport(
    int Files,
    int Directories,
    long Bytes,
    bool TemplateCompared,
    bool DependenciesMatchTemplate,
    IReadOnlyList<string>? ExtraDependencies);

/// <summary>
/// A streamed workspace archive. Like <see cref="SandboxRawFile"/>, disposing it releases the response
/// and the client that carries it, so the caller must finish reading <see cref="Content"/> first.
/// </summary>
public sealed class SandboxWorkspaceArchive : IAsyncDisposable
{
    private readonly IDisposable? _response;
    private readonly IDisposable? _client;

    public SandboxWorkspaceArchive(Stream content, int? entries, long? uncompressedBytes, int? skipped, IDisposable? response = null, IDisposable? client = null)
    {
        Content = content;
        Entries = entries;
        UncompressedBytes = uncompressedBytes;
        Skipped = skipped;
        _response = response;
        _client = client;
    }

    /// <summary>The gzip-compressed tar bytes.</summary>
    public Stream Content { get; }

    /// <summary>Files and directories in the archive, as the sandbox counted them (null when not reported).</summary>
    public int? Entries { get; }

    /// <summary>Uncompressed bytes in the archive (null when not reported).</summary>
    public long? UncompressedBytes { get; }

    /// <summary>Entries the sandbox refused to archive — links, devices — (null when not reported).</summary>
    public int? Skipped { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        _response?.Dispose();
        _client?.Dispose();
    }
}

/// <summary>
/// Protocol op <c>stage</c> (D8): one object, one method, one direction. See
/// <see cref="ISandboxSession.StageFromUrlAsync"/>.
/// </summary>
/// <param name="Url">The presigned GET. A URL is a bearer secret; it is never logged with its query string.</param>
/// <param name="Path">The workspace-relative destination.</param>
/// <param name="MaxBytes">The cap the worker enforces while streaming, and the local arm enforces after buffering.</param>
/// <param name="CommandTimeoutSeconds">The deadline, sized by the caller; the local arm sizes its own HTTP client off it.</param>
/// <param name="Sha256">The digest the caller expects. A runner may refuse a mismatch; the local arm does not check it.</param>
public sealed record SandboxStageRequest(
    string Url,
    string Path,
    long MaxBytes,
    int CommandTimeoutSeconds,
    string? Sha256 = null);

/// <summary>
/// Protocol op <c>publish</c> (D8): the workspace's bytes go to the URL the caller minted, and
/// nowhere else. See <see cref="ISandboxSession.PublishToUrlAsync"/>.
/// </summary>
/// <param name="Url">The presigned PUT. On a runner-backed session this is always <c>runner-staging/{jobId}/{n}</c>.</param>
/// <param name="Path">The workspace-relative source.</param>
/// <param name="MaxBytes">The cap the worker enforces while streaming.</param>
/// <param name="ContentType">Sent as the PUT's <c>Content-Type</c>; the presigned URL signed it, so the object keeps it.</param>
/// <param name="CommandTimeoutSeconds">The deadline, sized by the caller.</param>
public sealed record SandboxPublishRequest(
    string Url,
    string Path,
    long MaxBytes,
    string ContentType,
    int CommandTimeoutSeconds);

/// <summary>What a byte-moving op reports. <see cref="Sha256"/> is null when the arm that moved the bytes does not compute one.</summary>
public sealed record SandboxTransfer(long Bytes, string? Sha256);

/// <summary>
/// D8: how a publish out of this session must be routed.
/// <list type="bullet">
///   <item>
///   <b>Local</b> (<see cref="RequiresVerification"/> false) — nothing crossed a machine boundary, so
///   the caller uploads straight to the key it named. That is the pre-D8 behaviour, unchanged.
///   </item>
///   <item>
///   <b>Runner-backed</b> (true) — the only object a runner may be handed is one in its own job's
///   staging key space, and it reaches its final key only after D12 has verified it.
///   </item>
/// </list>
/// </summary>
/// <param name="RequiresVerification">True when the bytes come from a machine the engine does not control.</param>
/// <param name="RunnerJobId">The runner job whose staging key space a PUT URL must target; null on the local route.</param>
/// <param name="Target">What the job runs as, which is what decides D12's spot check; null on the local route.</param>
public sealed record SandboxPublishRoute(bool RequiresVerification, Guid? RunnerJobId, RunnerJobTarget? Target);

/// <summary>
/// Obtains the <see cref="ISandboxSession"/> for one workflow execution. The engine's sandbox tool
/// is a singleton and an execution-scoped session must not be, so the tool resolves one per call
/// through this seam; D7b is where that resolution becomes "pin the first session for the execution
/// and keep it", which is a change to the factory alone.
/// </summary>
public interface ISandboxSessionFactory
{
    /// <summary>The workspace session for <paramref name="executionId"/>.</summary>
    ISandboxSession Create(Guid executionId);
}

/// <summary>
/// A sandbox call answered with a non-success HTTP status. Deliberately an
/// <see cref="InvalidOperationException"/>: that is the exception type the engine has always thrown
/// for a failed sandbox request, with this exact message, and callers (and tests) catch it as one.
/// </summary>
public sealed class SandboxRequestFailedException : InvalidOperationException
{
    public SandboxRequestFailedException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The status the sandbox answered with — <see cref="HttpStatusCode.NotFound"/> is a legitimate answer to a listing.</summary>
    public HttpStatusCode StatusCode { get; }
}

/// <summary>The sandbox's own error envelope (<c>{"error": "..."}</c>).</summary>
public sealed record SandboxError(string? Error);

/// <summary>Workspace metadata, as the sandbox-executor and the engine's <c>ensure_sandbox</c>/<c>get_sandbox</c> tools report it.</summary>
public sealed record SandboxInfo(
    string Id,
    string WorkflowExecutionId,
    string ContainerName,
    string WorkspacePath,
    DateTime CreatedAt,
    DateTime LastActivity);

/// <summary>Protocol op <c>status</c>.</summary>
public sealed record SandboxStatusResponse(
    bool Exists,
    bool Ready,
    bool HasPackageJson,
    bool HasNodeModules,
    string? ContainerName,
    string? WorkspacePath,
    DateTime? CreatedAt,
    DateTime? LastActivity);

/// <summary>One protocol <c>listFiles</c> entry.</summary>
public sealed record SandboxFileEntry(string Name, bool IsDir, long Size, DateTime ModTime);

/// <summary>Protocol op <c>readFile</c>, decoded to UTF-8 text.</summary>
public sealed record SandboxTextFile(string Path, string Text);

/// <summary>The sandbox-executor's flat <c>{"ok": bool}</c> acknowledgement.</summary>
public sealed record SandboxActionResult(bool Ok);

/// <summary>Protocol op <c>exec</c>'s payload: the command's own stdout/stderr, folded into the two fields the tools read.</summary>
public sealed record SandboxExecResponse(string? Output, string? Error);

/// <summary>
/// Protocol op <c>exec</c>'s outcome. Everything the exec tool needs to build the same
/// model-facing result it has always built — including <see cref="TransportError"/>, which is data
/// here rather than an exception because a sandbox that is not reachable is something the agent is
/// told to retry against, not a step failure.
/// </summary>
/// <param name="RawBody">
/// The response body as it arrived. <see cref="Payload"/> is the tolerant reading of it; the
/// <c>npm run</c> helper parses this raw text itself, which is what it did before the seam, so a
/// body that is not JSON at all still behaves exactly as it used to.
/// </param>
public sealed record SandboxExecResult(
    bool Succeeded,
    SandboxExecResponse Payload,
    int? StatusCode,
    string RawBody,
    string RawError,
    Exception? TransportError);

/// <summary>
/// A streamed workspace file. Disposing it releases the response and the HTTP client that carries
/// it, so the caller must finish reading <see cref="Content"/> first — hence <c>await using</c>.
/// </summary>
public sealed class SandboxRawFile : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;

    private readonly Func<ValueTask>? _onDispose;

    internal SandboxRawFile(
        HttpClient client,
        HttpResponseMessage response,
        Stream content,
        long? contentLength,
        Func<ValueTask>? onDispose = null)
    {
        _client = client;
        _response = response;
        Content = content;
        ContentLength = contentLength;
        _onDispose = onDispose;
    }

    /// <summary>The file's bytes.</summary>
    public Stream Content { get; }

    /// <summary>
    /// The declared length, or null when the sandbox did not send a <c>Content-Length</c> — the case
    /// the render path refuses, because it must tell the object store how many bytes to expect from
    /// a non-seekable stream.
    /// </summary>
    public long? ContentLength { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        _response.Dispose();
        _client.Dispose();

        // D8: a raw read out of a runner-backed session IS a publish, so the staging object the read
        // was served from is deleted here rather than left in the bucket. Best effort, and never a
        // failure: the file has already been read and the object has no other reader.
        if (_onDispose is not null)
        {
            try
            {
                await _onDispose();
            }
            catch (Exception)
            {
                // Intentionally swallowed: a caller disposing a stream must not be handed a new
                // exception for cleanup it never asked for.
            }
        }
    }
}
