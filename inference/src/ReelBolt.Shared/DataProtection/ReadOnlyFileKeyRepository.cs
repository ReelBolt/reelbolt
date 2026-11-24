using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace ReelBolt.Shared.DataProtection;

/// <summary>
/// <see cref="IXmlRepository"/> that reads the key files another process (the Inference API) wrote to a
/// directory and never writes one. It does not create the directory either, so it works on a read-only
/// mount. A missing directory is an empty ring.
/// </summary>
public sealed class ReadOnlyFileKeyRepository : IXmlRepository
{
    private readonly string _directory;

    public ReadOnlyFileKeyRepository(string directory)
    {
        _directory = directory;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        if (!Directory.Exists(_directory))
            return [];

        List<XElement> elements = [];
        foreach (string file in Directory.EnumerateFiles(_directory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            using FileStream stream = File.OpenRead(file);
            elements.Add(XElement.Load(stream));
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName) =>
        throw new InvalidOperationException(
            "This process maps the Data Protection key ring read-only; only the Inference API generates keys.");
}
