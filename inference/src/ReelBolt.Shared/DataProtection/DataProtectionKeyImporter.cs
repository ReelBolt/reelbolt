using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.DataProtection;

/// <summary>
/// One-time import of an existing file-system key ring (<c>key-*.xml</c>, and <c>revocation-*.xml</c>)
/// into <c>data_protection_keys</c> when switching <c>DataProtection:Store</c> to <c>Database</c>.
/// Files are inserted verbatim: same key id, same encryption element (none on Linux), so every
/// ciphertext protected before the switch still unprotects (plan decision D20). Runs only when the
/// table is empty, so it is idempotent and never overwrites a ring that is already in the database.
/// </summary>
public static class DataProtectionKeyImporter
{
    public static async Task<int> ImportAsync(DbContext db, string keysPath, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Set<DataProtectionKey>().AnyAsync(ct))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(keysPath) || !Directory.Exists(keysPath))
        {
            return 0;
        }

        string[] files = Directory.GetFiles(keysPath, "key-*.xml")
            .Concat(Directory.GetFiles(keysPath, "revocation-*.xml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            return 0;
        }

        foreach (string file in files)
        {
            db.Set<DataProtectionKey>().Add(new DataProtectionKey
            {
                FriendlyName = Path.GetFileNameWithoutExtension(file),
                Xml = await File.ReadAllTextAsync(file, ct),
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Imported {Count} Data Protection key file(s) from {Path} into the database key ring.", files.Length, keysPath);
        return files.Length;
    }
}
