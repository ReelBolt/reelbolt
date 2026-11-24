using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.DataProtection;

/// <summary>
/// <see cref="IXmlRepository"/> over table <c>data_protection_keys</c>, so every replica shares one
/// key ring that survives container replacement. The Inference API is the only writer; a read-only
/// instance (WorkflowEngine) throws on <see cref="StoreElement"/>, which Data Protection only calls
/// when it generates or revokes a key, and the engine runs with automatic key generation disabled.
/// A fresh DbContext scope is used per call because Data Protection holds the repository as a singleton.
/// </summary>
public sealed class DataProtectionKeyStore<TContext> : IXmlRepository
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly bool _readOnly;

    public DataProtectionKeyStore(IServiceScopeFactory scopes, bool readOnly)
    {
        _scopes = scopes;
        _readOnly = readOnly;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using IServiceScope scope = _scopes.CreateScope();
        TContext db = scope.ServiceProvider.GetRequiredService<TContext>();
        return db.Set<DataProtectionKey>()
            .AsNoTracking()
            .OrderBy(k => k.Id)
            .Select(k => k.Xml)
            .ToList()
            .Select(xml => XElement.Parse(xml))
            .ToList()
            .AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        if (_readOnly)
        {
            throw new InvalidOperationException(
                "This process maps the Data Protection key ring read-only; only the Inference API generates keys.");
        }

        using IServiceScope scope = _scopes.CreateScope();
        TContext db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Set<DataProtectionKey>().Add(new DataProtectionKey
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        db.SaveChanges();
    }
}
