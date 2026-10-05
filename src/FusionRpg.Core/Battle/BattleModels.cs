using System.Text.Json.Serialization;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Battle;

/// <summary>One combatant entering a battle — a creature specimen snapshot or a wave enemy.</summary>
public sealed record BattleActorSetup
{
    public string Key { get; init; } = "";                 // stable within the battle (e.g. "squad:0", "wave:3")
    public string Side { get; init; } = "";                // "squad" | "wave"
    public string SpeciesId { get; init; } = "";
    public int TypeId { get; init; }                        // creature type id (disjoint space) for event emission
    public int Level { get; init; } = 1;

    /// <summary>Alias for <see cref="Level"/> — the actor's Θ (content-authoring, T2.3,
    /// spec-content-authoring.md §2.2). <c>Level</c> stays the real/serialized name: audit F7 and
    /// decisions.md:42 are explicit that a field rename here moves all four expedition hashes; "the
    /// alias is the rule, not a fallback." Read this for new Θ-flavored code; <c>Level</c> for
    /// anything that touches serialization. <see cref="JsonIgnoreAttribute"/> is load-bearing, not
    /// decoration — found the hard way: a first draft without it moved
    /// ExpeditionResolverTests.Tier_goldens_are_locked's hash, because System.Text.Json serializes
    /// get-only computed properties by default.</summary>
    [JsonIgnore]
    public int Index => Level;

    public ElementTypeId? ElementPrimary { get; init; }
    public ElementTypeId? ElementSecondary { get; init; }
    public IReadOnlyList<string> TraitIds { get; init; } = Array.Empty<string>();

    /// <summary>The `rpg_unique_actor` this setup represents — its own stable `instance_id` string,
    /// matching `OwnerScope.UniqueActor`'s key exactly (never a numeric id) — for module 5's equipment
    /// lookup, which reaches battle as op-aware `BoundDerivedAtom` rows through
    /// <see cref="BattleHubInputs.BoundAtoms"/> and is projected by
    /// <see cref="EquipAtomSource.DerivedAtomsFor"/> (the battle-only `EquipAtomSource.ModsFor` that
    /// this comment used to cite was deleted by `battle-ops-parity` T7, 2026-09-13 — see
    /// `actor-hub-ssot.md` §“Former debt, now fused”). Null for a setup with no durable specimen behind
    /// it (a wave
    /// creature, an expedition roster entry, a test fixture); equipment resolves to nothing.
    /// <see cref="JsonIgnoreAttribute"/> for the identical reason <see cref="Index"/> already carries
    /// one: expedition tier resolution serializes this record as part of its own golden hash, and a
    /// specimen id — always null there, since expeditions build setups from wave/species data, never a
    /// real owned creature — is not semantically part of what that hash locks. Found the same way
    /// <see cref="Index"/>'s comment describes: a first draft without this moved
    /// `ExpeditionResolverTests.Tier_goldens_are_locked`'s hash.</summary>
    [JsonIgnore]
    public string? SpecimenId { get; init; }
    public long MaxHp { get; init; }
    public long Atk { get; init; }
    public long Defense { get; init; }

    /// <summary>
    /// `battle-tempo` `tempo-content` — the actor's own attack interval, carried alongside
    /// <see cref="MaxHp"/>/<see cref="Atk"/>/<see cref="Defense"/> as a base stat rather than through
    /// <see cref="ChannelMods"/> (that field is the CALLER'S generic additive overlay; this is
    /// per-actor identity data the composer reads directly, the same shape those three already are).
    /// `0` (the default) means "no tempo authored" and floors to the default `turn.speed`
    /// (<see cref="Battle.SpeciesTempoProjection.SpeedFor"/>) — every existing setup literal in the
    /// tree (hand-built battle goldens included) stays exactly as it was without being touched.
    ///
    /// <para><b>Moves the expedition tier-resolution hash the moment a wave enemy carries a non-zero
    /// value</b> (`ExpeditionResolverTests.Tier_goldens_are_locked` serializes this record) — a MORE
    /// SPECIFIC instance of `tempo-content`'s own documented "moves goldens" cost
    /// (spec-tempo-content.md §9), not a new one: turn order was always going to move once species
    /// tempo varies, and this field is what lets it vary per actor.</para>
    /// </summary>
    public long AttackIntervalMs { get; init; }

    /// <summary>Additive derived-channel adjustments (tests, traits, one-offs). Equipment enters via <see cref="EquipAtomSource"/> when <c>SpecimenId</c> is set — do not also mirror equip here. Integer amounts only.</summary>
    public IReadOnlyList<BattleChannelMod> ChannelMods { get; init; } = Array.Empty<BattleChannelMod>();

    /// <summary>
    /// battle-hub-fuse T5 — Hub inputs the builders resolved for this actor (aptitude allocation,
    /// bound atoms, star/loyalty, draughts, injuries). <c>BattleHubCompose</c> reads these instead
    /// of pre-folded <c>ChannelMods</c>; null (the default) means baseline + affinity + traits +
    /// tempo only. <c>WhenWritingDefault</c> keeps every existing golden byte-identical: expedition
    /// tier resolution serializes this record before any builder attaches inputs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public BattleHubInputs? HubInputs { get; init; }

    /// <summary>Statuses applied attacker-less at battle start (test seams now, trait/attack riders later).</summary>
    public IReadOnlyList<BattleStatusSpec> InitialStatuses { get; init; } = Array.Empty<BattleStatusSpec>();


    /// <summary>Innate shield content row (battle-adoption) — applied at setup, no expiry unless set.</summary>
    public BattleInnateShield? InnateShield { get; init; }

    /// <summary>
    /// T22: which actions this actor is entering battle with — a real loadout if one exists, else the
    /// caller's own `RpgStore.GetLoadoutOrAutoEquip` resolution. Purely carried data: nothing in
    /// `BattleEngine`'s round loop reads it today (the engine's only declared action is the fixed
    /// basic attack, see `BasicAttack.cs`) — this exists so it can ride, unread, into
    /// <see cref="BattleActorResult.EquippedActionIds"/> for reporting. <c>null</c> when the caller has
    /// no action/loadout system to consult (every existing test builder).
    ///
    /// <para><b><see cref="JsonIgnoreAttribute"/> is load-bearing here, not decoration</b> — found the
    /// hard way, exactly like <see cref="Index"/>'s own comment warns: this record rides unchanged
    /// into <c>ExpeditionResolution</c>'s own serialized+hashed shape
    /// (<c>ExpeditionResolverTests.Tier_goldens_are_locked</c>), and a brand-new nullable field with no
    /// suppression serializes as an added `"EquippedActionIds":null` key for every existing squad
    /// builder, moving that golden for a shape change nobody reviewed as a determinism break.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string>? EquippedActionIds { get; init; }

    /// <summary>
    /// base-defense `siege-construction`/`siege-ai` (2026-09-07, resolved after a MAJOR finding this
    /// session: `DistrictAssaultResolver.BuildAnimateSetups` never set <see cref="EquippedActionIds"/>
    /// at all, so every siege actor fell back to the fixed basic attack — construction actions were
    /// structurally unreachable in any real siege, not just missing content). A PURELY ADDITIVE
    /// sibling to <see cref="EquippedActionIds"/>, never a replacement for it: <see cref="EquippedActionIds"/>'s
    /// own resolution (basic-attack fallback, or a real catalog-resolved loadout) runs completely
    /// unchanged, and these PRE-COMPILED actions are appended to whatever it produces
    /// (<c>BattleRunState</c>'s own setup loop) — an actor never loses its basic attack by gaining
    /// this. Pre-compiled rather than id-based like <see cref="EquippedActionIds"/> because the only
    /// today's caller (`DistrictAssaultResolver`) already holds statically-compiled, content
    /// (`ConstructionActions.Actions`, never player-rolled) that needs no `ActionCatalog`/database
    /// round trip to resolve — the same "caller already holds the compiled form" shape
    /// <c>ConstructionActivation.Fire</c>'s own <c>firingAction</c> parameter established. <c>null</c>
    /// for every existing caller (every non-siege battle, and any siege actor a future pass does not
    /// grant construction to) — the exact byte-identical-to-today default.
    ///
    /// <para><see cref="JsonIgnoreAttribute"/> for the SAME reason <see cref="EquippedActionIds"/>'s
    /// own comment states: `WhenWritingDefault` keeps every existing golden (expedition's own
    /// serialized+hashed `BattleActorSetup` included) byte-identical since this field is never set
    /// outside `DistrictAssaultResolver`.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<FusionRpg.Core.Actions.CompiledAction>? AdditionalHeldActions { get; init; }

    /// <summary>
    /// base-defense `combatant-kind` (owner decision 4): what sort of thing this actor is. Structures
    /// and obstacles are a new actor kind — no level, no equipment, no aura — but traits, actions, and
    /// hit points.
    ///
    /// <para><b>Plain <see cref="JsonIgnoreAttribute"/>, matching <see cref="Index"/> and
    /// <see cref="SpecimenId"/> exactly.</b> Both of those carry one because expedition tier resolution
    /// serializes this record into a golden hash and any newly-serialized member moves it — found the
    /// hard way, twice, and recorded in their own comments above. <c>WhenWritingDefault</c> was
    /// considered and rejected: it would move that hash at the first siege instead of never, which is
    /// the same defect with a delay long enough that the responsible module is no longer suspected.</para>
    ///
    /// <para>Safe to ignore because the kind is construction-time: a setup is built fresh from world
    /// state on every resolve, so it is never read back out of JSON.</para>
    /// </summary>
    [JsonIgnore]
    public CombatantKind Kind { get; init; } = CombatantKind.Animate;

    /// <summary>
    /// The animate actor's <see cref="Key"/> currently occupying this structure, or null. A garrisoned
    /// structure lends its actions to its occupant (<see cref="BattleEngine"/>'s
    /// <c>BattleRunState.HeldActionsOf</c>); it never acts on its own initiative, so
    /// <see cref="CombatantKind.Structure"/> stays a complete statement of "does not take turns."
    /// <see cref="JsonIgnoreAttribute"/> for the same reason as <see cref="Kind"/> — construction-time,
    /// never read back, and always null outside a siege so ignoring it costs nothing today and avoids
    /// the identical delayed-golden-move risk at the first garrisoned siege.
    /// </summary>
    [JsonIgnore]
    public string? GarrisonedBy { get; init; }

    // party-dungeon D2.8 (delve-battle-profile) — seven additive fields for a multi-room, multi-party
    // raid. Every one follows EquippedActionIds' own precedent above to the letter:
    // [JsonIgnore(Condition = WhenWritingDefault)], an init-only tail property (this record has no
    // positional constructor to break), and null for every existing caller (WaveCatalog,
    // WebMatchService, DistrictAssaultResolver, every test fixture) since none of them sets these —
    // so every existing serialized shape (all four battle hashes, the 32-seed sweep, all four
    // expedition tier hashes) stays byte-identical. Consumption (carry-in wiring, the delve profile,
    // the phase-grant trigger) is later delve-battle-profile tasks (D2.9-D2.14+); this task is shape
    // only.

    /// <summary>Carried HP from a previous room in the same delve. <c>null</c> means "no carry-in" —
    /// the consumer's own rule (D2.11) is <c>Hp = CurrentHp ?? MaxHp</c>, never a magic sentinel.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long? CurrentHp { get; init; }

    /// <summary>Which raid party (0-based) this actor belongs to — null for a non-raid setup (every
    /// shipped expedition/web-match/siege caller). Feeds the per-party economy key
    /// (<c>side:squad:p{PartyIndex}</c>, D2.11) and the route/route-mask bookkeeping upstream in
    /// `delve-graph-roll`'s own <c>DelveRoomFact.PartyRouteMask</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? PartyIndex { get; init; }

    /// <summary>How many ranks this actor occupies (a wide boss) — null means 1, matching every
    /// existing 1-wide actor exactly. Semantics (span <c>k</c> at rank <c>i</c> occupies
    /// <c>[i, i+k-1]</c>) and the engine read are `delve-battle-profile`'s; `encounter-generator`
    /// writes this only for the `boss` role, from `formation.boss.rankSpan`.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? RankSpan { get; init; }

    /// <summary>
    /// An explicit `Θ_actor` for the contest side, independent of <see cref="Level"/>/<see cref="Index"/>
    /// (spec-difficulty-ladder.md §7's own named wiring gap: "`Index` is a read-only alias of `Level`,
    /// so `delve-battle-profile` and `power-index` hydration decide whether that is `Level` or a new
    /// init-able field" — this is that field). Null means "no override, read `Index`/`Level` as every
    /// existing caller already does" — <see cref="ActorThetaSeam"/>'s own contest math takes a raw
    /// `int actorTheta` parameter today and does not yet read this field; wiring it in is
    /// `delve-battle-profile`'s job, not this shape-only task's.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? ThetaActor { get; init; }

    /// <summary>Non-HP resource pools (stamina, spirit, ...) carried in from a previous room, keyed by
    /// pool name. Null means "no carry-in" — same shape and reasoning as <see cref="CurrentHp"/>, kept
    /// as its own field rather than folded in because HP has its own carry rule (`??MaxHp`) while pools
    /// seed a party-scoped store (D2.11's `FromStored`).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyDictionary<string, long>? CarryInPools { get; init; }

    /// <summary>Boss HP-threshold grants (`{HpThresholdMilli, ContainerInstanceId}`,
    /// spec-delve-battle-profile.md §5/spec-encounter-generator.md §5) — a phase changes stats, never
    /// actions (the ideal's own stated boundary). Null for every non-boss actor and every existing
    /// caller. The threshold check itself (`hp * 1000 < threshold * maxHp`, `long`) and the grant
    /// trigger are `delve-battle-profile`'s engine read; `encounter-generator` only writes the list.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<PhaseGrant>? PhaseGrants { get; init; }

    /// <summary>Instance ids of `enemy.*` containers (`ContainerKind.Enemy`, D2.6) rolled for this
    /// actor's affixes at setup — an elite's roll, or a boss phase's grant. Null for every actor with
    /// no affix roll (every existing caller, and any elite drawn against an empty affix pool, which
    /// degrades to "no affix" rather than inventing one).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string>? GrantedContainerIds { get; init; }
}

/// <summary>One boss phase's HP-threshold grant (spec-delve-battle-profile.md §5): when the boss first
/// satisfies `hp * 1000 &lt; HpThresholdMilli * maxHp` (`long`), the engine grants
/// `ContainerInstanceId`'s effect ids through the same grant block `BindContainers` already uses. A
/// phase **is** this threshold-triggered grant — the `berserker` trait's own precedent (a per-hit read
/// against staged thresholds), never a kit change (a phase changes stats, never actions).</summary>
public sealed record PhaseGrant(long HpThresholdMilli, string ContainerInstanceId);

/// <summary>
/// base-defense `combatant-kind`: does this actor take a turn. Two values, not a richer taxonomy —
/// obstacle vs. building vs. emplacement is content identity and belongs to `structure-seed`; the
/// kernel needs exactly one bit.
/// </summary>
public enum CombatantKind
{
    /// <summary>A creature, a legion member, anything that takes turns. Index 0, so the default is
    /// today's behaviour for every existing caller.</summary>
    Animate,

    /// <summary>A wall, a tower, a barricade. Occupies a cell, can be attacked, never acts on its own
    /// initiative. May still act when garrisoned — see <see cref="BattleActorSetup.GarrisonedBy"/>.</summary>
    Structure
}

/// <summary>
/// Innate shield spec — durations in MILLISECONDS at the content boundary (host-converted:
/// battle ticks are rounds). Null duration = persists until broken.
/// </summary>
public sealed record BattleInnateShield(
    long BaseHp, ElementTypeId? Element = null, int Priority = 10, int? DurationMs = null);

/// <summary>One additive adjustment to a combat derived channel — validated against the generated channel list.</summary>
public sealed record BattleChannelMod(string ChannelId, long Amount);

/// <summary>
/// One status application: id from the locked catalog, signed HP per pulse (negative = DoT,
/// positive = regen; 0 for pure CC), millisecond duration/period on the round clock.
/// </summary>
public sealed record BattleStatusSpec(
    string StatusId, long MagnitudePerPulse, int DurationMs, int PeriodMs = 1000, int GrantChanceMilli = 1000);

/// <summary>Locked engine constants (spec-match-source-core.md). Changing RoundDurationMs or
/// MaxRounds (tunables-ssot.md T1 — gk-core/data/tuning/battle.v1.json) still needs a RulesetVersion bump,
/// same as any other engine-shape change; EngineVersion/RulesetVersion themselves stay structural.</summary>
public static class BattleRuleset
{
    public const int EngineVersion = 1;

    /// <summary>v2 (combat-unification battle-adoption): the SSOT resolver replaced the
    /// per-mille curves; baselines re-expressed in resolver-scale points (owner decision 5).
    /// v3 (T4.2, power-dial, 2026-08-24): power-scale.v2.json's bMilli 0 -> 400 — every magnitude
    /// away from the Theta=20 pin moves; the pin itself and every rate golden must not (PS-3).
    /// v4 (defense-shape, 2026-08-25): combat.defense stops SUBTRACTING and starts DIVIDING
    /// (combat-damage-ssot.md SS6.3, DefenseShape.Divisive). Every mitigated magnitude moves; no
    /// rate does, and PS-3 still does not apply to these hashes. Adopted because the subtractive
    /// shape floors damage at zero once defense outruns offense -- total immunity, the same defect
    /// removed from ampFactor in the same session, measured at 17.1% of LANDED hits dealing
    /// nothing. Divisive approaches zero asymptotically and never reaches it.
    /// v5 (battle-hub-fuse, 2026-09-13): BattleStatComposer is deleted; BattleEngine composes through
    /// BattleHubCompose (ActorHub) exclusively. T5's own pre-delete parity matrix proved Hub ==
    /// composer channel-for-channel (exact on battle channels, narrowing-bounded on funded aptitude
    /// channels, three adopted op-divergences on status.resist.{dot,cc,contagion} capped at 0.95 vs
    /// the old uncapped raw sums) -- this bump is the single, approved (Ask-first table) marker for
    /// that engine-shape change, not a magnitude retune. Any golden whose hash embeds RulesetVersion
    /// moves by construction and is re-blessed once alongside this bump, never per-golden thereafter.</summary>
    public const int RulesetVersion = 5;

    static BattleTuning? _tuning;

    public static void Configure(BattleTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>Phase 7 F3.2 (combat-unification, 2026-09-07): lets an OPTIONAL reader (one that has
    /// a safe, named fallback for "not configured yet" — <see cref="HybridSecondaryWeightMilli"/>'s
    /// own caller in <c>AtomPushService</c> is the first) check before reading, instead of every
    /// caller needing this static configured at all, the way <see cref="RoundDurationMs"/>/
    /// <see cref="MaxRounds"/> correctly still require.</summary>
    public static bool IsConfigured => _tuning is not null;

    static BattleTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "BattleRuleset.Configure(...) has not run. RoundDurationMs/MaxRounds read " +
        "data/tuning/battle.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>Synthetic clock per round — every reused subsystem is millisecond-based.</summary>
    public static int RoundDurationMs => Tuning.RoundDurationMs;
    public static int MaxRounds => Tuning.MaxRounds;

    /// <summary>base-defense F2: the ruleset-wide fallback every profile's `MaxRounds ?? this` and
    /// `RoundDurationMs ?? this` resolve against (see `TimelineProfileTuning`'s own doc comment).
    /// `classic-round` names neither, so it inherits this pair unchanged.</summary>
    public static int LoopGuardRoundMultiple => Tuning.LoopGuardRoundMultiple;

    /// <summary>Wave E3 — the secondary element's per-mille share of an attack payload. 0 (the
    /// shipped default) means the primary carries the whole payload, which is byte-identical to the
    /// behaviour before hybrid payloads existed.</summary>
    public static int HybridSecondaryWeightMilli => Tuning.HybridSecondaryWeightMilli;

    // battle-magnitude (T2.1, spec-battle-magnitude.md): delegate to the Θ ladder. Lazy (`??=`), not
    // an eager static field initializer — FusionRpg.Core.Power.PowerTuningHub.Configure runs during
    // host startup, and an eager field could race it depending on static-init order. Read PowerTuningHub
    // directly rather than adding a second Configure — the data already lives there once, one host call.
    static FusionRpg.Core.Power.PowerLadder? _hpLadder;
    static FusionRpg.Core.Power.ChannelLadder? _atkLadder;
    static FusionRpg.Core.Power.ChannelLadder? _defenseLadder;

    static FusionRpg.Core.Power.ChannelLadder ChannelLadderFor(string channelId)
    {
        var tuning = FusionRpg.Core.Power.PowerTuningHub.Tuning;
        if (!tuning.ChannelsOrEmpty.TryGetValue(channelId, out var channel))
            throw new InvalidOperationException(
                $"BattleRuleset: no '{channelId}' entry in data/tuning/power-scale.v{{n}}.json's channels block " +
                "(spec-battle-magnitude.md §2.1) — there is no built-in default to fall back to.");
        return new FusionRpg.Core.Power.ChannelLadder(tuning.Curve.BMilli, FusionRpg.Core.Power.PowerTuning.FixedPinValue, channel);
    }

    /// <summary>Baseline combat stats from specimen level — integer only. `level` is treated as Θ
    /// directly (T2.1 keeps call sites unchanged — spec-battle-magnitude.md §2.3; a real caller-supplied
    /// Θ arrives in a later wave).</summary>
    public static long BaseHp(int level) =>
        (_hpLadder ??= new FusionRpg.Core.Power.PowerLadder(FusionRpg.Core.Power.PowerTuningHub.Tuning)).Value(level);
    public static long BaseAtk(int level) => (_atkLadder ??= ChannelLadderFor("atk")).Value(level);
    public static long BaseDefense(int level) => (_defenseLadder ??= ChannelLadderFor("defense")).Value(level);

    /// <summary>
    /// action-enrich `action-base` (spec-action-base.md §The read): <c>P(Θ)</c> in whole units, the
    /// magnitude multiplier <see cref="Actions.ActionBaseMath.BasePerHit"/> pairs with a move's base.
    /// A sibling of <see cref="BaseHp"/> reading the SAME cached ladder instance — the `power-scale`
    /// curve, never a second one (ssot-power-scale.md §10 row 20).
    /// </summary>
    public static long PowerValue(int theta) =>
        (_hpLadder ??= new FusionRpg.Core.Power.PowerLadder(FusionRpg.Core.Power.PowerTuningHub.Tuning)).Value(theta);

    /// <summary>
    /// Accuracy-family baselines in RESOLVER-scale points (sigmoid scale 100). Derived from
    /// the owner's rate targets, locked by BattleRateTests:
    ///   parity hit: σ((220+26Θ−26Θ)/100) = σ(2.2) ≈ 0.900   (target 0.90 ± 0.02)
    ///   parity crit: σ((10Θ−(10Θ+250))/100) = σ(−2.5) ≈ 0.076 (target 0.05–0.10)
    ///   +5-index attacker: σ(3.5) ≈ 0.971 hit (target ≥ 0.97), σ(−2.0) ≈ 0.119 crit.
    /// Crit magnitude needs no baseline: delta 0 → ×1.5, the old CritMultBase.
    ///
    /// <para>battle-rates (T2.2, spec-battle-rates.md): a rename, not a formula change — these four
    /// read Θ <b>directly</b> (PS-3: contests read Θ, magnitudes read P(Θ)), never through
    /// <see cref="FusionRpg.Core.Power.PowerLadder"/>/<see cref="FusionRpg.Core.Power.ChannelLadder"/>.
    /// Under B&gt;0 both accuracy and dodge would grow quadratically, and so would their difference —
    /// the only thing the sigmoid sees — turning a fixed one-index gap into a dial-dependent blowout.
    /// <see cref="BaseHp"/>/<see cref="BaseAtk"/>/<see cref="BaseDefense"/> above are the only three
    /// that may touch the ladder.</para>
    /// </summary>
    public static int BaseAccuracy(int theta) => 220 + 26 * theta;
    public static int BaseDodge(int theta) => 26 * theta;
    public static int BaseCritRate(int theta) => 10 * theta;
    public static int BaseCritResist(int theta) => 10 * theta + 250;

    static BattleResourceTuning? _resourceTuning;

    public static void ConfigureResources(BattleResourceTuning tuning) =>
        _resourceTuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static BattleResourceTuning ResourceTuning => _resourceTuning ?? throw new InvalidOperationException(
        "BattleRuleset.ConfigureResources(...) has not run. The pool shares read data/tuning/" +
        BattleResourceTuningFiles.Current + " (spec-battle-resources.md §2.2a) — there is no built-in " +
        "default to fall back to.");

    /// <summary>
    /// `battle-tempo` `battle-resources` — one actor resource pool's maximum, as a per-mille share of
    /// the shipped HP ladder: <c>BaseHp(theta) × poolShareMilli[id] / 1000</c>.
    ///
    /// <para>A PROJECTION of <see cref="BaseHp"/>, never a second curve (spec §2.2, and the same
    /// argument <see cref="SpeciesTempoProjection"/> makes for `turn.speed`): magnitudes read `P(Θ)`
    /// per `ssot-power-scale.md`, and a constant share of an existing scale is that scale read through
    /// a ratio — not a new power-shaped scale, so no §10 inventory row is owed.</para>
    ///
    /// <para><c>hp</c> does not derive here — the caller passes <c>BattleActorSetup.MaxHp</c> through
    /// <paramref name="setupMaxHp"/> so the channel mirrors the actor's real maximum instead of
    /// inventing a second, disagreeing one (spec §2.6).</para>
    ///
    /// <para>⚠️ Overflow discipline: both operands are already <c>long</c> (widen before multiply) and
    /// the single <c>/1000</c> happens last. <c>checked</c> so an absurd share throws rather than
    /// wrapping — PS-8 exempt as an arithmetic guard, not a progression ceiling.</para>
    /// </summary>
    public static long BaseResourceMax(int theta, string resourceId, long setupMaxHp) =>
        BaseResourceMax(theta, resourceId, setupMaxHp, ResourceTuning);

    /// <summary><see cref="BaseResourceMax(int, string, long)"/> against an explicit tuning instance
    /// instead of the configured one — lets a test read a real shipped file without mutating ambient
    /// state shared by parallel test collections.</summary>
    public static long BaseResourceMax(int theta, string resourceId, long setupMaxHp, BattleResourceTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (resourceId == "hp") return setupMaxHp;
        return checked(BaseHp(theta) * tuning.ShareOf(resourceId)) / 1000;
    }

    /// <summary>
    /// `battle-tempo` `battle-resources` — per-tick regen for every Hub compose that seeds the resource
    /// baseline: the lawn, the <c>/sheet</c>, and battle (<c>BattleHubCompose</c>).
    ///
    /// <para><b>Owner decision 2026-09-15 (lawn-combat-wire L-N28): battle stamina regen is ON.</b> T11's
    /// stamina row reaches battle through the shared <c>ResourceBaselineSubsystem</c> on purpose, as
    /// <c>spec-resource-subtick.md</c> said ("regen earns its tuning rows in both battle and the lawn").
    /// <c>resource-hub-ssot.md</c> §11 is amended to match: pools persist and refill at rest, and a resource
    /// with an authored regen row also regenerates during an encounter. The history below is kept
    /// because it explains why the other rows stay 0.</para>
    ///
    /// <para>Originally in-battle regen was <b>0 for every resource</b> as a design position (spec §2.5).</para>
    ///
    /// <para>Two independent reasons were originally given. (1) — <b>resolved by S10.1, 2026-09-13</b>
    /// — the reader used to round the channel to a whole <c>long</c>, so the smallest expressible
    /// non-zero rate was 1/tick, and a round runs several hundred ticks (`action-timing.v1.json`: the
    /// basic attack alone is 150 wind-up + 50 recovery), accruing ~300 poise against
    /// `reaction-lane.v1.json`'s spend of 100 — three counters a round, with nothing representable
    /// between that and "nothing". <see cref="Stats.Derived.ResourceChannelReader.RegenPerMilleTick"/>
    /// now reads the rate in per-mille per tick and <see cref="Actions.Cost.ResourcePoolState"/>
    /// carries the remainder, so 999 rates exist inside that former gap and a rate is no longer
    /// forced to be coarse. (2) was that `resource-hub-ssot.md` §11 said pools "refill <b>at rest</b>" and
    /// a battle is not a rest — superseded for authored rows by the 2026-09-15 decision above (§11
    /// amended); it still argues for keeping a row at 0 until something deliberately authors it.</para>
    ///
    /// <para><b>T11 (lawn-combat-wire, spec-lawn-combat-calibration.md, 2026-09-14) authors the first
    /// non-zero row.</b> `stamina` now regenerates, in battle as well as on the lawn — the other four ids
    /// stay exactly 0, by poise's scarcity argument or because nothing spends them yet (hunger/spirit/qi). The rate
    /// is expressed as <see cref="BattleResourceTuning.RegenShareOf"/> — a per-mille SHARE of the
    /// resource's OWN pool max, regenerated per second — so it projects through <see cref="BaseHp"/>
    /// the same way <see cref="BaseResourceMax"/> already does, rather than forking a second,
    /// theta-independent curve. `hp` carries no share row at all (mirrors <see cref="BaseResourceMax"/>'s
    /// own hp special-case) and always returns 0 here.</para>
    ///
    /// <para>Returns UNITS PER TICK (the channel's own meaning, <see cref="ResourceChannelReader"/>),
    /// as a <c>double</c> — the whole point of `resource-subtick` (S10.1) was making a SUB-tick rate
    /// expressible, so this can no longer be a <c>long</c>: <c>regenPerSecond / TicksPerSecond</c>,
    /// dividing once, at the end, from long arithmetic into the one double this method returns.</para>
    /// </summary>
    public static double BaseResourceRegen(int theta, string resourceId) =>
        BaseResourceRegen(theta, resourceId, ResourceTuning);

    /// <summary><see cref="BaseResourceRegen(int, string)"/> against an explicit tuning instance — see
    /// the matching <see cref="BaseResourceMax(int, string, long, BattleResourceTuning)"/> overload.</summary>
    public static double BaseResourceRegen(int theta, string resourceId, BattleResourceTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (resourceId == "hp") return 0;

        // solid-remediation T5.3/T5.4 (X3): the pool max is NOT recomputed here. This line used to
        // carry its own copy of `checked(BaseHp(theta) * ShareOf(id)) / 1000` — the same expression
        // BaseResourceMax declares — so one mechanism had two owners and regen silently depended on the
        // copy. A share row edited with only one of them in view would have moved a pool's size without
        // moving its regen, or the reverse, and nothing would have reported it.
        //
        // `BaseHp(theta)` is passed as the hp pool because that is exactly what the non-hp branch of
        // BaseResourceMax ignores; every non-hp id derives from BaseHp(theta) there. Regen returns 0 for
        // hp above, so the one id whose max reads `setupMaxHp` never reaches this call.
        var poolMaxUnits = BaseResourceMax(theta, resourceId, BaseHp(theta), tuning);

        // Integer per-mille: widen before multiplying, divide by 1000 once, and only then cross into
        // the double the sub-tick rate needs.
        var regenPerSecondUnits = checked(poolMaxUnits * tuning.RegenShareOf(resourceId)) / 1000;
        return regenPerSecondUnits / (double)TicksPerSecond;
    }

    /// <summary>The kernel's own tick period is 100 ms (`KernelDriveHost`, `EffectRuntime`'s shield
    /// upkeep grid — both cite "the period stays 100 ms" as a substitution, not a redesign). Structural,
    /// not a tunable: it is the sub-tick unit's own denominator, not a balance number a pass would
    /// change — changing it would mean re-deriving every per-mille-per-tick rate in this file.</summary>
    const int TicksPerSecond = 10;

    // The v1 per-mille Hit*/Crit* constants are retired (combat-unification ban test);
    // the SSOT resolver's sigmoid + CombatProbabilityPolicy own hit/crit math now.
}

public sealed record BattleSetup
{
    public IReadOnlyList<BattleActorSetup> Squad { get; init; } = Array.Empty<BattleActorSetup>();
    public IReadOnlyList<BattleActorSetup> Wave { get; init; } = Array.Empty<BattleActorSetup>();
    public string WaveId { get; init; } = "";

    /// <summary>aura-skill T12 (Gate B): commander auras active for this battle. Empty for every
    /// existing caller (no behavior change) — populated once a real caller (T13+) resolves an active
    /// aura's magnitude via `AuraMagnitude.Compute` and hands the FINAL value here. This record owns
    /// delivery only, never the magnitude math (T10's own job, already proven independently) — keeping
    /// the two concerns composable rather than re-deriving Θ/tuning inside the battle resolver.</summary>
    public IReadOnlyList<ActiveCommanderAura> ActiveAuras { get; init; } = Array.Empty<ActiveCommanderAura>();

    /// <summary>species-build-todo.md T4.5, spec-zomboss-adaptive.md's own determinism rule: the
    /// pattern is part of the SETUP, resolved before the battle runs — never rolled during resolution,
    /// or a battle would stop being reproducible from its own `(setup, seed)`. Null for every existing
    /// caller and every non-Zomboss battle (no behavior change) — the wave-building seam
    /// (`WebMatchService.cs`, T4.6) is the only writer.
    ///
    /// <para><see cref="JsonIgnoreAttribute"/> is load-bearing here, not decoration — the exact same
    /// reason <see cref="BattleActorSetup.EquippedActionIds"/>'s own comment warns about: this record
    /// rides into `ExpeditionResolution`'s own serialized+hashed shape
    /// (<c>ExpeditionResolverTests.Tier_goldens_are_locked</c>), and a nullable field with no
    /// suppression serializes as an added `"ZombossPatternId":null` key for every existing caller,
    /// moving that golden for a shape change nobody reviewed as a determinism break.</para></summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ZombossPatternId { get; init; }

    /// <summary>species-build-todo.md T4.6 — which encounter (in this player's own Zomboss-adaptation
    /// sequence) this setup was chosen for, so a REPLAY of a stored setup can still compute the correct
    /// delayed reveal (`RpgStore.GetRevealedZombossPatternId`) without re-invoking the selector. Null
    /// alongside <see cref="ZombossPatternId"/> for every non-Zomboss battle. Same
    /// <see cref="JsonIgnoreAttribute"/> treatment, for the same golden-safety reason.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? ZombossEncounterIndex { get; init; }

    /// <summary>
    /// base-defense `siege-waves` §7: reinforcement batches scheduled for this battle. Empty for every
    /// existing caller — with it empty, the reinforcement event kind is never scheduled, so the event
    /// queue behaves exactly as it does today (a never-scheduled event kind cannot change a tick
    /// sequence, the structural half of this module's byte-identity argument).
    /// </summary>
    public IReadOnlyList<ReinforcementBatch> Reinforcements { get; init; } = Array.Empty<ReinforcementBatch>();
}

/// <summary>
/// base-defense `siege-waves` §1: one reinforcement batch — WHEN it arrives and WHAT arrives.
///
/// <para><b>A tick, not a condition.</b> Audit F8: a state-based trigger ("when the current batch
/// drops below 30%") is turtle-exploitable — a defender who never engages never advances the trigger,
/// so the dominant strategy becomes standing still. A clock cannot be gamed by declining to play.
/// <see cref="World.Turn.TurnEngine"/> is unrelated; this is battle-internal simulation time.</para>
/// </summary>
public sealed record ReinforcementBatch
{
    /// <summary>Simulation tick of arrival, absolute from battle start. `long` — compared against
    /// `maxBattleTick`, which is already `long`; a narrower type here would silently truncate at
    /// exactly the horizon that matters.</summary>
    public long AtTick { get; init; }
    public string Side { get; init; } = "";
    public IReadOnlyList<BattleActorSetup> Actors { get; init; } = Array.Empty<BattleActorSetup>();

    /// <summary>Which board edge they enter from. Ignored in a boardless battle — resolving an edge
    /// into real candidate cells is `siege-resolver`'s job (a later module), the same scoping
    /// `Board/Placement.cs` already states for initial placement.</summary>
    public World.District.BoardEdge Edge { get; init; }
}

/// <summary>aura-skill T12: one commander aura's already-resolved delivery — "an aura is on" made
/// concrete as "this channel gets this value, for every actor on this side." <paramref name="Value"/>
/// is the T10 magnitude (`AuraMagnitude.Compute`'s output), computed by the caller before the battle
/// resolver ever sees it — this record's own job is delivery, not computation.</summary>
public sealed record ActiveCommanderAura(string CommanderSide, string TargetChannel, long Value, string SourceId);

public enum BattleOutcome
{
    Victory,   // wave wiped
    Defeat,    // squad wiped
    Stalemate  // MaxRounds hit
}

/// <summary>
/// One structured battle occurrence — the emitter maps these onto the event vocabulary.
/// Shield events (battle-adoption) carry the optional tail fields; death events may additionally
/// carry the explicit attacker key that caused the lethal interaction.
/// </summary>
public sealed record BattleEventRec(
    int Round, string Kind, string ActorKey, int TypeId, string Side,
    long Amount = 0, string? Element = null, string? ShieldId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? KillerActorKey = null);

public static class BattleEventKinds
{
    public const string Spawn = "spawn";
    public const string Die = "die";
    // Shield vocabulary (battle-adoption) — deliberate expansion; absorbed entries are
    // per-round aggregates from ShieldRuntime.DrainEvents, never per-attack spam.
    public const string ShieldGranted = "shield.granted";
    public const string ShieldAbsorbed = "shield.absorbed";
    public const string ShieldBroken = "shield.broken";
    public const string ShieldExpired = "shield.expired";
}

/// <summary>Per-actor tallies. XpMilli is a per-mille XP MULTIPLIER (base 1000; genius raises
/// it), not an XP amount — consumers scale their own rates by it. ShieldAbsorbed = damage the
/// actor's own shields ate (battle-adoption).</summary>
public sealed record BattleActorResult(
    string Key, string Side, string SpeciesId, int TypeId,
    long HpRemaining, long DamageDealt, int Kills, bool Survived,
    bool Retreated, int XpMilli, long ShieldAbsorbed = 0)
{
    /// <summary>
    /// T22 (action-todo.md): "the auto-equipped set appears in the battle report — otherwise a
    /// dominant auto-loadout is invisible to a matrix that compares allocations, not loadouts."
    /// Carried straight from <see cref="BattleActorSetup.EquippedActionIds"/> — pure observability,
    /// read by nothing in the round loop, damage math, targeting, or trait tail (confirmed by search,
    /// not assumed). <c>null</c>, never <see cref="Array.Empty{T}"/>, when the caller never resolved
    /// one: matches <see cref="BattleReport.ContentHash"/>'s own pattern one property up, and for the
    /// same reason — the field must serialize as ABSENT, not as an empty array, or every existing
    /// golden actor gains a `"EquippedActionIds":[]` and all four hashes move for a reason that is not
    /// a determinism break.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string>? EquippedActionIds { get; init; }

    /// <summary>Carried straight from <see cref="BattleActorSetup.PartyIndex"/> — same reasoning as
    /// <see cref="EquippedActionIds"/> above: an init-only tail property (never positional, this
    /// record's 11-parameter primary constructor is otherwise untouched), <c>null</c> for every
    /// existing caller, so all four battle hashes/the 32-seed sweep/all four expedition hashes stay
    /// byte-identical.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? PartyIndex { get; init; }

    /// <summary>
    /// D2.14 — <c>Delve.Battle.DelveCarryOut? { Statuses, Shield, Retreated }</c>
    /// (spec-delve-battle-profile.md §5's golden-argument table). Same tail-property/
    /// `WhenWritingDefault` treatment as <see cref="PartyIndex"/> immediately above, for the
    /// identical reason: null for every existing caller, so no hash moves.
    ///
    /// <para><b>Population is deliberately NOT wired here yet.</b> Building a real
    /// <c>DelveCarryOut</c> at battle end needs converting the actor's live
    /// <c>StatusInstance</c>s (<c>StatusRuntime.ForHost</c>) back into declarative
    /// <see cref="BattleStatusSpec"/>s — a genuinely new conversion this task's own file list
    /// (`DelveCarry.cs`, `DelveDecision.cs` only, no `BattleEngine.cs`) does not include. The type
    /// exists and is fully wired for JSON safety; a later task assigns it a real, non-null value
    /// for a `PartyIndex`-carrying actor.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public FusionRpg.Core.Delve.Battle.DelveCarryOut? CarryOut { get; init; }

    /// <summary>
    /// party-dungeon D2.21: true when this actor's `ActorState.WentDowned` was ever set this battle —
    /// the timeline FSM's `TurnState.Downed` transition, gated on `DownedOnDeplete` and a non-null
    /// `PartyIndex` (spec-delve-attrition.md §6). Sticky for the whole battle, so a mid-battle revive
    /// does not erase the signal.
    ///
    /// <para>Same tail-property/`WhenWritingDefault` treatment as <see cref="PartyIndex"/>/
    /// <see cref="CarryOut"/> above: `false` (the default) for every existing caller — nothing except
    /// the `delve` profile ever sets `ActorState.WentDowned` — so this field never serializes for any
    /// shipped battle and all four hashes/the 32-seed sweep/all four expedition hashes stay
    /// byte-identical. A later task (party state / extraction settlement) reads this once per room to
    /// fold into `DelveMemberState.DownedOnce`, which persists across rooms; this field itself does
    /// not — it is this ONE room's own report.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WentDowned { get; init; }
}

/// <summary>
/// Process environment fingerprint for the report's platform stamp — the coordinates that
/// actually move <c>Math.Exp</c>'s last ULP, and nothing else.
///
/// OS is part of the identity: on CoreCLR x64 there is no hardware `exp`, so Math.Exp calls
/// the platform libm (ucrtbase / glibc / Apple libm) and those differ. Architecture alone
/// would give Windows-x64 and Linux-x64 the same stamp — a collision on exactly the case the
/// guard exists to catch.
///
/// The runtime MAJOR version only: a servicing update (8.0.11 → 8.0.30) rebinds nothing in
/// this path, so including the patch number would strand every logged match behind a routine
/// `dotnet` upgrade. Keep this stable — widening it invalidates stamps already in the DB.
/// </summary>
public static class BattleEnvironment
{
    public static readonly string Stamp = string.Join("/",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        OsToken(),
        "net" + Environment.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture));

    static string OsToken()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "macos";
        return "other";
    }
}

public sealed record BattleReport
{
    public int EngineVersion { get; init; } = BattleRuleset.EngineVersion;
    public int RngAlgoVersion { get; init; } = SeededRng.RngAlgoVersion;
    public int RulesetVersion { get; init; } = BattleRuleset.RulesetVersion;

    /// <summary>
    /// Platform stamp (owner decision 7): Math.Exp is not bit-identical across architectures,
    /// so replay/sweep guards refuse cross-platform re-resolution like a version mismatch.
    /// </summary>
    public string EnvironmentStamp { get; init; } = BattleEnvironment.Stamp;

    /// <summary>
    /// Which content produced this report (E12, deferred here from E8).
    ///
    /// <para><b>Carried, and deliberately outside the determinism hash</b> — the same treatment the
    /// platform stamp gets, for the same reason. It is provenance, not battle math. Fold it into the
    /// hash input and every content edit anywhere — a new item, a seventh element, a re-priced
    /// coefficient — moves every battle golden, and a real determinism break becomes indistinguishable
    /// from someone adding a row. That is the machine-dependence problem in a different coat.</para>
    ///
    /// <para><b>Null</b> when the host has no catalog: battle does not require one, and inventing a
    /// hash for content that was not consulted would be a claim rather than a record. Null and not
    /// empty-string, because only null is a string's default — and the omission below keys on that.</para>
    ///
    /// <para><b>Omitted from the JSON when empty</b>, which is what let this land without re-blessing
    /// a single golden. Blanking the value was not enough — the property NAME alone moved all four
    /// hashes. Absent-when-empty means the determinism view (which blanks it) serializes to exactly
    /// the bytes it did before E12 existed.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? ContentHash { get; init; }

    public ulong Seed { get; init; }
    public string WaveId { get; init; } = "";
    public BattleOutcome Outcome { get; init; }
    public int Rounds { get; init; }

    /// <summary>species-build-todo.md T4.5/T4.6 — carried straight from <see cref="BattleSetup.ZombossPatternId"/>,
    /// same "omitted when default" treatment as <see cref="ContentHash"/> one property up and for the
    /// same reason: a non-Zomboss battle must serialize byte-identically to before this field existed,
    /// or every existing golden moves for a reason that is not a determinism break. The DELAYED reveal
    /// (spec's own decision 4 — "after the next fight," per `revealDelayEncounters`) is the server
    /// seam's own job (T4.6): this field is the raw, undelayed pattern the battle actually resolved
    /// with, not what a player is shown.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? ZombossPatternId { get; init; }

    /// <summary>Battle-level Souls multiplier (‰, base 1000) — greedy squad survivors raise it.</summary>
    public int SoulLootMilli { get; init; } = 1000;
    public IReadOnlyList<BattleEventRec> Events { get; init; } = Array.Empty<BattleEventRec>();
    public IReadOnlyList<BattleActorResult> Actors { get; init; } = Array.Empty<BattleActorResult>();

    /// <summary>
    /// aura-skill T3 (audit D3): named content dropped at resolve time rather than thrown — today,
    /// an actor whose `EquippedActionIds` cannot be resolved (no `ActionCatalog` supplied, or an id
    /// the catalog doesn't have) degrades to the single basic-attack fallback instead of failing the
    /// whole battle, and the dropped ids are recorded here.
    ///
    /// <para><b>Provenance, not battle math</b> — same treatment as <see cref="ContentHash"/> and
    /// <see cref="EnvironmentStamp"/>: null by default, omitted from JSON when empty, and blanked in
    /// the golden hash (`BattleGoldenTests.Hash`). Every setup blessed by an existing golden has a
    /// resolvable loadout (or none), so this is null for every one of them today — no golden moves by
    /// this field existing.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string>? Warnings { get; init; }
}
