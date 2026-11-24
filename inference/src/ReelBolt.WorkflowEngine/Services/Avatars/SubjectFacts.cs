using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Avatars;

// Several subjects per clip (docs/design/avatar-objects.md "Subjects in a video generation step"): the identity of a
// SET of subjects, what a result records about them, and the one reader of it. The single legacy avatar keeps
// AvatarFingerprint and AvatarOutputFacts exactly as they are; nothing here changes a value they produce.

/// <summary>
/// The identity of the subjects a clip shows, AS THEY ARE NOW and in the step's order: one hash over each subject's own
/// <see cref="AvatarFingerprint"/>. It joins the ledger request hash, the step-cache key and every reuse check of a
/// step that names <c>Subjects</c>, so a clip is reused only while every subject it shows is unchanged and the set and
/// its order are the same. Never used for a legacy <c>AvatarId</c> step, whose values stay the avatar's own.
/// </summary>
public static class SubjectsFingerprint
{
    /// <summary>Hex SHA-256 over the length-prefixed fingerprints, in order.</summary>
    public static string Compute(IEnumerable<string> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        var sb = new StringBuilder("subjects-v1\n");
        foreach (string fingerprint in fingerprints)
        {
            sb.Append("subject=").Append(fingerprint.Length).Append(':').Append(fingerprint).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>The same over resolved avatars.</summary>
    public static string Compute(IEnumerable<ResolvedAvatar> subjects) =>
        Compute(subjects.Select(AvatarFingerprint.Compute));
}

/// <summary>One subject as a <c>VideoGenerate</c> result recorded it.</summary>
public readonly record struct RecordedSubject(Guid AvatarId, string Fingerprint);

/// <summary>
/// The subject facts a <c>VideoGenerate</c> result records for a step that names <c>Subjects</c>
/// (<c>meta.subjects[]</c> and <c>meta.subjectPath</c> in the output and the manifest, <c>subjectsFingerprint</c> on
/// each clip), and the one reader of them, so the writer and every reuse check agree.
/// </summary>
public static class SubjectOutputFacts
{
    public const string MetaKey = "subjects";
    public const string PathKey = "subjectPath";
    public const string ClipFingerprintKey = "subjectsFingerprint";

    /// <summary>The <c>meta.subjectPath</c> word of each path.</summary>
    public static string PathWord(ReelBolt.Shared.Inference.VideoSubjectPath path) => path switch
    {
        ReelBolt.Shared.Inference.VideoSubjectPath.TalkingHead => "talkingHead",
        ReelBolt.Shared.Inference.VideoSubjectPath.References => "references",
        _ => "firstFrame",
    };

    /// <summary>The <c>kind</c> word of a subject.</summary>
    public static string KindWord(AvatarSubjectKind kind) => kind == AvatarSubjectKind.Object ? "object" : "person";

    /// <summary>
    /// The subjects a recorded VideoGenerate output was made with, in order; null when it records none.
    /// </summary>
    /// <returns>(WellFormed, Subjects): a non-blank output that is not a JSON object, or an entry without an id or a fingerprint, is not well formed.</returns>
    public static (bool WellFormed, IReadOnlyList<RecordedSubject>? Subjects) Read(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return (true, null);
        try
        {
            if (JsonNode.Parse(outputJson) is not JsonObject root)
                return (false, null);
            if (root["meta"] is not JsonObject meta || meta[MetaKey] is not { } node)
                return (true, null);
            if (node is not JsonArray array || array.Count == 0)
                return (false, null);

            var subjects = new List<RecordedSubject>(array.Count);
            foreach (JsonNode? entry in array)
            {
                if (entry is not JsonObject subject
                    || !Guid.TryParse(subject["avatarId"]?.GetValue<string>(), out Guid id)
                    || subject["fingerprint"]?.GetValue<string>() is not { Length: > 0 } fingerprint)
                {
                    return (false, null);
                }

                subjects.Add(new RecordedSubject(id, fingerprint));
            }

            return (true, subjects);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return (false, null);
        }
    }
}

/// <summary>
/// The avatar component of a <c>VideoGenerate</c> step's cache key (<c>StepCacheKeyInputs.AvatarConsent</c>): what the
/// step's subjects are NOW, so revoking, expiring, re-attesting or changing any of them stops a cached clip from being
/// replayed (the reuse guard re-checks a hit as well).
/// </summary>
public static class SubjectsCacheComponent
{
    /// <summary>
    /// Null for a step that shows nobody and nothing. A legacy <c>AvatarId</c> step: that avatar's fingerprint, or
    /// <c>unusable:{reason}</c>, exactly as before several subjects existed. A step that names <c>Subjects</c>:
    /// <c>subjects:</c> then each subject's fingerprint or <c>unusable:{reason}</c>, in order, separated by commas.
    /// </summary>
    public static async Task<string?> ComputeAsync(IAvatarResolver resolver, Guid projectId, VideoGenerateStepConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(config);

        if (!config.NamesSubjects)
        {
            if (config.AvatarId is not Guid avatarId)
                return null;

            ResolvedAvatar avatar = await resolver.ResolveAsync(projectId, avatarId, ct);
            return avatar.Usable ? AvatarFingerprint.Compute(avatar) : "unusable:" + avatar.Reason;
        }

        var parts = new List<string>(config.Subjects!.Count);
        foreach (VideoGenerateSubject subject in config.Subjects)
        {
            ResolvedAvatar avatar = await resolver.ResolveAsync(projectId, subject.AvatarId, ct);
            parts.Add(avatar.Usable ? AvatarFingerprint.Compute(avatar) : "unusable:" + avatar.Reason);
        }

        return "subjects:" + string.Join(",", parts);
    }
}

/// <summary>
/// The id a subject is offered to a planner under (docs/design/avatar-objects.md "Avatars and objects in planned
/// shots"): <c>a</c> plus the first 8 hex digits of the avatar id. Stable, never reused, needs no stored map.
/// </summary>
public static class SubjectOfferId
{
    /// <summary>The offer id of <paramref name="avatarId"/>.</summary>
    public static string For(Guid avatarId) => "a" + avatarId.ToString("N")[..8];

    /// <summary>
    /// The one subject <paramref name="offerId"/> names among <paramref name="usableSubjects"/>, or null when it names
    /// none or more than one (an ambiguous id is never guessed).
    /// </summary>
    public static ResolvedAvatar? Resolve(string? offerId, IReadOnlyList<ResolvedAvatar> usableSubjects)
    {
        ArgumentNullException.ThrowIfNull(usableSubjects);
        string id = offerId?.Trim() ?? string.Empty;
        if (id.Length == 0)
            return null;

        ResolvedAvatar? found = null;
        foreach (ResolvedAvatar subject in usableSubjects)
        {
            if (!string.Equals(For(subject.Id), id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found is not null)
                return null;
            found = subject;
        }

        return found;
    }
}
