using System.Text;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>Result of a workspace <c>stat</c>: whether the file is there and how many bytes it holds.</summary>
public readonly record struct MediaWorkspaceStat(bool Exists, long Bytes);

/// <summary>
/// Thrown when a bounded <see cref="IMediaWorkspace.ReadAsync(string, int, CancellationToken)"/>
/// would have returned more than the caller's <c>maxBytes</c>. A silent short read would be worse
/// than a failure — the caller counts frames out of the bytes it gets back.
/// </summary>
public sealed class MediaWorkspaceFileTooLargeException : IOException
{
    public MediaWorkspaceFileTooLargeException(string virtualPath, long actualBytes, long maxBytes)
        : base($"File '{virtualPath}' is {actualBytes} bytes, over the {maxBytes}-byte read limit.")
    {
        VirtualPath = virtualPath;
        ActualBytes = actualBytes;
        MaxBytes = maxBytes;
    }

    public string VirtualPath { get; }

    public long ActualBytes { get; }

    public long MaxBytes { get; }
}

/// <summary>
/// The one seam between the media pipeline and the machine that actually holds the bytes
/// (plan D9a). Callers name files by a session-relative <b>virtual path</b> — never an absolute
/// local path — and every ffmpeg/ffprobe process, every read and every publish goes through this
/// object. The method set is deliberately a one-to-one map of the media-session ops in
/// <c>docs/runner-protocol.md</c> ("Media session ops and argv validation"), so a remote
/// implementation (D11) is a transport, not a redesign:
/// <list type="bullet">
///   <item><see cref="StageAsync"/> — <c>stage</c>: object storage → the session directory.</item>
///   <item><see cref="RunFfmpegAsync(IReadOnlyList{string}, TimeSpan, CancellationToken)"/> — <c>ffmpeg</c>.</item>
///   <item><see cref="RunFfprobeAsync"/> — <c>ffprobe</c>.</item>
///   <item><see cref="ReadAsync(string, int, CancellationToken)"/> — <c>read</c>.</item>
///   <item><see cref="PublishAsync"/> — <c>publish</c>: the session directory → object storage.</item>
///   <item><see cref="StatAsync"/> — <c>stat</c>.</item>
/// </list>
/// <para>
/// <b>Argv contract.</b> An argv handed to <see cref="RunFfmpegAsync(IReadOnlyList{string}, TimeSpan, CancellationToken)"/>
/// or <see cref="RunFfprobeAsync"/> carries only virtual paths, i.e. the strings
/// <see cref="MaterializeAsync"/> produced for them. The Local implementation returns a real
/// absolute path (ffmpeg is a local child process and needs one); a remote implementation returns
/// the virtual path unchanged, because the worker resolves it inside the session directory it
/// created — which is the protocol's "Argv uses only session-relative paths" rule. Callers
/// therefore never build argv from a path they got anywhere else.
/// </para>
/// </summary>
public interface IMediaWorkspace
{
    /// <summary>Root of this workspace's session directory (local diagnostics only).</summary>
    string RootPath { get; }

    /// <summary>
    /// True for a remote (runner) session, whose host has none of the engine's files: argv for it carries no
    /// engine-host path such as the engine's fonts (security T-326). False for every local workspace.
    /// </summary>
    bool IsRemoteSession => false;

    /// <summary>
    /// Copies the object at <paramref name="storageKey"/> into the session directory at
    /// <paramref name="virtualPath"/>.
    /// </summary>
    Task StageAsync(string storageKey, string virtualPath, CancellationToken ct);

    /// <summary>
    /// <see cref="StageAsync(string, string, CancellationToken)"/> with a byte cap enforced WHILE the object is
    /// copied (not only from the object's declared size): a copy that would exceed <paramref name="maxBytes"/>
    /// stops and throws <see cref="MediaStageLimitExceededException"/>, leaving nothing at the path. The default
    /// body exists only for test doubles: it stages, then checks the size, and cannot remove what it staged, so a
    /// caller never uses the path after this throws. <see cref="LocalMediaWorkspace"/> and
    /// <see cref="RemoteMediaWorkspace"/> override it and enforce the cap while streaming.
    /// </summary>
    async Task StageAsync(string storageKey, string virtualPath, long maxBytes, CancellationToken ct)
    {
        await StageAsync(storageKey, virtualPath, ct).ConfigureAwait(false);
        MediaWorkspaceStat stat = await StatAsync(virtualPath, ct).ConfigureAwait(false);
        if (stat.Exists && stat.Bytes > maxBytes)
            throw new MediaStageLimitExceededException(virtualPath, maxBytes);
    }

    Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct);

    /// <inheritdoc cref="IVideoToolRunner.RunFfmpegAsync(IReadOnlyList{string}, TimeSpan, CancellationToken, Action{string})"/>
    Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine);

    Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Resolves <paramref name="virtualPath"/> to the path string that must appear in an argv
    /// handed back to this workspace (see the interface remarks), creating the parent directory if
    /// the session directory does not have it yet.
    /// </summary>
    Task<string> MaterializeAsync(string virtualPath, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="content"/> into the session at <paramref name="virtualPath"/>,
    /// overwriting any file already there and creating the parent directory when it does not exist.
    /// <para>
    /// <b>Why this is on the seam at all.</b> The media-session op set
    /// (<c>docs/runner-protocol.md</c> §"Media session ops and argv validation") has no write op, and
    /// the engine nevertheless produces files that ffmpeg must READ FROM THE SESSION: the
    /// drawtext/caption slot files, the ASS subtitle script, the <c>-filter_complex_script</c> body
    /// and the concat list. Those are named from inside a filtergraph (<c>textfile=</c>,
    /// <c>subtitles=</c>, <c>-filter_complex_script</c>), which the worker's argv validator accepts
    /// only when "the referenced file is a session-relative path that ALREADY EXISTS in the session
    /// directory". Before D11 the executor wrote them with <c>File.WriteAllTextAsync</c> on a path
    /// <see cref="MaterializeAsync"/> returned, which is correct for <see cref="LocalMediaWorkspace"/>
    /// (that path really is the session) and silently wrong for a remote one (the returned path is
    /// the session-relative NAME, and writing it would land in the engine's working directory). This
    /// member is what closes that hole, and the local path that used to be spelled
    /// <c>Materialize + File.WriteAllText</c> is now one call with identical bytes.
    /// </para>
    /// </summary>
    Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct);

    /// <summary>
    /// Writes text into the session. The bytes are exactly what
    /// <c>File.WriteAllTextAsync(path, content, encoding)</c> would produce — <paramref name="encoding"/>'s
    /// preamble, then its encoding of <paramref name="content"/> — so a caller that used to pass
    /// <see cref="Encoding.UTF8"/> (with its byte-order mark) still gets one.
    /// </summary>
    Task WriteTextAsync(string virtualPath, string content, Encoding encoding, CancellationToken ct);

    /// <summary>Reads a session file whole. The caller must already know it is small.</summary>
    Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of a session file. More content than that is
    /// <see cref="MediaWorkspaceFileTooLargeException"/>, never a silent truncation.
    /// </summary>
    Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct);

    Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct);

    /// <summary>
    /// Uploads the session file at <paramref name="virtualPath"/> to exactly
    /// <paramref name="finalStorageKey"/>, which must lie under this workspace's project scope.
    /// A key the object store would not round-trip to is a failure, not a best effort.
    /// </summary>
    Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct);

    /// <summary>
    /// Uploads the session file at <paramref name="virtualPath"/> to exactly
    /// <paramref name="finalStorageKey"/> as this project's own bare artifact, and answers with the
    /// key the object store reports.
    /// <para>
    /// <b>How this differs from <see cref="PublishAsync"/>, and why both exist.</b>
    /// <see cref="PublishAsync"/> is the MEDIA-session publish: it fails unless the object store
    /// round-trips the key byte for byte, because the caller registers that key as the location of a
    /// generated program and a silent redirect would be a wrong result. This member is the
    /// DOCUMENT path - the engine's own EDL and timeline manifest, written into the session and
    /// uploaded as ordinary project artifacts - and its contract is the one
    /// <c>IProjectFileWorkspace.UploadArtifactAsync</c> has always had: the caller passes the key it
    /// wants and then uses what came back. The two arms differ only in transport: a local workspace
    /// reads the file off this host's disk, a remote one runs the session's <c>publish</c> op and
    /// verifies what the runner produced, since THAT object crossed a machine boundary the engine does
    /// not control.
    /// </para>
    /// </summary>
    Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct);
}

/// <summary>
/// The encodings the media pipeline writes with, named once. <see cref="Utf8NoBom"/> is what
/// <c>File.WriteAllTextAsync(path, text)</c> uses when no encoding is given — UTF-8 with NO
/// byte-order mark — and is therefore the default a ported writer must pass to keep its bytes
/// identical.
/// </summary>
public static class MediaWorkspaceTexts
{
    /// <summary>UTF-8 without a byte-order mark: the no-encoding default of <c>File.WriteAllText</c>.</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}

/// <summary>A capped stage stopped because the object is larger than the cap.</summary>
public sealed class MediaStageLimitExceededException(string virtualPath, long maxBytes)
    : IOException($"Staging '{virtualPath}' stopped: the object is larger than {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}
