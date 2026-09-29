using System.Reflection;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E34 acceptance (spec-trigger-vocabulary.md). Input only: five host events become five atom
/// triggers -- OnWave, OnMatchStart, OnMatchEnd, OnSunCollect, OnGridPlace -- on the closed trigger
/// list, in <see cref="EffectEventAdapterCore.TryMap"/>, and on the narrow set of kinds that can act
/// with no entity in hand. No kind, attach point, or executor ships here; that is E35/E36's job.
/// </summary>
public class TriggerVocabularyTests
{
    static Dictionary<string, object> P(params (string Key, object Value)[] pairs)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    static string[] PublicConstStrings(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    static EffectGrant Grant(string ownerKey) => EffectGrant.FromDto(new EffectGrantDto
    {
        GrantId = "g",
        EffectId = "fx.probe",
        OwnerKey = ownerKey
    });

    // ---- §2.1: vocabulary size and contract parity ---------------------------------------------------

    [Fact]
    public void Trigger_vocabulary_is_self_consistent_and_carries_exactly_the_five_new_triggers()
    {
        // Self-consistency, never a literal -- the same style AtomKindRegistryTests.cs's own guard
        // uses (E33's "Trigger_vocabulary_is_closed_at_eight", left unedited by this module: its body
        // already re-derives from TriggerCount/All.Length and needs no change to stay correct at 13).
        Assert.Equal(AtomKindRegistry.TriggerCount, AtomTriggers.All.Length);
        Assert.Equal(AtomTriggers.All.Length, AtomTriggers.All.Distinct(StringComparer.Ordinal).Count());

        // "Five higher than before this module" made concrete without hardcoding the pre-E34 total (8)
        // anywhere in the assertion: MatchEvents (3) + BoardEconomyEvents (2) = 5, each present in
        // All exactly once.
        var newFive = AtomTriggers.MatchEvents.Concat(AtomTriggers.BoardEconomyEvents).ToArray();
        Assert.Equal(5, newFive.Length);
        foreach (var t in newFive)
            Assert.Single(AtomTriggers.All, x => string.Equals(x, t, StringComparison.Ordinal));
    }

    /// <summary>
    /// ⛔ The 13 trigger strings are declared ONCE, in <c>FusionRpg.Contracts.EffectTriggers</c>;
    /// <c>AtomTriggers</c> aliases them (<c>= EffectTriggers.X</c>). Owner ruling 2026-09-16: "we only
    /// have atom ssot" — <c>AtomTriggers</c> is the atom-facing SSOT surface, and the literals sit one
    /// assembly down because <c>EffectBag</c> matches by string across the Core/Contracts boundary and
    /// Contracts is the only assembly both sides can see.
    ///
    /// <para>Both classes used to declare all 13 literals, kept equal by the pairwise test below. That
    /// test cannot catch a RE-INTRODUCED literal — a fresh <c>= "OnSpawn"</c> on the Core side has the
    /// same value, so pairwise equality still holds and the duplicate comes back silently. This is the
    /// test that notices, and it is the reason the aliasing can be described as structural rather than
    /// as a convention someone has to remember.</para>
    ///
    /// <para>Source scan rather than reflection, because a <c>const</c> alias and a <c>const</c>
    /// literal are indistinguishable in metadata: the compiler inlines both to the same value. The
    /// duplication this guards is a property of the SOURCE, so the source is what it reads.</para>
    /// </summary>
    [Fact]
    public void AtomTriggers_declares_no_string_literal_of_its_own()
    {
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Effects", "Atoms", "AtomKind.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);

        var start = text.IndexOf("public static class AtomTriggers", StringComparison.Ordinal);
        Assert.True(start >= 0, "AtomTriggers class not found");
        var end = text.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, "AtomTriggers class body not delimited");

        var body = text[start..end];
        var code = string.Join(
            "\n",
            body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        foreach (var trigger in AtomTriggers.All)
            Assert.DoesNotContain("\"" + trigger + "\"", code, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public void Every_AtomTrigger_has_an_ordinally_equal_EffectTrigger_pairwise_and_no_others_exist()
    {
        // §2.1: "the same five constants are mirrored into EffectTriggers ... ordinally identical."
        // Pairwise both ways -- neither list may have an extra a balance session would notice drift on.
        var effectTriggerConsts = PublicConstStrings(typeof(EffectTriggers));
        foreach (var t in AtomTriggers.All)
            Assert.Contains(t, effectTriggerConsts, StringComparer.Ordinal);
        Assert.Equal(AtomTriggers.All.Length, effectTriggerConsts.Length);
    }

    [Fact]
    public void EffectTriggers_declares_all_five_new_constants_among_its_public_constants()
    {
        // §2.3 (the /effects/contract endpoint's own form): DebugEndpoints.cs's `/effects/contract`
        // reflects PublicConstStrings(typeof(EffectTriggers)) directly (DebugEndpoints.cs:398-404), so
        // pinning this class's own declared fields is the same assertion the wire array makes, by
        // construction -- matching E33's own Core-side testing style (ActivationEdgeTests.cs), not a
        // Server.Tests HTTP call.
        var consts = PublicConstStrings(typeof(EffectTriggers));
        Assert.Contains(EffectTriggers.OnWave, consts);
        Assert.Contains(EffectTriggers.OnMatchStart, consts);
        Assert.Contains(EffectTriggers.OnMatchEnd, consts);
        Assert.Contains(EffectTriggers.OnSunCollect, consts);
        Assert.Contains(EffectTriggers.OnGridPlace, consts);
    }

    // ---- §2.2: TryMap mapping ------------------------------------------------------------------------

    [Fact]
    public void TryMap_wave_change_maps_OnWave_with_wave_number_and_no_actor_or_side()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "wave.change", P(("wave", 4), ("maxWave", 10)), tick: 1, matchKey: "m-map-wave-change");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnWave, ev!.Trigger);
        Assert.Equal(4, ev.Wave);
        Assert.Equal("m-map-wave-change", ev.MatchKey);
        Assert.Equal(1, ev.Tick);
        Assert.Null(ev.ActorPtr);
        Assert.Null(ev.Side);
    }

    [Fact]
    public void TryMap_wave_spawn_maps_OnWave_when_it_is_the_first_sighting_of_that_wave()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "wave.spawn", P(("wave", 2)), tick: 5, matchKey: "m-map-wave-spawn-fresh");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnWave, ev!.Trigger);
        Assert.Equal(2, ev.Wave);
    }

    [Fact]
    public void TryMap_wave_huge_maps_OnWave_when_it_is_the_first_sighting_of_that_wave()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "wave.huge", P(("wave", 9)), tick: 5, matchKey: "m-map-wave-huge-fresh");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnWave, ev!.Trigger);
        Assert.Equal(9, ev.Wave);
    }

    [Fact]
    public void TryMap_board_start_maps_OnMatchStart_with_matchKey_and_tick_only()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "board.start", P(("levelName", "level1")), tick: 1, matchKey: "m-map-start");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnMatchStart, ev!.Trigger);
        Assert.Equal("m-map-start", ev.MatchKey);
        Assert.Equal(1, ev.Tick);
        Assert.Null(ev.ActorPtr);
        Assert.Null(ev.Side);
        Assert.Null(ev.TypeId);
    }

    [Theory]
    [InlineData("board.end")]
    [InlineData("match.win")]
    [InlineData("match.lose")]
    public void TryMap_board_end_and_match_result_kinds_all_map_OnMatchEnd(string kind)
    {
        var ev = EffectEventAdapterCore.TryMap(kind, P(), tick: 3, matchKey: "m-map-end");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnMatchEnd, ev!.Trigger);
        Assert.Equal("m-map-end", ev.MatchKey);
        Assert.Equal(3, ev.Tick);
    }

    [Fact]
    public void TryMap_sun_gain_maps_OnSunCollect_and_no_field_carries_the_collected_count()
    {
        // §2.2: the count is a resource amount, not a predicate field -- out of scope for this module
        // (E3's closed leaf list). Asserted by checking the only numeric magnitude field the DTO has
        // (Damage) stays null, alongside the trigger itself.
        var ev = EffectEventAdapterCore.TryMap(
            "sun.gain", P(("count", 25f), ("save", true)), tick: 7, matchKey: "m-map-sun");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnSunCollect, ev!.Trigger);
        Assert.Equal("m-map-sun", ev.MatchKey);
        Assert.Equal(7, ev.Tick);
        Assert.Null(ev.Damage);
        Assert.Null(ev.Wave);
    }

    [Fact]
    public void TryMap_grid_place_maps_OnGridPlace_with_grid_item_type_and_actor_ptr()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "grid.place",
            P(("ptr", "0xGRAVE"), ("type", 7), ("typeName", "Grave"), ("col", 3), ("row", 2)),
            tick: 11,
            matchKey: "m-map-grid");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnGridPlace, ev!.Trigger);
        Assert.Equal("m-map-grid", ev.MatchKey);
        Assert.Equal(11, ev.Tick);
        Assert.Equal(7, ev.TypeId);
        Assert.Equal("0xGRAVE", ev.ActorPtr);
    }

    [Fact]
    public void TryMap_grid_place_leaves_ActorPtr_null_when_the_payload_carries_no_ptr()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "grid.place", P(("type", 4)), tick: 11, matchKey: "m-map-grid-noptr");

        Assert.NotNull(ev);
        Assert.Null(ev!.ActorPtr);
        Assert.Equal(4, ev.TypeId);
    }

    // ---- §2.2: the one-edge-per-wave de-dupe ----------------------------------------------------------

    [Fact]
    public void One_edge_per_wave_wave_spawn_immediately_after_wave_change_for_the_same_wave_maps_to_null()
    {
        var matchKey = "m-one-edge-per-wave";
        var first = EffectEventAdapterCore.TryMap("wave.change", P(("wave", 4)), tick: 1, matchKey: matchKey);
        Assert.NotNull(first);
        Assert.Equal(4, first!.Wave);

        var second = EffectEventAdapterCore.TryMap("wave.spawn", P(("wave", 4)), tick: 2, matchKey: matchKey);
        Assert.Null(second);
    }

    [Fact]
    public void One_edge_per_wave_wave_huge_immediately_after_wave_change_for_the_same_wave_maps_to_null()
    {
        var matchKey = "m-one-edge-per-wave-huge";
        var first = EffectEventAdapterCore.TryMap("wave.change", P(("wave", 6)), tick: 1, matchKey: matchKey);
        Assert.NotNull(first);

        var second = EffectEventAdapterCore.TryMap("wave.huge", P(("wave", 6)), tick: 2, matchKey: matchKey);
        Assert.Null(second);
    }

    [Fact]
    public void A_genuinely_new_wave_number_still_maps_after_a_prior_wave_was_suppressed()
    {
        // The de-dupe suppresses a REPEAT of the same wave, not the mapper as a whole -- the next real
        // wave transition must still produce an OnWave.
        var matchKey = "m-one-edge-per-wave-advance";
        EffectEventAdapterCore.TryMap("wave.change", P(("wave", 1)), tick: 1, matchKey: matchKey);
        var suppressed = EffectEventAdapterCore.TryMap("wave.spawn", P(("wave", 1)), tick: 2, matchKey: matchKey);
        Assert.Null(suppressed);

        var next = EffectEventAdapterCore.TryMap("wave.change", P(("wave", 2)), tick: 3, matchKey: matchKey);
        Assert.NotNull(next);
        Assert.Equal(2, next!.Wave);
    }

    [Fact]
    public void PLANTED_VIOLATION_dropping_the_wave_number_dedupe_would_fail_the_one_edge_per_wave_test()
    {
        // The falsifier §4 asks for: if MapWave's LastMappedWave check is ever removed (mapping
        // wave.spawn/wave.huge unconditionally), this goes red -- a real balance bug (a doubled
        // resource-economy payout) that goldens cannot catch since no shipped content uses OnWave yet.
        var matchKey = "m-planted-wave-dedupe";
        EffectEventAdapterCore.TryMap("wave.change", P(("wave", 4)), tick: 1, matchKey: matchKey);
        var second = EffectEventAdapterCore.TryMap("wave.spawn", P(("wave", 4)), tick: 2, matchKey: matchKey);
        Assert.Null(second);
    }

    // ---- §2.3: kind eligibility ----------------------------------------------------------------------

    [Theory]
    [InlineData("spawn.entity")]
    [InlineData("board.action")]
    [InlineData("grid.spawn")]
    [InlineData("grid.clear")]
    [InlineData("box.set")]
    [InlineData("resource.economy")]
    public void The_six_named_kinds_accept_all_five_new_triggers(string kindId)
    {
        Assert.True(AtomKindRegistry.ValidateTrigger(kindId, AtomTriggers.OnWave).IsOk, kindId);
        Assert.True(AtomKindRegistry.ValidateTrigger(kindId, AtomTriggers.OnMatchStart).IsOk, kindId);
        Assert.True(AtomKindRegistry.ValidateTrigger(kindId, AtomTriggers.OnMatchEnd).IsOk, kindId);
        Assert.True(AtomKindRegistry.ValidateTrigger(kindId, AtomTriggers.OnSunCollect).IsOk, kindId);
        Assert.True(AtomKindRegistry.ValidateTrigger(kindId, AtomTriggers.OnGridPlace).IsOk, kindId);
    }

    [Theory]
    [InlineData("resource.delta")]
    [InlineData("status.apply")]
    [InlineData("shield.grant")]
    [InlineData("stat.modify")]
    [InlineData("stat.derived")]
    [InlineData("status.clear")]
    public void The_other_kinds_refuse_every_new_trigger(string kindId)
    {
        // §2.3: resource.delta/status.apply/shield.grant resolve their target from the event and would
        // otherwise reopen G5's unguarded FindObjectsOfType<Zombie>() loop; stat.modify/stat.derived
        // stay as they were (definitions.md §14.2); status.clear is simply not named by §2.3's table.
        foreach (var t in new[]
                 {
                     AtomTriggers.OnWave, AtomTriggers.OnMatchStart, AtomTriggers.OnMatchEnd,
                     AtomTriggers.OnSunCollect, AtomTriggers.OnGridPlace
                 })
        {
            Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
                AtomKindRegistry.ValidateTrigger(kindId, t).Reason);
        }
    }

    [Fact]
    public void ValidateTrigger_OnWave_ok_on_an_allowed_kind_and_refused_on_a_disallowed_one()
    {
        // §4's own named pair.
        Assert.True(AtomKindRegistry.ValidateTrigger("board.action", AtomTriggers.OnWave).IsOk);
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("status.apply", AtomTriggers.OnWave).Reason);
    }

    [Fact]
    public void PLANTED_VIOLATION_widening_a_triggerless_kinds_allowed_list_would_fail_the_refusal_test()
    {
        // The falsifier for G5: if AtomKindRegistry.AllTriggers (status.apply/resource.delta/
        // shield.grant/stat.modify's shared list) ever grows to include OnWave, this assertion --
        // which is exactly what The_other_kinds_refuse_every_new_trigger checks for status.apply --
        // goes red, and a wave-triggered status becomes authorable, silently statusing every zombie on
        // the board through the unguarded fan-out.
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed,
            AtomKindRegistry.ValidateTrigger("status.apply", AtomTriggers.OnWave).Reason);
    }

    [Fact]
    public void Match_modify_and_wave_control_both_now_exist_carrying_MatchEvents()
    {
        // §2.3 listed match.modify (E35) / wave.control (E36) as gaining MatchEvents once they exist.
        // Both have shipped now -- updated here rather than left stale, per this module's own "read
        // the section, not the line" discipline: a forward-reference gap this test itself closes is
        // not a defect, but an unedited assertion of its old shape would now be simply wrong.
        Assert.NotNull(AtomKindRegistry.Get("match.modify"));
        Assert.True(AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnWave).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnMatchStart).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("match.modify", AtomTriggers.OnMatchEnd).IsOk);

        Assert.NotNull(AtomKindRegistry.Get("wave.control"));
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnWave).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnMatchStart).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("wave.control", AtomTriggers.OnMatchEnd).IsOk);
    }

    // ---- §2.4: the owner-key leak and its fix ---------------------------------------------------------

    [Fact]
    public void Plant_owner_key_refuses_OnMatchStart()
    {
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnMatchStart, Tick = 1 };
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("plant:7"), ev));
    }

    [Fact]
    public void Zombie_owner_key_refuses_OnMatchStart()
    {
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnMatchStart, Tick = 1 };
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("zombie:7"), ev));
    }

    [Fact]
    public void Zombie_owner_key_refuses_OnGridPlace_for_the_grid_item_type_matching_its_own_tid()
    {
        // THE LEAK: grid.place -> OnGridPlace carries TypeId = the grid item type (§2.2). Without
        // §2.4's explicit arm, `zombie:7` would fall through to `(ev.TypeId ?? ev.TargetTypeId) == tid`
        // and match -- a zombie-type-keyed container reacting to a gravestone (grid item type 7)
        // being placed, on every placement, in every match.
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnGridPlace, TypeId = 7, Tick = 1 };
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("zombie:7"), ev));
    }

    [Fact]
    public void Plant_owner_key_also_refuses_OnGridPlace_for_the_matching_type()
    {
        // The plant branch already falls through to false here without any change (definitions.md's
        // "no change needed there in principle") -- but §2.4's arm covers BOTH branches explicitly, so
        // this is pinned too, not merely assumed to still hold.
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnGridPlace, TypeId = 7, Tick = 1 };
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("plant:7"), ev));
    }

    [Fact]
    public void Zombie_owner_key_refuses_OnSunCollect_even_though_sun_gain_carries_no_TypeId_today()
    {
        // "Safe only because sun.gain gives no TypeId today -- a safety that lasts exactly as long as
        // nobody adds one." §2.4's arm makes the refusal explicit rather than relying on that accident.
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnSunCollect, Tick = 1 };
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("zombie:7"), ev));
    }

    [Theory]
    [InlineData("plant:7")]
    [InlineData("zombie:7")]
    public void Type_keyed_grants_refuse_every_one_of_the_five_new_triggers(string ownerKey)
    {
        foreach (var trigger in new[]
                 {
                     EffectTriggers.OnWave, EffectTriggers.OnMatchStart, EffectTriggers.OnMatchEnd,
                     EffectTriggers.OnSunCollect, EffectTriggers.OnGridPlace
                 })
        {
            var ev = new EffectEventDto { Trigger = trigger, TypeId = 7, TargetTypeId = 7, Side = null, Tick = 1 };
            Assert.False(EffectOwnerKey.MatchesEvent(Grant(ownerKey), ev), $"{ownerKey} matched {trigger}");
        }
    }

    [Fact]
    public void Match_scoped_owner_key_still_matches_every_one_of_the_five_new_triggers()
    {
        // §2.4 touches only the type-keyed (plant:/zombie:) branches -- `match` is unchanged, as the
        // spec's own note says.
        foreach (var trigger in new[]
                 {
                     EffectTriggers.OnWave, EffectTriggers.OnMatchStart, EffectTriggers.OnMatchEnd,
                     EffectTriggers.OnSunCollect, EffectTriggers.OnGridPlace
                 })
        {
            var ev = new EffectEventDto { Trigger = trigger, Tick = 1 };
            Assert.True(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Match), ev), trigger);
        }
    }

    [Fact]
    public void PLANTED_VIOLATION_dropping_only_the_zombie_half_of_the_owner_key_arm_would_fail_this_test()
    {
        // The falsifier §4 names specifically: "drop the zombie half of §2.4's arm, keeping the plant
        // half" -- the plant-branch violation (a hypothetical drop of that arm) never covers this case,
        // because the plant branch's own fall-through already returns false with or without its arm.
        // This test is the one that goes red if EffectProcAndOwner's ZOMBIE branch's
        // IsTypeKeyedRefusalTrigger check is ever removed while the plant branch's copy survives: a
        // `zombie:7` container would then fire on every placement of grid item type 7.
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnGridPlace, TypeId = 7, Tick = 1 };
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("zombie:7"), ev));
    }
}
