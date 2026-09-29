namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// Every named RNG stream the loot pipeline derives, in one place (ssot-generation.md §4.3).
///
/// <para>The <c>item.</c> prefix is deliberately distinct from the shipped <c>atom.</c> prefix
/// (<see cref="FusionRpg.Core.Effects.Atoms.AtomStreams"/>) so an added item roll can never shift an
/// atom roll. Per-system streams derive from one sealed seed, which is what makes adding step 5a
/// (volume) leave every step-6…10 draw byte-identical — the property
/// <c>the_volume_stream_shifts_no_other_stream</c> asserts.</para>
///
/// <para>Names are built here and nowhere else. A stream name spelled inline at a call site is how
/// two systems end up sharing one stream by accident.</para>
/// </summary>
public static class LootStreams
{
    /// <summary>Step 2 — seals the event. Derived from the SOURCE record's seed, never the client's.</summary>
    public static string LootSeed(string correlationId) => "loot:" + correlationId;

    /// <summary>Step 3 — one item-level jitter draw per event.</summary>
    public const string ItemLevel = "item.ilvl";

    /// <summary>
    /// Step 5a — the volume scale's Bernoulli remainder, named for the group so adding a group never
    /// shifts another's draws. ⭐ This stream is NEW in module 11; it exists on its own name precisely
    /// so introducing it moves nothing that already rolled.
    /// </summary>
    public static string Volume(string tableId, string groupKey) =>
        $"item.volume.{tableId}.{groupKey}";

    /// <summary>Step 5 — one weighted draw per group, named for the group.</summary>
    public static string GroupDraw(string tableId, string groupKey) =>
        $"item.table.{tableId}.{groupKey}";

    /// <summary>
    /// species-gear-chain T34c (`species-materials` d, R22) — which of a multi-family species' own
    /// families a family-scope trophy group resolves to, named beside <see cref="GroupDraw"/> so
    /// adding this draw shifts no other stream's own sequence (the property
    /// `the_volume_stream_shifts_no_other_stream` already proves for step 5a; this is the same shape
    /// one level over). A single-family species consumes NO draw from this stream at all — the
    /// family is already known, so there is nothing to roll (R22's own "never doubles family volume"
    /// rule, restated as "never spends a roll it does not need").
    /// </summary>
    public static string TrophyFamily(string tableId, string groupKey) =>
        $"item.trophy-family.{tableId}.{groupKey}";

    /// <summary>Step 5 — a nested table's draw; depth disambiguates a table nested twice.</summary>
    public static string NestedGroupDraw(string tableId, string groupKey, int depth) =>
        $"item.table.{tableId}.{groupKey}.{depth}";

    /// <summary>Step 5 — non-equipment stack sizes.</summary>
    public static string Quantity(int index) => $"item.qty.{index}";

    /// <summary>Step 6 — INDEX, never the drawn id: a name that depended on the draw would shift
    /// later draws whenever content was added.</summary>
    public static string BaseType(int index) => $"item.base.{index}";

    /// <summary>Step 7 — the rarity ladder draw.</summary>
    public static string Rarity(int index) => $"item.rarity.{index}";

    /// <summary>Step 8 — affix counts, separate from rarity so a count-range change never moves a rarity.</summary>
    public static string Rolls(int index) => $"item.rolls.{index}";

    /// <summary>
    /// Step 9 — the per-instance roll seed handed to <c>Instantiator.TryInstantiate</c>. Derived from
    /// the loot seed so the two reproduction contracts hold at once (ssot-generation.md §4.3).
    /// </summary>
    public static string RollSeed(int index) => $"item.rollseed.{index}";

    /// <summary>
    /// Step 10 — sockets, rolled LAST so a count can never shift an affix.
    ///
    /// <para>⚠ Derived from the instance's own <c>roll_seed</c>, not from the loot seed. This spec's
    /// own step-10 row states <c>DeriveStream(roll_seed, "item.socket")</c> and module 16 —
    /// <c>spec-sockets.md:143-145</c>, the owner of the count rule — states the same. ssot-generation
    /// §4.3's stream table writes <c>item.socket.{i}</c> off the loot seed instead; the two disagree,
    /// and the owning module's spelling wins, because using the other one here would hand module 16 a
    /// different stream than it is written against — exactly the "a step added later is a migration"
    /// defect this ordering exists to prevent.</para>
    /// </summary>
    public const string Sockets = "item.socket";
}
