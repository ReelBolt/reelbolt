using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// One reference medium of a person (an avatar's picture, short video or speech recording) carried on a
/// <see cref="VideoGenerationRequest"/> (docs/design/avatars.md "Engine and generation", decision AV-11).
/// <para>
/// A reference is NOT a frame: it is subject material the model keeps consistent across the clip. The
/// position in <see cref="VideoGenerationRequest.References"/> is the user's order (the avatar's ordinals); each
/// client translates <see cref="Kind"/> into its own field (MiniMax: <c>reference_image</c>,
/// <c>reference_video</c>, <c>reference_audio</c>). <see cref="Role"/> is a hint for the mapping and is never sent
/// as free text.
/// </para>
/// </summary>
/// <param name="Kind">Picture, video or audio.</param>
/// <param name="Role">The avatar's role hint (face, half body, motion, speech ...).</param>
/// <param name="ContentType">The media type of <paramref name="Bytes"/>, lower case (e.g. <c>image/png</c>).</param>
/// <param name="Bytes">The medium itself; our storage is the master copy, the vendor receives a per-request copy.</param>
/// <param name="DurationMs">The probed length of a video or audio reference; null for a picture or when unknown.</param>
public sealed record VideoGenerationReference(
    AvatarReferenceKind Kind,
    AvatarReferenceRole Role,
    string ContentType,
    byte[] Bytes,
    int? DurationMs = null);

/// <summary>
/// What ONE video model accepts as person reference material, declared per model like
/// <see cref="VideoImageInputSupport"/> (docs/design/avatars.md, AV-11). Pure data: a client answers it from its
/// own model switch, offline.
/// </summary>
/// <param name="MaxImages">Most reference pictures in one request (0 = the model takes none).</param>
/// <param name="MaxVideos">Most reference videos in one request.</param>
/// <param name="MaxAudio">Most reference audio files in one request.</param>
/// <param name="MaxFiles">Most reference files of every kind together.</param>
/// <param name="MaxVideoTotalMs">Longest total length of the reference videos together.</param>
/// <param name="MaxAudioTotalMs">Longest total length of the reference audio together.</param>
/// <param name="AudioDrivesSpeech">
/// True only when the model is DOCUMENTED (or measured) to make the person say the words of a driving audio track,
/// rather than only borrowing a voice and rhythm. A step's <c>AvatarAudio</c> (the speech that drives the mouth) is
/// refused on a model that does not declare it.
/// </param>
/// <param name="ExclusiveWithFrames">True when reference mode cannot be combined with a first or last frame.</param>
public sealed record VideoReferenceInputSupport(
    int MaxImages,
    int MaxVideos,
    int MaxAudio,
    int MaxFiles,
    int MaxVideoTotalMs,
    int MaxAudioTotalMs,
    bool AudioDrivesSpeech,
    bool ExclusiveWithFrames)
{
    /// <summary>A model that takes no reference material at all.</summary>
    public static VideoReferenceInputSupport None { get; } = new(0, 0, 0, 0, 0, 0, false, false);

    /// <summary>
    /// Phase 3, the still-then-animate path (docs/design/avatars.md "Phased delivery"): the model takes NO reference
    /// pictures of its own; it animates ONE picture (sent as the first frame) driven by an audio track. The avatar is
    /// shown through a still the engine makes from the avatar's pictures with a reference-capable image model, and the
    /// step must name the speech (<c>AvatarAudio</c>), so such a model also declares <see cref="AudioDrivesSpeech"/>
    /// and <see cref="MaxAudio"/> = 1. Default false keeps every phase 2 declaration unchanged. Additive.
    /// </summary>
    public bool AnimatesStill { get; init; }

    /// <summary>An <see cref="AnimatesStill"/> model also takes a short motion prompt (Kling's avatar route); false = none is sent.</summary>
    public bool TakesPrompt { get; init; }

    /// <summary>The shortest driving audio the model takes, in milliseconds (0 = not stated).</summary>
    public int MinAudioMs { get; init; }

    /// <summary>
    /// True when the model can show an avatar: it takes at least one reference picture (an avatar always has one), or
    /// it animates a still made from those pictures.
    /// </summary>
    public bool AcceptsAvatar => (MaxImages > 0 && MaxFiles > 0) || AnimatesStill;

    /// <summary>The most references of <paramref name="kind"/> one request may carry.</summary>
    public int MaxOf(AvatarReferenceKind kind) => kind switch
    {
        AvatarReferenceKind.Image => MaxImages,
        AvatarReferenceKind.Video => MaxVideos,
        AvatarReferenceKind.Audio => MaxAudio,
        _ => 0,
    };
}

/// <summary>The size of one reference as pricing sees it: its kind and, for video or audio, its length.</summary>
public readonly record struct VideoReferenceMaterial(AvatarReferenceKind Kind, int? DurationMs);

/// <summary>
/// A video generation client that can say, offline, what reference material each of ITS models accepts and what
/// that material adds to the vendor's bill. Not a member of the frozen <see cref="IVideoGenerationClient"/> (the
/// reasoning <see cref="IVideoImageInputSupport"/> records): every existing fake keeps compiling, and a client that
/// does not implement it declares <see cref="VideoReferenceInputSupport.None"/>.
/// </summary>
public interface IVideoReferenceInputSupport
{
    /// <summary>What <paramref name="model"/> accepts as reference material. Pure: no network call, ever.</summary>
    /// <exception cref="NotSupportedException">The client has no such model.</exception>
    VideoReferenceInputSupport ReferenceInputSupport(string model);

    /// <summary>
    /// The vendor's INPUT-MATERIAL cost in USD for one take that carries <paramref name="references"/> at
    /// <paramref name="resolution"/>, on top of the per-output-second price <see cref="IVideoGenerationClient.Describe"/>
    /// publishes (MiniMax bills extra pictures and input video, docs/design/avatars.md "Vendor facts" M9). Pure.
    /// Never negative; <c>0</c> when the material is free.
    /// </summary>
    /// <exception cref="NotSupportedException">The model or resolution is not priced.</exception>
    decimal InputMaterialCostUsd(string model, string resolution, IReadOnlyList<VideoReferenceMaterial> references);
}

/// <summary>Which references a request carries after the per-model selection, and which were left out and why.</summary>
/// <param name="Chosen">The references to send, in the caller's order within each kind.</param>
/// <param name="Skipped">One entry per reference left out: its index in the candidate list and a reason word.</param>
public sealed record VideoReferenceSelection(
    IReadOnlyList<int> Chosen,
    IReadOnlyList<(int Index, string Reason)> Skipped);

/// <summary>
/// The ONE place reference material is checked against a model's declaration, so every vendor refuses the same
/// request for the same reason before anything is reserved (the <see cref="VideoImageInput"/> pattern).
/// </summary>
public static class VideoReferenceInput
{
    /// <summary>Failure code of a step whose avatar the resolved model cannot carry (docs/design/avatars.md).</summary>
    public const string NotSupportedCode = "AVATAR_NOT_SUPPORTED_BY_MODEL";

    /// <summary>Reason word: more references of this kind than the model takes.</summary>
    public const string SkipKindLimit = "kind_limit";

    /// <summary>Reason word: the model's total length for this kind would be exceeded.</summary>
    public const string SkipTotalLength = "total_length";

    /// <summary>Reason word: the model's file count would be exceeded.</summary>
    public const string SkipFileLimit = "file_limit";

    /// <summary>Reason word: the step did not ask for this role.</summary>
    public const string SkipRoleNotRequested = "role_not_requested";

    /// <summary>Reason word: the request body would exceed the vendor's documented ceiling.</summary>
    public const string SkipBodySize = "body_size";

    /// <summary>Reason word: a video or audio reference whose length is unknown cannot be checked against a total.</summary>
    public const string SkipUnknownLength = "unknown_length";

    /// <summary>
    /// What <paramref name="model"/> accepts according to the client, or <see cref="VideoReferenceInputSupport.None"/>
    /// when the client declares nothing (fail closed: no support is invented for an unknown client).
    /// </summary>
    public static VideoReferenceInputSupport DeclaredReferenceInputSupport(this IVideoGenerationClient client, string model)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client is IVideoReferenceInputSupport declared
            ? declared.ReferenceInputSupport(model)
            : VideoReferenceInputSupport.None;
    }

    /// <summary>
    /// The input-material cost the client declares for these references, or <c>0</c> for a client that declares no
    /// reference support (such a client is refused before it could be asked to carry any).
    /// </summary>
    public static decimal DeclaredInputMaterialCostUsd(
        this IVideoGenerationClient client, string model, string resolution, IReadOnlyList<VideoReferenceMaterial> references)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client is IVideoReferenceInputSupport declared && references.Count > 0
            ? declared.InputMaterialCostUsd(model, resolution, references)
            : 0m;
    }

    /// <summary>
    /// Refuses, with a sentence that names the model, a step the model cannot carry at all: no reference support,
    /// a first/last frame alongside reference mode on a model that forbids it, or a driving audio track on a model
    /// that does not declare that audio drives speech. Returns null when the step can be carried.
    /// </summary>
    public static string? Refusal(VideoReferenceInputSupport support, string model, bool hasFrames, bool wantsDrivingAudio)
    {
        ArgumentNullException.ThrowIfNull(support);
        string named = string.IsNullOrWhiteSpace(model) ? "(no model)" : $"'{model}'";

        if (!support.AcceptsAvatar)
        {
            return $"{named} takes no reference pictures, so it cannot show an avatar or an object from them. Choose a " +
                "model that accepts reference material (for example MiniMax-H3). Nothing was submitted.";
        }

        if (support.AnimatesStill)
        {
            // The still made from the avatar's pictures IS the frame this model animates, so a frame of the step's own
            // has no slot; and a talking head with nothing to say is no clip at all.
            if (hasFrames)
            {
                return $"{named} animates one still of the avatar, made from its pictures, so the step cannot also give " +
                    "it a first or last frame. Remove the frame. Nothing was submitted.";
            }

            if (!wantsDrivingAudio)
            {
                return $"{named} makes the avatar say the words of a speech track, and this step names none. Set the " +
                    "step's avatar audio (a Voiceover step of this run, or a project audio file). Nothing was submitted.";
            }

            return null;
        }

        if (hasFrames && support.ExclusiveWithFrames)
        {
            return $"{named} cannot combine an avatar's reference material with a first or last frame (the vendor " +
                "documents the two modes as mutually exclusive). Remove the frame or the avatar. Nothing was submitted.";
        }

        if (wantsDrivingAudio && !support.AudioDrivesSpeech)
        {
            return $"{named} is not known to make a person say the words of a driving audio track (it may only borrow " +
                "the voice), so the step's avatar audio cannot be used with it. Nothing was submitted.";
        }

        return null;
    }

    /// <summary>
    /// Picks the references one request carries, in candidate order, inside the model's declared limits and the
    /// vendor's body ceiling. Pictures are taken first (an avatar needs one), then audio (small), then video
    /// (large). Every left-out candidate is reported with a reason word, so the manifest can say what was not sent.
    /// </summary>
    /// <param name="support">The model's declaration.</param>
    /// <param name="candidates">Every reference the avatar offers, in the avatar's order.</param>
    /// <param name="maxBodyBytes">The whole-body ceiling; base64 inflates a medium by about 4/3.</param>
    /// <param name="reservedBodyBytes">Body bytes already used by the prompt and the other members.</param>
    /// <param name="sizeOf">
    /// The raw size of candidate <c>i</c> when its bytes are not loaded yet (the engine selects from the stored sizes,
    /// then reads only what was chosen); null measures <see cref="VideoGenerationReference.Bytes"/>.
    /// </param>
    public static VideoReferenceSelection Select(
        VideoReferenceInputSupport support,
        IReadOnlyList<VideoGenerationReference> candidates,
        long maxBodyBytes,
        long reservedBodyBytes = 64 * 1024,
        Func<int, long>? sizeOf = null)
    {
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(candidates);

        var chosen = new List<int>();
        var skipped = new List<(int, string)>();
        var counts = new Dictionary<AvatarReferenceKind, int>();
        int videoMs = 0, audioMs = 0;
        long body = reservedBodyBytes;

        foreach (AvatarReferenceKind kind in new[] { AvatarReferenceKind.Image, AvatarReferenceKind.Audio, AvatarReferenceKind.Video })
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                VideoGenerationReference r = candidates[i];
                if (r.Kind != kind)
                {
                    continue;
                }

                int count = counts.GetValueOrDefault(kind);
                if (count >= support.MaxOf(kind))
                {
                    skipped.Add((i, SkipKindLimit));
                    continue;
                }

                if (chosen.Count >= support.MaxFiles)
                {
                    skipped.Add((i, SkipFileLimit));
                    continue;
                }

                if (kind is AvatarReferenceKind.Video or AvatarReferenceKind.Audio)
                {
                    if (r.DurationMs is not { } ms || ms <= 0)
                    {
                        skipped.Add((i, SkipUnknownLength));
                        continue;
                    }

                    int total = kind == AvatarReferenceKind.Video ? videoMs : audioMs;
                    int cap = kind == AvatarReferenceKind.Video ? support.MaxVideoTotalMs : support.MaxAudioTotalMs;
                    if (total + ms > cap)
                    {
                        skipped.Add((i, SkipTotalLength));
                        continue;
                    }
                }

                long encoded = Base64Length(sizeOf?.Invoke(i) ?? r.Bytes.LongLength) + 128;
                if (body + encoded > maxBodyBytes)
                {
                    skipped.Add((i, SkipBodySize));
                    continue;
                }

                body += encoded;
                counts[kind] = count + 1;
                if (kind == AvatarReferenceKind.Video)
                {
                    videoMs += r.DurationMs!.Value;
                }
                else if (kind == AvatarReferenceKind.Audio)
                {
                    audioMs += r.DurationMs!.Value;
                }

                chosen.Add(i);
            }
        }

        return new VideoReferenceSelection(chosen, skipped.OrderBy(s => s.Item1).ToList());
    }

    /// <summary>
    /// Shares ONE request's reference slots between several subjects fairly
    /// (docs/design/avatar-objects.md "Subjects in a video generation step"): within each kind the subjects take turns,
    /// so every subject gets one picture before any subject gets a second, inside the model's counts, its total lengths
    /// and the vendor's body ceiling. Kinds are filled in the order of <see cref="Select"/> (pictures, then audio, then
    /// video); a kind a subject has none of simply passes its turn. With ONE subject the answer is exactly
    /// <see cref="Select"/>'s.
    /// </summary>
    /// <param name="support">The model's declaration.</param>
    /// <param name="subjects">Each subject's candidates, in the subject's own order; the subjects in the step's order.</param>
    /// <param name="maxBodyBytes">The whole-body ceiling; base64 inflates a medium by about 4/3.</param>
    /// <param name="reservedBodyBytes">Body bytes already used by the prompt and the other members.</param>
    /// <param name="sizeOf">The raw size of candidate <c>i</c> of subject <c>s</c> when its bytes are not loaded yet.</param>
    /// <returns>One selection per subject, in the subjects' order; each index is into that subject's own list.</returns>
    public static IReadOnlyList<VideoReferenceSelection> SelectShared(
        VideoReferenceInputSupport support,
        IReadOnlyList<IReadOnlyList<VideoGenerationReference>> subjects,
        long maxBodyBytes,
        long reservedBodyBytes = 64 * 1024,
        Func<int, int, long>? sizeOf = null)
    {
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(subjects);

        var chosen = subjects.Select(_ => new List<int>()).ToList();
        var skipped = subjects.Select(_ => new List<(int, string)>()).ToList();
        var counts = new Dictionary<AvatarReferenceKind, int>();
        int videoMs = 0, audioMs = 0, files = 0;
        long body = reservedBodyBytes;

        foreach (AvatarReferenceKind kind in new[] { AvatarReferenceKind.Image, AvatarReferenceKind.Audio, AvatarReferenceKind.Video })
        {
            // Each subject's candidates of this kind, in its own order; a turn takes the next one of each subject.
            List<List<int>> queues = [.. subjects.Select(list => Enumerable.Range(0, list.Count).Where(i => list[i].Kind == kind).ToList())];
            int rounds = queues.Count == 0 ? 0 : queues.Max(q => q.Count);
            for (int round = 0; round < rounds; round++)
            {
                for (int s = 0; s < subjects.Count; s++)
                {
                    if (round >= queues[s].Count)
                    {
                        continue;
                    }

                    int i = queues[s][round];
                    VideoGenerationReference r = subjects[s][i];
                    int count = counts.GetValueOrDefault(kind);
                    if (count >= support.MaxOf(kind))
                    {
                        skipped[s].Add((i, SkipKindLimit));
                        continue;
                    }

                    if (files >= support.MaxFiles)
                    {
                        skipped[s].Add((i, SkipFileLimit));
                        continue;
                    }

                    if (kind is AvatarReferenceKind.Video or AvatarReferenceKind.Audio)
                    {
                        if (r.DurationMs is not { } ms || ms <= 0)
                        {
                            skipped[s].Add((i, SkipUnknownLength));
                            continue;
                        }

                        int total = kind == AvatarReferenceKind.Video ? videoMs : audioMs;
                        int cap = kind == AvatarReferenceKind.Video ? support.MaxVideoTotalMs : support.MaxAudioTotalMs;
                        if (total + ms > cap)
                        {
                            skipped[s].Add((i, SkipTotalLength));
                            continue;
                        }
                    }

                    long encoded = Base64Length(sizeOf?.Invoke(s, i) ?? r.Bytes.LongLength) + 128;
                    if (body + encoded > maxBodyBytes)
                    {
                        skipped[s].Add((i, SkipBodySize));
                        continue;
                    }

                    body += encoded;
                    counts[kind] = count + 1;
                    files++;
                    if (kind == AvatarReferenceKind.Video)
                    {
                        videoMs += r.DurationMs!.Value;
                    }
                    else if (kind == AvatarReferenceKind.Audio)
                    {
                        audioMs += r.DurationMs!.Value;
                    }

                    chosen[s].Add(i);
                }
            }
        }

        return [.. subjects.Select((_, s) => new VideoReferenceSelection(chosen[s], skipped[s].OrderBy(x => x.Item1).ToList()))];
    }

    /// <summary>
    /// Throws for a request that carries reference material, from a client that declares no reference mode.
    /// </summary>
    /// <exception cref="NotSupportedException">The request carries references.</exception>
    public static void EnsureNoReferences(VideoGenerationRequest request, string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.References is { Count: > 0 } references)
        {
            throw new NotSupportedException(
                $"'{request.Model}' takes no reference material of an avatar or an object; this request carries {references.Count} " +
                $"reference item(s) ({parameterName ?? "request"}). Nothing was submitted.");
        }
    }

    /// <summary>The length of a base64 encoding of <paramref name="bytes"/> raw bytes.</summary>
    public static long Base64Length(long bytes) => (bytes + 2) / 3 * 4;
}
