using System.Text.Json;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `base-types` (item module 6) against the REAL, shipped `frame-lean.v1.json` — D11 clauses 2 and 3
/// (spec-base-types.md).
/// </summary>
public class FrameLeanTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static string RawJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "items", "_registry", "frame-lean.v1.json"));

    static FrameLeanTable Load() => FrameLean.Parse(RawJson());

    static readonly ClassLadder[] AllLadders =
        { ClassLadder.Armour, ClassLadder.Weapon, ClassLadder.Offhand, ClassLadder.Jewel, ClassLadder.Standard };

    [Fact]
    public void The_frame_lean_table_has_ten_blocks_and_eight_leans()
    {
        using var doc = JsonDocument.Parse(RawJson());
        var leans = doc.RootElement.GetProperty("leans");
        Assert.Equal(5, leans.EnumerateObject().Count()); // five ladders

        var authored = Load().AuthoredLeanCount;
        Assert.Equal(8, authored); // 4 real ladders x 2 frames; standard's pair is null, not authored
    }

    [Fact]
    public void The_standard_pair_is_declared_and_empty()
    {
        var table = Load();
        Assert.Null(table.Of(ClassLadder.Standard, ItemFrame.Humanoid));
        Assert.Null(table.Of(ClassLadder.Standard, ItemFrame.Plant));
    }

    [Fact]
    public void The_frame_lean_is_identical_across_every_real_ladder()
    {
        // D11 clause 3, HARD: one axis per frame, correlated across every ladder a hybrid-core role
        // could draw from -- the widening (item-ideal.md §2f.2).
        var table = Load();
        var real = new[] { ClassLadder.Armour, ClassLadder.Weapon, ClassLadder.Offhand, ClassLadder.Jewel };
        Assert.True(table.CorrelationHolds(real));

        foreach (var ladder in real)
        {
            Assert.Equal("burst", table.Of(ladder, ItemFrame.Humanoid)!.Value.ImplicitAxis);
            Assert.Equal("sustain", table.Of(ladder, ItemFrame.Plant)!.Value.ImplicitAxis);
        }
    }

    [Fact]
    public void A_per_role_lean_table_is_rejected_at_load()
    {
        // Clause 3 cannot be defeated by relocating the field: the parser's schema has no concept of
        // a role at all, only (ladder, frame). A document naming a role instead of a ladder key
        // fails to find any of the five required ladder keys and rejects.
        var perRole = """
            { "leans": { "armament-primary": { "humanoid": null, "plant": null } } }
            """;
        Assert.Throws<FrameLeanRejection>(() => FrameLean.Parse(perRole));
    }

    [Fact]
    public void No_lean_channel_is_side_restricted()
    {
        // plating/carapace write zombie-only Unity fields; a lean on them silently voids one frame.
        var table = Load();
        string[] sideRestricted = { "arm1", "arm1Max", "arm2", "arm2Max" };

        foreach (var ladder in new[] { ClassLadder.Armour, ClassLadder.Weapon, ClassLadder.Offhand, ClassLadder.Jewel })
        foreach (var frame in new[] { ItemFrame.Humanoid, ItemFrame.Plant })
        {
            var profile = table.Of(ladder, frame)!.Value;
            foreach (var channel in profile.BaseSplitPermille.Keys)
                Assert.DoesNotContain(channel, sideRestricted);
        }
    }

    [Fact]
    public void Neither_frames_profile_is_a_superset_of_the_others_for_any_real_ladder()
    {
        var table = Load();
        foreach (var ladder in new[] { ClassLadder.Armour, ClassLadder.Weapon, ClassLadder.Offhand, ClassLadder.Jewel })
        {
            var h = table.Of(ladder, ItemFrame.Humanoid)!.Value;
            var p = table.Of(ladder, ItemFrame.Plant)!.Value;
            Assert.True(FrameLeanTable.NeitherIsASuperset(h, p), $"{ladder}: one frame's profile dominates the other's");
        }
    }

    [Fact]
    public void No_body_role_resolves_to_the_standard_ladder()
    {
        string[] bodyRoles =
        {
            "armament-primary", "core-guard", "ward-array", "armament-secondary", "jewel-major",
            "manipulator", "mantle", "head-guard", "girdle", "sense", "footing", "infusion",
            "retinue", "jewel-minor-a", "jewel-minor-b",
        };

        foreach (var role in bodyRoles)
            Assert.NotEqual(ClassLadder.Standard, BaseTypeSlate.LadderOf(role));
    }

    [Fact]
    public void Parse_rejects_empty_input() =>
        Assert.Throws<FrameLeanRejection>(() => FrameLean.Parse(""));

    [Fact]
    public void Parse_rejects_a_non_null_standard_substitute_that_is_missing_fields()
    {
        var bad = """{ "leans": { "armour": {"humanoid": {}, "plant": {}}, "weapon": {"humanoid": null, "plant": null}, "offhand": {"humanoid": null, "plant": null}, "jewel": {"humanoid": null, "plant": null}, "standard": {"humanoid": null, "plant": null} } }""";
        Assert.Throws<FrameLeanRejection>(() => FrameLean.Parse(bad));
    }
}
