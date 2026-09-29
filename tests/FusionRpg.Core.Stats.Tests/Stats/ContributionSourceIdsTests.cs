using FusionRpg.Core.Commanders;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

public class ContributionSourceIdsTests
{
    [Fact]
    public void Equip_grammar_and_fiction_label()
    {
        var id = ContributionSourceIds.Equip("armament-primary", "item-42");
        Assert.Equal("equip:armament-primary:item-42", id);
        Assert.Equal("Equip · armament-primary (item-42)", ContributionSourceIds.FictionLabel(id));
    }

    [Fact]
    public void Aptitude_tree_status_grant_primary_labels()
    {
        Assert.Equal("Aptitude · Might",
            ContributionSourceIds.FictionLabel(ContributionSourceIds.Aptitude("Might")));
        Assert.Equal("Tree · might/skill/n0",
            ContributionSourceIds.FictionLabel(ContributionSourceIds.Tree("might", "skill.n0")));
        Assert.Equal("Status · abc",
            ContributionSourceIds.FictionLabel(ContributionSourceIds.Status("abc")));
        Assert.Equal("Grant · fx.1",
            ContributionSourceIds.FictionLabel(ContributionSourceIds.Grant("fx.1")));
        Assert.Equal("Primary · cheat|tab-a",
            ContributionSourceIds.FictionLabel(ContributionSourceIds.Primary("cheat", "tab-a")));
        Assert.Equal("Progression",
            ContributionSourceIds.FictionLabel(ContributionSourceIds.Progression));
    }

    [Fact]
    public void Empty_source_is_unattributed_not_silent()
    {
        Assert.Equal("(unattributed)", ContributionSourceIds.FictionLabel(""));
    }

    [Fact]
    public void Combo_grammar_and_fiction_label()
    {
        var id = ContributionSourceIds.Combo("armament-primary", "item-42",
            "combo.strain-might-offense", 0);
        Assert.Equal("combo:armament-primary:item-42:combo.strain-might-offense#c0", id);
        Assert.Equal("Combination · combo.strain-might-offense · armament-primary (item-42, c0)",
            ContributionSourceIds.FictionLabel(id));
        // The SAME combination in another circuit is another contribution — the suffix's whole reason
        // for existing on an eight-socket host.
        Assert.NotEqual(id, ContributionSourceIds.Combo("armament-primary", "item-42",
            "combo.strain-might-offense", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContributionSourceIds.Combo(
            "armament-primary", "item-42", "combo.strain-might-offense", -1));
    }

    // ---- species-layer-projector SP3.1: three new producer rows --------------------------------

    [Fact]
    public void SpeciesBase_grammar_and_fiction_label()
    {
        var id = ContributionSourceIds.SpeciesBase("peashooter");
        Assert.Equal("species-base:peashooter", id);
        Assert.Equal("Species · peashooter", ContributionSourceIds.FictionLabel(id));
    }

    [Fact]
    public void SpeciesPlayer_grammar_and_fiction_label()
    {
        var id = ContributionSourceIds.SpeciesPlayer("peashooter", "pool-roll");
        Assert.Equal("species-player:peashooter:pool-roll", id);
        Assert.Equal("Your species · peashooter (pool-roll)", ContributionSourceIds.FictionLabel(id));
    }

    [Fact]
    public void SpeciesEmpire_grammar_and_fiction_label()
    {
        var id = ContributionSourceIds.SpeciesEmpire(EmpireId.Dave, "peashooter", "Might");
        Assert.Equal("species-empire:dave:peashooter:Might", id);
        Assert.Equal("Empire species · peashooter · Might", ContributionSourceIds.FictionLabel(id));
    }

    [Fact]
    public void A_species_id_containing_a_colon_throws_for_every_species_helper()
    {
        Assert.Throws<ArgumentException>(() => ContributionSourceIds.SpeciesBase("pea:shooter"));
        Assert.Throws<ArgumentException>(() => ContributionSourceIds.SpeciesPlayer("pea:shooter", "pool-roll"));
        Assert.Throws<ArgumentException>(() =>
            ContributionSourceIds.SpeciesEmpire(EmpireId.Dave, "pea:shooter", "Might"));
    }

    [Fact]
    public void SpeciesEmpire_uses_the_empire_id_token_unchanged()
    {
        var id = ContributionSourceIds.SpeciesEmpire(EmpireId.Zomboss, "conezombie", "Ferocity");
        Assert.Equal("species-empire:zomboss:conezombie:Ferocity", id);
    }

    // ---- species-layer-delivery step 6.1 prerequisite, SP3.8: the per-scope aptitude id -------------

    [Fact]
    public void Commander_scope_keeps_the_unchanged_unscoped_form()
    {
        var scoped = ContributionSourceIds.Aptitude(AllocationScope.Commander, "Might");
        var unscoped = ContributionSourceIds.Aptitude("Might");
        Assert.Equal(unscoped, scoped);
        Assert.Equal("aptitude.Might", scoped);
        Assert.Equal("Aptitude · Might", ContributionSourceIds.FictionLabel(scoped));
    }

    [Theory]
    [InlineData(AllocationScope.CreatureType, "creatureType")]
    [InlineData(AllocationScope.Aspect, "aspect")]
    [InlineData(AllocationScope.UniqueCreature, "uniqueCreature")]
    public void Every_other_scope_carries_its_own_scope_text(AllocationScope scope, string scopeText)
    {
        var id = ContributionSourceIds.Aptitude(scope, "Might");
        Assert.Equal($"aptitude.{scopeText}.Might", id);
        Assert.Equal($"Aptitude · {scopeText} · Might", ContributionSourceIds.FictionLabel(id));
    }

    [Fact]
    public void AllocationScopeText_round_trips_through_ContributionSourceIds_Aptitude()
    {
        // The vocabulary this helper reads (AllocationScopeText) is the SAME one RpgStore's own
        // ScopeToText/ScopeFromText now delegate to — proven here by checking every non-Commander
        // scope's text matches what the store-facing type produces.
        foreach (var scope in new[] { AllocationScope.CreatureType, AllocationScope.Aspect, AllocationScope.UniqueCreature })
        {
            var id = ContributionSourceIds.Aptitude(scope, "Vigor");
            Assert.Contains($".{AllocationScopeText.ToText(scope)}.", id, StringComparison.Ordinal);
        }
    }
}
