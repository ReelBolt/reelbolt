#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="MediaBinsController"/> — the media panel's bins CRUD and bin membership.
///
/// <para>
/// <b>Four properties carry this surface, and each gets its own section below.</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>404, never 403.</b> A missing project, a foreign project and a foreign/nonexistent/soft-deleted
/// bin or item are all the same empty not-found. Like <c>MediaControllerTests</c>, the tests assert
/// the ABSENCE of a <see cref="ForbidResult"/> as well as the presence of a
/// <see cref="NotFoundResult"/>, because a 403 on any of those would turn the bins routes into an
/// existence oracle for somebody else's project id.
/// </item>
/// <item>
/// <b>A rejected call writes nothing.</b> Every refusal test compares a full text snapshot of the
/// <c>media_bins</c> + <c>media_bin_items</c> tables taken through a FRESH <see cref="DbContext"/>
/// before and after the request. A fresh context matters: the context that served the request would
/// answer from its own identity map, so a mutated-but-unsaved entity would look written.
/// </item>
/// <item>
/// <b>Cycles and the depth bound are the controller's job, not the database's.</b>
/// <c>media_bins.parent_bin_id</c> is a <c>Restrict</c> self-FK, which says nothing about a cycle,
/// so both are asserted through the public routes only.
/// </item>
/// <item>
/// <b>The mediaRef rules are two different failures.</b> A malformed token is a
/// <c>400 invalidMediaRef</c> answered before anything is looked up; a well-formed token resolving
/// to nothing in this project — including another project's media — is a <c>404</c>. Status codes
/// are the ones the controller actually returns: a new pin is <c>201</c>, a duplicate pin is
/// <c>200</c>, and the delete routes are <c>204</c>.
/// </item>
/// </list>
/// <para>
/// These tests never touch the code under test: the controller, its DTOs, its models and the
/// DbContext are all used exactly as shipped.
/// </para>
/// </summary>
public class MediaBinsControllerTests
{
    /// <summary>
    /// The depth bound the acceptance criteria name, held as a literal here so the pin is visible.
    /// Every other test builds its chains from <see cref="MediaBinsController.MaxBinDepth"/> itself.
    /// </summary>
    private const int ExpectedMaxBinDepth = 16;

    /// <summary>The API's real response serializer, mirrored: web defaults and nothing else.</summary>
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>A fixed guid spelling used by the malformed-token theory, where InlineData needs a constant.</summary>
    private const string SomeGuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    // =============================================================================================
    // Harness
    // =============================================================================================

    private static string NewDatabaseName() => Guid.NewGuid().ToString();

    private static InferenceApiDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options);

    private static ICurrentUser StubCurrentUser(Guid userId)
    {
        Mock<ICurrentUser> currentUser = new();
        currentUser.SetupGet(u => u.UserId).Returns(userId);
        currentUser.SetupGet(u => u.Email).Returns("test@example.com");
        currentUser.SetupGet(u => u.IsAuthenticated).Returns(true);
        currentUser.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        currentUser.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        currentUser.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);

        return currentUser.Object;
    }

    private static Project SeedProject(InferenceApiDbContext db, Guid ownerId, string name)
    {
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static MediaBin SeedBin(
        InferenceApiDbContext db,
        Guid projectId,
        string name,
        Guid? parentBinId = null,
        int sortOrder = 0,
        DateTime? deletedAt = null)
    {
        MediaBin bin = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            ParentBinId = parentBinId,
            SortOrder = sortOrder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deletedAt
        };

        db.MediaBins.Add(bin);
        db.SaveChanges();
        return bin;
    }

    private static MediaBinItem SeedItem(
        InferenceApiDbContext db,
        Guid binId,
        MediaSourceKind sourceKind,
        Guid sourceId,
        int sortOrder = 0,
        Guid? id = null)
    {
        MediaBinItem item = new()
        {
            Id = id ?? Guid.NewGuid(),
            BinId = binId,
            SourceKind = sourceKind,
            SourceProjectFileId = sourceKind == MediaSourceKind.ProjectFile ? sourceId : null,
            SourceStepResultId = sourceKind == MediaSourceKind.StepResult ? sourceId : null,
            SortOrder = sortOrder,
            CreatedAt = DateTime.UtcNow
        };

        db.MediaBinItems.Add(item);
        db.SaveChanges();
        return item;
    }

    private static ProjectFile SeedFile(InferenceApiDbContext db, Guid projectId, string fileName = "clip.mp4")
    {
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = fileName,
            StorageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}/{fileName}",
            StorageBucket = "reelforge",
            Category = "userFiles",
            MimeType = "video/mp4",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow
        };

        db.ProjectFiles.Add(file);
        db.SaveChanges();
        return file;
    }

    private static WorkflowStepResult SeedStepResult(InferenceApiDbContext db, Guid projectId)
    {
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            ProjectId = projectId,
            Status = ExecutionStatus.Passed
        };

        WorkflowStepResult result = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            ExecutedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            OutputStorageKey = $"projects/{projectId:D}/outputFiles/{execution.Id:D}/render.mp4",
            Status = StepStatus.Completed
        };

        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(result);
        db.SaveChanges();
        return result;
    }

    /// <summary>
    /// A chain of <paramref name="levels"/> bins nested one inside the next — level 1 is a root, so
    /// the last entry sits at depth <paramref name="levels"/>.
    /// </summary>
    private static List<MediaBin> SeedChain(InferenceApiDbContext db, Guid projectId, int levels)
    {
        List<MediaBin> chain = [];
        Guid? parent = null;

        for (int level = 1; level <= levels; level++)
        {
            MediaBin bin = SeedBin(db, projectId, $"Level {level}", parent);
            chain.Add(bin);
            parent = bin.Id;
        }

        return chain;
    }

    /// <summary>
    /// Every bin and item row rendered as text. Comparing this before and after a refused request is
    /// how "nothing was mutated" is proven — a single differing character is a write.
    /// </summary>
    private static string StateOf(InferenceApiDbContext db)
    {
        List<string> rows = [];

        rows.AddRange(db.MediaBins
            .IgnoreQueryFilters()
            .OrderBy(b => b.Id)
            .Select(b => $"bin {b.Id} name={b.Name} parent={b.ParentBinId} sort={b.SortOrder} deleted={b.DeletedAt != null}"));

        rows.AddRange(db.MediaBinItems
            .OrderBy(i => i.Id)
            .Select(i => $"item {i.Id} bin={i.BinId} kind={i.SourceKind} file={i.SourceProjectFileId} step={i.SourceStepResultId} sort={i.SortOrder}"));

        return string.Join("\n", rows);
    }

    /// <summary>
    /// The body of any status-carrying result — an <see cref="OkObjectResult"/> and a 201
    /// <see cref="ObjectResult"/> both qualify — so each test's status assertion and its body
    /// assertion stay independent of one another.
    /// </summary>
    private static T BodyOf<T>(IActionResult? result) where T : class =>
        result.Should().BeAssignableTo<ObjectResult>().Subject.Value.Should().BeOfType<T>().Subject;

    private static string ErrorCodeOf(IActionResult? result) =>
        JsonSerializer.SerializeToElement(
                result.Should().BeOfType<BadRequestObjectResult>().Subject.Value,
                WireJson)
            .GetProperty("error")
            .GetString()!;

    private static void ShouldBeNotFoundNotForbid(IActionResult? result)
    {
        result.Should().BeOfType<NotFoundResult>();
        result.Should().NotBeOfType<ForbidResult>();
    }

    private static void ShouldBeCreated(IActionResult? result) =>
        result.Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(201);

    private static MediaBinSummaryResponse BinBody(IActionResult? result) => BodyOf<MediaBinSummaryResponse>(result);

    private static MediaBinItemResponse ItemBody(IActionResult? result) => BodyOf<MediaBinItemResponse>(result);

    /// <summary>The listing's body, with the status assertion left to the caller.</summary>
    private static List<MediaBinItemDetailResponse> ItemRows(IActionResult? result) =>
        BodyOf<List<MediaBinItemDetailResponse>>(result);

    /// <summary>
    /// The single row of a listing that should hold exactly one, so an "enriched correctly" test
    /// cannot pass while the row count is wrong.
    /// </summary>
    private static MediaBinItemDetailResponse SingleRow(ActionResult<List<MediaBinItemDetailResponse>> result) =>
        ItemRows(result.Result).Should().ContainSingle().Subject;

    /// <summary>The listing of one bin of the fixture's own project.</summary>
    private static Task<ActionResult<List<MediaBinItemDetailResponse>>> ListItems(
        MediaBinsController controller,
        Guid projectId,
        Guid binId) =>
        controller.ListItems(projectId, binId, CancellationToken.None);

    /// <summary>An explicit JSON <c>null</c> — "move to the root", distinct from "do not touch the parent".</summary>
    private static JsonElement RootParent => JsonSerializer.SerializeToElement<Guid?>(null);

    /// <summary>A <c>parentBinId</c> the caller never mentioned (the default <see cref="JsonValueKind.Undefined"/>).</summary>
    private static JsonElement AbsentParent => default;

    private static JsonElement ParentToken(string raw) => JsonSerializer.SerializeToElement(raw);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DatabaseName = NewDatabaseName();
            UserId = Guid.NewGuid();
            Db = CreateContext(DatabaseName);
            Project = SeedProject(Db, UserId, "Owned Project");
            Controller = new MediaBinsController(Db, TestTenancy.Access(Db, StubCurrentUser(UserId)));
        }

        public string DatabaseName { get; }

        public Guid UserId { get; }

        public InferenceApiDbContext Db { get; }

        public Project Project { get; }

        public MediaBinsController Controller { get; }

        public Guid ProjectId => Project.Id;

        /// <summary>A context over the same in-memory database, for reads that must not be answered
        /// from the serving context's identity map.</summary>
        public InferenceApiDbContext NewContext() => CreateContext(DatabaseName);

        /// <summary>A different owner's project. The caller is scoped to <see cref="UserId"/> only.</summary>
        public Project AddForeignProject() => SeedProject(Db, Guid.NewGuid(), "Foreign Project");

        public MediaBin Bin(
            string name,
            Guid? parentBinId = null,
            int sortOrder = 0,
            DateTime? deletedAt = null,
            Guid? projectId = null) =>
            SeedBin(Db, projectId ?? ProjectId, name, parentBinId, sortOrder, deletedAt);

        public MediaBinItem Item(
            Guid binId,
            MediaSourceKind sourceKind,
            Guid sourceId,
            int sortOrder = 0,
            Guid? id = null) =>
            SeedItem(Db, binId, sourceKind, sourceId, sortOrder, id);

        public ProjectFile File(Guid? projectId = null, string fileName = "clip.mp4") =>
            SeedFile(Db, projectId ?? ProjectId, fileName);

        public WorkflowStepResult StepResult(Guid? projectId = null) =>
            SeedStepResult(Db, projectId ?? ProjectId);

        /// <summary>
        /// A step result of <paramref name="projectId"/> whose output key the caller controls — the
        /// only way to build the dangling render cases (no key, or a key under another project).
        /// </summary>
        public WorkflowStepResult StepResultWithKey(Guid? projectId = null, string? outputStorageKey = null)
        {
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = projectId ?? ProjectId,
                Status = ExecutionStatus.Passed
            };

            WorkflowStepResult result = new()
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = Guid.NewGuid(),
                ExecutedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                OutputStorageKey = outputStorageKey,
                Status = StepStatus.Completed
            };

            Db.WorkflowExecutions.Add(execution);
            Db.WorkflowStepResults.Add(result);
            Db.SaveChanges();
            return result;
        }

        /// <summary>A project file row of the caller's project, with a key the caller can name.</summary>
        public ProjectFile FileWith(string storageKey, string fileName = "clip.mp4")
        {
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = ProjectId,
                OriginalFileName = fileName,
                StorageKey = storageKey,
                StorageBucket = "reelforge",
                Category = "userFiles",
                MimeType = "video/mp4",
                SizeBytes = 1024,
                UploadedAt = DateTime.UtcNow
            };

            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public List<MediaBin> Chain(int levels) => SeedChain(Db, ProjectId, levels);

        /// <summary>The full table state, read through a fresh context.</summary>
        public string State()
        {
            using InferenceApiDbContext reader = NewContext();
            return StateOf(reader);
        }

        public void Dispose() => Db.Dispose();
    }

    // =============================================================================================
    // 404, never 403 — the ownership boundary is the outer gate on every route
    // =============================================================================================

    [Fact]
    public async Task ListBins_ReturnsNotFound_WhenProjectDoesNotExist()
    {
        using Fixture fx = new();

        ActionResult<List<MediaBinSummaryResponse>> result =
            await fx.Controller.ListBins(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListBins_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        fx.Bin("Foreign bin", projectId: foreign.Id);

        ActionResult<List<MediaBinSummaryResponse>> result =
            await fx.Controller.ListBins(foreign.Id, CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
    }

    [Fact]
    public async Task CreateBin_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            foreign.Id,
            new CreateMediaBinRequest("Sneaky", null),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before, "a refused create must write nothing");
    }

    [Fact]
    public async Task PatchBin_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            foreign.Id,
            foreignBin.Id,
            new PatchMediaBinRequest { Name = "Renamed by a stranger" },
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task DeleteBin_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        string before = fx.State();

        IActionResult result = await fx.Controller.DeleteBin(foreign.Id, foreignBin.Id, CancellationToken.None);

        ShouldBeNotFoundNotForbid(result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            foreign.Id,
            foreignBin.Id,
            new AddMediaBinItemRequest($"file:{foreignFile.Id:D}"),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_NotBadRequest_WhenTheProjectIsForeignEvenIfTheMediaRefIsMalformed()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);

        // The project gate is OUTERMOST: a caller with no claim on the project is told "not found"
        // whatever else is wrong with the request, so no 400 can confirm the project exists.
        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            foreign.Id,
            foreignBin.Id,
            new AddMediaBinItemRequest("not-a-media-ref"),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
    }

    [Fact]
    public async Task MoveItem_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        MediaBinItem foreignItem = fx.Item(foreignBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);
        MediaBin ourBin = fx.Bin("Ours");
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            foreign.Id,
            foreignBin.Id,
            foreignItem.Id,
            new MoveMediaBinItemRequest(ourBin.Id),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task MoveItem_ReturnsNotFound_NotBadRequest_WhenTheProjectIsForeignEvenIfBinIdIsAbsent()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);

        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            foreign.Id,
            foreignBin.Id,
            Guid.NewGuid(),
            new MoveMediaBinItemRequest(null),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
    }

    [Fact]
    public async Task DeleteItem_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        MediaBinItem foreignItem = fx.Item(foreignBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);
        string before = fx.State();

        IActionResult result = await fx.Controller.DeleteItem(
            foreign.Id,
            foreignBin.Id,
            foreignItem.Id,
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task EveryRoute_ReturnsNotFound_NotForbid_ForAProjectNobodyOwns()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        Guid missing = Guid.NewGuid();

        // A project id that does not exist. Note this is also the soft-deleted case: Project carries
        // no soft-delete column at all (unlike MediaBin, EditTimelineEntity and MediaDerivative), so
        // a deleted project's id IS an unowned, nonexistent one as far as this controller can see —
        // and it must be answered identically rather than 500 or 403.
        void AssertNotFound(IActionResult? result)
        {
            result.Should().BeOfType<NotFoundResult>();
            result.Should().NotBeOfType<ForbidResult>();
        }

        AssertNotFound((await fx.Controller.ListBins(missing, CancellationToken.None)).Result);
        AssertNotFound((await fx.Controller.CreateBin(missing, new CreateMediaBinRequest("x", null), CancellationToken.None)).Result);
        AssertNotFound((await fx.Controller.PatchBin(missing, bin.Id, new PatchMediaBinRequest { Name = "x" }, CancellationToken.None)).Result);
        AssertNotFound(await fx.Controller.DeleteBin(missing, bin.Id, CancellationToken.None));
        AssertNotFound((await fx.Controller.AddItem(missing, bin.Id, new AddMediaBinItemRequest($"file:{Guid.NewGuid():D}"), CancellationToken.None)).Result);
        AssertNotFound((await fx.Controller.ListItems(missing, bin.Id, CancellationToken.None)).Result);
        AssertNotFound((await fx.Controller.MoveItem(missing, bin.Id, Guid.NewGuid(), new MoveMediaBinItemRequest(bin.Id), CancellationToken.None)).Result);
        AssertNotFound(await fx.Controller.DeleteItem(missing, bin.Id, Guid.NewGuid(), CancellationToken.None));
    }

    // =============================================================================================
    // Project scoping of {binId} and {itemId} — a foreign bin is exactly as absent as a missing one
    // =============================================================================================

    [Fact]
    public async Task PatchBin_ReturnsNotFound_WhenBinBelongsToAnotherProject_AndNothingIsMutated()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", parentBinId: null, sortOrder: 3, projectId: foreign.Id);
        MediaBin foreignChild = fx.Bin("Foreign child", parentBinId: foreignBin.Id, projectId: foreign.Id);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            foreignBin.Id,
            new PatchMediaBinRequest { Name = "Renamed", ParentBinId = RootParent },
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before, "the foreign bin's name and parent must both be untouched");

        using InferenceApiDbContext reader = fx.NewContext();
        MediaBin stored = await reader.MediaBins.SingleAsync(b => b.Id == foreignBin.Id);
        stored.Name.Should().Be("Foreign bin");
        (await reader.MediaBins.SingleAsync(b => b.Id == foreignChild.Id)).ParentBinId.Should().Be(foreignBin.Id);
    }

    [Fact]
    public async Task DeleteBin_ReturnsNotFound_WhenBinBelongsToAnotherProject_AndTheForeignSubtreeSurvives()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        MediaBin foreignChild = fx.Bin("Foreign child", parentBinId: foreignBin.Id, projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        MediaBinItem foreignItem = fx.Item(foreignBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);
        string before = fx.State();

        IActionResult result = await fx.Controller.DeleteBin(fx.ProjectId, foreignBin.Id, CancellationToken.None);

        ShouldBeNotFoundNotForbid(result);
        fx.State().Should().Be(before);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBins.CountAsync(b => b.Id == foreignBin.Id || b.Id == foreignChild.Id))
            .Should().Be(2, "a foreign subtree must not be soft-deleted by a caller who does not own it");
        (await reader.MediaBinItems.AnyAsync(i => i.Id == foreignItem.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_WhenBinBelongsToAnotherProject_AndNoItemRowIsWritten()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);

        // The file IS ours, so only the bin scoping can refuse this.
        ProjectFile ourFile = fx.File();
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            fx.ProjectId,
            foreignBin.Id,
            new AddMediaBinItemRequest($"file:{ourFile.Id:D}"),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MoveItem_ReturnsNotFound_WhenTheSourceBinBelongsToAnotherProject()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        MediaBinItem foreignItem = fx.Item(foreignBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);
        MediaBin ourBin = fx.Bin("Ours");
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            fx.ProjectId,
            foreignBin.Id,
            foreignItem.Id,
            new MoveMediaBinItemRequest(ourBin.Id),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task MoveItem_ReturnsNotFound_WhenTheTargetBinBelongsToAnotherProject_AndTheItemStaysPut()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        MediaBin sourceBin = fx.Bin("Source");
        ProjectFile ourFile = fx.File();
        MediaBinItem item = fx.Item(sourceBin.Id, MediaSourceKind.ProjectFile, ourFile.Id);
        string before = fx.State();

        // A foreign target must be as absent as a nonexistent one — otherwise the move would be a
        // way to discover another project's bin ids.
        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            fx.ProjectId,
            sourceBin.Id,
            item.Id,
            new MoveMediaBinItemRequest(foreignBin.Id),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.SingleAsync(i => i.Id == item.Id)).BinId.Should().Be(sourceBin.Id);
    }

    [Fact]
    public async Task MoveItem_ReturnsNotFound_WhenTheItemBelongsToAnotherBinOfTheSameProject()
    {
        using Fixture fx = new();
        MediaBin sourceBin = fx.Bin("Source");
        MediaBin otherBin = fx.Bin("Other");
        MediaBin targetBin = fx.Bin("Target");
        ProjectFile ourFile = fx.File();

        // The item exists and is in this project — but not in the bin the URL names. The item lookup
        // is scoped by BinId as well as Id, so this is a 404 rather than a silent cross-bin move.
        MediaBinItem item = fx.Item(otherBin.Id, MediaSourceKind.ProjectFile, ourFile.Id);
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            fx.ProjectId,
            sourceBin.Id,
            item.Id,
            new MoveMediaBinItemRequest(targetBin.Id),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task DeleteItem_ReturnsNotFound_WhenTheItemBelongsToAnotherBinOfTheSameProject()
    {
        using Fixture fx = new();
        MediaBin namedBin = fx.Bin("Named by the URL");
        MediaBin otherBin = fx.Bin("Holds the item");
        ProjectFile ourFile = fx.File();
        MediaBinItem item = fx.Item(otherBin.Id, MediaSourceKind.ProjectFile, ourFile.Id);
        string before = fx.State();

        IActionResult result = await fx.Controller.DeleteItem(
            fx.ProjectId,
            namedBin.Id,
            item.Id,
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task DeleteItem_ReturnsNotFound_WhenTheItemIsInAnotherProjectsBin()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        MediaBinItem foreignItem = fx.Item(foreignBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);
        MediaBin ourBin = fx.Bin("Ours");
        string before = fx.State();

        IActionResult result = await fx.Controller.DeleteItem(
            fx.ProjectId,
            ourBin.Id,
            foreignItem.Id,
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task PatchBin_ReturnsNotFound_WhenTheBinIsSoftDeleted()
    {
        using Fixture fx = new();
        MediaBin deleted = fx.Bin("Deleted", deletedAt: DateTime.UtcNow);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            deleted.Id,
            new PatchMediaBinRequest { Name = "Resurrected" },
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task DeleteBin_ReturnsNotFound_WhenTheBinIsSoftDeleted()
    {
        using Fixture fx = new();
        MediaBin deleted = fx.Bin("Deleted", deletedAt: DateTime.UtcNow);
        string before = fx.State();

        IActionResult result = await fx.Controller.DeleteBin(fx.ProjectId, deleted.Id, CancellationToken.None);

        ShouldBeNotFoundNotForbid(result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_WhenTheBinIsSoftDeleted()
    {
        using Fixture fx = new();
        MediaBin deleted = fx.Bin("Deleted", deletedAt: DateTime.UtcNow);
        ProjectFile ourFile = fx.File();
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            fx.ProjectId,
            deleted.Id,
            new AddMediaBinItemRequest($"file:{ourFile.Id:D}"),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task PatchBin_ReturnsNotFound_WhenTheBinDoesNotExist()
    {
        using Fixture fx = new();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            Guid.NewGuid(),
            new PatchMediaBinRequest { Name = "x" },
            CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeleteItem_ReturnsNotFound_WhenTheItemDoesNotExist()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        string before = fx.State();

        IActionResult result = await fx.Controller.DeleteItem(
            fx.ProjectId,
            bin.Id,
            Guid.NewGuid(),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        fx.State().Should().Be(before);
    }

    // =============================================================================================
    // Cycle prevention — 400 binHierarchyCycle, with the tree left exactly as it was
    // =============================================================================================

    [Fact]
    public async Task PatchBin_ReturnsBadRequest_WhenTheBinIsMadeItsOwnParent()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("Solo");
        ProjectFile ourFile = fx.File();
        fx.Item(bin.Id, MediaSourceKind.ProjectFile, ourFile.Id);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            bin.Id,
            new PatchMediaBinRequest { Name = "Renamed", ParentBinId = ParentToken(bin.Id.ToString("D")) },
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinHierarchyCycleError);
        fx.State().Should().Be(before, "a refused cycle must leave parent_bin_id, the name and the item counts alone");
    }

    [Fact]
    public async Task PatchBin_ReturnsBadRequest_WhenTheBinIsMadeAChildOfItsOwnChild()
    {
        using Fixture fx = new();
        MediaBin parent = fx.Bin("Parent");
        MediaBin child = fx.Bin("Child", parentBinId: parent.Id);
        ProjectFile ourFile = fx.File();
        fx.Item(parent.Id, MediaSourceKind.ProjectFile, ourFile.Id);
        fx.Item(child.Id, MediaSourceKind.ProjectFile, ourFile.Id);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            parent.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(child.Id.ToString("D")) },
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinHierarchyCycleError);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task PatchBin_ReturnsBadRequest_WhenTheBinIsMadeAChildOfItsOwnGrandchild()
    {
        using Fixture fx = new();
        MediaBin root = fx.Bin("Root");
        MediaBin child = fx.Bin("Child", parentBinId: root.Id);
        MediaBin grandchild = fx.Bin("Grandchild", parentBinId: child.Id);
        string before = fx.State();

        // Two levels down: the walk must follow the whole ancestor chain, not just the parent link.
        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            root.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(grandchild.Id.ToString("D")) },
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinHierarchyCycleError);
        fx.State().Should().Be(before);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBins.SingleAsync(b => b.Id == root.Id)).ParentBinId.Should().BeNull();
        (await reader.MediaBins.SingleAsync(b => b.Id == grandchild.Id)).ParentBinId.Should().Be(child.Id);
    }

    [Fact]
    public async Task PatchBin_ReturnsBadRequest_WhenADeepDescendantIsMadeTheParent()
    {
        using Fixture fx = new();
        List<MediaBin> chain = fx.Chain(6);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            chain[0].Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(chain[5].Id.ToString("D")) },
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinHierarchyCycleError);
        fx.State().Should().Be(before);
    }

    // =============================================================================================
    // The depth bound — depth 16 is accepted, depth 17 is 400 binDepthExceeded
    // =============================================================================================

    [Fact]
    public void MaxBinDepth_IsPinnedAtSixteen()
    {
        MediaBinsController.MaxBinDepth.Should().Be(
            ExpectedMaxBinDepth,
            "the acceptance criteria name a 16-level bound; every other test in this file derives its chains from this constant");
    }

    [Fact]
    public async Task CreateBin_AcceptsTheSixteenthLevel()
    {
        using Fixture fx = new();
        List<MediaBin> chain = fx.Chain(MediaBinsController.MaxBinDepth - 1);

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            fx.ProjectId,
            new CreateMediaBinRequest("Level 16", chain[^1].Id),
            CancellationToken.None);

        ShouldBeCreated(result.Result);
        BinBody(result.Result).ParentBinId.Should().Be(chain[^1].Id);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBins.CountAsync()).Should().Be(MediaBinsController.MaxBinDepth);
    }

    [Fact]
    public async Task CreateBin_ReturnsBadRequest_AtTheSeventeenthLevel()
    {
        using Fixture fx = new();
        List<MediaBin> chain = fx.Chain(MediaBinsController.MaxBinDepth);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            fx.ProjectId,
            new CreateMediaBinRequest("Level 17", chain[^1].Id),
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinDepthExceededError);
        fx.State().Should().Be(before, "a bin past the bound must not be written");
    }

    [Fact]
    public async Task PatchBin_AcceptsAReparentThatLandsExactlyOnTheBound()
    {
        using Fixture fx = new();
        List<MediaBin> chain = fx.Chain(MediaBinsController.MaxBinDepth - 1);
        MediaBin mover = fx.Bin("Mover");

        // Depth 15 parent + the mover as its child = depth 16, which is still inside the bound.
        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            mover.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(chain[^1].Id.ToString("D")) },
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        BinBody(result.Result).ParentBinId.Should().Be(chain[^1].Id);
    }

    [Fact]
    public async Task PatchBin_ReturnsBadRequest_WhenAReparentWouldPutTheBinAtTheSeventeenthLevel()
    {
        using Fixture fx = new();
        List<MediaBin> chain = fx.Chain(MediaBinsController.MaxBinDepth);
        MediaBin mover = fx.Bin("Mover");
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            mover.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(chain[^1].Id.ToString("D")) },
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinDepthExceededError);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task PatchBin_ReturnsBadRequest_WhenAReparentWouldPushADescendantPastTheBound()
    {
        using Fixture fx = new();
        List<MediaBin> chain = fx.Chain(MediaBinsController.MaxBinDepth);
        MediaBin mover = fx.Bin("Mover");
        MediaBin moverChild = fx.Bin("Mover child", parentBinId: mover.Id);

        // The mover itself would land at depth 16 (fine), but it keeps its own child, so the DEEPEST
        // descendant would be at 17. The bound has to hold for the subtree, not just for the moved
        // bin — height 0 is a leaf, height 1 is one level below.
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            mover.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(chain[^1].Id.ToString("D")) },
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinDepthExceededError);
        fx.State().Should().Be(before);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBins.SingleAsync(b => b.Id == mover.Id)).ParentBinId.Should().BeNull();
        (await reader.MediaBins.SingleAsync(b => b.Id == moverChild.Id)).ParentBinId.Should().Be(mover.Id);
    }

    [Fact]
    public async Task PatchBin_AcceptsAReparentThatPushesTheWholeSubtreeExactlyToTheBound()
    {
        using Fixture fx = new();
        List<MediaBin> chain = fx.Chain(MediaBinsController.MaxBinDepth - 2);
        MediaBin mover = fx.Bin("Mover");
        fx.Bin("Mover child", parentBinId: mover.Id);

        // Depth 14 parent + mover + one descendant = depth 16 for the deepest row.
        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            mover.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(chain[^1].Id.ToString("D")) },
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        BinBody(result.Result).ParentBinId.Should().Be(chain[^1].Id);
    }

    // =============================================================================================
    // Delete — soft-delete the subtree, HARD-delete its items, leave siblings alone
    // =============================================================================================

    [Fact]
    public async Task DeleteBin_ReturnsNoContent()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");

        IActionResult result = await fx.Controller.DeleteBin(fx.ProjectId, bin.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteBin_SoftDeletesTheWholeSubtree_HardDeletesTheirItems_AndLeavesASiblingUntouched()
    {
        using Fixture fx = new();
        MediaBin target = fx.Bin("Target");
        MediaBin child = fx.Bin("Child", parentBinId: target.Id);
        MediaBin grandchild = fx.Bin("Grandchild", parentBinId: child.Id);
        MediaBin sibling = fx.Bin("Sibling");
        MediaBin siblingChild = fx.Bin("Sibling child", parentBinId: sibling.Id);

        ProjectFile file = fx.File();
        MediaBinItem targetItem = fx.Item(target.Id, MediaSourceKind.ProjectFile, file.Id);
        MediaBinItem childItem = fx.Item(child.Id, MediaSourceKind.ProjectFile, file.Id);
        MediaBinItem grandchildItem = fx.Item(grandchild.Id, MediaSourceKind.StepResult, Guid.NewGuid());
        MediaBinItem siblingItem = fx.Item(sibling.Id, MediaSourceKind.ProjectFile, file.Id);
        MediaBinItem siblingChildItem = fx.Item(siblingChild.Id, MediaSourceKind.ProjectFile, file.Id);

        IActionResult result = await fx.Controller.DeleteBin(fx.ProjectId, target.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        using InferenceApiDbContext reader = fx.NewContext();

        // The subtree is soft-deleted, so the default read no longer sees any of those three bins.
        List<Guid> live = await reader.MediaBins.Select(b => b.Id).ToListAsync();
        live.Should().BeEquivalentTo([sibling.Id, siblingChild.Id]);

        // ...and the rows are still there, marked rather than removed: a bin is soft-deleted, its
        // items are not.
        List<MediaBin> deletedRows = await reader.MediaBins
            .IgnoreQueryFilters()
            .Where(b => b.Id == target.Id || b.Id == child.Id || b.Id == grandchild.Id)
            .ToListAsync();
        deletedRows.Should().HaveCount(3);
        deletedRows.Should().OnlyContain(b => b.DeletedAt != null);

        // The subtree's items are HARD-deleted — media_bin_items has no deleted_at column, so a
        // surviving row would be an orphan the panel could never render or remove.
        List<Guid> remainingItems = await reader.MediaBinItems.Select(i => i.Id).ToListAsync();
        remainingItems.Should().BeEquivalentTo([siblingItem.Id, siblingChildItem.Id]);
        remainingItems.Should().NotContain(targetItem.Id);
        remainingItems.Should().NotContain(childItem.Id);
        remainingItems.Should().NotContain(grandchildItem.Id);

        // The media itself is never touched — a pin is a pointer, not a copy.
        (await reader.ProjectFiles.AnyAsync(f => f.Id == file.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteBin_RemovesTheSubtreeFromTheListEndpoint()
    {
        using Fixture fx = new();
        MediaBin target = fx.Bin("Target");
        MediaBin child = fx.Bin("Child", parentBinId: target.Id);
        MediaBin survivor = fx.Bin("Survivor");

        await fx.Controller.DeleteBin(fx.ProjectId, target.Id, CancellationToken.None);

        ActionResult<List<MediaBinSummaryResponse>> listed =
            await fx.Controller.ListBins(fx.ProjectId, CancellationToken.None);

        List<MediaBinSummaryResponse> body =
            listed.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<List<MediaBinSummaryResponse>>().Subject;

        body.Select(b => b.Id).Should().BeEquivalentTo([survivor.Id]);
        body.Should().NotContain(b => b.Id == child.Id);
    }

    // =============================================================================================
    // mediaRef parsing — malformed is 400 and writes nothing
    // =============================================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-media-ref")]
    [InlineData("file:")]
    [InlineData("file:not-a-guid")]
    [InlineData("file:------------------------------------")]
    [InlineData("step_result:")]
    [InlineData("step_result:not-a-guid")]
    [InlineData(SomeGuid)]
    [InlineData("storage:projects/p/userFiles/clip.mp4")]
    [InlineData("file:3f2504e0-4f89-11d3-9a0c-0305e82c3301-extra")]
    public async Task AddItem_ReturnsBadRequest_AndWritesNoItem_ForAMalformedMediaRef(string? mediaRef)
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            fx.ProjectId,
            bin.Id,
            new AddMediaBinItemRequest(mediaRef),
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.InvalidMediaRefError);
        fx.State().Should().Be(before, "a malformed token is rejected before anything is looked up or written");
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_WhenTheTokenIsWellFormedButResolvesToNothingInThisProject()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        string before = fx.State();

        // Well formed means "the prefix is known and the payload parses as a guid" — the two
        // failures are deliberately different problems, so this is a 404, not a 400.
        ActionResult<MediaBinItemResponse> fileResult = await fx.Controller.AddItem(
            fx.ProjectId,
            bin.Id,
            new AddMediaBinItemRequest($"file:{Guid.NewGuid():D}"),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(fileResult.Result);

        ActionResult<MediaBinItemResponse> stepResult = await fx.Controller.AddItem(
            fx.ProjectId,
            bin.Id,
            new AddMediaBinItemRequest($"step_result:{Guid.NewGuid():D}"),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(stepResult.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_WhenTheMediaRefIsAnotherProjectsFile()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        MediaBin ourBin = fx.Bin("Ours");
        ProjectFile foreignFile = fx.File(foreign.Id);
        string before = fx.State();

        // The SAME id asked for through the wrong project: the answer must not differ from an id
        // that does not exist at all, and no pin may be created for it.
        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            fx.ProjectId,
            ourBin.Id,
            new AddMediaBinItemRequest($"file:{foreignFile.Id:D}"),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.CountAsync()).Should().Be(0);
        (await reader.MediaBinItems.AnyAsync(i => i.BinId == foreignBin.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_WhenTheMediaRefIsAnotherProjectsStepResult()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin ourBin = fx.Bin("Ours");
        WorkflowStepResult foreignResult = fx.StepResult(foreign.Id);
        string before = fx.State();

        // A step result has no ProjectId of its own — its project is the owning execution's — so
        // this is the case where a naive lookup would leak another project's render into our bin.
        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            fx.ProjectId,
            ourBin.Id,
            new AddMediaBinItemRequest($"step_result:{foreignResult.Id:D}"),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    // =============================================================================================
    // mediaRef resolution — 201 for a new pin, 200 for a duplicate, exactly one row either way
    // =============================================================================================

    [Fact]
    public async Task AddItem_PinsAProjectFileOfThisProject_With201()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile file = fx.File();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            fx.ProjectId,
            bin.Id,
            new AddMediaBinItemRequest($"file:{file.Id:D}"),
            CancellationToken.None);

        ShouldBeCreated(result.Result);

        MediaBinItemResponse body = ItemBody(result.Result);
        body.BinId.Should().Be(bin.Id);
        body.MediaRef.Should().Be($"file:{file.Id:D}", "the token round-trips in its canonical lower-case hyphenated form");
        body.SourceKind.Should().Be(MediaSourceKind.ProjectFile.ToString());

        using InferenceApiDbContext reader = fx.NewContext();
        MediaBinItem stored = await reader.MediaBinItems.SingleAsync();
        stored.SourceProjectFileId.Should().Be(file.Id);
        stored.SourceStepResultId.Should().BeNull("only the column the kind names may carry a reference");
    }

    [Fact]
    public async Task AddItem_PinsAStepResultOfThisProject_With201()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        WorkflowStepResult stepResult = fx.StepResult();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.AddItem(
            fx.ProjectId,
            bin.Id,
            new AddMediaBinItemRequest($"step_result:{stepResult.Id:D}"),
            CancellationToken.None);

        ShouldBeCreated(result.Result);
        ItemBody(result.Result).SourceKind.Should().Be(MediaSourceKind.StepResult.ToString());

        using InferenceApiDbContext reader = fx.NewContext();
        MediaBinItem stored = await reader.MediaBinItems.SingleAsync();
        stored.SourceStepResultId.Should().Be(stepResult.Id);
        stored.SourceProjectFileId.Should().BeNull();
    }

    [Fact]
    public async Task AddItem_ReturnsTheExistingPinWith200_WhenTheSameMediaRefIsAddedTwice()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile file = fx.File();
        string mediaRef = $"file:{file.Id:D}";

        ActionResult<MediaBinItemResponse> first = await fx.Controller.AddItem(
            fx.ProjectId, bin.Id, new AddMediaBinItemRequest(mediaRef), CancellationToken.None);
        ActionResult<MediaBinItemResponse> second = await fx.Controller.AddItem(
            fx.ProjectId, bin.Id, new AddMediaBinItemRequest(mediaRef), CancellationToken.None);

        ShouldBeCreated(first.Result);
        second.Result.Should().BeOfType<OkObjectResult>("a duplicate add is 200, so the two outcomes stay distinguishable");
        ItemBody(second.Result).Id.Should().Be(ItemBody(first.Result).Id, "the existing pin is returned, not a copy");

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.CountAsync()).Should().Be(1, "a duplicate row would render twice and then delete only once");
    }

    [Fact]
    public async Task AddItem_TreatsADifferentSpellingOfTheSameIdAsTheSamePin()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile file = fx.File();

        ActionResult<MediaBinItemResponse> first = await fx.Controller.AddItem(
            fx.ProjectId, bin.Id, new AddMediaBinItemRequest($"file:{file.Id:D}"), CancellationToken.None);

        // Upper-cased prefix and the braced guid spelling: the payload is parsed, not string-compared,
        // so both spellings name the same reference and must not create a second pin.
        ActionResult<MediaBinItemResponse> second = await fx.Controller.AddItem(
            fx.ProjectId,
            bin.Id,
            new AddMediaBinItemRequest($"FILE:{file.Id.ToString("B").ToUpperInvariant()}"),
            CancellationToken.None);

        ShouldBeCreated(first.Result);
        second.Result.Should().BeOfType<OkObjectResult>();
        ItemBody(second.Result).MediaRef.Should().Be($"file:{file.Id:D}");

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AddItem_PinsTheSameMediaRefIndependentlyInTwoBins()
    {
        using Fixture fx = new();
        MediaBin firstBin = fx.Bin("First");
        MediaBin secondBin = fx.Bin("Second");
        ProjectFile file = fx.File();
        string mediaRef = $"file:{file.Id:D}";

        ActionResult<MediaBinItemResponse> first = await fx.Controller.AddItem(
            fx.ProjectId, firstBin.Id, new AddMediaBinItemRequest(mediaRef), CancellationToken.None);
        ActionResult<MediaBinItemResponse> second = await fx.Controller.AddItem(
            fx.ProjectId, secondBin.Id, new AddMediaBinItemRequest(mediaRef), CancellationToken.None);

        // Idempotency is per BIN, not per project: the same media may sit in two bins.
        ShouldBeCreated(first.Result);
        ShouldBeCreated(second.Result);
        ItemBody(second.Result).Id.Should().NotBe(ItemBody(first.Result).Id);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task AddItem_AppendsToTheEndOfTheBinsItems()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile first = fx.File(fileName: "first.mp4");
        ProjectFile second = fx.File(fileName: "second.mp4");

        MediaBinItemResponse firstPin = ItemBody((await fx.Controller.AddItem(
            fx.ProjectId, bin.Id, new AddMediaBinItemRequest($"file:{first.Id:D}"), CancellationToken.None)).Result);
        MediaBinItemResponse secondPin = ItemBody((await fx.Controller.AddItem(
            fx.ProjectId, bin.Id, new AddMediaBinItemRequest($"file:{second.Id:D}"), CancellationToken.None)).Result);

        secondPin.SortOrder.Should().BeGreaterThan(firstPin.SortOrder);
    }

    // =============================================================================================
    // The listing — the itemId the panel needs, and the fields it needs to render the row
    // =============================================================================================

    [Fact]
    public async Task ListItems_ReturnsAnEmptyList_ForAnEmptyBin()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("Empty");

        ActionResult<List<MediaBinItemDetailResponse>> result =
            await ListItems(fx.Controller, fx.ProjectId, bin.Id);

        result.Result.Should().BeOfType<OkObjectResult>(
            "an empty bin exists and is a state the panel renders, so it is not a 404");
        ItemRows(result.Result).Should().BeEmpty();
    }

    [Fact]
    public async Task ListItems_EnrichesAProjectFilePin_WithTheFilesOwnFields()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile file = fx.File();
        MediaBinItem pin = fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id);
        string before = fx.State();

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, bin.Id));

        // The six existing fields, unchanged in name, order and meaning.
        row.Id.Should().Be(pin.Id);
        row.BinId.Should().Be(bin.Id);
        row.MediaRef.Should().Be($"file:{file.Id:D}");
        row.SourceKind.Should().Be(MediaSourceKind.ProjectFile.ToString());
        row.SortOrder.Should().Be(pin.SortOrder);
        row.CreatedAt.Should().Be(pin.CreatedAt);

        // The four fields resolved from the source itself — not from its storage key.
        row.Kind.Should().Be(MediaKind.Video);
        row.FileName.Should().Be("clip.mp4");
        row.MimeType.Should().Be("video/mp4");
        row.SizeBytes.Should().Be(1024);

        fx.State().Should().Be(before, "a listing writes nothing");
    }

    [Fact]
    public async Task ListItems_EnrichesAStepResultPin_FromItsObjectKey_AndReportsNoSize()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        WorkflowStepResult render = fx.StepResult();
        fx.Item(bin.Id, MediaSourceKind.StepResult, render.Id);

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, bin.Id));

        row.MediaRef.Should().Be($"step_result:{render.Id:D}");
        row.Kind.Should().Be(MediaKind.Video);
        row.FileName.Should().Be("render.mp4");
        row.MimeType.Should().Be("video/mp4");
        row.SizeBytes.Should().BeNull(
            "a step result records no object size, and this endpoint does not HEAD the store to invent one");
    }

    [Fact]
    public async Task ListItems_OrdersBySortOrder_ThenById()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile file = fx.File();
        Guid lowId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        Guid highId = Guid.Parse("ffffffff-0000-0000-0000-000000000002");

        // Seeded deliberately out of order, and the two SAME-order pins are seeded highest-id-first,
        // so neither the store's insertion order nor a missing id tie-break can pass this.
        MediaBinItem secondInOrder = fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id, sortOrder: 0, id: highId);
        MediaBinItem lastInOrder = fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id, sortOrder: 5, id: Guid.NewGuid());
        MediaBinItem firstInOrder = fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id, sortOrder: 0, id: lowId);

        List<MediaBinItemDetailResponse> rows = ItemRows((await ListItems(fx.Controller, fx.ProjectId, bin.Id)).Result);

        rows.Select(r => r.Id).Should().Equal(firstInOrder.Id, secondInOrder.Id, lastInOrder.Id);
        rows.Select(r => r.SortOrder).Should().Equal(0, 0, 5);
    }

    [Fact]
    public async Task ListItems_ReturnsADanglingProjectFilePin_WithNullFields()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile file = fx.File();
        Guid deletedFileId = file.Id;

        // The file is deleted after the pin exists — the case the pin's lack of an FK exists for.
        fx.Db.ProjectFiles.Remove(file);
        fx.Db.SaveChanges();

        MediaBinItem pin = fx.Item(bin.Id, MediaSourceKind.ProjectFile, deletedFileId);

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, bin.Id));

        row.Id.Should().Be(pin.Id, "the pin still exists, so the client must still be able to remove it");
        row.MediaRef.Should().Be($"file:{deletedFileId:D}");
        row.Kind.Should().BeNull();
        row.FileName.Should().BeNull();
        row.MimeType.Should().BeNull();
        row.SizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task ListItems_ReturnsADanglingPin_ForAFileIdThatNeverExisted()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        Guid neverExisted = Guid.NewGuid();
        MediaBinItem pin = fx.Item(bin.Id, MediaSourceKind.ProjectFile, neverExisted);

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, bin.Id));

        row.Id.Should().Be(pin.Id);
        row.MediaRef.Should().Be($"file:{neverExisted:D}");
        row.Kind.Should().BeNull();
        row.FileName.Should().BeNull();
        row.MimeType.Should().BeNull();
        row.SizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task ListItems_ReturnsNullFields_ForAPinNamingAnotherProjectsFile()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        MediaBin ourBin = fx.Bin("Ours");

        // A leaked pin (the write route refuses this, a later move of ownership could still leave it):
        // the other project's file name and size must not appear in our project's answer.
        MediaBinItem pin = fx.Item(ourBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, ourBin.Id));

        row.Id.Should().Be(pin.Id);
        row.Kind.Should().BeNull();
        row.FileName.Should().BeNull();
        row.MimeType.Should().BeNull();
        row.SizeBytes.Should().BeNull();

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.AnyAsync(i => i.BinId == foreignBin.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ListItems_ReturnsNullFields_ForAStepResultOfAnotherProject()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        WorkflowStepResult foreignRender = fx.StepResult(foreign.Id);
        MediaBin ourBin = fx.Bin("Ours");
        MediaBinItem pin = fx.Item(ourBin.Id, MediaSourceKind.StepResult, foreignRender.Id);

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, ourBin.Id));

        row.Id.Should().Be(pin.Id, "a step result has no ProjectId of its own, so this is where a naive lookup would leak a render");
        row.Kind.Should().BeNull();
        row.FileName.Should().BeNull();
        row.MimeType.Should().BeNull();
        row.SizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task ListItems_ReturnsNullFields_ForAStepResultWhoseKeyIsAbsentOrOutsideThisProjectsPrefix()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin bin = fx.Bin("bin");

        // Two ways a render's key stops being ours: it was never written, or it points at a prefix
        // this project does not own. Both are dangling, and neither may fall back to the key's name.
        WorkflowStepResult noKey = fx.StepResultWithKey(outputStorageKey: null);
        WorkflowStepResult foreignKey = fx.StepResultWithKey(
            outputStorageKey: $"projects/{foreign.Id:D}/outputFiles/render.mp4");

        MediaBinItem noKeyPin = fx.Item(bin.Id, MediaSourceKind.StepResult, noKey.Id, sortOrder: 0);
        MediaBinItem foreignKeyPin = fx.Item(bin.Id, MediaSourceKind.StepResult, foreignKey.Id, sortOrder: 1);

        List<MediaBinItemDetailResponse> rows = ItemRows((await ListItems(fx.Controller, fx.ProjectId, bin.Id)).Result);

        rows.Should().HaveCount(2, "a dangling pointer is never a dropped row");
        rows.Select(r => r.Id).Should().Equal(noKeyPin.Id, foreignKeyPin.Id);

        foreach (MediaBinItemDetailResponse row in rows)
        {
            row.Kind.Should().BeNull();
            row.FileName.Should().BeNull();
            row.MimeType.Should().BeNull();
            row.SizeBytes.Should().BeNull();
        }
    }

    [Fact]
    public async Task ListItems_ReturnsEveryPin_WhenOnlySomeResolve()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("Mixed");
        ProjectFile file = fx.File();
        WorkflowStepResult render = fx.StepResult();
        Guid danglingId = Guid.NewGuid();

        fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id, sortOrder: 0);
        fx.Item(bin.Id, MediaSourceKind.StepResult, render.Id, sortOrder: 1);
        fx.Item(bin.Id, MediaSourceKind.ProjectFile, danglingId, sortOrder: 2);

        List<MediaBinItemDetailResponse> rows = ItemRows((await ListItems(fx.Controller, fx.ProjectId, bin.Id)).Result);

        rows.Should().HaveCount(3);
        rows[0].FileName.Should().Be("clip.mp4");
        rows[1].FileName.Should().Be("render.mp4");
        rows[2].FileName.Should().BeNull();
        rows[2].MediaRef.Should().Be($"file:{danglingId:D}", "even a dangling row keeps the token the client removes it by");
    }

    [Fact]
    public async Task ListItems_ReturnsTheItemIdTheRemoveRouteNeeds()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile file = fx.File();
        fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id);

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, bin.Id));

        // The whole point of the route: add → list → remove, with the listing supplying the id.
        IActionResult removed = await fx.Controller.DeleteItem(fx.ProjectId, bin.Id, row.Id, CancellationToken.None);

        removed.Should().BeOfType<NoContentResult>();
        ItemRows((await ListItems(fx.Controller, fx.ProjectId, bin.Id)).Result).Should().BeEmpty();
    }

    // --- Ownership, scoping and the wire contract -----------------------------------------------

    [Fact]
    public async Task ListItems_ReturnsNotFound_NotForbid_ForAForeignProject_AndWritesNothing()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        fx.Item(foreignBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);
        string before = fx.State();

        ActionResult<List<MediaBinItemDetailResponse>> result =
            await ListItems(fx.Controller, foreign.Id, foreignBin.Id);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before, "a refused read writes nothing");
    }

    [Fact]
    public async Task ListItems_ReturnsNotFound_NotForbid_ForAForeignBin_AndWritesNothing()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        ProjectFile foreignFile = fx.File(foreign.Id);
        fx.Item(foreignBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);
        string before = fx.State();

        // Our project, somebody else's bin: scoped by ProjectId as well as id, so it is exactly as
        // absent as a bin that does not exist — and the foreign items must not be listed.
        ActionResult<List<MediaBinItemDetailResponse>> result =
            await ListItems(fx.Controller, fx.ProjectId, foreignBin.Id);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task ListItems_ReturnsNotFound_WhenTheProjectDoesNotExist()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");

        ActionResult<List<MediaBinItemDetailResponse>> result =
            await ListItems(fx.Controller, Guid.NewGuid(), bin.Id);

        ShouldBeNotFoundNotForbid(result.Result);
    }

    [Fact]
    public async Task ListItems_ReturnsNotFound_WhenTheBinIsSoftDeleted()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin", deletedAt: DateTime.UtcNow);

        ActionResult<List<MediaBinItemDetailResponse>> result =
            await ListItems(fx.Controller, fx.ProjectId, bin.Id);

        ShouldBeNotFoundNotForbid(result.Result);
    }

    [Fact]
    public async Task ListItems_ReturnsNotFound_WhenTheBinDoesNotExist()
    {
        using Fixture fx = new();

        ActionResult<List<MediaBinItemDetailResponse>> result =
            await ListItems(fx.Controller, fx.ProjectId, Guid.NewGuid());

        ShouldBeNotFoundNotForbid(result.Result);
    }

    [Fact]
    public async Task ListItems_ResponseCarriesTheWireShape_WithNoStorageKeyBucketOrObjectUrl()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");

        // A key with a directory and a guid in it, so a key leaking into any field is unmistakable.
        ProjectFile file = fx.FileWith(
            storageKey: $"projects/{fx.ProjectId:D}/userFiles/{Guid.NewGuid():N}/clip.mp4");
        WorkflowStepResult render = fx.StepResult();

        fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id, sortOrder: 0);
        fx.Item(bin.Id, MediaSourceKind.StepResult, render.Id, sortOrder: 1);

        List<MediaBinItemDetailResponse> rows = ItemRows((await ListItems(fx.Controller, fx.ProjectId, bin.Id)).Result);
        string json = JsonSerializer.Serialize(rows, WireJson);

        // Property names AND their order: the first six are the add/move shape's, unchanged.
        JsonElement first = JsonSerializer.SerializeToElement(rows[0], WireJson);

        first.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id",
            "binId",
            "mediaRef",
            "sourceKind",
            "sortOrder",
            "createdAt",
            "kind",
            "fileName",
            "mimeType",
            "sizeBytes");

        // The kind is the camelCase wire string, not the enum's ordinal.
        first.GetProperty("kind").GetString().Should().Be("video");
        first.GetProperty("sizeBytes").GetInt64().Should().Be(1024);

        json.Should().Contain("clip.mp4");
        json.Should().NotContain(file.StorageKey, "an object key is internal to the service");
        json.Should().NotContain(render.OutputStorageKey!, "an object key is internal to the service");
        json.Should().NotContain(file.StorageBucket, "a bucket name is internal to the service");
        json.Should().NotContain("projects/", "no field may carry a key prefix");
        json.Should().NotContain("http", "no field may carry an object URL");
    }

    [Fact]
    public async Task ListItems_EncodesANullKindAsJsonNull()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        fx.Item(bin.Id, MediaSourceKind.ProjectFile, Guid.NewGuid());

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, bin.Id));
        JsonElement wire = JsonSerializer.SerializeToElement(row, WireJson);

        // Null, not "0" (the enum's ordinal) and not a fabricated "video".
        wire.GetProperty("kind").ValueKind.Should().Be(JsonValueKind.Null);
        wire.GetProperty("fileName").ValueKind.Should().Be(JsonValueKind.Null);
        wire.GetProperty("mimeType").ValueKind.Should().Be(JsonValueKind.Null);
        wire.GetProperty("sizeBytes").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListItems_ReportsNoKind_ForAFileWhoseMimeTypeIsMissing()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");

        ProjectFile file = fx.FileWith(storageKey: $"projects/{fx.ProjectId:D}/userFiles/{Guid.NewGuid():N}/blob");
        file.OriginalFileName = string.Empty;
        file.MimeType = string.Empty;
        fx.Db.SaveChanges();

        fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id);

        MediaBinItemDetailResponse row = SingleRow(await ListItems(fx.Controller, fx.ProjectId, bin.Id));

        // A file with no recorded MIME type has no kind; null is the honest answer, and the fallback
        // name is the object's base name — never its key.
        row.Kind.Should().BeNull();
        row.MimeType.Should().BeNull();
        row.FileName.Should().Be("blob");
    }

    // =============================================================================================
    // Move — a move must never be the one way a duplicate row can appear
    // =============================================================================================

    [Fact]
    public async Task MoveItem_MovesTheItemToTheTargetBin()
    {
        using Fixture fx = new();
        MediaBin sourceBin = fx.Bin("Source");
        MediaBin targetBin = fx.Bin("Target");
        ProjectFile file = fx.File();
        MediaBinItem item = fx.Item(sourceBin.Id, MediaSourceKind.ProjectFile, file.Id);

        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            fx.ProjectId,
            sourceBin.Id,
            item.Id,
            new MoveMediaBinItemRequest(targetBin.Id),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        ItemBody(result.Result).Id.Should().Be(item.Id);
        ItemBody(result.Result).BinId.Should().Be(targetBin.Id);

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.SingleAsync()).BinId.Should().Be(targetBin.Id);
    }

    [Fact]
    public async Task MoveItem_WhenTheTargetBinAlreadyHoldsTheMedia_RemovesTheMovedRowAndCreatesNoDuplicate()
    {
        using Fixture fx = new();
        MediaBin sourceBin = fx.Bin("Source");
        MediaBin targetBin = fx.Bin("Target");
        ProjectFile file = fx.File();
        MediaBinItem moved = fx.Item(sourceBin.Id, MediaSourceKind.ProjectFile, file.Id);
        MediaBinItem existing = fx.Item(targetBin.Id, MediaSourceKind.ProjectFile, file.Id);

        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            fx.ProjectId,
            sourceBin.Id,
            moved.Id,
            new MoveMediaBinItemRequest(targetBin.Id),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        ItemBody(result.Result).Id.Should().Be(existing.Id, "the pin that was already there wins");

        using InferenceApiDbContext reader = fx.NewContext();
        List<MediaBinItem> items = await reader.MediaBinItems.ToListAsync();
        items.Should().HaveCount(1, "a move onto an existing pin must collapse, not duplicate");
        items[0].Id.Should().Be(existing.Id);
        items[0].BinId.Should().Be(targetBin.Id);
    }

    [Fact]
    public async Task MoveItem_MovingWithinTheSameBin_ReturnsTheItemUnchanged()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("bin");
        ProjectFile file = fx.File();
        MediaBinItem item = fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id, sortOrder: 7);
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            fx.ProjectId,
            bin.Id,
            item.Id,
            new MoveMediaBinItemRequest(bin.Id),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        ItemBody(result.Result).SortOrder.Should().Be(7, "a no-op move must not rewrite the sort order");
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task MoveItem_ReturnsBadRequest_WhenTheBodyCarriesNoBinId()
    {
        using Fixture fx = new();
        MediaBin sourceBin = fx.Bin("Source");
        ProjectFile file = fx.File();
        MediaBinItem item = fx.Item(sourceBin.Id, MediaSourceKind.ProjectFile, file.Id);
        string before = fx.State();

        ActionResult<MediaBinItemResponse> result = await fx.Controller.MoveItem(
            fx.ProjectId,
            sourceBin.Id,
            item.Id,
            new MoveMediaBinItemRequest(null),
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.InvalidBinIdError);
        fx.State().Should().Be(before, "an absent binId must not be read as Guid.Empty and matched by accident");
    }

    // =============================================================================================
    // The rest of the bins contract: PATCH's three parent states, names, ordering, item counts
    // =============================================================================================

    [Fact]
    public async Task CreateBin_CreatesARootBin_WhenParentBinIdIsAbsent()
    {
        using Fixture fx = new();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            fx.ProjectId,
            new CreateMediaBinRequest("Root", null),
            CancellationToken.None);

        ShouldBeCreated(result.Result);

        MediaBinSummaryResponse body = BinBody(result.Result);
        body.ParentBinId.Should().BeNull();
        body.SortOrder.Should().Be(0);
        body.ItemCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateBin_ReturnsNotFound_WhenParentBinIdIsNotInThisProject()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            fx.ProjectId,
            new CreateMediaBinRequest("Orphan", foreignBin.Id),
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task CreateBin_AppendsToTheEndOfItsParentsChildren()
    {
        using Fixture fx = new();
        MediaBin parent = fx.Bin("Parent");
        fx.Bin("First child", parentBinId: parent.Id, sortOrder: 0);
        fx.Bin("Second child", parentBinId: parent.Id, sortOrder: 1);

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            fx.ProjectId,
            new CreateMediaBinRequest("Third child", parent.Id),
            CancellationToken.None);

        ShouldBeCreated(result.Result);
        BinBody(result.Result).SortOrder.Should().Be(2);
    }

    [Theory]
    [InlineData(null, MediaBinsController.BinNameRequiredError)]
    [InlineData("", MediaBinsController.BinNameRequiredError)]
    [InlineData("   ", MediaBinsController.BinNameRequiredError)]
    public async Task CreateBin_ReturnsBadRequest_WhenTheNameIsBlank(string? name, string expectedError)
    {
        using Fixture fx = new();
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            fx.ProjectId,
            new CreateMediaBinRequest(name, null),
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(expectedError);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task CreateBin_ReturnsBadRequest_WhenTheNameIsLongerThanTheColumn()
    {
        using Fixture fx = new();
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            fx.ProjectId,
            new CreateMediaBinRequest(new string('a', 121), null),
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinNameTooLongError);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task CreateBin_TrimsTheNameBeforeCheckingItsLength()
    {
        using Fixture fx = new();
        string padded = "  " + new string('a', 120) + "  ";

        // 124 raw characters but 120 after trimming: the trim happens FIRST, which is what makes
        // "  x  " a one-character name rather than a five-character one.
        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.CreateBin(
            fx.ProjectId,
            new CreateMediaBinRequest(padded, null),
            CancellationToken.None);

        ShouldBeCreated(result.Result);
        BinBody(result.Result).Name.Should().Be(new string('a', 120));
    }

    [Fact]
    public async Task PatchBin_WithParentBinIdAbsent_KeepsTheParentAndTheSortOrder()
    {
        using Fixture fx = new();
        MediaBin parent = fx.Bin("Parent");
        MediaBin bin = fx.Bin("Old name", parentBinId: parent.Id, sortOrder: 5);

        // State one of three: the caller never mentioned parentBinId, so this is a pure rename — and
        // a pure rename must not reorder the panel.
        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            bin.Id,
            new PatchMediaBinRequest { Name = "New name", ParentBinId = AbsentParent },
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();

        MediaBinSummaryResponse body = BinBody(result.Result);
        body.Name.Should().Be("New name");
        body.ParentBinId.Should().Be(parent.Id);
        body.SortOrder.Should().Be(5);
    }

    [Fact]
    public async Task PatchBin_WithExplicitNull_MovesTheBinToTheRoot()
    {
        using Fixture fx = new();
        MediaBin parent = fx.Bin("Parent");
        MediaBin bin = fx.Bin("Nested", parentBinId: parent.Id, sortOrder: 4);

        // State two of three: an explicit JSON null. This is the request a Guid? parent could not
        // represent at all — absent and null would arrive identically — so it is the case that makes
        // the JsonElement DTO load-bearing.
        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            bin.Id,
            new PatchMediaBinRequest { ParentBinId = RootParent },
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        BinBody(result.Result).ParentBinId.Should().BeNull();
        BinBody(result.Result).Name.Should().Be("Nested", "an omitted name is 'no change', not 'clear it'");

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBins.SingleAsync(b => b.Id == bin.Id)).ParentBinId.Should().BeNull();
    }

    [Fact]
    public async Task PatchBin_WithAGuid_ReparentsTheBin()
    {
        using Fixture fx = new();
        MediaBin oldParent = fx.Bin("Old parent");
        MediaBin newParent = fx.Bin("New parent");
        MediaBin bin = fx.Bin("Mover", parentBinId: oldParent.Id);
        ProjectFile file = fx.File();
        fx.Item(bin.Id, MediaSourceKind.ProjectFile, file.Id);

        // State three of three: a guid.
        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            bin.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(newParent.Id.ToString("D")) },
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        BinBody(result.Result).ParentBinId.Should().Be(newParent.Id);
        BinBody(result.Result).ItemCount.Should().Be(1, "a reparent keeps the bin's own items");

        using InferenceApiDbContext reader = fx.NewContext();
        (await reader.MediaBinItems.SingleAsync()).BinId.Should().Be(bin.Id);
    }

    [Theory]
    [InlineData("\"not-a-guid\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public async Task PatchBin_ReturnsBadRequest_WhenParentBinIdIsNeitherAGuidNorNull(string rawParent)
    {
        using Fixture fx = new();
        MediaBin parent = fx.Bin("Parent");
        MediaBin bin = fx.Bin("bin", parentBinId: parent.Id);
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            bin.Id,
            new PatchMediaBinRequest { Name = "Renamed", ParentBinId = RawJson(rawParent) },
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.InvalidParentBinIdError);
        fx.State().Should().Be(before, "an unusable parentBinId is a 400 before anything is looked up, and nothing is written");
    }

    [Fact]
    public async Task PatchBin_ReturnsNotFound_WhenParentBinIdIsAnotherProjectsBin()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin foreignBin = fx.Bin("Foreign bin", projectId: foreign.Id);
        MediaBin bin = fx.Bin("bin");
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            bin.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(foreignBin.Id.ToString("D")) },
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task PatchBin_ReturnsNotFound_WhenParentBinIdIsSoftDeleted()
    {
        using Fixture fx = new();
        MediaBin deleted = fx.Bin("Deleted", deletedAt: DateTime.UtcNow);
        MediaBin bin = fx.Bin("bin");
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            bin.Id,
            new PatchMediaBinRequest { ParentBinId = ParentToken(deleted.Id.ToString("D")) },
            CancellationToken.None);

        ShouldBeNotFoundNotForbid(result.Result);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task PatchBin_ReturnsBadRequest_AndKeepsTheOldName_WhenTheNewNameIsTooLong()
    {
        using Fixture fx = new();
        MediaBin bin = fx.Bin("Kept");
        string before = fx.State();

        ActionResult<MediaBinSummaryResponse> result = await fx.Controller.PatchBin(
            fx.ProjectId,
            bin.Id,
            new PatchMediaBinRequest { Name = new string('a', 121) },
            CancellationToken.None);

        ErrorCodeOf(result.Result).Should().Be(MediaBinsController.BinNameTooLongError);
        fx.State().Should().Be(before);
    }

    [Fact]
    public async Task ListBins_ReturnsEveryLiveBinWithItsOwnItemCount_InAStableOrder()
    {
        using Fixture fx = new();
        MediaBin alpha = fx.Bin("Alpha", sortOrder: 1);
        MediaBin beta = fx.Bin("Beta", sortOrder: 0);
        MediaBin gamma = fx.Bin("Gamma", sortOrder: 2);
        MediaBin deleted = fx.Bin("Gone", sortOrder: 3, deletedAt: DateTime.UtcNow);

        ProjectFile file = fx.File();
        fx.Item(alpha.Id, MediaSourceKind.ProjectFile, file.Id);
        fx.Item(alpha.Id, MediaSourceKind.StepResult, Guid.NewGuid());

        ActionResult<List<MediaBinSummaryResponse>> result =
            await fx.Controller.ListBins(fx.ProjectId, CancellationToken.None);

        List<MediaBinSummaryResponse> body =
            result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<List<MediaBinSummaryResponse>>().Subject;

        body.Select(b => b.Id).Should().ContainInOrder(beta.Id, alpha.Id, gamma.Id);
        body.Should().NotContain(b => b.Id == deleted.Id, "a soft-deleted bin is not listed");

        body.Single(b => b.Id == alpha.Id).ItemCount.Should().Be(2, "the count is the bin's OWN items");
        body.Single(b => b.Id == beta.Id).ItemCount.Should().Be(0);
    }

    [Fact]
    public async Task ListBins_CountsOnlyTheBinsOwnItems_NotItsDescendants()
    {
        using Fixture fx = new();
        MediaBin parent = fx.Bin("Parent");
        MediaBin child = fx.Bin("Child", parentBinId: parent.Id);
        ProjectFile file = fx.File();
        fx.Item(child.Id, MediaSourceKind.ProjectFile, file.Id);

        ActionResult<List<MediaBinSummaryResponse>> result =
            await fx.Controller.ListBins(fx.ProjectId, CancellationToken.None);

        List<MediaBinSummaryResponse> body =
            result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<List<MediaBinSummaryResponse>>().Subject;

        body.Single(b => b.Id == parent.Id).ItemCount.Should().Be(0);
        body.Single(b => b.Id == child.Id).ItemCount.Should().Be(1);
    }

    [Fact]
    public async Task ListBins_NeverReportsAnotherProjectsBins()
    {
        using Fixture fx = new();
        Project foreign = fx.AddForeignProject();
        MediaBin ours = fx.Bin("Ours");
        fx.Bin("Theirs", projectId: foreign.Id);

        ActionResult<List<MediaBinSummaryResponse>> result =
            await fx.Controller.ListBins(fx.ProjectId, CancellationToken.None);

        List<MediaBinSummaryResponse> body =
            result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<List<MediaBinSummaryResponse>>().Subject;

        body.Select(b => b.Id).Should().BeEquivalentTo([ours.Id]);
    }

    /// <summary>Raw JSON as a <see cref="JsonElement"/>, detached from any document.</summary>
    private static JsonElement RawJson(string raw)
    {
        using JsonDocument document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
