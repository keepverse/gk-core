using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match;
using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// item-ideal.md, `equip-runtime` (module 5), P1.5 — the COMPILED (passive) half of the live lawn
/// push, which until now carried no owner identity at all.
///
/// <para><b>What was wrong.</b> <c>AtomCompiler.EmitDefAndGrant</c> never stamped an
/// <c>EffectGrantDto.OwnerKey</c>, so every compiled grant took the DTO default,
/// <see cref="EffectOwnerKeys.Match"/>, whichever owner's binding produced it. Harmless for
/// <see cref="OwnerKind.Player"/> — a player has no single live entity for a passive buff to sit on —
/// and silently wrong for <see cref="OwnerKind.UniqueActor"/>, a specific live entity, whose passive
/// gear then applied to every plant and zombie on the lawn instead of to the one wearing it.</para>
///
/// <para><b>Where the fix stops, precisely.</b> The stamp is durable
/// (<c>instance:{specimenId}</c>) and the hot path REFUSES that key by design
/// (<c>StatApplyScope.Matches</c> returns false, <c>EffectBag.Grant</c> throws). Something has to
/// rewrite it to <c>entity:{ptr}</c> once the specimen actually spawns, and that something is
/// <see cref="UniqueOwnerBinder.BindGrant"/> — proven here against a real stamped grant. The CALLER
/// of it at spawn time is injector-side and is not built or tested by anything CI runs
/// (<c>GrantedDerivedAtomReader</c>'s own doc comment: net6.0 + BepInEx/Il2Cpp interop, needs a real
/// PVZ Fusion install). So: both ends of the handoff are proven here, the wire between them is not.
/// The failure mode if it is never wired is loud, not silent — see
/// <see cref="An_unbound_instance_key_is_still_hot_path_illegal_so_nothing_reaches_the_runtime_unrewritten"/>.</para>
/// </summary>
public class CompiledGrantOwnerScopeTests
{
    static AtomRow Atom(
        string family,
        string paramsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        string? icdKey = null) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.modify",
        FamilyId = family,
        Variant = "",
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = "{}",
        IcdKey = icdKey,
    };

    static CompiledCatalog Compile(
        Func<string, IReadOnlyCollection<string>?>? owners, params AtomRow[] atoms) =>
        AtomCompiler.Compile(atoms, RuntimeId.Lawn, catalogRevision: 1, grantOwnerKeys: owners);

    static Func<string, IReadOnlyCollection<string>?> Owners(
        params (string AtomId, string[] Keys)[] rows)
    {
        var map = rows.ToDictionary(r => r.AtomId, r => (IReadOnlyCollection<string>)r.Keys,
            StringComparer.Ordinal);
        return id => map.TryGetValue(id, out var keys) ? keys : null;
    }

    static string SpecimenKey(string instanceId) =>
        UniqueOwnerBinder.OwnerKeyForDurableGrant(new OwnerScope(OwnerKind.UniqueActor, instanceId));

    // ---- (b) non-regression: everything that was match-scoped stays match-scoped ------------------

    [Fact]
    public void With_no_owner_map_a_compiled_grant_is_byte_for_byte_what_it_always_was()
    {
        var atom = Atom("atom.vitality");

        var grant = Assert.Single(Compile(null, atom).Compiled);

        Assert.Equal("atom:" + atom.AtomId, grant.GrantId);
        Assert.Equal(atom.AtomId, grant.EffectId);
        Assert.Equal(EffectOwnerKeys.Match, grant.OwnerKey);
        Assert.Equal("match", grant.OwnerKind);
        Assert.Equal("atom", grant.PluginId);
        Assert.Equal(0, grant.Priority);
    }

    [Fact]
    public void A_Player_sourced_compiled_grant_is_unchanged_still_match()
    {
        // A player owns no single live entity, so match-wide is CORRECT here rather than merely
        // tolerated -- and this is also the pre-existing shipped push, which must not move.
        var atom = Atom("atom.vitality");
        var player = UniqueOwnerBinder.OwnerKeyForDurableGrant(new OwnerScope(OwnerKind.Player, "1"));

        var grant = Assert.Single(
            Compile(Owners((atom.AtomId, new[] { player })), atom).Compiled);

        Assert.Equal(EffectOwnerKeys.Match, grant.OwnerKey);
        Assert.Equal("atom:" + atom.AtomId, grant.GrantId);
    }

    [Fact]
    public void Only_UniqueActor_earns_a_scoped_key_every_other_scope_answers_match()
    {
        Assert.Equal("instance:specimen-abc", SpecimenKey("specimen-abc"));

        foreach (var scope in new[]
                 {
                     OwnerScope.Match,
                     new OwnerScope(OwnerKind.Player, "1"),
                     new OwnerScope(OwnerKind.Plant, "12"),
                     new OwnerScope(OwnerKind.Zombie, "3"),
                     new OwnerScope(OwnerKind.Entity, "7ffab0"),
                     new OwnerScope(OwnerKind.Sector, "north-reach"),
                     new OwnerScope(OwnerKind.Slot, "forge-a"),
                 })
            Assert.Equal(EffectOwnerKeys.Match, UniqueOwnerBinder.OwnerKeyForDurableGrant(scope));

        // A UniqueActor scope with no key is a malformed row, not a specimen -- it must not produce
        // `instance:` with an empty id, which BindGrant could never rewrite to anything meaningful.
        Assert.Equal(EffectOwnerKeys.Match,
            UniqueOwnerBinder.OwnerKeyForDurableGrant(new OwnerScope(OwnerKind.UniqueActor, "")));
    }

    // ---- (a) the fix itself ------------------------------------------------------------------------

    [Fact]
    public void A_UniqueActor_sourced_compiled_grant_carries_instance_plus_the_specimen_id()
    {
        var atom = Atom("atom.precision");

        var grant = Assert.Single(
            Compile(Owners((atom.AtomId, new[] { SpecimenKey("specimen-abc") })), atom).Compiled);

        Assert.Equal("instance:specimen-abc", grant.OwnerKey);

        // In the StatApplyScope-normalized form, not merely "a string starting with instance:" --
        // UniqueOwnerBinder.ExtractInstanceId normalizes before slicing, so a key that is not already
        // canonical would round-trip to a different id than the one stamped.
        Assert.Equal(StatApplyScope.Normalize(grant.OwnerKey), grant.OwnerKey);
        Assert.True(StatApplyScope.IsInstanceOwnerKey(grant.OwnerKey));

        // The ownerKind that ships beside a durable key everywhere else (UniqueEquipmentCatalog.Grant,
        // RelicCatalog.TryGetGrant) -- mirrored, not invented here.
        Assert.Equal(EffectOwnerKeys.InstanceKind, grant.OwnerKind);

        // Id carries the owner too: two owners' grants on one def share a def but never a grant id,
        // and EffectBag's grant store is keyed by GrantId.
        Assert.Equal("atom:" + atom.AtomId + "@instance:specimen-abc", grant.GrantId);
    }

    [Fact]
    public void Two_owners_sharing_one_atom_get_one_def_and_one_grant_each()
    {
        // The union-then-compile rule AtomPushService.Build established stays intact: the DEF is
        // deduped by ICD key. What changes is that the def now has a grant per owner rather than one
        // match-wide grant standing in for both -- so the specimen's own copy of the item applies to
        // the specimen, and the player's applies match-wide, which is two real sources stacking.
        var atom = Atom("atom.might", "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":12}");
        var player = UniqueOwnerBinder.OwnerKeyForDurableGrant(new OwnerScope(OwnerKind.Player, "1"));

        var catalog = Compile(
            Owners((atom.AtomId, new[] { player, SpecimenKey("specimen-shared") })), atom);

        Assert.Single(catalog.Defs);
        Assert.Equal(2, catalog.Compiled.Count);
        Assert.All(catalog.Compiled, g => Assert.Equal(atom.AtomId, g.EffectId));
        Assert.Equal(
            new[] { EffectOwnerKeys.Match, "instance:specimen-shared" }.OrderBy(k => k, StringComparer.Ordinal),
            catalog.Compiled.Select(g => g.OwnerKey).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(2, catalog.Compiled.Select(g => g.GrantId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Two_specimens_wearing_the_same_atom_each_get_their_own_key()
    {
        var atom = Atom("atom.might", "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":12}");

        var catalog = Compile(
            Owners((atom.AtomId, new[] { SpecimenKey("specimen-a"), SpecimenKey("specimen-b") })), atom);

        Assert.Single(catalog.Defs);
        Assert.Equal(
            new[] { "instance:specimen-a", "instance:specimen-b" },
            catalog.Compiled.Select(g => g.OwnerKey).OrderBy(k => k, StringComparer.Ordinal));
        Assert.DoesNotContain(catalog.Compiled, g => g.OwnerKey == EffectOwnerKeys.Match);
    }

    [Fact]
    public void An_atom_the_owner_map_says_nothing_about_stays_match()
    {
        // Defensive, and the shape a caller hits when an atom reaches the compile through a path that
        // never went through a binding at all: absent must mean "as before", never "unowned, so drop".
        var known = Atom("atom.might", "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":12}");
        var unknown = Atom("atom.vitality");

        var catalog = Compile(
            Owners((known.AtomId, new[] { SpecimenKey("specimen-abc") })), known, unknown);

        var stray = Assert.Single(catalog.Compiled, g => g.EffectId == unknown.AtomId);
        Assert.Equal(EffectOwnerKeys.Match, stray.OwnerKey);
    }

    // ---- the collision the union-compile design cannot express ------------------------------------

    [Fact]
    public void An_icd_group_whose_atoms_come_from_DIFFERENT_owners_falls_back_to_the_shipped_behaviour()
    {
        // Two atoms, one authored icd_key, two different owners. The def is the ICD GROUP, so it
        // carries the union of both actions -- handing that def to each owner separately would give
        // each the other's action, twice over. There is no def-per-owner available (EffectId IS the
        // ICD key), so this refuses to amplify and emits exactly what shipped before: one match grant.
        var mine = Atom("atom.might", "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":12}", icdKey: "fx.shared");
        var theirs = Atom("atom.vitality", icdKey: "fx.shared");

        var catalog = Compile(
            Owners(
                (mine.AtomId, new[] { UniqueOwnerBinder.OwnerKeyForDurableGrant(new OwnerScope(OwnerKind.Player, "1")) }),
                (theirs.AtomId, new[] { SpecimenKey("specimen-abc") })),
            mine, theirs);

        Assert.Single(catalog.Defs);
        var grant = Assert.Single(catalog.Compiled);
        Assert.Equal(EffectOwnerKeys.Match, grant.OwnerKey);
        Assert.Equal("atom:fx.shared", grant.GrantId);
    }

    [Fact]
    public void An_icd_group_whose_atoms_come_from_the_SAME_owner_is_still_scoped()
    {
        // The homogeneous case must not be collateral damage of the rule above: one specimen wearing a
        // two-atom set that shares an icd_key still gets its own scoped grant.
        var one = Atom("atom.might", "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":12}", icdKey: "fx.set");
        var two = Atom("atom.vitality", icdKey: "fx.set");
        var key = SpecimenKey("specimen-abc");

        var catalog = Compile(Owners((one.AtomId, new[] { key }), (two.AtomId, new[] { key })), one, two);

        Assert.Single(catalog.Defs);
        var grant = Assert.Single(catalog.Compiled);
        Assert.Equal("instance:specimen-abc", grant.OwnerKey);
    }

    // ---- (c) the handoff to the binder -------------------------------------------------------------

    [Fact]
    public void UniqueOwnerBinder_rewrites_a_real_stamped_grant_to_the_live_entity_key()
    {
        // The two halves actually connect. The grant here is not hand-written: it is the real output
        // of AtomCompiler for a UniqueActor-sourced atom, handed straight to the shipped binder.
        var atom = Atom("atom.precision");
        var stamped = Assert.Single(
            Compile(Owners((atom.AtomId, new[] { SpecimenKey("specimen-abc") })), atom).Compiled);

        var bound = UniqueOwnerBinder.BindGrant(stamped, "0x7FFAB0");

        // `0x` stripped and the hex UPPER-cased: ToEntityKey goes through
        // MatchUniqueBindingsFacet.NormalizePtr (`p.ToUpperInvariant()`), not StatApplyScope.Normalize
        // (which lower-cases). ⚠ Pre-existing since W5-B -- UniqueLoadoutSpec.BindToPtr has always
        // produced this shape -- and worth naming rather than quietly matching: the key is NOT in the
        // canonical form the rest of the grammar defines, and `OwnerScope.Validate(OwnerKind.Entity,
        // ...)` would refuse it for exactly that reason (its HexRe is `^[0-9a-f]+$`). Harmless where
        // it is actually consumed, because every ptr comparison downstream is OrdinalIgnoreCase.
        // Asserted the way it behaves, not the way it reads.
        Assert.Equal("entity:7FFAB0", bound.OwnerKey);
        Assert.Equal("entity:7ffab0", StatApplyScope.Normalize(bound.OwnerKey));
        Assert.False(UniqueOwnerBinder.WouldRejectOnHot(bound.OwnerKey));

        // Everything else rides through untouched -- the binder rewrites one field, and the grant id
        // in particular must survive so a withdraw still matches what was applied.
        Assert.Equal(stamped.GrantId, bound.GrantId);
        Assert.Equal(stamped.EffectId, bound.EffectId);
        Assert.Equal(stamped.PluginId, bound.PluginId);
        Assert.Equal(stamped.OwnerKind, bound.OwnerKind);

        // And once bound it resolves on the hot path for THAT entity and no other.
        Assert.True(StatApplyScope.Matches(bound.OwnerKey, StatSide.Plant, typeId: 0, entityKey: "7ffab0"));
        Assert.False(StatApplyScope.Matches(bound.OwnerKey, StatSide.Plant, typeId: 0, entityKey: "deadbe"));
    }

    [Fact]
    public void Binding_a_match_scoped_grant_leaves_it_match_scoped()
    {
        // The player's own grants travel on the same push. A binder call that swept them up must not
        // quietly narrow a match-wide buff onto whichever specimen happened to spawn.
        var atom = Atom("atom.vitality");
        var grant = Assert.Single(Compile(null, atom).Compiled);

        Assert.Equal(EffectOwnerKeys.Match, UniqueOwnerBinder.BindGrant(grant, "0x7FFAB0").OwnerKey);
    }

    // ---- (d) the fail-closed guarantee -------------------------------------------------------------

    [Fact]
    public void An_unbound_instance_key_is_still_hot_path_illegal_so_nothing_reaches_the_runtime_unrewritten()
    {
        var atom = Atom("atom.precision");
        var stamped = Assert.Single(
            Compile(Owners((atom.AtomId, new[] { SpecimenKey("specimen-abc") })), atom).Compiled);

        // This is the whole reason `instance:` is the right stamp rather than, say, a bare specimen id:
        // if the injector-side BindGrant call is never wired, the grant is REFUSED, loudly and by
        // several independent gates -- it does not quietly fall back to applying match-wide, which is
        // exactly the defect this pass exists to close.
        Assert.True(UniqueOwnerBinder.WouldRejectOnHot(stamped.OwnerKey));
        Assert.False(StatApplyScope.Matches(stamped.OwnerKey, StatSide.Plant, typeId: 0, entityKey: "7ffab0"));
        Assert.False(StatApplyScope.IsMatchWide(stamped.OwnerKey));
        Assert.False(StatApplyScope.IsKnownOwnerKey(stamped.OwnerKey));
    }

    // ---- determinism -------------------------------------------------------------------------------

    [Fact]
    public void The_same_owners_in_a_different_order_bake_the_same_bytes()
    {
        // A push is compared against what the injector already holds, so the same revision must bake
        // identically no matter what order the caller happened to collect its bindings in.
        var atom = Atom("atom.might", "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":12}");
        var a = SpecimenKey("specimen-a");
        var b = SpecimenKey("specimen-b");

        var first = Compile(Owners((atom.AtomId, new[] { a, b })), atom).Compiled;
        var second = Compile(Owners((atom.AtomId, new[] { b, a, a })), atom).Compiled;

        Assert.Equal(
            first.Select(g => g.GrantId + "|" + g.OwnerKey + "|" + g.OwnerKind),
            second.Select(g => g.GrantId + "|" + g.OwnerKey + "|" + g.OwnerKind));
    }

    [Fact]
    public void Each_owners_grant_holds_its_own_overlay_not_a_shared_reference()
    {
        // Chance / icd_ms / filters ride the overlay. Two grants aliasing one dictionary would let a
        // later mutation of one silently rewrite the other's.
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.searing", "", 1),
            KindId = "stat.modify",
            FamilyId = "atom.searing",
            Variant = "",
            Tier = 1,
            Name = "atom.searing",
            ParamsJson = "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":5}",
            WhenJson = "{\"icd_ms\":500}",
        };

        var grants = Compile(
            Owners((atom.AtomId, new[] { SpecimenKey("specimen-a"), SpecimenKey("specimen-b") })), atom)
            .Compiled;

        Assert.Equal(2, grants.Count);
        Assert.All(grants, g => Assert.Equal(500, Convert.ToInt32(g.Overlay!["icd_ms"])));
        Assert.NotSame(grants[0].Overlay, grants[1].Overlay);
    }
}
