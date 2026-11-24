#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane L6 of docs/design/avatar-objects.md: <c>GET .../avatars/{id}/usage</c> also counts the pictures that were
/// generated or edited WITH the avatar (layers whose content names it), and deleting the avatar leaves those layers.
/// </summary>
public class AvatarPictureUsageTests
{
    private static string Shows(params (Guid Id, string Name, string Kind)[] subjects) => JsonSerializer.Serialize(new
    {
        subjects = subjects.Select(s => new { avatarId = s.Id, name = s.Name, kind = s.Kind }),
        aiGenerated = true,
        disclosure = "Visible"
    });

    private static Guid AddPicture(H h, Guid projectId, params string?[] layerContent)
    {
        using InferenceApiDbContext db = h.NewDb();
        EditImageDocument document = new() { Id = Guid.NewGuid(), ProjectId = projectId, Name = "Picture", Revision = 1 };
        db.EditImageDocuments.Add(document);
        int z = 1;
        foreach (string? content in layerContent)
        {
            db.EditImageLayers.Add(new EditImageLayer
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                ZOrder = z++,
                Kind = EditImageLayerKind.Generated,
                Role = EditImageLayerRoles.Background,
                Label = "Generated",
                StorageKey = $"projects/{projectId}/agentFiles/{Guid.NewGuid():N}.png",
                StorageBucket = "test-bucket",
                MimeType = "image/png",
                TransformJson = "{}",
                ContentJson = content,
                ProvenanceJson = "{\"operation\":\"generate\",\"prompt\":\"my subjects\"}"
            });
        }

        db.SaveChanges();
        return document.Id;
    }

    [Fact]
    public async Task Usage_counts_the_pictures_generated_with_the_avatar_and_deleting_it_leaves_their_layers()
    {
        var h = new H();
        h.Options.Objects.Enabled = true;
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid machine = await h.CreateObjectAsync("Espresso machine");

        Guid both = AddPicture(h, h.Project.Id,
            Shows((anna, "Anna", "Person"), (machine, "Espresso machine", "Object")),
            Shows((anna, "Anna", "Person")),   // a second layer of the same picture: one picture
            null);                             // an ordinary AI layer
        AddPicture(h, h.Project.Id, Shows((anna, "Anna", "Person")));
        AddPicture(h, h.Project.Id, Shows((machine, "Espresso machine", "Object")));
        AddPicture(h, h.Project.Id, null, null); // names nobody (its provenance says "subjects" in a prompt, which is not the list)

        // another project's picture that happens to carry the same id is not this project's usage
        Guid otherProject = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project { Id = otherProject, Name = "Other", OwnerId = h.OwnerId, OrganizationId = h.OrganizationId });
            await db.SaveChangesAsync();
        }

        AddPicture(h, otherProject, Shows((anna, "Anna", "Person")));

        (int status, JsonElement annaUsage) = H.Read(await h.Controller().Usage(h.Project.Id, anna, CancellationToken.None));
        status.Should().Be(200);
        annaUsage.GetProperty("picturesGenerated").GetInt32().Should().Be(2, "counted per picture, like the pictures that use a reference photo");
        annaUsage.GetProperty("picturesUsingReferences").GetInt32().Should().Be(0);

        (_, JsonElement machineUsage) = H.Read(await h.Controller().Usage(h.Project.Id, machine, CancellationToken.None));
        machineUsage.GetProperty("picturesGenerated").GetInt32().Should().Be(2);

        // Deleting the avatar leaves the generated layers, as generated clips stay.
        (int deleted, _) = H.Read(await h.Controller().Delete(h.Project.Id, anna, CancellationToken.None));
        deleted.Should().Be(200);
        using InferenceApiDbContext after = h.NewDb();
        (await after.EditImageLayers.CountAsync(l => l.DocumentId == both)).Should().Be(3);
        (await after.EditImageLayers.Where(l => l.DocumentId == both).Select(l => l.ContentJson).ToListAsync())
            .Count(p => p is not null && p.Contains(anna.ToString(), StringComparison.Ordinal)).Should().Be(2, "the layers still say who they show");
    }
}
