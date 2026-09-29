using System.Text.Json;

namespace FusionRpg.Core.Progression;

/// <summary>
/// `species-build` T1.1 (module 3, `species-xp`) — the tunable surface for a creature SPECIES' own
/// per-player level. The level itself is NOT a parallel type: it reuses <see cref="RpgActorState"/>,
/// <see cref="RpgXpApply"/> and <see cref="RpgXpCurve"/> exactly as `player`/`plant`/`zombie` do, via
/// the new <see cref="RpgActorKinds.Species"/> kind (`RpgProgression.cs`) — <see cref="RpgXpCurve.ParamsFor"/>
/// reads this hub's <see cref="SpeciesProgressionTuning.CurveFirst"/>/<see cref="SpeciesProgressionTuning.CurveStep"/>
/// for that kind.
///
/// <para><b>Why a species is its own row, not a join</b> (spec-species-xp.md §1, decided against code,
/// not assumed): <c>rpg_actor_progression</c>'s existing `kind='plant'|'zombie'` rows key on the PvZ
/// engine's own type id and are read by other things today — a species-XP module has no mandate to
/// migrate them. And a species is reachable from TWO sources (lawn placement, expedition victory),
/// only one of which has a PvZ type id at all; a join has nothing to join on for the other. So species
/// gets its own `kind` value in the SAME tables (Option A), keyed on <c>CreatureSpeciesDef.CreatureTypeId</c>
/// — already a unique int per species (`CreatureSpeciesCatalog.Validate`'s own duplicate-creatureTypeId
/// check) — never a second store forking the ledger/retention/compaction/`LevelChangePipeline` that
/// already exist for it.</para>
///
/// <para><b>Why its own tunable file, not `progression.v3.json`'s existing curve/awards.</b> A
/// species' levelling pace and its run-completion/placement award sizes are this program's own balance
/// surface (spec-species-xp.md §4), not plant/zombie type progression's — bundling them into the
/// existing file would mean a species balance pass touches a file three other, unrelated systems also
/// read.</para>
/// </summary>
public sealed record SpeciesProgressionTuning(
    long CurveFirst, long CurveStep,
    long RunCompletionAward, long PlacementAward);

/// <summary>Same throw-if-unconfigured shape as every other tuning hub in this codebase
/// (`AptitudeTuningHub`, `PowerTuningHub`, `ProgressionTuningHub`) — "failing loudly at load beats
/// failing later." Configured by the SERVER HOST ONLY (`Program.cs`) — the injector never computes a
/// species level, so it never needs this.</summary>
public static class SpeciesProgressionTuningHub
{
    static SpeciesProgressionTuning? _tuning;

    public static void Configure(SpeciesProgressionTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>Non-throwing check — <c>RpgXpAwardMap</c>'s species-placement award treats this hub as
    /// an optional enrichment (most progression tests never configure it, and type/player XP awarding
    /// must keep working exactly as it always has without it).</summary>
    public static bool IsConfigured => _tuning != null;

    public static SpeciesProgressionTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "SpeciesProgressionTuningHub.Configure(...) has not run. Species levels read " +
        "data/tuning/species-progression.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");
}

public sealed class SpeciesProgressionTuningRejection : Exception
{
    public SpeciesProgressionTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2) — same shape as
/// <see cref="ProgressionTuningLoader"/>.</summary>
public static class SpeciesProgressionTuningLoader
{
    public static SpeciesProgressionTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SpeciesProgressionTuningRejection("species progression tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new SpeciesProgressionTuningRejection($"species progression tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var curve = Obj(root, "xpCurve");
            var awards = Obj(root, "awards");

            return new SpeciesProgressionTuning(
                CurveFirst: Long(curve, "first"),
                CurveStep: Long(curve, "step"),
                RunCompletionAward: Long(awards, "runCompletion"),
                PlacementAward: Long(awards, "placement"));
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new SpeciesProgressionTuningRejection($"species progression tuning: missing or non-object '{key}'");
        return el;
    }

    /// <summary>Whole-number reader accepting `80`/`80.0`, refusing `80.5` — XP is an integer
    /// magnitude end to end (docs/architecture/numeric-types.md).</summary>
    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new SpeciesProgressionTuningRejection($"species progression tuning: missing or non-number '{key}'");
        if (el.TryGetInt64(out var exact)) return exact;

        var raw = el.GetDouble();
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw != Math.Floor(raw))
            throw new SpeciesProgressionTuningRejection(
                $"species progression tuning: '{key}' = {raw} is not a whole number — XP is an integer magnitude");
        if (raw < long.MinValue || raw > long.MaxValue)
            throw new SpeciesProgressionTuningRejection($"species progression tuning: '{key}' = {raw} is out of range for long");
        return (long)raw;
    }
}
