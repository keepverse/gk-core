using FusionRpg.Contracts;
using FusionRpg.Core.Match;
using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests;

public class UniqueBindingsTests
{
    [Fact]
    public void Pending_Bound_Cleared_lifecycle()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-bind" });

        var id = Guid.NewGuid().ToString("N");
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(rt.TryBeginPending(id, corr, "plant", 7));
        Assert.Equal(1, rt.UniqueBindingCount);
        Assert.True(rt.TryGetBinding(id, out var pending));
        Assert.Equal(UniqueBindingPhase.PendingSpawn, pending!.Phase);
        Assert.Null(pending.Ptr);

        rt.Apply("plant.spawn", new Dictionary<string, object>
        {
            ["ptr"] = "0xABC",
            ["typeId"] = 7,
            ["correlationId"] = corr,
            ["instanceId"] = id
        });

        Assert.True(rt.TryGetBinding(id, out var bound));
        Assert.Equal(UniqueBindingPhase.Bound, bound!.Phase);
        Assert.Equal("ABC", bound.Ptr);
        var consumed = rt.ConsumeLastBound();
        Assert.NotNull(consumed);
        Assert.Equal(id, consumed!.InstanceId);
        Assert.Null(rt.ConsumeLastBound());

        rt.Apply("plant.die", new Dictionary<string, object> { ["ptr"] = "0xABC" });
        Assert.Equal(0, rt.UniqueBindingCount);
        Assert.True(rt.TryGetBinding(id, out var cleared));
        Assert.Equal(UniqueBindingPhase.Cleared, cleared!.Phase);
    }

    [Fact]
    public void Ack_kind_also_binds()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var id = "inst-ack";
        var corr = "corr-ack";
        Assert.True(rt.TryBeginPending(id, corr, "zombie", 2));

        rt.Apply("pvz.spawn.extra.ack", new Dictionary<string, object>
        {
            ["ptr"] = "DEADBEEF",
            ["correlationId"] = corr,
            ["side"] = "zombie",
            ["typeId"] = 2
        });

        Assert.True(rt.TryGetBinding(id, out var b));
        Assert.Equal(UniqueBindingPhase.Bound, b!.Phase);
        Assert.Equal("DEADBEEF", b.Ptr);
    }

    [Fact]
    public void Second_ack_is_noop_no_second_lastBound()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.True(rt.TryBeginPending("id1", "c1", "plant", 1));
        rt.Apply("pvz.spawn.extra.ack", new Dictionary<string, object>
        {
            ["ptr"] = "AAA",
            ["correlationId"] = "c1"
        });
        Assert.NotNull(rt.ConsumeLastBound());

        rt.Apply("pvz.spawn.extra.ack", new Dictionary<string, object>
        {
            ["ptr"] = "BBB",
            ["correlationId"] = "c1"
        });
        Assert.Null(rt.ConsumeLastBound());
        Assert.True(rt.TryGetBinding("id1", out var b));
        Assert.Equal("AAA", b!.Ptr);
    }

    [Fact]
    public void TryClearPending_by_corr_allows_redeploy()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.True(rt.TryBeginPending("spec", "corr-old", "plant", 3));
        Assert.Equal(1, rt.UniqueBindingCount);

        Assert.True(rt.TryClearPending(correlationId: "corr-old"));
        Assert.Equal(0, rt.UniqueBindingCount);

        Assert.True(rt.TryBeginPending("spec", "corr-new", "plant", 3));
        Assert.Equal(1, rt.UniqueBindingCount);
    }

    [Fact]
    public void Cleared_then_re_Pending_same_instanceId()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.True(rt.TryBeginPending("reuse", "c-a", "zombie", 1));
        rt.Apply("pvz.spawn.extra.ack", new Dictionary<string, object>
        {
            ["ptr"] = "111",
            ["correlationId"] = "c-a"
        });
        rt.Apply("zombie.die", new Dictionary<string, object> { ["ptr"] = "111" });
        Assert.Equal(0, rt.UniqueBindingCount);

        Assert.True(rt.TryBeginPending("reuse", "c-b", "zombie", 1));
        Assert.Equal(1, rt.UniqueBindingCount);
        Assert.True(rt.TryGetBinding("reuse", out var pending));
        Assert.Equal(UniqueBindingPhase.PendingSpawn, pending!.Phase);
    }

    [Fact]
    public void Board_end_clears_all_bindings()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-end" });
        Assert.True(rt.TryBeginPending("a", "c1", "plant", 1));
        Assert.True(rt.TryBeginPending("b", "c2", "zombie", 2));
        rt.Apply("plant.spawn", new Dictionary<string, object>
        {
            ["ptr"] = "111",
            ["correlationId"] = "c1",
            ["typeId"] = 1
        });
        Assert.Equal(2, rt.UniqueBindingCount);

        rt.Apply("board.end");
        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Equal(0, rt.UniqueBindingCount);
        Assert.Empty(rt.ToSnapshot().Bindings);
    }

    [Fact]
    public void Snapshot_includes_bindings_cold()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.True(rt.TryBeginPending("snap", "c-snap", "plant", 0,
            """{"absolutes":{"hp":9}}"""));
        var snap = rt.ToSnapshot();
        Assert.Single(snap.Bindings);
        Assert.Equal("snap", snap.Bindings[0].InstanceId);
        Assert.Equal(UniqueBindingPhase.PendingSpawn, snap.Bindings[0].Phase);
        Assert.Contains("hp", snap.Bindings[0].LoadoutJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBeginPending_rejects_outside_InMatch()
    {
        var rt = new MatchRuntime();
        Assert.False(rt.TryBeginPending("x", "y", "plant", 0));
    }
}

public class UniqueOwnerBinderTests
{
    [Fact]
    public void ToEntityKey_and_BindGrant_never_leave_instance()
    {
        var key = UniqueOwnerBinder.ToEntityKey("guid-1", "0xAa");
        Assert.Equal("entity:AA", key);
        Assert.False(StatApplyScope.IsInstanceOwnerKey(key));

        var grant = UniqueOwnerBinder.BindGrant(new EffectGrantDto
        {
            GrantId = "g1",
            EffectId = "e1",
            OwnerKey = "instance:guid-1"
        }, "BBB");
        Assert.Equal("entity:BBB", grant.OwnerKey);
        Assert.False(UniqueOwnerBinder.WouldRejectOnHot(grant.OwnerKey));
        Assert.True(UniqueOwnerBinder.WouldRejectOnHot("instance:guid-1"));
    }

    [Fact]
    public void Bound_owner_matches_entity_not_sibling_type()
    {
        var owner = UniqueOwnerBinder.BindOwnerKey("instance:abc", "AAA");
        Assert.True(StatApplyScope.Matches(owner, StatSide.Plant, 3, "AAA"));
        Assert.False(StatApplyScope.Matches(owner, StatSide.Plant, 3, "BBB"));
        Assert.False(StatApplyScope.Matches("instance:abc", StatSide.Plant, 3, "AAA"));
    }
}

public class UniqueLoadoutSpecTests
{
    [Fact]
    public void Parse_and_bind_grants_to_entity()
    {
        var spec = UniqueLoadoutSpec.Parse("""
            {
              "absolutes": { "hp": 500, "maxHp": 500, "atk": 40 },
              "grants": [
                { "grantId": "g1", "effectId": "fx", "ownerKey": "instance:specimen-1" }
              ]
            }
            """);
        Assert.False(spec.IsEmpty);
        Assert.Equal(500, spec.Absolutes["hp"]);

        var bound = spec.BindToPtr("0xC0FFEE");
        Assert.Equal("entity:C0FFEE", bound.Grants[0].OwnerKey);
        Assert.True(UniqueLoadoutSpec.AbsoluteWouldApplyToEntity(
            "C0FFEE", bound.Grants[0].OwnerKey, StatSide.Zombie, 1));
        Assert.False(UniqueLoadoutSpec.AbsoluteWouldApplyToEntity(
            "OTHER", bound.Grants[0].OwnerKey, StatSide.Zombie, 1));
    }

    [Fact]
    public void ToCheatAbsoluteMap_keeps_both_hp_and_maxHp()
    {
        var map = UniqueLoadoutSpec.Parse("""{"absolutes":{"hp":100,"maxHp":500,"atk":9}}""")
            .ToCheatAbsoluteMap();
        Assert.Equal(100, map["hp"]);
        Assert.Equal(500, map["maxHp"]);
        Assert.Equal(9, map["atk"]);
    }

    [Fact]
    public void Empty_json_is_noop()
    {
        Assert.True(UniqueLoadoutSpec.Parse(null).IsEmpty);
        Assert.True(UniqueLoadoutSpec.Parse("{}").IsEmpty);
        Assert.True(UniqueLoadoutSpec.Parse("not-json").IsEmpty);
    }

    [Fact]
    public void Sibling_type_wide_key_still_matches_both_but_entity_does_not()
    {
        var entityOwner = UniqueOwnerBinder.ToEntityKey("i", "PTR1");
        Assert.True(StatApplyScope.Matches("plant:5", StatSide.Plant, 5, "PTR1"));
        Assert.True(StatApplyScope.Matches("plant:5", StatSide.Plant, 5, "PTR2"));
        Assert.True(StatApplyScope.Matches(entityOwner, StatSide.Plant, 5, "PTR1"));
        Assert.False(StatApplyScope.Matches(entityOwner, StatSide.Plant, 5, "PTR2"));
    }

    [Fact]
    public void Merge_emptyish_deploy_falls_back_to_mods()
    {
        var mods = """{"absolutes":{"hp":12}}""";
        Assert.Equal(mods, UniqueLoadoutMerge.Merge("{}", mods));
        Assert.Equal(mods, UniqueLoadoutMerge.Merge("""{"absolutes":{}}""", mods));
        Assert.Equal(mods, UniqueLoadoutMerge.Merge("""{"grants":[]}""", mods));

        var deploy = """{"absolutes":{"hp":99}}""";
        Assert.Equal(deploy, UniqueLoadoutMerge.Merge(deploy, mods));
        Assert.Equal("{}", UniqueLoadoutMerge.Merge(null, null));
    }
}
