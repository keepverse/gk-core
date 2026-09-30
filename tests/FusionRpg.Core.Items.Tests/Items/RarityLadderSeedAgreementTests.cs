using System.Text.Json;
using FusionRpg.Core.Items;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// tier-propagation-contract T2, Open question 1: <c>RarityLadder.RungIds</c> stays as the
/// boot-time declaring read the seed import validates against (converting it to a tuning read
/// would move a load-order dependency for no behavioural gain) — and this test is what makes
/// that allowlist honest: the array must match <c>gk-data/packs/fusion/data/seed/rarity/ladder.v1.json</c>
/// value-for-value, in ordinal order. A rung added to one side without the other fails here,
/// not in production. The rung set is a CLOSED VOCABULARY the code owns (validation-ssot.md
/// §1); nothing here asserts a corpus size.
/// </summary>
public class RarityLadderSeedAgreementTests
{
    [Fact]
    public void RungIds_match_the_seeded_ladder_value_for_value()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(KeepverseRoots.Content(), "data", "seed", "rarity", "ladder.v1.json")));

        var seeded = doc.RootElement.GetProperty("entries").EnumerateArray()
            .OrderBy(e => e.GetProperty("ordinal").GetInt32())
            .Select(e => e.GetProperty("id").GetString()!)
            .ToList();

        Assert.Equal(seeded, RarityLadder.RungIds);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
