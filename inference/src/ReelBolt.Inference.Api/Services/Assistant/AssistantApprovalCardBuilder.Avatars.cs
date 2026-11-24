using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Builds;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Assistant;

// The cards of the avatar tools (docs/design/avatar-objects.md "Avatar tools for the assistant and MCP"). Each is built
// from what the tool itself resolves from the database with the exact arguments (AssistantAvatarTools' Prepare*
// methods, the same code the tool runs), never from the model's words or from ids alone: names, kinds, the object
// statement to confirm, what is sent and to whom, and the price. A call the tool would refuse gets no card.
public sealed partial class AssistantApprovalCardBuilder
{
    internal const string MediaForThisCallLine =
        "Allows sending these pictures and videos to the AI service for this one call. The project's own setting is not changed.";

    /// <summary>The card line that shows the object statement; allowing the card is the user's confirmation of it.</summary>
    internal static string StatementLine(string locale) =>
        "By allowing this you confirm: “" + (AvatarObjectStatement.For(locale) ?? AvatarObjectStatement.English) + "”";

    private async Task<ApprovalVerdict> AvatarCardAsync(
        string tool, ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        if (_avatarTools is null)
            return ApprovalVerdict.Unreachable(AssistantAvatarTools.NotAvailableMessage);

        return tool switch
        {
            "ApplyCreateObjectAvatar" => await CreateObjectAvatarCardAsync(info, args, context, ct),
            "ApplyExtractObjectFromVideo" => await ExtractObjectCardAsync(info, args, context, ct),
            "ApplyCreateAvatarVariant" => await AvatarVariantCardAsync(info, args, context, ct),
            "ApplyImportLibraryAvatar" => await ImportLibraryAvatarCardAsync(info, args, context, ct),
            "ApplyGenerateSubjectClip" => await SubjectClipCardAsync(info, args, context, ct),
            _ => ApprovalVerdict.Unreachable()
        };
    }

    private async Task<ApprovalVerdict> CreateObjectAvatarCardAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        (AssistantAvatarTools.CreateObjectPrepared? prepared, string? error) =
            await _avatarTools!.PrepareCreateObjectAsync(context, AssistantAvatarTools.CreateObjectArgs.From(args), ct);
        if (prepared is null)
            return ApprovalVerdict.Unreachable(ErrorWords(error));

        var changes = new List<AssistantActionChange> { new(AssistantChangeKind.Add, $"The object avatar {Quote(prepared.Name)}") };
        changes.AddRange(FileLines(prepared.Files));
        changes.Add(new(AssistantChangeKind.Info, "Every picture is checked for people first; a picture that shows a person is refused."));
        changes.Add(new(AssistantChangeKind.Info, StatementLine(prepared.Locale)));
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Make the object avatar {Quote(prepared.Name)}",
            Summary: $"Make the object avatar {Quote(prepared.Name)} in {Quote(prepared.Project.Name)} from {AssistantAvatarTools.FilesWords(prepared.Files)}. Free.",
            Changes: changes, ProjectName: prepared.Project.Name, Cost: "Free", ProjectId: prepared.Project.Id, CostCredits: 0,
            Statement: AssistantAvatarTools.CardStatement(prepared.Locale)));
    }

    private async Task<ApprovalVerdict> ExtractObjectCardAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        (AssistantAvatarTools.ExtractPrepared? prepared, string? error) =
            await _avatarTools!.PrepareExtractAsync(context, AssistantAvatarTools.ExtractArgs.From(args), ct);
        if (prepared is null)
            return ApprovalVerdict.Unreachable(ErrorWords(error));

        ExtractionEstimate estimate = prepared.Estimate;
        bool asksForMedia = AssistantAvatarTools.AsksForMedia(prepared.Project, prepared.AllowMediaEgress);
        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Add, $"The object avatar {Quote(prepared.Name)}, taken out of the video {Quote(prepared.VideoPath)}"),
            new(AssistantChangeKind.Info, $"Which object: {Quote(prepared.ObjectDescription)}"),
            new(AssistantChangeKind.Info, estimate.Segment
                ? $"Sends up to {estimate.Moments} frames of the video to the object-finding service, which finds the object in each."
                : $"Uses up to {estimate.Moments} whole frames of the video; nothing is sent out."),
            new(AssistantChangeKind.Info, estimate.Segment
                ? "Every frame is checked for people before it is sent out, and the cut-out frames are checked again before the avatar is made; a frame that shows a person means no avatar is made."
                : "The frames become project files and are checked for people before the avatar is made; a frame that shows a person means no avatar is made."),
        };
        if (asksForMedia && estimate.Segment)
            changes.Add(new(AssistantChangeKind.Info, MediaForThisCallLine));
        changes.Add(new(AssistantChangeKind.Info, StatementLine(prepared.Locale)));

        (string risk, string cost, double? credits) = estimate.Credits is { } c and > 0
            ? (AssistantActionRisk.Paid, $"About {CreditCostWording.Up(c)} credits", (double?)c)
            : (AssistantActionRisk.Normal, "Free", 0d);
        if (estimate.Credits is > 0 && !estimate.Affordable)
            changes.Add(new(AssistantChangeKind.Info, "You don't have enough credits to start this."));
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, risk, $"Take {Quote(prepared.ObjectDescription)} out of {Quote(prepared.VideoPath)}",
            Summary: $"Make the object avatar {Quote(prepared.Name)} in {Quote(prepared.Project.Name)} from the video {Quote(prepared.VideoPath)}. {cost}.",
            Changes: changes, ProjectName: prepared.Project.Name, Cost: cost, ProjectId: prepared.Project.Id, CostCredits: credits,
            AllowsMediaEgress: asksForMedia && estimate.Segment,
            Statement: AssistantAvatarTools.CardStatement(prepared.Locale)));
    }

    private async Task<ApprovalVerdict> AvatarVariantCardAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        (AssistantAvatarTools.VariantPrepared? prepared, string? error) =
            await _avatarTools!.PrepareVariantAsync(context, AssistantAvatarTools.VariantArgs.From(args), ct);
        if (prepared is null)
            return ApprovalVerdict.Unreachable(ErrorWords(error));

        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Add, $"The variant {Quote(prepared.Name)} of the object {Quote(prepared.Original.Name)}"),
            new(AssistantChangeKind.Info, $"What sets it apart: {Quote(prepared.Description, 200)}"),
        };

        string risk = AssistantActionRisk.Normal;
        string cost = "Free";
        double? credits = 0;
        bool unestimated = false;
        bool asksForMedia = false;
        if (prepared.Files is { } files)
        {
            changes.AddRange(FileLines(files));
            changes.Add(new(AssistantChangeKind.Info, "Every picture is checked for people first; a picture that shows a person is refused."));
        }
        else
        {
            VariantEstimate estimate = prepared.Estimate!;
            changes.Add(new(AssistantChangeKind.Info, $"What the picture model changes: {Quote(prepared.Instruction!, 200)}"));
            changes.Add(new(AssistantChangeKind.Info,
                $"Sends {estimate.Pictures} picture{(estimate.Pictures == 1 ? "" : "s")} of {Quote(prepared.Original.Name)} to the AI picture service to be changed" +
                (estimate.Videos > 0 ? $", and the first frame of {estimate.Videos} video{(estimate.Videos == 1 ? "" : "s")}, which the AI video service makes again (the movement can differ)." : ".")));
            asksForMedia = AssistantAvatarTools.AsksForMedia(prepared.Project, prepared.AllowMediaEgress);
            if (asksForMedia)
                changes.Add(new(AssistantChangeKind.Info, MediaForThisCallLine));
            risk = AssistantActionRisk.Paid;
            if (estimate.BillingEnabled)
            {
                credits = estimate.CreditsHigh ?? estimate.Credits;
                cost = credits is { } c
                    ? (estimate.Videos > 0 ? $"Up to {CreditCostWording.Up(c)} credits" : $"About {CreditCostWording.Up(c)} credits")
                    : CreditCostWording.CostUnknown;
                unestimated = credits is null;
                if (!estimate.Affordable)
                    changes.Add(new(AssistantChangeKind.Info, "You don't have enough credits to start this."));
            }
            else
            {
                cost = "Paid";
                credits = null;
            }
        }

        changes.Add(new(AssistantChangeKind.Info, StatementLine(prepared.Locale)));
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, risk, $"Make the variant {Quote(prepared.Name)}",
            Summary: AssistantAvatarTools.VariantSummary(prepared),
            Changes: changes, ProjectName: prepared.Project.Name, Cost: cost, ProjectId: prepared.Project.Id,
            CostCredits: credits, CostUnestimated: unestimated, AllowsMediaEgress: asksForMedia,
            Statement: AssistantAvatarTools.CardStatement(prepared.Locale)));
    }

    private async Task<ApprovalVerdict> ImportLibraryAvatarCardAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        (AssistantAvatarTools.ImportPrepared? prepared, string? answer) = await _avatarTools!.PrepareImportAsync(
            context, ToolArgs.GetString(args, "name"), ToolArgs.GetGuid(args, "projectId"), ct);
        if (prepared is null)
        {
            // Already imported is an answer, not a refusal: the tool would write nothing either.
            return answer is not null && PropertyOf(answer, "error") is null
                ? ApprovalVerdict.AnswerWith(answer)
                : ApprovalVerdict.Unreachable(ErrorWords(answer));
        }

        AvatarLibraryItemView view = prepared.Item;
        string kind = AssistantAvatarTools.KindWord(view.Item.SubjectKind);
        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Add, $"{Quote(view.Item.Name)} ({kind}) from the workspace avatar library"),
            new(AssistantChangeKind.Info, $"Copies {view.Images} picture{(view.Images == 1 ? "" : "s")}" +
                (view.Videos > 0 ? $", {view.Videos} video{(view.Videos == 1 ? "" : "s")}" : "") +
                (view.Audio > 0 ? $", {view.Audio} recording{(view.Audio == 1 ? "" : "s")}" : "") + " into the project's files."),
            new(AssistantChangeKind.Info, "It stays linked: changes made to it in the library apply here too."),
        };
        if (view.Item.SubjectKind == AvatarSubjectKind.Person)
            changes.Add(new(AssistantChangeKind.Info, "The person's consent was given in the app when they were saved to the library."));
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Import {Quote(view.Item.Name)} from the workspace library",
            Summary: AssistantAvatarTools.ImportSummary(prepared),
            Changes: changes, ProjectName: prepared.Project.Name, Cost: "Free", ProjectId: prepared.Project.Id, CostCredits: 0));
    }

    private async Task<ApprovalVerdict> SubjectClipCardAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        (AssistantAvatarTools.ClipPrepared? prepared, string? error) =
            await _avatarTools!.PrepareClipAsync(context, AssistantAvatarTools.ClipArgs.From(args), ct);
        if (prepared is null)
            return ApprovalVerdict.Unreachable(ErrorWords(error));

        SubjectClipRequest request = prepared.Request;
        var changes = new List<AssistantActionChange>();
        foreach (ProjectAvatar subject in prepared.Subjects)
        {
            changes.Add(new(AssistantChangeKind.Info, subject.SubjectKind == AvatarSubjectKind.Object
                ? $"Shows: {Quote(subject.Name)} (object). Sends its pictures and videos to the AI video service."
                : $"Shows: {Quote(subject.Name)} (person). Sends that person's pictures, videos and voice samples to the AI video service."));
        }

        changes.Add(new(AssistantChangeKind.Info, $"What happens: {Quote(request.Prompt?.Trim() ?? string.Empty, 200)}"));
        changes.Add(new(AssistantChangeKind.Info, prepared.ModelName is { } model ? $"Video model: {Quote(model)}" : "Video model: the workspace's default"));
        string seconds = (request.DurationSeconds ?? 6).ToString(CultureInfo.InvariantCulture);
        int takes = request.Takes ?? 1;
        changes.Add(new(AssistantChangeKind.Info,
            (prepared.SpeechPath is null ? $"{seconds} s" : "As long as the speech") + $", {request.Ratio ?? "16:9"}, {takes} take{(takes == 1 ? "" : "s")}"));
        if (prepared.SpeechPath is { } speech)
            changes.Add(new(AssistantChangeKind.Info, $"Says the recording {Quote(speech)}"));
        if (prepared.Estimate.StillIncluded)
            changes.Add(new(AssistantChangeKind.Info, "One still picture of the scene is made first by the AI picture service."));
        bool asksForMedia = AssistantAvatarTools.AsksForMedia(prepared.Project, prepared.AllowMediaEgress);
        if (asksForMedia)
            changes.Add(new(AssistantChangeKind.Info, MediaForThisCallLine));
        changes.Add(new(AssistantChangeKind.Add, "Each take is added to the project's files as a video."));

        SubjectClipEstimate estimate = prepared.Estimate;
        string cost;
        double? credits = null;
        bool unestimated = false;
        if (estimate.BillingEnabled)
        {
            credits = estimate.CreditsHigh ?? estimate.Credits;
            cost = credits is { } c ? $"Up to {CreditCostWording.Up(c)} credits" : CreditCostWording.CostUnknown;
            unestimated = credits is null;
            if (!estimate.Affordable)
                changes.Add(new(AssistantChangeKind.Info, "You don't have enough credits to start this."));
        }
        else
        {
            cost = "Paid";
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, AssistantActionRisk.Paid, $"Make a clip with {AssistantAvatarTools.SubjectsWords(prepared.Subjects)}",
            Summary: $"Make a clip in {Quote(prepared.Project.Name)} that shows {AssistantAvatarTools.SubjectsWords(prepared.Subjects)}. {cost}. It starts a run; the clip is added to the project's files.",
            Changes: changes, ProjectName: prepared.Project.Name, Cost: cost, ProjectId: prepared.Project.Id,
            CostCredits: credits, CostUnestimated: unestimated, AllowsMediaEgress: asksForMedia));
    }

    private static IEnumerable<AssistantActionChange> FileLines(IReadOnlyList<AssistantAvatarTools.ObjectFile> files) =>
        files.Select(f => new AssistantActionChange(AssistantChangeKind.Info,
            $"{(f.Kind == AvatarReferenceKind.Video ? "Video" : "Picture")} ({f.Role}): {Quote(f.Path, 160)}"));

    /// <summary>The plain sentence of a tool's error JSON (what the refusal tells the model), or a generic one.</summary>
    private static string ErrorWords(string? errorJson) =>
        errorJson is not null && PropertyOf(errorJson, "error") is { Length: > 0 } words ? words : "that cannot be done here";
}
