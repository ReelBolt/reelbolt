using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// The admin surface: platform admin only, cross-tenant, and it returns no content, storage key,
/// URL, hash or token. A rescan never auto-releases a quarantined row.
/// </summary>
public class CsamAdminControllerTests
{
    /// <summary>ASP.NET's own naming policy, so the test reads the field names the page receives.</summary>
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string Json(object? value) => JsonSerializer.Serialize(value, WebJson);
    [Fact]
    public async Task NonAdmin_IsForbidden()
    {
        AdminFixture f = new(isPlatformAdmin: false);

        IActionResult list = await f.Controller.ListScans(null, null, null, CancellationToken.None);
        IActionResult rescan = await f.Controller.Rescan(f.FileA.Id, CancellationToken.None);

        list.Should().BeOfType<ForbidResult>();
        rescan.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task List_ReturnsNoContentStorageKeyUrlOrHash()
    {
        AdminFixture f = new();

        IActionResult result = await f.Controller.ListScans(null, null, null, CancellationToken.None);

        object? value = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        string json = Json(value);
        json.Should().Contain("fileName");
        json.Should().NotContainEquivalentOf("storageKey");
        json.Should().NotContainEquivalentOf("storage_key");
        json.Should().NotContainEquivalentOf("presigned");
        json.Should().NotContainEquivalentOf("pdqHash");
        json.Should().NotContainEquivalentOf("sha256");
        json.Should().NotContainEquivalentOf("token");
        json.Should().NotContainEquivalentOf("content");
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        AdminFixture f = new();

        IActionResult result = await f.Controller.ListScans("not-scanned", null, null, CancellationToken.None);

        string json = Json(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain("not-scanned");
        json.Should().NotContain("\"status\":\"match\"");
    }

    [Fact]
    public async Task List_PagesByBefore()
    {
        AdminFixture f = new();
        f.AddAudit("clean", DateTime.UtcNow.AddHours(-1));
        f.AddAudit("clean", DateTime.UtcNow.AddHours(-2));
        await f.Db.SaveChangesAsync();

        IActionResult first = await f.Controller.ListScans("all", 2, null, CancellationToken.None);

        string json = Json(first.Should().BeOfType<OkObjectResult>().Subject.Value);
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
        document.RootElement.GetProperty("nextBefore").ValueKind.Should().NotBe(JsonValueKind.Null);
        document.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Rescan_RunsTheMatcher_AndNeverReleasesAQuarantinedRow()
    {
        AdminFixture f = new();
        f.FileA.CsamStatus = CsamScanStatus.Quarantined;
        f.FileA.CsamQuarantineKey = "projects/p/quarantine/x";
        f.FileA.CsamQuarantinedAt = DateTime.UtcNow;
        await f.Db.SaveChangesAsync();

        IActionResult result = await f.Controller.Rescan(f.FileA.Id, CancellationToken.None);

        f.Matcher.ScanCalls.Should().Be(1);
        f.Matcher.LastSeen!.Id.Should().Be(f.FileA.Id);
        result.Should().BeOfType<OkObjectResult>();
        string json = Json(((OkObjectResult)result).Value);
        json.Should().Contain("Clean");
    }

    [Fact]
    public async Task Rescan_UnknownFile_IsNotFound()
    {
        AdminFixture f = new();
        IActionResult result = await f.Controller.Rescan(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    private sealed class AdminFixture
    {
        public AdminFixture(bool isPlatformAdmin = true)
        {
            Db = CsamTestSupport.NewDb();
            FileA = CsamTestSupport.AddFile(Db, "image/jpeg", "a.jpg");
            FileB = CsamTestSupport.AddFile(Db, "video/mp4", "b.mp4");
            AddAudit("match", DateTime.UtcNow);
            AddAudit("not-scanned", DateTime.UtcNow.AddMinutes(-1));
            Db.SaveChanges();

            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.IsPlatformAdmin).Returns(isPlatformAdmin);
            user.SetupGet(u => u.UserId).Returns(Guid.NewGuid());
            Matcher = new RecordingMatcher();
            Controller = new CsamAdminController(Db, user.Object, Matcher, Options.Create(CsamTestSupport.EnabledOptions()));
        }

        public InferenceApiDbContext Db { get; }
        public ProjectFile FileA { get; }
        public ProjectFile FileB { get; }
        public RecordingMatcher Matcher { get; }
        public CsamAdminController Controller { get; }

        public CsamScanAudit AddAudit(string status, DateTime createdAt)
        {
            ProjectFile file = FileA;
            CsamScanAudit audit = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = file.ProjectId,
                ProjectFileId = file.Id,
                Status = status,
                Classification = status == "match" ? "csam" : null,
                MatchType = status == "match" ? "exact" : null,
                Quarantined = status == "match",
                QuarantineSucceeded = status == "match",
                CreatedAt = createdAt
            };
            Db.CsamScanAudits.Add(audit);
            return audit;
        }
    }

    private sealed class RecordingMatcher : ICsmaHashMatcher
    {
        public int ScanCalls { get; private set; }
        public ProjectFile? LastSeen { get; private set; }

        public Task<CsamScanOutcome> ScanAsync(ProjectFile file, Guid? actorUserId, CancellationToken ct)
        {
            ScanCalls++;
            LastSeen = file;
            return Task.FromResult(new CsamScanOutcome(CsamScanStatus.Clean, "no-known-match", null, false, null));
        }
    }
}
