using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Aptitudes;

/// <summary>class-system-todo.md P6.1 — <see cref="PointBudget"/> (spec-point-economy.md, read in
/// full this session). Table in §7: tests 1-5 covered here — the ones that are actually
/// <see cref="PointBudget"/>'s/<see cref="AptitudeAllocation"/>'s own concern. Tests 6-9 (respec,
/// persistence, the two-currency rule) belong to `RespecPolicy`/`AllocationStore`, P6.2/P6.3,
/// unbuilt — spec-point-economy.md §5's own project-structure listing keeps them in separate files
/// for the same reason `balance-guard` keeps `TerminationGuard`/`DominanceGuard` in separate
/// files: the standing/scope difference is the design, not an implementation detail.</summary>
public class PointBudgetTests
{
    static string FindShippedAptitudesTuningPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", "aptitudes.v2.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate data/tuning/aptitudes.v2.json above " + AppContext.BaseDirectory);
    }

    static AptitudeTuning ShippedTuning() =>
        AptitudeTuningLoader.Parse(File.ReadAllText(FindShippedAptitudesTuningPath()));

    // ── C6 (spec-tree-state.md §3, D34): SkillPointsFor / skillPointsPerThetaMilliByScope ──────────
    // ShippedTuning() above stays pinned to aptitudes.v2.json, which predates this table (its own
    // pointEconomy.skillPointsPerThetaMilliByScope is absent, deliberately -- see
    // AptitudePointEconomy's own doc comment). The tests below that need real scoped skill rates load
    // the CURRENT shipped file (the one RpgHost.cs/Program.cs actually load) -- v7 as of D55
    // (2026-09-06, spec-tree-state.md open question 3: creatureType/aspect/uniqueCreature moved from the
    // borrowed-placeholder {4,4,6} to the {3,4,4,6}-ratio-derived {15,15,22}); v6 was the first
    // version to carry the table at all.

    static string FindAptitudesV6Path()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", "aptitudes.v10.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate data/tuning/aptitudes.v10.json above " + AppContext.BaseDirectory);
    }

    static AptitudeTuning ShippedTuningWithSkillScopes() =>
        AptitudeTuningLoader.Parse(File.ReadAllText(FindAptitudesV6Path()));

    [Fact]
    public void Four_scopes_sum_in_the_allocation_object_but_the_resolver_reads_each_alone()
    {
        // Rewritten for species-progression step 6.1 (spec-species-layer-delivery.md, "Rewrite,
        // never re-bless, the tests whose subject is the merge"): AptitudeAllocation's OWN
        // Total/GrandTotal/Share methods are UNCHANGED by 6.1 -- they still sum every scope's points
        // for one aptitude and divide by the grand total across all twelve, exactly as
        // spec-point-economy.md §2 always specified for the ALLOCATION OBJECT. This test still proves
        // that math.
        //
        // What is NO LONGER TRUE is that a real resolve reads .Share() at all: since step 6.1,
        // AptitudeResolver.Resolve resolves each AllocationScope present ALONE, via
        // .ShareWithinScope(scope, aptitudeId) -- one scope's contribution is unchanged by adding
        // points to another scope, the opposite of "share taken on the sum". That per-layer contract
        // is proven directly against the resolver in AptitudeResolverTests.cs's own
        // TwoNonEmptyScopes_resolveIndependently_eachAtItsOwnScopesFullShare_notTheMergedGrandTotalShare
        // and AddingPointsToASecondScope_leavesTheFirstScopesOwnContributionUnchanged.
        var allocation =
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 10)
            + AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 20)
            + AptitudeAllocation.Single(AllocationScope.Aspect, "Might", 30)
            + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 40)
            + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 100);

        Assert.Equal(100, allocation.Total("Might")); // 10+20+30+40, all four scopes summed
        Assert.Equal(200, allocation.GrandTotal()); // Might 100 + Vigor 100

        // .Share() itself is still "on the sum" (100/200 = 0.5) -- an allocation-object fact, not a
        // claim about how AptitudeResolver.Resolve treats the two scopes since step 6.1.
        Assert.Equal(0.5, allocation.Share("Might"), precision: 12);
    }

    [Fact]
    public void Each_scope_draws_from_its_own_budget()
    {
        // spec-point-economy.md §7 test 2: overspending one scope cannot be covered by another.
        var tuning = ShippedTuning();
        var commanderRate = tuning.PointEconomy.AptitudePointsPerThetaMilliByScope[AllocationScope.Commander];
        var commanderBudget = PointBudget.PointsFor(AllocationScope.Commander, sourceValue: 100, tuning);
        Assert.Equal(100 * commanderRate, commanderBudget);

        // Spend OVER the commander budget, while leaving creatureType entirely unspent (a huge surplus
        // there, if scopes could cover each other).
        var overspentCommander = AptitudeAllocation.Single(AllocationScope.Commander, "Might", commanderBudget + 1);

        var check = PointBudget.CheckScope(AllocationScope.Commander, overspentCommander, sourceValue: 100, tuning);

        Assert.False(check.WithinBudget);
        Assert.Equal(commanderBudget + 1, check.Spent);
        Assert.Equal(commanderBudget, check.Budget);
        // The unspent creatureType scope has no bearing on this check at all -- CheckScope never reads
        // any scope but the one it was asked about, so there is no code path for a surplus elsewhere
        // to "cover" this shortfall.
    }

    [Fact]
    public void Rates_are_ordered_commander_smallest_unique_largest()
    {
        // ⛔ RENAMED (species-build T0.3, audit A1) from "Commander_budget_is_smallest_...". Holding
        // the source constant on purpose ONLY proves the RATE table's own ordering (3 < 4 <= 4 < 6) --
        // it does NOT prove the BUDGET ordering the old name claimed, because the four scopes' real
        // sources are in different UNITS (an index vs an accumulation). That gap is exactly what let
        // the CreatureType-source defect (almanac XP, a 176x inversion at species L12) ship undetected --
        // this test kept passing straight through it. See Real_budgets_are_ordered_at_representative_sources
        // below for the test that actually proves the budget claim.
        var tuning = ShippedTuning();
        const long sameSourceValue = 100; // isolates the RATE ordering from any per-scope source difference.

        var commander = PointBudget.PointsFor(AllocationScope.Commander, sameSourceValue, tuning);
        var creatureType = PointBudget.PointsFor(AllocationScope.CreatureType, sameSourceValue, tuning);
        var aspect = PointBudget.PointsFor(AllocationScope.Aspect, sameSourceValue, tuning);
        var uniqueCreature = PointBudget.PointsFor(AllocationScope.UniqueCreature, sameSourceValue, tuning);

        Assert.True(commander < creatureType, $"commander ({commander}) must be < creatureType ({creatureType})");
        Assert.True(creatureType <= aspect, $"creatureType ({creatureType}) must be <= aspect ({aspect})");
        Assert.True(aspect < uniqueCreature, $"aspect ({aspect}) must be < uniqueCreature ({uniqueCreature})");
    }

    [Fact]
    public void Real_budgets_are_ordered_at_representative_sources()
    {
        // species-build T0.3, audit A1 -- the test the old (renamed) one above could not be, because
        // it held the source constant. Each scope is fed a REPRESENTATIVE VALUE IN ITS OWN UNITS,
        // drawn from this repo's own already-recorded ordinary-play numbers, and the ordering is
        // asserted on the resulting BUDGETS -- the claim the class actually needs to hold.
        //
        // Sources, all picked at the SAME representative "mid-game milestone" magnitude -- the whole
        // point of species-build's audit A1 fix is that species level is now an INDEX comparable in
        // scale to Theta_player and specimen level, unlike the old "almanac XP" accumulation (2,640 at
        // species L12) that was never comparable to anything:
        //   commander:   Theta_player = 20        (ssot-power-scale.md's own pin, "P(20) = 680")
        //   creatureType:   species level 21 -> CreatureTypeSourceFromLevel(21) = 20
        //   uniqueCreature: specimen level 20         (rpg-progression.md's own balance note:
        //                                            "L12-20 after 20 matches" -- the range's own top)
        //
        // Aspect is deliberately EXCLUDED. Its real source, `element_mastery`, does not exist yet --
        // it is owned by the creature program's `aspect-scope` module, itself reverted and not authorized
        // to build (decisions.md, "Creature program" row). Inventing a value for it here would decide the
        // very ordering this test exists to prove, which is the same "fabricated source" defect this
        // module exists to fix -- so this test asserts commander < creatureType < uniqueCreature over real
        // sources only, and leaves Aspect's own ordering proof to whoever builds that tier for real.
        var tuning = ShippedTuning();

        const long thetaPlayer = 20;
        const long speciesLevel = 21;
        const long specimenLevel = 20;

        var commanderBudget = PointBudget.PointsFor(AllocationScope.Commander, thetaPlayer, tuning);
        var creatureTypeBudget = PointBudget.PointsFor(
            AllocationScope.CreatureType, PointBudget.CreatureTypeSourceFromLevel(speciesLevel), tuning);
        var uniqueCreatureBudget = PointBudget.PointsFor(AllocationScope.UniqueCreature, specimenLevel, tuning);

        Assert.True(commanderBudget < creatureTypeBudget,
            $"commander ({commanderBudget}) must be < creatureType ({creatureTypeBudget}) at real sources");
        Assert.True(creatureTypeBudget < uniqueCreatureBudget,
            $"creatureType ({creatureTypeBudget}) must be < uniqueCreature ({uniqueCreatureBudget}) at real sources");
    }

    [Fact]
    public void CreatureTypeSourceFromLevel_isZero_atLevelZeroAndLevelOne()
    {
        // species-build T0.4 -- an unrecorded actor's progression defaults to Level = 1
        // (RpgStore.Progression.cs's own DefaultPlayerDtoUnlocked), so a never-levelled species must
        // carry EXACTLY ZERO points or every battle/expedition golden would move the moment
        // `creature-type-allocation`'s compose-at-read baseline lands.
        Assert.Equal(0, PointBudget.CreatureTypeSourceFromLevel(0));
        Assert.Equal(0, PointBudget.CreatureTypeSourceFromLevel(1));
        Assert.Equal(1, PointBudget.CreatureTypeSourceFromLevel(2));
        Assert.Equal(11, PointBudget.CreatureTypeSourceFromLevel(12));
    }

    [Fact]
    public void PointsFor_creatureType_atLevelZeroOrOne_isZeroBudget()
    {
        // The composed proof: PointsFor(CreatureType, CreatureTypeSourceFromLevel(level)) is zero for a
        // never-levelled species, at any real CreatureType rate.
        var tuning = ShippedTuning();
        Assert.Equal(0, PointBudget.PointsFor(AllocationScope.CreatureType, PointBudget.CreatureTypeSourceFromLevel(0), tuning));
        Assert.Equal(0, PointBudget.PointsFor(AllocationScope.CreatureType, PointBudget.CreatureTypeSourceFromLevel(1), tuning));
    }

    [Fact]
    public void UniqueCreatureSourceFromLevel_isZero_atLevelZeroAndLevelOne()
    {
        // passive-tree G7 -- the exact UniqueCreature-scope mirror of
        // CreatureTypeSourceFromLevel_isZero_atLevelZeroAndLevelOne above: RpgStore.CreateUniqueActor
        // starts every specimen at level 1 (never 0), so a never-levelled specimen must carry EXACTLY
        // ZERO points here too, or every roster entry ever created gets a free non-empty allocation.
        Assert.Equal(0, PointBudget.UniqueCreatureSourceFromLevel(0));
        Assert.Equal(0, PointBudget.UniqueCreatureSourceFromLevel(1));
        Assert.Equal(1, PointBudget.UniqueCreatureSourceFromLevel(2));
        Assert.Equal(11, PointBudget.UniqueCreatureSourceFromLevel(12));
    }

    [Fact]
    public void PointsFor_uniqueCreature_atLevelZeroOrOne_isZeroBudget()
    {
        // The composed proof: PointsFor(UniqueCreature, UniqueCreatureSourceFromLevel(level)) is zero for a
        // never-levelled specimen, at any real UniqueCreature rate -- the same property CreatureType has.
        var tuning = ShippedTuning();
        Assert.Equal(0, PointBudget.PointsFor(AllocationScope.UniqueCreature, PointBudget.UniqueCreatureSourceFromLevel(0), tuning));
        Assert.Equal(0, PointBudget.PointsFor(AllocationScope.UniqueCreature, PointBudget.UniqueCreatureSourceFromLevel(1), tuning));
    }

    [Fact]
    public void No_cap_on_an_aptitude()
    {
        // PS-8 (AGENTS.md, CLAUDE.md): no hard progression ceiling. A budget an actor earns more of is
        // not a cap (spec-point-economy.md §2.2) -- an enormous source value must produce a
        // proportionally enormous budget, never clamp.
        var tuning = ShippedTuning();
        const long enormousSourceValue = 10_000_000_000; // far past any Theta a real player reaches today.

        var budget = PointBudget.PointsFor(AllocationScope.UniqueCreature, enormousSourceValue, tuning);
        var rate = tuning.PointEconomy.AptitudePointsPerThetaMilliByScope[AllocationScope.UniqueCreature];

        Assert.Equal(enormousSourceValue * rate, budget); // exact, not clamped to any ceiling.

        // The spend side is equally uncapped -- AptitudeAllocation.Single accepts the same enormous
        // figure without throwing (Phase 1's own PS-8 guarantee; confirmed here from point-economy's
        // own testing table, not re-derived from AptitudeAllocation's own tests alone).
        var allocation = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", budget);
        Assert.Equal(budget, allocation.Total("Might"));
    }

    [Fact]
    public void Budget_is_exact_at_high_theta()
    {
        // spec-point-economy.md §6's own worked example: "int overflows near Theta = 715,000,000 at 3
        // points/Theta" -- reachable, not hypothetical, once Theta is genuinely uncapped (PS-8). Picks
        // a source value comfortably past that threshold and checks the result is the EXACT product,
        // not an int-truncated or wrapped one.
        var tuning = ShippedTuning();
        const long farPastIntOverflow = 1_000_000_000; // int.MaxValue is ~2.147B; 3x this alone exceeds it.
        var rate = tuning.PointEconomy.AptitudePointsPerThetaMilliByScope[AllocationScope.Commander];

        var budget = PointBudget.PointsFor(AllocationScope.Commander, farPastIntOverflow, tuning);

        // Computed independently in decimal (not through the long multiplication under test) so a
        // silently-wrapped int result would disagree with this expectation, not coincidentally match it.
        var expected = (long)((decimal)farPastIntOverflow * rate);
        Assert.Equal(expected, budget);
        Assert.True(budget > int.MaxValue, $"expected the exact product to exceed int.MaxValue, got {budget}");
    }

    [Fact]
    public void PointsFor_nullTuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => PointBudget.PointsFor(AllocationScope.Commander, 100, null!));
    }

    [Fact]
    public void PointsFor_negativeSourceValue_throws()
    {
        var tuning = ShippedTuning();
        Assert.Throws<ArgumentOutOfRangeException>(() => PointBudget.PointsFor(AllocationScope.Commander, -1, tuning));
    }

    [Fact]
    public void PointsFor_zeroSourceValue_isZeroBudget_notRejected()
    {
        // A fresh actor with no progression yet (Theta_player=0, a brand-new creature type, etc.) is
        // ordinary, not an error -- only a NEGATIVE source value is a validation failure.
        var tuning = ShippedTuning();
        Assert.Equal(0, PointBudget.PointsFor(AllocationScope.Commander, 0, tuning));
    }

    [Fact]
    public void CheckScope_nullAllocation_throws()
    {
        var tuning = ShippedTuning();
        Assert.Throws<ArgumentNullException>(() => PointBudget.CheckScope(AllocationScope.Commander, null!, 100, tuning));
    }

    // ── SkillPointsFor — the exact structural sibling of PointsFor (C6) ─────────────────────────────

    [Fact]
    public void SkillPointsFor_nullTuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => PointBudget.SkillPointsFor(AllocationScope.Commander, 100, null!));
    }

    [Fact]
    public void SkillPointsFor_negativeSourceValue_throws()
    {
        var tuning = ShippedTuningWithSkillScopes();
        Assert.Throws<ArgumentOutOfRangeException>(() => PointBudget.SkillPointsFor(AllocationScope.Commander, -1, tuning));
    }

    [Fact]
    public void SkillPointsFor_zeroSourceValue_isZeroBudget_notRejected()
    {
        // Mirrors PointsFor_zeroSourceValue_isZeroBudget_notRejected -- a fresh actor (Theta_player=0,
        // a brand-new creature type) is ordinary, not an error; only a NEGATIVE source value is rejected.
        var tuning = ShippedTuningWithSkillScopes();
        Assert.Equal(0, PointBudget.SkillPointsFor(AllocationScope.Commander, 0, tuning));
    }

    [Fact]
    public void SkillPointsFor_commander_matchesTheShippedRateExactly()
    {
        // D38: g = 11, sized against the measured corner build (10.40 rounded up) -- spec-tree-state.md
        // §8's own table, pinned here the same way ParsesTheShippedFile pins real shipped numbers.
        var tuning = ShippedTuningWithSkillScopes();
        const long thetaPlayer = 100;
        Assert.Equal(11, tuning.PointEconomy.SkillPointsPerThetaMilliByScope[AllocationScope.Commander]);
        Assert.Equal(1_100, PointBudget.SkillPointsFor(AllocationScope.Commander, thetaPlayer, tuning));
    }

    [Fact]
    public void SkillPointsFor_noCap_evenAtAnEnormousSourceValue()
    {
        // PS-8, mirroring No_cap_on_an_aptitude above -- a budget an actor earns more of is not a cap.
        var tuning = ShippedTuningWithSkillScopes();
        const long enormousSourceValue = 10_000_000_000;

        var budget = PointBudget.SkillPointsFor(AllocationScope.UniqueCreature, enormousSourceValue, tuning);
        var rate = tuning.PointEconomy.SkillPointsPerThetaMilliByScope[AllocationScope.UniqueCreature];

        Assert.Equal(enormousSourceValue * rate, budget); // exact, never clamped
    }

    [Fact]
    public void SkillPointsFor_missingScopeRate_isALoadRejectionNamingTheScope()
    {
        // T5 / spec-tree-state.md §3: "a missing rate is a load rejection naming it, never a default."
        // aptitudes.v2.json predates skillPointsPerThetaMilliByScope entirely (the table is OPTIONAL at
        // parse time -- AptitudePointEconomy's own doc comment explains why), so every scope is
        // "missing" against it. The rejection must still name the exact scope asked for, never fall
        // back to Theta_player silently.
        var tuning = ShippedTuning(); // v2.json -- no skillPointsPerThetaMilliByScope at all
        var ex = Assert.Throws<AptitudeTuningRejection>(
            () => PointBudget.SkillPointsFor(AllocationScope.CreatureType, 100, tuning));
        Assert.Contains("CreatureType", ex.Message, StringComparison.Ordinal);
        Assert.Contains("skillPointsPerThetaMilliByScope", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>D34 — the module's own required proof. Positive half: four scopes read the SAME
    /// shared Theta-shaped source value and resolve to a DIFFERENT budget per scope, because each
    /// reads its own rate. Unlike the sibling aptitude-point table, this one carries no
    /// commander-smallest/uniqueCreature-largest ordering claim -- D38's commander = 11 is calibrated
    /// independently (the corner-share derivation) and happens to be the LARGEST of the four shipped
    /// rates, not the smallest; the other three are UNMEASURED placeholders borrowed verbatim from the
    /// sibling table's own {4,4,6} (creatureType and aspect legitimately tie at 4, same as the sibling).
    /// Negative half: a creature actor that (by mistake) read the COMMANDER scope's rate against its own
    /// source value gets a WRONG, visibly different number from reading its own CreatureType scope
    /// correctly -- proving scope isolation actually matters here, not merely that the configured
    /// numbers happen to differ.</summary>
    [Fact]
    public void Every_actor_reads_its_own_scope_budget()
    {
        var tuning = ShippedTuningWithSkillScopes();
        const long sharedSourceValue = 100; // one shared actor-Theta-shaped value, fed to all four scopes.

        var commander = PointBudget.SkillPointsFor(AllocationScope.Commander, sharedSourceValue, tuning);
        var creatureType = PointBudget.SkillPointsFor(AllocationScope.CreatureType, sharedSourceValue, tuning);
        var aspect = PointBudget.SkillPointsFor(AllocationScope.Aspect, sharedSourceValue, tuning);
        var uniqueCreature = PointBudget.SkillPointsFor(AllocationScope.UniqueCreature, sharedSourceValue, tuning);

        // Positive proof: each scope reads its OWN rate against the same shared source value. commander
        // and uniqueCreature are each distinct from every other scope; creatureType and aspect legitimately
        // tie (D55, 2026-09-06: both derived from the sibling {3,4,4,6} ratio's shared "4" for
        // creatureType/aspect, scaled against commander=11 -- 15 each) -- a tie is not a bug here, so it
        // is asserted explicitly rather than folded into a blanket "all four differ" claim that would
        // be false against the shipped numbers.
        Assert.NotEqual(commander, creatureType);
        Assert.NotEqual(commander, aspect);
        Assert.NotEqual(commander, uniqueCreature);
        Assert.Equal(creatureType, aspect); // legal tie -- both 15, D55's ratio-derived rate
        Assert.NotEqual(aspect, uniqueCreature);
        Assert.NotEqual(creatureType, uniqueCreature);

        // Negative proof (D34's own named failure mode): a creature actor is scoped CreatureType. If its
        // code path mistakenly read the COMMANDER rate against the creature's own source value instead
        // (the "every actor reads Theta_player" bug this table exists to prevent), the number it would
        // get is WRONG -- it must not equal the correct, own-scope answer.
        var creatureReadingCommanderScopeByMistake = PointBudget.SkillPointsFor(AllocationScope.Commander, sharedSourceValue, tuning);
        Assert.NotEqual(creatureType, creatureReadingCommanderScopeByMistake);
    }
}
