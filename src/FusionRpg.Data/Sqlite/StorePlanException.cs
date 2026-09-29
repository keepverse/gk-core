namespace FusionRpg.Data.Sqlite;

/// <summary>
/// Thrown when a store operation is not available for the store's current storage plan. The one case
/// today: a filesystem-backed archive entry point on an in-memory store (module
/// <c>memory-storage-plan</c>), which must fail loudly rather than resolve a bogus cwd-relative path
/// and write there. The <c>archive-target</c> module removes this for archive slices.
/// </summary>
public sealed class StorePlanException : InvalidOperationException
{
    public StorePlanException(string message) : base(message)
    {
    }
}
