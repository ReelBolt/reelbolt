#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// Readers of a video-analysis file (audio facts, edit proposal facts, source durations): the file of an analysis the
/// project keeps back is not opened, and the file of a later analysis is.
/// </summary>
public sealed class AnalysisArtifactReadersSurfaceTests
{
    public enum Reader { AudioFacts, ProposalFacts, SourceDurations }

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public List<string> Opened { get; } = [];
        public Mock<IFileStorageService> Storage { get; } = new();

        public Rig(bool earlierAnalysis)
        {
            W.Step.StepType = StepType.VideoAnalyze;
            // Only the analysis under test carries an artifact, so it is the one the reader picks.
            (earlierAnalysis ? W.LaterResult : W.EarlierResult).ArtifactStorageKey = null;
            Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, string, CancellationToken>((_, key, _) => Opened.Add(key))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        }

        public string ArtifactKey(bool earlier) => (earlier ? W.EarlierResult : W.LaterResult).ArtifactStorageKey!;

        public async Task Run(Reader reader, SurfaceState? state, InferenceApiDbContext? db = null)
        {
            if (state is { } s) W.In(s); else W.Db.SaveChanges();
            InferenceApiDbContext d = db ?? W.Db;
            switch (reader)
            {
                case Reader.AudioFacts:
                    await AnalysisAudioFacts.LoadAsync(d, Storage.Object, NullLogger.Instance, W.Project.Id, W.EarlierRun.Id, CancellationToken.None);
                    break;
                case Reader.ProposalFacts:
                    await ProposalFactsLoader.LoadAsync(d, Storage.Object, NullLogger.Instance, W.Project.Id, [W.Upload.Id], CancellationToken.None);
                    break;
                default:
                    AssetRef render = new(AssetRefKind.StepResult, StepResultId: W.LaterResult.Id);
                    await new SourceDurationResolver(d, Storage.Object, NullLogger.Instance).ResolveAsync(
                        W.Project.Id, [render], new Dictionary<Guid, string>(),
                        new Dictionary<Guid, string> { [W.LaterResult.Id] = W.LaterResult.OutputStorageKey! }, 30, CancellationToken.None);
                    break;
            }
        }
    }

    public static IEnumerable<object[]> Readers() => Enum.GetValues<Reader>().Select(r => new object[] { r });

    public static IEnumerable<object[]> ServedStates()
    {
        foreach (Reader r in Enum.GetValues<Reader>())
        {
            yield return [r, SurfaceState.Clear];
            yield return [r, SurfaceState.ModerationOnly];
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task The_file_of_an_earlier_analysis_is_not_opened(Reader reader)
    {
        Rig r = new(earlierAnalysis: true);
        await r.Run(reader, SurfaceState.Held);
        r.Opened.Should().NotContain(r.ArtifactKey(earlier: true));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task The_file_of_a_later_analysis_is_opened_while_earlier_outputs_are_kept(Reader reader)
    {
        Rig r = new(earlierAnalysis: false);
        await r.Run(reader, SurfaceState.Held);
        r.Opened.Should().Contain(r.ArtifactKey(earlier: false));
    }

    [Theory]
    [MemberData(nameof(ServedStates))]
    public async Task The_file_of_the_earlier_analysis_is_opened_when_the_project_keeps_nothing(Reader reader, SurfaceState state)
    {
        Rig r = new(earlierAnalysis: true);
        await r.Run(reader, state);
        r.Opened.Should().Contain(r.ArtifactKey(earlier: true));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task The_file_of_an_analysis_is_not_opened_when_the_marker_cannot_be_read(Reader reader)
    {
        Rig r = new(earlierAnalysis: false);
        r.W.Db.SaveChanges();
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        await r.Run(reader, null, failing);
        r.Opened.Should().NotContain(r.ArtifactKey(earlier: false));
    }
}
