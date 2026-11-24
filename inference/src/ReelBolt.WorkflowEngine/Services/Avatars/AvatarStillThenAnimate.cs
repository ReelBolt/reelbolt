using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Avatars;

// Avatars phase 3, the still-then-animate path (docs/design/avatars.md "Phased delivery", docs/avatars.md "The
// still-then-animate path"). A talking-head model (VideoReferenceInputSupport.AnimatesStill) takes ONE picture and ONE
// speech track. The picture is a still of the avatar made from its consented pictures with a reference-capable image
// model; the speech is the run's own narration. Both are made here, in the VideoGenerate executor, never as a step of
// their own: a step would make the still an ordinary output (registered, offered, reusable), and the still is derived
// personal data that must live and die with the avatar.

/// <summary>Failure codes of the still-then-animate path; see <c>FailureClassifier</c> for who pays.</summary>
public static class AvatarStillCodes
{
    /// <summary>The step names no usable speech (no such step, no spoken line, an unreadable project file). UserInput.</summary>
    public const string AudioUnavailable = "AVATAR_AUDIO_UNAVAILABLE";

    /// <summary>The speech is shorter or longer than the model and the step allow. UserInput.</summary>
    public const string AudioLength = "AVATAR_AUDIO_LENGTH";

    /// <summary>The speech could not be read or assembled from OUR storage (refunded).</summary>
    public const string AudioUnreadable = "AVATAR_AUDIO_UNREADABLE";

    /// <summary>No fal.ai ImageEdit model is configured to make the still. UserInput (a setup to fix).</summary>
    public const string StillProviderMissing = "AVATAR_STILL_PROVIDER_MISSING";

    /// <summary>The ImageEdit model cannot make a still from several pictures (not catalogued, or a single-image route). UserInput.</summary>
    public const string StillModelUnsupported = "AVATAR_STILL_MODEL_UNSUPPORTED";

    /// <summary>The still could not be made or stored (vendor or storage failure; refunded).</summary>
    public const string StillFailed = "AVATAR_STILL_FAILED";
}

/// <summary>One speech track, ready to send: WAV bytes, its probed length and a digest of the bytes.</summary>
public sealed record AvatarSpeechTrack(byte[] Bytes, string ContentType, int DurationMs, string Sha256, int Lines);

/// <summary>The speech track, or the code and sentence that refuse the step.</summary>
public sealed record AvatarSpeechTrackOutcome(AvatarSpeechTrack? Track, string? Code = null, string? Message = null)
{
    public static AvatarSpeechTrackOutcome Refused(string code, string message) => new(null, code, message);
}

/// <summary>Reads the speech that drives an avatar's mouth (<c>AvatarAudio</c> / <c>AvatarAudioProjectFileId</c>).</summary>
public interface IAvatarSpeechTrackSource
{
    /// <summary>
    /// One WAV track: a <c>Voiceover</c> step's spoken lines joined in their order with a short pause between them, or a
    /// project audio file re-encoded. Never throws for a missing or unreadable source: it answers a refusal.
    /// </summary>
    Task<AvatarSpeechTrackOutcome> ReadAsync(StepExecutionContext context, VideoGenerateStepConfig config, CancellationToken ct);
}

/// <summary>One avatar picture a still is made from, already digest-checked against the consent.</summary>
public sealed record AvatarStillPicture(Guid ReferenceId, string ContentType, byte[] Bytes);

/// <summary>What one still is made from.</summary>
/// <param name="Fingerprint">The avatar's fingerprint now (a re-attested or changed avatar gets a new still).</param>
/// <param name="Prompt">The step's sanitized prompt, the setting and look of the still.</param>
/// <param name="Ratio">The step's ratio; the talking head's canvas follows the still.</param>
/// <param name="StillProviderId">The step's <c>stillProviderId</c> (an ImageEdit capability), or null for the default picture-edit model.</param>
public sealed record AvatarStillRequest(
    Guid ProjectId, Guid AvatarId, string Fingerprint, IReadOnlyList<AvatarStillPicture> Pictures, string Prompt, string Ratio,
    Guid? StillProviderId = null);

/// <summary>A still: its bytes, a digest, the model that made it, and whether it was made now or reused.</summary>
/// <param name="StorageKey">Where it is kept (never surfaced in a step output).</param>
public sealed record AvatarStill(byte[] Bytes, string ContentType, string Sha256, string Model, bool Reused, string StorageKey);

/// <summary>The still, or the code and sentence that refuse the step.</summary>
public sealed record AvatarStillOutcome(AvatarStill? Still, string? Code = null, string? Message = null)
{
    public static AvatarStillOutcome Refused(string code, string message) => new(null, code, message);
}

/// <summary>Makes (or finds) the still a talking-head model animates.</summary>
public interface IAvatarStillMaker
{
    Task<AvatarStillOutcome> MakeAsync(AvatarStillRequest request, CancellationToken ct);
}

/// <summary>
/// Joins a <c>Voiceover</c> step's spoken lines into one track (or re-encodes a project audio file) with first-party
/// ffmpeg argv: WAV, mono, 44.1 kHz, 16-bit, lines in their order with <see cref="PauseSeconds"/> between them. A talking
/// head says what it is given back to back, so the lines' own positions on a film's timeline are not kept.
/// </summary>
public sealed class AvatarSpeechTrackSource : IAvatarSpeechTrackSource
{
    /// <summary>The pause between two spoken lines.</summary>
    public const double PauseSeconds = 0.25;

    /// <summary>The largest single line or project file read into the join.</summary>
    public const long MaxInputBytes = 50L * 1024 * 1024;

    /// <summary>The most lines joined (a talking head speaks at most 15 s today).</summary>
    public const int MaxLines = 40;

    private const string InputFormats = "wav,mp3";

    private readonly IProjectFileWorkspace _workspace;
    private readonly IVideoToolRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly ILogger<AvatarSpeechTrackSource> _logger;

    public AvatarSpeechTrackSource(
        IProjectFileWorkspace workspace, IVideoToolRunner runner, IMediaProbe probe, ILogger<AvatarSpeechTrackSource> logger)
    {
        _workspace = workspace;
        _runner = runner;
        _probe = probe;
        _logger = logger;
    }

    public async Task<AvatarSpeechTrackOutcome> ReadAsync(StepExecutionContext context, VideoGenerateStepConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        Guid projectId = context.Execution.ProjectId;

        (IReadOnlyList<string>? keys, string? why) = config.AvatarAudioProjectFileId is { } fileId
            ? await ProjectFileKeyAsync(projectId, fileId, ct)
            : VoiceoverLineKeys(context, config.AvatarAudio, projectId);
        if (keys is null)
        {
            return AvatarSpeechTrackOutcome.Refused(AvatarStillCodes.AudioUnavailable,
                "The avatar has nothing to say: " + why + " Nothing was submitted.");
        }

        string scratch = Path.Combine(Path.GetTempPath(), "reelbolt-avatar-speech-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            var inputs = new List<string>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                string local = Path.Combine(scratch, $"in-{i}.audio");
                await _workspace.DownloadStorageKeyToFileAsync(projectId, keys[i], local, MaxInputBytes, ct);
                inputs.Add(local);
            }

            string output = Path.Combine(scratch, "speech.wav");
            VideoToolResult joined = await _runner.RunFfmpegAsync(BuildJoinArgs(inputs, output), TimeSpan.FromMinutes(2), ct);
            if (!joined.Succeeded || !File.Exists(output))
            {
                _logger.LogWarning("Joining the avatar speech track failed (exit {Exit}): {Err}", joined.ExitCode,
                    FfmpegStderrSummary.Tail(joined.StdErr));
                return AvatarSpeechTrackOutcome.Refused(AvatarStillCodes.AudioUnreadable,
                    "The speech for the avatar could not be prepared. Nothing was submitted; try the run again.");
            }

            MediaProbeResult probed = await _probe.ProbeAsync(output, ct);
            int durationMs = (int)Math.Round(probed.DurationSec * 1000, MidpointRounding.AwayFromZero);
            byte[] bytes = await File.ReadAllBytesAsync(output, ct);
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new AvatarSpeechTrackOutcome(new AvatarSpeechTrack(bytes, "audio/wav", durationMs, sha, keys.Count));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // S-67: storage and process failures name keys and hosts; the step answers one fixed sentence.
            _logger.LogWarning(ex, "Reading the avatar speech track for project {ProjectId} failed", projectId);
            return AvatarSpeechTrackOutcome.Refused(AvatarStillCodes.AudioUnreadable,
                "The speech for the avatar could not be read from storage. Nothing was submitted; try the run again.");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove the avatar speech scratch directory {Scratch}", scratch);
            }
        }
    }

    /// <summary>
    /// The join's argv: every input resampled to mono 44.1 kHz 16-bit, padded with the pause (all but the last), then
    /// concatenated. Inputs are limited to the WAV and MP3 demuxers and the file protocol.
    /// </summary>
    internal static IReadOnlyList<string> BuildJoinArgs(IReadOnlyList<string> inputs, string output)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        foreach (string input in inputs)
        {
            args.AddRange(["-protocol_whitelist", "file", "-format_whitelist", InputFormats, "-i", input]);
        }

        var graph = new StringBuilder();
        for (int i = 0; i < inputs.Count; i++)
        {
            graph.Append('[').Append(i).Append(":a:0]aresample=44100,aformat=sample_fmts=s16:channel_layouts=mono");
            if (i < inputs.Count - 1)
            {
                graph.Append(",apad=pad_dur=").Append(PauseSeconds.ToString(CultureInfo.InvariantCulture));
            }

            graph.Append("[a").Append(i).Append("];");
        }

        for (int i = 0; i < inputs.Count; i++) graph.Append("[a").Append(i).Append(']');
        graph.Append("concat=n=").Append(inputs.Count).Append(":v=0:a=1[out]");

        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[out]", "-c:a", "pcm_s16le", "-f", "wav", output]);
        return args;
    }

    /// <summary>
    /// The spoken lines of the Voiceover step the reference names (<c>From=Step</c>: that step; <c>Previous</c>: the most
    /// recent step whose output has <c>view.lines</c>), in their order: every <c>ok</c> line with a positive length and
    /// a storage key of this project.
    /// </summary>
    internal static (IReadOnlyList<string>? Keys, string? Why) VoiceoverLineKeys(
        StepExecutionContext context, ExtractInputRef? reference, Guid projectId)
    {
        if (reference is null) return (null, "the step names no speech.");

        StepOutputHistoryEntry? entry = reference.From == ExtractInputSource.Step
            ? context.StepOutputHistory.LastOrDefault(h => h.StepOrder == reference.StepOrder)
            : context.StepOutputHistory.LastOrDefault(h => HasLines(h.Output));
        if (entry is null)
        {
            return (null, reference.From == ExtractInputSource.Step
                ? $"step {reference.StepOrder} has not run in this run."
                : "no earlier step of this run is a voiceover.");
        }

        var keys = new List<string>();
        string prefix = $"projects/{projectId}/";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(entry.Output);
            if (!doc.RootElement.TryGetProperty("view", out JsonElement view)
                || !view.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array)
            {
                return (null, $"step {entry.StepOrder} is not a voiceover.");
            }

            foreach (JsonElement line in lines.EnumerateArray())
            {
                if (line.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String
                    && !string.Equals(status.GetString(), "ok", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!line.TryGetProperty("durationSec", out JsonElement dur) || dur.ValueKind != JsonValueKind.Number || dur.GetDouble() <= 0)
                {
                    continue;
                }

                if (line.TryGetProperty("storageKey", out JsonElement key) && key.ValueKind == JsonValueKind.String
                    && key.GetString() is { Length: > 0 } k && k.StartsWith(prefix, StringComparison.Ordinal)
                    && !k.Contains("..", StringComparison.Ordinal))
                {
                    keys.Add(k);
                }
            }
        }
        catch (JsonException)
        {
            return (null, $"the output of step {entry.StepOrder} could not be read.");
        }

        if (keys.Count == 0) return (null, $"step {entry.StepOrder} spoke no line.");
        if (keys.Count > MaxLines) return (null, $"step {entry.StepOrder} spoke {keys.Count} lines, more than the {MaxLines} a talking head takes.");
        return (keys, null);
    }

    private static bool HasLines(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("view", out JsonElement view) && view.ValueKind == JsonValueKind.Object
                && view.TryGetProperty("lines", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<(IReadOnlyList<string>? Keys, string? Why)> ProjectFileKeyAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(projectId, ct);
        ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == fileId);
        if (file is null || string.IsNullOrWhiteSpace(file.StorageKey))
        {
            return (null, "the audio file the step names is not in this project.");
        }

        return (file.StorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal) ? [file.StorageKey] : null,
            "the audio file the step names is outside this project.");
    }
}

/// <summary>
/// Makes the still a talking-head model animates: one fal.ai instruction edit over up to
/// <see cref="AvatarStillStorage.MaxPictures"/> of the avatar's pictures (each downscaled to
/// <see cref="MaxPictureSidePx"/>, sent as a data URI, with <c>X-Fal-Store-IO: 0</c>), kept under the avatar's stills
/// folder. Content-addressed: the same avatar (fingerprint), model, ratio and prompt reuse the stored still and buy
/// nothing. The edit is metered by the image client's own meter, inside the step's usage scope, so it settles into the
/// step's hold.
/// </summary>
public sealed class AvatarStillMaker : ISubjectStillMaker
{
    /// <summary>The longest side a picture is sent at (enough for a 1K still; less of a person's photo leaves the host).</summary>
    public const int MaxPictureSidePx = 1536;

    /// <summary>A picture that cannot be downscaled is sent as it is only below this size.</summary>
    public const long MaxUnscaledBytes = 8L * 1024 * 1024;

    /// <summary>The version of the still's recipe: a change to the instruction below must bump it, so old stills miss.</summary>
    public const string RecipeVersion = "avatar-still-v1";

    private readonly IInferenceProviderResolver _providers;
    private readonly IImageClientFactory _images;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IVideoToolRunner _runner;
    private readonly ILogger<AvatarStillMaker> _logger;

    public AvatarStillMaker(
        IInferenceProviderResolver providers,
        IImageClientFactory images,
        IProjectFileWorkspace workspace,
        IVideoToolRunner runner,
        ILogger<AvatarStillMaker> logger)
    {
        _providers = providers;
        _images = images;
        _workspace = workspace;
        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// The first-party instruction wrapped around the step's prompt. The step's words describe the setting and look; the
    /// fixed words keep the person as they are and give a talking head something it can animate.
    /// </summary>
    public static string Instruction(string prompt) =>
        "Make one photorealistic picture of the person shown in these pictures, keeping their face, hair, skin tone and " +
        "features exactly as they are. Show only this one person from the chest up, facing the camera, eyes open, mouth " +
        "closed, a relaxed neutral expression, in good even light. Setting and look: " + prompt.Trim() +
        " No text, no logos, no watermark.";

    public Task<AvatarStillOutcome> MakeAsync(AvatarStillRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Pictures.Count == 0)
        {
            return Task.FromResult(AvatarStillOutcome.Refused(
                AvatarStillCodes.StillFailed, "No picture of the avatar could be used for its still."));
        }

        // The talking head's still, recipe avatar-still-v1: its instruction, its digest and its folder are what they
        // have always been (the scene still below has its own recipe and never changes these).
        return MakeCoreAsync(
            new StillJob(
                request.ProjectId, request.AvatarId, request.StillProviderId, request.Ratio,
                [.. request.Pictures.Take(AvatarStillStorage.MaxPictures)],
                model =>
                {
                    string instruction = Instruction(request.Prompt);
                    return (instruction, Digest(request, model, instruction));
                },
                "A talking-head model needs a still of the avatar, made by a fal.ai picture-edit model, and none is set up " +
                "as the default for picture edits. Nothing was submitted."),
            ct);
    }

    /// <summary>
    /// The composed scene still of one to three subjects (recipe <see cref="SubjectSceneStill.RecipeVersion"/>): up to
    /// <see cref="SubjectSceneStill.MaxPicturesPerSubject"/> pictures of each subject and
    /// <see cref="SubjectSceneStill.MaxPictures"/> in all, taken in turns, downscaled and sent like a talking head's;
    /// kept under the person's stills folder when a person is among the subjects, else under the first object's.
    /// </summary>
    public Task<AvatarStillOutcome> MakeSceneAsync(SubjectSceneStillRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Subjects.Count == 0 || request.Subjects.Any(s => s.Pictures.Count == 0))
        {
            return Task.FromResult(AvatarStillOutcome.Refused(
                AvatarStillCodes.StillFailed, "A subject has no picture that could be used for the scene's still."));
        }

        // Exactly the pictures that are sent, subject by subject, so the instruction's positions are the array's.
        IReadOnlyList<int> share = SubjectSceneStill.SharePictures([.. request.Subjects.Select(s => s.Pictures.Count)]);
        SubjectSceneStillRequest sent = request with
        {
            Subjects = [.. request.Subjects.Select((s, i) => s with { Pictures = [.. s.Pictures.Take(share[i])] })],
        };

        return MakeCoreAsync(
            new StillJob(
                sent.ProjectId, sent.OwnerAvatarId, sent.StillProviderId, sent.Ratio,
                [.. sent.Subjects.SelectMany(s => s.Pictures)],
                model =>
                {
                    string instruction = SubjectSceneStill.Instruction(sent.Subjects, sent.Prompt);
                    return (instruction, SubjectSceneStill.Digest(sent, model, instruction));
                },
                "This model shows an avatar or an object through one composed first frame, made by a fal.ai picture-edit " +
                "model, and none is set up as the default for picture edits. Nothing was submitted.",
                RatioOnlyWhenOffered: true),
            ct);
    }

    /// <summary>One still to make: where it is kept, what it is made from, and its recipe (instruction and content address).</summary>
    private sealed record StillJob(
        Guid ProjectId,
        Guid AvatarId,
        Guid? StillProviderId,
        string Ratio,
        IReadOnlyList<AvatarStillPicture> Pictures,
        Func<string, (string Instruction, string Digest)> Recipe,
        string NoDefaultProviderMessage,
        bool RatioOnlyWhenOffered = false);

    private async Task<AvatarStillOutcome> MakeCoreAsync(StillJob request, CancellationToken ct)
    {
        // (1) Who makes it: the step's still model when it names one (an app's "Picture model"), else the default ImageEdit
        // capability; fal.ai only (data URIs and the retention header are fal's). A foreign or missing id fails closed.
        ResolvedImageProvider? provider;
        try
        {
            provider = await _providers.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, request.StillProviderId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Resolving the image-edit provider for an avatar still failed");
            provider = null;
        }

        if (provider is null || provider.Kind != InferenceProviderKind.FalAi)
        {
            return AvatarStillOutcome.Refused(AvatarStillCodes.StillProviderMissing,
                request.StillProviderId is null
                    ? request.NoDefaultProviderMessage
                    : "The picture model chosen for the avatar's still is not a fal.ai picture-edit model this workspace can use. " +
                      "Nothing was submitted.");
        }

        string model = string.IsNullOrWhiteSpace(provider.ModelName)
            ? FalImageCatalog.Instance.DefaultFor(FalImageJob.Instruct).Id
            : provider.ModelName.Trim();
        FalImageEndpoint? endpoint = FalImageCatalog.Instance.Find(model);
        if (endpoint is null || endpoint.Job != FalImageJob.Instruct || endpoint.ImageUrlsField is null)
        {
            return AvatarStillOutcome.Refused(AvatarStillCodes.StillModelUnsupported,
                $"'{model}' cannot make a still from several pictures of the avatar (an instruction edit that takes an image " +
                "array is needed, for example google/nano-banana-2.1/edit). Nothing was submitted.");
        }

        (string instruction, string digest) = request.Recipe(model);
        string relative = AvatarStillStorage.RelativePath(request.AvatarId, digest, "img");
        string storageKey = AvatarStillStorage.ObjectPrefix(request.ProjectId, request.AvatarId) + digest + ".img";

        string scratch = Path.Combine(Path.GetTempPath(), "reelbolt-avatar-still-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);

            // (2) Reuse: the same avatar, recipe, model, ratio and prompt already made this still.
            if (await _workspace.ArtifactExistsAsync(request.ProjectId, storageKey, ct))
            {
                string local = Path.Combine(scratch, "reused.img");
                await _workspace.DownloadStorageKeyToFileAsync(request.ProjectId, storageKey, local, ProjectAvatarLimits.MaxImageBytes, ct);
                byte[] stored = await File.ReadAllBytesAsync(local, ct);
                if (ImageDimensions.ContentTypeOf(stored) is { } storedType)
                {
                    return new AvatarStillOutcome(new AvatarStill(stored, storedType, Sha(stored), model, Reused: true, storageKey));
                }

                _logger.LogWarning("A stored avatar still is not a PNG or JPEG; it is made again");
            }

            // (3) The pictures, downscaled, as data URIs.
            var uris = new List<string>(request.Pictures.Count);
            for (int i = 0; i < request.Pictures.Count; i++)
            {
                (string type, byte[] bytes)? sent = await DownscaleAsync(request.Pictures[i], scratch, i, ct);
                if (sent is null)
                {
                    return AvatarStillOutcome.Refused(AvatarStillCodes.StillFailed,
                        "A picture of the avatar could not be prepared for its still. Nothing was submitted; try the run again.");
                }

                uris.Add("data:" + sent.Value.type + ";base64," + Convert.ToBase64String(sent.Value.bytes));
            }

            // (4) The edit. The image client meters it inside the step's usage scope.
            IImageClient client = _images.Get(provider);
            GeneratedImageSet set = await client.EditAsync(
                new ImageEditRequest(model, instruction, uris[0],
                    AspectRatio: AspectFor(endpoint, request),
                    ReferenceImageUrls: uris.Count > 1 ? uris.Skip(1).ToList() : null)
                { NoVendorRetention = true },
                ct);

            GeneratedImage? image = set.Images.FirstOrDefault();
            string? contentType = image is null ? null : ImageDimensions.ContentTypeOf(image.Bytes);
            if (image is null || contentType is null)
            {
                return AvatarStillOutcome.Refused(AvatarStillCodes.StillFailed,
                    "The picture-edit model returned no usable still of the avatar. Nothing else was submitted.");
            }

            // (5) Kept under the avatar's stills folder: no project file, no output, deleted with the avatar.
            string file = Path.Combine(scratch, "still.img");
            await File.WriteAllBytesAsync(file, image.Bytes, ct);
            await _workspace.UploadArtifactAsync(request.ProjectId, file, relative, contentType, ct, AvatarStillStorage.Category);
            return new AvatarStillOutcome(new AvatarStill(image.Bytes, contentType, Sha(image.Bytes), set.ResolvedModel ?? model, Reused: false, storageKey));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // S-13/S-67: the vendor's or the store's own words stay in the log; the step answers one sentence.
            _logger.LogWarning(ex, "Making the still of avatar {AvatarId} failed", request.AvatarId);
            return AvatarStillOutcome.Refused(AvatarStillCodes.StillFailed,
                "The still of the avatar could not be made. No video was submitted; try the run again.");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove the avatar still scratch directory {Scratch}", scratch);
            }
        }
    }

    /// <summary>
    /// The aspect ratio sent with the edit. A talking head's still sends the step's ratio whenever the endpoint has an
    /// aspect field, as it always has. A scene still sends it only when the endpoint lists it: a video model whose
    /// canvas follows its first frame is configured with a ratio word of its own (for example <c>native</c>), which no
    /// picture model knows, and the still then keeps the picture model's own shape.
    /// </summary>
    private static string? AspectFor(FalImageEndpoint endpoint, StillJob request)
    {
        if (endpoint.AspectRatioField is null)
            return null;
        if (!request.RatioOnlyWhenOffered)
            return request.Ratio;
        return endpoint.FieldNamed(endpoint.AspectRatioField)?.Values is { } offered
               && !offered.Contains(request.Ratio, StringComparer.Ordinal)
            ? null
            : request.Ratio;
    }

    /// <summary>The still's content address: the recipe, the avatar as it is now, the model, ratio, instruction and pictures.</summary>
    internal static string Digest(AvatarStillRequest request, string model, string instruction)
    {
        var sb = new StringBuilder();
        foreach (string part in new[] { RecipeVersion, request.Fingerprint, model, request.Ratio, instruction }
                     .Concat(request.Pictures.Select(p => p.ReferenceId.ToString("N"))))
        {
            sb.Append(part.Length).Append(':').Append(part).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>A JPEG copy whose longest side is at most <see cref="MaxPictureSidePx"/>; the original when that fails and it is small.</summary>
    private async Task<(string Type, byte[] Bytes)?> DownscaleAsync(AvatarStillPicture picture, string scratch, int index, CancellationToken ct)
    {
        string input = Path.Combine(scratch, $"pic-{index}.src");
        string output = Path.Combine(scratch, $"pic-{index}.jpg");
        await File.WriteAllBytesAsync(input, picture.Bytes, ct);
        string side = MaxPictureSidePx.ToString(CultureInfo.InvariantCulture);
        string[] args =
        [
            "-hide_banner", "-nostdin", "-y",
            "-protocol_whitelist", "file", "-format_whitelist", "image2,png_pipe,jpeg_pipe,webp_pipe",
            "-i", input,
            "-vf", $"scale=w='min({side},iw)':h='min({side},ih)':force_original_aspect_ratio=decrease",
            "-frames:v", "1", "-c:v", "mjpeg", "-q:v", "3", "-f", "image2", output
        ];

        VideoToolResult result = await _runner.RunFfmpegAsync(args, TimeSpan.FromSeconds(60), ct);
        if (result.Succeeded && File.Exists(output))
        {
            byte[] scaled = await File.ReadAllBytesAsync(output, ct);
            if (ImageDimensions.ContentTypeOf(scaled) is { } type) return (type, scaled);
        }

        _logger.LogWarning("Downscaling an avatar picture failed (exit {Exit}); sending it as it is only if small", result.ExitCode);
        return picture.Bytes.LongLength <= MaxUnscaledBytes ? (picture.ContentType, picture.Bytes) : null;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
