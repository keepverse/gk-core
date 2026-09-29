namespace FusionRpg.Core.Diagnostics;

/// <summary>
/// The `lawn-perf-budget` tuning revision the production reader loads. Bumped in the SAME commit as the
/// publish that makes a new revision current — the filename and the reader move together, so a published
/// file with no reader and a reader pointed at the wrong file are both impossible. Same pattern as
/// <c>SocketTuningFiles.Current</c> and `CombatAiTuningFiles.Current` (combat-ai `CAI-F1`).
/// </summary>
public static class LawnPerfBudgetFiles
{
    /// <summary>`v2` adds `ceiling.sections`, the per-section shares. The one this program owes is
    /// `lawn.ai.decide`, and it is **declared and unmeasured** (`null`) — the file's own `_meta` says a
    /// gate must read `null` as "cannot pass yet", never as a satisfied gate, because no clean 300-zombie
    /// A/B has been taken on the current build (that is `CAI5.1`'s live probe).</summary>
    public const string Current = "lawn-perf-budget.v2.json";
}
