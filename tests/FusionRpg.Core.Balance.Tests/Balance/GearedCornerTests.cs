using FusionRpg.Core.Balance.Guards;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Workspace;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>
/// item-todo.md P1.5 / item-plan.md Checkpoint 1 — <b>the first geared corner run</b>.
///
/// <para><b>What was actually missing, and what was not.</b> The deferral this closes reasoned that a
/// geared corner run needed "a new <see cref="IActorStatSubsystem"/> reading a specimen's real
/// bindings, wired into <c>ActorHubBootstrap</c> the way <c>AptitudeSubsystem</c> already is." Read
/// against the code, that subsystem already exists and already ships:
/// <see cref="AtomDerivedSubsystem"/>, registered at its reserved order-350 slot through
/// <c>CreateDefault</c>'s <c>boundDerivedAtoms</c> arm since 2026-08-30 (decisions.md, "Derived-write
/// lawn executor"). Equipment is the same <c>stat.derived</c> kind that subsystem was built for, and
/// its own doc comment names "a second delivery path for a value the composer already owns" as the
/// thing to avoid — so a SECOND subsystem would have been the defect, not the fix. What was genuinely
/// absent is two joints: a projection from equipped atoms to
/// <see cref="BoundDerivedAtom"/> (<see cref="EquipAtomSource.DerivedAtomsFor"/>), and a way for the
/// balance guards to pass one in.</para>
///
/// <para><b>`class-system-map.md` §2a.0 no longer forbids anything here.</b> It used to say
/// <c>BattleStatComposer</c> ran no subsystems and that aptitudes reached battle via `ChannelMods`
/// instead — "the composers stay separate". battle-hub-fuse (T6) deleted `BattleStatComposer`; both
/// seams are `ActorHub` now, just with different registered subsystems (battle-ops-parity T7 closed
/// the remaining op-honoring gap between them).</para>
///
/// <para><b>Why the non-regression tests below are the important half.</b> <see cref="ActorHub"/> is
/// the shared derived-stat SSOT (`actor-hub-ssot.md`), not class-system's private property, and
/// <see cref="TerminationGuard"/>/<see cref="DominanceGuard"/> back class-system's own extensively
/// tested Checkpoint 8. Every change here is additive by construction.</para>
/// </summary>
[Collection("AptitudeTuningHub")]
public class GearedCornerTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The REAL shipped atom, transcribed from <c>gk-data/packs/fusion/data/seed/atoms/trait-critical-hunter.json</c>
    /// — the only concrete <c>stat.derived</c> row the corpus carries today, and the calibration anchor
    /// <c>bands.v1.json</c>'s own <c>sigmoidDerivedChannel</c> group is scaled against. Read off disk by
    /// <see cref="TheShippedCorpus_stillCarriesTheAtomThisSuitePinsAsRepresentative"/> so this fixture
    /// cannot silently drift from the file.</summary>
    const string ShippedAtomId = "atom.critical-hunter.t1";
    const long ShippedAmount = 150;
    static string ShippedChannel => DerivedStatChannels.CombatCritRateOmni;

    static AtomRow Atom(string channel, string op, long amount, string family = "atom.equip-probe") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1), KindId = "stat.derived",
        FamilyId = family, Variant = "", Tier = 1, Name = family,
        ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"{op}\",\"amount\":{amount}}}",
    };

    static EquipAtomSource EquipWith(params AtomRow[] rows) =>
        EquipAtomSource.FromResolver(specimenId => specimenId == "s1" ? rows : Array.Empty<AtomRow>());

    static ActorDerivedSnapshot ResolveHub(IReadOnlyList<BoundDerivedAtom>? gear)
    {
        var hub = ActorHubBootstrap.CreateDefault(
            boundDerivedAtoms: gear is null || gear.Count == 0 ? null : _ => gear);
        return hub.ResolveDerived(hub.Stats.Contexts.ForPlant("probe", new EntityBaseline()));
    }

    static AptitudeAllocation Corner(string aptitudeId) =>
        AptitudeAllocation.Single(AllocationScope.Commander, aptitudeId, 100);

    static AptitudeAllocation[] TwoCorners() => new[] { Corner("Might"), Corner("Fortitude") };

    /// <summary>The real roster and the real spike/floor corner shape <c>gk-forge/tools/DominanceBaseline</c>
    /// and <c>DominanceGuardTests</c> both use, kept identical here rather than re-derived — the
    /// geared run's actual subject. <c>floor</c> is <c>BestResponse.DominanceMatrix</c>'s own fixed
    /// corner-shape constant (100/12/2, per-mille), not a balance dial.</summary>
    static readonly string[] Roster =
    {
        "Might", "Fortitude", "Vigor", "Onslaught", "Agility", "Composure",
        "Pierce", "Focus", "Bulwark", "Retribution", "Precision", "Ferocity",
    };

    const long CornerFloor = 4167;

    static AptitudeAllocation RealCorner(string spikeId) =>
        Roster.Aggregate(AptitudeAllocation.Empty, (acc, id) => acc + AptitudeAllocation.Single(
            AllocationScope.Commander, id,
            id == spikeId ? 100_000 - CornerFloor * (Roster.Length - 1) : CornerFloor));

    static AptitudeAllocation[] RealCorners() => Roster.Select(RealCorner).ToArray();

    // ── (a) the non-regression proof: no equipment changes nothing, by construction ────────────────

    [Fact]
    public void NoGear_registersNoEquipmentSubsystemAtAll_notMerelyAnInertOne()
    {
        // The strongest form of "existing callers are unaffected": the hub is not composed to the same
        // numbers, it is the SAME hub. Arithmetic equality would still leave an extra entry in
        // ActorHub.Subsystems, which ActorHubTests asserts on and which any future consumer counting
        // subsystems would see.
        var bare = ActorHubBootstrap.CreateDefault();
        var nullGear = ActorHubBootstrap.CreateDefault(boundDerivedAtoms: null);

        Assert.DoesNotContain(bare.Subsystems, s => s.SubsystemId == "atom.derived");
        Assert.DoesNotContain(nullGear.Subsystems, s => s.SubsystemId == "atom.derived");
        Assert.Equal(bare.Subsystems.Count, nullGear.Subsystems.Count);
    }

    [Fact]
    public void NoGear_producesTheIdenticalDerivedSnapshot_asBeforeTheGearArmExisted()
    {
        var bare = ResolveHub(null);
        var emptyGear = ResolveHub(Array.Empty<BoundDerivedAtom>());

        // Every channel, not a sampled one -- a snapshot that agreed on the channels a test happened
        // to name while diverging elsewhere is exactly the regression this is meant to exclude.
        var bareAll = bare.Channels.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray();
        var emptyAll = emptyGear.Channels.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray();

        Assert.Equal(bareAll.Length, emptyAll.Length);
        for (var i = 0; i < bareAll.Length; i++)
        {
            Assert.Equal(bareAll[i].Key, emptyAll[i].Key);
            Assert.Equal(bareAll[i].Value, emptyAll[i].Value);
        }
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void NoGear_leavesBothGuardsExactlyWhereTheyWere()
    {
        DominanceGuardTestSupport.ConfigureShippedTuning();
        var builds = TwoCorners();

        var before = DominanceGuard.Measure(builds, 100);
        var withNullGear = DominanceGuard.Measure(builds, 100, gear: null);
        var withEmptyGear = DominanceGuard.Measure(builds, 100,
            gear: new IReadOnlyList<BoundDerivedAtom>[] { Array.Empty<BoundDerivedAtom>(), Array.Empty<BoundDerivedAtom>() });

        Assert.Equal(before.Matrix.Count, withNullGear.Matrix.Count);
        for (var i = 0; i < before.Matrix.Count; i++)
        {
            Assert.Equal(before.Matrix[i].WinShareAttacker, withNullGear.Matrix[i].WinShareAttacker);
            Assert.Equal(before.Matrix[i].WinShareAttacker, withEmptyGear.Matrix[i].WinShareAttacker);
        }

        var t1 = TerminationGuard.Assert(builds, 100);
        var t2 = TerminationGuard.Assert(builds, 100, gear: null);
        Assert.Equal(t1, t2);
    }

    // ── (b) the cross-check: battle Hub folds exactly what the sheet/overlay Hub computes ───────────

    static BattleActorSetup BattleSetupWith(IReadOnlyList<BoundDerivedAtom>? gear) => new()
    {
        Key = "s1", Side = "squad", SpeciesId = "spec", TypeId = 1, Level = 5,
        SpecimenId = "s1", MaxHp = 100, Atk = 50, Defense = 20,
        HubInputs = gear is null ? null : new BattleHubInputs { BoundAtoms = gear },
    };

    [Fact]
    public void AnEquippedAtom_foldsIntoTheDerivedSnapshot_theSameWayOnBattleAndOverlayHub()
    {
        // battle-ops-parity T7: battle-hub-fuse (T6) already made battle an ActorHub compose; this
        // proves the SAME equipped-atom projection (DerivedAtomsFor -- the one parse EquipAtomSource
        // carries now that ModsFor/BattleChannelMod is deleted) reaches an identical total on both the
        // overlay/sheet Hub (ResolveHub, ActorHubBootstrap) and the battle Hub (BattleHubCompose).
        var equip = EquipWith(Atom(ShippedChannel, "flat", ShippedAmount));
        var hubAtoms = equip.DerivedAtomsFor("s1");

        Assert.Single(hubAtoms);
        Assert.Equal(DerivedModifierOp.Flat, hubAtoms[0].Op);

        var bareOverlay = ResolveHub(null);
        var gearedOverlay = ResolveHub(hubAtoms);
        var bareBattle = BattleHubCompose.Compose(BattleSetupWith(null));
        var gearedBattle = BattleHubCompose.Compose(BattleSetupWith(hubAtoms));

        Assert.Equal(bareOverlay.Get(ShippedChannel) + ShippedAmount, gearedOverlay.Get(ShippedChannel));
        Assert.Equal(bareBattle.Get(ShippedChannel) + ShippedAmount, gearedBattle.Get(ShippedChannel));
    }

    [Fact]
    public void AnUnequippedSpecimen_contributesNothing_onBothSides()
    {
        var equip = EquipWith(Atom(ShippedChannel, "flat", 999));
        Assert.Empty(equip.DerivedAtomsFor("someone-else"));
    }

    [Fact]
    public void TheDerivedOpIsHonoured_onBattleHubToo_notJustOverlay()
    {
        // battle-ops-parity T7 closed the gap this test used to pin as permanent: before the fuse,
        // BattleStatComposer folded every equip mod additively, so an `increased` atom applied as
        // `flat` in battle. Battle now composes through BattleHubCompose -> HubInputs.BoundAtoms, the
        // same op-aware DerivedAtomsFor projection the overlay side always used -- both sides honour
        // `increased` identically.
        //
        // Channel choice matters: `ShippedChannel` (combat.crit.rate.omni) is a FlatSum channel, whose
        // own compose only sums Flat-op mods (DerivedComposer.ComposeChannel) -- an Increased-op atom
        // on it is correctly dropped, not a battle-side bug. `StatusPowerOmni` is registered
        // SumIncreased (DefaultValue 0), so an Increased contribution is the one this op can actually
        // move on either engine.
        var channel = DerivedStatChannels.StatusPowerOmni;
        var equip = EquipWith(Atom(channel, "increased", 40));
        var hubAtoms = equip.DerivedAtomsFor("s1");

        Assert.Equal(DerivedModifierOp.Increased, Assert.Single(hubAtoms).Op);

        var bareBattle = BattleHubCompose.Compose(BattleSetupWith(null)).Get(channel);
        var gearedBattle = BattleHubCompose.Compose(BattleSetupWith(hubAtoms)).Get(channel);
        var bareOverlay = ResolveHub(null).Get(channel);
        var gearedOverlay = ResolveHub(hubAtoms).Get(channel);

        Assert.Equal(bareOverlay + 40, gearedOverlay);
        Assert.Equal(bareBattle + 40, gearedBattle);
    }

    [Fact]
    public void AnUnparseableOp_isSkipped_neverCoercedToFlat()
    {
        // AtomDerivedSubsystem.TryParseOp's own standing contract: there is no `more` on the derived
        // side, and silent coercion is how a wrong number ships looking correct.
        var equip = EquipWith(Atom(ShippedChannel, "more", 40));
        Assert.Empty(equip.DerivedAtomsFor("s1"));
    }

    // ── the run itself: gear reaches the guards, and termination survives it ───────────────────────

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void AGearedCornerRun_movesTheGuardsPrediction_soTheWiringIsNotInert()
    {
        DominanceGuardTestSupport.ConfigureShippedTuning();

        // The REAL 12-corner shape, deliberately -- not a two-build shortcut. Uniform gear on two
        // near-identical small allocations provably moves NOTHING even when the wiring is perfect:
        // a flat crit-rate line scales both sides' damage by the same factor, so time-to-kill shrinks
        // equally on both and the win RATIO is unchanged. Measured, not assumed (an earlier version of
        // this test used Might-vs-Fortitude at allocation 100 and read exactly 0.0). The twelve corners
        // differ in their crit-relevant baselines, so the same flat line lands on twelve different
        // points of a sigmoid and the matrix genuinely moves.
        var builds = RealCorners();
        var equip = EquipWith(Atom(ShippedChannel, "flat", ShippedAmount));
        var gear = Enumerable.Range(0, builds.Length).Select(_ => equip.DerivedAtomsFor("s1")).ToArray();

        var bare = DominanceGuard.Measure(builds, 100);
        var geared = DominanceGuard.Measure(builds, 100, gear);

        // The falsifying half. A subsystem that registered but contributed nothing would produce an
        // identical matrix, and the run would "execute" while proving nothing.
        var moved = bare.Matrix.Zip(geared.Matrix,
            (b, g) => Math.Abs(b.WinShareAttacker - g.WinShareAttacker)).Max();
        Assert.True(moved > 0, "equipping a real shipped stat.derived atom moved no predicted win share -- the gear reached no channel the predictor reads");
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void AGearedCornerRun_keepsTerminationGreen_andReportsCoverage()
    {
        DominanceGuardTestSupport.ConfigureShippedTuning();
        // The real twelve, matching gk-forge/tools/DominanceBaseline --geared: this IS Checkpoint 1's own
        // success criterion ("the first geared corner run executes, termination stays green, and
        // dominance reports with its coverage line"), pinned as a test rather than only as a
        // once-captured console run.
        var builds = RealCorners();
        var equip = EquipWith(Atom(ShippedChannel, "flat", ShippedAmount));
        var gear = Enumerable.Range(0, builds.Length).Select(_ => equip.DerivedAtomsFor("s1")).ToArray();

        // Throws TerminationViolation if a geared pair can never resolve -- the whole point of the
        // hard guard, exercised with equipment in the pipeline for the first time.
        var verdict = TerminationGuard.Assert(builds, 100, gear);
        Assert.True(verdict.PairsChecked > 0);

        var report = DominanceGuard.Measure(builds, 100, gear);
        Assert.False(string.IsNullOrWhiteSpace(report.Coverage.ElementAxis));
        Assert.NotEmpty(report.Coverage.ReservedFamilies);
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void Gear_misalignedWithBuilds_throwsRatherThanSilentlyGearingTheWrongCorner()
    {
        DominanceGuardTestSupport.ConfigureShippedTuning();
        var builds = TwoCorners();
        var oneEntry = new IReadOnlyList<BoundDerivedAtom>[] { Array.Empty<BoundDerivedAtom>() };

        Assert.Throws<ArgumentException>(() => DominanceGuard.Measure(builds, 100, oneEntry));
        Assert.Throws<ArgumentException>(() => TerminationGuard.Assert(builds, 100, oneEntry));
    }

    // ── the content the run stands on ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheShippedCorpus_stillCarriesTheAtomThisSuitePinsAsRepresentative()
    {
        // Reads the real file so the constants above cannot drift from it. Deliberately does NOT
        // assert the corpus SIZE: every stat.derived affix family is refused by FamilyExpansion (E43)
        // today because gk-data/packs/fusion/data/seed/items/_tuning/tier-bands.v1.json authors a sharePermille for the 14
        // stat.modify primary-channel families only -- a real, named content gap. When it closes, the
        // corpus grows and this test must not break for it.
        // `data/seed/**` is the CONTENT PACK's tree, so `FindRepoFile` - which walks UP from the test
        // host's own directory looking for the relative path - can never find it: the only ancestors of a
        // gk-core test's bin output are gk-core and the workspace root, and the file lives in a SIBLING
        // repository. Its refusal said so and nothing else:
        //     could not locate data\seed\atoms\trait-critical-hunter.json above ...\tests\...\bin\
        //
        // The comment four lines above already names the correct post-split path -
        // `gk-data/packs/fusion/data/seed/items/_tuning/tier-bands.v1.json` - so the prose was right and
        // the code beside it was not: the sixth instance of that shape in this program.
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "atoms",
                                "trait-critical-hunter.json");
        Assert.True(File.Exists(path), $"the shipped atom this suite pins is not there: {path}");
        var collected = AtomSeedFile.Collect(new[] { (path, File.ReadAllText(path)) });

        var atom = Assert.Single(collected.Content.Atoms, a => a.AtomId == ShippedAtomId);
        Assert.Equal("stat.derived", atom.KindId);

        var mods = EquipAtomSource.FromResolver(_ => new[] { atom }).DerivedAtomsFor("s1");
        var bound = Assert.Single(mods);
        Assert.Equal(ShippedChannel, bound.Channel);
        Assert.Equal(DerivedModifierOp.Flat, bound.Op);
        Assert.Equal(ShippedAmount, bound.Amount);
    }
}

/// <summary>Shared with <see cref="DominanceGuardTests"/>'s own reasoning: <c>AptitudeTuningHub.Tuning</c>'s
/// getter throws until <c>Configure</c> has run, so every guard test loads the LIVE shipped config
/// itself rather than depending on some earlier test in the run having done it.</summary>
static class DominanceGuardTestSupport
{
    public static void ConfigureShippedTuning() =>
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(ShippedAptitudesPath())));

    static string ShippedAptitudesPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var tuningDir = Path.Combine(dir.FullName, "data", "tuning");
            if (Directory.Exists(tuningDir))
            {
                var best = Directory.GetFiles(tuningDir, "aptitudes.v*.json")
                    .Select(f => (Path: f, V: int.TryParse(
                        Path.GetFileNameWithoutExtension(f).Split(".v").Last(), out var v) ? v : -1))
                    .Where(x => x.V >= 0).OrderByDescending(x => x.V).FirstOrDefault();
                if (best.Path is not null) return best.Path;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate data/tuning/aptitudes.v*.json above " + AppContext.BaseDirectory);
    }
}
