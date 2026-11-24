using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Graphics;

/// <summary>What a finished graphic design produced, recorded on its request row for the API to settle.</summary>
public sealed record GraphicDesignResult(
    string StorageKey,
    int Width,
    int Height,
    int OffsetX,
    int OffsetY,
    Guid? WorkspaceId,
    Guid? VersionId,
    int? VersionNumber,
    string CompositionId,
    string? Summary,
    string? ModelName);

/// <summary>
/// The engine's view of <c>edit_image_graphic_requests</c> (owned by the Inference API, mapped here with
/// <c>ExcludeFromMigrations()</c>): read a request by its execution, and write the result columns. The
/// engine never writes anything else — the layer itself is the API's to insert.
/// </summary>
public interface IGraphicDesignRequestStore
{
    Task<EditImageGraphicRequest?> FindByExecutionAsync(Guid executionId, CancellationToken ct);

    Task RecordResultAsync(Guid requestId, GraphicDesignResult result, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GraphicDesignRequestStore : IGraphicDesignRequestStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public GraphicDesignRequestStore(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<EditImageGraphicRequest?> FindByExecutionAsync(Guid executionId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await db.EditImageGraphicRequests.AsNoTracking().FirstOrDefaultAsync(r => r.ExecutionId == executionId, ct);
    }

    public async Task RecordResultAsync(Guid requestId, GraphicDesignResult result, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        EditImageGraphicRequest? row = await db.EditImageGraphicRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (row is null)
            return;

        row.ResultStorageKey = result.StorageKey;
        row.ResultWidth = result.Width;
        row.ResultHeight = result.Height;
        row.ResultOffsetX = result.OffsetX;
        row.ResultOffsetY = result.OffsetY;
        row.ResultWorkspaceId = result.WorkspaceId;
        row.ResultVersionId = result.VersionId;
        row.ResultVersionNumber = result.VersionNumber;
        row.ResultCompositionId = result.CompositionId.Length > 200 ? result.CompositionId[..200] : result.CompositionId;
        row.ResultSummary = result.Summary is { Length: > 1000 } s ? s[..1000] : result.Summary;
        row.ResultModelName = result.ModelName is { Length: > 200 } m ? m[..200] : result.ModelName;
        row.ResultRecordedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
