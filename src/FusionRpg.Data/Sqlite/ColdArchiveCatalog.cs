using FusionRpg.Data.Abstractions;

namespace FusionRpg.Data;

public sealed class ColdArchiveCatalog : IColdArchiveCatalog
{
    readonly RpgStore _store;

    public ColdArchiveCatalog(RpgStore store) => _store = store;

    public IReadOnlyList<ColdArchiveEntry> List() => _store.ListArchiveCatalog();
}
