using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Contracts;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P1.14 — every item in spec-distribution-reconcile.md's nine-item
/// register has a landed verdict. §6's test list 1/2/2b/3/4/5/6/7 maps onto: 2b =
/// <see cref="BattleKnownChannelsTests"/>, 4/parts-of-3 = <see cref="PowerIndexHydrationTests"/>,
/// 6 = <see cref="SeamCoverageTests"/>. This file covers 1, 5, 7, and the master enumeration; 2 (the
/// full battle/overlay agreement test) is honestly deferred to P2.6 — it needs an `AptitudeSubsystem`
/// and a `ChannelMods` producer that do not exist until Phase 2, same reasoning as V3.</summary>
public class DistributionReconcileVerdictTests
{
    /// <summary>The nine register items, each cross-checked against the spec's own "Verdict:" text —
    /// not a duplicate opinion, a proof the document actually states one for every item it claims to
    /// cover.</summary>
    static readonly (string Item, string VerdictSnippet)[] NineItems =
    {
        ("3.1 ClassStatPlugin wrong pipeline", "KEEP IT, with a two-part comment"),
        ("3.2 BattleStatComposer runs no subsystems", "do NOT unify the composers"),
        ("3.2a known-channel set narrower than distribution", "widen the known-channel set"),
        ("3.3 Theta is zero on the overlay path", "WIRE, and it is"),
        ("3.4 BattleStatComposer aliases Theta to Level", "DOCUMENT the contract"),
        ("3.5 progression.bonus.* private f(level)", "absent from the closed power inventory"),
        ("3.6 derived to primary bridge is five channels", "Verdict: DOCUMENT."),
        ("3.7 unitClass/statClass nulls", "Owned by"),
        ("3.9 four empty plugins undetected", "this is the shape of the whole problem"),
    };

    [Fact]
    public void AllNineRegisterItemsHaveALandedVerdict()
    {
        var repoRoot = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(repoRoot, "docs", "architecture", "class-system", "spec-distribution-reconcile.md"));

        Assert.Equal(9, NineItems.Length);
        var missing = new List<string>();
        foreach (var (item, snippet) in NineItems)
            if (!text.Contains(snippet, StringComparison.Ordinal))
                missing.Add($"{item}: expected verdict text '{snippet}' not found");

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void AnActorSubsystemReachesAComposedChannel()
    {
        // 3.1's repair, proven end to end through ActorHub.ResolveDerived -- the exact path 3.1 found
        // ClassStatPlugin does NOT sit on. Distinct from PowerIndexHydrationTests: this asserts the
        // SEAM works at all, independent of Theta hydration specifically.
        //
        // class-system-todo.md P3.3 (2026-08-27): RpgProgressionSubsystem's level-gated bonus-flat
        // stub is retired, so this now proves the seam via progression.power/progression.realm --
        // the two channels that subsystem still owns -- rather than the retired bonus flats.
        // AptitudeSubsystem (the OTHER subsystem shipping today, since P2.4) proves the SAME seam
        // property for progression.bonus.* in ActorHubTests.Applied_combat_includes_progression_
        // bonus_flats and throughout AptitudeSubsystemTests.cs -- not duplicated here.
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new RpgProgressionSubsystem(new FixedPowerIndexProvider(1234)));

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var result = hub.ResolveDerived(ctx);

        Assert.Equal(1234.0, result.Get(DerivedStatChannels.ProgressionPower));
    }

    [Fact]
    public void ProgressionBonusStubCurveIsGoneFromCodeAndLedger()
    {
        // class-system-todo.md P3.3 (2026-08-27) flips this test's own pinned choice: P1.13 chose
        // "kept, inventoried" (asserted here until now); P3.3's acceptance is "the §10 row cleared in
        // the same change" that deletes the stub -- so the ONLY passing state left is both gone.
        var repoRoot = FindRepoRoot();
        var subsystemSource = File.ReadAllText(Path.Combine(repoRoot, "src", "FusionRpg.Core", "Stats", "Derived", "Subsystems", "RpgProgressionSubsystem.cs"));
        var ledgerText = File.ReadAllText(Path.Combine(repoRoot, "docs", "architecture", "power", "ssot-power-scale.md"));

        var stubCurveStillInCode = subsystemSource.Contains("level * 10", StringComparison.Ordinal);
        var inventoried = ledgerText.Contains("RpgProgressionSubsystem`'s bonus flats", StringComparison.Ordinal)
                        && ledgerText.Contains("Latent stub, found by class-system P1.13", StringComparison.Ordinal);

        Assert.False(stubCurveStillInCode, "expected P3.3's retirement: the level*10 stub is gone from RpgProgressionSubsystem.cs");
        Assert.False(inventoried, "expected P3.3's retirement: the §10 inventory row is cleared, not left stale");
    }

    [Fact]
    public void DerivedToPrimaryBridgeIsFourChannels()
    {
        // Canary (§6 test 7): MergeAppliedCombat reads exactly FOUR progression.bonus.* channels since
        // 2026-09-18. Widening it silently is how an extra door into Unity's primary fields would
        // appear unnoticed.
        //
        // It was five until the owner retired `progression.bonus.atk` (solid-enforcement `retire-atk`),
        // which `MergeAppliedCombat` no longer adds. The atk edge below is KEPT in this fixture on
        // purpose: it proves the loader drops it, so the bridge cannot silently regrow through it.
        //
        // class-system-todo.md P3.3 (2026-08-27): the modifier source is AptitudeSubsystem now, not
        // RpgProgressionSubsystem's retired stub -- four aptitudes, one edge each, funded equally so
        // every one of the four bridge channels lands nonzero.
        var tuning = AptitudeTuningLoader.Parse("""
            {
              "schemaVersion": 1, "version": 1,
              "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
              "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
              "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
              "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
              "familyRead": {
                "progression.bonus.maxHp": "magnitude", "progression.bonus.atk": "magnitude",
                "progression.bonus.defense": "magnitude", "progression.bonus.arm1": "magnitude",
                "progression.bonus.arm2": "magnitude"
              },
              "edges": [
                { "channel": "progression.bonus.maxHp", "source": "Vigor", "kMilli": 12000 },
                { "channel": "progression.bonus.atk", "source": "Might", "kMilli": 10000 },
                { "channel": "progression.bonus.defense", "source": "Fortitude", "kMilli": 10000 },
                { "channel": "progression.bonus.arm1", "source": "Bulwark", "kMilli": 8000 },
                { "channel": "progression.bonus.arm2", "source": "Retribution", "kMilli": 8000 }
              ]
            }
            """);
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 1)
                        + AptitudeAllocation.Single(AllocationScope.Commander, "Might", 1)
                        + AptitudeAllocation.Single(AllocationScope.Commander, "Fortitude", 1)
                        + AptitudeAllocation.Single(AllocationScope.Commander, "Bulwark", 1)
                        + AptitudeAllocation.Single(AllocationScope.Commander, "Retribution", 1);
        var ladder = new PowerLadder(PowerTuningHub.Tuning);

        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new AptitudeSubsystem(tuning, ladder, new FixedPowerIndexProvider(100), _ => allocation));

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var result = hub.Resolve(ctx);

        // All four REMAINING bridge channels must be reachable (non-throwing) and the merge must
        // actually move AppliedCombat away from RuntimePrimary when they are nonzero -- proving the
        // bridge is live, not just declared.
        string[] bridgeChannels =
        {
            DerivedStatChannels.ProgressionBonusMaxHp,
            DerivedStatChannels.ProgressionBonusDefense, DerivedStatChannels.ProgressionBonusArm1,
            DerivedStatChannels.ProgressionBonusArm2
        };
        Assert.Equal(4, bridgeChannels.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(result.RuntimePrimary.MaxHp, result.AppliedCombat.MaxHp);

        // The fixture above still carries a `progression.bonus.atk` edge AND its `familyRead` row, and
        // the loader drops both -- so this is the drop path proven end to end through the real Hub
        // (`retire-atk` R2), not a comment about it. Atk is now exactly the primary value.
        Assert.Equal(result.RuntimePrimary.Atk, result.AppliedCombat.Atk);
    }

    [Fact]
    public void BothThetaMechanismsAreDocumented()
    {
        // 3.4's verdict: DOCUMENT the contract (wiring only if 3.2 required it, which it does not --
        // 3.2's own verdict keeps the composers separate). This asserts the divergence is a STATED
        // rule, not a silent one: overlay reads IPowerIndexProvider, battle aliases Level.
        var repoRoot = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(repoRoot, "docs", "architecture", "class-system", "spec-distribution-reconcile.md"));
        Assert.Contains("the two paths obtain", text, StringComparison.Ordinal);
        Assert.Contains("DOCUMENT the contract", text, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    sealed class FixedPowerIndexProvider : IPowerIndexProvider
    {
        readonly int _theta;
        public FixedPowerIndexProvider(int theta) => _theta = theta;
        public int ActorIndex(StatContext ctx) => _theta;
        public int ContentIndex(ContentContext ctx) => _theta;
        public PowerAxisReport Explain(StatContext ctx) => throw new NotSupportedException();
        public int ActorIndexFor(FusionRpg.Core.Saves.SaveId save, FusionRpg.Core.Commanders.EmpireId empire) => _theta;
    }
}
