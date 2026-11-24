namespace ReelBolt.Shared.Tenancy;

/// <summary>
/// The organization a unit of work runs for. Request code reads it from the validated token; background
/// work (no HTTP request) must open an explicit scope. With neither a request org nor a scope there is
/// no tenant, and tenant filters must match nothing (fail closed).
/// </summary>
public interface ITenantContext
{
    /// <summary>The active organization, or null when none (and not in a system scope).</summary>
    Guid? OrganizationId { get; }

    /// <summary>True inside <see cref="BeginSystemScope"/>: work that deliberately spans every organization.</summary>
    bool IsSystem { get; }

    /// <summary>Marks the current async flow as platform-internal (migrations, seeding, cleanup).</summary>
    IDisposable BeginSystemScope();

    /// <summary>Runs the current async flow as the given organization (background jobs for one project).</summary>
    IDisposable BeginOrganizationScope(Guid organizationId);
}
