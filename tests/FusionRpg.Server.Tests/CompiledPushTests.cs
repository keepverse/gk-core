using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// E19 (spec-compiled-push.md), server half: what leaves the server for a connected injector.
///
/// <para>The guarantee is the one that makes the Cold/Hot split legal — <b>the injector never holds
/// content rows</b>. Everything here is already compiled: predicates as flat int ops, values as
/// curve-scaled bounds, names interned away.</para>
/// </summary>
public class CompiledPushTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly AtomPushService _push;

    static readonly JsonSerializerOptions Wire = new();

    public CompiledPushTests()
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

    /// <summary>Two runner atoms (an OnApply range and a capped economy) plus one that compiles.</summary>
    void Seed()
    {
        Add("atom.searing", "resource.delta",
            """{"amount":{"min":-120,"max":-80,"roll":"onApply"},"element":"fire"}""",
            """{"trigger":"OnDamageDealt","chance":250,"icd_ms":500}""");

        Add("atom.sun-tap", "resource.economy",
            """{"currency":"sun","op":"add","amount":25,"capPerMatch":3}""",
            """{"trigger":"OnDamageDealt"}""");

        Add("atom.vitality", "stat.modify", """{"channel":"maxHp","op":"flat","amount":45}""");

        Container("item.blade", ContainerKind.Item, "atom.searing.t1", "atom.sun-tap.t1");
        Container("trait.stalwart", ContainerKind.Trait, "atom.vitality.t1");

        void Add(string family, string kind, string paramsJson, string whenJson = "{}")
        {
            var result = _store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId(family, "", 1),
                KindId = kind, FamilyId = family, Variant = "", Tier = 1,
                Name = family, ParamsJson = paramsJson, WhenJson = whenJson,
            });
            // The reason, not just the id: a seed rejected for an unrelated schema change should
            // say so rather than fail every test in the class with a bare family name.
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

    // T3.4 (content-scale): 20 is the pin -- contentScale(20) == 1.000 exactly.
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, // fixed anchor (Fixed* consts are `internal` to Core+Core.Tests only)
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string Bind(string containerId, string ownerKey, int priority = 0)
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
            OwnerKind = OwnerKind.Player,
            OwnerKey = ownerKey,
            Priority = priority,
            Source = "test",
        }, bindingId).IsOk);

        return bindingId;
    }

    static OwnerScope Owner(string key = "1") => new(OwnerKind.Player, key);
    static BindContext Lawn() => new(RuntimeId.Lawn);

    // ---- delivery ----------------------------------------------------------------------------------

    [Fact]
    public void A_cold_start_receives_the_full_set()
    {
        Bind("item.blade", "1");

        var payload = _push.Build(Owner(), Lawn(), matchSeed: 7, matchKey: "m1");

        Assert.False(payload.UpToDate);
        Assert.NotEmpty(payload.RunnerBindings);
        Assert.Equal(_store.GetCatalogRevision(), payload.CatalogRevision);
    }

    [Fact]
    public void A_receiver_already_on_this_revision_gets_an_empty_apply()
    {
        Bind("item.blade", "1");
        var revision = _store.GetCatalogRevision();

        // E26: the short-circuit is two-term now (spec-runner-def-emit.md §3.3) — the receiver must
        // also echo the current EmitterVersion, or a compiler-code change with no revision bump would
        // never re-push.
        var payload = _push.Build(Owner(), Lawn(), matchSeed: 7,
            receiverRevision: revision, receiverEmitterVersion: AtomPushCodec.EmitterVersion);

        Assert.True(payload.UpToDate);
        Assert.Empty(payload.RunnerBindings);
        Assert.Empty(payload.Grants);
        Assert.Empty(payload.Defs);
    }

    [Fact]
    public void An_up_to_date_reply_still_carries_the_content_hash_and_the_seed()
    {
        // A reconnect that delivers no content must still make a content mismatch visible, and the
        // injector still needs the seed to roll reproducibly.
        Bind("item.blade", "1");

        var payload = _push.Build(
            Owner(), Lawn(), matchSeed: 99, matchKey: "m1",
            receiverRevision: _store.GetCatalogRevision(), receiverEmitterVersion: AtomPushCodec.EmitterVersion);

        Assert.True(payload.UpToDate);
        Assert.False(string.IsNullOrEmpty(payload.ContentHash));
        Assert.Equal(99UL, payload.MatchSeed);
        Assert.Equal("m1", payload.MatchKey);
    }

    [Fact]
    public void A_stale_revision_gets_the_full_set_again_not_a_delta()
    {
        Bind("item.blade", "1");

        var payload = _push.Build(Owner(), Lawn(), matchSeed: 7, receiverRevision: -1);

        Assert.False(payload.UpToDate);
        Assert.NotEmpty(payload.RunnerBindings);
    }

    [Fact]
    public void An_owner_with_no_bindings_gets_a_payload_not_an_error()
    {
        // A match with no pushed bindings runs with none. That is a normal state, not an error.
        var payload = _push.Build(Owner("does-not-exist"), Lawn(), matchSeed: 7);

        Assert.Empty(payload.RunnerBindings);
        Assert.False(payload.UpToDate);
    }

    [Fact]
    public void Only_the_requested_owners_bindings_travel()
    {
        Bind("item.blade", "1");
        Bind("item.blade", "2");

        var mine = _push.Build(Owner("1"), Lawn(), matchSeed: 7);

        Assert.NotEmpty(mine.RunnerBindings);
        Assert.All(mine.RunnerBindings, b => Assert.Equal("1", b.OwnerKey));
    }

    // ---- the (binding, atom) identity ---------------------------------------------------------------

    [Fact]
    public void A_container_with_two_runner_atoms_yields_two_independently_keyed_bindings()
    {
        // Two runner atoms in one container need two ICD clocks and two caps. A shared id would
        // merge them AND tie the evaluation sort, making order depend on how rows happened to arrive.
        var bindingId = Bind("item.blade", "1");

        var payload = _push.Build(Owner(), Lawn(), matchSeed: 7);

        Assert.Equal(2, payload.RunnerBindings.Count);
        Assert.Equal(2, payload.RunnerBindings.Select(b => b.BindingId).Distinct().Count());
        Assert.All(payload.RunnerBindings, b => Assert.StartsWith(bindingId + "#", b.BindingId));
    }

    [Fact]
    public void The_delivered_set_builds_a_trigger_index_with_no_duplicate_slots()
    {
        Bind("item.blade", "1");

        var payload = _push.Build(Owner(), Lawn(), matchSeed: 7);
        var index = TriggerIndex.Build(AtomPushCodec.DecodeBindings(payload));

        Assert.Equal(2, index.Count);
        Assert.Equal(2, index.SlotsFor(AtomTriggers.OnDamageDealt).Length);
    }

    [Fact]
    public void The_cap_that_routed_an_atom_to_the_runner_arrives_intact()
    {
        Bind("item.blade", "1");

        var payload = _push.Build(Owner(), Lawn(), matchSeed: 7);
        var entries = AtomPushCodec.DecodeBindings(payload).Select(b => b.Entry).ToList();

        var economy = Assert.Single(entries.Where(e => e.KindId == "resource.economy"));
        Assert.Equal(3, economy.Limits.CapPerMatch);
    }

    [Fact]
    public void A_compiled_atom_travels_as_a_grant_not_as_a_runner_entry()
    {
        // The compile/run split, end to end: a permanent modifier is an ordinary Foundation grant
        // with zero runtime cost, and only what Foundation cannot express reaches the runner.
        Bind("trait.stalwart", "1");

        var payload = _push.Build(Owner(), Lawn(), matchSeed: 7);

        Assert.Empty(payload.RunnerBindings);
        Assert.NotEmpty(payload.Grants);
        Assert.NotEmpty(payload.Defs);
    }

    // ---- the guarantee -------------------------------------------------------------------------------

    [Fact]
    public void The_payload_carries_no_content_row_of_any_kind()
    {
        Bind("item.blade", "1");
        Bind("trait.stalwart", "1");

        var json = JsonSerializer.Serialize(_push.Build(Owner(), Lawn(), matchSeed: 7, matchKey: "m1"), Wire);

        foreach (var column in new[]
                 {
                     "when_json", "params_json", "tags_json", "points_json", "overrides_json",
                     "family_id", "container_id", "curve_id", "rarity_id", "group_key",
                     "instance_id", "roll_seed", "power_json",
                 })
            Assert.DoesNotContain(column, json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the per-match seed (D5) ---------------------------------------------------------------------

    [Fact]
    public void The_seed_for_a_match_key_is_stable_across_calls()
    {
        Assert.Equal(MatchSeed.For("m-123"), MatchSeed.For("m-123"));
    }

    [Fact]
    public void Different_match_keys_get_different_seeds()
    {
        Assert.NotEqual(MatchSeed.For("m-123"), MatchSeed.For("m-124"));
    }

    [Fact]
    public void The_seed_is_a_named_hash_not_the_randomised_runtime_one()
    {
        // String.GetHashCode is randomised per process: using it would make "same match key, same
        // rolls" false on every restart, silently. FNV-1a of "m-123", computed independently here.
        var expected = 14695981039346656037UL;
        foreach (var ch in "m-123")
        {
            expected ^= ch;
            expected *= 1099511628211UL;
        }

        Assert.Equal(expected, MatchSeed.For("m-123"));
    }

    [Fact]
    public void An_absent_match_key_seeds_zero_rather_than_throwing()
    {
        Assert.Equal(0UL, MatchSeed.For(null));
        Assert.Equal(0UL, MatchSeed.For(""));
    }

    // ---- T6.2: the assembled WIRE payload, not just the DTO -----------------------------------------
    //
    // ⛔ The defect this block exists for, found 2026-09-06 and PRE-EXISTING (true of the original
    // Player-only push, long before any equip-runtime work): `AtomPushDto.Grants` is filled by
    // AtomPushCodec.BuildPayload:188 from `catalog.Compiled` and was then DROPPED by both server call
    // sites, each of which hand-rolled its own payload dictionary carrying `defs` + `runnerBindings`
    // and nothing else. Every test above asserts the DTO; none asserted what actually goes on the
    // wire, which is why a whole half of the push could be inert without a single red test.
    //
    // A def with no grant naming it does nothing: EffectBag holds the content and never applies it.

    [Fact]
    public void The_wire_payload_carries_the_compiled_grants_not_just_the_defs()
    {
        Bind("trait.stalwart", "1"); // stat.modify, no trigger -> compiles to a grant, not a runner entry

        var atoms = _push.Build(Owner(), Lawn(), matchSeed: 7, matchKey: "m1");
        Assert.NotEmpty(atoms.Grants); // precondition: the DTO half already worked

        var payload = AtomPushService.BuildApplyPayload(atoms, sessionGrants: null);

        var grants = Assert.IsType<List<FusionRpg.Contracts.EffectGrantDto>>(payload["grants"]);
        var grant = Assert.Single(grants);
        Assert.Equal(AtomRow.DeriveId("atom.vitality", "", 1), grant.EffectId);
        Assert.Equal(FusionRpg.Contracts.EffectOwnerKeys.Match, grant.OwnerKey);
    }

    [Fact]
    public void The_session_snapshot_and_the_compiled_grants_share_one_array_session_first()
    {
        // One key, not two: the injector's RunEffectsGrantsApply loops `grants[]` -> RunEffectGrant,
        // and AtomPushReceiver.Install deliberately does NOT apply AtomPushDto.Grants ("the command
        // runner's existing grant loop owns that" -- its own doc comment). A separately-named key
        // would have been read by nothing at all.
        Bind("trait.stalwart", "1");

        var session = new[]
        {
            new FusionRpg.Contracts.EffectGrantDto
            {
                GrantId = "session-1", EffectId = "fx.session", OwnerKind = "match",
                OwnerKey = FusionRpg.Contracts.EffectOwnerKeys.Match, PluginId = "test",
            },
        };

        var atoms = _push.Build(Owner(), Lawn(), matchSeed: 7);
        var grants = Assert.IsType<List<FusionRpg.Contracts.EffectGrantDto>>(
            AtomPushService.BuildApplyPayload(atoms, session)["grants"]);

        Assert.Equal(2, grants.Count);
        Assert.Equal("session-1", grants[0].GrantId);          // pre-existing half, order untouched
        Assert.StartsWith("atom:", grants[1].GrantId);          // compiled half, appended
    }

    [Fact]
    public void The_compiled_grants_survive_the_injectors_own_two_reads_of_the_same_payload()
    {
        // The real receive path reads this payload TWICE, two different ways, and both must see it:
        //   1. RunEffectsGrantsApply: p.TryGetProperty("grants") -> EnumerateArray -> RunEffectGrant
        //   2. InstallAtomPush: JsonSerializer.Deserialize<AtomPushDto>(p.GetRawText())
        // AtomPushDto.Grants is declared [JsonPropertyName("grants")], so read 2 lands on the same key
        // read 1 walks -- which is exactly why the compiled grants belong there and nowhere else.
        Bind("trait.stalwart", "1");

        var atoms = _push.Build(Owner(), Lawn(), matchSeed: 7, matchKey: "m1");
        var json = JsonSerializer.Serialize(AtomPushService.BuildApplyPayload(atoms, sessionGrants: null), Wire);
        using var doc = JsonDocument.Parse(json);

        // Read 1 -- the grant loop.
        Assert.True(doc.RootElement.TryGetProperty("grants", out var arr));
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        var raw = Assert.Single(arr.EnumerateArray().ToList());
        Assert.Equal(AtomRow.DeriveId("atom.vitality", "", 1), raw.GetProperty("effectId").GetString());

        // Read 2 -- the atom installer, through the very same bytes.
        var round = JsonSerializer.Deserialize<FusionRpg.Contracts.AtomPushDto>(json);
        Assert.NotNull(round);
        Assert.Equal(atoms.Grants.Count, round!.Grants.Count);
        Assert.Equal(atoms.Defs.Count, round.Defs.Count);
        Assert.Equal(atoms.CatalogRevision, round.CatalogRevision);
        Assert.Equal(atoms.EmitterVersion, round.EmitterVersion);
    }

    [Fact]
    public void Grants_is_always_an_array_even_with_nothing_to_put_in_it()
    {
        // The injector refuses the WHOLE command -- InstallAtomPush never reached -- when `grants` is
        // absent or not an array ("effects.grants.apply: missing grants[]"). So the key survives a
        // failed atom build and an empty session alike.
        var empty = AtomPushService.BuildApplyPayload(atoms: null, sessionGrants: null);
        Assert.Empty(Assert.IsType<List<FusionRpg.Contracts.EffectGrantDto>>(empty["grants"]));
        Assert.False(empty.ContainsKey("defs")); // a failed atom build must not fake an empty catalog

        var withAtoms = AtomPushService.BuildApplyPayload(
            _push.Build(Owner(), Lawn(), matchSeed: 7), sessionGrants: null);
        Assert.True(withAtoms.ContainsKey("defs"));
        Assert.True(withAtoms.ContainsKey("runnerBindings"));
        Assert.IsType<List<FusionRpg.Contracts.EffectGrantDto>>(withAtoms["grants"]);
    }

    [Fact]
    public void A_runner_only_push_puts_no_compiled_grant_on_the_wire()
    {
        // The negative half: item.blade is two TRIGGERED atoms, so nothing compiles. If this ever
        // starts carrying a grant, the compile/run split has moved and the runner would double-apply.
        Bind("item.blade", "1");

        var atoms = _push.Build(Owner(), Lawn(), matchSeed: 7);
        Assert.Empty(atoms.Grants);
        Assert.NotEmpty(atoms.RunnerBindings);

        var grants = Assert.IsType<List<FusionRpg.Contracts.EffectGrantDto>>(
            AtomPushService.BuildApplyPayload(atoms, sessionGrants: null)["grants"]);
        Assert.Empty(grants);
    }
}
