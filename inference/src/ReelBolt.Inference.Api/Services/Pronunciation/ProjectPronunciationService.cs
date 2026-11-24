using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Pronunciation;

public enum PronunciationSaveStatus
{
    Created,
    Updated,
    Unchanged,
    Invalid,
    Full,
    TermExists,
    NotFound,
    /// <summary>Content moderation refused the spelling; <c>Errors</c> carries the user-facing message.</summary>
    Blocked
}

public sealed record PronunciationSaveResult(
    PronunciationSaveStatus Status,
    ProjectPronunciationEntry? Entry = null,
    IReadOnlyList<string>? Errors = null);

/// <summary>
/// A project's pronunciation glossary (list / upsert / delete). The caller has ALREADY proved access
/// to <c>projectId</c> through <c>IProjectAccessService</c>; this service never widens that.
/// </summary>
public class ProjectPronunciationService(InferenceApiDbContext db, IContentModerationGate moderation)
{
    /// <summary>
    /// The moderation attribution for a glossary save: the spelling is text the provider will speak, so it is
    /// the same surface as other generated narration text (fails open when moderation is off or unavailable).
    /// </summary>
    public static ModerationAttribution Attribution(Guid organizationId, Guid projectId, Guid? userId) =>
        new(organizationId, projectId, userId, null, null, ModerationSurface.GeneratedText, ModerationContentKind.Text);

    /// <summary>
    /// Screens a spelling before it is stored. Null = allowed (Allow, Review, or moderation off/unavailable:
    /// the generated-text surface fails open); a Block returns a refusal and nothing is written.
    /// </summary>
    private async Task<PronunciationSaveResult?> ScreenSpellingAsync(
        string? spelling, ModerationAttribution attribution, CancellationToken ct)
    {
        ModerationDecision decision = await moderation.ModerateAsync(
            new ModerationInput(PronunciationGlossaryValidator.Normalize(spelling), [], ModerationContentKind.Text),
            attribution, ct);
        if (decision.Action != ModerationAction.Block)
            return null;
        string message = string.IsNullOrWhiteSpace(decision.UserMessage)
            ? "That pronunciation may violate the content policy."
            : decision.UserMessage;
        return new(PronunciationSaveStatus.Blocked, Errors: [message]);
    }

    public async Task<List<ProjectPronunciationEntry>> ListAsync(Guid projectId, CancellationToken ct) =>
        await db.ProjectPronunciationEntries.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.Term)
            .ToListAsync(ct);

    /// <summary>Adds an entry; the same term (any case) is <see cref="PronunciationSaveStatus.TermExists"/>.</summary>
    public async Task<PronunciationSaveResult> CreateAsync(
        Guid projectId, string? term, string? spelling, bool caseSensitive, ModerationAttribution attribution, CancellationToken ct)
    {
        IReadOnlyList<string> errors = PronunciationGlossaryValidator.Validate(term, spelling);
        if (errors.Count > 0)
            return new(PronunciationSaveStatus.Invalid, Errors: errors);
        if (await ScreenSpellingAsync(spelling, attribution, ct) is { } blocked)
            return blocked;

        string t = PronunciationGlossaryValidator.Normalize(term);
        string s = PronunciationGlossaryValidator.Normalize(spelling);

        List<ProjectPronunciationEntry> existing = await db.ProjectPronunciationEntries
            .Where(e => e.ProjectId == projectId).ToListAsync(ct);
        if (existing.Any(e => SameTerm(e.Term, t)))
            return new(PronunciationSaveStatus.TermExists);
        if (existing.Count >= ProjectPronunciationEntry.MaxEntriesPerProject)
            return new(PronunciationSaveStatus.Full);

        var entry = NewEntry(projectId, t, s, caseSensitive);
        db.ProjectPronunciationEntries.Add(entry);
        if (!await TrySaveNewAsync(entry, ct))
            return new(PronunciationSaveStatus.TermExists);
        return new(PronunciationSaveStatus.Created, entry);
    }

    /// <summary>
    /// A concurrent request may insert the same term between our read and our write; the unique index then
    /// rejects the second insert, which is reported as an existing term rather than a server error.
    /// </summary>
    private async Task<bool> TrySaveNewAsync(ProjectPronunciationEntry entry, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(entry).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Upserts by term (case-insensitive, NFC): creates, updates the spelling/case flag, or reports
    /// <see cref="PronunciationSaveStatus.Unchanged"/>. The assistant tool and the REST layer share this.
    /// </summary>
    public async Task<PronunciationSaveResult> UpsertAsync(
        Guid projectId, string? term, string? spelling, bool caseSensitive, ModerationAttribution attribution, CancellationToken ct)
    {
        IReadOnlyList<string> errors = PronunciationGlossaryValidator.Validate(term, spelling);
        if (errors.Count > 0)
            return new(PronunciationSaveStatus.Invalid, Errors: errors);
        if (await ScreenSpellingAsync(spelling, attribution, ct) is { } blocked)
            return blocked;

        string t = PronunciationGlossaryValidator.Normalize(term);
        string s = PronunciationGlossaryValidator.Normalize(spelling);

        List<ProjectPronunciationEntry> existing = await db.ProjectPronunciationEntries
            .Where(e => e.ProjectId == projectId).ToListAsync(ct);
        ProjectPronunciationEntry? match = existing.FirstOrDefault(e => SameTerm(e.Term, t));
        if (match is null)
        {
            if (existing.Count >= ProjectPronunciationEntry.MaxEntriesPerProject)
                return new(PronunciationSaveStatus.Full);
            var entry = NewEntry(projectId, t, s, caseSensitive);
            db.ProjectPronunciationEntries.Add(entry);
            if (!await TrySaveNewAsync(entry, ct))
                return new(PronunciationSaveStatus.TermExists);
            return new(PronunciationSaveStatus.Created, entry);
        }

        if (match.Spelling == s && match.CaseSensitive == caseSensitive)
            return new(PronunciationSaveStatus.Unchanged, match);

        // The stored term keeps its original casing; only the spelling and case flag change.
        match.Spelling = s;
        match.CaseSensitive = caseSensitive;
        match.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(PronunciationSaveStatus.Updated, match);
    }

    /// <summary>Edits one entry by id; renaming onto another entry's term is <c>TermExists</c>.</summary>
    public async Task<PronunciationSaveResult> UpdateAsync(
        Guid projectId, Guid id, string? term, string? spelling, bool caseSensitive, ModerationAttribution attribution, CancellationToken ct)
    {
        ProjectPronunciationEntry? entry = await db.ProjectPronunciationEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId, ct);
        if (entry is null)
            return new(PronunciationSaveStatus.NotFound);

        IReadOnlyList<string> errors = PronunciationGlossaryValidator.Validate(term, spelling);
        if (errors.Count > 0)
            return new(PronunciationSaveStatus.Invalid, Errors: errors);
        if (await ScreenSpellingAsync(spelling, attribution, ct) is { } blocked)
            return blocked;

        string t = PronunciationGlossaryValidator.Normalize(term);
        string s = PronunciationGlossaryValidator.Normalize(spelling);

        List<ProjectPronunciationEntry> others = await db.ProjectPronunciationEntries
            .Where(e => e.ProjectId == projectId && e.Id != id).ToListAsync(ct);
        if (others.Any(e => SameTerm(e.Term, t)))
            return new(PronunciationSaveStatus.TermExists);

        if (entry.Term == t && entry.Spelling == s && entry.CaseSensitive == caseSensitive)
            return new(PronunciationSaveStatus.Unchanged, entry);

        entry.Term = t;
        entry.Spelling = s;
        entry.CaseSensitive = caseSensitive;
        entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(PronunciationSaveStatus.Updated, entry);
    }

    /// <summary>Removes one entry; false when it is not in this project.</summary>
    public async Task<bool> DeleteAsync(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectPronunciationEntry? entry = await db.ProjectPronunciationEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId, ct);
        if (entry is null)
            return false;
        db.ProjectPronunciationEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool SameTerm(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ProjectPronunciationEntry NewEntry(Guid projectId, string term, string spelling, bool caseSensitive)
    {
        DateTime now = DateTime.UtcNow;
        return new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Term = term,
            Spelling = spelling,
            CaseSensitive = caseSensitive,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
