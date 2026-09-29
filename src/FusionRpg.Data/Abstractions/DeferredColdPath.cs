namespace FusionRpg.Data.Abstractions;

/// <summary>Deferred stubs — multi-archive cold-path query fan-in remains unimplemented.
/// User-driven purge lives on Storage REST / RpgStore (W12); no auto GC.</summary>
public sealed class DeferredColdPathQuery : IColdPathQuery
{
    public bool IsImplemented => false;
}

public sealed class DeferredGarbageCollector : IGarbageCollector
{
    public bool IsImplemented => false;
}
