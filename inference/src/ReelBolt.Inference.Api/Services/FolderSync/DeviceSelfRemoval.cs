using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.FolderSync;

/// <summary>
/// A paired computer removing itself (<c>DELETE /api/v1/device/me</c>): what the desktop app's
/// "Unpair" does on the server, so the old device does not linger in the workspace until someone
/// removes it from the dashboard.
///
/// <para>
/// It does exactly what the dashboard's own removal does (the Go API's <c>DELETE /api/v1/runners/{id}</c>):
/// the <c>runner_devices</c> row is kept and stamped <c>revoked_at</c>, which takes it out of the
/// workspace's device list, makes every later signed request from it a <c>401 device_unauthorized</c>,
/// and makes the runner gateway drop its connection at its next <c>revoked_at</c> re-check (60 s). Its
/// folder links stay where they are, shown as belonging to a removed computer, and the files they
/// synced stay in their projects — the same as when the dashboard removes it.
/// </para>
///
/// <para>
/// <c>revoked_by_user_id</c> is left <c>null</c>: no person revoked it, the computer did, and recording
/// the pairing user there would claim an action they did not take. No <c>RunnerDeviceRevoked</c> event
/// is published either: the computer is disconnecting itself, and the gateway's re-check is the
/// authority the event only hurries along.
/// </para>
/// </summary>
public sealed class DeviceSelfRemoval(InferenceApiDbContext db, ILogger<DeviceSelfRemoval> logger, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Revokes the device. Returns <c>false</c> when it was already revoked (a repeat of a request whose
    /// answer was lost), which the caller answers exactly like the first time.
    /// </summary>
    public async Task<bool> RevokeAsync(DeviceContext device, CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        int changed;
        if (db.Database.IsRelational())
        {
            // One conditional UPDATE, so two racing removals revoke once and the first timestamp stands.
            changed = await db.RunnerDevices
                .Where(d => d.Id == device.DeviceId && d.OrganizationId == device.OrganizationId && d.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.RevokedAt, (DateTime?)now)
                    .SetProperty(d => d.RevokedByUserId, (Guid?)null), ct);
        }
        else
        {
            var row = await db.RunnerDevices.FirstOrDefaultAsync(
                d => d.Id == device.DeviceId && d.OrganizationId == device.OrganizationId && d.RevokedAt == null, ct);
            if (row is null)
            {
                changed = 0;
            }
            else
            {
                row.RevokedAt = now;
                row.RevokedByUserId = null;
                await db.SaveChangesAsync(ct);
                changed = 1;
            }
        }

        if (changed > 0)
        {
            logger.LogInformation("Device {DeviceId} of organization {OrganizationId} removed itself", device.DeviceId, device.OrganizationId);
        }
        return changed > 0;
    }
}
