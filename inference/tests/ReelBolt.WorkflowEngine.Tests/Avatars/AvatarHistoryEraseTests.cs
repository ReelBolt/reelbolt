#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>Deleting an avatar's reference and consent files also erases their history copies.</summary>
public class AvatarHistoryEraseTests
{
    private sealed class RecordingEraser : IProjectHistoryEraser
    {
        public List<Guid> Erased { get; } = [];

        public Task EraseFileAsync(Guid projectId, Guid fileId, CancellationToken ct)
        {
            Erased.Add(fileId);
            return Task.CompletedTask;
        }

        public Task DeleteProjectObjectsAsync(Guid projectId, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Deleting_an_avatar_erases_the_history_of_each_reference_file_it_removes()
    {
        var h = new H { HistoryEraser = new RecordingEraser() };
        ProjectFile face = h.AddImage("anna-face.png", salt: 1);
        Guid avatarId = await h.CreateReadyAsync("Anna", face);

        using InferenceApiDbContext db = h.NewDb();
        AvatarDeleteReport? report = await h.Deletion(db).DeleteAsync(
            h.Project.Id, avatarId, h.OrganizationId, h.OwnerId, AvatarDeleteCause.User, CancellationToken.None);

        report.Should().NotBeNull();
        ((RecordingEraser)h.HistoryEraser!).Erased.Should().Contain(face.Id);
    }
}
