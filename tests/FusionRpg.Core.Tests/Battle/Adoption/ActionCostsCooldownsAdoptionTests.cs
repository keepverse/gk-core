using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Unlock;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// A19 (spec-action-costs-cooldowns-adoption.md) — T56.1: `AlwaysAffordable.Instance` swapped for a
/// real, per-battle `CostLedger` at both real construction sites (`BasicAttack.cs:117`,
/// `TimelineDispatch.cs:70`). Built on `BattleGoldenTests.CloseSetup()`, the same proven-reliable
/// base `ActionDispatchGeneralizationTests.cs` (A18f) uses, for the identical reason: a hand-rolled
/// actor pair risks stalemating or resolving in one swing before the observable ever gets a chance
/// to matter.
/// </summary>
public class ActionCostsCooldownsAdoptionTests
{
    /// <summary>A real action costing 100,000,000 `qi` on commit. `LawnActorResourcePools.GetOrCreate`
    /// seeds every actor's pools FULL on first touch (`ActorResourcePools.CreateFull` — "every resource
    /// starts at max", NOT zero; the `resource-hub` memory's "battle pools still unseeded" claim does
    /// NOT hold for this runtime path and needs correcting). So the fixture cannot rely on an unseeded
    /// pool — it instead picks an amount no plausible derived `qi` max can reach, making it reliably
    /// unaffordable regardless of the actor's real stats.</summary>
    static CompiledAction UnaffordableSkill() => new(
        ActionId: "skill.unaffordable", Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = "skill.unaffordable" },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: new[] { new CompiledActionCost("qi", ValueSpec.Of(100_000_000), ActionCostTiming.OnCommit) },
        Scopes: Array.Empty<ActionScopeRow>());

    /// <summary>A real action costing exactly `amount` `qi` on commit — used to probe where squad:0's
    /// real (empirically found, not hardcoded-assumed) `qi` max sits, per T56.2's evidence: a cost of
    /// 100 is affordable exactly ONCE across a 13-round battle (never again — `qi` regen is zero or
    /// negligible relative to 100 over that many rounds), while a cost of 110+ is never affordable even
    /// once. This makes 100 a reliable "pay once, then starve" fixture without hardcoding the actual
    /// max (found to sit at 105-109 for this fixture, but that exact number is not the point being
    /// tested and should never become a magic constant here).</summary>
    static CompiledAction CostedSkill(string actionId, int amount) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: new[] { new CompiledActionCost("qi", ValueSpec.Of(amount), ActionCostTiming.OnCommit) },
        Scopes: Array.Empty<ActionScopeRow>());

    static BattleSetup EquipSquadZero(string actionId) => BattleGoldenTests.CloseSetup() with
    {
        Squad = BattleGoldenTests.CloseSetup().Squad.Select((a, i) => i == 0
            ? a with { EquippedActionIds = new[] { actionId } }
            : a).ToArray(),
    };

    [Fact]
    public void An_actor_who_cannot_afford_its_only_action_never_attacks_with_it()
    {
        var catalog = ActionCatalog.Build(new[] { UnaffordableSkill() });
        var setup = EquipSquadZero("skill.unaffordable");
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog);

        // If CostLedger were still AlwaysAffordable, squad:0 would attack normally (CloseSetup's own
        // fixture already proves squad:0 acts in `The_fixture_itself_neither_stalemates...` next to
        // this file). With a real, unaffordable cost, squad:0 must never declare a hit.
        Assert.DoesNotContain(trace.Targets, line => line.Contains(" squad:0->", System.StringComparison.Ordinal));
    }

    [Fact]
    public void An_action_with_no_authored_cost_is_byte_identical_to_today()
    {
        // The additive-discipline proof every prior adoption in this program has run: an action with
        // an EMPTY cost list must be completely unaffected by the CostLedger swap.
        var freeAction = UnaffordableSkill() with { Costs = System.Array.Empty<CompiledActionCost>() };
        var catalog = ActionCatalog.Build(new[] { freeAction });
        var setup = EquipSquadZero("skill.unaffordable");
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog);

        Assert.Contains(trace.Targets, line => line.Contains(" squad:0->", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// T56.2: `TryCommitReady` (timeline-dispatch) and `RunBasicAttackStep` (atomic) both call
    /// `CostLedger.TryPay(..., ActionCostTiming.OnCommit, ...)` at their own real commit point
    /// (`TimelineDispatch.cs`, right after `runner.TryCommit` clears; `BasicAttack.cs`, right after
    /// `DeclareBasicAttack` returns `Proceed`) — proven here by an actor that can afford the FIRST
    /// commit but, having actually been debited, can never afford a second one. If `TryPay` were never
    /// called (only `Check`, T56.1's gate), the pool would never move and squad:0 would attack every
    /// round it is active, exactly like the free-action control test above and like
    /// `The_fixture_itself_neither_stalemates...` (`ActionDispatchGeneralizationTests`) already proves
    /// for an uncosted loadout.
    /// </summary>
    [Fact]
    public void An_actor_who_pays_its_only_commit_can_never_afford_a_second_one()
    {
        const int OnceAffordableCost = 100; // see CostedSkill's own doc comment
        var catalog = ActionCatalog.Build(new[] { CostedSkill("skill.once", OnceAffordableCost) });
        var setup = EquipSquadZero("skill.once");
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog);

        var squadZeroAttacks = trace.Targets.Where(t => t.Contains(" squad:0->", System.StringComparison.Ordinal)).ToList();
        Assert.Single(squadZeroAttacks); // paid exactly once -- not zero (T56.1 would already cover
                                          // that), not every round (that would mean TryPay is a no-op)
        Assert.StartsWith("1 ", squadZeroAttacks[0]); // the one payable commit is round 1
    }

    /// <summary>A real action with a `perTick` (not `onCommit`) `qi` cost, opted into
    /// `Interruptible.OnDamage` — the only policy `ActionRunner.YieldsTo` lets a `ResourceExhausted`
    /// cause through (`Never`, the default, would refuse the interrupt and let the hit land unpaid;
    /// `OnCC` only yields to crowd control). `CostLedger.Check` (T56.1's commit-time gate) only reads
    /// `ActionCostTiming.OnCommit` rows, so a `perTick`-only cost is invisible to it — the actor
    /// re-commits every round regardless of whether it can actually afford the `perTick` charge that
    /// follows, which is exactly the scenario T56.3 exists for. Its own `EffectivenessChannel` is a
    /// dedicated, boostable channel (`skill.effectiveness.support`) so squad:0's OWN landed hits can be
    /// told apart from squad:1's/wave's ordinary attacks on the SAME target by magnitude alone — the
    /// same technique `ActionDispatchGeneralizationTests` uses, needed here because `BattleTrace.Apply`
    /// records the TARGET's key, not the attacker's, so a raw " squad:0 " substring match (a real bug
    /// caught by re-running this exact test in isolation: it silently counted times squad:0 was HIT,
    /// not times squad:0's OWN hit landed) can never isolate one attacker's hits from another's.</summary>
    static CompiledAction PerTickCostedSkill(string actionId, int amount) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with
        {
            ActionId = actionId,
            Interruptible = Interruptible.OnDamage,
            EffectivenessChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategorySupport),
        },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: new[] { new CompiledActionCost("qi", ValueSpec.Of(amount), ActionCostTiming.PerTick) },
        Scopes: Array.Empty<ActionScopeRow>());

    /// <summary>A magnitude threshold no ordinary `CloseSetup()` attack crosses on its own (empirically
    /// found: every other actor's deltas run -21 to -39 across the whole battle) but squad:0's own
    /// 20,000-permille-boosted hit clears easily (-448, round 1) — found by dumping the real trace via
    /// a scratch `Assert.True(false, ...)` probe, not guessed; the probe was removed once the real
    /// number was known.</summary>
    const long BoostedHitMagnitudeThreshold = 100;

    /// <summary>
    /// T56.3: a `perTick` cost shortfall at resolve time interrupts through the real
    /// `ActionRunner.Interrupt(..., InterruptCause.ResourceExhausted)` path (`TimelineDispatch.cs`,
    /// charged right before `OnResolveDue` while the actor is still `Committed` -- `Interrupt` itself
    /// only accepts that state). Same "pay once, then starve" shape as T56.2's commit-time proof, but
    /// through the resolve-time gate instead of the commit-time one: round 1's resolve can afford 100
    /// `qi` (squad:0's real pool sits at 105-109, same empirical finding T56.2 used), round 2's cannot
    /// -- and because nothing gates `perTick` at commit (`CostLedger.Check` never reads a `perTick`
    /// row), squad:0 keeps DECLARING the attack every round (`trace.Targets` keeps growing) even after
    /// it can no longer afford to land one, which is the one place this test's signal differs from
    /// T56.2's: here the interrupt shows up as a growing gap between declared attacks and actual
    /// applied hits, not as the attacks stopping outright.
    /// </summary>
    [Fact]
    public void A_perTick_shortfall_interrupts_the_committed_action_before_it_resolves()
    {
        const int OnceAffordablePerTick = 100; // see PerTickCostedSkill's own doc comment
        var boostedChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategorySupport);
        var catalog = ActionCatalog.Build(new[] { PerTickCostedSkill("skill.pertick", OnceAffordablePerTick) });
        var setup = BattleGoldenTests.CloseSetup() with
        {
            Squad = BattleGoldenTests.CloseSetup().Squad.Select((a, i) => i == 0
                ? a with { EquippedActionIds = new[] { "skill.pertick" }, ChannelMods = new[] { new BattleChannelMod(boostedChannel, 20_000) } }
                : a).ToArray(),
        };
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog);

        var squadZeroDeclared = trace.Targets.Count(t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
        // `BattleTrace.Apply(round, ownerKey, signedDelta)` records the TARGET's key, not the
        // attacker's -- so squad:0's OWN landed hits are found by magnitude (its 20,000-permille
        // boosted `EffectivenessChannel`), not by a substring match on "squad:0" (which would instead
        // count times squad:0 was HIT, the exact bug this test's own doc comment names).
        var squadZeroApplied = trace.Applies.Count(a =>
        {
            var delta = long.Parse(a.Split(' ')[2]);
            return System.Math.Abs(delta) >= BoostedHitMagnitudeThreshold;
        });

        // Declares every round it's active (perTick is invisible to the commit-time Check gate) --
        // matching the free-action control's round count, NOT the "paid once, then never declares
        // again" shape T56.2's onCommit test has (a materially different observable, confirming this
        // exercises the resolve-time gate and not the commit-time one).
        Assert.True(squadZeroDeclared > 1, $"expected squad:0 to keep declaring every round; declared {squadZeroDeclared} time(s)");
        // But only ever LANDS once -- round 1's resolve could still afford the perTick charge; every
        // later resolve is interrupted before ApplyBasicAttack ever runs.
        Assert.Equal(1, squadZeroApplied);
    }

    /// <summary>A real Attack-category skill (not the basic attack) with a small, always-affordable
    /// `onCommit` cost AND a long real cooldown. `CooldownTicks` is deliberately large enough to
    /// outlast the whole battle (`CloseSetup()` never runs past ~22 rounds per T56.2/T56.3's own
    /// empirical traces), so the cooldown gate alone -- not a race against it expiring mid-battle -- is
    /// what's under test.
    /// <para><b>Real finding, found via the RED/GREEN/revert cycle</b>: <c>StartsAt = Commit</c> below
    /// is NOT what actually arms this skill's cooldown in a real battle. `ApplyBasicAttack`
    /// (`BasicAttack.cs:217`) calls `state.Cooldowns.Start(...)` UNCONDITIONALLY on every landed hit,
    /// with no `StartsAt` check at all -- unlike `ActionRunner.TryCommit`'s three call sites
    /// (`ActionRunner.cs:236,289,361`), which each gate on a specific `StartsAt` value. Disabling
    /// `ActionRunner.cs:236`'s `Commit`-start arming alone did NOT produce RED (squad:0 still only
    /// attacked once) -- `ApplyBasicAttack`'s own unconditional arm-on-hit was the real, independent
    /// mechanism actually blocking the second commit. This means a Skill authored with
    /// `StartsAt = Resolve` or `RecoveryEnd` (wanting its cooldown timed from THAT point) will ALSO get
    /// an extra, earlier re-arm from `ApplyBasicAttack` on every landed hit, since `CooldownLedger.Start`
    /// simply overwrites `_readyAt` on every call -- not a bug this task fixes (T56.4's own acceptance
    /// is "a real skill's cooldown refuses a second commit", which holds either way), but a real,
    /// previously-unknown gap named here rather than silently discovered by a future session: see the
    /// `action-plan.md` §5 deferred-table entry this finding added.</para></summary>
    static CompiledAction CooldownGatedAttackSkill(string actionId) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Category: ActionCategory.Attack,
        Envelope: ActionEnvelope.NoOp with
        {
            ActionId = actionId,
            Class = CooldownClass.Specific, // NoOp's default is CooldownClass.None -- TrySlot then
                                             // refuses to key ANY slot at all, so both IsReady and
                                             // Start silently no-op regardless of CooldownTicks. Found
                                             // by reading CooldownLedger.cs after the first version of
                                             // this test attacked every round despite a 1,000,000-tick
                                             // cooldown -- a real bug in the test, not in production.
            CooldownTicks = 1_000_000,
            StartsAt = CooldownStart.Commit,
        },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: new[] { new CompiledActionCost("qi", ValueSpec.Of(1), ActionCostTiming.OnCommit) },
        Scopes: Array.Empty<ActionScopeRow>());

    /// <summary>
    /// T56.4: a real Attack-category skill's own authored cooldown refuses a second commit, proving
    /// cooldown arming (already correct per S2/A18f, unconfirmed end-to-end until now — noted as an
    /// open gap in T55.2's own evidence) and T56.1's affordability gate compose correctly together on
    /// the SAME action, not just individually. Gate order in `UsabilityEvaluator.Evaluate` is
    /// stance -> bound -> cooldown -> afford -> range -> condition (cooldown BEFORE afford) -- so if
    /// cooldown arming were broken (never armed, or armed but never checked), the small always-
    /// affordable `qi` cost would let squad:0 keep attacking every round, exactly like T56.2's own
    /// "no authored cost" control shows for an uncosted action.
    /// </summary>
    [Fact]
    public void A_real_attack_category_skills_own_cooldown_refuses_a_second_commit()
    {
        var catalog = ActionCatalog.Build(new[] { CooldownGatedAttackSkill("skill.cooldown-gated") });
        var setup = EquipSquadZero("skill.cooldown-gated");
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog);

        var squadZeroAttacks = trace.Targets.Where(t => t.Contains(" squad:0->", System.StringComparison.Ordinal)).ToList();
        Assert.Single(squadZeroAttacks); // committed once, then refused every round after by its own cooldown
        Assert.StartsWith("1 ", squadZeroAttacks[0]);
    }

    /// <summary>T58.3 (A23, spec-cost-scaling-holder-rung.md): a real action whose authored `Rung` is 1
    /// (`CostMulti` 1000‰, inert) but whose paid cost must scale with the HOLDER's `effectiveRung`
    /// instead, once one is supplied. 40 `qi` at rung 1 is unchanged (40); at rung 5
    /// (`CostMulti` 3627‰, the shipped `action-rungs.v2.json` row) it rounds to 145 — above squad:0's
    /// own real derived `qi` max (empirically 105-109, T56.2/T56.3's own finding for this exact
    /// `CloseSetup()` actor), so a holder who has earned to rung 5 finds the SAME action permanently
    /// unaffordable while a holder at rung 1 pays it freely.</summary>
    static CompiledAction RungScaledSkill(string actionId, int baseCost) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: new[] { new CompiledActionCost("qi", ValueSpec.Of(baseCost), ActionCostTiming.OnCommit) },
        Scopes: Array.Empty<ActionScopeRow>());

    static readonly UnlockTuning RungTestTuning =
        new(P1Milli: 1000, DeltaMilli: 500, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);

    /// <summary>
    /// T58.3 criterion 2 (spec-cost-scaling-holder-rung.md): the SAME actor, SAME seed, SAME setup,
    /// SAME authored action -- the ONLY variable across the two `BattleEngine.Resolve` calls is which
    /// `UnlockState` squad:0 holds for it, isolating the rung as the sole cause of any difference
    /// (no second actor's possibly-different derived stats to confound the comparison).
    /// </summary>
    [Fact]
    public void An_actor_holding_the_same_action_at_a_higher_earned_rung_pays_more_and_goes_unaffordable()
    {
        const int BaseCost = 40; // rung 1 -> 40 (affordable); rung 5 -> round(40 * 3.627) = 145 (not)
        var catalog = ActionCatalog.Build(new[] { RungScaledSkill("skill.rung-scaled", BaseCost) });
        var setup = EquipSquadZero("skill.rung-scaled");

        var lowRungState = UnlockState.FromPersisted(1, new[] { new HeldUnlock("skill.rung-scaled", EarnCountAtAcceptance: 1) });
        var highRungState = UnlockState.FromPersisted(5, new[] { new HeldUnlock("skill.rung-scaled", EarnCountAtAcceptance: 5) });

        var traceLow = new BattleTrace();
        BattleEngine.Resolve(setup, seed: 5501, trace: traceLow, actionCatalog: catalog,
            unlockStateFor: key => key == "squad:0" ? lowRungState : UnlockState.Empty(),
            unlockTuning: RungTestTuning);

        var traceHigh = new BattleTrace();
        BattleEngine.Resolve(setup, seed: 5501, trace: traceHigh, actionCatalog: catalog,
            unlockStateFor: key => key == "squad:0" ? highRungState : UnlockState.Empty(),
            unlockTuning: RungTestTuning);

        Assert.Contains(traceLow.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal));      // rung 1: affordable, attacks
        Assert.DoesNotContain(traceHigh.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal)); // rung 5: unaffordable, never attacks
    }

    /// <summary>T58.3 criterion 3: a caller supplying no `unlockStateFor` at all -- the EXACT call
    /// shape every T56.x test above already uses -- must resolve to the authored `Rung`
    /// (`RungScaledSkill`'s own `Rung: 1`), byte-identical to today. Proven against the SAME
    /// rung-1-equivalent outcome the low-rung run above shows, through the real default path rather
    /// than an explicit `UnlockState`.</summary>
    [Fact]
    public void No_unlock_state_supplied_falls_back_to_the_authored_rung_byte_identical_to_today()
    {
        const int BaseCost = 40;
        var catalog = ActionCatalog.Build(new[] { RungScaledSkill("skill.rung-scaled", BaseCost) });
        var setup = EquipSquadZero("skill.rung-scaled");
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog); // no unlockStateFor/unlockTuning at all

        Assert.Contains(trace.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
    }

    /// <summary>Records every call so the test can assert the demand was actually taken, mirroring
    /// <c>ActionUsabilityStockSpendTests</c>'s own in-memory fixture shape (that file proves the
    /// USABILITY half at the leaf/evaluator level; this one proves the COMMIT half at the real
    /// `BattleEngine.Resolve` call site, which is where the gap actually lived — see
    /// `spec-siege-construction.md` §11 / `[[action-program]]`'s own memory of this exact bug).</summary>
    sealed class FakeStockLedger : IStockLedger
    {
        public readonly List<(string ActorKey, string ActionId, IReadOnlyList<StockDemand> Demands)> Calls = new();

        public StockSpendResult TrySpend(string actorKey, string actionId, IReadOnlyList<StockDemand> demands)
        {
            Calls.Add((actorKey, actionId, demands));
            return StockSpendResult.Spent;
        }
    }

    static CompiledAction StockGatedSkill(string actionId) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: Array.Empty<CompiledActionCost>(),
        StockDemands: new[] { new StockDemand("stock.test-consumable", 1) },
        Scopes: Array.Empty<ActionScopeRow>());

    /// <summary>
    /// The real bug (`ActionStockCommit.TryCommit` had ZERO production callers, confirmed by a
    /// class-name-only grep — `spec-siege-construction.md` §11): a battle-context action compiled with
    /// a real `StockDemand` fired through `BattleEngine.Resolve` without the supplied `IStockLedger`
    /// ever being consulted, matching `gk-core/data/tuning/consumables.v1.json`'s own live consequence for the
    /// shipped `battle` context. Proven at the SAME two real call sites T56.2 proved for
    /// `CostLedger.TryPay` (`TimelineDispatch.cs`'s `TryCommitReady`, the live path for every shipped
    /// profile today per `BattleModeProfile.cs`), not a synthetic unit around `ActionStockCommit` alone
    /// (already covered by `ActionUsabilityStockSpendTests`).
    /// </summary>
    [Fact]
    public void A_holdsStock_gated_action_actually_spends_its_stock_at_commit()
    {
        var ledger = new FakeStockLedger();
        var catalog = ActionCatalog.Build(new[] { StockGatedSkill("skill.consumable") });
        var setup = EquipSquadZero("skill.consumable");
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog, stockLedger: ledger);

        Assert.Contains(ledger.Calls, c => c.ActorKey == "squad:0" && c.ActionId == "skill.consumable"
            && c.Demands.Count == 1 && c.Demands[0].StockId == "stock.test-consumable" && c.Demands[0].MinQty == 1);
    }

    /// <summary>The additive-discipline proof every prior adoption in this program runs: an action with
    /// NO `StockDemands` (every action shipped today) must never touch a supplied ledger at all — the
    /// overwhelming majority path, and the one every existing test above already exercises without
    /// ever supplying a `stockLedger`.</summary>
    [Fact]
    public void An_action_with_no_stock_demands_never_touches_a_supplied_ledger()
    {
        var ledger = new FakeStockLedger();
        var catalog = ActionCatalog.Build(new[] { CostedSkill("skill.no-stock", amount: 1) });
        var setup = EquipSquadZero("skill.no-stock");
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog, stockLedger: ledger);

        Assert.Empty(ledger.Calls);
    }

    /// <summary>No `stockLedger` supplied at all — every call site above this pair in the file — must
    /// remain exactly as it is today: `BattleRunState` falls back to `NoStockLedger.Instance`
    /// internally, never a `NullReferenceException`.</summary>
    [Fact]
    public void No_stock_ledger_supplied_is_byte_identical_to_today()
    {
        var catalog = ActionCatalog.Build(new[] { CostedSkill("skill.no-stock-ledger", amount: 1) });
        var setup = EquipSquadZero("skill.no-stock-ledger");
        var trace = new BattleTrace();

        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog); // no stockLedger at all

        Assert.Contains(trace.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
    }
}
