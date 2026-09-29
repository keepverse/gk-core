using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

/// <summary>
/// `atom-catalog-ssot.md` is the document that answers "what is in the atom effect pool". It had no
/// test, and it drifted badly: it claimed **99** derived channels against a real **267**, **8** primary
/// channels against a real **11**, and **7** triggers against a real **8** — the derived figure stale
/// across three separate expansions (T2 element widening 99→256, `poise-resource` →259,
/// `turn.speed`/`turn.haste` →261, `resource.restore` →267). Measured and corrected 2026-09-02
/// (`docs/research/atom-effect-pool-audit-2026-09-02.md`).
///
/// <para><b>Why the sibling document never drifted:</b> `spec-derived-stat-sheet.md` carries the same
/// numbers and is pinned by <c>ElementHubDocDriftTests.StatSheetCountsMatchGeneration</c>. This suite is
/// that same shape, applied to the file that actually calls itself the SSOT — including the planted-drift
/// companion, because a drift test that cannot fail is not a guard.</para>
/// </summary>
public class AtomCatalogSsotDriftTests
{
    const string Doc = "atom-catalog-ssot.md";

    static string ReadSsot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "docs", "architecture", "effect-atom", Doc);
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"could not find docs/architecture/effect-atom/{Doc}");
    }

    [Fact]
    public void ChannelAndVocabularyCountsMatchCode()
    {
        var text = ReadSsot();
        var registry = DerivedStatRegistry.CreateDefault();

        // Sanity anchors: these ARE today's generated values, not guesses. If one of these Assert.Equal
        // lines fails, code moved and the doc needs updating — that is the point.
        // 267 -> 268 (party-dungeon D4.25, 2026-09-06): `loadout.slots`, the extend-action-slot grant.
        // 268 -> 269 (base-defense siege-ai, 2026-09-07): `ai.aggression`, §5.20 rule 4's signed
        // targeting-priority scalar.
        // 269 -> 268 (solid-enforcement `retire-atk` R4, 2026-09-18: `progression.bonus.atk` retired by owner ruling and unregistered).
        // Canonical site (solid-enforcement population-pin SE3.2, 2026-09-19): every other file
        // asserting this SAME total now reads it live off this same registry, never a second literal
        // (the six-fold `269` defect this module measured and closed) — ElementHubDocDriftTests/
        // SeedCatalogTests/StatTaxonomyTests/ActorSurfaceCatalogTests (x2).
        // pin: closed-vocabulary DerivedStatRegistry.AllRegistered — a new channel is a reviewed change
        Assert.Equal(268, registry.AllRegistered.Count);
        // 11 since E16; E38 (spec-entity-fields-12plus.md) took it to 23.
        // pin: closed-vocabulary StatChannels — a new primary channel is a reviewed change
        Assert.Equal(23, StatChannels.All.Length);
        // E34 (spec-trigger-vocabulary.md): 8 -> 13 with OnWave/OnMatchStart/OnMatchEnd/OnSunCollect/
        // OnGridPlace. Verified live before editing, as this module's own citation-hygiene rule asks.
        // pin: closed-vocabulary AtomTriggers — a new trigger is a reviewed change
        Assert.Equal(13, AtomTriggers.All.Length);
        // E35 (spec-match-modify.md): 12 -> 13 kinds, 5 -> 6 attach points, with match.modify / Match.
        // E36 (spec-wave-control.md): 13 -> 14 kinds, wave.control on the same Match attach point.
        // E37 (spec-projectile-control.md): 14 -> 15 kinds, bullet.modify on the existing Board point
        // (no attach-point change).
        // E41 (spec-ui-attach-point.md): 15 -> 16 kinds, 6 -> 7 attach points, with ui.present / Ui.
        // base-defense `siege-construction` (decision 27, 2026-09-06): 16 -> 17 kinds, 7 -> 8 attach
        // points, with structure.place / Siege.
        // passive-tree `element-conversion` (D56, spec-element-conversion.md, 2026-09-07): 17 -> 18
        // kinds, 8 -> 9 attach points, with element.convert / Element.
        // pin: closed-vocabulary AtomKindRegistry.KindCount — a new atom kind is a reviewed change
        Assert.Equal(18, AtomKindRegistry.KindCount);
        // pin: closed-vocabulary AtomKindRegistry.AttachPointCount — a new attach point is a reviewed change
        Assert.Equal(9, AtomKindRegistry.AttachPointCount);

        Assert.Contains($"Derived — {registry.AllRegistered.Count} registered", text);
        Assert.Contains($"Primary — {StatChannels.All.Length}, and only these", text);
        Assert.Contains($"Trigger vocabulary — {AtomTriggers.All.Length}", text);
        Assert.Contains($"The closed kind list — {AtomKindRegistry.KindCount}", text);

        // Every primary channel is named in the doc's own §4.1 list, so the list cannot go stale by
        // omission the way it did when attackInterval/produceInterval/zombieSpeed were "pending".
        foreach (var channel in StatChannels.All)
            Assert.Contains($"`{channel}`", text);

        // Same for triggers — OnActivate was missing entirely until 2026-09-02.
        foreach (var trigger in AtomTriggers.All)
            Assert.Contains($"`{trigger}`", text);
    }

    [Fact]
    public void ChannelAndVocabularyCountsMatchCode_failsOnAPlantedDrift()
    {
        var text = ReadSsot();

        // The exact pre-correction wording. If any of these ever comes back the doc has regressed to a
        // number that was wrong by 168 channels, and the test above would still pass on a partial edit.
        Assert.DoesNotContain("Derived — 99 pre-registered", text);
        Assert.DoesNotContain("Primary — 8, and only these", text);
        Assert.DoesNotContain("Trigger vocabulary — 7", text);
        Assert.DoesNotContain("| **Triggers** | **7** |", text);

        // The 2026-08-22 sweep's status claims, all re-measured 2026-09-02 and all wrong: every one of
        // the 21 statuses has an executing consumer in at least one runtime. "13 functional" was the
        // number that made ember/jala/kelp and the four ModifyStat statuses look unauthorable.
        Assert.DoesNotContain("## 5. Status catalog — 21 declared, 13 functional", text);
    }

    /// <summary>
    /// The status catalog is the largest enumerable vocabulary behind any atom param (`status.apply`
    /// takes one of these ids), so its size is part of "what is in the pool". Pinned to the bootstrap
    /// rather than to a literal.
    /// </summary>
    [Fact]
    public void StatusCountMatchesTheCatalog()
    {
        var text = ReadSsot();
        var declared = StatusCatalogBootstrap.CreateDefault().All().Count;

        Assert.Equal(24, declared); // sanity anchor: today's generated value (21 -> 24, delve-attrition D2.19's nerve.* block)
        Assert.Contains($"Status catalog — {declared} declared", text);
    }

    /// <summary>
    /// The audit's load-bearing distinction: the vocabulary is BUILT while the pool is EMPTY. A reader
    /// who takes this file as an inventory of authorable content gets it exactly backwards, which is why
    /// §0 exists — and why it must keep existing.
    /// </summary>
    [Fact]
    public void SsotStatesThatRegisteredIsNotAuthored()
    {
        var text = ReadSsot();
        Assert.Contains("The POOL is empty", text);
        Assert.Contains("Registered ≠ authorable", text);
    }
}
