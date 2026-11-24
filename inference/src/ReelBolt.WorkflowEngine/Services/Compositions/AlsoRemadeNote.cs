using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// The first-party note the reviser gets when the same change request also re-made steps UPSTREAM of the film
/// (new narration, new clips): it must re-stage what they produce before rendering. Built ONLY from the step
/// type and the validated item ids of the request rows — never from the owner's instruction or override text,
/// which stay inside the quoted request fence (the prompt-injection boundary).
/// </summary>
public static partial class AlsoRemadeNote
{
    private const int MaxIdsPerStep = 12;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex IdShape();

    /// <summary>The note, or null when there is nothing to say (no other targets re-made upstream).</summary>
    public static string? Build(IEnumerable<(string StepType, string? ScopeJson)> others)
    {
        List<string> parts = [];
        List<string> tools = [];
        foreach ((string stepType, string? scopeJson) in others)
        {
            if (!Enum.TryParse(stepType, ignoreCase: false, out StepType type) || !Enum.IsDefined(type))
                continue;

            (string label, string noun, string? tool) = type switch
            {
                StepType.Voiceover => ("the narration", "lines", "StageVoiceoverAudio"),
                StepType.VideoGenerate => ("the generated clips", "clips", "StageGeneratedClips"),
                _ => ($"the {type} step", "items", null)
            };

            List<string> ids = ValidIds(scopeJson);
            parts.Add(ids.Count > 0 ? $"{label} ({noun} {string.Join(", ", ids)})" : label);
            if (tool is not null && !tools.Contains(tool))
                tools.Add(tool);
        }

        if (parts.Count == 0)
            return null;

        StringBuilder sb = new("In this same change, earlier steps were also re-made: ");
        sb.Append(string.Join(", ", parts)).Append('.');
        sb.Append(tools.Count > 0
            ? $" Re-stage what they produce with {string.Join(" / ", tools)} before rendering, so the film uses the new versions."
            : " Make sure the film uses their new versions before rendering.");
        return sb.ToString();
    }

    private static List<string> ValidIds(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson))
            return [];
        try
        {
            List<string>? ids = JsonSerializer.Deserialize<List<string>>(scopeJson);
            return ids is null ? [] : [.. ids.Where(i => i is not null && IdShape().IsMatch(i)).Distinct().Take(MaxIdsPerStep)];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Reads the other targets of the revise step's run (one <c>step_change_requests</c> row per target).</summary>
public interface IAlsoRemadeNoteSource
{
    /// <summary>The note for <paramref name="reviseStepOrder"/> of the run, or null (no rows for earlier targets).</summary>
    Task<string?> FindAsync(Guid executionId, Guid projectId, int reviseStepOrder, CancellationToken ct);
}

/// <summary>Reads through a fresh DI scope per call, so the singleton Revise executor can depend on it.</summary>
public sealed class AlsoRemadeNoteSource(IServiceScopeFactory scopeFactory) : IAlsoRemadeNoteSource
{
    public async Task<string?> FindAsync(Guid executionId, Guid projectId, int reviseStepOrder, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        var rows = await db.StepChangeRequests.AsNoTracking()
            .Where(r => r.ExecutionId == executionId && r.ProjectId == projectId && r.TargetStepOrder < reviseStepOrder)
            .OrderBy(r => r.TargetStepOrder)
            .Select(r => new { r.StepType, r.ScopeJson })
            .ToListAsync(ct);
        return AlsoRemadeNote.Build(rows.Select(r => (r.StepType, r.ScopeJson)));
    }
}
