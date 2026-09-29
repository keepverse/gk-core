using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// The <c>creature-kill</c> loot source (species-gear-chain T30, `creature-drop-tables` E1),
/// resolved at RUNTIME from a kill the expedition / delve / wild path reports.
///
/// <para><b>What changed, and why this file exists.</b> A kill is a loot event with its own
/// per-rung tables, but no seed file can hold its source row: the row's id must be unique per kill
/// (species + kill ref), or two kills would share one correlation id and the second would replay
/// the first and mint nothing — the exact trap `WorldSectorLootSource`'s own class doc records for
/// sectors. So the row is resolved, never authored, and the tables it points at are authored per
/// rung in <c>gk-data/packs/fusion/data/seed/loot/tables-creature.json</c>.</para>
///
/// <para>⛔ <b>Kill attribution keys on a source these three paths supply — never the lawn.</b>
/// `KillerActorKey` carries nothing on the lawn (spec § What exists, struck bullet), so the source
/// id is built from what a server-resolved kill always has — the killed species id and a
/// caller-supplied kill ref (battle / room / encounter id plus kill index) — not from any lawn
/// pointer. Gameless-first: resolving a source needs no game, no match, no actor.</para>
///
/// <para>⛔ <b>No new material id, no new <c>DropEntryKind</c>.</b> The tables draw from the closed
/// 27-id material vocabulary (`MaterialCatalog.All`) and the existing `Equipment` mint arm; trophy
/// entries are T30b/T34c's, not this file's.</para>
///
/// <para>This type stays free of <c>FusionRpg.Core.Creatures</c> on purpose, the same way
/// `WorldSectorLootSource` stays free of <c>FusionRpg.Core.World</c>: the rung arrives as the
/// ten-rung ladder id both ladders share (`RarityLadder.RungIds`), so the loot lane never loads the
/// creature catalog to price a kill. A rung the ladder does not carry is refused by name — never
/// defaulted to chaff, which would price a sunwoven kill as trash.</para>
///
/// <para><b>No new <c>f(level)</c>.</b> The content level passes straight through from the caller —
/// the path that reports the kill already knows its own level, and deriving one here from the rung
/// would be a second power curve next to the one ladder (`ssot-power-scale.md`).</para>
/// </summary>
public static class CreatureKillLootSource
{
    static CreatureKillLootSource() => ContentRuleNamespaces.Register("drop");

    /// <summary>One of <see cref="DropTableValidator.KnownSourceKinds"/>, reserved since T30.</summary>
    public const string SourceKind = "creature-kill";

    /// <summary>
    /// The per-rung table a kill draws from. One table per rung id on the shared ten-rung ladder —
    /// the per-rung granularity Open question 3 recommends, with the per-species override slot left
    /// empty for a later task to fill.
    /// </summary>
    public static string TableIdFor(string rungId) => $"drop.creature.rung-{rungId}";

    /// <summary>
    /// The loot source for one reported kill, or a rejection naming why there is none.
    ///
    /// <para>⛔ <b>The kill ref must identify ONE kill event.</b> The correlation id derives from the
    /// source id (`LootCorrelation.Derive`), and the pipeline keys idempotency on
    /// (player_id, correlation_id) — so two kills sharing a ref collapse into one loot event and the
    /// second mints nothing. A blank ref is refused by name rather than defaulted, for the same
    /// reason a blank sector id is.</para>
    /// </summary>
    public static AtomRejection TryResolve(
        string speciesId, string rungId, string killRef, int contentLevel, out LootSourceRow? source)
    {
        source = null;

        if (string.IsNullOrWhiteSpace(speciesId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                "a creature-kill loot source needs the killed species' id — the table is per-rung, "
                + "but the source names the kill, and a kill without a species is not attributable");

        if (string.IsNullOrWhiteSpace(rungId) ||
            !RarityLadder.RungIds.Contains(rungId, StringComparer.Ordinal))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"a creature-kill loot source needs the killed species' rung id — '{rungId}' is not "
                + "one of the ten rungs, and defaulting it would price the kill on the wrong table");

        if (string.IsNullOrWhiteSpace(killRef))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                "a creature-kill loot source needs the kill's OWN ref (battle / room / encounter id "
                + "plus kill index) — the correlation id derives from it, and a shared ref would make "
                + "two kills one loot event");

        if (contentLevel < 1)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"loot_source 'creature-kill' has content_level {contentLevel}; item level is content, "
                + "and content starts at 1");

        source = new LootSourceRow(SourceKind, $"{speciesId}:{killRef}", TableIdFor(rungId), contentLevel);
        return AtomRejection.Ok;
    }
}
