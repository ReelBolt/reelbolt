namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One ASP.NET Core Data Protection key (or revocation) XML document, stored verbatim in table
/// <c>data_protection_keys</c> when <c>DataProtection:Store = Database</c>. The Inference API owns the
/// table and is the only writer; the WorkflowEngine maps it read-only.
/// </summary>
public class DataProtectionKey
{
    public int Id { get; set; }

    /// <summary>The key's file name without extension (e.g. <c>key-0f3c…</c>); informational.</summary>
    public string? FriendlyName { get; set; }

    /// <summary>The element XML exactly as Data Protection produced it.</summary>
    public string Xml { get; set; } = string.Empty;
}
