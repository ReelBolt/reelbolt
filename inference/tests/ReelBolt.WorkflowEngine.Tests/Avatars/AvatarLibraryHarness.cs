#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Scaffolding for the workspace avatar library tests (lane L9 of docs/design/avatar-objects.md), on top of
/// <see cref="AvatarTestHarness"/>: an in-memory library store that shares the harness's byte store (so a copy between a
/// project key and a library key is a real copy of real bytes), a plan gate whose storage answer a test can set, both
/// avatar switches on, and helpers that publish and import through the real controllers.
/// </summary>
internal sealed class AvatarLibraryHarness
{
    /// <summary>The library object store over the harness's bytes. It checks its keys exactly like the S3 store.</summary>
    internal sealed class FakeStore(ConcurrentDictionary<string, byte[]> objects) : IAvatarLibraryStore
    {
        public HashSet<string> FailingDeletes { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public Dictionary<string, DateTime> Modified { get; } = new(StringComparer.Ordinal);
        public bool FailCopiesIntoProject { get; set; }

        /// <summary>Runs inside every listing of an item's folder: the seam between a delete's first reads and its guarded section.</summary>
        public Action? OnList { get; set; }

        /// <summary>Runs before a copy into the library and before a copy into a project: the seam outside the organization's critical section.</summary>
        public Action? OnCopy { get; set; }

        public Task CopyIntoLibraryAsync(Guid projectId, string projectKey, string libraryKey, CancellationToken ct)
        {
            OnCopy?.Invoke();
            RequireProject(projectId, projectKey);
            RequireLibrary(libraryKey);
            if (!objects.TryGetValue(projectKey, out byte[]? bytes)) throw new FileNotFoundException(projectKey);
            objects[libraryKey] = [.. bytes];
            return Task.CompletedTask;
        }

        public Task CopyIntoProjectAsync(string libraryKey, Guid projectId, string projectKey, CancellationToken ct)
        {
            OnCopy?.Invoke();
            RequireLibrary(libraryKey);
            RequireProject(projectId, projectKey);
            if (FailCopiesIntoProject) throw new IOException("store unavailable");
            if (!objects.TryGetValue(libraryKey, out byte[]? bytes)) throw new FileNotFoundException(libraryKey);
            if (objects.ContainsKey(projectKey)) throw new InvalidOperationException("an import must never overwrite a project object");
            objects[projectKey] = [.. bytes];
            return Task.CompletedTask;
        }

        public Task<Stream?> OpenReadAsync(string libraryKey, CancellationToken ct)
        {
            RequireLibrary(libraryKey);
            return Task.FromResult<Stream?>(objects.TryGetValue(libraryKey, out byte[]? bytes) ? new MemoryStream(bytes, writable: false) : null);
        }

        public Task DeleteAsync(string libraryKey, CancellationToken ct)
        {
            RequireLibrary(libraryKey);
            if (FailingDeletes.Contains(libraryKey)) throw new IOException("store unavailable");
            Deleted.Add(libraryKey);
            objects.TryRemove(libraryKey, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredObjectEntry>> ListAsync(string prefix, CancellationToken ct)
        {
            OnList?.Invoke();
            if (!S3AvatarLibraryStore.IsLibraryPrefix(prefix)) throw new UnauthorizedAccessException(prefix);
            return Task.FromResult(Under(prefix));
        }

        public Task<IReadOnlyList<StoredObjectEntry>> ListAllAsync(CancellationToken ct) => Task.FromResult(Under("organizations/"));

        /// <summary>Every library key the store holds.</summary>
        public List<string> Keys() => [.. objects.Keys.Where(k => AvatarLibraryStorage.Parse(k) is not null).Order(StringComparer.Ordinal)];

        private IReadOnlyList<StoredObjectEntry> Under(string prefix) =>
            [.. objects.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && AvatarLibraryStorage.Parse(k) is not null)
                .Select(k => new StoredObjectEntry(k, Modified.TryGetValue(k, out DateTime at) ? at : DateTime.UtcNow))];

        private static void RequireLibrary(string key)
        {
            if (AvatarLibraryStorage.Parse(key) is null) throw new UnauthorizedAccessException(key);
        }

        private static void RequireProject(Guid projectId, string key)
        {
            if (!key.StartsWith($"projects/{projectId}/", StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal))
                throw new UnauthorizedAccessException(key);
        }
    }

    /// <summary>The plan gate, reduced to the one answer the library asks for: does this many more bytes fit?</summary>
    internal sealed class FakePlanGate : IPlanGateService
    {
        public PlanGateViolation? Storage { get; set; }
        public List<long> Asked { get; } = [];

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) => Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            Asked.Add(incomingBytes);
            return Task.FromResult(Storage);
        }

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }

    public H Harness { get; } = new();
    public FakeStore Store { get; }
    public FakePlanGate PlanGate { get; } = new();

    public AvatarLibraryHarness()
    {
        Harness.Options.Objects.Enabled = true;
        Store = new FakeStore(Harness.Objects);
        Harness.Storage.Setup(s => s.BucketName).Returns("test-bucket");
    }

    public Guid OrganizationId => Harness.OrganizationId;
    public Project Project => Harness.Project;

    public InferenceApiDbContext NewDb(bool intercepted = false) => Harness.NewDb(intercepted);

    public AvatarLibraryService Library(InferenceApiDbContext? db = null)
    {
        db ??= NewDb();
        return new AvatarLibraryService(
            db, Store, Harness.Storage.Object, Harness.Deletion(db), Microsoft.Extensions.Options.Options.Create(Harness.Options),
            NullLogger<AvatarLibraryService>.Instance, Harness.Entitlements, PlanGate);
    }

    public AvatarLibraryController Controller(InferenceApiDbContext? db = null, ICurrentUser? user = null)
    {
        db ??= NewDb();
        user ??= Harness.Owner();
        return new AvatarLibraryController(Library(db), user, TestTenancy.Access(db, user))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>Another project of the same organization.</summary>
    public Project AddProject(string name = "Second") => AddProject(name, Harness.OwnerId, OrganizationId);

    public Project AddProject(string name, Guid ownerId, Guid organizationId)
    {
        var project = new Project { Id = Guid.NewGuid(), Name = name, OwnerId = ownerId, OrganizationId = organizationId };
        using InferenceApiDbContext db = NewDb();
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    public static PublishToAvatarLibraryRequest PersonRequest(Guid projectId, Guid avatarId, int? retentionDays = 120) =>
        new(projectId, avatarId, AvatarWorkspaceConsentStatement.English, "en", SubjectIsAdult: true, RetentionDays: retentionDays);

    public static PublishToAvatarLibraryRequest ObjectRequest(Guid projectId, Guid avatarId) =>
        new(projectId, avatarId, AvatarWorkspaceObjectStatement.English, "en");

    /// <summary>Creates a ready person avatar in the harness project and publishes it; answers (item id, source avatar id).</summary>
    public async Task<(Guid ItemId, Guid AvatarId)> PublishPersonAsync(string name = "Anna", params ProjectFile[] files)
    {
        Guid avatarId = await Harness.CreateReadyAsync(name, files);
        return (await PublishAsync(PersonRequest(Project.Id, avatarId)), avatarId);
    }

    /// <summary>Creates an object avatar in the harness project and publishes it; answers (item id, source avatar id).</summary>
    public async Task<(Guid ItemId, Guid AvatarId)> PublishObjectAsync(string name = "Espresso machine", params ProjectFile[] files)
    {
        Guid avatarId = await Harness.CreateObjectAsync(name, files);
        return (await PublishAsync(ObjectRequest(Project.Id, avatarId)), avatarId);
    }

    public async Task<Guid> PublishAsync(PublishToAvatarLibraryRequest request)
    {
        (int status, JsonElement body) = H.Read(await Controller().Publish(request, CancellationToken.None));
        if (status != 201) throw new InvalidOperationException("publish failed: " + status + " " + body);
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>Imports an item into <paramref name="project"/> through the project route; answers the new avatar's id.</summary>
    public async Task<Guid> ImportAsync(Guid itemId, Project project, ICurrentUser? user = null)
    {
        (int status, JsonElement body) = await ImportResultAsync(itemId, project, user);
        if (status != 201) throw new InvalidOperationException("import failed: " + status + " " + body);
        return body.GetProperty("id").GetGuid();
    }

    public async Task<(int Status, JsonElement Body)> ImportResultAsync(Guid itemId, Project project, ICurrentUser? user = null)
    {
        InferenceApiDbContext db = NewDb();
        return H.Read(await Harness.Controller(db, user).ImportFromLibrary(
            project.Id, new ImportLibraryAvatarRequest(itemId), Library(db), CancellationToken.None));
    }

    /// <summary>The project file rows an avatar's references point at, in reference order.</summary>
    public List<ProjectFile> FilesOf(Guid avatarId)
    {
        using InferenceApiDbContext db = NewDb();
        List<Guid> ids = [.. db.ProjectAvatarReferences.Where(r => r.AvatarId == avatarId).OrderBy(r => r.Ordinal).Select(r => r.ProjectFileId)];
        Dictionary<Guid, ProjectFile> files = db.ProjectFiles.Where(f => ids.Contains(f.Id)).ToDictionary(f => f.Id);
        return [.. ids.Select(id => files[id])];
    }
}
