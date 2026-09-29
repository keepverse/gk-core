using System.Text.RegularExpressions;

namespace FusionRpg.Core.Items.Uniques;

/// <summary>
/// ssot-uniques.md §3.7 device 1. Declared per unique and <b>checked against the content</b>, never
/// trusted from the column — the same device ssot-sets.md used to make the Diablo 3 set failure
/// literally unauthorable.
/// </summary>
public enum UniqueCounterPressure
{
    /// <summary>The item costs you something: a core atom with a negative magnitude.</summary>
    Drawback = 0,

    /// <summary>The capability only fires in a state: a core atom carrying a non-empty predicate.</summary>
    Conditional,

    /// <summary>It is deliberately a worse stat stick: summed raw-stat AE under the rung's ceiling.</summary>
    Narrow,
}

/// <summary>ssot-uniques.md §4.5's three acquisition channels.</summary>
public enum UniqueAcquisition
{
    /// <summary>Low weight in the general table. Refused at ordinal ≥ 90 (§4.5 rule 1).</summary>
    Drop = 0,

    /// <summary>One source id — a boss, a sector, an expedition tier. The primary channel.</summary>
    SourceLocked,

    /// <summary>First clear or a blueprint craft. The deterministic top-rung answer.</summary>
    Deterministic,
}

/// <summary>ssot-uniques.md §4.4. One value today; the column exists because I6 reads it.</summary>
public enum UniqueEnhanceScope
{
    /// <summary>
    /// Enhancement may touch <c>stat.modify</c> and <c>resource.delta</c> <b>magnitudes</b> in the
    /// core and nothing else: "20% more Lava" is not a number, and scaling a <c>chance</c> or an
    /// <c>icd_ms</c> moves a capability's shape rather than its size.
    /// </summary>
    MagnitudeOnly = 0,
}

/// <summary>
/// Structural limits of the unique class. Every one is <b>exempt</b> from AGENTS.md's
/// no-hard-ceilings rule and, as that rule requires, says here why.
/// </summary>
public static class UniqueLimits
{
    /// <summary>
    /// <b>STRUCTURAL — this is the class's own definition, not a progression ceiling.</b>
    /// spec-uniques.md's corrected shape rule: a unique is defined as
    /// <c>PrefixRolls + SuffixRolls ≤ 1</c> (the shipped columns; <c>pool_rolls</c> no longer
    /// exists — T3.2 replaced it because D2 and PoE cap the two classes separately). Two rolls
    /// reintroduce the rare's grind on the one item whose promise was that <i>finding</i> it was the
    /// event. Making this a tunable would let a balance pass author a rare with a name, which is the
    /// exact thing §3.2's refusal exists to prevent — so it is a const, and the player's path to a
    /// stronger unique is a different unique, enhancement, or the variance reroll.
    /// </summary>
    public const int MaxTotalRolls = 1;

    /// <summary>
    /// <b>STRUCTURAL, exempt, and saying so as AGENTS.md requires.</b> Promotion <i>only adds</i>
    /// affixes drawn in the new rung's window (ssot-rarity.md §3.7 rule 3), and a unique is DEFINED as
    /// <see cref="MaxTotalRolls"/>. Promoting one would either break its shape or do nothing. This is
    /// <b>not</b> a progression ceiling — D7 lifted ssot-rarity rule 7, so the rung ladder itself now
    /// reaches ordinal 100 for everything; this is the class's own shape.
    /// </summary>
    public const bool UniquesArePromotable = false;

    /// <summary>
    /// <b>STRUCTURAL, and the one place a reviewer will mistake it for a gap.</b> Effect-pipeline
    /// module 12 (`affix-channel-weights`) turns a power class into a <i>pool rate</i>. A unique's
    /// identity atoms are fixed-core rows and are <b>never drawn</b>, so no channel weight applies to
    /// them — not "their weight is tuned to zero", but <i>there is no draw for a weight to modify</i>.
    /// L0's coverage report showing every unique at 0.00 is correct, not missing data. The
    /// <b>variance slot is the exception and is fully L0-governed</b>: it is one real draw from an
    /// authored affix pool and carries a power class and a channel weight like any other draw.
    /// </summary>
    public const int FixedCoreChannelWeightMilli = 0;

    /// <summary>
    /// The <c>rarity_budget</c> key this module owns, registered in <see cref="RarityBudgetKeys"/>.
    /// Its value is <b>derived</b> from the rung ordinal against the tuning floor, never authored as a
    /// second per-rung table beside the seeded ladder.
    /// </summary>
    public const string EligibilityBudgetKey = "unique_eligible";
}

/// <summary>
/// The <c>item_unique</c> row — ssot-uniques.md §5.2's nine columns, keyed 1:1 on an ordinary
/// <c>effect_container</c> of kind <c>item</c>. The container and its <c>item_base_type</c> row stay
/// ordinary, so bind, frame filter, role gate, requirement gate and socket capacity need no branch.
/// </summary>
/// <param name="ContainerId">
/// <c>item.{slug}</c>. <b>Not the seed id.</b> The authoring corpus tracks a unique as
/// <c>unique.{theme}-{band}-{seq}</c>, which is not a legal container id at all — `definitions.md` §1's
/// alternation has no <c>unique.</c> arm — and `naming.v1.json`'s own `idVsContainerIdNote` left the
/// derivation open "for wave-1b". <see cref="UniqueContainerIds"/> closes it.
/// </param>
/// <param name="DerivedFrom">
/// The parent <c>item_base_type</c> id, for display and for inheriting class/frame flavour —
/// "Kiln Nozzle — Pea Nozzle". ⚠ <b>Not an FK:</b> the <c>item_base_type</c> table exists
/// (corrected 2026-09-23 — this used to say none existed) and its <c>id</c> is a primary key, so an FK
/// is possible; none was ever added, and adding one is a schema change. The reference is checked by
/// the corpus validator against the loaded base-type registry, not by SQLite.
/// </param>
/// <param name="BudgetAeHundredths">The author's declared total in <b>AE × 100</b> (SC4 forbids floats
/// in content). E9 replaces it with a computed power read when one exists.</param>
/// <param name="PowerAxis">One of `core.v1.json`'s five `powerCategories` — the axis the item
/// <i>is about</i>, and the third key of the anti-convergence rule.</param>
/// <param name="FlavourKey">
/// A localisation <b>key</b>, never a literal — the same rule module 7 applied to `display_key`.
/// Nullable: 32 of the shipped 144 carry no flavour key yet.
/// </param>
public sealed record UniqueRow(
    string ContainerId,
    string DerivedFrom,
    UniqueCounterPressure CounterPressure,
    long BudgetAeHundredths,
    string PowerAxis,
    UniqueAcquisition Acquisition,
    UniqueEnhanceScope EnhanceScope = UniqueEnhanceScope.MagnitudeOnly,
    string? FlavourKey = null,
    bool Enabled = true,
    int Revision = 1);

/// <summary>
/// The seed id → container id derivation `naming.v1.json` left open, and the parse back.
///
/// <para>The authoring corpus's <c>unique.{theme}-{rungBandLowOrdinal}-{seq:03}</c> is a
/// <b>tracking</b> id: globally unique, structurally collision-safe, and deliberately outside the
/// container grammar so a seed row can never be mistaken for a shipped container. A unique's real
/// container id is an ordinary <c>item.{slug}</c> (ssot-uniques.md §3.4 / §5.1) — no new
/// <c>container_kind</c>, no new prefix.</para>
///
/// <para>The slug is the seed id's body, verbatim. Rewriting it into something prettier would make the
/// mapping unrecoverable in both directions, and a corpus of 144 rows whose db ids cannot be traced
/// back to the partition that authored them is the defect this type exists to avoid.</para>
/// </summary>
public static class UniqueContainerIds
{
    public const string SeedPrefix = "unique.";
    public const string ContainerPrefix = "item.";

    /// <summary>The container-id body grammar `definitions.md` §1 fixes, mirrored here.</summary>
    static readonly Regex SlugRe = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary><c>unique.ember-harvest-30-001</c> → <c>item.ember-harvest-30-001</c>.</summary>
    public static string FromSeedId(string seedId)
    {
        if (seedId is null) throw new ArgumentNullException(nameof(seedId));
        if (!seedId.StartsWith(SeedPrefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"unique seed id '{seedId}' does not start with '{SeedPrefix}' — naming.v1.json " +
                "idNamespaces.uniques fixes the template", nameof(seedId));

        var slug = seedId[SeedPrefix.Length..];
        if (!SlugRe.IsMatch(slug))
            throw new ArgumentException(
                $"unique seed id '{seedId}' has body '{slug}', which is not the kebab-case slug a " +
                "container id requires", nameof(seedId));

        return ContainerPrefix + slug;
    }

    /// <summary>The inverse, so a shipped row can always name the partition that authored it.</summary>
    public static string ToSeedId(string containerId)
    {
        if (containerId is null) throw new ArgumentNullException(nameof(containerId));
        if (!containerId.StartsWith(ContainerPrefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"container id '{containerId}' does not start with '{ContainerPrefix}'", nameof(containerId));
        return SeedPrefix + containerId[ContainerPrefix.Length..];
    }
}
