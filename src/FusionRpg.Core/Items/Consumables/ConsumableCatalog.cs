using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Consumables;

/// <summary>
/// One line of the pre-dispatch draught manifest: a stack the player named, and how many of it.
/// </summary>
/// <param name="ContainerId">The consumable's container id.</param>
/// <param name="Qty">How many of the stack are spent. ≥ 1.</param>
public readonly record struct DraughtManifestEntry(string ContainerId, int Qty);

/// <summary>
/// What the belt allows. ⭐ <b>D37: there is no global carry limit.</b> The equipped <c>girdle</c> is
/// the limit — role 7 of the fifteen, already shipped, so no sixteenth role — and a better girdle
/// carries more. With no belt equipped the count is <see cref="ConsumableLimits.UnbeltedSlots"/>,
/// which is 0 and not a default.
///
/// <para>⏸ <b>A wiring gap, named with that word:</b> <c>consumableSlots</c> is a base-type property
/// module 6 has not authored yet — a fresh grep over <c>gk-data/packs/fusion/data/seed/items/base-types/</c> finds the key
/// nowhere. So the count arrives here as a parameter rather than being read from the equipped item,
/// and the day module 6 authors it the caller changes, not this type.</para>
/// </summary>
/// <param name="Slots">
/// The equipped girdle's <c>consumableSlots</c>, or <see cref="ConsumableLimits.UnbeltedSlots"/> when
/// nothing is equipped. ⛔ <b>No upper bound is applied here</b> — a belt with a hundred slots is a
/// content decision, and clamping it would be the hard progression ceiling AGENTS.md forbids. A
/// negative value throws, because a negative slot count is a bug in the caller, not a stingy belt.
/// </param>
public readonly record struct BeltCapacity(int Slots)
{
    public static BeltCapacity Unequipped => new(ConsumableLimits.UnbeltedSlots);

    public static BeltCapacity FromEquippedGirdle(int consumableSlots) =>
        consumableSlots < 0
            ? throw new ArgumentOutOfRangeException(nameof(consumableSlots),
                "a girdle's consumableSlots cannot be negative — with no belt equipped the count is " +
                "ConsumableLimits.UnbeltedSlots (0), which is a different thing from a negative one")
            : new BeltCapacity(consumableSlots);
}

/// <summary>
/// SC7's named consumer — <b>without it the <c>consumable_def</c> rows are <c>status.expose.*</c>:
/// registered, valid, hashed, and read by nobody.</b> Pure and store-free: the caller supplies the
/// rows it loaded.
///
/// <para>⛔ <b>NOTHING CALLS THIS TODAY — corrected 2026-09-23. The paragraph here used to read
/// "Called by the dispatch endpoint and by the squad builder", and neither does.</b> Read, not assumed:
/// <c>ConsumableCatalog.Load</c> is invoked only from
/// <c>gk-core/tests/FusionRpg.Core.Items.Tests/Items/DraughtManifestTests.cs</c>;
/// <c>RpgStore.UpsertConsumableDef</c> only from <c>RunDraughtStoreTests</c>;
/// <c>ListConsumableDefs</c>/<c>GetConsumableDef</c> have no caller at all; and <c>consumable_def</c>
/// is not in <c>ContentHashRegistry</c> — so the shipped corpus
/// (<c>gk-data/packs/fusion/data/seed/items/consumables/*.json</c>, six files) reaches no table and no surface in
/// production. See <c>ITEM-consumable-1</c> in tasks/item-todo.md for the whole chain and for what a
/// real caller would have to supply.</para>
/// </summary>
public sealed class ConsumableCatalog
{
    readonly Dictionary<string, ConsumableDefRow> _byId;

    ConsumableCatalog(Dictionary<string, ConsumableDefRow> byId) => _byId = byId;

    public IReadOnlyCollection<ConsumableDefRow> All => _byId.Values;

    public int Count => _byId.Count;

    /// <summary>
    /// Load and validate. <paramref name="orphanContainerIds"/> are <c>consumable</c> containers with
    /// no def row — §6.3's "an orphan container is not usable content", reported rather than ignored.
    /// </summary>
    public static ConsumableCatalogLoad Load(
        IReadOnlyList<ConsumableDefRow> defs,
        ConsumableTuning tuning,
        IReadOnlyList<string>? orphanContainerIds = null)
    {
        if (defs is null) throw new ArgumentNullException(nameof(defs));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var fails = new List<AtomRejection>();
        var byId = new Dictionary<string, ConsumableDefRow>(StringComparer.Ordinal);

        foreach (var def in defs.OrderBy(d => d.ContainerId, StringComparer.Ordinal))
        {
            if (!byId.TryAdd(def.ContainerId, def))
            {
                fails.Add(ConsumableRules.Fail(ConsumableRules.Orphan,
                    $"'{def.ContainerId}' has two consumable_def rows; the table is 1:1 on the container"));
                continue;
            }

            // A def row loaded here has no concrete container behind it yet (X7 + seed-to-concrete), so
            // the shape rules run and the container-kind binding does not. The moment a container
            // exists, ValidateDef is the entry point and it adds exactly one more refusal.
            fails.AddRange(ConsumableValidator.ValidateShape(
                def, Array.Empty<ConsumableCoreAtom>(), 0, 0, null, null, null, tuning));
        }

        foreach (var orphan in (orphanContainerIds ?? Array.Empty<string>()).OrderBy(s => s, StringComparer.Ordinal))
            fails.Add(ConsumableRules.Fail(ConsumableRules.Orphan,
                $"container '{orphan}' is a consumable with no consumable_def row — an orphan container " +
                "is not usable content (§6.3)"));

        return new ConsumableCatalogLoad(new ConsumableCatalog(byId), fails);
    }

    public ConsumableDefRow? Resolve(string containerId) =>
        containerId is not null && _byId.TryGetValue(containerId, out var row) ? row : null;

    /// <summary>
    /// The dispatch gate — §6.3's last row, and the mechanic's primary player-visible refusal.
    /// <b>Runs at dispatch, not after</b>: the manifest is an input to the sealed run, so there is no
    /// path around it.
    ///
    /// <para>Returns every refusal, so a player naming three bad lines is told about three.</para>
    /// </summary>
    public IReadOnlyList<AtomRejection> GateManifest(
        IReadOnlyList<DraughtManifestEntry> entries,
        BeltCapacity belt,
        UseContext context = UseContext.Dispatch)
    {
        entries ??= Array.Empty<DraughtManifestEntry>();
        var fails = new List<AtomRejection>();
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // long, and widened before multiplying: manifest_cost has no upper bound (a strong draught may
        // cost several places, §5.2) and neither does a belt's slot count, so the summed cost is a
        // magnitude and gets a magnitude's type. checked, so an absurd qty throws rather than wrapping
        // into a total that fits.
        long totalCost = 0;

        foreach (var entry in entries)
        {
            if (entry.Qty < 1)
                fails.Add(ConsumableRules.Fail(ConsumableRules.BadValue,
                    $"manifest entry '{entry.ContainerId}' has qty {entry.Qty}; a spent stack is at least one"));

            var def = Resolve(entry.ContainerId);
            if (def is null)
            {
                fails.Add(ConsumableRules.Fail(ConsumableRules.UnknownConsumable,
                    $"manifest names '{entry.ContainerId}', which the consumable catalog does not know"));
                continue;
            }

            if (!seen.Add(entry.ContainerId))
                fails.Add(ConsumableRules.Fail(ConsumableRules.FamilyConflict,
                    $"manifest names '{entry.ContainerId}' twice; one line per stack, with qty carrying " +
                    "the count"));

            if (!def.UseContexts.Contains(context))
                fails.Add(ConsumableRules.Fail(ConsumableRules.UseContextUnsupported,
                    $"'{entry.ContainerId}' is not usable at '{UseContexts.Wire(context)}' — it names " +
                    $"[{def.UseContextWire}]"));

            if (groups.TryGetValue(def.ExclusionGroup, out var firstHolder))
                fails.Add(ConsumableRules.Fail(ConsumableRules.FamilyConflict,
                    $"'{entry.ContainerId}' and '{firstHolder}' share exclusion group " +
                    $"'{def.ExclusionGroup}'; one per run (§4.4 defence 2 — the shipped pool-group rule, " +
                    "reused rather than reinvented)"));
            else
                groups[def.ExclusionGroup] = entry.ContainerId;

            if (entry.Qty >= 1)
                totalCost = checked(totalCost + (long)def.ManifestCost * entry.Qty);
        }

        if (totalCost > belt.Slots)
            fails.Add(ConsumableRules.Fail(ConsumableRules.LimitExceeded,
                $"the manifest costs {totalCost} belt places and the equipped girdle carries " +
                $"{belt.Slots}" +
                (belt.Slots == ConsumableLimits.UnbeltedSlots
                    ? " — no girdle is equipped, and an unequipped slot grants nothing (D37)"
                    : "")));

        return fails;
    }
}

/// <summary>The catalog plus every refusal its load produced — never an exception, so sixty rows
/// report sixty problems in one pass.</summary>
/// <param name="Catalog">Holds every row that parsed, including ones carrying refusals: a caller that
/// wants only clean rows filters on <paramref name="Rejections"/>, and a caller that wants to REPORT
/// needs the bad rows too.</param>
public readonly record struct ConsumableCatalogLoad(
    ConsumableCatalog Catalog,
    IReadOnlyList<AtomRejection> Rejections);
