using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T5.1 (`world-seed`, `spec-world-seed.md`): the one derivation contract every per-player roll in
/// this program (and `creature-seed`'s `player-materialise`) must go through.
/// </summary>
public class WorldSeedTests
{
    [Fact]
    public void Derive_roll_seed_is_pure_and_deterministic()
    {
        var a = WorldSeed.DeriveRollSeed(42, "affix.draw", "item.ember-band");
        var b = WorldSeed.DeriveRollSeed(42, "affix.draw", "item.ember-band");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_stream_names_never_collide()
    {
        var slot = WorldSeed.DeriveRollSeed(42, "affix.slot", "item.ember-band");
        var draw = WorldSeed.DeriveRollSeed(42, "affix.draw", "item.ember-band");

        Assert.NotEqual(slot, draw);
    }

    [Fact]
    public void Different_target_ids_never_collide()
    {
        var a = WorldSeed.DeriveRollSeed(42, "affix.draw", "item.ember-band");
        var b = WorldSeed.DeriveRollSeed(42, "affix.draw", "item.blazing-crown");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_world_seeds_never_collide_for_the_same_stream_and_target()
    {
        var a = WorldSeed.DeriveRollSeed(1, "affix.draw", "item.ember-band");
        var b = WorldSeed.DeriveRollSeed(2, "affix.draw", "item.ember-band");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_lost_roster_reconstructs_byte_identically_from_the_two_retained_numbers_alone()
    {
        // §3.6's own property: (worldSeed, catalog_revision) together are the whole fact. Simulated
        // here as "roll twice, independently, from nothing but those two numbers plus the stream/
        // target contract" — no other state feeds the derivation.
        const long worldSeed = 9001;
        const long catalogRevision = 7;
        var targetId = $"species-passive.conezombie@{catalogRevision}"; // the target folds in the revision itself

        var first = WorldSeed.DeriveRollSeed(worldSeed, "affix.draw", targetId);
        var second = WorldSeed.DeriveRollSeed(worldSeed, "affix.draw", targetId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void An_empty_stream_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => WorldSeed.DeriveRollSeed(1, "", "item.ember-band"));
    }

    [Fact]
    public void An_empty_target_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => WorldSeed.DeriveRollSeed(1, "affix.draw", ""));
    }
}
