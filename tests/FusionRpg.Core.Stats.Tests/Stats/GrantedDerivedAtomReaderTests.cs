using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// aura-skill-todo.md Phase 5 / <b>TC3</b> — <see cref="GrantedDerivedAtomReader"/>.
///
/// <para><b>The headline test here is <see cref="An_FA1_ModifyStat_grant_is_not_consumed_as_a_derived_atom"/>.</b>
/// The reader's first draft matched bare <c>channel</c>/<c>op</c>/<c>amount</c> — exactly the keys
/// <c>InjectorEffectActionSink.ExecModifyStat</c> (FA1) and <c>ExecApplyResourceDelta</c> (FA10) already
/// read. Every FA1 grant on the board would have been consumed twice: once as a primary stat modifier
/// and again as a derived channel mod. That was caught by review before shipping and fixed by
/// namespacing the keys — but until TC3 the fix was guarded by <b>a comment and nothing else</b>,
/// because the code lived in the injector where no CI test can reach it.</para>
/// </summary>
public class GrantedDerivedAtomReaderTests
{
    // ── a minimal in-memory grant store ──────────────────────────────────────────────────────────

    sealed class FakeGrantStore : IEffectGrantStore
    {
        readonly List<EffectGrant> _grants = new();
        public bool ThrowOnForOwner { get; set; }

        public FakeGrantStore Add(string ownerKind, string ownerKey, Dictionary<string, object?> overlay,
            string effectId = "test.effect", string grantId = "g1")
        {
            _grants.Add(new EffectGrant
            {
                GrantId = grantId, EffectId = effectId,
                OwnerKind = ownerKind, OwnerKey = ownerKey, Overlay = overlay,
            });
            return this;
        }

        public IReadOnlyList<EffectGrant> ForOwner(string? ownerKind, string ownerKey)
        {
            if (ThrowOnForOwner) throw new InvalidOperationException("bag is not up");
            return _grants.Where(g =>
                string.Equals(g.OwnerKind, ownerKind, StringComparison.Ordinal) &&
                string.Equals(g.OwnerKey, ownerKey, StringComparison.Ordinal)).ToList();
        }

        public EffectGrant? Get(string grantId) => _grants.FirstOrDefault(g => g.GrantId == grantId);
        public IReadOnlyList<EffectGrant> All() => _grants;
        public IReadOnlyList<EffectGrant> Matching(EffectEventDto ev) => _grants;
        public void Upsert(EffectGrant grant) => _grants.Add(grant);
        public bool Withdraw(string grantId) => _grants.RemoveAll(g => g.GrantId == grantId) > 0;
        public void Clear() => _grants.Clear();
    }

    static Dictionary<string, object?> DerivedOverlay(string channel, string op, object amount) => new()
    {
        [GrantedDerivedAtomReader.ChannelKey] = channel,
        [GrantedDerivedAtomReader.OpKey] = op,
        [GrantedDerivedAtomReader.AmountKey] = amount,
    };

    static StatContext PlantCtx(int typeId = 7, string entityKey = "1A2B") =>
        new() { Side = StatSide.Plant, TypeId = typeId, EntityKey = entityKey };

    // ── ⭐ the collision guard ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// An <b>FA1 <c>ModifyStat</c></b> grant uses bare <c>channel</c> (confirmed against
    /// <c>InjectorEffectActionSink.ExecModifyStat</c>, which reads <c>GetString(p, "channel")</c>). It
    /// must produce <b>no</b> derived atom — otherwise it is applied twice, once as a primary stat
    /// modifier and once as a derived channel mod, and the entity's stats are silently doubled.
    /// </summary>
    [Fact]
    public void An_FA1_ModifyStat_grant_is_not_consumed_as_a_derived_atom()
    {
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match, new Dictionary<string, object?>
        {
            ["channel"] = "atk",     // FA1's real key
            ["op"] = "flat",
            ["amount"] = 50.0,
            ["remove"] = false,
        });

        Assert.Empty(GrantedDerivedAtomReader.Read(store, PlantCtx()));
    }

    /// <summary>
    /// The FA10 <c>ApplyResourceDelta</c> twin — bare <c>channel</c> + <c>amount</c>
    /// (<c>ExecApplyResourceDelta</c> reads exactly those, plus <c>targetPtr</c>).
    ///
    /// <para><b>Honest scope, measured not assumed.</b> Running the bare-key mutation against this file
    /// showed only the two FA1 cases go red; this one stayed green <i>vacuously</i>. FA10's real params
    /// carry <b>no <c>op</c> key</b>, so even the buggy bare-key reader skipped it at the op check —
    /// FA10 was never actually at risk, and it would be wrong to claim this test proved otherwise.
    /// <b>FA1 is the real collision.</b></para>
    ///
    /// <para>The case is kept as a <i>forward</i> guard: the day FA10 grows an <c>op</c> param it would
    /// become a genuine collision, and <see cref="An_FA10_grant_that_grows_an_op_key_still_yields_nothing"/>
    /// below covers that shape now rather than after it ships.</para>
    /// </summary>
    [Fact]
    public void An_FA10_ApplyResourceDelta_grant_is_not_consumed_as_a_derived_atom()
    {
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match, new Dictionary<string, object?>
        {
            ["channel"] = "hp",
            ["amount"] = -25.0,
            ["targetPtr"] = "1A2B",
        });

        Assert.Empty(GrantedDerivedAtomReader.Read(store, PlantCtx()));
    }

    /// <summary>The FA10 shape that <i>would</i> collide if the keys were ever un-namespaced — every
    /// bare key present, <c>op</c> included. This is the case the previous test does not actually
    /// exercise, written after a falsifier run proved that gap rather than assuming coverage.</summary>
    [Fact]
    public void An_FA10_grant_that_grows_an_op_key_still_yields_nothing()
    {
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match, new Dictionary<string, object?>
        {
            ["channel"] = "hp",
            ["op"] = "flat",
            ["amount"] = -25.0,
            ["targetPtr"] = "1A2B",
        });

        Assert.Empty(GrantedDerivedAtomReader.Read(store, PlantCtx()));
    }

    /// <summary>The positive control for the two above: the same grant, with the <b>namespaced</b> keys,
    /// IS read. Without this, both collision tests would pass on a reader that read nothing at all.</summary>
    [Fact]
    public void A_namespaced_derived_grant_is_read()
    {
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match,
            DerivedOverlay("combat.power.omni", "flat", 120.0));

        var atom = Assert.Single(GrantedDerivedAtomReader.Read(store, PlantCtx()));
        Assert.Equal("combat.power.omni", atom.Channel);
        Assert.Equal(DerivedModifierOp.Flat, atom.Op);
        Assert.Equal(120.0, atom.Amount);
        Assert.Equal("grant:test.effect", atom.SourceId);
    }

    /// <summary>Both shapes on the board at once — the realistic case. Exactly the derived one is read.</summary>
    [Fact]
    public void An_FA1_grant_and_a_derived_grant_side_by_side_yield_only_the_derived_one()
    {
        var store = new FakeGrantStore()
            .Add("match", EffectOwnerKeys.Match, new Dictionary<string, object?>
            {
                ["channel"] = "atk", ["op"] = "flat", ["amount"] = 50.0,
            }, effectId: "fa1.effect", grantId: "g-fa1")
            .Add("match", EffectOwnerKeys.Match,
                DerivedOverlay("combat.power.omni", "flat", 120.0), effectId: "derived.effect", grantId: "g-derived");

        var atom = Assert.Single(GrantedDerivedAtomReader.Read(store, PlantCtx()));
        Assert.Equal("grant:derived.effect", atom.SourceId);
    }

    // ── owner scopes ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The three shipped owner scopes all reach the actor, and they compose — a plant is
    /// subject to match-wide, its own type's, and its own entity's grants simultaneously.</summary>
    [Fact]
    public void All_three_owner_scopes_are_collected()
    {
        var store = new FakeGrantStore()
            .Add("match", EffectOwnerKeys.Match, DerivedOverlay("combat.power.omni", "flat", 1), grantId: "m")
            .Add("plant", EffectOwnerKeys.PlantType(7), DerivedOverlay("combat.defense.omni", "flat", 2), grantId: "t")
            .Add("entity", EffectOwnerKeys.Entity("1A2B"), DerivedOverlay("combat.accuracy.omni", "flat", 3), grantId: "e");

        var atoms = GrantedDerivedAtomReader.Read(store, PlantCtx(typeId: 7, entityKey: "1A2B"));

        Assert.Equal(3, atoms.Count);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, atoms.Select(a => a.Amount).OrderBy(x => x));
    }

    /// <summary>A zombie reads the `zombie:` scope, never the `plant:` one — the side must actually be
    /// consulted, not defaulted.</summary>
    [Fact]
    public void Side_selects_the_type_scope_a_zombie_never_picks_up_plant_type_grants()
    {
        var store = new FakeGrantStore()
            .Add("plant", EffectOwnerKeys.PlantType(7), DerivedOverlay("combat.power.omni", "flat", 99))
            .Add("zombie", EffectOwnerKeys.ZombieType(7), DerivedOverlay("combat.defense.omni", "flat", 5));

        var zombie = GrantedDerivedAtomReader.Read(store,
            new StatContext { Side = StatSide.Zombie, TypeId = 7, EntityKey = "" });

        var atom = Assert.Single(zombie);
        Assert.Equal("combat.defense.omni", atom.Channel);
    }

    /// <summary>An empty <c>EntityKey</c> must not query an `entity:` scope at all — an `entity:`
    /// lookup on a blank key is how a match-wide grant would leak onto every actor.</summary>
    [Fact]
    public void A_blank_entity_key_does_not_query_the_entity_scope()
    {
        var store = new FakeGrantStore().Add("entity", EffectOwnerKeys.Entity(""), DerivedOverlay("combat.power.omni", "flat", 42));

        Assert.Empty(GrantedDerivedAtomReader.Read(store,
            new StatContext { Side = StatSide.Plant, TypeId = 7, EntityKey = "" }));
    }

    // ── op parsing ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("flat", DerivedModifierOp.Flat)]
    [InlineData("increased", DerivedModifierOp.Increased)]
    [InlineData("replace", DerivedModifierOp.Replace)]
    [InlineData("flag", DerivedModifierOp.Flag)]
    public void Each_supported_op_parses(string op, DerivedModifierOp expected)
    {
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match,
            DerivedOverlay("combat.power.omni", op, 10));

        Assert.Equal(expected, Assert.Single(GrantedDerivedAtomReader.Read(store, PlantCtx())).Op);
    }

    /// <summary><c>more</c> is a real op on the EFFECT side and deliberately absent on the derived
    /// side. Skipping it — rather than coercing it to <c>flat</c> — is the point: a coerced value would
    /// be wrong but plausible, and would hide that the bind gate should have refused the row.</summary>
    [Theory]
    [InlineData("more")]
    [InlineData("FLAT")]     // case-sensitive on purpose
    [InlineData("")]
    [InlineData("nonsense")]
    public void An_unsupported_op_is_skipped_never_coerced(string op)
    {
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match,
            DerivedOverlay("combat.power.omni", op, 10));

        Assert.Empty(GrantedDerivedAtomReader.Read(store, PlantCtx()));
    }

    // ── amount coercion, including the JSON path the wire actually delivers ──────────────────────

    /// <summary>Overlays arrive off the wire as <see cref="JsonElement"/>, not as boxed CLR numbers —
    /// so the <c>JsonElement</c> cases are the REAL path, and the boxed ones are the in-process path.
    /// Both must work or the reader is dead on the live lawn while green in tests.</summary>
    [Fact]
    public void Amount_is_read_from_every_supported_representation_including_JsonElement()
    {
        using var doc = JsonDocument.Parse("""{"n": 12.5, "s": "12.5"}""");
        var jsonNumber = doc.RootElement.GetProperty("n");

        foreach (object amount in new object[] { 12.5d, 12.5f, "12.5", jsonNumber })
        {
            var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match,
                DerivedOverlay("combat.power.omni", "flat", amount));

            var atom = Assert.Single(GrantedDerivedAtomReader.Read(store, PlantCtx()));
            Assert.Equal(12.5, atom.Amount, 6);
        }

        foreach (object amount in new object[] { 12L, 12 })
        {
            var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match,
                DerivedOverlay("combat.power.omni", "flat", amount));

            Assert.Equal(12.0, Assert.Single(GrantedDerivedAtomReader.Read(store, PlantCtx())).Amount);
        }
    }

    /// <summary>A channel delivered as a JSON string is read; a missing or blank one is skipped rather
    /// than producing an atom targeting the empty channel.</summary>
    [Fact]
    public void Channel_reads_a_JsonElement_string_and_rejects_a_blank_one()
    {
        using var doc = JsonDocument.Parse("""{"c": "combat.power.omni"}""");
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match, new Dictionary<string, object?>
        {
            [GrantedDerivedAtomReader.ChannelKey] = doc.RootElement.GetProperty("c"),
            [GrantedDerivedAtomReader.OpKey] = "flat",
            [GrantedDerivedAtomReader.AmountKey] = 5.0,
        });
        Assert.Single(GrantedDerivedAtomReader.Read(store, PlantCtx()));

        var blank = new FakeGrantStore().Add("match", EffectOwnerKeys.Match,
            DerivedOverlay("   ", "flat", 5.0));
        Assert.Empty(GrantedDerivedAtomReader.Read(blank, PlantCtx()));
    }

    [Fact]
    public void A_grant_missing_any_required_key_is_skipped()
    {
        foreach (var drop in new[] { GrantedDerivedAtomReader.ChannelKey, GrantedDerivedAtomReader.OpKey, GrantedDerivedAtomReader.AmountKey })
        {
            var overlay = DerivedOverlay("combat.power.omni", "flat", 10);
            overlay.Remove(drop);

            var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match, overlay);
            Assert.Empty(GrantedDerivedAtomReader.Read(store, PlantCtx()));
        }
    }

    /// <summary>Falls back to <c>GrantId</c> when the grant carries no <c>EffectId</c>, so a
    /// contribution is never attributed to the empty string in a contribution trace.</summary>
    [Fact]
    public void SourceId_falls_back_to_the_grant_id_when_the_effect_id_is_blank()
    {
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match,
            DerivedOverlay("combat.power.omni", "flat", 10), effectId: "", grantId: "g-42");

        Assert.Equal("grant:g-42", Assert.Single(GrantedDerivedAtomReader.Read(store, PlantCtx())).SourceId);
    }

    // ── never throw on the hot path ──────────────────────────────────────────────────────────────

    /// <summary>No live match means no bag. That is a normal state on the title screen, not an error —
    /// an exception here would fire on every resolve before a board exists.</summary>
    [Fact]
    public void A_null_store_or_context_yields_empty_never_null_and_never_throws()
    {
        Assert.Empty(GrantedDerivedAtomReader.Read(null, PlantCtx()));
        Assert.Empty(GrantedDerivedAtomReader.Read(new FakeGrantStore(), null));
        Assert.Empty(GrantedDerivedAtomReader.Read(null, null));
    }

    [Fact]
    public void A_store_that_throws_is_swallowed_not_propagated()
    {
        var store = new FakeGrantStore { ThrowOnForOwner = true };
        Assert.Empty(GrantedDerivedAtomReader.Read(store, PlantCtx()));
    }

    // ── the subsystem end of the wire ────────────────────────────────────────────────────────────

    /// <summary>End to end through the real seam: a granted derived atom reaches a composed channel
    /// value via <see cref="AtomDerivedSubsystem"/> and <c>ActorHub</c>. Proves the reader's output
    /// shape is the one the subsystem actually consumes, not merely well-formed in isolation.</summary>
    [Fact]
    public void A_granted_atom_reaches_a_composed_channel_through_the_subsystem()
    {
        var store = new FakeGrantStore().Add("match", EffectOwnerKeys.Match,
            DerivedOverlay(DerivedStatChannels.ProgressionBonusMaxHp, "flat", 5000));

        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(ctx => GrantedDerivedAtomReader.Read(store, ctx)));

        var result = hub.Resolve(PlantCtx());

        Assert.Equal(5000, result.Derived.Get(DerivedStatChannels.ProgressionBonusMaxHp, 0), 6);
        // ...and it crosses into the Writer input, which is the whole point of the lawn executor.
        Assert.False(ReferenceEquals(result.AppliedCombat, result.RuntimePrimary));
        Assert.Equal(result.RuntimePrimary.MaxHp + 5000, result.AppliedCombat.MaxHp);
    }
}
