#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using Dto = ReelBolt.Inference.Api.Controllers.Dto;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Shared scaffolding for the avatar API tests (lanes A2 and A3 of docs/design/avatars.md): an in-memory database that
/// several contexts can share (so a race can be staged from a second context), a byte store, configurable user, role,
/// email verification, plan, moderation and CSAM switches.
/// </summary>
internal sealed class AvatarTestHarness
{
    internal sealed class User(Guid userId, Guid organizationId, OrganizationRole role, bool emailVerified) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "owner@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
        public bool EmailVerified { get; } = emailVerified;
    }

    internal sealed class FixedEntitlements(OrganizationEntitlementSet set) : IOrganizationEntitlements
    {
        public OrganizationEntitlementSet Set { get; set; } = set;
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Set);
    }

    internal sealed class RecordingVendorDeleter : IAvatarVendorAssetDeleter
    {
        public AvatarVendorAssetDeleteOutcome Outcome { get; set; } = AvatarVendorAssetDeleteOutcome.Deleted;
        public bool Throws { get; set; }
        public List<string> Deleted { get; } = [];

        public Task<AvatarVendorAssetDeleteOutcome> DeleteAsync(ProjectAvatarVendorAsset asset, CancellationToken ct)
        {
            if (Throws) throw new InvalidOperationException("vendor down");
            Deleted.Add(asset.RemoteAssetId);
            return Task.FromResult(Outcome);
        }
    }

    /// <summary>
    /// The object person check as a test double (docs/design/avatar-objects.md, AO-1): <see cref="Active"/> stands for
    /// "a Vision provider is configured", <see cref="Answer"/> decides per picture, and every picture shown is recorded.
    /// </summary>
    internal sealed class FakePersonCheck : IObjectReferencePersonCheck
    {
        public bool Active { get; set; } = true;
        public Func<byte[], ObjectPersonCheckVerdict> Answer { get; set; } = _ => ObjectPersonCheckVerdict.NoPerson;
        public List<byte[]> Shown { get; } = [];
        public List<(Guid OrganizationId, Guid ProjectId)> Scopes { get; } = [];

        public Task<bool> IsActiveAsync(CancellationToken ct) => Task.FromResult(Active);

        public Task<ObjectPersonCheckVerdict> CheckAsync(Guid organizationId, Guid projectId, byte[] picture, CancellationToken ct)
        {
            Shown.Add(picture);
            Scopes.Add((organizationId, projectId));
            return Task.FromResult(Answer(picture));
        }
    }

    /// <summary>Runs <see cref="Action"/> once, just before the first SaveChanges of the context it is attached to.</summary>
    internal sealed class BeforeFirstSave : SaveChangesInterceptor
    {
        public Func<Task>? Action { get; set; }

        /// <summary>
        /// The in-memory provider does not move <c>xmin</c>, so after <see cref="Action"/> the save fails the way
        /// PostgreSQL's conditional UPDATE does when the row changed under it.
        /// </summary>
        public bool ThenConflict { get; set; } = true;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Action is { } action)
            {
                Action = null;
                await action();
                if (ThenConflict) throw new DbUpdateConcurrencyException("simulated xmin conflict");
            }

            return result;
        }
    }

    public string DatabaseName { get; } = Guid.NewGuid().ToString();
    public Guid OwnerId { get; } = Guid.NewGuid();
    public Guid OrganizationId { get; }
    public Project Project { get; }
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public List<string> DeletedKeys { get; } = [];
    public HashSet<string> FailingDeletes { get; } = new(StringComparer.Ordinal);
    public Mock<IFileStorageService> Storage { get; } = new();
    public AvatarOptions Options { get; } = new() { Enabled = true };
    public ContentModerationOptions Moderation { get; } = new();
    public CsamOptions Csam { get; } = new();
    public FixedEntitlements Entitlements { get; } = new(new OrganizationEntitlementSet { AvatarsAllowed = true });
    public RecordingVendorDeleter Vendor { get; } = new();
    public ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? HistoryEraser { get; set; }
    public BeforeFirstSave Interceptor { get; } = new();

    /// <summary>The object person check the reference checks get: active and answering "no person" by default (the check is mandatory); null = none registered, so objects are refused.</summary>
    public FakePersonCheck? PersonCheck { get; set; } = new();

    /// <summary>The identity-facts reader (docs/design/avatar-objects.md "Object fidelity"); null = none registered.</summary>
    public IAvatarIdentityDescriber? Describer { get; set; }

    public AvatarTestHarness()
    {
        OrganizationId = TestTenancy.OrgOf(OwnerId);
        Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = OwnerId, OrganizationId = OrganizationId };
        using InferenceApiDbContext db = NewDb();
        db.Projects.Add(Project);
        // The owner is a member of their workspace: a build job re-checks that its requester may still edit.
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = OrganizationId, UserId = OwnerId, Role = ReelBolt.Shared.Tenancy.OrganizationRole.Owner });
        db.SaveChanges();

        Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string key, CancellationToken _) =>
                Objects.TryGetValue(key, out byte[]? bytes) ? new MemoryStream(bytes, writable: false) : throw new FileNotFoundException(key));
        Storage.Setup(s => s.DeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string key, CancellationToken _) =>
            {
                if (FailingDeletes.Contains(key)) throw new IOException("store unavailable");
                DeletedKeys.Add(key);
                Objects.TryRemove(key, out _);
                return Task.CompletedTask;
            });
    }

    /// <summary>A fresh context on the shared in-memory store.</summary>
    public InferenceApiDbContext NewDb(bool intercepted = false)
    {
        DbContextOptionsBuilder<InferenceApiDbContext> builder = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(DatabaseName);
        if (intercepted) builder.AddInterceptors(Interceptor);
        return new InferenceApiDbContext(builder.Options);
    }

    public User Owner(bool emailVerified = true) => new(OwnerId, OrganizationId, OrganizationRole.Owner, emailVerified);

    public User Member(Guid? id = null) => new(id ?? Guid.NewGuid(), OrganizationId, OrganizationRole.Member, true);

    public User InRole(OrganizationRole role) => new(Guid.NewGuid(), OrganizationId, role, true);

    public static User Stranger()
    {
        Guid id = Guid.NewGuid();
        return new User(id, TestTenancy.OrgOf(id), OrganizationRole.Owner, true);
    }

    public AvatarReferenceChecks Checks(InferenceApiDbContext db) =>
        new(db, Storage.Object, Microsoft.Extensions.Options.Options.Create(Moderation),
            Microsoft.Extensions.Options.Options.Create(Csam), NullLogger<AvatarReferenceChecks>.Instance, personCheck: PersonCheck);

    public ProjectAvatarDeletion Deletion(InferenceApiDbContext db) =>
        new(db, Storage.Object, Vendor, NullLogger<ProjectAvatarDeletion>.Instance, historyEraser: HistoryEraser);

    public ProjectAvatarsController Controller(InferenceApiDbContext? db = null, ICurrentUser? user = null)
    {
        db ??= NewDb();
        user ??= Owner();
        return new ProjectAvatarsController(
            db, user, TestTenancy.Access(db, user), Microsoft.Extensions.Options.Options.Create(Options),
            Checks(db), Deletion(db), NullLogger<ProjectAvatarsController>.Instance, Entitlements,
            identityDescriber: Describer)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    // ------------------------------------------------------------------ files

    public static byte[] Png(int width, int height, byte salt = 0)
    {
        byte[] b = new byte[40];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        sig.CopyTo(b, 0);
        b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16); b[18] = (byte)(width >> 8); b[19] = (byte)width;
        b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16); b[22] = (byte)(height >> 8); b[23] = (byte)height;
        b[24] = 8; b[25] = 6;
        b[39] = salt;
        return b;
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public ProjectFile AddFile(string name, string mime, byte[] bytes, Action<ProjectFile>? tweak = null, Guid? projectId = null)
    {
        Guid project = projectId ?? Project.Id;
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project,
            OriginalFileName = name,
            OriginalPath = name,
            MimeType = mime,
            StorageKey = $"projects/{project}/userFiles/{Guid.NewGuid():N}-{name}",
            StorageBucket = "test-bucket",
            SizeBytes = bytes.Length,
            Category = "userFiles",
            CsamStatus = CsamScanStatus.Clean,
            ModerationStatus = FileModerationStatus.Clean
        };
        tweak?.Invoke(file);
        Objects[file.StorageKey] = bytes;
        using InferenceApiDbContext db = NewDb();
        db.ProjectFiles.Add(file);
        db.SaveChanges();
        return file;
    }

    public ProjectFile AddImage(string name = "face.png", int width = 1024, int height = 1024, byte salt = 0, Action<ProjectFile>? tweak = null) =>
        AddFile(name, "image/png", Png(width, height, salt), tweak);

    /// <summary>A video or recording with an ingest probe stored the way the engine's derivative run stores it.</summary>
    public ProjectFile AddProbedMedia(string name, string mime, double durationSec, string? videoCodec = null, int fpsNum = 30, int fpsDen = 1, byte[]? bytes = null)
    {
        // A recording needs the header its type promises: the checks read the first bytes, not just the label.
        byte[] body = bytes ?? mime switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => [.. "RIFF"u8, 1, 2, 3, 4, .. "WAVE"u8, (byte)name.Length],
            "audio/mpeg" or "audio/mp3" => [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, (byte)name.Length],
            _ => [1, 2, 3, (byte)name.Length]
        };
        ProjectFile file = AddFile(name, mime, body);
        using InferenceApiDbContext db = NewDb();
        db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = Project.Id,
            SourceStorageKey = file.StorageKey,
            SourceProbeJson = new MediaSourceProbe(durationSec, fpsNum, fpsDen, 1280, 720, videoCodec, "aac", "mp4",
                2, 48000, 1_000_000, true).ToJson(),
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        return file;
    }

    // ------------------------------------------------------------------ requests

    public static Dto.CreateProjectAvatarRequest Create(params (Guid FileId, string Role)[] references) => new(
        Name: "Anna",
        ConsentStatement: AvatarConsentStatement.English,
        ConsentLocale: "en",
        ConsentSubjectName: "Anna Rossi",
        ConsentBasis: "Self",
        SubjectIsAdult: true,
        References: [.. Array.ConvertAll(references, r => new Dto.AvatarReferenceRequest(r.FileId, r.Role))],
        RetentionDays: 90);

    /// <summary>A create request for an OBJECT avatar: the object statement and the references, nothing of a person.</summary>
    public static Dto.CreateProjectAvatarRequest CreateObject(params (Guid FileId, string Role)[] references) => new(
        Name: "Espresso machine",
        ConsentStatement: AvatarObjectStatement.English,
        ConsentLocale: "en",
        ConsentSubjectName: null,
        ConsentBasis: null,
        SubjectIsAdult: false,
        References: [.. Array.ConvertAll(references, r => new Dto.AvatarReferenceRequest(r.FileId, r.Role))],
        SubjectKind: "Object");

    /// <summary>Creates an object avatar through the controller (objects switched on) and answers its id.</summary>
    public async Task<Guid> CreateObjectAsync(string name = "Espresso machine", params ProjectFile[] files)
    {
        if (files.Length == 0) files = [AddImage(name + ".png", salt: (byte)(name.Length + 100))];
        (Guid, string)[] references =
            [.. Array.ConvertAll(files, f => (f.Id, f.MimeType.StartsWith("image/", StringComparison.Ordinal) ? "Front" : "Motion"))];
        Dto.CreateProjectAvatarRequest request = CreateObject(references) with { Name = name };
        ActionResult<Dto.ProjectAvatarResponse> result = await Controller().Create(Project.Id, request, CancellationToken.None);
        if (result.Result is not CreatedAtActionResult created)
            throw new InvalidOperationException("create failed: " + JsonSerializer.Serialize((result.Result as ObjectResult)?.Value));
        return ((Dto.ProjectAvatarResponse)created.Value!).Id;
    }

    /// <summary>A filmstrip derivative of <paramref name="video"/> (one sheet), the way the engine's derivative run stores it.</summary>
    public byte[] AddFilmstrip(ProjectFile video, byte salt = 9)
    {
        string directory = $"projects/{Project.Id}/derivatives/{video.Id:N}";
        byte[] sheet = Png(640, 360, salt);
        Objects[directory + "/sheet-0.png"] = sheet;
        Objects[directory + "/index.json"] = System.Text.Encoding.UTF8.GetBytes(
            "{\"densitySec\":1,\"cols\":4,\"rows\":4,\"sheetCount\":1,\"sheets\":[{\"storageLeaf\":\"sheet-0.png\",\"firstThumbIndex\":0,\"thumbCount\":16,\"firstTimeSec\":0}]}");
        using InferenceApiDbContext db = NewDb();
        db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = Project.Id,
            Kind = MediaDerivativeKind.Filmstrip,
            SourceKind = MediaSourceKind.ProjectFile,
            SourceProjectFileId = video.Id,
            SourceStorageKey = video.StorageKey,
            StorageKey = directory + "/sheet-0.png",
            IndexStorageKey = directory + "/index.json",
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        return sheet;
    }

    /// <summary>Creates a ready avatar through the controller and answers its id.</summary>
    public async Task<Guid> CreateReadyAsync(string name = "Anna", params ProjectFile[] files)
    {
        if (files.Length == 0) files = [AddImage(name + ".png", salt: (byte)name.Length)];
        Dto.CreateProjectAvatarRequest request = Create([.. Array.ConvertAll(files, f => (f.Id, RoleFor(f)))]) with { Name = name };
        ActionResult<Dto.ProjectAvatarResponse> result = await Controller().Create(Project.Id, request, CancellationToken.None);
        if (result.Result is not CreatedAtActionResult created)
            throw new InvalidOperationException("create failed: " + JsonSerializer.Serialize((result.Result as ObjectResult)?.Value));
        return ((Dto.ProjectAvatarResponse)created.Value!).Id;
    }

    private static string RoleFor(ProjectFile file) =>
        file.MimeType.StartsWith("image/", StringComparison.Ordinal) ? "Face"
        : file.MimeType.StartsWith("video/", StringComparison.Ordinal) ? "Motion" : "Speech";

    /// <summary>Status code and JSON body of an action result.</summary>
    public static (int Status, JsonElement Body) Read(IActionResult? result)
    {
        return result switch
        {
            ObjectResult o => (o.StatusCode ?? 200, JsonSerializer.SerializeToElement(o.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            StatusCodeResult s => (s.StatusCode, default),
            ForbidResult => (403, default),
            _ => throw new InvalidOperationException("unexpected result " + result?.GetType().Name)
        };
    }

    public static (int Status, JsonElement Body) Read<T>(ActionResult<T> result) =>
        result.Result is null
            ? (200, JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            : Read(result.Result);
}
