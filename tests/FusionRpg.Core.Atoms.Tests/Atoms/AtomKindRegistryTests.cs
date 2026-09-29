using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E1 acceptance. The count guards exist so growing the vocabulary requires editing a test and
/// noticing — a closed vocabulary that drifts is just an open one with extra steps.
/// </summary>
public class AtomKindRegistryTests
{
    static Dictionary<string, object?> P(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Vocabulary_is_closed_at_eighteen_kinds_and_nine_attach_points()
    {
        // E35 (spec-match-modify.md §2.1), E36 (spec-wave-control.md §2.1), E37
        // (spec-projectile-control.md §2b), then E41 (spec-ui-attach-point.md §2a): each states only
        // its own +1 delta over the state before it (12 kinds/5 attach points, then 13/6, then 14/6,
        // then 15/6 — E37 adds no attach point, its bullet.modify reuses the existing Board — then
        // 16/7 — E41's ui.present is the second Wave 8 module to add its own attach point, Ui) — the
        // guard itself is a self-consistency check (Const == BuiltCount), never a copied literal, so a
        // sibling Wave 8 module landing its own kind/attach point moves these two consts again without
        // this test needing to guess the wave's combined end state.
        //
        // base-defense `siege-construction` (decision 27, 2026-09-06): 16/7 -> 17/8 with
        // structure.place / AttachPoint.Siege — the tactical-siege-board mirror of Board's Lawn-only
        // reach (AttachPoint.Siege's own doc comment).
        //
        // passive-tree `element-conversion` (D56, spec-element-conversion.md §2a/§2b, 2026-09-07):
        // 17/8 -> 18/9 with element.convert / AttachPoint.Element.
        Assert.Equal(AtomKindRegistry.KindCount, AtomKindRegistry.All.Count);
        Assert.Equal(AtomKindRegistry.AttachPointCount, Enum.GetValues<AttachPoint>().Length);
    }

    [Fact]
    public void Rejection_reasons_are_the_closed_list_of_thirty_three_plus_the_namespaced_catch_all()
    {
        // definitions.md §10 fixes the list at 33 (plus None). item-ideal.md §2b.1 adds exactly one
        // more, permanently: ContentRuleViolated, the single namespaced catch-all every later lane
        // raises instead of minting its own code (ContentRuleNamespaces.Register). 33 + None +
        // ContentRuleViolated = 35. It is the operator-facing error surface: a code added without
        // review is a code no runbook explains, and ContentRuleViolated's own point is that nothing
        // after it may add a 36th.
        var reasons = Enum.GetValues<AtomRejectionReason>();

        // Canonical site (population-pin SE3.3, 2026-09-19): DropVolumeCorpusTests reads this same
        // live enum length instead of a second literal.
        // pin: closed-vocabulary AtomRejectionReason — a new rejection reason is a reviewed change
        Assert.Equal(35, reasons.Length);
        Assert.Contains(AtomRejectionReason.None, reasons);
        Assert.Contains(AtomRejectionReason.ContentRuleViolated, reasons);
    }

    [Fact]
    public void Every_kind_declares_a_runtime_a_trigger_and_a_power_category()
    {
        // Kinds with no executor in ANY runtime. Listing them here is the point: a kind may sit in
        // the vocabulary ahead of its consumer, but it must be quarantined (all-None, so binds are
        // rejected) and named in this set, never advertising support it does not have.
        //
        // First occupant since 2026-08-23 (`stat.derived` was the last, cleared by E12's own first
        // consumer): `element.convert` (D56, spec-element-conversion.md) validates its own params but
        // has no real reader yet — the combat-dispatch call site is a genuine, undecided engineering
        // choice (spec §2b), not built here. Remove it the day a real Lawn/Battle/Sim reader lands and
        // the matrix moves off all-None, per-runtime, exactly as `stat.derived` itself was re-opened.
        var awaitingConsumer = new[] { "element.convert" };

        // Permanent modifiers are not event-driven, so they declare no trigger (definitions.md §14.2).
        // stat.modify moved out of this set 2026-08-28 (A18e) -- it is no longer PURELY permanent, it
        // may ALSO be triggered (contributing from first fire onward); TriggerOptional (AtomKind.cs)
        // is what keeps its OWN no-trigger case still legal despite Triggers now being non-empty.
        //
        // E37 (spec-projectile-control.md §2b.1): "bullet.modify" added deliberately, named here per
        // that spec's own MANDATORY instruction -- a bullet.modify grant's PRESENCE is the effect, read
        // at Bullet.InitData (CheatPrefixes.BulletInitCheat via GrantedBulletModifyAtomReader), the
        // same resolved-read shape stat.derived uses. It has no event to fire on, so giving it a
        // trigger just to keep this test green would be the status.expose.* defect this same spec
        // names -- a declared trigger nothing ever raises.
        //
        // passive-tree `element-conversion` (D56, spec-element-conversion.md §4): "element.convert"
        // is the third member -- a permanent reweighting of the actor's own ElementPayload, present
        // for as long as the node is owned, the same stat.derived/bullet.modify shape.
        var permanentModifiers = new[] { "stat.derived", "bullet.modify", "element.convert" };

        // E41 (spec-ui-attach-point.md §2b.1): a SEPARATE exemption set from permanentModifiers above
        // — a different axis entirely. permanentModifiers is about TRIGGERS (a grant's presence is
        // the effect, so it declares none); cosmetic is about PRICING CATEGORY (a present writes no
        // state, so a category on it would let a floater be budgeted as if it contributed real
        // power). Conflating the two sets would blur what each one means — a kind could be cosmetic
        // without being a permanent modifier (ui.present carries triggers, AllTriggers) or vice versa.
        var cosmetic = new[] { "ui.present" };

        foreach (var kind in AtomKindRegistry.All)
        {
            var anyRuntime = kind.SupportIn(RuntimeId.Lawn) != RuntimeState.None
                             || kind.SupportIn(RuntimeId.Battle) != RuntimeState.None
                             || kind.SupportIn(RuntimeId.Sim) != RuntimeState.None;

            if (awaitingConsumer.Contains(kind.KindId))
                Assert.False(anyRuntime, $"{kind.KindId} is listed as awaiting a consumer but claims a runtime");
            else
                Assert.True(anyRuntime, $"{kind.KindId} supports no runtime and is not listed as awaiting one");

            if (permanentModifiers.Contains(kind.KindId))
                Assert.Empty(kind.Triggers);
            else
                Assert.True(kind.Triggers.Count > 0, $"{kind.KindId} allows no trigger");

            if (cosmetic.Contains(kind.KindId))
                Assert.True(kind.Categories == PowerCategory.None,
                    $"{kind.KindId} is cosmetic (writes no state) and must price to no category");
            else
                Assert.True(kind.Categories != PowerCategory.None, $"{kind.KindId} prices to no category");
        }
    }

    [Fact]
    public void Trigger_vocabulary_is_closed_at_eight()
    {
        // A18b (spec-on-activate-trigger.md): 7 -> 8 with OnActivate. Still a self-consistency check,
        // not a hardcoded literal -- TriggerCount and AtomTriggers.All.Length moved together.
        Assert.Equal(AtomKindRegistry.TriggerCount, AtomTriggers.All.Length);
        Assert.Equal(AtomTriggers.All.Length, AtomTriggers.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_kinds_triggers_are_drawn_from_the_eight()
    {
        foreach (var kind in AtomKindRegistry.All)
            foreach (var t in kind.Triggers)
                Assert.True(AtomTriggers.IsKnown(t), $"{kind.KindId} names unknown trigger {t}");
    }

    [Fact]
    public void Unknown_trigger_and_disallowed_trigger_reject_differently()
    {
        // E34 (spec-trigger-vocabulary.md) made "OnWave" a real, known trigger -- this placeholder
        // moved to a string that stays unknown so this test keeps testing UnknownTrigger rather than
        // silently starting to test TriggerNotAllowed instead.
        Assert.Equal(AtomRejectionReason.UnknownTrigger,
            AtomKindRegistry.ValidateTrigger("stat.modify", "OnNope").Reason);

        // stat.derived is STILL a pure permanent modifier (A18e only widened stat.modify): it carries
        // no trigger at all. Not OnDamageDealt...
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("stat.derived", AtomTriggers.OnDamageDealt).Reason);

        // ...and not OnGranted either, for EITHER kind (stat.modify included, despite its own
        // AllTriggers widen -- OnGranted/OnRemoved are the separate Lifecycle array, never part of
        // AllTriggers). OnGranted/OnRemoved are runtime lifecycle states, not authorable triggers
        // (definitions.md §14.2) — the bag injects the revert itself. Allowing content to name only
        // the OnGranted half was how a permanent buff could leak.
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("stat.modify", AtomTriggers.OnGranted).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("stat.derived", AtomTriggers.OnGranted).Reason);

        // stat.modify's own new case: OnDamageDealt (part of AllTriggers since A18e) is now allowed.
        Assert.True(AtomKindRegistry.ValidateTrigger("stat.modify", AtomTriggers.OnDamageDealt).IsOk);

        Assert.True(AtomKindRegistry.ValidateTrigger("resource.delta", AtomTriggers.OnTimer).IsOk);
    }

    /// <summary>
    /// A18b (spec-on-activate-trigger.md §1): the one-line `AllTriggers` widen reached exactly three
    /// kinds at first (A18c/d's own) — this test's own original doc comment predicted `stat.modify`
    /// would move separately, "A18e's own separate call." A18e (2026-08-28) made that call: updated
    /// here to the post-A18e state (four kinds now), not a stale prediction left unfulfilled. Board
    /// kinds stay refused (H3: Battle is `RuntimeState.None` for all of them regardless of trigger).
    /// </summary>
    [Fact]
    public void OnActivate_reaches_exactly_resource_delta_status_apply_shield_grant_stat_modify_and_structure_place()
    {
        Assert.True(AtomKindRegistry.ValidateTrigger("resource.delta", AtomTriggers.OnActivate).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("status.apply", AtomTriggers.OnActivate).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("shield.grant", AtomTriggers.OnActivate).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("stat.modify", AtomTriggers.OnActivate).IsOk);
        // base-defense `siege-construction` (decision 27, 2026-09-06): the fifth -- construction is
        // exactly "an actor's own decision to act", the same category BasicAttack's own OnActivate fire
        // already establishes, never a board/economy event.
        Assert.True(AtomKindRegistry.ValidateTrigger("structure.place", AtomTriggers.OnActivate).IsOk);

        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("stat.derived", AtomTriggers.OnActivate).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("spawn.entity", AtomTriggers.OnActivate).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("board.action", AtomTriggers.OnActivate).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("grid.spawn", AtomTriggers.OnActivate).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("grid.clear", AtomTriggers.OnActivate).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("box.set", AtomTriggers.OnActivate).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("resource.economy", AtomTriggers.OnActivate).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("status.clear", AtomTriggers.OnActivate).Reason);
    }

    // Four states, not three. PlanOnly must never read as Full, or sim silently accepts
    // bindings it cannot execute - the exact no-op this layer exists to prevent.
    [Fact]
    public void Runtime_support_carries_four_distinct_states()
    {
        var statModify = AtomKindRegistry.Get("stat.modify")!;
        Assert.Equal(RuntimeState.Full, statModify.SupportIn(RuntimeId.Lawn));
        // Battle moved None -> Full with A18e (2026-08-28) -- BattleStatModifierLedger. spawn.entity
        // (below, Battle_support_is_narrow_and_honest) is this matrix's own live None example now.
        Assert.Equal(RuntimeState.Full, statModify.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.PlanOnly, statModify.SupportIn(RuntimeId.Sim));

        // status.apply in battle is Full since A18d (2026-08-28): BattleEffectSink's own
        // ExecApplyStatus branch means a real FA2 path exists now, not just scripted InitialStatuses.
        Assert.Equal(RuntimeState.Full,
            AtomKindRegistry.Get("status.apply")!.SupportIn(RuntimeId.Battle));
    }

    [Fact]
    public void Unknown_kind_is_a_rejection_not_a_skip()
    {
        var r = AtomKindRegistry.Validate("stat.nope", P());
        Assert.Equal(AtomRejectionReason.UnknownKind, r.Reason);
    }

    [Fact]
    public void Unknown_param_rejects()
    {
        var r = AtomKindRegistry.Validate("stat.modify",
            P(("channel", "atk"), ("op", "flat"), ("amount", 10), ("wat", 1)));
        Assert.Equal(AtomRejectionReason.UnknownParam, r.Reason);
    }

    // G7: ExecModifyStat defaults a missing channel to "atk". A malformed atom would silently
    // buff attack, which is the worst possible failure mode for a content bug.
    [Fact]
    public void Missing_channel_rejects_rather_than_defaulting_to_atk()
    {
        var r = AtomKindRegistry.Validate("stat.modify", P(("op", "flat"), ("amount", 10)));
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
        Assert.Contains("channel", r.Detail);
    }

    // E28 fix #5 (spec-param-parity.md §3 row 5): atk now reaches the spawned body for plant/zombie
    // — DebugActions.ApplyAbsoluteProps already had the plant hook (P-ATK) and the zombie cheat id
    // (Z-ATK) already existed, both just never read from the payload.
    // E37 (spec-projectile-control.md §2a): widened to bullets too — a spawned bullet carries damage
    // on the projectile itself (Bullet.Damage, not a modifier-bag hook), and the sink's SpawnBulletOnce
    // now translates atk -> damage at the payload boundary. "Bullets have no such hook" was the wiring
    // gap this module closes, not a permanent limitation — atk is honoured for all three kinds now.
    [Theory]
    [InlineData("zombie", true)]
    [InlineData("plant", true)]
    [InlineData("bullet", true)]
    public void Spawn_atk_is_honoured_for_every_kind(string kind, bool shouldPass)
    {
        var r = AtomKindRegistry.Validate("spawn.entity",
            P(("kind", kind), ("typeId", 0), ("atk", 500)));

        if (shouldPass) Assert.True(r.IsOk, r.ToString());
        else Assert.Equal(AtomRejectionReason.ParamNotHonoured, r.Reason);
    }

    // G1 again, the conditional half: hp is forwarded for zombies and dropped for plants.
    [Theory]
    [InlineData("zombie", true)]
    [InlineData("plant", false)]
    [InlineData("bullet", false)]
    public void Spawn_hp_is_only_honoured_for_zombies(string kind, bool shouldPass)
    {
        var r = AtomKindRegistry.Validate("spawn.entity",
            P(("kind", kind), ("typeId", 0), ("hp", 500)));

        if (shouldPass) Assert.True(r.IsOk, r.ToString());
        else Assert.Equal(AtomRejectionReason.ParamNotHonoured, r.Reason);
    }

    [Fact]
    public void Spawn_col_is_only_honoured_for_plants()
    {
        Assert.True(AtomKindRegistry.Validate("spawn.entity",
            P(("kind", "plant"), ("col", 3))).IsOk);

        Assert.Equal(AtomRejectionReason.ParamNotHonoured, AtomKindRegistry.Validate("spawn.entity",
            P(("kind", "zombie"), ("col", 3))).Reason);
    }

    // E28 fix #7 (spec-param-parity.md §3 row 7): cells[] now validates — ExecSetBox paints every
    // listed cell instead of refusing the param outright.
    [Fact]
    public void BoxSet_cells_validates_now_that_the_executor_paints_every_listed_cell()
    {
        var r = AtomKindRegistry.Validate("box.set",
            P(("boxType", 2), ("cells", new[] { 1, 2 })));
        Assert.True(r.IsOk, r.ToString());
    }

    // G4 CLOSED 2026-08-22. capPerMatch sat in the FA9 allowlist implemented nowhere, so E1 refused
    // it at load. E15 shipped the counter (AtomRunner + RunnerState + CapPerMatchTests), and leaving
    // the refusal in place would have made the feature unauthorable by the content it was built for
    // — a guard outliving its reason is just a silently dead feature.
    [Fact]
    public void Economy_capPerMatch_validates_now_that_the_runner_owns_it()
    {
        var r = AtomKindRegistry.Validate("resource.economy",
            P(("currency", "sun"), ("op", "add"), ("amount", 25), ("capPerMatch", 3)));

        Assert.True(r.IsOk, r.ToString());
        Assert.Null(AtomKindRegistry.Get("resource.economy")!.Params.Defs
            .First(d => d.Name == "capPerMatch").NotImplementedNote);
    }

    // G5: an empty target means "every zombie on the board" — and D7 established that this CANNOT be
    // closed here. FA2 has no `target` param at all; the target comes from ResolveStatusTargetPtr(ctx),
    // i.e. from the event, at runtime. A required-key check would have declared a key the executor
    // never reads, so content would validate and the hole would stay open. The fix belongs to whoever
    // guards the FindObjectsOfType<Zombie>() loop, not to load-time validation.
    [Fact]
    public void StatusApply_does_not_pretend_to_close_G5_with_a_param()
    {
        Assert.False(
            AtomKindRegistry.Get("status.apply")!.Params.Defs.Any(d => d.Name == "target"),
            "FA2 carries no target param; declaring one would validate a key nothing reads");

        // The FA2 shape that really is authorable validates.
        Assert.True(AtomKindRegistry.Validate("status.apply", P(("status", "butter"))).IsOk);
    }

    // The counterpart: a missing `status` is a real load-time refusal, because FA2 does read it.
    [Fact]
    public void StatusApply_requires_the_status_name_FA2_reads()
    {
        var r = AtomKindRegistry.Validate("status.apply", P(("level", 1)));
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
        Assert.Contains("status", r.Detail);
    }

    [Fact]
    public void Canonical_samples_validate()
    {
        Assert.True(AtomKindRegistry.Validate("stat.modify",
            P(("channel", "maxHp"), ("op", "flat"), ("amount", 10))).IsOk);

        Assert.True(AtomKindRegistry.Validate("stat.derived",
            P(("channel", "combat.power.fire"), ("op", "flat"), ("amount", 10))).IsOk);

        Assert.True(AtomKindRegistry.Validate("resource.delta",
            P(("amount", -50), ("element", "fire"))).IsOk);

        Assert.True(AtomKindRegistry.Validate("shield.grant",
            P(("amount", 80), ("element", "ice"))).IsOk);

        Assert.True(AtomKindRegistry.Validate("board.action", P(("op", "cherry"))).IsOk);
    }

    // Battle consumes FA10 only and never calls OnEvent - the matrix says so rather than pretending.
    [Fact]
    public void Battle_support_is_narrow_and_honest()
    {
        // D6, 2026-08-22: this test used to assert None for resource.delta/shield.grant.
        // Re-verification against BattleEngine at the time showed both unreachable FROM AN ATOM:
        // battle never granted and never called OnEvent, so no trigger could fire; BattleEffectHost
        // never set Bag.ShieldGate, so a shield grant skipped with "shield-runtime-missing". Battle
        // having a working FA10 sink was not the same as an atom being able to reach it.
        //
        // A18a-c (2026-08-28, action-map.md §12) grew that grant path for real: BattleRunState binds
        // real EffectGrantDtos (A18a), OnActivate/OnDamageDealt actually fire (A18b/A18c), and
        // Bag.Status/Bag.StatusRng/Bag.ShieldGate are all wired. Both cells move to Full for the same
        // reason stat.derived's did below -- the cell moved because the code did, proven by real
        // shipped defs in BattleResourceShieldGrantsTests (T46), not asserted from this test alone.
        Assert.Equal(RuntimeState.Full, AtomKindRegistry.Get("resource.delta")!.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.Full, AtomKindRegistry.Get("shield.grant")!.SupportIn(RuntimeId.Battle));

        // stat.derived RE-OPENED for battle 2026-08-23: E12 shipped the consumer. `BattleStatComposer`
        // reads bound stat.derived atoms at squad build, through `TraitAtomSource` — the same place it
        // read `ChannelMods` before. The cell moved because the code did, which is the only reason a
        // cell in this matrix is ever allowed to move.
        Assert.Equal(RuntimeState.Full, AtomKindRegistry.Get("stat.derived")!.SupportIn(RuntimeId.Battle));

        // stat.modify moved to Full with A18e (2026-08-28) -- BattleStatModifierLedger. spawn.entity/
        // board.action stay None: no A18 sub-module touches the Board attach point (H3's own boundary).
        Assert.Equal(RuntimeState.Full, AtomKindRegistry.Get("stat.modify")!.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.None, AtomKindRegistry.Get("spawn.entity")!.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.None, AtomKindRegistry.Get("board.action")!.SupportIn(RuntimeId.Battle));

        // status.apply moved to Full with A18d (2026-08-28) -- see the dedicated test above.
        Assert.Equal(RuntimeState.Full, AtomKindRegistry.Get("status.apply")!.SupportIn(RuntimeId.Battle));

        // Sim: shield.grant is one line of wiring away (SimEffectHost sets Bag.Status and Bag.UtcNow,
        // never Bag.ShieldGate), but until that line exists a bind would be a silent skip.
        Assert.Equal(RuntimeState.None, AtomKindRegistry.Get("shield.grant")!.SupportIn(RuntimeId.Sim));

        // stat.derived: LAWN opened 2026-08-30 (decisions.md "Derived-write lawn executor") because it
        // finally has a consumer -- `AtomDerivedSubsystem`, registered on the injector's ActorHub. The
        // rule this line has always enforced is unchanged: a runtime opens when, and only when, a
        // consumer exists for it.
        Assert.Equal(RuntimeState.Full, AtomKindRegistry.Get("stat.derived")!.SupportIn(RuntimeId.Lawn));
        // SIM opened 2026-09-06 (mechanism-wiring E5) to PARTIAL: it had a consumer (ActorDerivedLookup's
        // contribution fold, reached by SimEffectHost and FoundationHarness, plus a real
        // BindContext(RuntimeId.Sim) bind site) but the fold was a plain sum (ActorDerivedSnapshot.
        // OverlayAdd) with no notion of DerivedModifierOp, honouring Flat/Increased but not Replace/Flag.
        // sim-hub-parity (T15, 2026-09-13) closed that gap: Resolve now folds per channel via
        // DerivedComposer.ComposeChannelWithBaseline, the SAME op-aware fold every other runtime uses,
        // proven against the real DerivedComposer by
        // EffectOfflineKitTests.The_four_derived_ops_decide_Full_versus_Partial -- all four ops now match.
        Assert.Equal(RuntimeState.Full, AtomKindRegistry.Get("stat.derived")!.SupportIn(RuntimeId.Sim));
    }

    // The documented channel enum listed four keys effects cannot reach. Pin the real eight.
    [Fact]
    public void Primary_channels_are_the_real_twentythree_since_E38()
    {
        // It was eight, and this test asserted the three intervals were ABSENT — correctly, because
        // they were cheat-document keys written straight to the Unity field, bypassing the modifier
        // bag. The documented enum listed them anyway, which is how the gap survived. E16 promoted
        // them into real composed channels (8 -> 11), so the absent-assertions became wrong. E38
        // (spec-entity-fields-12plus.md) repeated the same promotion for twelve more (11 -> 23).
        // AtomKindRegistry.PrimaryChannels IS FusionRpg.Core.Stats.StatChannels.All (same array,
        // AtomKindRegistry.cs) -- StatChannels.All is pinned once, AtomCatalogSsotDriftTests
        // (population-pin SE3.3, 2026-09-19); reads that same live length, never a second literal.
        Assert.Equal(FusionRpg.Core.Stats.StatChannels.All.Length, AtomKindRegistry.PrimaryChannels.Length);
        Assert.Contains("defense", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("arm1Max", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("attackInterval", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("produceInterval", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("zombieSpeed", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("plantShield", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("attackCountdown", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("attackSpeedAdder", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("produceCountdown", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("plantSpeed", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("plantMoveSpeed", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("plantLevel", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("shootingLevel", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("armorFlat", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("takeDmgMultiplier", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("zombieSpeedCurrent", AtomKindRegistry.PrimaryChannels);
        Assert.Contains("zombieOriginSpeed", AtomKindRegistry.PrimaryChannels);
    }

    [Fact]
    public void The_registry_and_the_stat_layer_cannot_disagree_about_the_channel_list()
    {
        // Two hand-maintained copies of one list is how the documented nine came to differ from the
        // real eight in the first place.
        Assert.Equal(FusionRpg.Core.Stats.StatChannels.All, AtomKindRegistry.PrimaryChannels);
    }

    // G6: the registry declares PrimaryChannels and, until now, never read it — so `channel: "atkk"`
    // validated and then wrote nothing. Declared-but-unread is the same silent no-op as an unknown
    // leaf, which is the failure this module exists to refuse.
    [Fact]
    public void An_unknown_primary_channel_is_rejected()
    {
        var r = AtomKindRegistry.Validate("stat.modify",
            P(("channel", "atkk"), ("op", "flat"), ("amount", 10)));

        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void Every_real_primary_channel_is_accepted()
    {
        foreach (var channel in AtomKindRegistry.PrimaryChannels)
            Assert.True(
                AtomKindRegistry.Validate("stat.modify",
                    P(("channel", channel), ("op", "flat"), ("amount", 10))).IsOk,
                channel);
    }

    // ---- E35 (spec-match-modify.md) — match.modify's own field vocabulary and closed schema -------

    [Theory]
    [InlineData("zombieHealthMultiplier")]
    [InlineData("zombieDamageMultiplier")]
    [InlineData("zombieSpeedMultiplier")]
    [InlineData("zombieCountMultiplier")]
    [InlineData("zombieStartAmmor")]
    [InlineData("plantModifyMin")]
    [InlineData("plantModifyMax")]
    [InlineData("zombieModifyMin")]
    [InlineData("zombieModifyMax")]
    [InlineData("waveInterval")]
    [InlineData("conveyInterval")]
    public void MatchModify_accepts_every_one_of_the_eleven_legal_fields(string field)
    {
        Assert.True(
            AtomKindRegistry.Validate("match.modify", P(("field", field), ("amount", 1500))).IsOk,
            field);
    }

    [Fact]
    public void MatchModify_field_count_is_exactly_eleven()
    {
        Assert.Equal(11, AtomKindRegistry.Get("match.modify")!.Params.Defs
            .First(d => d.Name == "field").Vocabulary!().Count);
    }

    // §4: "a typo... BadParamValue, naming the field and the eleven legal values" — this is the real
    // guardrail per E29's own not-yet-landed registry check, not the schema shape.
    [Fact]
    public void MatchModify_typo_field_rejects_with_BadParamValue()
    {
        var r = AtomKindRegistry.Validate("match.modify",
            P(("field", "zombieHelthMultiplier"), ("amount", 1500)));

        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("zombieHelthMultiplier", r.Detail);
    }

    // PLANTED VIOLATION (§4): if MatchModifyFields's own Vocabulary check were ever dropped (accepting
    // any string), this typo would validate, compile, reach the sink, match no CheatIdFor case and do
    // nothing forever — the exact E29-class defect this test exists to catch before it ships.
    [Fact]
    public void PLANTED_VIOLATION_dropping_the_field_vocabulary_check_would_let_a_typo_validate()
    {
        Assert.Equal(AtomRejectionReason.BadParamValue,
            AtomKindRegistry.Validate("match.modify",
                P(("field", "zombieHelthMultiplier"), ("amount", 1500))).Reason);
    }

    [Fact]
    public void MatchModify_missing_field_rejects_with_MissingParam()
    {
        var r = AtomKindRegistry.Validate("match.modify", P(("amount", 1500)));
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    // §3/§4: no `op` — the executor assigns, and a multiply would need live host state.
    [Fact]
    public void MatchModify_op_is_UnknownParam()
    {
        var r = AtomKindRegistry.Validate("match.modify",
            P(("field", "zombieHealthMultiplier"), ("amount", 1500), ("op", "mul")));
        Assert.Equal(AtomRejectionReason.UnknownParam, r.Reason);
    }

    // §3: no row/col/cells — anything needing a cell is Board, not Match.
    [Theory]
    [InlineData("row")]
    [InlineData("col")]
    [InlineData("cells")]
    public void MatchModify_cell_params_are_UnknownParam(string key)
    {
        var pairs = new List<(string, object?)>
        {
            ("field", "zombieHealthMultiplier"), ("amount", 1500), (key, key == "cells" ? new[] { 1 } : 2)
        };
        var r = AtomKindRegistry.Validate("match.modify", P(pairs.ToArray()));
        Assert.Equal(AtomRejectionReason.UnknownParam, r.Reason);
    }

    // §2.2: MatchEvents only (OnWave/OnMatchStart/OnMatchEnd) — a board event is TriggerNotAllowed.
    [Fact]
    public void MatchModify_carries_MatchEvents_only()
    {
        Assert.True(AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnWave).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnMatchStart).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnMatchEnd).IsOk);

        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnDamageDealt).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnSunCollect).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnGridPlace).Reason);
    }

    // §2.2: Battle and Sim are None — neither has a Board.config or a consumer.
    [Fact]
    public void MatchModify_runtime_support_is_lawn_only()
    {
        var kind = AtomKindRegistry.Get("match.modify")!;
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Lawn));
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Sim));
    }

    // §2: no attach point beyond Match. E36 (spec-wave-control.md §2.1) makes wave.control the
    // second kind on it — this test used to assert match.modify was the ONLY one; updated rather
    // than left stale the moment a sibling module landed the second, exactly as its own doc predicted.
    [Fact]
    public void Match_carries_exactly_match_modify_and_wave_control()
    {
        var onMatch = AtomKindRegistry.All.Where(k => k.Attach == AttachPoint.Match)
            .Select(k => k.KindId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "match.modify", "wave.control" }, onMatch);
    }

    // ---- E36 (spec-wave-control.md) — wave.control's own op vocabulary and closed schema ----------

    [Theory]
    [InlineData("summon")]
    [InlineData("huge")]
    [InlineData("setTimer")]
    [InlineData("hold")]
    public void WaveControl_accepts_every_one_of_the_four_legal_ops(string op)
    {
        var pairs = op switch
        {
            "summon" => new[] { ("op", (object?)op), ("wave", 3) },
            "setTimer" => new[] { ("op", (object?)op), ("timerMs", 5000) },
            "hold" => new[] { ("op", (object?)op), ("enabled", true) },
            _ => new[] { ("op", (object?)op) },
        };
        Assert.True(AtomKindRegistry.Validate("wave.control", P(pairs)).IsOk, op);
    }

    [Fact]
    public void WaveControl_summon_with_wave_is_ok()
    {
        Assert.True(AtomKindRegistry.Validate("wave.control", P(("op", "summon"), ("wave", 3))).IsOk);
    }

    // §4: "op: summon, timerMs: 5000 -> ParamNotHonoured (wrong op for that param)."
    [Fact]
    public void WaveControl_timerMs_is_only_honoured_under_setTimer()
    {
        var r = AtomKindRegistry.Validate("wave.control", P(("op", "summon"), ("timerMs", 5000)));
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, r.Reason);
    }

    // §4: "op: freeze -> BadParamValue, naming the four legal ops. The op is hold, and the message
    // says the floor is a floor." This is also the vocabulary half of PLANTED VIOLATION #2 below.
    [Fact]
    public void WaveControl_freeze_rejects_by_name_not_just_by_membership()
    {
        var r = AtomKindRegistry.Validate("wave.control", P(("op", "freeze")));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("summon", r.Detail);
        Assert.Contains("huge", r.Detail);
        Assert.Contains("setTimer", r.Detail);
        Assert.Contains("hold", r.Detail);
        Assert.Contains("floors", r.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not stop", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WaveControl_no_op_is_MissingParam()
    {
        var r = AtomKindRegistry.Validate("wave.control", P());
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    // §4: "op: setTimer, timerMs: -1 -> BadParamValue."
    [Fact]
    public void WaveControl_negative_timerMs_rejects()
    {
        var r = AtomKindRegistry.Validate("wave.control", P(("op", "setTimer"), ("timerMs", -1)));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    // §2.2: wave is a wave ORDINAL, not a magnitude -- zero and negative both refuse.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WaveControl_wave_below_one_rejects(int wave)
    {
        var r = AtomKindRegistry.Validate("wave.control", P(("op", "summon"), ("wave", wave)));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    // §2.2: `enabled` only honoured under `hold`.
    [Fact]
    public void WaveControl_enabled_is_only_honoured_under_hold()
    {
        var r = AtomKindRegistry.Validate("wave.control", P(("op", "summon"), ("enabled", true)));
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, r.Reason);
    }

    // §2.5: same match/player-only scope rule as match.modify.
    [Fact]
    public void WaveControl_carries_the_events_plus_match_trigger_set()
    {
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnSpawn).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnDamageDealt).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnDamageTaken).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnDeath).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnWave).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnMatchStart).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnMatchEnd).IsOk);

        // Board-economy events are deliberately NOT part of this kind's set (EventsPlusMatch, not
        // EventsPlusMatchAndEconomy) -- wave.control has no board-economy meaning.
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnSunCollect).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnGridPlace).Reason);
    }

    [Fact]
    public void WaveControl_runtime_support_is_lawn_only()
    {
        var kind = AtomKindRegistry.Get("wave.control")!;
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Lawn));
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Sim));
    }

    // PLANTED VIOLATION (§4, vocabulary half): if wave.control's own op-vocabulary check were ever
    // dropped, "freeze" would need to be re-validated some other way -- pinning BadParamValue here
    // directly against the live registry means removing the check (or reverting the op's name back
    // to "freeze" in the kind's own vocabulary) fails this test, naming CheatActions.cs's F-WAVE-
    // FREEZE floor behaviour in the same assertion as the vocabulary membership check above.
    [Fact]
    public void PLANTED_VIOLATION_naming_the_op_freeze_would_repeat_the_fx_set_dirt_box_defect()
    {
        var r = AtomKindRegistry.Validate("wave.control", P(("op", "freeze")));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    // ---- E37 (spec-projectile-control.md) — the swept BulletMoveWay set, spawn.entity's bullet arm,
    // and bullet.modify's own closed schema -------------------------------------------------------

    // Criterion 0: the real, complete, 18-member set the assembly sweep found (ilspycmd against three
    // independent sources — see docs/research/effect-runtime/03-status-and-spawn-surface.md), never
    // the old unswept right|left|up|down|track guess.
    static readonly string[] RealBulletMoveWayMembers =
    {
        "MoveRight", "Puff", "MoveRight_threePeater", "Track", "Fly", "Free", "Left", "Split_left",
        "Throw", "Cannon", "PeaNut", "Stable", "SmoothTrack", "Sin", "Spin", "Jump", "SuperGatling",
        "None",
    };

    [Fact]
    public void SpawnEntity_moveWay_vocabulary_is_exactly_the_eighteen_swept_members()
    {
        var vocabulary = AtomKindRegistry.Get("spawn.entity")!.Params.Defs
            .First(d => d.Name == "moveWay").Vocabulary!();

        // The count is fully implied by the element-wise equality below against the real, swept
        // RealBulletMoveWayMembers array -- never pinned separately (population-pin SE3.3,
        // 2026-09-19).
        Assert.Equal(
            RealBulletMoveWayMembers.OrderBy(x => x, StringComparer.Ordinal),
            vocabulary.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("MoveRight")] [InlineData("Puff")] [InlineData("MoveRight_threePeater")]
    [InlineData("Track")] [InlineData("Fly")] [InlineData("Free")] [InlineData("Left")]
    [InlineData("Split_left")] [InlineData("Throw")] [InlineData("Cannon")] [InlineData("PeaNut")]
    [InlineData("Stable")] [InlineData("SmoothTrack")] [InlineData("Sin")] [InlineData("Spin")]
    [InlineData("Jump")] [InlineData("SuperGatling")] [InlineData("None")]
    public void Every_swept_moveWay_member_is_accepted_on_spawn_entity(string member)
    {
        Assert.True(AtomKindRegistry.Validate("spawn.entity",
            P(("kind", "bullet"), ("typeId", 0), ("moveWay", member))).IsOk, member);
    }

    // A member the sweep did NOT find (the old guess's own spelling) is a load-time BadParamValue,
    // never an unmatched cast at execute (§4's own test-table row).
    [Theory]
    [InlineData("spiral")]
    [InlineData("right")]   // the old, never-swept guess's spelling — real member is "MoveRight"
    [InlineData("up")]
    [InlineData("down")]
    public void An_unswept_moveWay_value_is_BadParamValue_at_load(string bogus)
    {
        var r = AtomKindRegistry.Validate("spawn.entity",
            P(("kind", "bullet"), ("typeId", 0), ("moveWay", bogus)));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("moveWay")]
    [InlineData("fromType")]
    public void Bullet_only_spawn_params_are_honoured_for_bullet_and_dropped_for_plant_and_zombie(string key)
    {
        object value = key == "moveWay" ? "Track" : 3;

        Assert.True(AtomKindRegistry.Validate("spawn.entity",
            P(("kind", "bullet"), ("typeId", 0), (key, value))).IsOk, key);

        Assert.Equal(AtomRejectionReason.ParamNotHonoured, AtomKindRegistry.Validate("spawn.entity",
            P(("kind", "plant"), ("typeId", 0), (key, value))).Reason);
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, AtomKindRegistry.Validate("spawn.entity",
            P(("kind", "zombie"), ("typeId", 0), (key, value))).Reason);
    }

    // ---- bullet.modify's own closed schema ---------------------------------------------------------

    [Theory]
    [InlineData("set")]
    [InlineData("add")]
    [InlineData("scale")]
    public void BulletModify_accepts_every_one_of_the_three_legal_ops(string op)
    {
        Assert.True(AtomKindRegistry.Validate("bullet.modify",
            P(("op", op), ("amount", 1500))).IsOk, op);
    }

    [Fact]
    public void BulletModify_bad_op_rejects_with_BadParamValue()
    {
        var r = AtomKindRegistry.Validate("bullet.modify", P(("op", "multiply"), ("amount", 1500)));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void BulletModify_missing_op_is_MissingParam()
    {
        var r = AtomKindRegistry.Validate("bullet.modify", P(("amount", 1500)));
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    [Fact]
    public void BulletModify_missing_amount_is_MissingParam()
    {
        var r = AtomKindRegistry.Validate("bullet.modify", P(("op", "set")));
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    [Fact]
    public void BulletModify_accepts_optional_bulletType_and_moveWay()
    {
        Assert.True(AtomKindRegistry.Validate("bullet.modify",
            P(("op", "set"), ("amount", 200), ("bulletType", 3), ("moveWay", "Track"))).IsOk);
    }

    [Fact]
    public void BulletModify_bad_moveWay_is_BadParamValue()
    {
        var r = AtomKindRegistry.Validate("bullet.modify",
            P(("op", "set"), ("amount", 200), ("moveWay", "spiral")));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    // §2b.1: a permanent modifier — no trigger may bind to it, on either the known or unknown side.
    [Fact]
    public void BulletModify_carrying_any_trigger_is_TriggerNotAllowed()
    {
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("bullet.modify", AtomTriggers.OnDamageDealt).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("bullet.modify", AtomTriggers.OnSpawn).Reason);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("bullet.modify", AtomTriggers.OnActivate).Reason);
    }

    // §2b: Lawn only today — Battle/Sim have no projectile consumer (E1's living-table "pending, never
    // never" rule — RuntimeState.None here records a real gap, not a permanent "never").
    [Fact]
    public void BulletModify_runtime_support_is_lawn_only()
    {
        var kind = AtomKindRegistry.Get("bullet.modify")!;
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Lawn));
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Sim));
    }

    // §2b: no new attach point — bullet.modify reuses the existing Board seam spawn.entity/board.action
    // etc already use, distinct from match.modify/wave.control's own Match attach point.
    [Fact]
    public void BulletModify_attaches_to_the_existing_Board_point()
    {
        Assert.Equal(AttachPoint.Board, AtomKindRegistry.Get("bullet.modify")!.Attach);
    }

    // §4's "re-add NotImplementedNote to atk -> load test must fail with ParamNotImplemented" planted
    // violation, load-time half (the sink-forwarding half needs a live host — see spec §4's own note
    // that this repo's CI never builds the injector). Same shape as
    // Economy_capPerMatch_validates_now_that_the_runner_owns_it's own NotImplementedNote assertion.
    [Fact]
    public void SpawnEntity_atk_carries_no_NotImplementedNote()
    {
        Assert.Null(AtomKindRegistry.Get("spawn.entity")!.Params.Defs
            .First(d => d.Name == "atk").NotImplementedNote);
    }

    // ---- base-defense `siege-construction` (decision 27, 2026-09-06) -------------------------------

    [Fact]
    public void StructurePlace_attaches_to_the_new_Siege_point_and_is_battle_only()
    {
        var kind = AtomKindRegistry.Get("structure.place")!;
        Assert.Equal(AttachPoint.Siege, kind.Attach);
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Lawn)); // no tactical board on the lawn
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Battle));
    }

    [Fact]
    public void StructurePlace_requires_structureId_and_instant()
    {
        Assert.Equal(AtomRejectionReason.MissingParam,
            AtomKindRegistry.Validate("structure.place", P(("instant", true))).Reason);
        Assert.Equal(AtomRejectionReason.MissingParam,
            AtomKindRegistry.Validate("structure.place", P(("structureId", "well"))).Reason);
    }

    [Fact]
    public void StructurePlace_refuses_an_unknown_structure_id()
    {
        var rejection = AtomKindRegistry.Validate("structure.place",
            P(("structureId", "not-a-real-structure"), ("instant", true)));
        Assert.Equal(AtomRejectionReason.BadParamValue, rejection.Reason);
    }

    [Fact]
    public void StructurePlace_accepts_a_real_shipped_structure_id()
    {
        Assert.True(AtomKindRegistry.Validate("structure.place",
            P(("structureId", "well"), ("instant", true))).IsOk);
        Assert.True(AtomKindRegistry.Validate("structure.place",
            P(("structureId", "granary"), ("instant", false))).IsOk);
    }
}
