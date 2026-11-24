using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The arguments of one "schedule a post" call, read the same way by the tool and by its approval card, so the card can
/// only describe what the tool will do. <see cref="Canonical"/> is the confirmation token's payload.
/// </summary>
internal sealed record SocialDraft(
    Guid ProjectId,
    Guid OutputId,
    Guid AccountId,
    string? OutputKind,
    string? Title,
    string? Body,
    IReadOnlyList<string> Hashtags,
    Guid? PostTextId,
    string? Privacy,
    bool? AiGenerated,
    string? ScheduledAt,
    string? ScheduledLocal,
    string? TimeZone)
{
    /// <summary>The draft as the card builder sees it: the gate's canonical argument copy.</summary>
    public static SocialDraft FromArgs(AIFunctionArguments args, Guid projectId) => new(
        projectId,
        ToolArgs.GetGuid(args, "outputId") ?? Guid.Empty,
        ToolArgs.GetGuid(args, "accountId") ?? Guid.Empty,
        Clean(ToolArgs.GetString(args, "outputKind")),
        Clean(ToolArgs.GetString(args, "title")),
        Clean(ToolArgs.GetString(args, "body")),
        ToolArgs.GetStrings(args, "hashtags") ?? [],
        ToolArgs.GetGuid(args, "postTextId"),
        Clean(ToolArgs.GetString(args, "privacy")),
        ToolArgs.GetBool(args, "aiGenerated"),
        Clean(ToolArgs.GetString(args, "scheduledAt")),
        Clean(ToolArgs.GetString(args, "scheduledLocal")),
        Clean(ToolArgs.GetString(args, "timeZone")));

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Everything that decides what is posted, where and when; the token binds it.</summary>
    public string Canonical() => string.Join('\n',
        ProjectId.ToString("N"), OutputId.ToString("N"), AccountId.ToString("N"), OutputKind ?? string.Empty,
        Title ?? string.Empty, Body ?? string.Empty, string.Join('\u001f', Hashtags),
        PostTextId?.ToString("N") ?? string.Empty, Privacy ?? string.Empty, AiGenerated?.ToString() ?? string.Empty,
        ScheduledAt ?? string.Empty, ScheduledLocal ?? string.Empty, TimeZone ?? string.Empty);
}

/// <summary>A schedule request after resolution: what it names (by NAME) and the exact values the app's create route gets.</summary>
internal sealed record SocialDraftView(
    string AccountName,
    string PlatformName,
    string ProjectName,
    string OutputName,
    string OutputKind,
    string? Title,
    string? Body,
    IReadOnlyList<string> Hashtags,
    string WhenText,
    string? Privacy,
    bool AiGenerated,
    DateTime ScheduledUtc);

/// <summary>Resolves a <see cref="SocialDraft"/> against the active organization; shared by the tool and the card.</summary>
internal static class AssistantSocialDrafts
{
    /// <summary>
    /// What the confirmation token binds: the draft's arguments plus a hash of the EXACT text the card shows (the title, body and
    /// hashtags after a saved post text is resolved). Editing the saved text after the card was built changes the hash, so the
    /// approval can only ever release the text the person saw.
    /// </summary>
    public static string TokenPayload(SocialDraft draft, SocialDraftView view) =>
        draft.Canonical() + "\n" + TextHash(view);

    public static string TextHash(SocialDraftView view)
    {
        string text = string.Join('\u001e', view.Title ?? string.Empty, view.Body ?? string.Empty, string.Join('\u001f', view.Hashtags));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public const string BadWhen = "give the time as scheduledAt (an instant with a Z or offset) or scheduledLocal plus timeZone (an IANA name such as Europe/Rome)";

    /// <summary>The moment the draft names, as the create route receives it. Null parts are left null.</summary>
    public static bool TryParseWhen(SocialDraft draft, out DateTimeOffset? at, out DateTime? local)
    {
        at = null;
        local = null;
        if (draft.ScheduledAt is { } text)
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
                || !HasOffset(text))
                return false;
            at = parsed;
        }

        if (draft.ScheduledLocal is { } localText)
        {
            if (!DateTime.TryParse(localText, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                return false;
            local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }

        return true;
    }

    private static bool HasOffset(string text) =>
        text.EndsWith('Z') || text.EndsWith('z') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$");

    /// <summary>
    /// The kind and the NAME of the output, found by the app's own resolver (so it is the project's, in the organization, and a
    /// video). A kind the model did not say is looked for as a finished run's render first, then as a project file.
    /// </summary>
    public static async Task<(string? Kind, string? Name)> FindOutputAsync(
        ISocialOutputResolver outputs, InferenceApiDbContext db, Guid organizationId, Guid projectId, Guid outputId, string? kind, CancellationToken ct)
    {
        foreach (string candidate in kind is null ? new[] { SocialOutputKinds.StepResult, SocialOutputKinds.ProjectFile } : new[] { kind })
        {
            SocialResolvedOutput? found = await outputs.ResolveAsync(organizationId, projectId, candidate, outputId, ct);
            if (found is null)
                continue;

            string? name = candidate == SocialOutputKinds.ProjectFile
                ? await db.ProjectFiles.IgnoreQueryFilters().AsNoTracking()
                    .Where(f => f.Id == outputId && f.ProjectId == projectId).Select(f => f.OriginalFileName).FirstOrDefaultAsync(ct)
                : null;
            name ??= found.StorageKey[(found.StorageKey.LastIndexOf('/') + 1)..];
            return (candidate, name);
        }

        return (null, null);
    }

    public static string AccountLabel(SocialAccount account) =>
        string.IsNullOrWhiteSpace(account.DisplayName)
            ? $"{SocialPlatforms.DisplayName(account.Platform)} account"
            : account.DisplayName.Trim();

    /// <summary>
    /// Resolves the draft for <paramref name="project"/> (already authorized by the caller) in <paramref name="organizationId"/>.
    /// A failure names no id and confirms nothing about another organization.
    /// </summary>
    public static async Task<(SocialDraftView? View, string? Failure)> ResolveAsync(
        ISocialOutputResolver outputs, InferenceApiDbContext db, Guid organizationId, Project project, SocialDraft draft, DateTime nowUtc, CancellationToken ct)
    {
        SocialAccount? account = await db.SocialAccounts.AsNoTracking().FirstOrDefaultAsync(
            a => a.Id == draft.AccountId && a.OrganizationId == organizationId && a.Status != SocialAccountStatus.Revoked, ct);
        if (account is null)
            return (null, "account not found");
        if (account.Status != SocialAccountStatus.Active)
            return (null, "that account needs to be reconnected in the Publishing page before it can post");

        (string? kind, string? outputName) = await FindOutputAsync(outputs, db, organizationId, project.Id, draft.OutputId, draft.OutputKind, ct);
        if (kind is null || outputName is null)
            return (null, "video not found in this project");

        string? title = draft.Title;
        string? body = draft.Body;
        List<string> hashtags = [.. draft.Hashtags];
        if (draft.PostTextId is { } postTextId)
        {
            string textPlatformId = SocialPlatformLimits.For(account.Platform).TextPlatformId;
            var saved = await db.PostTexts.AsNoTracking()
                .Where(t => t.Id == postTextId && t.ProjectId == project.Id && t.OutputId == draft.OutputId && t.Platform == textPlatformId)
                .Select(t => new { t.Title, t.Body, t.HashtagsJson })
                .FirstOrDefaultAsync(ct);
            if (saved is null)
                return (null, "saved post text not found for this video and platform");
            title ??= saved.Title;
            body ??= saved.Body;
            if (draft.Hashtags.Count == 0)
            {
                try { hashtags = JsonSerializer.Deserialize<List<string>>(saved.HashtagsJson ?? "[]") ?? []; }
                catch (JsonException) { hashtags = []; }
            }
        }

        if (!TryParseWhen(draft, out DateTimeOffset? at, out DateTime? local))
            return (null, BadWhen);
        SocialScheduleResult when = SocialScheduleTime.Resolve(false, at, local, draft.TimeZone, nowUtc);
        if (!when.Ok)
            return (null, when.ErrorMessage);

        string whenText = when.TimeZone is { } zone && SocialScheduleTime.ToLocalText(when.Utc, zone) is { } localText
            ? $"{localText.Replace('T', ' ')[..16]} ({zone})"
            : $"{when.Utc:yyyy-MM-dd HH:mm} UTC";

        return (new SocialDraftView(
            AccountLabel(account), SocialPlatforms.DisplayName(account.Platform), project.Name, outputName, kind,
            DraftText(title), DraftText(body),
            [.. hashtags.Select(SocialPlatformLimits.NormalizeHashtag).Where(t => t.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase)],
            whenText, draft.Privacy, draft.AiGenerated == true, when.Utc!.Value), null);
    }

    private static string? DraftText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
