using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P1.11 — every channel an aptitude edge names must resolve through
/// the battle compose path's `ChannelMods` overlay without throwing "Unknown combat channel id".
/// Widening the known-channel set is the whole fix (P1.11's own acceptance: compose LOGIC is
/// unchanged) — this test proves the widened set, not just that it compiles. battle-hub-fuse T6:
/// `BattleHubCompose.Compose` reads the Hub's own registry for this check now (same unknown-channel
/// refusal, re-homed with the composer's deletion).</summary>
public class BattleKnownChannelsTests
{
    [Fact]
    public void EveryAptitudeEdgeChannelIsInTheBattleKnownChannelSet()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "tools", "CombatSim", "tuning", "aptitudes.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var channelIds = doc.RootElement.GetProperty("edges").EnumerateArray()
            .Where(e => e.TryGetProperty("channel", out var c) && !string.IsNullOrWhiteSpace(c.GetString()))
            .Select(e => e.GetProperty("channel").GetString()!)
            // `retire-atk` R2/R4: the frozen POC v1 still names the retired channel, and the battle
            // compose path must NOT know it — a retired channel is deliberately unresolvable.
            .Where(id => !DerivedStatChannels.Retired.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(channelIds.Count > 0, "parsed zero edge channels from aptitudes.v1.json");

        var setup = MinimalSetup(channelIds.Select(id => new BattleChannelMod(id, 100)).ToList());

        // Compose must not throw "Unknown combat channel id" for ANY of them -- a computed set
        // difference against the registry (matching G2's own registration check) explains WHICH
        // channel, if any, was missed, rather than a bare exception from Compose.
        var registeredIds = DerivedStatRegistry.CreateDefault().AllRegistered.Select(d => d.ChannelId).ToHashSet(StringComparer.Ordinal);
        var missingFromRegistry = channelIds.Where(id => !registeredIds.Contains(id)).ToList();
        Assert.True(missingFromRegistry.Count == 0,
            "aptitude edge channel(s) not registered at all: " + string.Join(", ", missingFromRegistry));

        var ex = Record.Exception(() => BattleHubCompose.Compose(setup));
        Assert.Null(ex);
    }

    [Fact]
    public void ComposeLogicUnchanged_defenseAndBaseChannelsStillResolveTheSameWay()
    {
        // P1.11's own boundary: only the KNOWN-CHANNEL SET widened. The five base channels Compose
        // always sets (defense/accuracy/dodge/critRate/critResist) must still resolve to exactly the
        // documented formula, proving no compose logic moved alongside the set widening.
        var setup = MinimalSetup(Array.Empty<BattleChannelMod>());
        var snap = BattleHubCompose.Compose(setup);

        Assert.Equal(setup.Defense, snap.Get(DerivedStatChannels.CombatDefenseOmni));
        Assert.Equal(BattleRuleset.BaseAccuracy(setup.Index), snap.Get(DerivedStatChannels.CombatAccuracyOmni));
        Assert.Equal(BattleRuleset.BaseDodge(setup.Index), snap.Get(DerivedStatChannels.CombatDodgeOmni));
    }

    static BattleActorSetup MinimalSetup(IReadOnlyList<BattleChannelMod> channelMods) => new()
    {
        Key = "test:0",
        Side = "squad",
        SpeciesId = "test-species",
        TypeId = 90_001,
        Level = 10,
        MaxHp = BattleRuleset.BaseHp(10),
        Atk = BattleRuleset.BaseAtk(10),
        Defense = BattleRuleset.BaseDefense(10),
        ChannelMods = channelMods
    };

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
