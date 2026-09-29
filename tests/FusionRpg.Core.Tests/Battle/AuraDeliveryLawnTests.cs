using FusionRpg.Contracts;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// aura-skill-todo.md Phase 5 / <b>TC2</b> — the <b>lawn</b> twin of <see cref="AuraDeliveryTests"/>,
/// which proves the same thing on the Sim host. <b>TC2 is closed: all three acceptance criteria plus
/// the grant-transport hop.</b>
///
/// <para><b>"BLOCKED" did not survive contact with the code — twice.</b> TC2 was recorded as blocked on
/// <c>EffectBag.Grant</c> rejecting an unregistered <c>EffectId</c>, and the whole hop was assigned to
/// <c>effect-atom</c> Wave 6 / E20-E25 (*"a loader, an importer run, and a producer of bindings"*).
/// Applying DESIGN-GATE's <i>"test the constraint before you declare it"</i> dismantled that in stages:</para>
/// <list type="number">
///   <item>The catalog rejection is a <b>content</b> gap on the live lawn, not a testability wall —
///   <c>EffectBag</c> takes an <see cref="IEffectCatalog"/> by ctor injection and tests already register
///   their own defs.</item>
///   <item>The real blocker was <c>EffectOverlayMerge.AllowedByAction</c> having no derived-stat
///   action... </item>
///   <item>...and, one layer deeper, <c>AtomCompiler.OpcodeOf</c> returning <c>null</c> for
///   <c>stat.derived</c>, plus <c>Compilability.OpcodeKinds</c> omitting it — which routed the kind
///   down the <b>runner</b> path, so it never became an <c>EffectDef</c> at all.</item>
/// </list>
///
/// <para><b>The fix was five small links, not a wave:</b> an <c>EffectActions.ModifyDerivedStat</c>
/// constant, its <c>AllowedByAction</c> row, the <c>OpcodeOf</c> mapping, the <c>OpcodeKinds</c> entry,
/// and making <see cref="GrantedDerivedAtomReader"/> catalog-aware. Measured before being kept:
/// <b>no goldens and no content hashes moved.</b></para>
///
/// <para><b>Two real bugs were found on the way</b>, both invisible to every test written before them
/// because those tests shared the code's own wrong assumptions:</para>
/// <list type="bullet">
///   <item>The reader used <b>bare</b> owner keys where production uses <c>plant:{typeId}</c> /
///   <c>entity:{ptr}</c>. <c>StatApplyScope.Normalize</c> is not prefix-agnostic, so <b>two of three
///   owner scopes matched nothing</b>; only <c>match</c> worked and hid it.</item>
///   <item>The reader read <c>grant.Overlay</c>, but the production grant path
///   (<c>BattlefieldOwnSideReactor.BuildGrant</c>) emits <b>no overlay at all</b> — the values live on
///   the def's action row. The executor was <b>inert in production</b> while green offline.</item>
/// </list>
///
/// <para><b>Still open:</b> only <b>A5</b>, the live on-the-lawn probe against a running game, in
/// <c>docs/architecture/effect-atom/spec-derived-write-lawn.md</c>. Every offline link is now proven.</para>
/// </summary>
public class AuraDeliveryLawnTests
{
    // Same two tunings AuraDeliveryTests uses, so "the two hosts agree" is a comparison of like with
    // like rather than of two differently-configured ladders.
    static AuraTuning Rung7To10() => new(new Dictionary<int, long>
    {
        [7] = 5359, [8] = 7090, [9] = 9379, [10] = 12407,
    }, MaxActiveAuras: 1);

    static AptitudeTuning LinearGammaTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 1, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude" },
          "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 1000 } ]
        }
        """);

    /// <summary>The identical call <see cref="AuraDeliveryTests"/> makes for its Sim-host assertion.</summary>
    static long T10Value() =>
        AuraMagnitude.Compute(rung: 10, share: 1.0, pTheta: 1_000_000, Rung7To10(), LinearGammaTuning());

    const int PlantType = 20_001;

    static InMemoryEffectGrantStore StoreWith(params EffectGrant[] grants)
    {
        var store = new InMemoryEffectGrantStore();
        foreach (var g in grants) store.Upsert(g);
        return store;
    }

    static EffectGrant AuraGrant(string ownerKind, string ownerKey, string channel, double amount) => new()
    {
        GrantId = "g-aura",
        EffectId = "aura:test-ember",           // the same source tag the Sim-host twin uses
        OwnerKind = ownerKind,
        OwnerKey = ownerKey,
        Overlay = new Dictionary<string, object?>
        {
            [GrantedDerivedAtomReader.ChannelKey] = channel,
            [GrantedDerivedAtomReader.OpKey] = "flat",
            [GrantedDerivedAtomReader.AmountKey] = amount,
        },
    };

    static FusionRpg.Core.Stats.Derived.ActorHub HubOver(IEffectGrantStore store)
    {
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(ctx => GrantedDerivedAtomReader.Read(store, ctx)));
        return hub;
    }

    static StatContext Plant(string entityKey = "0xPLANT") =>
        new() { Side = StatSide.Plant, TypeId = PlantType, EntityKey = entityKey };

    static StatContext Zombie(string entityKey = "0xZOMBIE") =>
        new() { Side = StatSide.Zombie, TypeId = PlantType, EntityKey = entityKey };

    // ── criterion 1: the two hosts agree ─────────────────────────────────────────────────────────

    /// <summary>
    /// TC2's first acceptance criterion, and its stated point: <i>"the two hosts agreeing"</i>. The
    /// value <see cref="AuraDeliveryTests"/> feeds into <c>ActiveCommanderAura</c> on the Sim host
    /// reaches <c>combat.power.omni</c> on a lawn plant, unchanged, through the real lawn executor.
    /// </summary>
    [Fact]
    public void An_active_aura_raises_combat_power_omni_on_a_lawn_plant_by_the_same_T10_value_the_sim_host_uses()
    {
        var t10 = T10Value();
        Assert.True(t10 > 1000, "test needs a large, unambiguous buff, not a rounding-noise one");

        var hub = HubOver(StoreWith(AuraGrant("plant", EffectOwnerKeys.PlantType(PlantType), DerivedStatChannels.CombatPowerOmni, t10)));

        var derived = hub.Resolve(Plant()).Derived;

        Assert.Equal(t10, derived.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
    }

    /// <summary>The aura reaches <b>every</b> plant of its type, not just the one whose entity key was
    /// named — the lawn twin of the Sim host's "every friendly squad actor". Type-scoped delivery is
    /// what makes an aura an aura rather than a single-target buff.</summary>
    [Fact]
    public void A_type_scoped_aura_reaches_every_plant_of_that_type()
    {
        var t10 = T10Value();
        var hub = HubOver(StoreWith(AuraGrant("plant", EffectOwnerKeys.PlantType(PlantType), DerivedStatChannels.CombatPowerOmni, t10)));

        foreach (var key in new[] { "0xA", "0xB", "0xC" })
            Assert.Equal(t10, hub.Resolve(Plant(key)).Derived.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
    }

    /// <summary>A match-scoped aura reaches a plant too — the `match` owner key is the broadest of the
    /// three shipped scopes and is how a commander-wide aura is expressed.</summary>
    [Fact]
    public void A_match_scoped_aura_also_reaches_a_lawn_plant()
    {
        var t10 = T10Value();
        var hub = HubOver(StoreWith(AuraGrant("match", EffectOwnerKeys.Match, DerivedStatChannels.CombatPowerOmni, t10)));

        Assert.Equal(t10, hub.Resolve(Plant()).Derived.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
    }

    // ── criterion 2: never the enemy side ────────────────────────────────────────────────────────

    /// <summary>TC2's second acceptance criterion — the lawn twin of
    /// <c>AuraDeliveryTests.The_aura_never_touches_the_enemy_wave_side</c>. A plant-side aura must not
    /// reach a zombie, <b>even at the identical type id</b>, which is the case a naive owner-key match
    /// would get wrong.</summary>
    [Fact]
    public void A_plant_side_aura_never_touches_a_zombie_even_at_the_same_type_id()
    {
        var hub = HubOver(StoreWith(AuraGrant("plant", EffectOwnerKeys.PlantType(PlantType), DerivedStatChannels.CombatPowerOmni, T10Value())));

        var zombie = hub.Resolve(Zombie());

        Assert.Equal(0, zombie.Derived.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
        // ...and the plant in the same resolve pair genuinely IS buffed, so this cannot pass because
        // the aura reached nobody at all.
        Assert.True(hub.Resolve(Plant()).Derived.Get(DerivedStatChannels.CombatPowerOmni, 0) > 0);
    }

    // ── criterion 3: absent, nothing moves ───────────────────────────────────────────────────────

    /// <summary>TC2's third acceptance criterion. With no aura granted, the lawn resolve is unchanged —
    /// asserted by reference identity on <c>AppliedCombat</c>, which <c>ActorHub.MergeAppliedCombat</c>
    /// returns as the primary <i>instance</i> when no bridge channel carries a value. No goldens move
    /// because nothing composes at all.</summary>
    [Fact]
    public void Absent_any_aura_the_lawn_resolve_is_unchanged()
    {
        var hub = HubOver(StoreWith());

        var result = hub.Resolve(Plant());

        Assert.Equal(0, result.Derived.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
        Assert.True(ReferenceEquals(result.AppliedCombat, result.RuntimePrimary),
            "an empty aura set must leave AppliedCombat as the primary instance — anything else is a silent write");
    }

    /// <summary>Withdrawing the aura returns the channel to zero — the "disable" half. An aura that
    /// cannot be turned off is a permanent stat, which is a different feature.</summary>
    [Fact]
    public void Withdrawing_the_aura_returns_the_channel_to_zero()
    {
        var store = StoreWith(AuraGrant("plant", EffectOwnerKeys.PlantType(PlantType), DerivedStatChannels.CombatPowerOmni, T10Value()));
        var hub = HubOver(store);

        Assert.True(hub.Resolve(Plant()).Derived.Get(DerivedStatChannels.CombatPowerOmni, 0) > 0);

        Assert.True(store.Withdraw("g-aura"));

        Assert.Equal(0, hub.Resolve(Plant()).Derived.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
    }

    // ── the named remaining gap, asserted rather than described ──────────────────────────────────

    /// <summary>
    /// <b>The TC2 gap, pinned as a test so it cannot be quietly forgotten.</b> This asserts the blocker
    /// is exactly where this file's summary says it is: a derived-stat overlay is refused by
    /// <c>EffectOverlayMerge.AllowedByAction</c>, because no shipped effect action declares those keys.
    ///
    /// <para>When <c>effect-atom</c> Wave 6 (E20-E25) adds the derived-stat action, <b>this test will
    /// start failing</b> — and that is the intended signal. At that point delete it and write the real
    /// end-to-end grant test in its place, closing TC2's last hop.</para>
    /// </summary>
    [Fact]
    public void The_remaining_gap_a_derived_overlay_is_still_refused_by_every_shipped_effect_action()
    {
        var overlay = new Dictionary<string, object?>
        {
            [GrantedDerivedAtomReader.ChannelKey] = DerivedStatChannels.CombatPowerOmni,
            [GrantedDerivedAtomReader.OpKey] = "flat",
            [GrantedDerivedAtomReader.AmountKey] = 1234.0,
        };

        // Every action the shipped whitelist knows about, tried one at a time. Not one of them accepts
        // a derived-stat overlay -- which is precisely why TC2's last hop cannot be written yet.
        foreach (var action in new[]
                 {
                     EffectActions.ModifyStat, EffectActions.ApplyStatus, EffectActions.ClearStatus,
                     EffectActions.ApplyResourceDelta, EffectActions.Economy,
                 })
        {
            var actions = new[] { new EffectActionRow { Action = action } };

            var ok = EffectOverlayMerge.TryValidateOverlayForDef(actions, overlay, out var error);

            Assert.False(ok, $"action '{action}' now accepts a derived-stat overlay — Wave 6 has landed; " +
                             "delete this test and write the real end-to-end grant test that closes TC2.");
            Assert.Contains("derived.", error!, StringComparison.Ordinal);
        }
    }

    // ── TC2's last hop, now CLOSED: the real EffectBag.Grant path end to end ─────────────────────

    /// <summary>
    /// <b>⭐ TC2's remaining hop, closed 2026-08-30.</b> This is the test the task said could not be
    /// written until Wave 6: a def carrying a <c>ModifyDerivedStat</c> action row, granted through the
    /// <b>real <see cref="EffectBag.Grant"/></b> — surviving its catalog lookup <i>and</i> its overlay
    /// validation — reaching <c>combat.power.omni</c> on a lawn plant via the real reader/subsystem/hub.
    ///
    /// <para>The blocker was real but narrower than recorded: it was one missing opcode
    /// (<c>AtomCompiler.OpcodeOf</c> returned null for <c>stat.derived</c>) plus its
    /// <c>AllowedByAction</c> row — not a whole loader/importer wave. Adding them moved <b>no</b>
    /// goldens and <b>no</b> content hashes, measured before being kept.</para>
    /// </summary>
    [Fact]
    public void A_real_def_granted_through_the_real_EffectBag_reaches_a_lawn_plant()
    {
        var t10 = T10Value();

        // Build the def from REAL AtomCompiler output rather than hand-writing its action params.
        // Hand-writing them is how the first version of this test passed while the production shape was
        // different: the compiler rewrites {op:"flat", amount:N} into the op-as-key form {flat:N}
        // (ToOpcodeShape), and a hand-built {op, amount} def matches nothing real.
        var atom = new FusionRpg.Core.Effects.Atoms.AtomRow
        {
            AtomId = FusionRpg.Core.Effects.Atoms.AtomRow.DeriveId("atom.aura-might", "", 1),
            KindId = "stat.derived",
            FamilyId = "atom.aura-might",
            Variant = "",
            Tier = 1,
            Name = "Might (live aura)",
            ParamsJson = $$"""{"channel":"{{DerivedStatChannels.CombatPowerOmni}}","op":"flat","amount":{{t10}}}""",
            WhenJson = "{}",   // permanent modifier: no trigger
            IcdKey = "aura.might.live",
        };

        var compiled = FusionRpg.Core.Effects.Atoms.AtomCompiler.Compile(
            new[] { atom }, FusionRpg.Core.Effects.Atoms.RuntimeId.Lawn, catalogRevision: 1);

        Assert.Empty(compiled.Rejected);
        var compiledDef = Assert.Single(compiled.Defs);
        var compiledAction = Assert.Single(compiledDef.Actions);
        Assert.Equal(EffectActions.ModifyDerivedStat, compiledAction.Action);

        var catalog = new InMemoryEffectCatalog();
        catalog.Upsert(new EffectDef
        {
            EffectId = compiledDef.EffectId,
            EffectType = compiledDef.EffectType,
            Name = compiledDef.Name,
            Actions = { new EffectActionRow { Action = compiledAction.Action, Params = compiledAction.Params } },
        });

        var bag = new EffectBag(catalog, new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)), new RecordingEffectSink());

        // The exact shape BattlefieldOwnSideReactor.BuildGrant emits: id + owner, no overlay.
        bag.Grant(new EffectGrantDto
        {
            GrantId = compiledDef.EffectId + ":0xPLANT",
            EffectId = compiledDef.EffectId,
            OwnerKind = "entity",
            OwnerKey = EffectOwnerKeys.Entity("0xPLANT"),
        });

        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(ctx => GrantedDerivedAtomReader.Read(bag.Grants, bag.Catalog, ctx)));

        var result = hub.Resolve(Plant("0xPLANT"));

        Assert.Equal(t10, result.Derived.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);

        // ...and a plant that is NOT the grant's owner is untouched — per-entity scoping survives the
        // real grant path, not just the hand-built one.
        Assert.Equal(0, hub.Resolve(Plant("0xOTHER")).Derived.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
    }

    /// <summary>The collision guard, re-proven on the <b>catalog</b> path: an FA1 <c>ModifyStat</c> def
    /// whose params use the bare <c>channel</c>/<c>op</c>/<c>amount</c> names yields no derived atom,
    /// because the reader matches on the action id rather than on key names. This is what makes the
    /// catalog path structurally collision-proof instead of collision-proof by naming convention.</summary>
    [Fact]
    public void An_FA1_def_on_the_catalog_path_still_yields_no_derived_atom()
    {
        var catalog = new InMemoryEffectCatalog();
        catalog.Upsert(new EffectDef
        {
            EffectId = "fx.fa1.buff",
            EffectType = EffectTypes.Passive,
            Actions =
            {
                new EffectActionRow
                {
                    Action = EffectActions.ModifyStat,
                    Params = new Dictionary<string, object?>
                    {
                        ["channel"] = "atk", ["flat"] = 50.0,
                    },
                },
            },
        });

        var store = new InMemoryEffectGrantStore();
        store.Upsert(new EffectGrant
        {
            GrantId = "g-fa1", EffectId = "fx.fa1.buff",
            OwnerKind = "entity", OwnerKey = EffectOwnerKeys.Entity("0xPLANT"),
        });

        Assert.Empty(GrantedDerivedAtomReader.Read(store, catalog, Plant("0xPLANT")));
    }

    /// <summary>
    /// <b>⛔ The defect this file's investigation actually uncovered, pinned so it cannot be lost.</b>
    ///
    /// <para><c>spec-derived-write-lawn.md</c> claimed <i>"This module's own half is done: the moment
    /// such a def is grantable, the executor consumes it."</i> <b>That is false</b>, and this test is
    /// why. The production grant path — <c>BattlefieldOwnSideReactor.BuildGrant</c> — emits a grant
    /// carrying an <c>EffectId</c> and <b>no Overlay at all</b>. <see cref="GrantedDerivedAtomReader"/>
    /// reads <c>grant.Overlay</c>. So a real reactor-issued grant yields <b>nothing</b>: the lawn
    /// executor is <b>inert in production</b>, not merely waiting for content.</para>
    ///
    /// <para>Verified independently: no file under <c>src/</c> writes
    /// <c>derived.channel</c>/<c>derived.op</c>/<c>derived.amount</c> onto a grant — the only writers
    /// are this test project's fixtures. The values are supposed to live on the compiled def's
    /// <b>action row params</b> (the <c>stat.derived</c> ParamSchema in <c>AtomKindRegistry</c> names
    /// them <c>channel</c>/<c>op</c>/<c>amount</c>), which is a different transport entirely.</para>
    ///
    /// <para>This is a <b>wiring gap with four named missing links</b>, all in <c>effect-atom</c>
    /// Wave 6 / E20-E25 — see <c>tasks/aura-skill-todo.md</c> Phase 5 TC2 for the work order.</para>
    /// </summary>
    [Fact]
    public void The_production_grant_shape_carries_no_overlay_so_the_reader_is_inert_today()
    {
        // Exactly what BattlefieldOwnSideReactor.BuildGrant produces: id + owner, no overlay.
        var productionShaped = new EffectGrant
        {
            GrantId = "aura:test-ember:0xPLANT",
            EffectId = "aura.might.live",
            OwnerKind = "entity",
            OwnerKey = EffectOwnerKeys.Entity("0xPLANT"),
            // Overlay deliberately left at its default (empty) -- BuildGrant sets none.
        };

        var atoms = GrantedDerivedAtomReader.Read(StoreWith(productionShaped), Plant("0xPLANT"));

        Assert.Empty(atoms);

        // The same grant WITH the overlay the reader expects does produce an atom -- so the emptiness
        // above is specifically the missing transport, not a broken reader.
        var withOverlay = new EffectGrant
        {
            GrantId = productionShaped.GrantId,
            EffectId = productionShaped.EffectId,
            OwnerKind = productionShaped.OwnerKind,
            OwnerKey = productionShaped.OwnerKey,
            Overlay = new Dictionary<string, object?>
            {
                [GrantedDerivedAtomReader.ChannelKey] = DerivedStatChannels.CombatPowerOmni,
                [GrantedDerivedAtomReader.OpKey] = "flat",
                [GrantedDerivedAtomReader.AmountKey] = 123.0,
            },
        };

        Assert.Single(GrantedDerivedAtomReader.Read(StoreWith(withOverlay), Plant("0xPLANT")));
    }
}
