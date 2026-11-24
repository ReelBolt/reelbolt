#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.FolderSync;

/// <summary>
/// <c>DELETE /api/v1/device/me</c>: the desktop app's "Unpair" removes the computer on the server too
/// (docs/local-folder-sync.md "Removing this computer"). It must do what the dashboard's removal does —
/// stamp <c>revoked_at</c> and keep the row — and nothing more: the folder links and the files they
/// synced stay, and every later request the computer signs is refused.
/// </summary>
public class DeviceSelfRemovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_computer_that_removes_itself_is_revoked_like_a_dashboard_removal()
    {
        Fixture f = new();

        IActionResult result = await f.Controller().ForgetMe(f.Removal(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        RunnerDevice row = await f.Reload();
        row.RevokedAt.Should().Be(Now.UtcDateTime);
        row.RevokedByUserId.Should().BeNull("no person revoked it; the computer did");
        (await f.Db.RunnerDevices.CountAsync()).Should().Be(1, "the dashboard keeps a revoked row, and so does this");
    }

    [Fact]
    public async Task Its_folder_links_and_their_files_are_left_exactly_as_a_dashboard_removal_leaves_them()
    {
        Fixture f = new();

        await f.Removal().RevokeAsync(f.Device, CancellationToken.None);

        (await f.Db.ProjectLocalFolderLinks.CountAsync()).Should().Be(1);
        (await f.Db.ProjectLocalFolderLinks.SingleAsync()).DeviceId.Should().Be(f.DeviceId);
    }

    [Fact]
    public async Task Every_later_request_the_computer_signs_is_refused()
    {
        Fixture f = new();
        await f.Removal().RevokeAsync(f.Device, CancellationToken.None);

        DeviceAuthResult after = await f.AuthenticateAnything();

        after.Succeeded.Should().BeFalse();
        after.Failure.Should().Be(DeviceAuthFailure.DeviceNotUsable);
    }

    [Fact]
    public async Task A_repeat_revokes_once_and_keeps_the_first_time()
    {
        Fixture f = new();
        (await f.Removal().RevokeAsync(f.Device, CancellationToken.None)).Should().BeTrue();
        f.Clock.Advance(TimeSpan.FromMinutes(5));

        (await f.Removal().RevokeAsync(f.Device, CancellationToken.None)).Should().BeFalse();

        (await f.Reload()).RevokedAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task Only_the_device_in_its_own_organization_is_touched()
    {
        Fixture f = new();
        // A context naming another organization (never produced by the filter, which reads the row) must
        // not revoke the row.
        DeviceContext elsewhere = f.Device with { OrganizationId = Guid.NewGuid() };

        (await f.Removal().RevokeAsync(elsewhere, CancellationToken.None)).Should().BeFalse();

        (await f.Reload()).RevokedAt.Should().BeNull();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Db = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);
            DeviceId = Guid.NewGuid();
            Guid organizationId = Guid.NewGuid();
            Guid pairingUserId = Guid.NewGuid();

            Db.RunnerDevices.Add(new RunnerDevice
            {
                Id = DeviceId,
                Kind = RunnerKind.Desktop,
                Name = "Studio PC",
                OrganizationId = organizationId,
                PairedByUserId = pairingUserId,
                PublicKey = Convert.ToBase64String(new byte[32]),
                CreatedAt = Now.UtcDateTime
            });
            Db.ProjectLocalFolderLinks.Add(new ProjectLocalFolderLink
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OrganizationId = organizationId,
                DeviceId = DeviceId,
                TargetFolder = "Footage",
                DisplayPath = "D:\\Footage",
                CreatedByUserId = pairingUserId
            });
            Db.SaveChanges();

            Device = new DeviceContext(DeviceId, "Studio PC", organizationId, pairingUserId);
        }

        public InferenceApiDbContext Db { get; }

        public Clock Clock { get; } = new(Now);

        public Guid DeviceId { get; }

        public DeviceContext Device { get; }

        public DeviceSelfRemoval Removal() => new(Db, NullLogger<DeviceSelfRemoval>.Instance, Clock);

        /// <summary>The controller as the filter leaves it: the authenticated device in the request items.</summary>
        public DeviceFolderSyncController Controller()
        {
            // ForgetMe touches nothing but the device and the removal service.
            DeviceFolderSyncController controller = new(Db, null!, null!, null!, null!,
                NullLogger<DeviceFolderSyncController>.Instance);
            DefaultHttpContext http = new();
            http.Items[DeviceRequestAuthenticator.ItemKey] = Device;
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            return controller;
        }

        public async Task<RunnerDevice> Reload() =>
            await Db.RunnerDevices.AsNoTracking().SingleAsync(d => d.Id == DeviceId);

        /// <summary>
        /// A request with well-formed headers: a revoked device is refused before its signature is even
        /// checked, so the signature need not be valid to prove the refusal.
        /// </summary>
        public async Task<DeviceAuthResult> AuthenticateAnything()
        {
            DeviceRequestAuthenticator authenticator = new(
                Db, new DeviceNonceCache(), NullLogger<DeviceRequestAuthenticator>.Instance, Clock);
            DefaultHttpContext context = new();
            context.Request.Method = "GET";
            context.Request.Path = "/api/v1/device/me";
            context.Request.Body = new MemoryStream();
            context.Request.Headers[DeviceSignature.DeviceHeader] = DeviceId.ToString();
            context.Request.Headers[DeviceSignature.TimestampHeader] =
                Clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            context.Request.Headers[DeviceSignature.NonceHeader] =
                Convert.ToBase64String(Enumerable.Range(1, 24).Select(i => (byte)i).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            context.Request.Headers[DeviceSignature.SignatureHeader] = "AAAA";
            return await authenticator.AuthenticateAsync(context.Request, CancellationToken.None);
        }
    }
}
