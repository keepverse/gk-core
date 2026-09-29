using System;
using System.Linq;
using System.Reflection;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `replay-identity` (module 8, CAI2.1, spec-replay-identity.md §1) — the Core half. The
/// module's whole claim is that the profile stamp is a <see cref="ContentHashStamp"/> and its decision
/// procedure is <see cref="ContentHashComparison"/>; these tests exercise the stamp and the four
/// verdicts, and assert that a profile's canonical form covers every declared member (so a field added
/// to the record cannot silently escape the hash).
///
/// <para>The Data half (the nullable `combat_ai_profile` column, `AppendWebMatchLog`, `SelectLog`) and
/// the Server half (the pinned re-resolve, the boot-sweep guard) are outside this lane's file fence and
/// are filed against the owning paths — see the evidence fragment.</para>
/// </summary>
public class CombatAiProfileIdentityTests
{
    static string ProfileJson(int weightHitChance = 70) => $$"""
        {
          "tierOverride": null,
          "tierByActorClass": { "unique": "smart", "general": "performance" },
          "rows": [ { "selector": "nearest", "condition": "always", "census": "none", "actions": {} } ],
          "scoring": { "weightHitChance": {{weightHitChance}}, "weightObjective": 50, "weightKill": 15,
                       "weightLowHp": 10, "weightCannotCounter": 10, "weightRound": 1, "weightRisk": 120,
                       "aggressionRange": 2, "maxCandidatesScored": 32 },
          "selection": { "mode": "argmax", "keepPctMilli": 1000, "rngStreamName": "ai.select" },
          "reserves": [],
          "guards": { "minTargetsForArea": 1, "killMarginMilli": 0, "fightEndingLiveCount": 0 },
          "antiRepeat": { "retargetLatencyTicks": 0, "commitmentBonus": 0, "repeatDecayHalfLifeTicks": 0 },
          "personality": { "bounds": { "aggression": 0, "recklessness": 0, "focus": 0, "thrift": 0 } }
        }
        """;

    static CombatAiTuning Set(int version, string profilesJson) =>
        CombatAiTuningLoader.Parse(
            "{\"schemaVersion\":1,\"version\":" + version + ",\"profiles\":{" + profilesJson +
            "},\"router\":{\"orderTimeoutTicks\":5000,\"reactionsPerRoundExpected\":1000}}");

    static CombatAiTuning OneProfile(int version = 1, int weightHitChance = 70) =>
        Set(version, $"\"*/default\":{ProfileJson(weightHitChance)}");

    [Fact]
    public void Stamp_round_trips_through_its_compact_form()
    {
        var stamp = CombatAiProfileIdentity.StampOf(OneProfile());

        Assert.True(ContentHashStamp.TryParse(stamp.ToCompact(), out var parsed));
        Assert.Equal(stamp.ToCompact(), parsed.ToCompact());
        Assert.Equal(1, parsed.SchemaVersion); // the PUBLISH counter, so the pin is addressable
        Assert.Equal(stamp.Hash, parsed.Hash);
        Assert.True(parsed.TableDigests.ContainsKey("*/default"));
    }

    [Fact]
    public void The_same_set_stamps_identically_twice()
    {
        Assert.Equal(
            CombatAiProfileIdentity.StampOf(OneProfile()).ToCompact(),
            CombatAiProfileIdentity.StampOf(OneProfile()).ToCompact());
    }

    [Fact]
    public void The_stamp_is_order_independent_across_the_profile_map()
    {
        var rootFirst = Set(1, $"\"*/default\":{ProfileJson()},\"siege/default\":{ProfileJson()}");
        var siegeFirst = Set(1, $"\"siege/default\":{ProfileJson()},\"*/default\":{ProfileJson()}");

        Assert.Equal(
            CombatAiProfileIdentity.StampOf(rootFirst).ToCompact(),
            CombatAiProfileIdentity.StampOf(siegeFirst).ToCompact());
    }

    [Fact]
    public void One_changed_weight_is_a_mismatch_naming_that_profile()
    {
        // Edited IN PLACE under its own version -- the hand-edit ban, turned into a detection.
        var stored = CombatAiProfileIdentity.StampOf(
            Set(1, $"\"*/default\":{ProfileJson()},\"siege/default\":{ProfileJson(70)}"));
        var current = CombatAiProfileIdentity.StampOf(
            Set(1, $"\"*/default\":{ProfileJson()},\"siege/default\":{ProfileJson(999)}"));

        var comparison = CombatAiProfileIdentity.Compare(stored.ToCompact(), current);

        Assert.Equal(ContentHashVerdict.Mismatch, comparison.Verdict);
        Assert.True(comparison.ShouldRefuse);
        Assert.Contains("siege/default", comparison.ChangedTables);
        Assert.DoesNotContain("*/default", comparison.ChangedTables);
    }

    [Fact]
    public void An_added_profile_is_a_registry_change_and_not_a_refusal()
    {
        // A new place x role ships as a publish (v2), so the version moves and the verdict is
        // RegistryChanged: every earlier match must still replay, not be stranded.
        var stored = CombatAiProfileIdentity.StampOf(Set(1, $"\"*/default\":{ProfileJson()}"));
        var current = CombatAiProfileIdentity.StampOf(
            Set(2, $"\"*/default\":{ProfileJson()},\"siege/default\":{ProfileJson()}"));

        var comparison = CombatAiProfileIdentity.Compare(stored.ToCompact(), current);

        Assert.Equal(ContentHashVerdict.RegistryChanged, comparison.Verdict);
        Assert.False(comparison.ShouldRefuse);
        Assert.Contains("siege/default", comparison.AddedTables);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_stamped_is_a_match(string? stored)
    {
        var comparison = CombatAiProfileIdentity.Compare(stored, CombatAiProfileIdentity.StampOf(OneProfile()));

        Assert.Equal(ContentHashVerdict.Match, comparison.Verdict);
        Assert.False(comparison.ShouldRefuse);
    }

    [Fact]
    public void A_corrupted_stamp_is_unreadable_and_refused()
    {
        var comparison = CombatAiProfileIdentity.Compare(
            "not-a-stamp", CombatAiProfileIdentity.StampOf(OneProfile()));

        Assert.Equal(ContentHashVerdict.Unreadable, comparison.Verdict);
        Assert.True(comparison.ShouldRefuse); // unverifiable is not proven
    }

    /// <summary>
    /// The canonical form walks the record's declared members, so adding a field to `CombatAiProfile`
    /// adds it to the hash. This pins that property by name, because the failure mode it prevents is
    /// silent: a new weight that never reaches the stamp makes a rebalance replay as if nothing moved.
    /// </summary>
    [Fact]
    public void Every_declared_profile_member_appears_in_the_canonical_form()
    {
        var profile = OneProfile().Profiles["*/default"];
        var canonical = CombatAiProfileIdentity.Canonical(profile);

        var declared = typeof(CombatAiProfile)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        Assert.NotEmpty(declared);
        foreach (var name in declared)
            Assert.Contains(name + "=", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void The_source_seam_refuses_an_unknown_version_rather_than_falling_back()
    {
        ICombatAiProfileSource source = new InMemorySource(OneProfile(version: 3));

        Assert.Equal(3, source.Current.Version);
        Assert.NotNull(source.ForVersion(3));
        Assert.Null(source.ForVersion(2)); // refuse -- never "resolve it on Current"
    }

    sealed class InMemorySource : ICombatAiProfileSource
    {
        public InMemorySource(CombatAiTuning current) => Current = current;

        public CombatAiTuning Current { get; }

        public CombatAiTuning? ForVersion(int tuningVersion) =>
            tuningVersion == Current.Version ? Current : null;
    }
}
