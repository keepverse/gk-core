using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E37 (spec-projectile-control.md §2b, criteria 5/6). The full <c>Bullet.InitData</c> postfix
/// ordering rule — <c>bullet.modify</c> grants fold first, D- cheat state applies last and always wins
/// — extracted into <see cref="BulletFireResolver"/> so it is provable here with no live Unity
/// <c>Bullet</c>. <c>CheatPrefixes.BulletInitCheat</c> in the injector is a thin shell over this.
/// </summary>
public class BulletFireResolverTests
{
    static readonly BulletFireState Fresh = new(Damage: 100, BulletType: null, MoveWay: null);

    // No cheat set: IVal's own -1-means-unset convention, FVal's own 1.0-means-unset convention.
    const int NoSet = -1;
    const float NoPercent = 1f;
    const int NoSwap = -1;
    const bool NoHoming = false;

    static BoundBulletModifyAtom Grant(string op, long amount, int? bulletType = null, string? moveWay = null) =>
        new(op, amount, bulletType, moveWay, SourceId: "atom.test");

    // Criterion 5: a bound grant changes the damage of a bullet the game fired, with no cheat set.
    [Fact]
    public void A_bound_grant_changes_damage_with_no_cheat_set()
    {
        var resolved = BulletFireResolver.Resolve(
            Fresh, new[] { Grant("add", 50) }, NoSet, NoPercent, NoSwap, NoHoming);

        Assert.Equal(150, resolved.Damage);
    }

    [Fact]
    public void A_bound_grant_can_change_bulletType_and_moveWay_with_no_cheat_set()
    {
        var resolved = BulletFireResolver.Resolve(
            Fresh, new[] { Grant("set", 200, bulletType: 3, moveWay: "Track") },
            NoSet, NoPercent, NoSwap, NoHoming);

        Assert.Equal(200, resolved.Damage);
        Assert.Equal(3, resolved.BulletType);
        Assert.Equal("Track", resolved.MoveWay);
    }

    // Criterion 6: cheat state still wins over a bound bullet.modify grant — the ordering test.
    [Fact]
    public void D_DMG_SET_wins_over_a_bound_grant()
    {
        var resolved = BulletFireResolver.Resolve(
            Fresh, new[] { Grant("set", 999) },
            cheatDamageSet: 42, NoPercent, NoSwap, NoHoming);

        Assert.Equal(42, resolved.Damage); // NOT 999 — the grant's own value, if cheat did not win
    }

    [Fact]
    public void D_DMG_percent_wins_over_a_bound_grant()
    {
        var resolved = BulletFireResolver.Resolve(
            Fresh, new[] { Grant("set", 999) },
            NoSet, cheatDamagePercent: 0.5f, NoSwap, NoHoming);

        // Applied to the GRANT's own result (999), not the fresh 100 — cheat reads whatever is
        // current when it runs, same as the pre-E37 behaviour this resolver's tail preserves exactly.
        Assert.Equal(500, resolved.Damage); // round(999 * 0.5) = 500 (round-half-to-even -> 500)
    }

    [Fact]
    public void D_TYPE_SWAP_wins_over_a_bound_grants_bulletType()
    {
        var resolved = BulletFireResolver.Resolve(
            Fresh, new[] { Grant("set", 100, bulletType: 3) },
            NoSet, NoPercent, cheatTypeSwap: 9, NoHoming);

        Assert.Equal(9, resolved.BulletType); // NOT 3
    }

    [Fact]
    public void D_HOMING_wins_over_a_bound_grants_moveWay()
    {
        var resolved = BulletFireResolver.Resolve(
            Fresh, new[] { Grant("set", 100, moveWay: "Puff") },
            NoSet, NoPercent, NoSwap, cheatHoming: true);

        Assert.Equal("Track", resolved.MoveWay); // NOT "Puff"
    }

    // The structural floor (§3: "a zero-damage bullet is inert, not balanced") survives the refactor —
    // a percent cheat that would round to 0 still clamps to 1, exactly as it did pre-E37.
    [Fact]
    public void D_DMG_percent_still_floors_at_one_rather_than_zeroing_the_bullet()
    {
        var resolved = BulletFireResolver.Resolve(
            new BulletFireState(1, null, null), Array.Empty<BoundBulletModifyAtom>(),
            NoSet, cheatDamagePercent: 0.01f, NoSwap, NoHoming);

        Assert.Equal(1, resolved.Damage); // round(1 * 0.01) = 0, floored to 1
    }

    // Multiple grants fold left to right — proven directly rather than assumed.
    [Fact]
    public void Multiple_grants_fold_in_order()
    {
        var resolved = BulletFireResolver.Resolve(
            Fresh, new[] { Grant("add", 50), Grant("scale", 2000) }, // (100+50) * 2.0 = 300
            NoSet, NoPercent, NoSwap, NoHoming);

        Assert.Equal(300, resolved.Damage);
    }

    [Fact]
    public void No_grants_and_no_cheat_leaves_the_bullet_untouched()
    {
        var resolved = BulletFireResolver.Resolve(
            Fresh, Array.Empty<BoundBulletModifyAtom>(), NoSet, NoPercent, NoSwap, NoHoming);

        Assert.Equal(Fresh, resolved);
    }
}
