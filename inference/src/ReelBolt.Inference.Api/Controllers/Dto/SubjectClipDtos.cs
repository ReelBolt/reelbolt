namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>One avatar a subject clip shows (<c>{ avatarId }</c>).</summary>
public sealed record SubjectClipSubjectRequest(Guid AvatarId);

/// <summary>
/// The body of <c>POST .../subject-clips</c> and <c>POST .../subject-clips/estimate</c> (docs/design/avatar-objects.md
/// "Generating a clip with avatars from the editor (subject clips)"; the web client is
/// <c>web/lib/editing/subjects/subject-clip-api.ts</c>, <c>SubjectClipRequest</c>).
/// </summary>
/// <param name="Subjects">1..3 avatars of the project, at most one of them a person, in the order chosen.</param>
/// <param name="Prompt">What happens in the clip, 1..2000 characters.</param>
/// <param name="CapabilityId">A <c>VideoGeneration</c> model capability; left out = the workspace's default one.</param>
/// <param name="DurationSeconds">The clip's length in seconds (the model's range); left out = 6. A talking clip is as long as its speech.</param>
/// <param name="Ratio">The frame shape (<c>16:9</c>, <c>9:16</c>, <c>1:1</c>, <c>4:5</c>, as the model offers them); left out = 16:9.</param>
/// <param name="Resolution">The model's size word (<c>768P</c>, <c>2K</c>, ...); left out = 768P when offered.</param>
/// <param name="Takes">1..2; left out = 1.</param>
/// <param name="SpeechFileId">A project WAV or MP3 the one chosen person says (a talking clip): one person, nothing else.</param>
/// <param name="SubjectMode"><c>Auto</c> (left out), <c>References</c> or <c>FirstFrame</c>.</param>
/// <param name="AllowMediaEgress">
/// Allows sending the avatars' files to the AI provider for THIS request when the project's media consent is not
/// recorded. Counts only for a caller the safe rule lets allow it (a person signed in to the app, on this route); it
/// never records the project consent. The editor does not send it: it asks for the one-time consent instead.
/// </param>
public sealed record SubjectClipRequestBody(
    IReadOnlyList<SubjectClipSubjectRequest>? Subjects = null,
    string? Prompt = null,
    Guid? CapabilityId = null,
    int? DurationSeconds = null,
    string? Ratio = null,
    string? Resolution = null,
    int? Takes = null,
    Guid? SpeechFileId = null,
    string? SubjectMode = null,
    bool? AllowMediaEgress = null);
