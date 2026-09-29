namespace FusionRpg.Data.Sqlite;

/// <summary>
/// Full-customization constructor options for <see cref="RpgStore"/> (see module
/// <c>memory-storage-plan</c>). The file plan is the production default; the memory plan is a
/// first-class test substrate.
///
/// <para><b>The URI trap is enforced here.</b> A <c>file:</c>/<c>mode=memory</c> URI supplied as
/// <see cref="DataDir"/> with <see cref="InMemory"/> false would be rewritten by
/// <see cref="System.IO.Path.GetFullPath(string)"/> into a filesystem path and re-create a file, so
/// it is rejected instead.</para>
/// </summary>
public sealed record RpgStoreOptions
{
    /// <summary>Directory holding the store's two files. Ignored entirely when <see cref="InMemory"/>.</summary>
    public string? DataDir { get; init; }

    /// <summary>Run against two uniquely-named shared-memory databases instead of files.</summary>
    public bool InMemory { get; init; }

    /// <summary>Name a specific memory database (default: a unique name per store).</summary>
    public string? HotName { get; init; }

    /// <summary>Name a specific media memory database (default: a unique name per store).</summary>
    public string? MediaName { get; init; }

    /// <summary>Builds options from the string-constructor shape, honoring the bool plan selector.</summary>
    public static RpgStoreOptions For(string dataDir, bool inMemory = false) =>
        new() { DataDir = dataDir, InMemory = inMemory };

    /// <summary>
    /// Throws when a memory URI is supplied with the file plan, because the production constructor's
    /// <c>Path.GetFullPath</c>/<c>Path.Combine</c> would silently turn it into a file path.
    /// </summary>
    public RpgStoreOptions Resolve()
    {
        if (!InMemory && DataDir is not null && SqliteConnectionFactory.IsMemoryUri(DataDir))
            throw new InvalidOperationException(
                $"'{DataDir}' is a memory URI but InMemory is false. Set InMemory: true to use the " +
                "memory plan, or pass a real directory for the file plan.");
        return this;
    }
}
