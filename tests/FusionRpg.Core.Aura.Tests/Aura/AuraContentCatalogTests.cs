using System.Linq;
using FusionRpg.Core.Aura;
using Xunit;

namespace FusionRpg.Core.Tests.Aura;

/// <summary>aura-skill T16: the twelve auras as data. Opposition closure holds over the non-exempt
/// set (Retribution's unbacked contest channel and Focus's reversal are declared exemptions, not
/// silent gaps) — this is checked by cross-referencing the catalog against ITSELF, not asserted by
/// eye.</summary>
public class AuraContentCatalogTests
{
    static readonly string[] ExemptFromClosure = { "Retribution", "Focus" };

    [Fact]
    public void Exactly_twelve_auras_one_per_aptitude()
    {
        // Reads AptitudeCatalog.Count live rather than a second 12 (population-pin SE3.3,
        // 2026-09-19) -- a real cross-registry check: a 13th aptitude with no matching aura now goes
        // red here instead of two independent literals silently drifting apart.
        var aptitudeCount = FusionRpg.Core.Stats.Aptitudes.AptitudeCatalog.Count;
        Assert.Equal(aptitudeCount, AuraContentCatalog.All.Count);
        Assert.Equal(aptitudeCount, AuraContentCatalog.All.Select(a => a.AptitudeId).Distinct().Count());
    }

    [Fact]
    public void Every_aura_writes_the_omni_slot_never_a_bare_family_or_a_specific_element()
    {
        foreach (var aura in AuraContentCatalog.All)
        {
            foreach (var channel in aura.GrantChannels.Concat(aura.ContestChannels))
            {
                Assert.EndsWith(".omni", channel, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Opposition_closure_holds_over_the_non_exempt_set()
    {
        // For every non-exempt aura's contest channel, some OTHER aura in the catalog must GRANT
        // that exact channel -- proving the table is closed under opposition, not merely asserted.
        var allGrantChannels = AuraContentCatalog.All.SelectMany(a => a.GrantChannels).ToHashSet();

        foreach (var aura in AuraContentCatalog.All)
        {
            if (ExemptFromClosure.Contains(aura.AuraId)) continue;

            foreach (var contested in aura.ContestChannels)
            {
                Assert.Contains(contested, allGrantChannels);
            }
        }
    }

    [Fact]
    public void Retributions_contest_channel_is_the_declared_unbacked_exemption()
    {
        var retribution = AuraContentCatalog.Resolve("Retribution");
        var allGrantChannels = AuraContentCatalog.All.SelectMany(a => a.GrantChannels).ToHashSet();

        // The exemption IS that nothing grants it -- if something ever did, this test should fail
        // loudly rather than silently stop meaning anything.
        Assert.DoesNotContain(retribution.ContestChannels.Single(), allGrantChannels);
    }

    [Fact]
    public void Focus_is_the_declared_reversal_exemption_empty_grant_and_contest()
    {
        var focus = AuraContentCatalog.Resolve("Focus");
        Assert.True(focus.IsReversed);
        Assert.Empty(focus.GrantChannels);
        Assert.Empty(focus.ContestChannels);
    }

    [Fact]
    public void No_aura_channel_ever_appears_in_both_a_grant_list_and_its_own_contest_list()
    {
        // A self-contradicting aura (granting and contesting the same channel) would be a real
        // authoring defect.
        foreach (var aura in AuraContentCatalog.All)
        {
            foreach (var channel in aura.GrantChannels)
                Assert.DoesNotContain(channel, aura.ContestChannels);
        }
    }

    [Fact]
    public void Resolve_of_an_unknown_aura_id_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => AuraContentCatalog.Resolve("not-a-real-aura"));
        Assert.False(AuraContentCatalog.IsKnown("not-a-real-aura"));
    }

    [Fact]
    public void Every_aptitude_id_matches_a_real_ZombossPattern_aptitude_id()
    {
        // Cross-checks against the SAME 12 aptitude ids ZombossPatterns' own force/finesse/bastion
        // pure builds name (Might, Vigor, Onslaught, Retribution / Agility, Composure, Pierce, Focus /
        // Bulwark, Fortitude, Precision, Ferocity) -- proving this catalog didn't invent a 13th
        // aptitude or misspell one of the twelve.
        var forcePure = FusionRpg.Core.Battle.Ai.ZombossPatterns.Resolve("force-pure").SharePermille.Keys;
        var finessePure = FusionRpg.Core.Battle.Ai.ZombossPatterns.Resolve("finesse-pure").SharePermille.Keys;
        var bastionPure = FusionRpg.Core.Battle.Ai.ZombossPatterns.Resolve("bastion-pure").SharePermille.Keys;
        var knownAptitudes = forcePure.Concat(finessePure).Concat(bastionPure).ToHashSet();

        foreach (var aura in AuraContentCatalog.All)
            Assert.Contains(aura.AptitudeId, knownAptitudes);
        // Reads AptitudeCatalog.Count live rather than a second 12 (population-pin SE3.3, 2026-09-19).
        Assert.Equal(FusionRpg.Core.Stats.Aptitudes.AptitudeCatalog.Count, knownAptitudes.Count);
    }
}
