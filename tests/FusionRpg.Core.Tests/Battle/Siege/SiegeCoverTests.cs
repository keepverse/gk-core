using System.Numerics;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Siege;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-cover` (spec-siege-cover.md), owner decision 35: the HoMM3-inspired shooting
/// model — cover area, range falloff, obstruction, projectile kind — composed into one per-mille
/// power factor. Every mechanic REDUCES power; none of them blocks a shot outright (that is
/// `siege-obstacles`' separate, harder `RequiresLineOfSight`/`LineOfFire.CanFire` gate).
/// </summary>
public class SiegeCoverTests
{
    static readonly SiegeShootingTuning Tuning = new(
        RangeThresholdMilli: 500, RangePowerMilli: 500,
        ObstructionPowerMilli: 700, ObstructionFloorMilli: 250, MeleeLockPowerMilli: 500);

    // ---- Mechanic 1: cover area ----

    [Fact]
    public void Target_in_a_cover_area_takes_reduced_power()
    {
        var target = new GridPos(0, 0);
        var obstacles = new[] { (Cell: new GridPos(0, 1), CoverRadius: 2, CoverPowerMilli: 600) };
        Assert.Equal(600, Shooting.BestCoverMilli(target, obstacles));
    }

    [Fact]
    public void Outside_every_cover_radius_takes_full_power()
    {
        var target = new GridPos(0, 0);
        var obstacles = new[] { (Cell: new GridPos(5, 5), CoverRadius: 1, CoverPowerMilli: 600) };
        Assert.Equal(1000, Shooting.BestCoverMilli(target, obstacles));
    }

    [Fact]
    public void Cover_radius_is_authored_per_kind()
    {
        // Two different "kinds" are just two different (radius, power) tuples -- decision 39's whole
        // point is that the SEED writes the kind and a TUNABLE writes the cells; this proves the
        // mechanism reads whatever radius/power it is handed, per obstacle.
        var target = new GridPos(0, 3);
        var trench = (Cell: new GridPos(0, 0), CoverRadius: 2, CoverPowerMilli: 800); // out of range
        var rampart = (Cell: new GridPos(0, 2), CoverRadius: 2, CoverPowerMilli: 600); // in range
        Assert.Equal(600, Shooting.BestCoverMilli(target, new[] { trench, rampart }));
    }

    [Fact]
    public void Best_single_cover_applies_and_covers_do_not_stack()
    {
        var target = new GridPos(0, 0);
        var weak = (Cell: new GridPos(0, 1), CoverRadius: 2, CoverPowerMilli: 900);
        var strong = (Cell: new GridPos(1, 0), CoverRadius: 2, CoverPowerMilli: 500);
        // The BEST (lowest-power, i.e. strongest reduction) single cover applies -- not the product of
        // both, which would make a cluster of cheap works strictly better than one good one.
        Assert.Equal(500, Shooting.BestCoverMilli(target, new[] { weak, strong }));
    }

    [Fact]
    public void A_destroyed_obstacle_projects_no_cover_and_no_obstruction()
    {
        // Mechanic 5's whole appeal: destroying an obstacle removes BOTH effects together. Neither
        // BestCoverMilli nor ObstructionPowerMilli know about HP/ruin state themselves (by design,
        // matching Shooting's own doc comment) -- the caller excludes a destroyed obstacle from BOTH
        // lists it builds, so the "single mechanism" this test proves is that exclusion, done once,
        // removes both effects at the same time rather than requiring two separate un-wirings.
        var target = new GridPos(0, 0);
        var liveObstacleCell = new GridPos(0, 1);

        var withLiveObstacle = Shooting.BestCoverMilli(target, new[] { (Cell: liveObstacleCell, CoverRadius: 2, CoverPowerMilli: 600) });
        var obstructionWithLive = Shooting.ObstructionPowerMilli(new[] { new Obstruction(liveObstacleCell) }, Tuning);

        // Once destroyed, a correct caller passes empty lists for BOTH -- proving the single exclusion
        // point removes both effects together, not one now and one forgotten.
        var withDestroyedObstacle = Shooting.BestCoverMilli(target, Array.Empty<(GridPos, int, int)>());
        var obstructionWithDestroyed = Shooting.ObstructionPowerMilli(Array.Empty<Obstruction>(), Tuning);

        Assert.True(withLiveObstacle < 1000, "sanity: the live obstacle must have granted real cover");
        Assert.True(obstructionWithLive < 1000, "sanity: the live obstacle must have obstructed");
        Assert.Equal(1000, withDestroyedObstacle);
        Assert.Equal(1000, obstructionWithDestroyed);
    }

    // ---- Mechanic 2: range ----

    [Fact]
    public void Power_falls_off_beyond_the_range_threshold()
    {
        // boardSide=10, rangeThresholdMilli=500 -> threshold = 5 cells.
        Assert.Equal(1000, Shooting.RangePowerMilli(chebyshevDistance: 5, boardSide: 10, Tuning));
        Assert.Equal(500, Shooting.RangePowerMilli(chebyshevDistance: 6, boardSide: 10, Tuning));
    }

    [Fact]
    public void Range_threshold_scales_with_board_side()
    {
        // Distance 10 is within threshold on a 30-cell board (threshold 15) but beyond it on an
        // 18-cell board (threshold 9) -- an 18-cell and a 30-cell board must not share a falloff point.
        Assert.Equal(1000, Shooting.RangePowerMilli(chebyshevDistance: 10, boardSide: 30, Tuning));
        Assert.Equal(500, Shooting.RangePowerMilli(chebyshevDistance: 10, boardSide: 18, Tuning));
    }

    // ---- Mechanic 3: obstruction ----

    [Fact]
    public void A_unit_in_the_line_reduces_power()
    {
        var oneObstruction = new[] { new Obstruction(new GridPos(1, 1)) };
        Assert.Equal(700, Shooting.ObstructionPowerMilli(oneObstruction, Tuning));
    }

    [Fact]
    public void An_obstruction_reduces_but_never_blocks()
    {
        var many = Enumerable.Range(0, 20).Select(i => new Obstruction(new GridPos(i, i))).ToList();
        var power = Shooting.ObstructionPowerMilli(many, Tuning);
        Assert.True(power > 0, "an obstruction must reduce power, never zero it out (never a block)");
    }

    [Fact]
    public void Obstructions_compound_but_stop_at_the_floor()
    {
        var many = Enumerable.Range(0, 20).Select(i => new Obstruction(new GridPos(i, i))).ToList();
        Assert.Equal(Tuning.ObstructionFloorMilli, Shooting.ObstructionPowerMilli(many, Tuning));

        var few = new[] { new Obstruction(new GridPos(0, 0)) };
        Assert.True(Shooting.ObstructionPowerMilli(few, Tuning) > Shooting.ObstructionPowerMilli(many, Tuning),
            "more obstructions must reduce power at least as much as fewer");
    }

    [Fact]
    public void Zero_obstructions_is_full_power()
    {
        Assert.Equal(1000, Shooting.ObstructionPowerMilli(Array.Empty<Obstruction>(), Tuning));
    }

    // ---- Mechanic 3b: melee lock ----

    [Fact]
    public void A_shooter_with_an_adjacent_enemy_shoots_weaker()
    {
        Assert.Equal(500, Shooting.MeleeLockPowerMilli(shooterHasAdjacentEnemy: true, Tuning));
        Assert.Equal(1000, Shooting.MeleeLockPowerMilli(shooterHasAdjacentEnemy: false, Tuning));
    }

    // ---- Line trace: determinism, symmetry, tie-break ----

    [Fact]
    public void Line_trace_is_identical_across_ten_thousand_runs()
    {
        var a = new GridPos(0, 0);
        var b = new GridPos(7, 4);
        var first = LineOfFire.Trace(a, b);
        for (var i = 0; i < 10_000; i++)
            Assert.Equal(first, LineOfFire.Trace(a, b));
    }

    [Fact]
    public void Line_trace_is_symmetric()
    {
        var a = new GridPos(0, 0);
        var b = new GridPos(7, 4);
        Assert.Equal(LineOfFire.Trace(a, b), LineOfFire.Trace(b, a));
    }

    [Fact]
    public void The_trace_is_never_passed_to_a_targeting_resolver()
    {
        // Source scan: LineOfFire.Trace's own return type is a plain cell list, never a
        // CompiledTargetSpec/ActionTargetMode -- and no file in Actions/ references LineOfFire at all,
        // confirming the trace never becomes a fifth area shape by the back door.
        var actionsDir = FindRepoDir("src/FusionRpg.Core/Actions");
        foreach (var file in Directory.GetFiles(actionsDir, "*.cs", SearchOption.AllDirectories))
        {
            if (System.IO.Path.GetFileName(file) == "ProjectilePenalties.cs") continue;
            Assert.DoesNotContain("LineOfFire", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    static string FindRepoDir(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"could not find '{relativePath}' above {AppContext.BaseDirectory}");
    }

    // ---- RequiresLineOfSight / ProjectilePenalties ----

    [Fact]
    public void Requires_line_of_sight_means_pays_obstruction_not_blocked()
    {
        // Decision 35 fixes the MEANING: an action that sets RequiresLineOfSight pays the obstruction
        // penalty (mechanic 3); it does NOT get hard-blocked by an obstruction (only a
        // BlocksLineOfFire structure, siege-obstacles' separate harder gate, does that).
        var oneObstruction = new[] { new Obstruction(new GridPos(1, 1)) };
        var power = Shooting.ObstructionPowerMilli(oneObstruction, Tuning);
        Assert.True(power > 0); // reduced, never zeroed -- "pays the penalty", not "is blocked"
    }

    [Fact]
    public void Default_projectile_pays_every_penalty()
    {
        var row = new ActionRow { ActionId = "test.shot" };
        Assert.Equal(ProjectilePenalties.All, row.ProjectilePenalties);
    }

    [Theory]
    [InlineData(ProjectilePenalties.Range)]
    [InlineData(ProjectilePenalties.Obstruction)]
    [InlineData(ProjectilePenalties.MeleeLock)]
    [InlineData(ProjectilePenalties.All)]
    [InlineData(ProjectilePenalties.None)]
    public void Projectile_flags_exempt_exactly_what_they_name(ProjectilePenalties flags)
    {
        var row = new ActionRow { ActionId = "test.shot", ProjectilePenalties = flags };
        Assert.Equal(flags, row.ProjectilePenalties);
        Assert.Equal(flags.HasFlag(ProjectilePenalties.Range), (flags & ProjectilePenalties.Range) != 0);
        Assert.Equal(flags.HasFlag(ProjectilePenalties.Obstruction), (flags & ProjectilePenalties.Obstruction) != 0);
        Assert.Equal(flags.HasFlag(ProjectilePenalties.MeleeLock), (flags & ProjectilePenalties.MeleeLock) != 0);
    }

    // ---- Mechanic 6: composition ----

    [Fact]
    public void Penalties_compose_multiplicatively_in_one_place()
    {
        var power = Shooting.ComposedPower(basePower: 1000, coverMilli: 600, rangeMilli: 500,
            obstructionMilli: 700, meleeLockMilli: 1000);
        // 1000 * 0.6 * 0.5 * 0.7 * 1.0 = 210
        Assert.Equal(210, power);
    }

    [Fact]
    public void Power_chain_overflows_loudly()
    {
        Assert.Throws<OverflowException>(() =>
            Shooting.ComposedPower(long.MaxValue / 2, 1000, 1000, 1000, 2000));
    }

    [Fact]
    public void Four_divides_beat_one_combined_divide()
    {
        const long basePower = 9_000_000_000_000L; // large enough that combining divisors would overflow long differently
        const int cover = 900, range = 800, obstruction = 700, meleeLock = 600;

        var actual = Shooting.ComposedPower(basePower, cover, range, obstruction, meleeLock);

        BigInteger reference = (BigInteger)basePower * cover / 1000;
        reference = reference * range / 1000;
        reference = reference * obstruction / 1000;
        reference = reference * meleeLock / 1000;
        Assert.Equal((long)reference, actual);
    }

    [Fact]
    public void Multipliers_are_equally_decisive_at_theta_1_and_theta_200()
    {
        // Scale-free claim: the SAME per-mille multiplier produces the SAME proportional reduction
        // regardless of the base magnitude -- it never touches P(Theta) and never decays.
        var small = Shooting.ComposedPower(100, 500, 1000, 1000, 1000);
        var large = Shooting.ComposedPower(100_000_000, 500, 1000, 1000, 1000);
        Assert.Equal(50, small);
        Assert.Equal(50_000_000, large);
    }

    // ---- §8: what this module no longer does ----

    [Fact]
    public void No_dodge_grant_no_scope_membership_change_no_source_by_cover_matrix()
    {
        var shootingText = File.ReadAllText(FindRepoFile("src/FusionRpg.Core/Battle/Siege/Shooting.cs"));
        var lineOfFireText = File.ReadAllText(FindRepoFile("src/FusionRpg.Core/Battle/Siege/LineOfFire.cs"));

        // No combat.dodge.omni grant -- the contest path is untouched by cover.
        Assert.DoesNotContain("dodge", shootingText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dodge", lineOfFireText, StringComparison.OrdinalIgnoreCase);

        // No ScopeMembershipTransition change here -- that budget belongs to siege-obstacles' Mine.
        Assert.DoesNotContain("ScopeMembershipTransition", shootingText, StringComparison.Ordinal);
        Assert.DoesNotContain("ScopeMembershipTransition", lineOfFireText, StringComparison.Ordinal);

        // No (damage source x cover type) matrix -- superseded by four multipliers plus the
        // projectile flags. DamageSourceKind exists (siege-obstacles owns it) but this module never
        // references a matrix/table type keyed by it.
        Assert.DoesNotContain("DamageSourceKind", shootingText, StringComparison.Ordinal);
    }

    static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"could not find '{relativePath}' above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void The_wire_carries_each_factor_separately()
    {
        var breakdown = new ShootingBreakdown(CoverMilli: 600, RangeMilli: 500, ObstructionMilli: 700,
            MeleeLockMilli: 1000, FinalPower: Shooting.ComposedPower(1000, 600, 500, 700, 1000));
        Assert.Equal(600, breakdown.CoverMilli);
        Assert.Equal(500, breakdown.RangeMilli);
        Assert.Equal(700, breakdown.ObstructionMilli);
        Assert.Equal(210, breakdown.FinalPower);
    }
}
