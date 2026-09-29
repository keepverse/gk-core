namespace FusionRpg.Data.Abstractions;

/// <summary>Marker for the sole persistence façade. Concrete: <c>RpgStore</c>.</summary>
public interface IRpgDb
{
    void Init();
}

/// <summary>Write cold archive segment before trimming hot (ON v1; Slice D).</summary>
public interface IColdArchiveWriter
{
    string? PromoteClosedRunCapture(long runId);
}

/// <summary>Registry of cold archive files.</summary>
public interface IColdArchiveCatalog
{
    IReadOnlyList<ColdArchiveEntry> List();
}

public sealed record ColdArchiveEntry(
    string Uri,
    string Kind,
    long? RunId,
    string CreatedUtc);

/// <summary>Snapshot-verified trim after successful archive (Slice D).</summary>
public interface IHotCompactor
{
    void CompactAfterRunClosed(long? closedRunId);
}

/// <summary>Deferred: unified cold-path reads across archive files.</summary>
public interface IColdPathQuery
{
    bool IsImplemented { get; }
}

/// <summary>Deferred: garbage-collect old archive files.</summary>
public interface IGarbageCollector
{
    bool IsImplemented { get; }
}
