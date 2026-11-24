#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>Opening a picture in the studio through the assistant: a file or a step result the project keeps back answers the neutral text.</summary>
public sealed class AssistantPictureDocumentToolsSurfaceTests
{
    public enum Source { EarlierFile, LaterFile, Upload, EarlierResult, LaterResult }

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public Dictionary<Source, Guid> Ids { get; } = [];

        public Rig()
        {
            foreach (ProjectFile f in new[] { W.EarlierFile, W.LaterFile, W.Upload }) f.MimeType = "image/png";
            // The step results register the image file they produced, the way an extracted frame does.
            W.EarlierResult.Output = JsonSerializer.Serialize(new { projectFileId = W.EarlierFile.Id });
            W.LaterResult.Output = JsonSerializer.Serialize(new { projectFileId = W.LaterFile.Id });
            W.Db.SaveChanges();
            Ids[Source.EarlierFile] = W.EarlierFile.Id;
            Ids[Source.LaterFile] = W.LaterFile.Id;
            Ids[Source.Upload] = W.Upload.Id;
            Ids[Source.EarlierResult] = W.EarlierResult.Id;
            Ids[Source.LaterResult] = W.LaterResult.Id;
        }

        public async Task<string> Propose(Source source, InferenceApiDbContext? db = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            FakeFileStorage storage = new();
            AssistantPictureDocumentTools tools = new(
                d, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(d, W.User), W.User,
                Mock.Of<IImageDocumentCommands>(), storage);
            AIFunction function = tools.GetTools(new AssistantToolContext(W.UserId, W.Project.Id)).Single(t => t.Name == "ProposeCreatePictureDocument");
            bool isResult = source is Source.EarlierResult or Source.LaterResult;
            AIFunctionArguments args = new() { [isResult ? "outputId" : "fileId"] = Ids[source] };
            object? raw = await function.InvokeAsync(args);
            return raw switch
            {
                JsonElement { ValueKind: JsonValueKind.String } s => s.GetString()!,
                JsonElement e => e.GetRawText(),
                _ => raw?.ToString() ?? string.Empty,
            };
        }
    }

    private static readonly Source[] Withheld = [Source.EarlierFile, Source.EarlierResult];
    private static readonly Source[] Kept = [Source.LaterFile, Source.Upload, Source.LaterResult];
    public static IEnumerable<object[]> WithheldCases() => Withheld.Select(s => new object[] { s });
    public static IEnumerable<object[]> KeptCases() => Kept.Select(s => new object[] { s });
    public static IEnumerable<object[]> ServedCases()
    {
        foreach (Source s in Enum.GetValues<Source>())
        {
            yield return [s, SurfaceState.Clear];
            yield return [s, SurfaceState.ModerationOnly];
        }
    }

    [Theory]
    [MemberData(nameof(WithheldCases))]
    public async Task An_earlier_output_answers_the_neutral_text_and_no_proposal(Source source)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        string answer = await r.Propose(source);
        answer.Should().Contain(OutputHoldResponse.Message).And.NotContain("confirmationToken");
    }

    [Theory]
    [MemberData(nameof(KeptCases))]
    public async Task A_later_output_and_an_upload_are_proposed_while_earlier_outputs_are_kept(Source source)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Propose(source)).Should().Contain("confirmationToken");
    }

    [Theory]
    [MemberData(nameof(ServedCases))]
    public async Task Every_output_is_proposed_when_the_project_keeps_nothing(Source source, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        (await r.Propose(source)).Should().Contain("confirmationToken");
    }

    [Theory]
    [InlineData(Source.LaterFile)]
    [InlineData(Source.LaterResult)]
    public async Task A_failed_read_answers_the_neutral_text(Source source)
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        (await r.Propose(source, failing)).Should().Contain(OutputHoldResponse.Message);
    }
    [Fact]
    public async Task An_earlier_step_result_is_refused_even_when_the_image_it_names_is_an_upload()
    {
        Rig r = new();
        r.W.EarlierResult.Output = JsonSerializer.Serialize(new { projectFileId = r.W.Upload.Id });
        r.W.In(SurfaceState.Held);
        (await r.Propose(Source.EarlierResult)).Should().Contain(OutputHoldResponse.Message).And.NotContain("confirmationToken");
    }
}
