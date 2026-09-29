using FusionRpg.Data.Abstractions;

namespace FusionRpg.Data;

public sealed class ColdArchiveWriter : IColdArchiveWriter
{
    readonly RpgStore _store;

    public ColdArchiveWriter(RpgStore store) => _store = store;

    public string? PromoteClosedRunCapture(long runId) => _store.PromoteClosedRunCapture(runId);
}
