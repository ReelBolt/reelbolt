using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

// The picture half of GET .../avatars/{id}/usage (docs/design/avatar-objects.md "Avatars and objects in the picture
// editor"): pictures that were generated or edited WITH the avatar, next to the pictures that use one of its
// reference photos as a layer.
public partial class ProjectAvatarsController
{
    /// <summary>The most layers of one project read for the count; a project with more counts the newest ones.</summary>
    internal const int MaxUsageLayersRead = 5000;

    /// <summary>
    /// How many pictures of the project have a layer whose content names <paramref name="avatarId"/> among its
    /// subjects (<see cref="PictureSubjectFacts"/>). Counted per picture, like <c>PicturesUsingReferences</c>. The
    /// layers stay when the avatar is deleted, as generated clips do; they are then simply no longer counted for it.
    /// </summary>
    private async Task<int> PicturesGeneratedWithAsync(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        // The facts live inside the layer's content JSON, which is read here rather than queried: among bitmap layers
        // only the ones made with subjects carry one, and the newest of them are enough for a count shown to a person.
        var layers = await _db.EditImageLayers.AsNoTracking()
            .Where(l => l.ContentJson != null && l.Kind != EditImageLayerKind.Text)
            .Join(_db.EditImageDocuments.AsNoTracking().Where(d => d.ProjectId == projectId),
                l => l.DocumentId, d => d.Id, (l, d) => new { d.Id, l.ContentJson, l.CreatedAt })
            .OrderByDescending(l => l.CreatedAt)
            .Take(MaxUsageLayersRead)
            .ToListAsync(ct);

        return layers
            .Where(l => PictureSubjectFacts.Names(l.ContentJson, avatarId))
            .Select(l => l.Id)
            .Distinct()
            .Count();
    }
}
