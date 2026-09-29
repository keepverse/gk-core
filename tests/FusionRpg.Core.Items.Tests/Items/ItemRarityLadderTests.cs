using FusionRpg.Core.Creatures;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T25 — the item ladder's own rung arithmetic. Its shape mirrors
/// `CreatureRarityLadder`'s without sharing its type: same ladder SECMENT, different ordinals.
/// </summary>
public class ItemRarityLadderTests
{
    [Fact]
    public void RungCount_is_derived_from_the_ids_so_the_two_cannot_disagree() =>
        Assert.Equal(RarityLadder.RungIds.Count, RarityLadder.RungCount);

    [Fact]
    public void Only_the_last_rung_is_the_top()
    {
        Assert.True(RarityLadder.IsTopRung(RarityLadder.RungIds[^1]));
        Assert.False(RarityLadder.IsTopRung(RarityLadder.RungIds[0]));
        // An id the ladder does not carry is not the top either — it is simply unknown, which is the
        // caller's own refusal to make.
        Assert.False(RarityLadder.IsTopRung("not-a-rung"));
    }

    [Fact]
    public void OneRungAbove_walks_the_ladder_in_order()
    {
        for (var i = 0; i < RarityLadder.RungCount - 1; i++)
        {
            Assert.Equal(RarityLadder.RungIds[i + 1], RarityLadder.OneRungAbove(RarityLadder.RungIds[i]));
        }
    }

    [Fact]
    public void Promotion_past_the_top_throws_rather_than_clamping()
    {
        // An absolute bound derived from the ladder: a caller that can reach the top refuses first
        // (T26 does), so reaching here is a caller defect and must be loud.
        Assert.Throws<InvalidOperationException>(() =>
            RarityLadder.OneRungAbove(RarityLadder.RungIds[^1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RarityLadder.OneRungAbove("not-a-rung"));
    }

    [Fact]
    public void The_ladder_carries_a_promotion_op_kind_and_it_is_the_only_new_one()
    {
        // T25's own acceptance: promotion is the twelfth member, priced by the `elevate` rows that
        // already exist — so no CraftOperation was added beside it.
        Assert.Contains(FusionRpg.Core.Items.Mutation.MutationOpKind.Promotion,
            FusionRpg.Core.Items.Mutation.MutationOpKinds.All);
        Assert.Contains("promotion", FusionRpg.Core.Items.Mutation.MutationOpKinds.AllIds);
        Assert.DoesNotContain("promotion", FusionRpg.Core.Items.Materials.CraftOperations.AllIds);
    }

    // ---- D15 (combat-math-dedup Task 11): the ten rung ids are declared once --------------------

    /// <summary>
    /// <c>CreatureRarityLadder.All</c> is the one declaration; the item ladder reads through it. The
    /// guard is a SET/SEQUENCE equality, so a rung added to the creature enum moves the item ladder
    /// with it (the derivation), and a re-introduced literal block would have to match it exactly to
    /// pass — which is why the negative source scan below is the part that actually bites.
    /// </summary>
    [Fact]
    public void The_rung_ids_are_declared_once()
    {
        var fromEnum = CreatureRarityLadder.All.Select(r => r.ToId()).ToArray();
        Assert.Equal(fromEnum, RarityLadder.RungIds.ToArray());

        // Non-vacuity: the two guarded-rung ids are the enum's own, not a fresh literal pair.
        Assert.Equal(CreatureRarity.Heirloom.ToId(), RarityDraw.HeirloomId);
        Assert.Equal(CreatureRarity.Sunwoven.ToId(), RarityDraw.SunwovenId);
    }

    /// <summary>
    /// The negative guard D15 asks for: no rung-id literal survives in the item-rarity declaring
    /// files. Scoped to <c>RarityLadder.cs</c> + <c>LootPity.cs</c> rather than the whole repo —
    /// `Delve/Difficulty` has its own unrelated rung vocabulary and tests legitimately assert ids, so
    /// a repo-wide scan would be a false-positive generator. It bites on the pre-dedup shape (the
    /// literal array and the <c>const</c> pair this replaced).
    /// </summary>
    [Fact]
    public void The_item_rarity_files_declare_no_rung_id_literal_of_their_own()
    {
        var root = RepoRoot();
        var files = new[]
        {
            Path.Combine(root, "src", "FusionRpg.Core", "Items", "RarityLadder.cs"),
            Path.Combine(root, "src", "FusionRpg.Core", "Items", "Drops", "LootPity.cs"),
        };

        foreach (var file in files)
        {
            Assert.True(File.Exists(file), "missing " + file);
            var text = File.ReadAllText(file);
            foreach (var rung in CreatureRarityLadder.All)
                Assert.DoesNotContain("\"" + rung.ToId() + "\"", text, StringComparison.Ordinal);
        }
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
}
