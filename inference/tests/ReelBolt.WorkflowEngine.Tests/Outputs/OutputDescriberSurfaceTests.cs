#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>What the assistant can look at: an output the project keeps back is absent for the lookup, the description and the render check.</summary>
public sealed class OutputDescriberSurfaceTests
{
    public enum Which { EarlierRender, LaterRender, EarlierFile, LaterFile, Upload }

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public InMemoryFileStorage Storage { get; } = new();

        public Rig()
        {
            foreach (string key in new[] { W.EarlierResult.OutputStorageKey!, W.LaterResult.OutputStorageKey!, W.EarlierFile.StorageKey, W.LaterFile.StorageKey, W.Upload.StorageKey })
                Storage.Objects[key] = [1, 2, 3, 4];
        }

        public Guid IdOf(Which w) => w switch
        {
            Which.EarlierRender => W.EarlierResult.Id,
            Which.LaterRender => W.LaterResult.Id,
            Which.EarlierFile => W.EarlierFile.Id,
            Which.LaterFile => W.LaterFile.Id,
            _ => W.Upload.Id,
        };

        public OutputDescriber Describer(InferenceApiDbContext? db = null)
        {
            DateTimeOffset now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
            OutputLinkService links = new(ReelBolt.Inference.Api.Services.Assistant.ConfirmationTokenService.CreateEphemeral(), new ConfigurationBuilder().Build(), new FixedClock(now));
            return new OutputDescriber(db ?? W.Db, Storage, links, Mock.Of<IPublishEndpoint>());
        }
    }

    private static readonly Which[] Withheld = [Which.EarlierRender, Which.EarlierFile];
    private static readonly Which[] Kept = [Which.LaterRender, Which.LaterFile, Which.Upload];
    public static IEnumerable<object[]> WithheldCases() { foreach (Which w in Withheld) yield return [w]; }
    public static IEnumerable<object[]> KeptCases() { foreach (Which w in Kept) yield return [w]; }
    public static IEnumerable<object[]> ServedCases()
    {
        foreach (Which w in Enum.GetValues<Which>())
        {
            yield return [w, SurfaceState.Clear];
            yield return [w, SurfaceState.ModerationOnly];
        }
    }

    [Theory]
    [MemberData(nameof(WithheldCases))]
    public async Task An_earlier_output_is_absent_for_the_lookup_and_the_description(Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Describer().FindSourceAsync(r.W.Project.Id, r.IdOf(which), CancellationToken.None)).Should().BeNull();
        (await r.Describer().DescribeAsync(r.W.Project, r.IdOf(which), r.W.UserId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_earlier_render_is_absent_for_the_render_check()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Describer().FindRenderAsync(r.W.Project.Id, r.W.EarlierResult.Id, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(KeptCases))]
    public async Task A_later_output_and_an_upload_are_found_and_described_while_earlier_outputs_are_kept(Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Describer().FindSourceAsync(r.W.Project.Id, r.IdOf(which), CancellationToken.None)).Should().NotBeNull();
        (await r.Describer().DescribeAsync(r.W.Project, r.IdOf(which), r.W.UserId, CancellationToken.None)).Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(ServedCases))]
    public async Task Every_output_is_found_and_described_when_the_project_keeps_nothing(Which which, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        (await r.Describer().FindSourceAsync(r.W.Project.Id, r.IdOf(which), CancellationToken.None)).Should().NotBeNull();
        (await r.Describer().DescribeAsync(r.W.Project, r.IdOf(which), r.W.UserId, CancellationToken.None)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(Which.LaterRender)]
    [InlineData(Which.LaterFile)]
    public async Task A_failed_read_makes_an_output_absent(Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        (await r.Describer(failing).FindSourceAsync(r.W.Project.Id, r.IdOf(which), CancellationToken.None)).Should().BeNull();
        (await r.Describer(failing).DescribeAsync(r.W.Project, r.IdOf(which), r.W.UserId, CancellationToken.None)).Should().BeNull();
    }
}
