namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// An <see cref="IDesignImporter"/> that also reads the whole import set, not just the files its own
/// <see cref="IDesignImporter.CanRead"/> claimed. Used by importers (e.g. Style Dictionary) whose
/// config names, by glob, the files another importer should read.
/// </summary>
public interface IContextualDesignImporter : IDesignImporter
{
    /// <summary>
    /// PURE; never throws. <paramref name="claimed"/> are the files this importer's
    /// <see cref="IDesignImporter.CanRead"/> accepted; <paramref name="all"/> is the whole import set,
    /// which the importer may read from but must never claim purely to widen its own scope.
    /// </summary>
    DesignImportResult Import(IReadOnlyList<ImportFile> claimed, IReadOnlyList<ImportFile> all);
}
