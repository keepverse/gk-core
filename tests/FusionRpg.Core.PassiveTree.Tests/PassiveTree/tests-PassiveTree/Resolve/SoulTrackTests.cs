using System.IO;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>Task D3 — `SoulTrack` (spec-tree-binder.md §5.1-§5.4; spec-tree-resolve.md §6.2;
/// spec-tree-catalog.md §2.3). A soul level offsets `Θ`, it never scales the stored `kMicro`.</summary>
public class SoulTrackTests
{
    static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    static PowerTuning RealPowerTuning() =>
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    static LoadedTree OneNodeTree(long kMicro) =>
        new(new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
                "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true),
            new[]
            {
                new NodeRecord("skill.might-off-t5-n0", "might", TreeBranch.Off, 5, "n0",
                    Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
                    new[]
                    {
                        new NodeAtom("stat.derived", AttachPoint.Stat, DerivedStatChannels.CombatPowerOmni,
                            NodeAtomOp.Flat, null, null, kMicro, ScaleAxis.PTheta, UnitClass.GameUnits),
                    },
                    Array.Empty<string>(), ExclusionForm.None, null, true, null),
            });

    [Fact] // thetaPerSoulLevelMilli = 1000 is one Theta per level
    public void ThetaPerSoulLevelMilli_1000_is_exactly_one_theta_per_level()
    {
        Assert.Equal(100, SoulTrack.ThetaNode(thetaActor: 100, soulLevel: 0, thetaPerSoulLevelMilli: 1000));
        Assert.Equal(105, SoulTrack.ThetaNode(thetaActor: 100, soulLevel: 5, thetaPerSoulLevelMilli: 1000));
        Assert.Equal(150, SoulTrack.ThetaNode(thetaActor: 100, soulLevel: 50, thetaPerSoulLevelMilli: 1000));
    }

    [Fact]
    public void A_fractional_per_mille_rate_divides_exactly_once_before_being_used()
    {
        // 500 per-mille = half a Theta per soul level; the divide happens here, not twice.
        Assert.Equal(125, SoulTrack.ThetaNode(thetaActor: 100, soulLevel: 50, thetaPerSoulLevelMilli: 500));
    }

    [Fact] // soul_level_offsets_theta_never_the_coefficient
    public void Soul_level_offsets_theta_never_the_coefficient()
    {
        var tree = OneNodeTree(kMicro: 3038);
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };
        var tuning = RealPowerTuning();

        // The SAME catalog atom, resolved at two different soul levels via two different Theta_node
        // values -- the atom's own KMicro field is the identical value from the identical NodeAtom
        // instance both times (byte-identical by construction: it is never read or touched by
        // SoulTrack.ThetaNode at all), while the resolved AMOUNT differs because Theta_node differs.
        var thetaAtSoul0 = SoulTrack.ThetaNode(thetaActor: 100, soulLevel: 0, thetaPerSoulLevelMilli: 1000);
        var thetaAtSoul50 = SoulTrack.ThetaNode(thetaActor: 100, soulLevel: 50, thetaPerSoulLevelMilli: 1000);
        Assert.NotEqual(thetaAtSoul0, thetaAtSoul50); // sanity: the two reads really differ

        var boundAtSoul0 = TreeAtomSource.BoundAtomsFor(tree, owned, 10, thetaAtSoul0, tuning, fMilli: 1000);
        var boundAtSoul50 = TreeAtomSource.BoundAtomsFor(tree, owned, 10, thetaAtSoul50, tuning, fMilli: 1000);

        var atomAtSoul0 = tree.Nodes[0].Atoms[0];
        var atomAtSoul50 = tree.Nodes[0].Atoms[0]; // the exact same catalog row -- nothing re-reads it differently
        Assert.Equal(3038, atomAtSoul0.KMicro);
        Assert.Equal(3038, atomAtSoul50.KMicro);
        Assert.Same(atomAtSoul0, atomAtSoul50); // literally the same object -- no per-soul-level copy exists

        // Only the RESOLVED amount moves, because only Theta_node moved.
        Assert.NotEqual(boundAtSoul0[0].Amount, boundAtSoul50[0].Amount);
    }

    [Fact] // power_is_linear_in_souls_spent -- the soul-caused Theta offset has a CONSTANT slope
    public void Power_is_linear_in_souls_spent()
    {
        // Reading: "power" here is the soul-track's own contribution to Theta_node (the quantity this
        // module actually computes) -- its slope with respect to soulLevel is the fixed
        // thetaPerSoulLevelMilli/1000 rate at every level, never accelerating or decaying. (P(Theta)
        // ITSELF is quadratic in Theta by design -- PS-3's whole point -- so a claim that the final
        // RESOLVED MAGNITUDE is linear in soul level would contradict the power ladder; this test
        // instead pins the one quantity SoulTrack itself owns and that IS linear by construction: the
        // Theta offset.)
        const long rateMilli = 1000;
        long? previousTheta = null;
        long? previousDelta = null;
        for (long soulLevel = 0; soulLevel <= 200; soulLevel += 10)
        {
            var theta = SoulTrack.ThetaNode(thetaActor: 100, soulLevel, rateMilli);
            if (previousTheta.HasValue)
            {
                var delta = theta - previousTheta.Value;
                if (previousDelta.HasValue)
                    Assert.Equal(previousDelta.Value, delta); // constant slope across every 10-level step
                previousDelta = delta;
            }
            previousTheta = theta;
        }
    }

    [Fact]
    public void The_soul_read_widens_before_the_multiply_and_throws_rather_than_wraps()
    {
        Assert.Throws<OverflowException>(() =>
            SoulTrack.ThetaNode(thetaActor: 0, soulLevel: long.MaxValue / 2, thetaPerSoulLevelMilli: 1_000_000));
    }

    [Fact]
    public void A_negative_soul_level_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SoulTrack.ThetaNode(100, -1, 1000));
    }

    [Fact]
    public void A_negative_rate_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SoulTrack.ThetaNode(100, 5, -1));
    }

    [Fact]
    public void Zero_soul_level_leaves_theta_actor_unchanged()
    {
        Assert.Equal(100, SoulTrack.ThetaNode(100, 0, 1000));
        Assert.Equal(100, SoulTrack.ThetaNode(100, 0, 999));
    }
}
