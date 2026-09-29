using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-map W44 acceptance: `RecruitStock`/`ProjectId`/`ProjectTurnsRemaining` default to
/// zero/null on every existing template, and each has its own rejecting case (matching
/// `LoamValidationTests`'s own pattern for rules 9-14, one level up for rules 15-16).
/// </summary>
public class SectorDevelopmentValidationTests
{
    static WorldState FirstLight() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 42);
    static WorldState TwoHearths() => WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 7);

    static string Throws(WorldState w) =>
        Assert.Throws<InvalidOperationException>(() => WorldValidation.Validate(w)).Message;

    static IReadOnlyList<WorldSector> Replace(IReadOnlyList<WorldSector> source, int index, Func<WorldSector, WorldSector> edit) =>
        source.Select((item, i) => i == index ? edit(item) : item).ToList();

    [Fact]
    public void Every_sector_of_both_shipped_templates_defaults_the_three_new_fields()
    {
        foreach (var w in new[] { FirstLight(), TwoHearths() })
            foreach (var s in w.Sectors)
            {
                Assert.Equal(0, s.RecruitStock);
                Assert.Null(s.ProjectId);
                Assert.Null(s.ProjectTurnsRemaining);
            }
    }

    [Fact]
    public void Rule15_negative_recruit_stock_rejects()
    {
        var w = FirstLight();
        var broken = w with { Sectors = Replace(w.Sectors, 0, s => s with { RecruitStock = -1 }) };
        Assert.Contains(w.Sectors[0].SectorId, Throws(broken));
    }

    [Fact]
    public void Rule15_zero_recruit_stock_is_legal()
    {
        var w = FirstLight();
        var atFloor = w with { Sectors = Replace(w.Sectors, 0, s => s with { RecruitStock = 0 }) };
        WorldValidation.Validate(atFloor); // does not throw
    }

    [Fact]
    public void Rule16_project_turns_remaining_with_no_project_id_rejects()
    {
        var w = FirstLight();
        var broken = w with { Sectors = Replace(w.Sectors, 0, s => s with { ProjectId = null, ProjectTurnsRemaining = 3 }) };
        Assert.Contains(w.Sectors[0].SectorId, Throws(broken));
    }

    [Fact]
    public void Rule16_a_project_id_with_its_own_turns_remaining_is_legal()
    {
        var w = FirstLight();
        var withProject = w with
        {
            Sectors = Replace(w.Sectors, 0, s => s with { ProjectId = "placeholder-project", ProjectTurnsRemaining = 3 })
        };
        WorldValidation.Validate(withProject); // does not throw — ProjectCatalog (W52) is what would reject an unknown id
    }

    [Fact]
    public void Rule16_a_finished_project_with_no_turns_remaining_is_legal()
    {
        var w = FirstLight();
        var finished = w with
        {
            Sectors = Replace(w.Sectors, 0, s => s with { ProjectId = "placeholder-project", ProjectTurnsRemaining = null })
        };
        WorldValidation.Validate(finished); // does not throw
    }
}
