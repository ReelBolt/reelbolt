using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.History;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// The one place every run start asks "is a restore of this project rewriting it right now?". REST starts
/// reach it through <see cref="RunStartGate"/> (Execute, Retry, Resume, workflow-assistant execute); the
/// assistant/MCP ExecuteWorkflow tool and the audio-cleanup hidden run call it directly. A held lock
/// answers the same <c>409 RESTORE_IN_PROGRESS</c> shape everywhere (assistant tools return it as their
/// usual refusal JSON with that code).
/// </summary>
public static class RestoreRunGate
{
    public const string Message = "A restore of this project is running. Wait for it to finish, then start the run.";

    private static readonly AsyncLocal<Func<Guid, bool>?> HeldOverride = new();

    /// <summary>True while a restore of the project holds its lock.</summary>
    public static async Task<bool> IsHeldAsync(DbContext db, Guid projectId, CancellationToken ct)
    {
        // A project the test did not name is not forced: it falls through to the real probe.
        if (HeldOverride.Value is { } forced && forced(projectId))
            return true;

        return await ProjectRestoreLockProbe.IsHeldAsync(db, projectId, ct);
    }

    /// <summary>The 409 every REST-shaped start answers with.</summary>
    public static ObjectResult Refusal() => new(new
    {
        code = ProjectHistoryRestoreCodes.RestoreInProgress,
        message = Message
    })
    { StatusCode = StatusCodes.Status409Conflict };

    /// <summary>The refusal JSON the assistant tools return.</summary>
    public static string RefusalJson() => JsonSerializer.Serialize(new
    {
        executed = false,
        code = ProjectHistoryRestoreCodes.RestoreInProgress,
        error = Message,
        message = Message
    });

    /// <summary>
    /// Test seam: the Npgsql-only lock probe cannot run on the InMemory provider, so a test names the
    /// projects whose lock counts as held for the current async flow. Disposing ends the override.
    /// </summary>
    internal static IDisposable HoldForTests(params Guid[] projectIds)
    {
        Func<Guid, bool>? previous = HeldOverride.Value;
        HeldOverride.Value = id => projectIds.Contains(id) || (previous is not null && previous(id));
        return new Restore(previous);
    }

    private sealed class Restore(Func<Guid, bool>? previous) : IDisposable
    {
        public void Dispose() => HeldOverride.Value = previous;
    }
}
