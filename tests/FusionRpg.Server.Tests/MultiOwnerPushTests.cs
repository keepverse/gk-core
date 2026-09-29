using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// item-ideal.md, equip-runtime (module 5) — the live lawn push's missing half, named explicit and
/// deferred at P1.5, now closed: <see cref="AtomPushService.Build(System.Collections.Generic.IReadOnlyList{OwnerScope}, BindContext, ulong, string, long?, int?, int?)"/>
/// merges a player's own grants with every deployed <see cref="OwnerKind.UniqueActor"/> specimen's
/// equipped-item atoms into ONE compiled push — the shape <c>RpgHub.BuildApplyCommand</c> now sends.
///
/// <para>Same law <see cref="CompiledPushTests"/> already proves for a single owner: nothing here is a
/// live-game claim. It proves the compiled push a live match would receive is correct BEFORE reaching
/// the lawn, which is what module 5 itself named as "genuinely server-side C#, testable via
/// FusionRpg.Server.Tests without a live game."</para>
/// </summary>
public class MultiOwnerPushTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly AtomPushService _push;

    public MultiOwnerPushTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _push = new AtomPushService(_store);
        Seed();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    void Seed()
    {
        Add("atom.vitality", "stat.modify", """{"channel":"maxHp","op":"flat","amount":45}""");
        Add("atom.might", "stat.modify", """{"channel":"atk","op":"flat","amount":12}""");
        Add("atom.searing", "resource.delta",
            """{"amount":{"min":-120,"max":-80,"roll":"onApply"},"element":"fire"}""",
            """{"trigger":"OnDamageDealt","chance":250,"icd_ms":500}""");

        Container("trait.foundation", ContainerKind.Trait, "atom.vitality.t1");
        Container("item.gear", ContainerKind.Item, "atom.might.t1");
        Container("item.blade", ContainerKind.Item, "atom.searing.t1");

        void Add(string family, string kind, string paramsJson, string whenJson = "{}")
        {
            var result = _store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId(family, "", 1),
                KindId = kind, FamilyId = family, Variant = "", Tier = 1,
                Name = family, ParamsJson = paramsJson, WhenJson = whenJson,
            });
            Assert.True(result.IsOk, family + ": " + result);
        }

        void Container(string id, ContainerKind kind, params string[] atomIds) =>
            Assert.True(_store.UpsertContainer(new ContainerRow
            {
                ContainerId = id,
                Kind = kind,
                Atoms = atomIds.Select((a, i) => new ContainerAtomRow(i + 1, a)).ToArray(),
            }).IsOk, id);
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string Bind(string containerId, OwnerKind ownerKind, string ownerKey, int priority = 0)
    {
        var container = _store.GetContainer(containerId)!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);

        Assert.True(Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst).IsOk);

        var instanceId = _store.SaveInstance(inst! with { CatalogRevision = _store.GetCatalogRevision() });
        var bindingId = Guid.NewGuid().ToString("N");

        Assert.True(_store.Bind(new BindingRow
        {
            InstanceId = instanceId,
            OwnerKind = ownerKind,
            OwnerKey = ownerKey,
            Priority = priority,
            Source = "test",
        }, bindingId).IsOk);

        return bindingId;
    }

    /// <summary>
    /// P1.5-L (2026-09-07): a real, ptr-bound specimen — <see cref="AtomPushService.Build"/> now
    /// rewrites a UniqueActor-scoped grant's durable <c>instance:{id}</c> owner key to the specimen's
    /// own live <c>entity:{ptr}</c> using exactly this durably-tracked <c>LastPtr</c>, so a synthetic
    /// literal owner key with no backing row (this file's older fixture shape) can no longer reach the
    /// wire — the fix's whole point is refusing to send a key nothing can resolve. Real production
    /// sequence: Roster -&gt; Deploying -&gt; ActiveBound (<c>TryAckUniqueSpawn</c>), the same transition
    /// that stamps a live ptr outside this test file.
    /// </summary>
    string RealBoundSpecimen(string ptr)
    {
        var actor = _store.CreateUniqueActor(1, "plant", typeId: 1);
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryBeginUniqueDeploy(actor.InstanceId, corr).Ok);
        var ack = _store.TryAckUniqueSpawn(corr, ptr);
        Assert.True(ack.Ok, ack.Reason);
        return actor.InstanceId;
    }

    static BindContext Lawn() => new(RuntimeId.Lawn);

    [Fact]
    public void A_deployed_specimens_runner_atom_travels_alongside_the_players_own()
    {
        Bind("trait.foundation", OwnerKind.Player, "1");
        Bind("item.blade", OwnerKind.UniqueActor, "specimen-abc");

        var payload = _push.Build(
            new[] { new OwnerScope(OwnerKind.Player, "1"), new OwnerScope(OwnerKind.UniqueActor, "specimen-abc") },
            Lawn(), matchSeed: 7);

        // trait.foundation (stat.modify, no trigger) compiles to a Foundation grant; item.blade
        // (resource.delta, OnDamageDealt) needs the runner. Both must be present at all -- proving
        // the specimen's binding reached the payload alongside the player's own.
        Assert.NotEmpty(payload.Grants);
        Assert.NotEmpty(payload.RunnerBindings);
        Assert.All(payload.RunnerBindings, b => Assert.Equal("specimen-abc", b.OwnerKey));
    }

    [Fact]
    public void A_specimens_runner_atom_carries_its_own_ownerkey_not_the_players()
    {
        Bind("item.blade", OwnerKind.UniqueActor, "specimen-xyz");

        var payload = _push.Build(
            new[] { new OwnerScope(OwnerKind.Player, "1"), new OwnerScope(OwnerKind.UniqueActor, "specimen-xyz") },
            Lawn(), matchSeed: 7);

        // RunnerBinding.OwnerKey is stamped per (binding, atom) directly off BindingRow.OwnerKey --
        // this is the half of the push that already carries per-owner identity correctly today.
        Assert.NotEmpty(payload.RunnerBindings);
        Assert.All(payload.RunnerBindings, b => Assert.Equal("specimen-xyz", b.OwnerKey));
    }

    [Fact]
    public void Two_owners_sharing_the_same_atom_compile_to_one_catalog_entry_not_two()
    {
        // Player and specimen both wear an item.gear-equivalent atom -- the SAME atom id, resolved
        // through two different owner scopes. The union-then-compile design means this must produce
        // exactly one Defs entry for atom.might.t1, not two independently-compiled copies (which
        // would otherwise silently double the modifier if the client ever summed by def id).
        var specimen = RealBoundSpecimen("0xSHARED1");
        Bind("item.gear", OwnerKind.Player, "1");
        Bind("item.gear", OwnerKind.UniqueActor, specimen);

        var payload = _push.Build(
            new[] { new OwnerScope(OwnerKind.Player, "1"), new OwnerScope(OwnerKind.UniqueActor, specimen) },
            Lawn(), matchSeed: 7);

        var mightDefs = payload.Defs.Where(d => d.EffectId == AtomRow.DeriveId("atom.might", "", 1)).ToList();
        Assert.Single(mightDefs);

        // ONE def, and now one GRANT PER OWNER pointing at it -- see the block below. The dedup this
        // test exists for is a DEF property (the compiled content), never a grant property: a grant is
        // "this owner holds this effect", and two owners holding it is two of those.
        var mightGrants = payload.Grants
            .Where(g => g.EffectId == AtomRow.DeriveId("atom.might", "", 1)).ToList();
        Assert.Equal(2, mightGrants.Count);
        // P1.5-L: the specimen's half is now entity-scoped to ITS OWN live ptr (rewritten off the
        // durable instance: key inside Build), not left as the durable key this file's assertions used
        // to pin as the end state.
        Assert.Equal(
            new[] { FusionRpg.Contracts.EffectOwnerKeys.Entity("SHARED1"), FusionRpg.Contracts.EffectOwnerKeys.Match },
            mightGrants.Select(g => g.OwnerKey).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(2, mightGrants.Select(g => g.GrantId).Distinct(StringComparer.Ordinal).Count());
    }

    // ---- the compiled-grant gap this suite found, now closed server-side --------------------------

    [Fact]
    public void A_specimens_compiled_grant_is_scoped_to_that_specimen_not_to_the_whole_match()
    {
        // ⭐ item-ideal.md, equip-runtime (module 5), P1.5. This test previously PINNED the defect --
        // it asserted the grant came back at EffectOwnerKeys.Match -- because AtomCompiler.
        // EmitDefAndGrant never stamped an OwnerKey at all, so every COMPILED (passive, non-runner)
        // grant took the DTO default whichever owner's binding produced it. Harmless for a Player (no
        // single live entity to scope a passive buff to) and silently wrong for a UniqueActor: one
        // specimen's passive stat.derived/stat.modify gear reached every plant and zombie on the lawn.
        //
        // Closed server-side: AtomPushService now derives each binding's durable grant key from the
        // BINDING's own scope (UniqueOwnerBinder.OwnerKeyForDurableGrant) and hands the map to
        // AtomCompiler, which emits one grant per owner of an ICD group while leaving the DEF deduped
        // exactly as before.
        //
        // P1.5-L (2026-09-07): a SECOND real gap found the same way, live: the durable instance: key
        // this test used to pin as the final wire shape is exactly the one key the injector's hot path
        // refuses outright. Build now rewrites it to the specimen's own entity:{ptr} before the grant
        // ever leaves this method (using the specimen's durably-tracked LastPtr) -- proven here against
        // a real bound specimen rather than a synthetic literal with no backing row.
        var specimen = RealBoundSpecimen("0xABC123");
        Bind("trait.foundation", OwnerKind.UniqueActor, specimen);

        var payload = _push.Build(
            new[] { new OwnerScope(OwnerKind.UniqueActor, specimen) },
            Lawn(), matchSeed: 7);

        var grant = Assert.Single(payload.Grants);
        Assert.Equal(FusionRpg.Contracts.EffectOwnerKeys.Entity("ABC123"), grant.OwnerKey);
        Assert.NotEqual(FusionRpg.Contracts.EffectOwnerKeys.Match, grant.OwnerKey);
        Assert.False(FusionRpg.Core.Stats.StatApplyScope.IsInstanceOwnerKey(grant.OwnerKey));
    }

    [Fact]
    public void A_players_own_compiled_grant_is_unchanged_still_match_scoped()
    {
        // Non-regression for the pre-existing, already-shipped Player-only push: match-wide is the
        // CORRECT scope for a player's own passive buffs, so this number must not move.
        Bind("trait.foundation", OwnerKind.Player, "1");

        var payload = _push.Build(
            new[] { new OwnerScope(OwnerKind.Player, "1") }, Lawn(), matchSeed: 7);

        var grant = Assert.Single(payload.Grants);
        Assert.Equal(FusionRpg.Contracts.EffectOwnerKeys.Match, grant.OwnerKey);
        Assert.Equal("match", grant.OwnerKind);
        Assert.Equal("atom:" + AtomRow.DeriveId("atom.vitality", "", 1), grant.GrantId);
    }

    [Fact]
    public void The_stamped_grant_is_what_UniqueOwnerBinder_turns_into_a_live_entity_key()
    {
        // ⛔ THE BOUNDARY, restated after P1.5-L (2026-09-07). This test used to hand-call
        // UniqueOwnerBinder.BindGrant itself to simulate a rewrite step it documented as needing an
        // "injector-side BindGrant... [that] cannot be proven by anything CI runs" (net6.0/BepInEx
        // interop, no real PVZ Fusion install in CI). That framing turned out to be avoidable: found
        // LIVE that the rewrite is exactly as provable SERVER-side, using the specimen's own durably-
        // tracked LastPtr (RpgStore.TryAckUniqueSpawn already stores it) -- no injector build needed.
        // AtomPushService.Build now performs this rewrite itself, so the payload's own grant already
        // carries the live entity key; nothing left to hand-simulate.
        var specimen = RealBoundSpecimen("0x7ffab0");
        Bind("trait.foundation", OwnerKind.UniqueActor, specimen);

        var payload = _push.Build(
            new[] { new OwnerScope(OwnerKind.UniqueActor, specimen) },
            Lawn(), matchSeed: 7);
        var bound = Assert.Single(payload.Grants);

        // Upper-cased hex, no `0x`: MatchUniqueBindingsFacet.NormalizePtr's shape, shipped since W5-B.
        Assert.Equal("entity:7FFAB0", bound.OwnerKey);
        Assert.False(FusionRpg.Core.Match.UniqueOwnerBinder.WouldRejectOnHot(bound.OwnerKey));
        Assert.True(FusionRpg.Core.Stats.StatApplyScope.Matches(
            bound.OwnerKey, FusionRpg.Core.Stats.StatSide.Plant, typeId: 0, entityKey: "7ffab0"));
    }

    [Fact]
    public void A_specimen_with_no_live_ptr_yet_contributes_no_grant_rather_than_an_unresolvable_one()
    {
        // P1.5-L (2026-09-07): the real remaining fail-closed edge, restated after the fix. A specimen
        // that is bound (has an effect_binding) but has NEVER been deployed -- so RpgStore has no ptr
        // to rewrite against -- must not put a durable instance: key on the wire (guaranteed refusal by
        // the injector's hot path) and must not silently widen to match-wide either. Dropping it is the
        // documented safe direction; the NEXT re-push after a real deploy carries it correctly.
        var actor = _store.CreateUniqueActor(1, "plant", typeId: 1);
        Bind("trait.foundation", OwnerKind.UniqueActor, actor.InstanceId);

        var payload = _push.Build(
            new[] { new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId) },
            Lawn(), matchSeed: 7);

        Assert.Empty(payload.Grants);
        // The def itself still compiles -- only the grant (which needs a live owner to scope to) is held.
        Assert.Contains(payload.Defs, d => d.EffectId == AtomRow.DeriveId("atom.vitality", "", 1));
    }

    [Fact]
    public void An_undeployed_specimen_that_is_never_passed_contributes_nothing()
    {
        // A roster-only specimen (not ActiveBound) is simply never in the owners list -- RpgHub's own
        // job, proven separately. Here: an owner scope that IS passed but has no bindings at all
        // must not error and must not appear in the payload.
        Bind("trait.foundation", OwnerKind.Player, "1");

        var payload = _push.Build(
            new[] { new OwnerScope(OwnerKind.Player, "1"), new OwnerScope(OwnerKind.UniqueActor, "roster-only-specimen") },
            Lawn(), matchSeed: 7);

        Assert.DoesNotContain(payload.Grants, g => g.OwnerKey == "roster-only-specimen");
        Assert.DoesNotContain(payload.RunnerBindings, b => b.OwnerKey == "roster-only-specimen");
    }

    [Fact]
    public void The_single_owner_overload_is_unchanged_and_still_delegates_correctly()
    {
        // Regression pin: the pre-existing single-owner Build(...) call sites (CompiledPushTests'
        // whole suite) must keep working byte-identically now that it forwards to the multi-owner one.
        Bind("item.blade", OwnerKind.Player, "1");

        var single = _push.Build(new OwnerScope(OwnerKind.Player, "1"), Lawn(), matchSeed: 7);
        var multi = _push.Build(new[] { new OwnerScope(OwnerKind.Player, "1") }, Lawn(), matchSeed: 7);

        Assert.Equal(single.RunnerBindings.Count, multi.RunnerBindings.Count);
        Assert.Equal(
            single.RunnerBindings.Select(b => b.BindingId).OrderBy(x => x),
            multi.RunnerBindings.Select(b => b.BindingId).OrderBy(x => x));
    }

    [Fact]
    public void Zero_owners_is_refused_rather_than_silently_returning_an_empty_payload()
    {
        Assert.Throws<ArgumentException>(() => _push.Build(Array.Empty<OwnerScope>(), Lawn(), matchSeed: 7));
    }

    // ---- T6.2: the per-owner grant reaches the WIRE, not just the DTO ------------------------------
    //
    // ⛔ The gap this suite NAMED on 2026-09-06 and did not close: "`AtomPushDto.Grants` never reaches
    // the wire at all. Both server call sites build it and drop it." Verified still true at the time
    // of this pass and now closed -- so everything the block above proves about per-owner scope was,
    // until here, proven about a value nothing transmitted.

    [Fact]
    public void A_specimens_scoped_grant_survives_all_the_way_onto_the_wire_payload()
    {
        var specimen = RealBoundSpecimen("0xDEF456");
        Bind("trait.foundation", OwnerKind.UniqueActor, specimen);
        Bind("item.gear", OwnerKind.Player, "1");

        var atoms = _push.Build(
            new[] { new OwnerScope(OwnerKind.Player, "1"), new OwnerScope(OwnerKind.UniqueActor, specimen) },
            Lawn(), matchSeed: 7);

        var grants = Assert.IsType<List<FusionRpg.Contracts.EffectGrantDto>>(
            AtomPushService.BuildApplyPayload(atoms, sessionGrants: null)["grants"]);

        // Both owners' compiled grants -- the specimen's half entity-scoped to its own live ptr
        // (P1.5-L's rewrite), the player's half unchanged match-wide.
        Assert.Equal(2, grants.Count);
        var entityKey = FusionRpg.Contracts.EffectOwnerKeys.Entity("DEF456");
        Assert.Equal(
            new[] { entityKey, FusionRpg.Contracts.EffectOwnerKeys.Match },
            grants.Select(g => g.OwnerKey).OrderBy(k => k, StringComparer.Ordinal));

        var scoped = Assert.Single(grants, g => g.OwnerKey == entityKey);
        Assert.Equal(AtomRow.DeriveId("atom.vitality", "", 1), scoped.EffectId);
    }

    [Fact]
    public void The_owner_key_is_not_flattened_by_serialisation()
    {
        // The wire is where an owner key would silently be lost -- EffectGrantDto.OwnerKey has to
        // actually serialise for the injector's grant loop to read it back correctly.
        var specimen = RealBoundSpecimen("0x9988");
        Bind("trait.foundation", OwnerKind.UniqueActor, specimen);

        var atoms = _push.Build(
            new[] { new OwnerScope(OwnerKind.UniqueActor, specimen) }, Lawn(), matchSeed: 7);
        var json = System.Text.Json.JsonSerializer.Serialize(
            AtomPushService.BuildApplyPayload(atoms, sessionGrants: null));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var raw = Assert.Single(doc.RootElement.GetProperty("grants").EnumerateArray().ToList());
        var ownerKey = raw.GetProperty("ownerKey").GetString();
        Assert.Equal(FusionRpg.Contracts.EffectOwnerKeys.Entity("9988"), ownerKey);

        // P1.5-L: this is now the entity-scoped, ACCEPTED shape -- the fail-closed contract this test
        // used to prove (instance: surviving the wire, then refused) is now unreachable by construction:
        // the rewrite happens before the payload is ever built, so there is no instance: key to refuse.
        Assert.False(FusionRpg.Core.Match.UniqueOwnerBinder.WouldRejectOnHot(ownerKey));
    }

    [Fact]
    public void Two_owners_of_one_atom_put_two_distinct_grants_on_the_wire_over_one_def()
    {
        // The wire half of Two_owners_sharing_the_same_atom_compile_to_one_catalog_entry_not_two.
        // EffectBag's grant store is keyed on GrantId: two owners' grants collapsing to one id here
        // would silently drop an owner at the far end, which no DTO-only assertion can see.
        var specimen = RealBoundSpecimen("0xSHARED2");
        Bind("item.gear", OwnerKind.Player, "1");
        Bind("item.gear", OwnerKind.UniqueActor, specimen);

        var atoms = _push.Build(
            new[] { new OwnerScope(OwnerKind.Player, "1"), new OwnerScope(OwnerKind.UniqueActor, specimen) },
            Lawn(), matchSeed: 7);
        var json = System.Text.Json.JsonSerializer.Serialize(
            AtomPushService.BuildApplyPayload(atoms, sessionGrants: null));

        var round = System.Text.Json.JsonSerializer.Deserialize<FusionRpg.Contracts.AtomPushDto>(json)!;
        var might = round.Grants.Where(g => g.EffectId == AtomRow.DeriveId("atom.might", "", 1)).ToList();

        Assert.Equal(2, might.Count);
        Assert.Equal(2, might.Select(g => g.GrantId).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(round.Defs, d => d.EffectId == AtomRow.DeriveId("atom.might", "", 1));
    }
}
