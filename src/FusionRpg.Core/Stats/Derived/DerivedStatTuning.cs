using System.Text.Json;

namespace FusionRpg.Core.Stats.Derived;

/// <summary>Derived-stat balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="DerivedStatPolicy.Configure"/> and <see cref="DerivedStatTuningLoader"/>.
///
/// <para>Phase 1 (cap-consolidation) scope is narrow on purpose: <see cref="CategoryResistCap"/> is the
/// one cap this module moves to a single home. Per-channel caps for the 157 channels
/// <c>catalog-extension</c> registers are T7's job — extracted with values unchanged, tuned
/// separately — not invented here ahead of their channels.</para></summary>
/// <param name="TurnDefaultSpeed">battle-timeline T14 (spec-timeline-tunables.md §1) — the base value
/// of the <c>turn.speed</c> channel: "how fast is a baseline actor", which is the first number a
/// balance pass reaches for. It lives here rather than in <c>battle.v{n}.json</c> for a reason found in
/// the code, not chosen by taste: <see cref="DerivedStatRegistry"/> reads it at registration, exactly
/// as it reads <see cref="CategoryResistCap"/> — and while every registry consumer already calls
/// <see cref="DerivedStatPolicy.Configure"/>, several tools deliberately do not configure
/// <c>BattleTuningHub</c> (<c>gk-core/tools/ProveAptitude/Program.cs</c> says so in as many words), so sourcing
/// it from battle tuning would break them.
///
/// <para><b>Not to be confused with <see cref="Battle.Timeline.TurnReadiness.SpeedScale"/></b>, which is
/// the readiness formula's structural unit. The two held one value (100) before T14 split them, and
/// they still do — the split exists so a balance pass can move this one without re-scaling the
/// arithmetic.</para></param>
public sealed record DerivedStatTuning(int SchemaVersion, int Version, double CategoryResistCap, long TurnDefaultSpeed);

public sealed class DerivedStatTuningRejection : Exception
{
    public DerivedStatTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class DerivedStatTuningLoader
{
    public static DerivedStatTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DerivedStatTuningRejection("derived-stat tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new DerivedStatTuningRejection($"derived-stat tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            return new DerivedStatTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                CategoryResistCap: Dbl(root, "categoryResistCap"),
                TurnDefaultSpeed: Lng(root, "turnDefaultSpeed"));
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new DerivedStatTuningRejection($"derived-stat tuning: missing or non-integer '$.{key}'");
        return v;
    }

    /// <summary>`long`, not `int` — AGENTS.md invariant 13: every magnitude the power ladder can touch
    /// is `long`. A speed is a magnitude.</summary>
    static long Lng(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new DerivedStatTuningRejection($"derived-stat tuning: missing or non-integer '$.{key}'");
        if (v <= 0)
            throw new DerivedStatTuningRejection($"derived-stat tuning: '$.{key}' must be > 0 (it is a divisor and a rate); got {v}");
        return v;
    }

    static double Dbl(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new DerivedStatTuningRejection($"derived-stat tuning: missing or non-numeric '$.{key}'");
        return el.GetDouble();
    }
}

/// <summary>Design defaults gateway — cap-consolidation.md §2. The single enforcement point for the
/// category resist cap: <see cref="DerivedStatRegistry"/> reads this at registration instead of a
/// hardcoded literal, and nothing downstream (<see cref="Status.ResistanceEvaluator"/>) re-clamps.
///
/// <para><b>Scoped override, unlike the sibling <c>XPolicy</c> gateways.</b> Every other migrated
/// Policy class (<see cref="Status.StatusPolicy"/> and friends) is configured once by a test-assembly
/// bootstrap and never touched again — there is no existing test that reconfigures one mid-run. This
/// one needs to: <c>RaisingTheCapActuallyRaisesIt</c> (spec-cap-consolidation.md §5) has to construct a
/// registry against a cap value the bootstrap did not choose, and ~3000 other tests build a registry
/// against the global default concurrently (xUnit parallelizes across test classes by default). A bare
/// <see cref="Configure"/> call from inside a test would race every one of them. <see cref="UseScoped"/>
/// is the same <c>AsyncLocal</c> pattern <see cref="Combat.Element.ElementTable"/> and
/// <see cref="Stats.ChannelPolicyTable"/> already use for exactly this reason.</para></summary>
public static class DerivedStatPolicy
{
    static DerivedStatTuning? _global;
    static readonly AsyncLocal<DerivedStatTuning?> Scoped = new();

    /// <summary>Host-only (Injector/Server startup) or a test-assembly bootstrap's one-time call.</summary>
    public static void Configure(DerivedStatTuning tuning) =>
        _global = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>Swap for this async context only, so a test proving a different cap value cannot
    /// disturb one running beside it.</summary>
    public static IDisposable UseScoped(DerivedStatTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        var previous = Scoped.Value;
        Scoped.Value = tuning;
        return new Restore(previous);
    }

    static DerivedStatTuning Tuning => Scoped.Value ?? _global ?? throw new InvalidOperationException(
        "DerivedStatPolicy.Configure(...) has not run. Every derived-stat cap reads data/tuning/" +
        "derived-stats.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    public static double CategoryResistCap => Tuning.CategoryResistCap;

    /// <summary>battle-timeline T14 — the `turn.speed` channel's base value. Reaching this without
    /// having called <see cref="Configure"/> throws, by the same gateway rule as every other key here:
    /// there is deliberately no built-in default to silently fall back to.</summary>
    public static long TurnDefaultSpeed => Tuning.TurnDefaultSpeed;

    /// <summary>
    /// spec-actor-channels.md §2.2 — <c>resource.efficiency.{id}</c> is a cost-reduction ratio; 100% is
    /// the mathematical ceiling of "reduces cost", not a balance choice like <see cref="CategoryResistCap"/>'s
    /// 0.95 (why 0.95 and not 1.0 IS a choice). Bounded 0..1 by nature, PS-8 exempt — a plain
    /// structural <c>const</c>, not a loaded tunable, same reasoning as
    /// <see cref="Battle.Timeline.CooldownMath.MinTicksFloor"/>.
    /// </summary>
    public const double ResourceEfficiencyCap = 1.0;

    /// <summary>
    /// spec-actor-channels.md §4.2 — <c>progression.breakthroughSuccess</c> is a roll PROBABILITY; 100%
    /// is the ceiling of "chance", structurally, not a tuned value. PS-8 exempt for the same reason as
    /// <see cref="ResourceEfficiencyCap"/>: a probability's domain is closed, not a progression ceiling.
    /// </summary>
    public const double BreakthroughSuccessCap = 1.0;

    sealed class Restore : IDisposable
    {
        readonly DerivedStatTuning? _previous;
        public Restore(DerivedStatTuning? previous) => _previous = previous;
        public void Dispose() => Scoped.Value = _previous;
    }
}
