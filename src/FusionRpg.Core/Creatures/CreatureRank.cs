namespace FusionRpg.Core.Creatures;

/// <summary>
/// The creature rank ladder's ten rungs (spec-species-rank.md §1, owner-confirmed 2026-09-12). It
/// mirrors the rarity ladder's ids 1:1 but is a <b>separate closed vocabulary</b> on purpose: rank is
/// the hunting-fantasy axis (what the creature IS to a hunter), rarity is the scarcity axis (how hard
/// the item ladder makes it to hold) — unifying them into one enum would collapse two distinct
/// narrative axes into one, so the vocabulary could never say anything the other does not. Precedent
/// for what a careless unification costs: the `common→chaff` migration orphaned persisted `shard.*`
/// ids (`spec-rarity-migration.md:84`).
///
/// <para><b>Rank is identity, never a magnitude.</b> It gates and it displays; it never prices,
/// scales, contests, or enters Θ / <c>P(Θ)</c> / a power vector / a resolver.</para>
///
/// <para>⛔ <b>Never cast to/from <see cref="int"/> or compare with <c>&lt;</c>/<c>&gt;</c>/<c>&lt;=</c>/
/// <c>&gt;=</c> against a named member directly</b> — the same landmine <see cref="CreatureRarity"/>
/// carries: a bare ordinal comparison silently changes what fraction of the ladder it covers the day
/// the ladder widens. Use <see cref="CreatureRankLadder"/>'s named helpers; a guard test
/// (<c>CreatureRankLadderGuardTests</c>) forbids the bare forms outside the ladder file.</para>
/// </summary>
public enum CreatureRank
{
    Chaff,
    Sprout,
    Grafted,
    Cultivated,
    Fused,
    Chimeric,
    Heirloom,
    Firstseed,
    Sunwoven,
    Almanac,
}

/// <summary>The rank ladder's own lower-case ids — the wire spelling shared with
/// <c>gk-core/data/tuning/creature-rank.v1.json</c> and the species anchors' derived <c>rank</c> field.</summary>
public static class CreatureRankIds
{
    public static string ToId(this CreatureRank rank) => rank switch
    {
        CreatureRank.Chaff => "chaff",
        CreatureRank.Sprout => "sprout",
        CreatureRank.Grafted => "grafted",
        CreatureRank.Cultivated => "cultivated",
        CreatureRank.Fused => "fused",
        CreatureRank.Chimeric => "chimeric",
        CreatureRank.Heirloom => "heirloom",
        CreatureRank.Firstseed => "firstseed",
        CreatureRank.Sunwoven => "sunwoven",
        CreatureRank.Almanac => "almanac",
        _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, null)
    };

    /// <summary>The player-facing display copy for a rank id (spec-species-rank.md §5) — the
    /// title-case word for the closed id above, beside <see cref="ToId"/> so a new rung updates
    /// both in the same exhaustive switch (the compiler refuses a missing arm). A pure
    /// presentation projection of the closed vocabulary, never a second vocabulary: the ids are
    /// single words (not dotted paths), so this is the web <c>RARITY_LADDER</c> mirror's own
    /// shape ("a mirror of the ladder, never a second ladder"), not GG-62's forbidden
    /// dotted-id title-casing. Payloads carry this beside the id; the rank VALUE itself is always
    /// copied off the runtime species catalog, never re-derived here.</summary>
    public static string ToDisplayName(this CreatureRank rank) => rank switch
    {
        CreatureRank.Chaff => "Chaff",
        CreatureRank.Sprout => "Sprout",
        CreatureRank.Grafted => "Grafted",
        CreatureRank.Cultivated => "Cultivated",
        CreatureRank.Fused => "Fused",
        CreatureRank.Chimeric => "Chimeric",
        CreatureRank.Heirloom => "Heirloom",
        CreatureRank.Firstseed => "Firstseed",
        CreatureRank.Sunwoven => "Sunwoven",
        CreatureRank.Almanac => "Almanac",
        _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, null)
    };

    /// <summary>The rank's own id, or false for the pipeline's <c>"unresolved"</c> sentinel and for
    /// anything outside the closed vocabulary — never a default (spec Assumption 4: skip, don't
    /// fabricate).</summary>
    public static bool TryParse(string? value, out CreatureRank rank)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "chaff": rank = CreatureRank.Chaff; return true;
            case "sprout": rank = CreatureRank.Sprout; return true;
            case "grafted": rank = CreatureRank.Grafted; return true;
            case "cultivated": rank = CreatureRank.Cultivated; return true;
            case "fused": rank = CreatureRank.Fused; return true;
            case "chimeric": rank = CreatureRank.Chimeric; return true;
            case "heirloom": rank = CreatureRank.Heirloom; return true;
            case "firstseed": rank = CreatureRank.Firstseed; return true;
            case "sunwoven": rank = CreatureRank.Sunwoven; return true;
            case "almanac": rank = CreatureRank.Almanac; return true;
            default: rank = default; return false;
        }
    }
}
