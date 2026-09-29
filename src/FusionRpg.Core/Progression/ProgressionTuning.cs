using System.Text.Json;

namespace FusionRpg.Core.Progression;

/// <summary>
/// XP ladder parameters. <c>long</c>, not <c>double</c> (docs/architecture/numeric-types.md numeric rule — XP is a persisted
/// magnitude): the config carries whole numbers today and <see cref="ProgressionTuningLoader"/>
/// rejects a fractional one rather than silently truncating it.
/// </summary>
public sealed record XpCurveParams(long First, long Step);

/// <summary>Award deltas in whole XP — same integer rule as <see cref="XpCurveParams"/>.</summary>
public sealed record XpAwardsTuning(long Kill, long Defeat, long Mower, long PlantPlace, long ZombieSpawn)
{
    /// <summary>Dedicated unique-specimen lawn kill award. Zero keeps older tuning documents
    /// compatible until the balance file opts into the dedicated source.</summary>
    public long SpecimenLawnKill { get; init; }

    /// <summary>Active-match milliseconds in one dedicated participation interval.</summary>
    public long SpecimenBoundIntervalMs { get; init; }

    /// <summary>Dedicated unique-specimen XP awarded per completed active interval.</summary>
    public long SpecimenBoundIntervalXp { get; init; }

    /// <summary>`zomboss-commander-clock` SP7.1 — the award a human `defeat` gives Zomboss's commander
    /// (Zomboss "won" that run).
    ///
    /// <para><b>Deliberate, evidenced deviation from the spec's literal "rejects a file missing
    /// either one, naming the key" text</b> (the SAME lesson species-progression step 6.1's own
    /// `AptitudeLayerWeights` correction already established this session): when this key landed,
    /// `progression.v1.json` was the only published revision, and ~28 real, unrelated test files
    /// hardcode that literal path (no "find latest" resolver exists for this domain, unlike
    /// `aptitudes.v*.json`). A hard-required key here would have made every one of those files
    /// unloadable the instant `v2.json` published, breaking dozens of tests that never touch the
    /// Zomboss commander clock at all. This
    /// is the SAME "zero keeps older tuning documents compatible" discipline
    /// <see cref="SpecimenLawnKill"/> already established two fields above, applied to a second new
    /// field pair — absent parses to 0, never a rejection.
    ///
    /// <b>Corrected in SP7.2</b> (checked against evidence, not left as written here): a genuinely
    /// zeroed/absent value at first use gives nothing SILENTLY —
    /// <c>RpgStore.Progression.cs</c>'s own <c>AwardUniqueLawnKillUnlocked</c>/
    /// <c>AwardUniqueLawnDurationUnlocked</c> (the established sibling consumers of THIS SAME tuning
    /// class) already do exactly that (<c>if (delta &lt;= 0) return;</c>), not the throwing
    /// <c>PointBudget.SkillPointsFor</c> precedent this comment originally named — that was the wrong
    /// sibling to cite, since `PointBudget`'s absence signal is a missing dictionary key, not a
    /// defaulted-to-zero field.</para></summary>
    public long ZombossRunVictoryXp { get; init; }

    /// <summary>The award a human `victory` gives Zomboss's commander (a smaller consolation amount
    /// for losing the run) — same absence-tolerant-at-parse, silent-at-zero-first-use discipline as
    /// <see cref="ZombossRunVictoryXp"/> (see its doc comment for the corrected precedent).</summary>
    public long ZombossRunDefeatXp { get; init; }

    /// <summary>`empire-level` — the whole-XP delta one species level reaching a new highest level
    /// credits its own empire (`empire_species_level_up`).
    ///
    /// <para><b>Required from EP4.2 on.</b> The publish that first carries the key
    /// (`progression.v3.json`) also moves every reader of the previous version in the same commit (H7),
    /// so <see cref="ProgressionTuningLoader.Parse"/> now REQUIRES it and refuses a document without it
    /// by name (tunables-ssot.md T5). `null` is reachable only from an in-code tuning constructed in a
    /// test bootstrap that predates the key; <see cref="RpgXpAwards.SpeciesLevelUp"/> refuses that case
    /// by name too, rather than reading a zero that would freeze every empire level.</para></summary>
    public long? SpeciesLevelUp { get; init; }
}

/// <summary>Progression balance surface (tunables-ssot.md T1) — RpgXpCurve/RpgXpAwards.</summary>
public sealed record ProgressionTuning(
    int SchemaVersion, int Version,
    XpCurveParams PlantCurve, XpCurveParams ZombieCurve, XpCurveParams PlayerCurve,
    XpCurveParams SpecimenCurve,
    XpAwardsTuning Awards)
{
    /// <summary>`empire-level`'s own cost ladder (`xpCurve.empire`). REQUIRED by
    /// <see cref="ProgressionTuningLoader.Parse"/> from the version that first carries it
    /// (`progression.v3.json`, EP4.2) — that publish moves every reader of the previous version in the
    /// same commit (H7), so no loaded document can be missing it.
    ///
    /// <para>Held as a nullable init property rather than a positional parameter so that the existing
    /// positional constructions (the test bootstraps, which predate the key and are not loaded from a
    /// file) keep compiling unchanged. <c>null</c> is therefore an in-code-tuning state, not a
    /// published-document state; <see cref="RpgXpCurve.ParamsFor"/> refuses it by name.</para></summary>
    public XpCurveParams? EmpireCurve { get; init; }
}

public sealed class ProgressionTuningRejection : Exception
{
    public ProgressionTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class ProgressionTuningLoader
{
    public static ProgressionTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ProgressionTuningRejection("progression tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ProgressionTuningRejection($"progression tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var curve = Obj(root, "xpCurve");
            var awards = Obj(root, "awards");

            return new ProgressionTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                PlantCurve: Curve(curve, "plant"),
                ZombieCurve: Curve(curve, "zombie"),
                PlayerCurve: Curve(curve, "player"),
                SpecimenCurve: Curve(curve, "specimen"),
                Awards: new XpAwardsTuning(
                    Kill: Long(awards, "kill"),
                    Defeat: Long(awards, "defeat"),
                    Mower: Long(awards, "mower"),
                    PlantPlace: Long(awards, "plantPlace"),
                    ZombieSpawn: Long(awards, "zombieSpawn"))
                {
                    SpecimenLawnKill = OptionalPositiveLong(awards, "specimenLawnKill"),
                    SpecimenBoundIntervalMs = OptionalPositiveLong(awards, "specimenBoundIntervalMs"),
                    SpecimenBoundIntervalXp = OptionalPositiveLong(awards, "specimenBoundIntervalXp"),
                    // zomboss-commander-clock SP7.1: absence-tolerant at parse (see the field's own
                    // doc comment for why) -- the SAME OptionalPositiveLong discipline as the three
                    // fields above, not a hard Long(...) requirement.
                    ZombossRunVictoryXp = OptionalPositiveLong(awards, "zombossRunVictoryXp"),
                    ZombossRunDefeatXp = OptionalPositiveLong(awards, "zombossRunDefeatXp"),
                    // empire-level EP4.2: REQUIRED from this version on. `empire-level`'s own publish is
                    // the change that first carries it and that moves every reader of the previous
                    // version (H7), so the tolerant window EP4.1 opened is over -- T5's rule (a missing
                    // tunable is a load rejection naming it) now applies with no exception.
                    SpeciesLevelUp = RequiredPositiveLong(awards, "speciesLevelUp")
                })
            {
                // empire-level EP4.2: required at parse. `Curve` refuses a missing/non-object 'empire'
                // by name, the same shape plant/zombie/player/specimen already have.
                EmpireCurve = Curve(curve, "empire"),
            };
        }
    }

    static XpCurveParams Curve(JsonElement parent, string key)
    {
        var el = Obj(parent, key);
        return new XpCurveParams(Long(el, "first"), Long(el, "step"));
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new ProgressionTuningRejection($"progression tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ProgressionTuningRejection($"progression tuning: missing or non-integer '{key}'");
        return v;
    }

    /// <summary>
    /// A whole-number reader that accepts JSON's `80` and `80.0` alike but REFUSES `80.5`. XP is an
    /// integer magnitude end to end (docs/architecture/numeric-types.md: `long` for any magnitude, never a persisted `double`),
    /// so a fractional tuning value is a balance mistake to report, not a value to round away.
    /// </summary>
    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new ProgressionTuningRejection($"progression tuning: missing or non-number '{key}'");
        if (el.TryGetInt64(out var exact)) return exact;

        var raw = el.GetDouble();
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw != Math.Floor(raw))
            throw new ProgressionTuningRejection(
                $"progression tuning: '{key}' = {raw} is not a whole number — XP is an integer magnitude");
        if (raw < long.MinValue || raw > long.MaxValue)
            throw new ProgressionTuningRejection($"progression tuning: '{key}' = {raw} is out of range for long");
        return (long)raw;
    }

    static long OptionalPositiveLong(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out _)) return 0L;
        var value = Long(parent, key);
        if (value <= 0)
            throw new ProgressionTuningRejection($"progression tuning: '{key}' must be positive");
        return value;
    }

    /// <summary>`Long` plus the `>= 1` rule every award carries — a REQUIRED positive whole number. Used
    /// for a key a published version made mandatory (`empire-level` EP4.2), where
    /// <see cref="OptionalPositiveLong"/>'s absent-to-0 would silently freeze a progression track instead
    /// of reporting the missing key.</summary>
    static long RequiredPositiveLong(JsonElement parent, string key)
    {
        var value = Long(parent, key);
        if (value <= 0)
            throw new ProgressionTuningRejection($"progression tuning: '{key}' must be positive");
        return value;
    }
}

/// <summary>Fans one progression.v{n}.json load out to both classes that read it (tunables-ssot.md §7.2).</summary>
public static class ProgressionTuningHub
{
    public static void Configure(ProgressionTuning tuning)
    {
        RpgXpCurve.Configure(tuning);
        RpgXpAwards.Configure(tuning);
    }
}
