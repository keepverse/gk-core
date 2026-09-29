using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>Which room kind's shape an encounter fills (spec-dungeon-seed-contract.md §1.2 — the
/// room kind fixes the formation: `fight`/`wild` → `pack`, `elite` → `party`, `boss` → `boss`).</summary>
public enum Formation { Pack, Party, Boss }

/// <summary>The anchor's own `elementSpread` band — `mono`/`dual`/`rainbow` (spec-encounter-generator.md
/// §2 step 1).</summary>
public enum ElementSpreadMode { Mono, Dual, Rainbow }

/// <summary>
/// The encounter anchor (spec-dungeon-seed-contract.md §1.6) as a plain input parameter to
/// <see cref="Encounter.Build"/> — this module reads it, it does not parse `encounters/&lt;id&gt;.json`
/// from disk (that reader belongs to whichever module loads domain content, not yet built). Tests
/// construct this directly, the same way existing tests hand-build a <see cref="BattleSetup"/> without
/// a JSON reader.
/// </summary>
/// <param name="RankOrder">A permutation of indices into <see cref="Slots"/> — emit order, front to
/// back (spec §4). Not the reach-mask/target-preference default pick, which is `RankOrder.cs`'s own
/// separate, runtime read-model concern (D2.3) despite the similar name.</param>
/// <param name="BossKit">D2.4 — the anchor's own `boss{build, phasing, phaseTrigger, signatureAction,
/// retinue}` sub-shape. Null for every non-boss formation; required for `Formation.Boss` (checked at
/// build time, not by the type system, matching <see cref="BossSpeciesRef"/>'s own null-checked-at-use
/// treatment).</param>
public sealed record EncounterAnchor(
    Formation Formation, IReadOnlyList<EncounterSlot> Slots, IReadOnlyList<int> RankOrder,
    ElementSpreadMode ElementSpread, ThreatWindow ThreatWindow, string? BossSpeciesRef,
    BossKit? BossKit = null);

/// <summary>The `breakpoint`/`escalating` phase bands (spec §5) — `None` is a real, legal third case:
/// most boss rooms never phase at all.</summary>
public enum BossPhaseKind { None, Breakpoint, Escalating }

/// <summary>
/// D2.4's own slice of the encounter anchor's `boss` sub-object. <see cref="RetinueSlotIndex"/> is an
/// index into the SAME <see cref="EncounterAnchor.Slots"/> list every other slot lives in — the
/// retinue is "one more §2 slot" (spec §5), never a second vocabulary.
/// </summary>
public sealed record BossKit(
    string PatternId, string SignatureAction, BossPhaseKind PhaseKind, int RetinueSlotIndex);

/// <summary>§8's own coverage metric shape — `(postureMultiset, elementSpread, formation)`, never
/// entries-per-cell (ideal §11.4). <see cref="EncounterCoverage"/> (D2.7) is the closed-loop counter
/// over many of these; this module only produces one per build.</summary>
public sealed record EncounterCell(IReadOnlyList<Posture> PostureMultiset, IReadOnlySet<ElementTypeId> ElementSpread, Formation Formation);

/// <summary><see cref="W"/> is returned, never serialized onto <c>BattleSetup</c> — a `W` field there
/// would move all four expedition hashes (`WaveCatalog.cs:27-30`); the host applies it as
/// <c>profile with { W = w }</c>.</summary>
public sealed record EncounterHalf(IReadOnlyList<BattleActorSetup> Enemies, int W, IReadOnlyList<string> Warnings, EncounterCell Cell);

/// <summary>
/// `encounter-generator` D2.2 (spec-encounter-generator.md §1-4) — turns an encounter anchor plus the
/// room's Θ into the enemy half of a `BattleSetup`.
///
/// <para><b>Scope, stated plainly:</b> this build covers §2 steps 1-4 (spread set, filter, count,
/// draw) for every slot, including a boss room's OWN slots, plus a MINIMAL boss-species resolution
/// (direct `bossSpeciesRef` lookup, rung-floor check). It does NOT build the boss's kit, phases,
/// retinue, `W` override or shield pool (D2.4/D2.5's own acceptance lines, `BossBuild.cs`/
/// `BossShieldPool.cs`) or the elite affix roll (D2.6, `EliteAffix.cs`), or synergy adjacency
/// reordering (§2 step 5 — no task in this wave names a file for it yet). A boss-formation
/// `EncounterHalf` here carries the boss's own base setup and its retinue's base setups, un-kitted —
/// later tasks enrich the SAME setups, never replace them.</para>
///
/// <para><b>Stream root, resolved against the real signature, not the prose.</b> §1's own Inputs
/// paragraph mentions `(row, col)` alongside the seed, and §2's root name is written
/// `dungeon:encounter:{r}:{c}` — but the section's OWN stated signature,
/// `Build(anchor, roomTheta, climate, raid, rung, seed, corpus, tuning)`, carries no `row`/`col`
/// parameter at all. Resolved in favour of the signature: `seed` is trusted to already be
/// room-specific (the caller's own `SeededRng.DeriveStream(dungeonSeed, "room:{r}:{c}")` derivation,
/// `delve-graph-roll`'s own convention), so this module's root stream name is the fixed literal
/// `"dungeon:encounter"` — uniqueness comes from the seed, not a name this function cannot see.</para>
/// </summary>
public static class Encounter
{
    static readonly IReadOnlySet<ElementTypeId> AllElements = Enum.GetValues<ElementTypeId>().ToHashSet();
    const string StreamRoot = "dungeon:encounter";

    /// <param name="aptitudeTuning">D2.4 — required only when <paramref name="anchor"/>'s
    /// <see cref="EncounterAnchor.BossKit"/> is set (a boss whose kit should actually be resolved);
    /// optional and unread otherwise, so every D2.1-D2.3 caller/test keeps compiling and behaving
    /// identically without ever naming it.</param>
    /// <param name="powerTuning">Same conditional-requirement as <paramref name="aptitudeTuning"/>.</param>
    public static EncounterHalf Build(
        EncounterAnchor anchor, int roomTheta, ElementTypeId? climate, RaidModeTuning raid,
        DifficultyRungTuning rung, ulong seed, IReadOnlyList<ConcreteAnchor> corpus,
        EncounterTuning tuning, CreatureThreatTuning threatTuning,
        AptitudeTuning? aptitudeTuning = null, PowerTuning? powerTuning = null)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));
        if (raid is null) throw new ArgumentNullException(nameof(raid));
        if (rung is null) throw new ArgumentNullException(nameof(rung));
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (threatTuning is null) throw new ArgumentNullException(nameof(threatTuning));
        if (anchor.Formation == Formation.Boss && anchor.BossKit is not null && (aptitudeTuning is null || powerTuning is null))
            throw new ArgumentException(
                "anchor.BossKit is set, so resolving this boss's kit needs both aptitudeTuning and powerTuning.");

        var (spreadSet, offClimateMilli) = ResolveSpread(anchor.ElementSpread, climate, tuning, seed, $"{StreamRoot}:spread");
        var warnings = new List<string>();

        var w = anchor.Formation == Formation.Boss
            ? checked(raid.BossW + rung.BossWDelta)
            : anchor.Formation == Formation.Pack ? tuning.FormationPackW : tuning.FormationPartyW;

        var enemies = new List<BattleActorSetup>();
        var emittedFrom = new List<ConcreteAnchor>(); // parallel to `enemies` -- the source row behind each emitted setup, for Cell's posture multiset below
        var n = 0;

        void EmitAndTrack(ConcreteAnchor a, int? rankSpan = null)
        {
            var setup = Emit(a, roomTheta, threatTuning, n++);
            if (rankSpan is { } span) setup = RankOrder.WithRankSpan(setup, span);
            enemies.Add(setup);
            emittedFrom.Add(a);
        }

        if (anchor.Formation == Formation.Boss)
        {
            var boss = ResolveBoss(anchor, corpus, tuning, threatTuning);
            // §4: "Written only for the boss role, from formation.boss.rankSpan" -- every other
            // emitted setup below keeps RankSpan null (span 1), matching D2.3's own scope exactly.
            EmitAndTrack(boss, rankSpan: tuning.FormationBossRankSpan);

            if (anchor.BossKit is { } kit)
            {
                // D2.4: pattern -> allocation -> Hub inputs, plus the one signature action from
                // round 1. battle-hub-fuse T6: the kit reaches battle as inputs only — ApplyKit no
                // longer takes a ChannelMods parameter at all. Re-writes the just-emitted boss setup
                // in place (it is always index 0, EmitAndTrack having just added it above).
                var allocation = BossBuild.ResolveKitAllocation(kit.PatternId, enemies[0].Level, aptitudeTuning!);
                enemies[0] = BossBuild.ApplyKit(enemies[0], kit.SignatureAction)
                    with
                    {
                        HubInputs = (enemies[0].HubInputs ?? new BattleHubInputs()) with { Aptitude = allocation }
                    };
            }

            // §5 "Retinue... is one more §2 slot" -- count-only, stats at Θ_room like every other
            // slot; the retinue slot itself is `anchor.Slots` in rankOrder, same as pack/party, and
            // is handled inside the SAME loop below (its own delta formula, same emission ordering).
        }

        foreach (var slotIndex in anchor.RankOrder)
        {
            if (slotIndex < 0 || slotIndex >= anchor.Slots.Count)
                throw new ArgumentOutOfRangeException(nameof(anchor), $"rankOrder names slot index {slotIndex}, but there are only {anchor.Slots.Count} slots.");
            var slot = anchor.Slots[slotIndex];

            var band = tuning.SlotCountBand.TryGetValue(slot.CountBand, out var b)
                ? b
                : throw new EncounterRefusal(slot, $"countBand '{slot.CountBand}' has no entry in encounter.v1.json's slot.countBand");

            IReadOnlyList<ConcreteAnchor> drawn;
            if (anchor.BossKit is { } kitForRetinue && slotIndex == kitForRetinue.RetinueSlotIndex)
            {
                // Multiplicative in party count, not the flat rung delta every other slot reads --
                // BossBuild.DrawRetinue's own formula, §5's "n = NextInt(countBand) + bossRetinuePerPartyDelta * (parties-1)".
                drawn = BossBuild.DrawRetinue(corpus, slot, band, anchor.ThreatWindow, climate, offClimateMilli,
                    tuning.PackSameSpeciesMaxMilli, rung.BossRetinuePerPartyDelta, raid.Parties, seed,
                    $"{StreamRoot}:boss:retinue", spreadSet);
            }
            else
            {
                var candidates = SlotFilter.Candidates(corpus, slot, anchor.ThreatWindow, spreadSet);
                var delta = anchor.Formation == Formation.Party ? rung.EnemyCountDeltaElite : rung.EnemyCountDeltaFight;
                var countStream = SeededRng.DeriveStream(seed, $"{StreamRoot}:slot:{slotIndex}:count");
                var count = SlotFill.Count(countStream, band, delta);
                if (count < 1)
                    throw new EncounterRefusal(slot, $"count {count} < 1 after the rung delta ({delta:+0;-#})");

                try
                {
                    drawn = SlotFill.Draw(candidates, count, climate, offClimateMilli, tuning.PackSameSpeciesMaxMilli,
                        seed, $"{StreamRoot}:slot:{slotIndex}");
                }
                catch (EncounterFillExhausted ex)
                {
                    throw new EncounterRefusal(slot, ex.Message);
                }
            }

            foreach (var a in drawn)
                EmitAndTrack(a);
        }

        var postures = emittedFrom.Select(a => SlotFilter.PostureOf(a.AptitudePrimary)).ToList();
        var cell = new EncounterCell(postures, spreadSet, anchor.Formation);
        return new EncounterHalf(enemies, w, warnings, cell);
    }

    /// <summary>θ_enemy = Θ_room + thetaOffset(species) — the sum nothing computes today
    /// (spec §3). Every other field is a plain pass-through, matching `WaveCatalog.Enemies`'s own
    /// established shape exactly.</summary>
    static BattleActorSetup Emit(ConcreteAnchor a, int roomTheta, CreatureThreatTuning threatTuning, int n)
    {
        var theta = checked(roomTheta + threatTuning.OffsetFor(a.ThreatBand));
        return new BattleActorSetup
        {
            Key = $"wave:{n}", Side = "wave", SpeciesId = a.SpeciesId, TypeId = a.CreatureTypeId, Level = theta,
            ElementPrimary = a.ElementPrimary, ElementSecondary = a.ElementSecondary, TraitIds = a.TraitPool,
            MaxHp = BattleRuleset.BaseHp(theta), Atk = BattleRuleset.BaseAtk(theta),
            Defense = BattleRuleset.BaseDefense(theta), AttackIntervalMs = a.AttackIntervalMs,
        };
    }

    static ConcreteAnchor ResolveBoss(EncounterAnchor anchor, IReadOnlyList<ConcreteAnchor> corpus, EncounterTuning tuning, CreatureThreatTuning threatTuning)
    {
        if (anchor.BossSpeciesRef is not { } bossRef)
            throw new InvalidOperationException("a boss-formation encounter anchor must carry a bossSpeciesRef.");
        var boss = corpus.FirstOrDefault(a => string.Equals(a.SpeciesId, bossRef, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"bossSpeciesRef '{bossRef}' is not in the corpus.");
        if (boss.ThreatRung is not { } bossRung)
            throw new InvalidOperationException($"boss '{bossRef}' has no threatBand — never the rung-4 default.");

        var floorRung = threatTuning.Thresholds.FirstOrDefault(t => string.Equals(t.Id, tuning.ThreatWindowBossFloorRung, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"threatWindow.bossFloorRung '{tuning.ThreatWindowBossFloorRung}' has no rung in creature-threat.v1.json.");
        if (bossRung < floorRung.Rung)
            throw new InvalidOperationException(
                $"boss '{bossRef}' is rung {bossRung} ('{boss.ThreatBand}'), below the boss floor rung {floorRung.Rung} ('{floorRung.Id}').");

        return boss;
    }

    static (IReadOnlySet<ElementTypeId> Spread, long OffClimateMilli) ResolveSpread(
        ElementSpreadMode mode, ElementTypeId? climate, EncounterTuning tuning, ulong seed, string streamName)
    {
        // "under climate = none all six with no off-climate weight" (§2 step 1) -- overrides
        // mono/dual, which are meaningless with nothing to be mono/dual RELATIVE TO. A uniform
        // positive weight (any single value works, SlotFill.Draw only compares relative weights)
        // implements "no off-climate weight" as "no weight penalty", not "weight zero".
        if (climate is not { } stated)
            return (AllElements, 1000);

        return mode switch
        {
            ElementSpreadMode.Mono => (new HashSet<ElementTypeId> { stated }, tuning.SpreadOffClimateMilli["mono"]),
            ElementSpreadMode.Dual => (DrawDualSet(stated, seed, streamName), tuning.SpreadOffClimateMilli["dual"]),
            ElementSpreadMode.Rainbow => (AllElements, tuning.SpreadOffClimateMilli["rainbow"]),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    static IReadOnlySet<ElementTypeId> DrawDualSet(ElementTypeId climate, ulong seed, string streamName)
    {
        var others = Enum.GetValues<ElementTypeId>().Where(e => e != climate).ToArray(); // 5 remaining, stable declaration order
        var stream = SeededRng.DeriveStream(seed, streamName);
        var picked = others[stream.NextInt(others.Length)];
        return new HashSet<ElementTypeId> { climate, picked };
    }
}
