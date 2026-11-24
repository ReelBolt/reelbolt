using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Avatars;

/// <summary>
/// Re-checks an avatar when a <c>VideoGenerate</c> result is REUSED instead of produced (step-result cache hit, resume
/// reuse, composition replay or change-request copy; docs/design/avatars.md "Step cache and reuse": reusing an output
/// is new use). The executor's own checks run only on a live step, so without this a clip of a person whose consent
/// was withdrawn, whose avatar expired, or whose references were changed (re-attested) could be replayed from a
/// recorded result. A blocked answer means "treat as a MISS": the step then runs live and the executor fails it with
/// its usual <c>AVATAR_*</c> code. The voice twin is <c>IVoiceReuseGuard</c>.
/// </summary>
public interface IAvatarReuseGuard
{
    /// <summary>
    /// True when the recorded <paramref name="outputJson"/> used no avatar and the step names none, or when the avatar
    /// it was made with is still usable in <paramref name="projectId"/> AND unchanged (same fingerprint). A step that
    /// names several subjects (docs/design/avatar-objects.md) reuses only a result made with exactly those subjects, in
    /// that order, each still usable and unchanged. Never throws except for cancellation: an error is a block.
    /// </summary>
    Task<bool> IsReuseAllowedAsync(Guid projectId, string? videoGenerateConfigJson, string? outputJson, CancellationToken ct);
}

public sealed class AvatarReuseGuard : IAvatarReuseGuard
{
    private readonly IAvatarResolver _resolver;
    private readonly ILogger<AvatarReuseGuard>? _logger;

    public AvatarReuseGuard(IAvatarResolver resolver, ILogger<AvatarReuseGuard>? logger = null)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<bool> IsReuseAllowedAsync(Guid projectId, string? videoGenerateConfigJson, string? outputJson, CancellationToken ct)
    {
        try
        {
            Guid? configured = null;
            IReadOnlyList<VideoGenerateSubject>? subjects = null;
            VideoGenerateStepConfig? parsed = null;
            if (!string.IsNullOrWhiteSpace(videoGenerateConfigJson))
            {
                // Read exactly as the executor reads it; a config it could not parse is not reusable.
                VideoGenerateStepConfig config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(
                    videoGenerateConfigJson, VideoGenerateStepConfig.JsonOptions)
                    ?? throw new JsonException("VideoGenerateConfigJson deserialized to null.");
                configured = config.AvatarId;
                subjects = config.NamesSubjects ? config.Subjects : null;
                parsed = config;
            }

            // Several subjects per clip: the result must record exactly the subjects the step names now, in order,
            // and each must still be usable and unchanged. A result that records subjects the step no longer names is
            // not reused either (the live step decides).
            (bool subjectsWellFormed, IReadOnlyList<RecordedSubject>? recordedSubjects) = SubjectOutputFacts.Read(outputJson);
            if (!subjectsWellFormed)
                return false;

            // Planned subjects (docs/design/avatar-objects.md "Avatars and objects in planned shots"): a plan-sourced
            // step records every subject its clips show. Reused only while the step still shows planned subjects (and
            // allows each of them) and every one is still usable and unchanged.
            if (parsed is not null && parsed.PlanSource != VideoGeneratePlanSource.Inline && recordedSubjects is not null)
            {
                if (!PlannedSubjects.Allows(parsed))
                    return false;
                foreach (RecordedSubject planned in recordedSubjects)
                {
                    if (parsed.PlannedSubjectIds is { } allowed && !allowed.Contains(planned.AvatarId))
                        return false;
                    ResolvedAvatar subject = await _resolver.ResolveAsync(projectId, planned.AvatarId, ct);
                    if (!subject.Usable
                        || !string.Equals(AvatarFingerprint.Compute(subject), planned.Fingerprint, StringComparison.Ordinal))
                        return false;
                }

                return true;
            }
            if (subjects is not null)
            {
                if (recordedSubjects is null || recordedSubjects.Count != subjects.Count)
                    return false;
                for (int i = 0; i < subjects.Count; i++)
                {
                    if (recordedSubjects[i].AvatarId != subjects[i].AvatarId)
                        return false;
                    ResolvedAvatar subject = await _resolver.ResolveAsync(projectId, subjects[i].AvatarId, ct);
                    if (!subject.Usable
                        || !string.Equals(AvatarFingerprint.Compute(subject), recordedSubjects[i].Fingerprint, StringComparison.Ordinal))
                        return false;
                }

                return true;
            }

            if (recordedSubjects is not null)
                return false;

            (bool wellFormed, Guid? recorded, string? fingerprint) = AvatarOutputFacts.Read(outputJson);
            if (!wellFormed)
                return false;
            if (configured is null && recorded is null)
                return true;

            // A step that names an avatar reuses only a result made with THAT avatar; a result that records an
            // avatar the step no longer names is not reused either (the live step decides).
            if (configured != recorded || recorded is null)
                return false;

            ResolvedAvatar avatar = await _resolver.ResolveAsync(projectId, recorded.Value, ct);
            return avatar.Usable && string.Equals(AvatarFingerprint.Compute(avatar), fingerprint, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Avatar re-check failed for project {ProjectId}; treating the reuse as a miss", projectId);
            return false;
        }
    }
}
