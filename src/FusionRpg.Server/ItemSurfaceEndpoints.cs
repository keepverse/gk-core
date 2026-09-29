using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// item module 20 (`item-surfaces`) — the <b>read-only</b> server half of the six player surfaces.
///
/// <para>⛔ <b>Every route here reads. None writes, and none reaches generation.</b> D26 puts drop
/// volume and pacing out of the item program's scope, and the loot filter is the interface answer to
/// review pressure, never the metering one — so the armoury route hands the client rows and counts
/// and lets <see cref="LootFilterView"/> decide what is drawn. There is no <c>MapPost</c> in this
/// file, deliberately: equipping, socketing and salvaging already have owners (modules 4, 16, 14) and
/// a second write path through the presentation layer is exactly the "second surface" this module
/// exists to prevent.</para>
///
/// <para>⚠ <b>Honestly scoped, and the gap is named rather than filled with a guess.</b> An armoury
/// row's <c>role</c> and <c>frame</c> come from the item's BASE TYPE. ✅ <b>Corrected 2026-09-23:
/// they are filled</b> — this paragraph used to say they "come back empty here and role/frame filtering
/// is not offered, the field is ready the day the table exists", which stopped being true twice over:
/// <c>item_base_type</c> now exists, and the two fields are read from the item's own generation row
/// (<c>:152-181</c>, <c>generation?.Role</c>/<c>generation?.Frame</c>) and passed to
/// <c>ArmouryQuery.ApplyFilter</c> at <c>:197</c>. The earlier plan — answer them from the container's
/// <c>slot</c> — is still refused, and for the same reason: it is a different axis and would be a
/// plausible wrong answer.</para>
/// </summary>
public static class ItemSurfaceEndpoints
{
    public sealed record SurfaceStatusDto(string Surface, string State, string UnlockKey);

    /// <param name="BattleOnly">
    /// item-content `granted-action-text` (T15): this item grants an action that only exists inside a
    /// battle (a DefaultAttack grant replaces the species' basic attack). `ssot-presentation.md`
    /// §9.14 asks for the tag HERE and not only on the card — "a player scanning an armoury should
    /// not have to open each item to learn that half of them are inert on the lawn". Derived by
    /// <c>RpgStore.GrantsBattleOnlyAction</c>, the same read card block 9 uses.
    /// </param>
    /// <param name="ContainerName">
    /// item-content `item-naming` (T3): the base type's own AUTHORED name for this container, read
    /// through the same <see cref="ItemBaseTypeCorpus"/> delegate the card route uses (T2 carried the
    /// `name` field through; nothing outside the card read it). Additive and defaulted to <c>""</c>,
    /// which is the honest "this build has no corpus row for that container" — the client then says
    /// so rather than printing the container id in a name slot.
    ///
    /// <para>⚠ <b>It is the BASE TYPE's name, not module 8's composed one.</b> Composing the affix
    /// name needs the instance's frozen atoms and a full render per row, and the armoury page is up
    /// to 50 rows of a collection that is deliberately unbounded. The compact line shows the noun the
    /// composed name is built on; the card — one item, one render — shows the whole name.</para>
    /// </param>
    /// <param name="OriginKind">
    /// live-probe Task 21 (found 2026-09-16 closing Task 14): the row's real, persisted
    /// <c>RpgItemRow.OriginKind</c> ("drop"/"quest-reward"/"dungeon-clear"/...) — it always existed
    /// on the store row, it just never crossed the wire, so "lists it with a real provenance" could
    /// only be checked by reading the mint path in code, never through the API itself. Additive,
    /// never empty in practice (the column has a NOT NULL DEFAULT 'drop'), but defaulted here to ""
    /// for the same reason <see cref="ContainerName"/> is: an unmapped future origin should read as
    /// "unknown" to the client, never crash the route.
    /// </param>
    public sealed record ArmouryRowDto(
        string InstanceId, string ContainerId, string Rarity, int RarityOrdinal,
        bool Assigned, bool Locked, bool Unseen, bool Stale, string AcquiredUtc,
        bool BattleOnly = false, string ContainerName = "", string OriginKind = "",
        string Role = "", string Frame = "");

    public sealed record ArmouryPageDto(
        int Total, int Unseen, bool OverReviewPressure, string RenderStrategy, IReadOnlyList<ArmouryRowDto> Rows);

    public sealed record CombinationRowDto(
        string ComboId, string Shape, string State, int? Distance,
        IReadOnlyList<string> MissingFamilies, IReadOnlyList<string> MissingElements, int GrantedTier);

    /// <param name="lookupInsert">
    /// ⭐ <b>The gem catalog, by container id</b> — the same <see cref="GemInsertCorpus"/> delegate
    /// <see cref="ItemCardEndpoints"/> already reads (module 16 shipped <c>gems/*.json</c> as seed
    /// JSON, not a table, so this is a boot-time stopgap exactly like
    /// <see cref="BaseTypeSocketMaxCorpus"/>).
    ///
    /// <para>⛔ <b>Fixed 2026-09-06.</b> Both socket reads below used to build every
    /// <see cref="InsertDef"/> with a hardcoded <c>Element: ""</c>, and
    /// <c>CombinationEvaluator</c> matches ingredients on <c>Insert.Element</c> — so every
    /// element-shaped resonance (Pure, Ring, Eclipse, and Diversity's distinct-element count) was
    /// unreachable through this route no matter what the player had socketed. <c>null</c> here keeps
    /// the old element-free shape rather than failing, and a container the corpus does not carry
    /// falls back to <c>""</c>, which is a LEGITIMATE value: an element-free insert
    /// (<c>SocketModel.cs:72</c>) contributes to no resonance shape at all.</para>
    /// </param>
    /// <param name="lookupBaseType">
    /// ⭐ <b>The base-type corpus, by container id</b> — the same <see cref="ItemBaseTypeCorpus"/>
    /// delegate <see cref="ItemCardEndpoints"/> reads, handed in rather than loaded a second time for
    /// the same reason <paramref name="lookupInsert"/> is: two loads are two chances to disagree about
    /// what an item is called. <c>null</c> leaves every row's <c>ContainerName</c> empty, which is the
    /// pre-2026-09-06 shape and still honest.
    /// </param>
    public static void MapItemSurfaces(
        this WebApplication app, ItemSurfaceTuning surfaceTuning, SocketTuning socketTuning,
        Func<string, CardInsertLookup?>? lookupInsert = null,
        Func<string, CardBaseType?>? lookupBaseType = null,
        Func<string, int?>? socketMaxFor = null)
    {
        if (surfaceTuning is null) throw new ArgumentNullException(nameof(surfaceTuning));
        if (socketTuning is null) throw new ArgumentNullException(nameof(socketTuning));

        // GG-17 / GG-44 — which of the four designed states each of the six surfaces is in, and what
        // unlocks the locked ones. Derived from the player's own state, never from a constant list.
        app.MapGet("/api/items/surfaces/{playerId}", (string playerId, RpgStore store) =>
        {
            var owned = store.ListItemsByPlayer(playerId);
            var satisfied = new HashSet<string>(StringComparer.Ordinal);
            if (owned.Count > 0)
            {
                satisfied.Add("first-container-acquired");
                // ⚠ The honest condition is "a second candidate for a role you have filled", and the
                // role half needs module 6's base-type table. The key is the weaker, TRUE one.
                if (owned.Count > 1) satisfied.Add("second-container-owned");
                if (owned.Any(i => store.GetSockets(i.InstanceId).Count > 0))
                    satisfied.Add("first-socketed-item");
            }

            return Results.Ok(SurfaceCatalog.All
                .Select(s => SurfaceCatalog.Resolve(s, surfaceTuning, satisfied, loading: false, errored: false,
                    rowCount: s == ItemSurface.Compendium ? 1 : owned.Count))
                .Select(st => new SurfaceStatusDto(SurfaceCatalog.Id(st.Surface), st.State.ToString(), st.UnlockKey))
                .ToList());
        });

        // The armoury page. The inbox count is over the WHOLE armoury, never the page — an inbox you
        // can empty by paging past it is not an inbox.
        app.MapGet("/api/items/armoury/{playerId}", (string playerId, RpgStore store, int? limit, string? after,
            string? role, string? frame) =>
        {
            var ordinals = store.ListRarities().ToDictionary(r => r.RarityId, r => r.Ordinal, StringComparer.Ordinal);
            var owned = store.ListItemsByPlayer(playerId);

            // ⛔ `Assigned` was hard-coded `false` on both records until 2026-09-06 (defect R4) —
            // harmless while nothing could assign an item, and simply wrong from the day module 4's
            // equip route shipped. It made the loot filter's `hideAssigned` and the armoury's
            // `assigned` sort inert, both of which were already built and reading this field.
            //
            // Module 2's own read, reused rather than re-derived, and its `ref_kind` default is now
            // the assignment table's `rolled` (defect R2's other half): a `stock` row is the relic
            // wire's catalog id and never pins one of THESE instances. One dictionary for the whole
            // page, so the join does not re-run per row.
            var assigned = store.FindAssignmentHolders(
                owned.Select(i => i.InstanceId).ToList(), EquipRefKinds.Rolled);

            var entries = owned.Select(item =>
            {
                var instance = store.GetInstance(item.InstanceId);
                var container = instance is null ? null : store.GetContainer(instance.ContainerId);
                var rarity = container?.Rarity ?? "";
                var isAssigned = assigned.ContainsKey(item.InstanceId);
                // ⛔ `Role`/`Frame` were hard-coded `""` on both records below until 2026-09-23, and
                // the two rows that recorded it blamed a missing `item_base_type` table (`P1.4`'s
                // "the role is typed, not derived" and `P5.4`'s "an armoury row's role and frame come
                // back empty"). Both halves of that blocker have since dissolved: the table exists and
                // is seeded at boot (`RpgStore.BaseTypes.cs`, `ImportBaseTypes` in `Program.cs`), and
                // the fact is carried PER INSTANCE by `rpg_item_generation`, which records `role` and
                // `frame` at mint (`RpgStore.Loot.cs:652`). Read from the generation row rather than
                // joined through the base type: it is the same two strings, it is what the mint
                // actually stamped, and it needs no second lookup.
                var generation = store.GetItemGeneration(item.InstanceId);
                var role = generation?.Role ?? "";
                var frame = generation?.Frame ?? "";
                return (
                    Row: new ArmouryRowDto(
                        item.InstanceId, instance?.ContainerId ?? "", rarity,
                        rarity.Length > 0 && ordinals.TryGetValue(rarity, out var ord) ? ord : 0,
                        isAssigned, item.Locked, Unseen: !item.Seen, item.Stale, item.AcquiredUtc,
                        // §9.14's compact-line tag. No container means no grants to read, so the
                        // honest answer is false rather than a lookup on an empty id.
                        BattleOnly: instance is not null && store.GrantsBattleOnlyAction(instance.ContainerId),
                        // item-naming T3. `""` when the corpus has no row for this container — the
                        // client says "unnamed" rather than falling back to the id, because a
                        // container id in a name slot is the defect this task exists to remove.
                        ContainerName: instance is null
                            ? ""
                            : lookupBaseType?.Invoke(instance.ContainerId)?.Name ?? "",
                        OriginKind: item.OriginKind,
                        Role: role, Frame: frame),
                    Entry: new ArmouryEntry(
                        item.InstanceId, instance?.ContainerId ?? "", Role: role, Frame: frame,
                        rarity.Length > 0 && ordinals.TryGetValue(rarity, out var o2) ? o2 : 0,
                        item.AcquiredUtc, isAssigned, item.Locked, Unseen: !item.Seen, item.Stale,
                        RollQualityMilli: 0));
            }).ToList();

            var inbox = LootFilterView.Inbox(entries.Select(e => e.Entry).ToList(), surfaceTuning);
            var strategy = CollectionStrategy.For(entries.Count, surfaceTuning);

            var sorted = ArmouryQuery.ApplySort(entries.Select(e => e.Entry), ArmourySortKey.Acquired).ToList();
            // I13 §5.9's role/frame narrowing, which Core has always supported
            // (`ArmouryQuery.ApplyFilter`, `:94-95`) and no route ever offered — so the filter was
            // reachable only from a test. Applied AFTER the sort and BEFORE the page, so an absent
            // parameter is byte-identical to the shipped behaviour (both null imposes no constraint)
            // and paging still sees the narrowed list rather than narrowing a page.
            if (role is not null || frame is not null)
                sorted = ArmouryQuery.ApplyFilter(sorted, new ArmouryFilter(Role: role, Frame: frame)).ToList();
            var page = ArmouryQuery.ApplyPage(sorted, new ArmouryPageRequest(limit ?? 50, after));
            var byId = entries.ToDictionary(e => e.Entry.InstanceId, e => e.Row, StringComparer.Ordinal);

            return Results.Ok(new ArmouryPageDto(
                sorted.Count, inbox.Unseen, inbox.OverReviewPressure, strategy.ToString(),
                page.Items.Select(i => byId[i.InstanceId]).ToList()));
        });

        // The socket bench's preview and the compendium's four states for one item — the ONE
        // combination read, so a preview and a result can never come from two functions.
        app.MapGet("/api/items/{instanceId}/combinations", (string instanceId, RpgStore store, string? playerId) =>
        {
            var instance = store.GetInstance(instanceId);
            if (instance is null) return Results.NotFound(new { error = "unknown instance", instanceId });

            var slots = store.GetSockets(instanceId);
            var catalog = store.GetComboRecipes();

            // strain-splice-host SSH1.2 (host-gate F5): the real role/frame/set-piece flag, off the
            // one host builder every production caller now shares -- this route previously hard-coded
            // ArmamentPrimary, an empty frame and never-a-set-piece, which made a helm preview a
            // weapon's own Strains/Splices and never applied D21's set-piece exclusion.
            // ⛔ SSH1.3 (host-gate §2), corrected 2026-09-23: this used to pass `_ => null` for
            // `socketMaxFor` and say SSH1.3 would wire it "once SocketHost.Capacity exists to read
            // it". It exists and `CombinationDistance.Reachable` reads it five times, so the stub made
            // every capacity test pass and showed combinations this chassis can never hold as
            // reachable. The real lookup is threaded in from the boot-time corpus now.
            if (store.SocketHostFor(instanceId, socketMaxFor ?? (_ => null)) is not { } host)
                return Results.NotFound(new { error = "instance has no resolvable socket host", instanceId });

            // Only filled sockets reach the evaluator; an empty one is room, not an ingredient.
            var fill = slots
                .Where(s => !s.IsEmpty)
                .Select(s => new SocketFill(s.Index, s.Affinity, InsertOf(lookupInsert, s.InsertContainerId)))
                .ToList();

            var rows = CombinationDistance.Evaluate(host, fill, catalog, socketTuning, surfaceTuning, out _);

            // The held ledger is what the player has EVER held; stock is the honest approximation the
            // shipped schema supports today (there is no ever-held ledger table — named in module 20's
            // build log as owed to inventory, not invented here).
            var held = playerId is { Length: > 0 }
                ? HeldLedger.From(store.ListStock(playerId)
                    .Where(s => s.ContainerId.StartsWith("gem.", StringComparison.Ordinal))
                    .Select(s => InsertOf(lookupInsert, s.ContainerId)))
                : HeldLedger.Empty;

            var rendered = CompendiumReveal.Render(rows, catalog, held, socketTuning, surfaceTuning);

            return Results.Ok(rendered.Select(r => new CombinationRowDto(
                r.ComboId, ComboShapes.Id(r.Shape), r.State.ToString(), r.Distance,
                r.Missing.Select(m => m.FamilyId).ToList(), r.MissingElements, r.GrantedTier)).ToList());
        });
    }

    /// <summary>
    /// One insert, described the way module 16's evaluator needs it. <b>The element comes from the
    /// gem corpus, never from a constant</b> — see the <c>lookupInsert</c> parameter's note.
    ///
    /// <para>The fallback shape is the one this route used unconditionally before the corpus was
    /// wired: family = the container id (there is no family to know without the corpus) and
    /// <c>Element: ""</c>, which is the honest "no element" and is also what a genuinely
    /// element-free insert authors. Tier stays <see cref="GemInsertCorpus.UnauthoredInsertTier"/>
    /// here because a lookup MISS means the container is not in the corpus at all — corpus hits
    /// resolve through <c>UniqueBudget.TierOfPowerBand</c> (species-gear-chain T8), and this constant
    /// is now that miss path's identity, nothing else's.</para>
    /// </summary>
    static InsertDef InsertOf(Func<string, CardInsertLookup?>? lookupInsert, string? containerId)
    {
        var id = containerId ?? "";
        return lookupInsert?.Invoke(id)?.Def
               ?? new InsertDef(id, id, "", GemInsertCorpus.UnauthoredInsertTier);
    }
}
