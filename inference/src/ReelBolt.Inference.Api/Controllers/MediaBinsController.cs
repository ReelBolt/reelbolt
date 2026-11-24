using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Owner-scoped CRUD for media bins and bin membership — the bins half of the editor's media
/// panel. A bin is a user-created folder inside one project (<c>media_bins</c>); an item is a pin
/// from a bin to media that already exists elsewhere in the same project's pool
/// (<c>media_bin_items</c>).
///
/// <para>
/// <b>Tenancy is the outer gate; project scoping is the inner one.</b> Every route resolves the
/// project through <c>IProjectAccessService</c> first (docs/design/tenancy.md: a project in another
/// organization, or none at all, is <c>404</c> so existence never leaks across tenants; a caller in
/// the project's own organization whose role may not act is <c>403</c>, which reveals nothing a
/// member did not already know) and then constrains every <c>{binId}</c>/<c>{itemId}</c> lookup by
/// <c>ProjectId</c> as well, so a bin from another project is indistinguishable from a nonexistent
/// one. Reads need <c>View</c>; creating, renaming, moving and deleting bins or pins need
/// <c>Edit</c>.
/// </para>
/// <para>
/// <b>Below the project gate, 404 is the only answer.</b> A foreign, nonexistent or soft-deleted
/// bin, item or media reference is <c>404</c>, never <c>403</c>: a <c>403</c> for a bin id would
/// confirm that the row exists in some other project, which is a fact the caller has no need for,
/// and one uniform not-found path is the one to audit for a cross-project leak. Any new route on
/// this controller must keep that property.
/// </para>
/// <para>
/// <b>Cycles are prevented here, never by the database.</b> <c>media_bins.parent_bin_id</c> is a
/// self-reference mapped with <c>DeleteBehavior.Restrict</c>, which rejects nothing about a cycle —
/// a stored cycle would simply make every ancestor walk loop forever. Reparenting therefore walks
/// <c>ParentBinId</c> upward from the proposed parent with an explicit visited set and a hard
/// <see cref="MaxBinDepth"/>-step cap, and answers <c>400</c> when the bin being moved is reached.
/// Every other walk in this file (depth, subtree height, subtree collection) carries the same
/// visited set and cap, so no malformed row can turn a request into an unbounded loop.
/// </para>
/// <para>
/// <b>Deleting is asymmetric on purpose.</b> A bin delete soft-deletes the bin and its whole
/// subtree via <c>deleted_at</c>, but <b>hard-deletes their items</b>: <c>media_bin_items</c> has
/// no <c>deleted_at</c> column, and a pin is not content — removing it is a row delete. The subtree
/// walk is explicit and bounded rather than a database cascade, because a self-cascade would delete
/// an unbounded subtree from one row delete.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/media/bins")]
[Authorize]
public class MediaBinsController : ControllerBase
{
    /// <summary>
    /// The maximum nesting depth of the bin tree, counting a root bin as depth 1. Also the hard cap
    /// on every ancestor/subtree walk in this file.
    /// </summary>
    /// <remarks>
    /// A named constant rather than a literal because three separate checks compare against it —
    /// create, reparent (new parent's depth plus the moved subtree's height) and the walks' own
    /// bounds — and those must agree. 16 levels is far past any real media panel (a viewer
    /// organising footage by shoot/day/take is at 3) while keeping every walk's cost a small
    /// constant.
    /// </remarks>
    public const int MaxBinDepth = 16;

    /// <summary>Maximum length of <c>MediaBin.Name</c>, after trimming. Mirrors the column.</summary>
    private const int MaxBinNameLength = 120;

    /// <summary>The <c>file:</c> mediaRef prefix; the payload is a <c>ProjectFile</c> id.</summary>
    private const string FileMediaRefPrefix = "file:";

    /// <summary>The <c>step_result:</c> mediaRef prefix; the payload is a <c>WorkflowStepResult</c> id.</summary>
    private const string StepResultMediaRefPrefix = "step_result:";

    /// <summary>Named error: <c>name</c> was absent or blank.</summary>
    /// <summary>P6: a pin was addressed to a smart bin, whose items are computed and never pinned.</summary>
    public const string SmartBinReadOnlyError = "smartBinReadOnly";

    /// <summary>P6: the smart filter did not validate.</summary>
    public const string InvalidSmartFilterError = "invalidSmartFilter";

    public const string BinNameRequiredError = "binNameRequired";

    /// <summary>Named error: the trimmed <c>name</c> is longer than <see cref="MaxBinNameLength"/>.</summary>
    public const string BinNameTooLongError = "binNameTooLong";

    /// <summary>Named error: the operation would nest a bin deeper than <see cref="MaxBinDepth"/>.</summary>
    public const string BinDepthExceededError = "binDepthExceeded";

    /// <summary>
    /// Named error: the requested reparent would place a bin inside itself or inside one of its own
    /// descendants.
    /// </summary>
    public const string BinHierarchyCycleError = "binHierarchyCycle";

    /// <summary>
    /// Named error: the stored bin hierarchy is already unusable — a cycle, or deeper than
    /// <see cref="MaxBinDepth"/>. Distinguished from <see cref="BinHierarchyCycleError"/> so a
    /// report can tell "your request would create a cycle" from "this project's bins are corrupt".
    /// </summary>
    public const string BinHierarchyInvalidError = "binHierarchyInvalid";

    /// <summary>Named error: <c>parentBinId</c> was present but was not a guid and not null.</summary>
    public const string InvalidParentBinIdError = "invalidParentBinId";

    /// <summary>
    /// Named error: the mediaRef token is malformed — it is neither <c>file:&lt;guid&gt;</c> nor
    /// <c>step_result:&lt;guid&gt;</c>. A well-formed token that resolves to nothing in this project
    /// is a <c>404</c> instead.
    /// </summary>
    public const string InvalidMediaRefError = "invalidMediaRef";

    /// <summary>Named error: the move request carried no <c>binId</c>.</summary>
    public const string InvalidBinIdError = "invalidBinId";

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;

    public MediaBinsController(InferenceApiDbContext db, IProjectAccessService projectAccess)
    {
        _db = db;
        _projectAccess = projectAccess;
    }

    /// <summary>
    /// Lists every live bin of the project, with its item count.
    /// </summary>
    /// <remarks>
    /// Ordering is a total order — <c>sort_order</c>, then <c>name</c>, then <c>id</c> — so the
    /// response is stable between identical requests even when two bins of different parents share
    /// a sort order. The client rebuilds the tree from <c>parentBinId</c>; this endpoint never
    /// nests.
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<List<MediaBinSummaryResponse>>> ListBins(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        // The soft-delete query filter on MediaBin already removes deleted rows here, so no
        // explicit DeletedAt predicate is needed — or wanted, since a second one could drift.
        List<MediaBin> bins = await LoadBinTreeAsync(projectId, ct);
        List<Guid> binIds = bins.Select(b => b.Id).ToList();

        Dictionary<Guid, int> itemCounts = await _db.MediaBinItems
            .Where(i => binIds.Contains(i.BinId))
            .GroupBy(i => i.BinId)
            .Select(g => new { BinId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(row => row.BinId, row => row.Count, ct);

        List<MediaBinSummaryResponse> response = bins
            .Select(bin => ToSummaryResponse(
                bin,
                itemCounts.TryGetValue(bin.Id, out int count) ? count : 0))
            .ToList();

        return Ok(response);
    }

    /// <summary>
    /// Creates a bin, at the root or inside an existing bin of this project.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MediaBinSummaryResponse>> CreateBin(
        Guid projectId,
        [FromBody] CreateMediaBinRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (!TryNormalizeBinName(request.Name, out string name, out string nameError))
            return BadRequest(new { error = nameError, message = NameErrorMessage });

        List<MediaBin> bins = await LoadBinTreeAsync(projectId, ct);

        if (request.ParentBinId is { } parentBinId && bins.All(b => b.Id != parentBinId))
            return NotFound();

        Dictionary<Guid, MediaBin> byId = bins.ToDictionary(b => b.Id);
        int? parentDepth = DepthOf(byId, request.ParentBinId);
        if (parentDepth is null)
            return BadRequest(new { error = BinHierarchyInvalidError, message = HierarchyInvalidMessage });

        // A root bin is depth 1, so a child of a depth-N parent is depth N+1.
        if (parentDepth.Value + 1 > MaxBinDepth)
        {
            return BadRequest(new
            {
                error = BinDepthExceededError,
                message = $"Bins may not nest deeper than {MaxBinDepth} levels."
            });
        }

        string? smartFilterJson = null;
        if (request.SmartFilter is { } filter)
        {
            if (filter.Validate() is { } filterError)
                return BadRequest(new { error = InvalidSmartFilterError, message = filterError });
            smartFilterJson = filter.ToJson();
        }

        DateTime now = DateTime.UtcNow;
        MediaBin bin = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            ParentBinId = request.ParentBinId,
            SortOrder = NextSortOrder(bins, request.ParentBinId),
            SmartFilterJson = smartFilterJson,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.MediaBins.Add(bin);
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, ToSummaryResponse(bin, itemCount: 0));
    }

    /// <summary>
    /// Renames and/or reparents a bin.
    /// </summary>
    /// <remarks>
    /// A reparent appends the bin to the end of its new parent's children; a pure rename never
    /// touches <c>sort_order</c>, so renaming never reorders the panel.
    /// </remarks>
    [HttpPatch("{binId:guid}")]
    public async Task<ActionResult<MediaBinSummaryResponse>> PatchBin(
        Guid projectId,
        Guid binId,
        [FromBody] PatchMediaBinRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        MediaBin? bin = await LoadOwnedBinAsync(projectId, binId, ct);
        if (bin is null) return NotFound();

        string name = bin.Name;
        if (request.Name is not null && !TryNormalizeBinName(request.Name, out name, out string nameError))
            return BadRequest(new { error = nameError, message = NameErrorMessage });

        Guid? originalParentId = bin.ParentBinId;
        Guid? newParentId = originalParentId;
        if (request.HasParentBinId)
        {
            if (!request.TryGetParentBinId(out Guid? parsedParent))
            {
                return BadRequest(new
                {
                    error = InvalidParentBinIdError,
                    message = "parentBinId must be a bin guid or null."
                });
            }

            newParentId = parsedParent;
        }

        List<MediaBin> bins = await LoadBinTreeAsync(projectId, ct);
        Dictionary<Guid, MediaBin> byId = bins.ToDictionary(b => b.Id);

        if (newParentId is { } targetParentId)
        {
            if (!byId.ContainsKey(targetParentId)) return NotFound();

            // Cycle prevention. Walk UP from the proposed parent with an explicit visited set and a
            // hard MaxBinDepth-step cap: if the bin being moved is reached, the new parent is the
            // bin itself or one of its own descendants, and the write is refused. The database
            // cannot do this — parent_bin_id is a Restrict self-FK, which says nothing about a
            // cycle, and a stored cycle would make this very walk (and every descendant walk)
            // non-terminating.
            HashSet<Guid> visited = [];
            Guid? cursor = targetParentId;
            int steps = 0;

            while (cursor is { } current)
            {
                if (current == binId)
                {
                    return BadRequest(new
                    {
                        error = BinHierarchyCycleError,
                        message = "A bin cannot be moved into itself or into one of its own descendants."
                    });
                }

                if (!visited.Add(current))
                    return BadRequest(new { error = BinHierarchyInvalidError, message = HierarchyInvalidMessage });

                steps++;
                if (steps > MaxBinDepth)
                    return BadRequest(new { error = BinHierarchyInvalidError, message = HierarchyInvalidMessage });

                cursor = byId.TryGetValue(current, out MediaBin? node) ? node.ParentBinId : null;
            }
        }

        int? newParentDepth = DepthOf(byId, newParentId);
        if (newParentDepth is null)
            return BadRequest(new { error = BinHierarchyInvalidError, message = HierarchyInvalidMessage });

        // The whole subtree keeps its shape, so the bound has to hold for its DEEPEST descendant,
        // not just for the moved bin: height 0 means the bin is a leaf.
        int? heightBelow = SubtreeHeight(byId, binId);
        if (heightBelow is null)
            return BadRequest(new { error = BinHierarchyInvalidError, message = HierarchyInvalidMessage });

        if (newParentDepth.Value + 1 + heightBelow.Value > MaxBinDepth)
        {
            return BadRequest(new
            {
                error = BinDepthExceededError,
                message = $"Bins may not nest deeper than {MaxBinDepth} levels."
            });
        }

        if (request.HasSmartFilter)
        {
            if (request.SmartFilter.ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                bin.SmartFilterJson = null;
            }
            else
            {
                MediaSmartFilter? filter = MediaSmartFilter.Parse(request.SmartFilter.GetRawText());
                if (filter is null)
                    return BadRequest(new { error = InvalidSmartFilterError, message = "smartFilter must be a filter object or null." });
                if (filter.Validate() is { } filterError)
                    return BadRequest(new { error = InvalidSmartFilterError, message = filterError });
                bin.SmartFilterJson = filter.ToJson();
            }
        }

        bin.Name = name;
        bin.ParentBinId = newParentId;
        if (newParentId != originalParentId)
            bin.SortOrder = NextSortOrder(bins, newParentId);
        bin.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        int itemCount = await _db.MediaBinItems.CountAsync(i => i.BinId == binId, ct);
        return Ok(ToSummaryResponse(bin, itemCount));
    }

    /// <summary>
    /// Soft-deletes a bin and its whole subtree, and hard-deletes their items.
    /// </summary>
    /// <remarks>
    /// The item rows are removed, not soft-deleted, because <c>media_bin_items</c> has no
    /// <c>deleted_at</c> column: a pin is a UI placement over media owned elsewhere, so removing it
    /// is a row delete and nothing is lost. The referenced media itself is never touched.
    /// </remarks>
    [HttpDelete("{binId:guid}")]
    public async Task<IActionResult> DeleteBin(Guid projectId, Guid binId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        MediaBin? bin = await LoadOwnedBinAsync(projectId, binId, ct);
        if (bin is null) return NotFound();

        List<MediaBin> bins = await LoadBinTreeAsync(projectId, ct);
        List<Guid> subtreeIds = CollectSubtreeIds(bins, binId);

        // HARD delete: media_bin_items has deliberately no soft-delete column, and the bin items
        // cascade FK only fires on a physical bin delete (which soft-delete avoids), so the child
        // rows are removed explicitly here.
        List<MediaBinItem> items = await _db.MediaBinItems
            .Where(i => subtreeIds.Contains(i.BinId))
            .ToListAsync(ct);
        _db.MediaBinItems.RemoveRange(items);

        DateTime now = DateTime.UtcNow;
        foreach (MediaBin descendant in bins)
        {
            if (!subtreeIds.Contains(descendant.Id)) continue;
            descendant.DeletedAt = now;
            descendant.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// Lists the pins of one bin, each enriched with the four fields of the source it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes removal reachable.</b> A pin's <c>itemId</c> exists nowhere else the
    /// client can see, so without this route the panel could add a clip to a bin and then never show
    /// or remove it. The four resolved fields ride along on the same object so a bin view renders
    /// without a second client-side join — and deliberately without a <c>probe</c> field: probe
    /// metadata has exactly one producer (the pool payload), and a second one could disagree with
    /// it.
    /// </para>
    /// <para>
    /// <b>Ordered by <c>SortOrder</c>, then <c>Id</c>.</b> The stored order is the panel's order, and
    /// the id tie-break makes the response a total order: two pins sharing a sort order (which two
    /// concurrent adds can produce, since the next order is computed from a read) still come back in
    /// one stable sequence instead of in whatever order the store happened to return.
    /// </para>
    /// <para>
    /// <b>A dangling pin is returned, with all four resolved fields <c>null</c>.</b> A pin is a
    /// pointer, not a copy: the source can be deleted, belong to another project, or (for a render)
    /// no longer sit under this project's <c>outputFiles/</c> prefix, and the pin survives all of
    /// that by design. Dropping such a row would strand it — the client could never learn its
    /// <c>itemId</c> and so could never remove it — and substituting a default, an empty string or a
    /// zero size would state something about the source that is not known to be true. <c>null</c> is
    /// the only honest answer, and it is exactly what a client needs to render "this clip is gone".
    /// </para>
    /// <para>
    /// <b>An empty bin is <c>200</c> with an empty list</b>, never a <c>404</c>: an empty bin exists
    /// and is a state the panel displays. Access and project scoping follow the file's uniform
    /// rule — the project goes through the access service, and a missing, soft-deleted or foreign
    /// bin is <c>404</c>, never a <c>403</c>.
    /// </para>
    /// <para>
    /// No field of the response can carry a storage key, a bucket name or an object URL: a file name
    /// is a base name, and a step result's fields are derived from its object's key without ever
    /// exposing it.
    /// </para>
    /// </remarks>
    [HttpGet("{binId:guid}/items")]
    public async Task<ActionResult<List<MediaBinItemDetailResponse>>> ListItems(
        Guid projectId,
        Guid binId,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        MediaBin? bin = await LoadOwnedBinAsync(projectId, binId, ct);
        if (bin is null) return NotFound();

        if (MediaSmartFilter.Parse(bin.SmartFilterJson) is { } smartFilter)
        {
            return Ok(await ListSmartItemsAsync(projectId, bin, smartFilter, ct));
        }

        List<MediaBinItem> items = await _db.MediaBinItems
            .Where(i => i.BinId == binId)
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Id)
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            return Ok(new List<MediaBinItemDetailResponse>());
        }

        // Two lookups for the whole bin, rather than two per row: the pinned sources of each kind are
        // fetched in one project-scoped IN clause and the mapping then reads them from memory. Both
        // are project-scoped at the database, so a pin that names another project's media resolves
        // to nothing here exactly as a deleted one does.
        List<Guid> fileSourceIds = items
            .Where(i => i.SourceKind == MediaSourceKind.ProjectFile)
            .Select(SourceIdOf)
            .Distinct()
            .ToList();

        List<Guid> stepResultSourceIds = items
            .Where(i => i.SourceKind == MediaSourceKind.StepResult)
            .Select(SourceIdOf)
            .Distinct()
            .ToList();

        Dictionary<Guid, ProjectFile> files = await LoadFileSourcesAsync(projectId, fileSourceIds, ct);
        Dictionary<Guid, WorkflowStepResult> renders = await LoadStepResultSourcesAsync(projectId, stepResultSourceIds, ct);

        // A source whose bytes are not served resolves like a missing one.
        foreach (Guid held in await Services.Outputs.OutputGate.HeldFileIdsAsync(_db, projectId, files.Values.ToList(), ct)) files.Remove(held);
        foreach (Guid held in await Services.Outputs.OutputGate.HeldResultIdsAsync(_db, projectId, renders.Keys.ToList(), ct)) renders.Remove(held);

        List<MediaBinItemDetailResponse> rows = new(items.Count);

        foreach (MediaBinItem item in items)
        {
            Guid sourceId = SourceIdOf(item);

            MediaSourceFields source = item.SourceKind == MediaSourceKind.ProjectFile
                ? DescribeFileSource(files.TryGetValue(sourceId, out ProjectFile? file) ? file : null)
                : DescribeStepResultSource(renders.TryGetValue(sourceId, out WorkflowStepResult? render) ? render : null, projectId);

            rows.Add(ToDetailResponse(item, source));
        }

        return Ok(rows);
    }

    /// <summary>
    /// Adds a pool mediaRef to a bin.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent by media reference.</b> Adding the same mediaRef to the same bin a second time
    /// returns the existing pin with <c>200</c> instead of creating a second row — the panel's
    /// "add to bin" is a toggle-shaped action, and a duplicate row would render twice and then
    /// delete only once. A NEW pin is <c>201</c>, so the two outcomes stay distinguishable.
    /// </remarks>
    [HttpPost("{binId:guid}/items")]
    public async Task<ActionResult<MediaBinItemResponse>> AddItem(
        Guid projectId,
        Guid binId,
        [FromBody] AddMediaBinItemRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        MediaBin? bin = await LoadOwnedBinAsync(projectId, binId, ct);
        if (bin is null) return NotFound();

        if (bin.SmartFilterJson is not null)
        {
            return BadRequest(new { error = SmartBinReadOnlyError, message = "A smart bin's items are computed from its filter; pin the source to an ordinary bin instead." });
        }

        // Malformed first (400, nothing looked up), then unresolvable (404, nothing written): the
        // two failures are different problems and the client acts differently on each.
        if (!TryParseMediaRef(request.MediaRef, out MediaSourceKind sourceKind, out Guid sourceId))
        {
            return BadRequest(new
            {
                error = InvalidMediaRefError,
                message = "mediaRef must be 'file:<guid>' or 'step_result:<guid>'."
            });
        }

        if (!await ResolveMediaRefAsync(projectId, sourceKind, sourceId, ct))
            return NotFound();

        MediaBinItem? existing = await FindItemAsync(binId, sourceKind, sourceId, ct);
        if (existing is not null)
            return Ok(ToItemResponse(existing));

        MediaBinItem item = new()
        {
            Id = Guid.NewGuid(),
            BinId = binId,
            SourceKind = sourceKind,
            SourceProjectFileId = sourceKind == MediaSourceKind.ProjectFile ? sourceId : null,
            SourceStepResultId = sourceKind == MediaSourceKind.StepResult ? sourceId : null,
            SortOrder = await NextItemSortOrderAsync(binId, ct),
            CreatedAt = DateTime.UtcNow
        };

        _db.MediaBinItems.Add(item);
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, ToItemResponse(item));
    }

    /// <summary>
    /// Moves a bin item to another bin of the same project.
    /// </summary>
    /// <remarks>
    /// If the target bin already holds the same media reference, the moved row is removed and the
    /// existing pin returned — a move must not be the one way a duplicate row can appear.
    /// </remarks>
    [HttpPatch("{binId:guid}/items/{itemId:guid}")]
    public async Task<ActionResult<MediaBinItemResponse>> MoveItem(
        Guid projectId,
        Guid binId,
        Guid itemId,
        [FromBody] MoveMediaBinItemRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (request.BinId is not { } targetBinId)
            return BadRequest(new { error = InvalidBinIdError, message = "binId is required." });

        MediaBin? sourceBin = await LoadOwnedBinAsync(projectId, binId, ct);
        if (sourceBin is null) return NotFound();

        // Scoped by ProjectId like every other bin lookup, so a bin of another project is 404 here
        // and cannot be used to smuggle an item across projects.
        MediaBin? targetBin = await LoadOwnedBinAsync(projectId, targetBinId, ct);
        if (targetBin is null) return NotFound();

        MediaBinItem? item = await _db.MediaBinItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.BinId == binId, ct);
        if (item is null) return NotFound();

        if (targetBinId == binId)
            return Ok(ToItemResponse(item));

        Guid sourceId = SourceIdOf(item);

        MediaBinItem? duplicate = await FindItemAsync(targetBinId, item.SourceKind, sourceId, ct);
        if (duplicate is not null)
        {
            _db.MediaBinItems.Remove(item);
            await _db.SaveChangesAsync(ct);
            return Ok(ToItemResponse(duplicate));
        }

        item.BinId = targetBinId;
        item.SortOrder = await NextItemSortOrderAsync(targetBinId, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(ToItemResponse(item));
    }

    /// <summary>
    /// Removes a bin item. Hard delete; the referenced media is untouched.
    /// </summary>
    [HttpDelete("{binId:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> DeleteItem(Guid projectId, Guid binId, Guid itemId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        MediaBin? bin = await LoadOwnedBinAsync(projectId, binId, ct);
        if (bin is null) return NotFound();

        MediaBinItem? item = await _db.MediaBinItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.BinId == binId, ct);
        if (item is null) return NotFound();

        _db.MediaBinItems.Remove(item);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // --- Shared checks ---------------------------------------------------------------------

    /// <summary>
    /// A live bin of this project, or null. Constrained by <c>ProjectId</c> as well as id, so a bin
    /// from another project is exactly as absent as a nonexistent one; the soft-delete query filter
    /// on <see cref="MediaBin"/> covers the third case. Callers must have loaded the project first.
    /// </summary>
    private async Task<MediaBin?> LoadOwnedBinAsync(Guid projectId, Guid binId, CancellationToken ct) =>
        await _db.MediaBins.FirstOrDefaultAsync(b => b.Id == binId && b.ProjectId == projectId, ct);

    /// <summary>
    /// Every live bin of the project, in the deterministic order the list endpoint reports.
    /// Materialized once per request and walked in memory: the tree is bounded by
    /// <see cref="MaxBinDepth"/> and by the number of user-created folders, so an in-memory walk is
    /// both cheaper and easier to bound than a recursive SQL query.
    /// </summary>
    private async Task<List<MediaBin>> LoadBinTreeAsync(Guid projectId, CancellationToken ct) =>
        await _db.MediaBins
            .Where(b => b.ProjectId == projectId)
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Name)
            .ThenBy(b => b.Id)
            .ToListAsync(ct);

    /// <summary>
    /// True when <paramref name="sourceId"/> resolves to media that belongs to <paramref name="projectId"/>.
    /// </summary>
    /// <remarks>
    /// This is the check that actually prevents a cross-project leak. <c>media_bin_items</c> has no
    /// ownership FK on either source column — deliberately, since a step result is deletable and a
    /// pin must not hold it hostage — so nothing but this lookup keeps a bin in project A from
    /// pinning project B's render. A row that does not exist, or exists in another project, is
    /// simply false, and the caller answers <c>404</c> with nothing persisted.
    /// </remarks>
    private async Task<bool> ResolveMediaRefAsync(
        Guid projectId,
        MediaSourceKind sourceKind,
        Guid sourceId,
        CancellationToken ct)
    {
        if (sourceKind == MediaSourceKind.ProjectFile)
            return await _db.ProjectFiles.AnyAsync(
                f => f.Id == sourceId && f.ProjectId == projectId
                     && f.CsamStatus != CsamScanStatus.Quarantined, ct);

        // A WorkflowStepResult has no ProjectId of its own; its project is the owning execution's.
        return await _db.WorkflowStepResults
            .AnyAsync(r => r.Id == sourceId && r.WorkflowExecution.ProjectId == projectId, ct);
    }

    // --- Source enrichment (the listing's read-time resolution) --------------------------------

    /// <summary>
    /// The pinned <c>ProjectFile</c> sources of this project, by id — one project-scoped query for a
    /// whole bin rather than one per row.
    /// </summary>
    /// <remarks>
    /// <c>ProjectFile</c> carries no soft-delete column, so "deleted" and "never existed" are the
    /// same absent row here. A row of another project never comes back at all, which is the check
    /// that keeps a leaked pin from rendering another project's file name and size.
    /// </remarks>
    private async Task<Dictionary<Guid, ProjectFile>> LoadFileSourcesAsync(
        Guid projectId,
        List<Guid> sourceIds,
        CancellationToken ct) =>
        sourceIds.Count == 0
            ? []
            : await _db.ProjectFiles
                .Where(f => f.ProjectId == projectId && sourceIds.Contains(f.Id)
                            && f.CsamStatus != CsamScanStatus.Quarantined)
                .ToDictionaryAsync(f => f.Id, ct);

    /// <summary>
    /// The pinned <c>WorkflowStepResult</c> render sources of this project, by id. Project-scoped
    /// through the owning execution, since a step result has no <c>ProjectId</c> of its own.
    /// </summary>
    private async Task<Dictionary<Guid, WorkflowStepResult>> LoadStepResultSourcesAsync(
        Guid projectId,
        List<Guid> sourceIds,
        CancellationToken ct) =>
        sourceIds.Count == 0
            ? []
            : await _db.WorkflowStepResults
                .Where(r => r.WorkflowExecution.ProjectId == projectId && sourceIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, ct);

    /// <summary>
    /// The four resolved fields of a pinned <c>ProjectFile</c>, or all-null when the row is gone.
    /// </summary>
    /// <remarks>
    /// Every value is read from the row itself — the name the user sees, the MIME type it was
    /// uploaded with, the size that was recorded — and never derived from the storage key except for
    /// the file-name fallback <c>MediaController</c> uses when a row has no original name. Nothing
    /// here is invented: a file with no MIME type reports <c>null</c> for both it and the kind it
    /// would imply, rather than guessing from the extension.
    /// </remarks>
    private static MediaSourceFields DescribeFileSource(ProjectFile? file)
    {
        if (file is null) return MediaSourceFields.Dangling;

        string? mimeType = string.IsNullOrWhiteSpace(file.MimeType) ? null : file.MimeType;

        // The STORAGE KEY itself never leaves this controller; only its base name can, and only as
        // the last resort for a row whose original name is blank.
        string fileName = string.IsNullOrWhiteSpace(file.OriginalFileName)
            ? Path.GetFileName(file.StorageKey)
            : file.OriginalFileName;

        return new MediaSourceFields(
            KindForMimeType(mimeType),
            string.IsNullOrWhiteSpace(fileName) ? null : fileName,
            mimeType,
            file.SizeBytes);
    }

    /// <summary>
    /// The four resolved fields of a pinned <c>WorkflowStepResult</c> render, or all-null when the
    /// pointer is dangling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A render is addressed by its object's key — that is the only thing that says what the file is
    /// — so this is the one resolution path that carries the prefix re-check, the same guard
    /// <c>MediaController</c> and <c>OutputsController</c> use: the key must sit under THIS project's
    /// <c>outputFiles/</c> prefix. A missing key, or one belonging to another project, is dangling
    /// even though the row itself resolves.
    /// </para>
    /// <para>
    /// <see cref="MediaSourceFields.SizeBytes"/> stays <c>null</c> because a step result records no
    /// object size and this endpoint does not HEAD the store to invent one. That is deliberately not
    /// the media pool's <c>0</c>: there, every item reports a size and 0 is the contract's stated
    /// "not known"; here the field is nullable, so "not known" can be said exactly.
    /// </para>
    /// </remarks>
    private static MediaSourceFields DescribeStepResultSource(WorkflowStepResult? render, Guid projectId)
    {
        if (render?.OutputStorageKey is not { } storageKey ||
            !storageKey.StartsWith($"projects/{projectId:D}/outputFiles/", StringComparison.Ordinal))
        {
            return MediaSourceFields.Dangling;
        }

        string fileName = Path.GetFileName(storageKey);
        string? mimeType = InferMediaMimeType(fileName);

        // A key under outputFiles whose extension is not media (an EDL, a manifest) is a real,
        // removable pin with no kind to report — not a dangling one.
        return new MediaSourceFields(
            KindForMimeType(mimeType),
            string.IsNullOrWhiteSpace(fileName) ? null : fileName,
            mimeType,
            SizeBytes: null);
    }

    // --- Media references ------------------------------------------------------------------

    /// <summary>
    /// Parses the opaque pool token. The token is the ONLY thing a client may send — never a bare
    /// Guid (which would not say which of the two tables to look in) and never a storage key (which
    /// would let a caller name an object the project does not own).
    /// </summary>
    /// <remarks>
    /// The prefixes are compared case-insensitively so a client that upper-cases a token is not
    /// rejected over cosmetics; the Guid payload is parsed, not string-compared, so every spelling
    /// of one id resolves to the same reference and <see cref="MediaRefFor"/> can normalize it.
    /// </remarks>
    private static bool TryParseMediaRef(string? mediaRef, out MediaSourceKind sourceKind, out Guid sourceId)
    {
        sourceKind = default;
        sourceId = default;

        if (string.IsNullOrWhiteSpace(mediaRef)) return false;

        string token = mediaRef.Trim();

        if (token.StartsWith(FileMediaRefPrefix, StringComparison.OrdinalIgnoreCase))
        {
            sourceKind = MediaSourceKind.ProjectFile;
            return Guid.TryParse(token[FileMediaRefPrefix.Length..], out sourceId);
        }

        if (token.StartsWith(StepResultMediaRefPrefix, StringComparison.OrdinalIgnoreCase))
        {
            sourceKind = MediaSourceKind.StepResult;
            return Guid.TryParse(token[StepResultMediaRefPrefix.Length..], out sourceId);
        }

        return false;
    }

    /// <summary>
    /// The canonical token for a reference, always in lower-case hyphenated Guid form, so the value
    /// a client sends back round-trips and two spellings of one id compare equal.
    /// </summary>
    private static string MediaRefFor(MediaSourceKind sourceKind, Guid sourceId) =>
        sourceKind == MediaSourceKind.ProjectFile
            ? $"{FileMediaRefPrefix}{sourceId:D}"
            : $"{StepResultMediaRefPrefix}{sourceId:D}";

    /// <summary>
    /// The existing pin of <paramref name="sourceId"/> in <paramref name="binId"/>, if any. The
    /// branch is on the kind rather than on a conditional inside the predicate so each side is a
    /// plain indexed equality — and so only the column the kind actually names is ever compared.
    /// </summary>
    private async Task<MediaBinItem?> FindItemAsync(
        Guid binId,
        MediaSourceKind sourceKind,
        Guid sourceId,
        CancellationToken ct) =>
        sourceKind == MediaSourceKind.ProjectFile
            ? await _db.MediaBinItems.FirstOrDefaultAsync(
                i => i.BinId == binId && i.SourceProjectFileId == sourceId, ct)
            : await _db.MediaBinItems.FirstOrDefaultAsync(
                i => i.BinId == binId && i.SourceStepResultId == sourceId, ct);

    /// <summary>Appends a new pin to the end of its bin's items.</summary>
    private async Task<int> NextItemSortOrderAsync(Guid binId, CancellationToken ct)
    {
        int? max = await _db.MediaBinItems
            .Where(i => i.BinId == binId)
            .MaxAsync(i => (int?)i.SortOrder, ct);
        return (max ?? -1) + 1;
    }

    /// <summary>
    /// The media kind a MIME type denotes, or <c>null</c> for anything that is not video, audio or
    /// image.
    /// </summary>
    /// <remarks>
    /// Null is an answer, not a failure: a pinned file uploaded as <c>text/plain</c> is a real pin
    /// whose kind is genuinely unknown to this contract's closed three-value set, and reporting
    /// <c>video</c> as a nearest guess would be exactly the fabrication the listing must not make.
    /// </remarks>
    private static MediaKind? KindForMimeType(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return null;

        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Video;
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Audio;
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Image;

        return null;
    }

    /// <summary>
    /// The MIME type a stored object's extension implies, or <c>null</c> when it implies none.
    /// </summary>
    /// <remarks>
    /// A step result has no MIME column — its object's key is the only description of what it is —
    /// so the extension is the only thing there is to read. The table deliberately mirrors
    /// <c>MediaController.MimeTypeByExtension</c> (which extends <c>OutputsController</c>'s and
    /// <c>ProjectFilesController</c>'s shorter ones with the image types the media contract accepts):
    /// this file must not edit that controller, so the table is copied rather than shared, and a
    /// render's own tests pin the one extension that matters here. A non-media extension returns
    /// null, which is how a manifest or an EDL sitting under <c>outputFiles/</c> reports no kind
    /// instead of being classified as something it is not.
    /// </remarks>
    private static string? InferMediaMimeType(string fileName) =>
        MimeTypeByExtension.TryGetValue(Path.GetExtension(fileName), out string? mimeType) ? mimeType : null;

    private static readonly Dictionary<string, string> MimeTypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".gif"] = "image/gif",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
    };

    // --- In-memory tree walks (all bounded) ------------------------------------------------

    /// <summary>
    /// The depth of a bin in the stored hierarchy, counting a root as 1 and null as 0.
    /// </summary>
    /// <returns>
    /// Null when the stored hierarchy is unusable — it contains a cycle, or is deeper than
    /// <see cref="MaxBinDepth"/>. Callers answer <c>400 binHierarchyInvalid</c>; they must NOT
    /// treat null as a depth, or a corrupt tree would silently pass a bound check. The visited set
    /// and the step cap are what make that termination guaranteed rather than hoped for.
    /// </returns>
    private static int? DepthOf(IReadOnlyDictionary<Guid, MediaBin> byId, Guid? binId)
    {
        int depth = 0;
        HashSet<Guid> visited = [];
        Guid? cursor = binId;

        while (cursor is { } current)
        {
            // A parent that is missing from the filtered set is a dangling reference (its row was
            // soft-deleted); the chain ends there rather than being reported as corruption, since
            // the caller only needs a lower bound for the depth check.
            if (!byId.TryGetValue(current, out MediaBin? node)) return depth;

            if (!visited.Add(current)) return null;
            depth++;
            if (depth > MaxBinDepth) return null;

            cursor = node.ParentBinId;
        }

        return depth;
    }

    /// <summary>
    /// The height of <paramref name="rootId"/>'s subtree as the number of levels BELOW it — 0 for a
    /// leaf. Null when the subtree cannot be walked to a bound (a cycle, or absurd depth), which
    /// callers treat as <c>400 binHierarchyInvalid</c>.
    /// </summary>
    private static int? SubtreeHeight(IReadOnlyDictionary<Guid, MediaBin> byId, Guid rootId)
    {
        int height = 0;
        List<Guid> level = [rootId];
        HashSet<Guid> visited = [rootId];

        while (level.Count > 0)
        {
            if (height > MaxBinDepth) return null;

            List<Guid> next = [];
            foreach (MediaBin candidate in byId.Values)
            {
                if (candidate.ParentBinId is not { } parent || !level.Contains(parent)) continue;
                if (visited.Add(candidate.Id)) next.Add(candidate.Id);
            }

            level = next;
            if (level.Count > 0) height++;
        }

        return height;
    }

    /// <summary>
    /// Every bin id in the subtree rooted at <paramref name="rootId"/>, including the root. Bounded
    /// by the same visited set and level cap as <see cref="SubtreeHeight"/>, so a cycle in the
    /// stored data cannot turn a delete into an infinite walk; a truncated walk can only ever
    /// under-collect, never loop.
    /// </summary>
    private static List<Guid> CollectSubtreeIds(IReadOnlyList<MediaBin> bins, Guid rootId)
    {
        List<Guid> collected = [rootId];
        HashSet<Guid> visited = [rootId];
        List<Guid> level = [rootId];
        int levels = 0;

        while (level.Count > 0 && levels < MaxBinDepth)
        {
            List<Guid> next = [];
            foreach (MediaBin bin in bins)
            {
                if (bin.ParentBinId is not { } parent || !level.Contains(parent)) continue;
                if (visited.Add(bin.Id)) next.Add(bin.Id);
            }

            collected.AddRange(next);
            level = next;
            levels++;
        }

        return collected;
    }

    /// <summary>
    /// The sort order that appends a bin to the end of <paramref name="parentBinId"/>'s children.
    /// Server-assigned, never derived from the name, so a rename never reorders the panel.
    /// </summary>
    private static int NextSortOrder(IReadOnlyList<MediaBin> bins, Guid? parentBinId)
    {
        int max = -1;
        foreach (MediaBin bin in bins)
        {
            if (bin.ParentBinId == parentBinId && bin.SortOrder > max) max = bin.SortOrder;
        }

        return max + 1;
    }

    // --- Mapping ---------------------------------------------------------------------------

    private static MediaBinSummaryResponse ToSummaryResponse(MediaBin bin, int itemCount) =>
        new(bin.Id, bin.Name, bin.ParentBinId, bin.SortOrder, itemCount, MediaSmartFilter.Parse(bin.SmartFilterJson));

    // --- Smart bins (P6) --------------------------------------------------------------------

    /// <summary>
    /// A smart bin's items: every pool source (media-typed project files plus unregistered renders
    /// under this project's <c>outputFiles/</c>) that matches the saved filter against its
    /// annotation. Rows carry a deterministic id derived from the mediaRef — there is no pinned row
    /// to name — and the response shape is the ordinary listing's, so the panel renders both alike.
    /// </summary>
    private async Task<List<MediaBinItemDetailResponse>> ListSmartItemsAsync(Guid projectId, MediaBin bin, MediaSmartFilter filter, CancellationToken ct)
    {
        List<ProjectFile> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.CsamStatus != CsamScanStatus.Quarantined)
            .Where(f => f.MimeType != null && f.MimeType != string.Empty)
            .Where(f => f.MimeType.StartsWith("video/") || f.MimeType.StartsWith("audio/") || f.MimeType.StartsWith("image/"))
            .OrderByDescending(f => f.UploadedAt)
            .Take(SmartPoolCap)
            .ToListAsync(ct);

        string outputPrefix = $"projects/{projectId}/outputFiles/";
        List<WorkflowStepResult> renders = await _db.WorkflowStepResults
            .Where(r => r.WorkflowExecution.ProjectId == projectId && r.OutputStorageKey != null && r.OutputStorageKey.StartsWith(outputPrefix))
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .Take(SmartPoolCap)
            .ToListAsync(ct);
        HashSet<string> registeredKeys = files.Select(f => f.StorageKey).ToHashSet(StringComparer.Ordinal);
        HashSet<Guid> heldFileIds = await Services.Outputs.OutputGate.HeldFileIdsAsync(_db, projectId, files, ct);
        HashSet<Guid> heldRenderIds = await Services.Outputs.OutputGate.HeldResultIdsAsync(_db, projectId, renders.Select(r => r.Id).ToList(), ct);
        files = files.Where(f => !heldFileIds.Contains(f.Id)).ToList();
        renders = renders.Where(r => !heldRenderIds.Contains(r.Id)).ToList();

        List<MediaAnnotation> annotations = await _db.MediaAnnotations.Where(a => a.ProjectId == projectId).ToListAsync(ct);
        Dictionary<(MediaSourceKind, Guid), MediaAnnotationView> views = [];
        foreach (MediaAnnotation annotation in annotations)
        {
            Guid sourceId = annotation.SourceKind == MediaSourceKind.ProjectFile ? annotation.SourceProjectFileId ?? Guid.Empty : annotation.SourceStepResultId ?? Guid.Empty;
            views[(annotation.SourceKind, sourceId)] = new MediaAnnotationView(ReadKeywords(annotation.KeywordsJson), annotation.ColorLabel, annotation.Favourite, annotation.Flag);
        }

        List<MediaBinItemDetailResponse> rows = [];
        DateTime now = DateTime.UtcNow;
        foreach (ProjectFile file in files)
        {
            MediaSourceFields source = DescribeFileSource(file);
            views.TryGetValue((MediaSourceKind.ProjectFile, file.Id), out MediaAnnotationView? view);
            if (!filter.Matches(KindWord(source.Kind), source.FileName, view)) continue;
            rows.Add(SmartRow(bin.Id, MediaSourceKind.ProjectFile, file.Id, source, rows.Count, now));
        }

        foreach (WorkflowStepResult render in renders)
        {
            if (render.OutputStorageKey is null || registeredKeys.Contains(render.OutputStorageKey)) continue;
            MediaSourceFields source = DescribeStepResultSource(render, projectId);
            if (source.Kind is null) continue;
            views.TryGetValue((MediaSourceKind.StepResult, render.Id), out MediaAnnotationView? view);
            if (!filter.Matches(KindWord(source.Kind), source.FileName, view)) continue;
            rows.Add(SmartRow(bin.Id, MediaSourceKind.StepResult, render.Id, source, rows.Count, now));
        }

        return rows;
    }

    private const int SmartPoolCap = 500;

    private static string? KindWord(MediaKind? kind) => kind switch
    {
        MediaKind.Video => "video",
        MediaKind.Audio => "audio",
        MediaKind.Image => "image",
        _ => null
    };

    internal static IReadOnlyList<string> ReadKeywords(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static MediaBinItemDetailResponse SmartRow(Guid binId, MediaSourceKind kind, Guid sourceId, MediaSourceFields source, int index, DateTime now)
    {
        string mediaRef = MediaRefFor(kind, sourceId);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{binId:N}|{mediaRef}"));
        byte[] guid = hash[..16];
        guid[7] = (byte)((guid[7] & 0x0F) | 0x40);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new MediaBinItemDetailResponse(new Guid(guid), binId, mediaRef, kind.ToString(), index, now, source.Kind, source.FileName, source.MimeType, source.SizeBytes);
    }

    private static MediaBinItemResponse ToItemResponse(MediaBinItem item)
    {
        return new MediaBinItemResponse(
            item.Id,
            item.BinId,
            MediaRefFor(item.SourceKind, SourceIdOf(item)),
            item.SourceKind.ToString(),
            item.SortOrder,
            item.CreatedAt);
    }

    /// <summary>
    /// The listing's row: the add/move shape verbatim, plus the source's own fields.
    /// </summary>
    /// <remarks>
    /// Built ON <see cref="ToItemResponse"/> rather than beside it, so the six fields a client
    /// already parses cannot drift between the two routes — including the normalized
    /// <c>mediaRef</c>, which is what a dangling row still carries and what makes it removable.
    /// </remarks>
    private static MediaBinItemDetailResponse ToDetailResponse(MediaBinItem item, MediaSourceFields source)
    {
        MediaBinItemResponse header = ToItemResponse(item);

        return new MediaBinItemDetailResponse(
            header.Id,
            header.BinId,
            header.MediaRef,
            header.SourceKind,
            header.SortOrder,
            header.CreatedAt,
            source.Kind,
            source.FileName,
            source.MimeType,
            source.SizeBytes);
    }

    /// <summary>
    /// The id carried by the column the pin's kind names — the single place that mapping exists, so
    /// the add, move, delete and listing shapes cannot disagree about which reference is in play.
    /// </summary>
    private static Guid SourceIdOf(MediaBinItem item) =>
        item.SourceKind == MediaSourceKind.ProjectFile
            ? item.SourceProjectFileId ?? Guid.Empty
            : item.SourceStepResultId ?? Guid.Empty;

    /// <summary>
    /// Trims and checks a bin name. Trimming BEFORE the length check is the point: "  x  " is a
    /// one-character name, and a name of 121 spaces is not a 121-character name.
    /// </summary>
    private static bool TryNormalizeBinName(string? raw, out string name, out string errorCode)
    {
        name = string.Empty;
        errorCode = BinNameRequiredError;

        if (string.IsNullOrWhiteSpace(raw)) return false;

        name = raw.Trim();
        if (name.Length > MaxBinNameLength)
        {
            errorCode = BinNameTooLongError;
            return false;
        }

        return true;
    }

    private static string NameErrorMessage =>
        $"name must be 1..{MaxBinNameLength} characters after trimming.";

    private const string HierarchyInvalidMessage =
        "This project's bin hierarchy is invalid (a cycle, or nested deeper than the supported maximum).";

    // --- Local shapes ----------------------------------------------------------------------

    /// <summary>
    /// The four fields the item listing resolves from a pinned source.
    /// </summary>
    /// <remarks>
    /// All four are null — the <see cref="Dangling"/> instance — when the pointer cannot be resolved
    /// at all, and each is null on its own when the source genuinely has no such value: a render has
    /// no recorded size, and a file uploaded without a MIME type has no kind. Both cases are the same
    /// answer to the client, which is what keeps this from needing a second "why" field.
    /// </remarks>
    private sealed record MediaSourceFields(MediaKind? Kind, string? FileName, string? MimeType, long? SizeBytes)
    {
        /// <summary>The dangling pointer: the pin is real, nothing about its source is known.</summary>
        public static MediaSourceFields Dangling { get; } = new(null, null, null, null);
    }
}
