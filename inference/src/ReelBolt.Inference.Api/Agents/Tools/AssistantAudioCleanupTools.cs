using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// Clean the audio of a project file or a finished output, as assistant and MCP tools. <c>ProposeCleanAudio</c> checks the
/// source and prices nothing it would not start; <c>ApplyCleanAudio</c> starts the run through the SAME
/// <see cref="AudioCleanupService"/> the editor's route uses and returns the execution id without waiting.
/// <para>
/// Consent: the route has no server-side consent record (it only takes the <c>allowSourceMediaEgress</c> flag the editor's
/// dialog sends), so these tools never set that flag and never accept it. The hosted targets (<c>IsolateVoice</c>,
/// <c>RemoveMusic</c>) answer "needs the customer's OK in the editor" and run nothing; the local targets (<c>VoiceBasic</c>,
/// <c>Hum</c>, <c>Wind</c>, <c>Hiss</c>, <c>Echo</c>) run, free.
/// </para>
/// </summary>
public sealed class AssistantAudioCleanupTools
{
    internal const string OpCleanAudio = "CleanAudio";
    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";

    internal const string NeedsCustomerOkMessage =
        "Cleaning with AI (isolating the voice or removing music) sends the audio to a hosted AI model, and that needs the customer's OK in the editor. "
        + "Nothing was run. Ask them to open the file in the editor and use Clean audio there; the free cleanups (voice, hum, wind, hiss, echo) can be run from here.";

    public static readonly IReadOnlyDictionary<string, string> Captions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ProposeCleanAudio"] = "Checking the audio to clean",
        ["ApplyCleanAudio"] = "Starting the audio cleanup",
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly AudioCleanupService _cleanup;
    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly ICurrentUser _currentUser;

    public AssistantAudioCleanupTools(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        AudioCleanupService cleanup,
        IConfirmationTokenService confirmationTokens,
        ICurrentUser currentUser)
    {
        _db = db;
        _projectAccess = projectAccess;
        _cleanup = cleanup;
        _confirmationTokens = confirmationTokens;
        _currentUser = currentUser;
    }

    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string shape =
            " target is what to remove: VoiceBasic (clean up speech), Hum, Wind, Hiss or Echo (all free, done on our own servers); IsolateVoice and RemoveMusic use a hosted AI model and need the customer's OK in the editor, so they are refused here. Name the source with exactly one of fileId (a project file, from ListProjectFiles) or outputId (a finished render, from GetWorkflowExecution). outputKind is optional: Auto (default), Audio or VideoWithCleanAudio.";
        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (string target, Guid? fileId = null, Guid? outputId = null, string? outputKind = null, Guid? projectId = null)
                    => ProposeAsync(context, target, fileId, outputId, outputKind, projectId),
                name: "ProposeCleanAudio",
                description: "Proposes cleaning the audio of a project file or a finished render (remove hum, wind, hiss or echo, or tidy speech). Checks the source and returns a confirmationToken and a plain-language summary WITHOUT writing or running anything. The AI cleanups (isolate voice, remove music) are refused: they need the customer's OK in the editor." + shape + " " + AssistantProjectScope.ProjectIdNote + " The proposal's confirmationToken is accepted by ApplyCleanAudio."),
            AIFunctionFactory.Create(
                (string target, string confirmationToken, Guid? fileId = null, Guid? outputId = null, string? outputKind = null, Guid? projectId = null)
                    => ApplyAsync(context, target, confirmationToken, fileId, outputId, outputKind, projectId),
                name: "ApplyCleanAudio",
                description: "Starts the audio cleanup a prior ProposeCleanAudio call proposed and returns its executionId without waiting; the cleaned file is added to the project's files when it finishes (check with GetExecutionStatus). confirmationToken is verified against the proposal's source, target and output kind; a fabricated or stale token, or a changed argument, is refused and nothing runs." + shape + " " + AssistantProjectScope.ProjectIdNote),
        };
    }

    private async Task<string> ProposeAsync(
        AssistantToolContext context, string? target, Guid? fileId, Guid? outputId, string? outputKind, Guid? projectId)
    {
        Prepared prepared = await PrepareAsync(context, target, fileId, outputId, outputKind, projectId);
        if (prepared.Error is not null)
            return prepared.Error;

        string token = _confirmationTokens.Compute(OpCleanAudio, prepared.Payload!, context.UserId, _currentUser.OrganizationId);
        return JsonSerializer.Serialize(new
        {
            operation = OpCleanAudio,
            confirmationToken = token,
            summary = prepared.Summary,
            projectName = prepared.Project!.Name,
            target = prepared.Target,
            source = prepared.SourceName,
            minutes = prepared.Minutes,
            credits = "Free",
        }, Json);
    }

    private async Task<string> ApplyAsync(
        AssistantToolContext context, string? target, string? confirmationToken, Guid? fileId, Guid? outputId, string? outputKind, Guid? projectId)
    {
        Prepared prepared = await PrepareAsync(context, target, fileId, outputId, outputKind, projectId);
        if (prepared.Error is not null)
            return prepared.Error;

        if (!_confirmationTokens.Verify(OpCleanAudio, prepared.Payload!, context.UserId, _currentUser.OrganizationId, confirmationToken ?? string.Empty))
            return Error(StaleTokenMessage);

        // AllowSourceMediaEgress is never set here: only a local target reaches this line.
        AudioCleanupResult<AudioCleanupStartedResponse> started = await _cleanup.StartAsync(
            prepared.Project!.Id, prepared.MediaRef!, new AudioCleanupRequestBody(prepared.Target, false, prepared.OutputKind), CancellationToken.None);
        if (started.Failure is { } failure)
            return Failed(failure);

        return JsonSerializer.Serialize(new
        {
            executionId = started.Value!.ExecutionId,
            projectName = prepared.Project.Name,
            target = started.Value.Target,
            source = prepared.SourceName,
            state = started.Value.State,
            credits = "Free",
            hint = "The cleanup is running; it does not wait. Check it with GetExecutionStatus using this executionId. The cleaned file is added to the project's files when it is done.",
        }, Json);
    }

    /// <summary>Everything both tools decide the same way, so a token can only be minted for a cleanup Apply would start.</summary>
    private async Task<Prepared> PrepareAsync(
        AssistantToolContext context, string? target, Guid? fileId, Guid? outputId, string? outputKind, Guid? projectId)
    {
        // Authority first, re-read on every call: a project the caller may not edit is absent.
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string scopeError);
        if (project is null)
            return Prepared.Fail(scopeError);

        if (!AudioCleanupService.TryReadBody(
                new AudioCleanupRequestBody(target, false, outputKind), out AudioCleanupTarget parsed, out AudioCleanupOutputKind kind, out Microsoft.AspNetCore.Mvc.IActionResult? bad))
        {
            return Prepared.Fail(Failed(AudioCleanupFailure.From(bad!)));
        }

        if ((fileId is null) == (outputId is null))
            return Prepared.Fail(Error("name the source with exactly one of fileId (a project file) or outputId (a finished render).", "INVALID_REQUEST"));

        string mediaRef = fileId is { } f ? $"file:{f}" : $"step_result:{outputId}";
        AudioCleanupResult<AudioCleanupEstimateResponse> estimate = await _cleanup.EstimateAsync(
            project.Id, mediaRef, new AudioCleanupRequestBody(target, false, outputKind), CancellationToken.None);
        if (estimate.Failure is { } failure)
            return Prepared.Fail(Failed(failure));

        // No server-side consent record exists, so a hosted target is never run from here.
        if (AudioCleanupService.IsHosted(parsed))
            return Prepared.Fail(Error(NeedsCustomerOkMessage, "NEEDS_CUSTOMER_OK"));

        string sourceName = fileId is { } id
            ? await _db.ProjectFiles.AsNoTracking().Where(x => x.Id == id && x.ProjectId == project.Id).Select(x => x.OriginalFileName).FirstOrDefaultAsync() ?? "the file"
            : "an earlier render";
        double? minutes = estimate.Value!.Seconds is { } seconds ? Math.Round(seconds / 60.0, 1) : null;

        string payload = JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            mediaRef,
            target = parsed.ToString(),
            outputKind = kind.ToString(),
        });
        string summary = $"Clean the audio of {sourceName} in project \"{project.Name}\" ({Describe(parsed)}). It is free: it runs on our own servers and uses no AI model. "
            + "It starts a run and does not wait; the cleaned file is added to the project's files when it finishes.";
        return new Prepared(null, project, parsed.ToString(), kind.ToString(), mediaRef, sourceName, minutes, payload, summary);
    }

    internal static string Describe(AudioCleanupTarget target) => target switch
    {
        AudioCleanupTarget.VoiceBasic => "tidy up speech",
        AudioCleanupTarget.Hum => "remove hum",
        AudioCleanupTarget.Wind => "remove wind noise",
        AudioCleanupTarget.Hiss => "remove hiss",
        AudioCleanupTarget.Echo => "remove echo",
        _ => target.ToString(),
    };

    private static string Failed(AudioCleanupFailure failure) =>
        JsonSerializer.Serialize(new { error = failure.Message, code = failure.Code, retryAfterSeconds = failure.RetryAfterSeconds });

    private static string Error(string message, string? code = null) =>
        code is null
            ? JsonSerializer.Serialize(new { error = message })
            : JsonSerializer.Serialize(new { error = message, code });

    private sealed record Prepared(
        string? Error,
        Project? Project,
        string? Target,
        string? OutputKind,
        string? MediaRef,
        string? SourceName,
        double? Minutes,
        string? Payload,
        string? Summary)
    {
        public static Prepared Fail(string error) => new(error, null, null, null, null, null, null, null, null);
    }
}
