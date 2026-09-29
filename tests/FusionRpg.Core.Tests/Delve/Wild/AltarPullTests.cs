using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Creatures;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>
/// D4.7 (spec-wild-room.md §6) — `AltarPull.TryPull`. `SummonRoller`/`SummonBannerCatalog` are
/// already configured for the whole assembly by `ContractTuningTestBootstrap`'s own
/// `[ModuleInitializer]` (via `SummoningTuningHub.Configure`) — no per-test setup needed here.
///
/// <para><b>Named, not tested here</b>: "a pull is lost on a wipe and minted on extraction" is the
/// full delve-lifecycle half of this task's own acceptance — it needs `CloseDelve`'s store-side haul
/// persistence, which is explicitly D4.8's own job ("Cage, refusals, **store** and endpoints"), not
/// buildable or testable against this file alone. What IS this file's own job — that it never
/// re-derives the roll or the pity math itself — is proven below.</para>
/// </summary>
public class AltarPullTests
{
    static SeededRng Rng(ulong seed, string name) => SeededRng.DeriveStream(seed, name);

    [Fact]
    public void TryPull_with_a_known_banner_succeeds_and_returns_one_result()
    {
        var ok = AltarPull.TryPull(
            SummonBannerCatalog.StandardRift, focusElement: null, PityState.Fresh, Rng(11, "dungeon:altar:test:1"),
            out var result, out _, out var refusalId);

        Assert.True(ok);
        Assert.Null(refusalId);
        Assert.False(string.IsNullOrWhiteSpace(result.SpeciesId));
    }

    [Fact]
    public void TryPull_refuses_altar_banner_unknown_for_an_unrecognized_id()
    {
        var ok = AltarPull.TryPull(
            "not-a-real-banner", focusElement: null, PityState.Fresh, Rng(11, "dungeon:altar:test:1"),
            out _, out var newPity, out var refusalId);

        Assert.False(ok);
        Assert.Equal(AltarRefusal.BannerUnknown, refusalId);
        Assert.Equal(PityState.Fresh, newPity); // refused before any roll -- pity is untouched
    }

    [Fact]
    public void TryPull_matches_calling_SummonRoller_Roll_directly_no_private_recomputation()
    {
        var banner = SummonBannerCatalog.TryGet(SummonBannerCatalog.ElementFocus)!;
        var pity = new PityState(PullsSinceHeirloom: 5, PullsSinceSunwoven: 5);

        var (direct, directPity) = SummonRoller.Roll(banner, null, count: 1, pity, Rng(99, "dungeon:altar:test:2"));
        AltarPull.TryPull(
            SummonBannerCatalog.ElementFocus, focusElement: null, pity, Rng(99, "dungeon:altar:test:2"),
            out var viaAltarPull, out var viaAltarPity, out _);

        // A record with a List<string> property compares that property by reference, not content
        // (SummonRollerTests.cs's own established workaround) -- project to a value tuple instead.
        (string, CreatureRarity, string, string) AsTuple(SummonRollResult r) =>
            (r.SpeciesId, r.Rarity, r.Variant, string.Join(',', r.TraitIds));
        Assert.Equal(AsTuple(direct[0]), AsTuple(viaAltarPull));
        Assert.Equal(directPity, viaAltarPity);
    }

    [Fact]
    public void TryPull_genuinely_advances_pity_by_the_rollers_own_rules_a_heirloom_hard_pity_case()
    {
        // Pin one of SummonRollerTests.cs's own real cases (pull 25 hard-pities heirloom+) through
        // THIS function, proving pity advancement is the roller's own rule, not reimplemented here.
        var pity = new PityState(PullsSinceHeirloom: 24, PullsSinceSunwoven: 24);
        AltarPull.TryPull(
            SummonBannerCatalog.StandardRift, focusElement: null, pity, Rng(3, "dungeon:altar:test:3"),
            out var result, out _, out _);
        Assert.True(CreatureRarityLadder.AtLeast(result.Rarity, CreatureRarity.Heirloom));
    }

    [Fact]
    public void TryPull_never_calls_with_more_than_one_pull_count_is_not_exposed_as_a_parameter()
    {
        // Structural: there is no `count` parameter on TryPull at all -- confirmed by the method's
        // own signature above compiling with exactly these six parameters. This test exists to keep
        // that structural fact load-bearing rather than merely a doc-comment claim.
        var method = typeof(AltarPull).GetMethod(nameof(AltarPull.TryPull));
        Assert.NotNull(method);
        Assert.DoesNotContain(method!.GetParameters(), p => p.Name == "count");
    }

    [Fact]
    public void TryPull_null_pity_throws()
    {
        Assert.Throws<ArgumentNullException>(() => AltarPull.TryPull(
            SummonBannerCatalog.StandardRift, null, null!, Rng(1, "s"), out _, out _, out _));
    }
}
