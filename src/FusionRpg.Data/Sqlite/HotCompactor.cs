using FusionRpg.Data.Abstractions;

namespace FusionRpg.Data;

public sealed class HotCompactor : IHotCompactor
{
    readonly RpgStore _store;

    public HotCompactor(RpgStore store) => _store = store;

    public void CompactAfterRunClosed(long? closedRunId) => _store.CompactAfterRunClosed(closedRunId);
}
