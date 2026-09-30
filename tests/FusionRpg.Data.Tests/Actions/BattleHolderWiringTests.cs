using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Actions;

/// <summary>
/// T74/A33 (`spec-battle-holder-wiring.md`). `BattleEngine.Resolve` could already price a held action
/// at its holder's rung; nothing in production asked it to. This file proves the two halves that
/// changed:
///
/// <list type="number">
/// <item>the <b>key→owner mapping</b> — the new <see cref="RpgStore"/> helper resolves a battle actor
/// key to the exact <c>OwnerScope</c> the grant path writes, and refuses to invent a holder for
/// anything else; and</item>
/// <item>the <b>production wiring</b> — every `Resolve` call site in the web-match producer passes the
/// pair. This is a source scan on purpose: the claim is "the production caller supplies it", and a unit
/// test that supplied the delegate itself would prove nothing about production.</item>
/// </list>
///
/// <para>The pricing consequence of a low-vs-high effective rung is already proven against the real
/// engine by <c>ActionCostsCooldownsAdoptionTests</c> (both rungs, both costs, same setup); this file
/// does not restate it. What was missing was never the mechanism — it was a caller.</para>
/// </summary>
public sealed class BattleHolderWiringTests
{
    static BattleActorSetup Squad(string key, string? specimenId = null) => new()
    {
        Key = key, Side = "squad", SpeciesId = "wiring.sub", Level = 5, MaxHp = 100, Atk = 10,
        SpecimenId = specimenId,
    };

    static BattleActorSetup Wave(string key) => new()
    {
        Key = key, Side = "wave", SpeciesId = "wiring.host", Level = 5, MaxHp = 100, Atk = 10,
    };

    [Fact]
    public void EachSquadKeyResolvesToItsOwnSpecimensStateNeverItsNeighbours()
    {
        using var fixture = DataTestStore.Create();
        var store = fixture.Store;

        store.SaveUnlockState(
            new OwnerScope(OwnerKind.UniqueActor, "spec.a"),
            UnlockState.FromPersisted(9, new[] { new HeldUnlock("atk.zzz_combo", 9) }));
        store.SaveUnlockState(
            new OwnerScope(OwnerKind.UniqueActor, "spec.b"),
            UnlockState.FromPersisted(3, new[] { new HeldUnlock("atk.basic_strike", 3) }));

        var setup = new BattleSetup
        {
            WaveId = "wiring",
            Squad = new[] { Squad("squad:0", "spec.a"), Squad("squad:1", "spec.b") },
            Wave = new[] { Wave("wave:0") },
        };
        var unlockStateFor = store.UnlockStateFor(setup);

        var a = unlockStateFor("squad:0");
        var b = unlockStateFor("squad:1");

        Assert.Equal(9, a.EarnCount);
        Assert.Equal("atk.zzz_combo", Assert.Single(a.Held).UnlockId);
        Assert.Equal(3, b.EarnCount);
        Assert.Equal("atk.basic_strike", Assert.Single(b.Held).UnlockId);
    }

    /// <summary>Every key that is not a squad actor with a real specimen is "no holder" — never a
    /// fabricated one. This is the half that keeps an enemy, a structure or a non-player force on the
    /// authored-rung fallback rather than paying someone else's price.</summary>
    [Fact]
    public void NonHoldersResolveToEmptyRatherThanSomeoneElsesState()
    {
        using var fixture = DataTestStore.Create();
        var store = fixture.Store;

        store.SaveUnlockState(
            new OwnerScope(OwnerKind.UniqueActor, "spec.a"),
            UnlockState.FromPersisted(9, new[] { new HeldUnlock("atk.zzz_combo", 9) }));

        var setup = new BattleSetup
        {
            WaveId = "wiring",
            Squad = new[] { Squad("squad:0", "spec.a"), Squad("squad:1") /* no SpecimenId */ },
            Wave = new[] { Wave("wave:0") },
        };
        var unlockStateFor = store.UnlockStateFor(setup);

        Assert.Equal(0, unlockStateFor("wave:0").EarnCount);
        Assert.Empty(unlockStateFor("wave:0").Held);
        Assert.Equal(0, unlockStateFor("squad:1").EarnCount);
        Assert.Empty(unlockStateFor("squad:1").Held);
        Assert.Equal(0, unlockStateFor("no.such.key").EarnCount);
    }

    [Fact]
    public void TheSingleOwnerReadTreatsABlankIdAsNoHolder()
    {
        using var fixture = DataTestStore.Create();
        var store = fixture.Store;

        store.SaveUnlockState(
            new OwnerScope(OwnerKind.UniqueActor, "spec.a"),
            UnlockState.FromPersisted(9, new[] { new HeldUnlock("atk.zzz_combo", 9) }));

        Assert.Equal(9, store.UnlockStateOf("spec.a").EarnCount);
        Assert.Equal(0, store.UnlockStateOf("").EarnCount);
        Assert.Equal(0, store.UnlockStateOf(null).EarnCount);
        Assert.Equal(0, store.UnlockStateOf("spec.never.seen").EarnCount);
    }

    /// <summary>Acceptance: the production web-match producer passes the pair. Both the two replay
    /// resolves and the fresh resolve every real match takes — a replay that priced differently from
    /// the fight it replays would not be a replay.</summary>
    [Fact]
    public void EveryWebMatchResolveSitePassesTheUnlockPair()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Server", "WebMatchService.cs"));

        var resolveSites = CountOccurrences(source, "BattleEngine.Resolve(");
        Assert.Equal(3, resolveSites);
        Assert.Equal(resolveSites, CountOccurrences(source, "unlockStateFor: _store.UnlockStateFor("));
        Assert.Equal(resolveSites, CountOccurrences(source, "unlockTuning: UnlockTuningPolicy.Tuning,"));
    }

    /// <summary>The rest of the production call sites — the two Core resolvers and the balance harness.
    /// Acceptance 3 names them explicitly: "every production `BattleEngine.Resolve` call site passes the
    /// pair, or carries a one-line comment saying why that battle has no holders."
    ///
    /// <para>Checked PER FILE with an expected call count, never as one global tally: a new battle mode
    /// is a normal event, and a global count would fail on it for no reason. A file that gains a resolve
    /// site without the pair changes its own count and fails here.</para></summary>
    [Fact]
    public void EveryRemainingProductionResolveSitePassesTheUnlockPair()
    {
        var sites = new (string Path, int ResolveCalls)[]
        {
            (Path.Combine("src", "FusionRpg.Core", "Delve", "Battle", "DelveBattle.cs"), 1),
            (Path.Combine("src", "FusionRpg.Core", "World", "Turn", "DistrictAssaultResolver.cs"), 1),
            (Path.Combine("src", "FusionRpg.Core", "Battle", "SyntheticLoadoutHarness.cs"), 1),
        };

        foreach (var (path, calls) in sites)
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), path));
            Assert.Equal(calls, CountOccurrences(source, "BattleEngine.Resolve("));
            Assert.Equal(calls, CountOccurrences(source, "unlockStateFor:"));
            Assert.Equal(calls, CountOccurrences(source, "unlockTuning:"));
        }
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
