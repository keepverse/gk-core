namespace FusionRpg.Data.Policies;

/// <summary>Sealed hot-path retain limits and snapshot schema versions (archive/trim = Slice D).
/// Retain tails are config-backed (tunables-ssot.md T1) — gk-core/data/tuning/data.v1.json's retain. Schema
/// versions stay structural consts.</summary>
public static class SealedCompactionPolicy
{
    static DataTuning? _tuning;

    public static void Configure(DataTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static DataTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "SealedCompactionPolicy.Configure(...) has not run. Retain tails read data/tuning/data.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    static RetainTuning Retain => Tuning.Retain;

    public static int ActivityRetainTail => Retain.ActivityTail;
    public static int XpRetainTailPerActor => Retain.XpTailPerActor;
    public static int SoulRetainTailPerPlayer => Retain.SoulTailPerPlayer;
    public static int KeepLastNFullCaptureRuns => Retain.KeepLastNFullCaptureRuns;

    public const int ActivitySnapshotSchemaVersion = 1;
    public const int XpSnapshotSchemaVersion = 1;
}
