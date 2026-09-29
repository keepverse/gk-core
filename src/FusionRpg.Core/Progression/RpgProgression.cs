namespace FusionRpg.Core.Progression;

public static class RpgActorKinds
{
    public const string Player = "player";
    public const string Plant = "plant";
    public const string Zombie = "zombie";

    /// <summary>`species-build` T1.1 — a creature SPECIES' own per-player level (module 3, `species-xp`),
    /// distinct from the `Plant`/`Zombie` PvZ-TYPE rows above: those key on the PvZ engine's own type
    /// id and are read by other things today, so they stay untouched. A species row keys on
    /// <c>CreatureSpeciesDef.CreatureTypeId</c> (the disjoint ≥10000 id space, already unique per species —
    /// `CreatureSpeciesCatalog.Validate`) — spec-species-xp.md §1 Option A: reuse
    /// <c>rpg_actor_progression</c>/<c>rpg_xp_ledger</c> with a new `kind`, never a second store.</summary>
    public const string Species = "species";

    /// <summary>A unique creature INSTANCE's own level (`rpg_unique_actors`), distinct from
    /// <see cref="Species"/>, which is the per-player level of a species TYPE. Added 2026-09-05 by the
    /// effort-power reconciliation: this level feeds the same quadratic `P(Theta)` as every other, but
    /// its cost was a flat, hardcoded 100 XP per level, which made specimen power quadratic IN EFFORT
    /// where ssot-power-scale.md &sect;10.5 requires linear. It now reads the shared arithmetic ladder.</summary>
    public const string Specimen = "specimen";

    /// <summary>`empire-level` (module 13, `empire-progression` Wave D) — ONE row per empire, keyed
    /// `(SaveId, EmpireId)` with `type_id = 0`, fed only by that empire's own species reaching a new
    /// highest level. It exists so the empire level inherits the ledger, the dedupe, the level-change
    /// events, the revision counter, the REST reads and the `RpgProgressionUpdated` broadcast for free,
    /// exactly as <see cref="Species"/> did — never a second store. It **never** feeds `Theta` or
    /// `P(Theta)`: an empire level grants respecs, never a magnitude (`decisions.md`, *Empire level
    /// (2026-09-18)*; `ssot-power-scale.md` &sect;10.1 row 6's cost-ladder verdict).</summary>
    public const string Empire = "empire";

    /// <summary>The closed vocabulary of a progression row's kind (5 members until 2026-09-21, 6 now).
    /// Growth is a reviewed change: it is a code-owned vocabulary a human edits, which is why
    /// <c>EmpireLevelGrantsTests</c> pins the member count and says so.</summary>
    public static bool IsKnown(string? kind) =>
        kind is Player or Plant or Zombie or Species or Specimen or Empire;
}

public static class RpgXpReasons
{
    public const string Kill = "kill";
    public const string Defeat = "defeat";
    public const string Mower = "mower";
    public const string PlantPlace = "plant_place";
    public const string ZombieSpawn = "zombie_spawn";

    /// <summary>`species-build` T1.3 — the "outcome" term of the two-term species faucet
    /// (spec-species-xp.md §3), fired once per resolved match a species was fielded in. The
    /// per-placement term reuses <see cref="PlantPlace"/>/<see cref="ZombieSpawn"/> as its reason.</summary>
    public const string SpeciesRunComplete = "species_run_complete";

    /// <summary>`species-build` T1.4 — the non-lawn source (spec-species-xp.md §2 "Expedition"):
    /// a specimen's battle-won xp also levels its species row, in the same transaction as the
    /// specimen award. The standalone-first proof source — reachable with the game closed.</summary>
    public const string SpeciesExpedition = "species_expedition";
    public const string SpecimenLawnKill = "specimen_lawn_kill";
    public const string SpecimenLawnDuration = "specimen_lawn_duration";

    /// <summary>`zomboss-commander-clock` SP7.2 — a human `defeat` awards this to Zomboss's commander
    /// (the outcome is named from ZOMBOSS's own side: Zomboss "won" the run).</summary>
    public const string ZombossRunVictory = "zomboss_run_victory";

    /// <summary>A human `victory` awards this (a smaller consolation amount) to Zomboss's commander.</summary>
    public const string ZombossRunDefeat = "zomboss_run_defeat";

    /// <summary>`empire-level` — the one reason an empire row is ever credited: one species of that
    /// empire reached a new highest level. The dedupe key beside it is `sp:{speciesTypeId}:L{level}`,
    /// so the empire's own ledger says which species level paid for which empire level. There is no
    /// second reason today: the empire row never demotes, so it is never written with a negative
    /// delta.</summary>
    public const string EmpireSpeciesLevelUp = "empire_species_level_up";
}

/// <summary>Arithmetic XP curve per actor kind (POC-tuned; faster early levels). Config-backed
/// (tunables-ssot.md T1) — gk-core/data/tuning/progression.v3.json's xpCurve.</summary>
public static class RpgXpCurve
{
    static ProgressionTuning? _tuning;

    public static void Configure(ProgressionTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static ProgressionTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "RpgXpCurve.Configure(...) has not run. Every XP curve reads " +
        "data/tuning/progression.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    public static (long First, long Step) ParamsFor(string kind) => kind switch
    {
        // Species reads its OWN tunable pair from its own file (species-progression.v1.json), not
        // progression.v3.json's xpCurve — a species' pace is this program's own balance surface, not
        // plant/zombie/player progression's (spec-species-xp.md §4). Matched first and evaluated
        // lazily by the switch, so a caller that never touches Species never needs this hub configured.
        RpgActorKinds.Species => (SpeciesProgressionTuningHub.Tuning.CurveFirst, SpeciesProgressionTuningHub.Tuning.CurveStep),
        RpgActorKinds.Specimen => (Tuning.SpecimenCurve.First, Tuning.SpecimenCurve.Step),
        RpgActorKinds.Empire => EmpireCurve(),
        RpgActorKinds.Plant => (Tuning.PlantCurve.First, Tuning.PlantCurve.Step),
        RpgActorKinds.Zombie => (Tuning.ZombieCurve.First, Tuning.ZombieCurve.Step),
        // player — first match clears L1; mid-game paces ~L12–18 / 20 wins
        _ => (Tuning.PlayerCurve.First, Tuning.PlayerCurve.Step)
    };

    /// <summary>
    /// `empire-level`'s own pair (`xpCurve.empire`). Row 6's arithmetic ladder again, with its own row
    /// landed beside it (`ssot-power-scale.md` &sect;10.1; `decisions.md`, *Empire level (2026-09-18)*) —
    /// a cost ladder whose unit is species level-ups, **never** a power ladder.
    ///
    /// <para><b>Why a rejection survives here at all.</b>
    /// <see cref="ProgressionTuningLoader.Parse"/> now REQUIRES the key, so no published document can be
    /// missing it — the EP4.2 publish that added it moved every reader of the previous version in the
    /// same commit, which is why the requirement could be hard from that version on (tunables-ssot.md
    /// T5; `spec-empire-level.md` &sect;Tunables). `null` is still reachable one way: an in-code
    /// `ProgressionTuning` built by a test bootstrap that predates the key and is never loaded from a
    /// file. That case refuses by name too rather than reading a zero that would level an empire every
    /// 1 XP. Nothing is silently defaulted: a curve is either the published one or an error.</para>
    /// </summary>
    static (long First, long Step) EmpireCurve()
    {
        var curve = Tuning.EmpireCurve ?? throw new ProgressionTuningRejection(
            "progression tuning: missing 'xpCurve.empire' — the empire level reads its own tunable pair " +
            "(ssot-power-scale.md s10.1 row 6; decisions.md, 'Empire level (2026-09-18)'). There is no " +
            "built-in default (tunables-ssot.md T5).");
        return (curve.First, curve.Step);
    }

    /// <summary>
    /// The arithmetic cost ladder `first + (L−1)·step` — ssot-power-scale.md §10 row 6, kept
    /// unchanged as a COST ladder (exempt from the one-ladder rule; only its ratio against `P(Θ)`
    /// matters, §10.5). `long` end to end: XP is a persisted magnitude, so overflow throws rather
    /// than silently losing precision the way a `double` would past 2^53.
    /// </summary>
    public static long XpToNext(string kind, long level)
    {
        if (level < 1) level = 1;
        var (first, step) = ParamsFor(kind);
        long need;
        checked { need = first + (level - 1) * step; }
        return need < 1 ? 1 : need;
    }

    /// <summary>
    /// Cumulative XP to reach `level` — the triangular sum of the arithmetic ladder above, which is
    /// why total cost is QUADRATIC while each step is linear (§10.5). `n·(2·first + (n−1)·step)` is
    /// always even, so the halving is exact and no rounding decision exists to get wrong.
    /// </summary>
    public static long TotalToReach(string kind, long level)
    {
        if (level <= 1) return 0;
        var (first, step) = ParamsFor(kind);
        // sum_{i=0}^{L-2} (first + i*step)
        var n = level - 1;
        checked { return n * (2 * first + (n - 1) * step) / 2; }
    }
}

/// <summary>Base award deltas (POC-tuned). Kill is multiplied by a power scale (T3.3: the stub class
/// that used to carry this is deleted — see RpgXpAwardMap.NoKillPowerScaleYet).
/// Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/progression.v3.json's awards. Not a `const`:
/// RpgXpAwardMapTests' [InlineData] rows hardcode the current values instead (attributes require
/// compile-time constants), asserted separately against the live value.</summary>
public static class RpgXpAwards
{
    static ProgressionTuning? _tuning;

    public static void Configure(ProgressionTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static XpAwardsTuning Tuning => (_tuning ?? throw new InvalidOperationException(
        "RpgXpAwards.Configure(...) has not run. Every award reads data/tuning/progression.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.")).Awards;

    public static long Kill => Tuning.Kill;
    public static long Defeat => Tuning.Defeat;
    public static long Mower => Tuning.Mower;
    public static long PlantPlace => Tuning.PlantPlace;
    public static long ZombieSpawn => Tuning.ZombieSpawn;
    public static long SpecimenLawnKill => Tuning.SpecimenLawnKill;
    public static long SpecimenBoundIntervalMs => Tuning.SpecimenBoundIntervalMs;
    public static long SpecimenBoundIntervalXp => Tuning.SpecimenBoundIntervalXp;

    /// <summary>`zomboss-commander-clock` SP7.1 — see <see cref="XpAwardsTuning.ZombossRunVictoryXp"/>'s
    /// own doc comment for why this reads 0 rather than throwing against an old `progression.v3.json`:
    /// SP7.2's own writer is where "never configured" is refused, at first real use.</summary>
    public static long ZombossRunVictoryXp => Tuning.ZombossRunVictoryXp;

    /// <summary>See <see cref="ZombossRunVictoryXp"/>.</summary>
    public static long ZombossRunDefeatXp => Tuning.ZombossRunDefeatXp;

    /// <summary>`empire-level` — see <see cref="XpAwardsTuning.SpeciesLevelUp"/>'s own doc comment: a
    /// REQUIRED key from the version that first carries it, refused by name at parse, and here only for
    /// an in-code tuning a test bootstrap built without it.</summary>
    public static long SpeciesLevelUp => Tuning.SpeciesLevelUp ?? throw new ProgressionTuningRejection(
        "progression tuning: missing 'awards.speciesLevelUp' — the empire level's only faucet " +
        "(spec-empire-level.md; tunables-ssot.md T5). There is no built-in default.");
}

public sealed class RpgActorState
{
    public long Level { get; set; } = 1;
    /// <summary>Whole XP. `long`, never `double` — this value is persisted (docs/architecture/numeric-types.md numeric rule).</summary>
    public long Xp { get; set; }
    public long HighestLevel { get; set; } = 1;
    public long DemotionCount { get; set; }
    public long Revision { get; set; }
}

public sealed class LevelChangeEvent
{
    public long PlayerId { get; init; }
    public string Kind { get; init; } = RpgActorKinds.Player;
    public int TypeId { get; init; }
    public long LevelBefore { get; init; }
    public long LevelAfter { get; init; }
    public long XpAfter { get; init; }
    public long DemotionCount { get; init; }
    public string Reason { get; init; } = "";
    public string Direction { get; init; } = "up"; // up | down
}

public sealed class RpgXpApplyResult
{
    public RpgActorState State { get; init; } = new();
    public IReadOnlyList<LevelChangeEvent> LevelChanges { get; init; } = Array.Empty<LevelChangeEvent>();
}

public static class RpgXpApply
{
    /// <summary>
    /// Applies a whole-XP delta. `delta` is `long` because XP is a persisted magnitude: any
    /// fractional scaling (today `RpgXpAwardMap.NoKillPowerScaleYet = 1.0`, tomorrow content-scale)
    /// is rounded ONCE at the award boundary in <see cref="RpgXpAwardMap"/>, never accumulated as a
    /// fraction here — an XP total built from repeated fractional adds is order-dependent, and
    /// `state.Xp >= need` would then compare accumulated error against a threshold.
    /// </summary>
    public static RpgXpApplyResult Apply(
        string kind,
        RpgActorState state,
        long delta,
        long playerId = 0,
        int typeId = 0,
        string reason = "")
    {
        var beforeLevel = state.Level;
        var beforeXp = state.Xp;
        var demotion = state.DemotionCount;
        var changes = new List<LevelChangeEvent>();

        // species-build T1.1: XP is a persisted magnitude (docs/architecture/numeric-types.md numeric-overflow rule) -- this
        // project does not set <CheckForOverflowUnderflow>, so a plain `+=` here wrapped silently
        // instead of throwing (caught by SpeciesProgressionTests.Apply_overflow_throws_neverWraps,
        // species-xp's own "overflow throws" acceptance criterion). `checked` applies to every kind,
        // not just species -- player/plant/zombie XP is exactly as much a magnitude as a species level.
        checked { state.Xp += delta; }

        if (delta > 0)
        {
            while (state.Level < long.MaxValue)
            {
                var need = RpgXpCurve.XpToNext(kind, state.Level);
                if (state.Xp < need) break;
                state.Xp -= need;
                var from = state.Level;
                state.Level++;
                if (state.Level > state.HighestLevel)
                    state.HighestLevel = state.Level;
                changes.Add(new LevelChangeEvent
                {
                    PlayerId = playerId,
                    Kind = kind,
                    TypeId = typeId,
                    LevelBefore = from,
                    LevelAfter = state.Level,
                    XpAfter = state.Xp,
                    DemotionCount = demotion,
                    Reason = reason,
                    Direction = "up"
                });
            }
        }

        while (state.Xp < 0)
        {
            if (state.Level <= 1)
            {
                state.Xp = 0;
                break;
            }
            state.Level--;
            demotion++;
            state.DemotionCount = demotion;
            checked { state.Xp += RpgXpCurve.XpToNext(kind, state.Level); }
            changes.Add(new LevelChangeEvent
            {
                PlayerId = playerId,
                Kind = kind,
                TypeId = typeId,
                LevelBefore = state.Level + 1,
                LevelAfter = state.Level,
                XpAfter = state.Xp,
                DemotionCount = demotion,
                Reason = reason,
                Direction = "down"
            });
        }

        if (state.Level > state.HighestLevel)
            state.HighestLevel = state.Level;
        state.Revision++;

        _ = beforeLevel;
        _ = beforeXp;
        return new RpgXpApplyResult { State = state, LevelChanges = changes };
    }
}

public interface ILevelChangeHandler
{
    int Order { get; }
    void Handle(LevelChangeEvent e, Action next);
}

public sealed class LevelChangePipeline
{
    readonly List<ILevelChangeHandler> _handlers;

    public LevelChangePipeline(IEnumerable<ILevelChangeHandler>? handlers = null)
    {
        _handlers = (handlers ?? Array.Empty<ILevelChangeHandler>())
            .OrderBy(h => h.Order)
            .ToList();
    }

    public void Run(LevelChangeEvent e)
    {
        var i = 0;
        void Next()
        {
            if (i >= _handlers.Count) return;
            var h = _handlers[i++];
            h.Handle(e, Next);
        }
        Next();
    }

    public void RunAll(IEnumerable<LevelChangeEvent> events)
    {
        foreach (var e in events)
            Run(e);
    }
}
