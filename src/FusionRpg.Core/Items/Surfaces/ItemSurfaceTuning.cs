using System.Text.Json;

namespace FusionRpg.Core.Items.Surfaces;

public sealed class ItemSurfaceTuningRejection : Exception
{
    public ItemSurfaceTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser over <c>gk-core/data/tuning/item-surfaces.v1.json</c> — no file I/O (tunables-ssot.md §7.2:
/// "Core never reads a file. Hosts load and inject"), matching <see cref="Sockets.SocketTuning"/>,
/// <see cref="Mutation.EnhancementTuning"/> and <see cref="Materials.MaterialTuning"/>.
///
/// <para><b>No key has a default.</b> A missing section throws at load rather than resolving to a
/// silently-invented render threshold — a surface that quietly decides to draw two thousand rows is a
/// frame-budget bug with no symptom.</para>
///
/// <para>⛔ <b>Nothing here is a progression ceiling and the file says so per section.</b> These are
/// presentation thresholds: how many rows a surface draws before it changes strategy, and how much of
/// a 127-row catalog it shows. D26 puts drop volume, bag capacity and pacing out of this program's
/// scope, so this parser refuses a table that would turn a display bound into a refusal — see
/// <see cref="KnownInactiveRowCap"/>'s own note.</para>
/// </summary>
public sealed class ItemSurfaceTuning
{
    ItemSurfaceTuning(
        int renderAllThrough,
        int virtualizeThrough,
        int oneAwayDistance,
        int knownInactiveRowCap,
        int defaultHideBelowRarityOrdinal,
        int reviewPressurePerContentEvent,
        int inflowWatchPerContentEvent,
        IReadOnlyDictionary<string, string> surfaceUnlocks)
    {
        RenderAllThrough = renderAllThrough;
        VirtualizeThrough = virtualizeThrough;
        OneAwayDistance = oneAwayDistance;
        KnownInactiveRowCap = knownInactiveRowCap;
        DefaultHideBelowRarityOrdinal = defaultHideBelowRarityOrdinal;
        ReviewPressurePerContentEvent = reviewPressurePerContentEvent;
        InflowWatchPerContentEvent = inflowWatchPerContentEvent;
        SurfaceUnlocks = surfaceUnlocks;
    }

    /// <summary>GG-50: at or below this many rows the armoury draws every one of them.</summary>
    public int RenderAllThrough { get; }

    /// <summary>GG-50: above <see cref="RenderAllThrough"/> and at or below this, the list virtualizes;
    /// above it, the surface goes search-first.</summary>
    public int VirtualizeThrough { get; }

    /// <summary>ssot-presentation.md §4.3's boundary between <c>one-away</c> and <c>known-inactive</c>.</summary>
    public int OneAwayDistance { get; }

    /// <summary>
    /// A bound on the NAME-ONLY tail of the compendium. ⛔ Active and one-away rows are never dropped
    /// by it — <see cref="CompendiumReveal"/> applies it only to <c>known-inactive</c> — so it can
    /// never hide a combination the player is about to earn. A display bound on one list, not a limit
    /// on how many combinations may exist or fire.
    /// </summary>
    public int KnownInactiveRowCap { get; }

    /// <summary>The rung the "salvage everything below X" button starts at. <b>0 means hide
    /// nothing.</b> ssot-generation.md:859-862 says "no loot filter on day one", and the starting rung
    /// is one of this module's named ask-first questions — a non-zero default would answer it.</summary>
    public int DefaultHideBelowRarityOrdinal { get; }

    /// <summary>ssot-inventory.md: ~60 reviewed per content event before players stop reading. A
    /// number a surface may WARN on; it refuses nothing.</summary>
    public int ReviewPressurePerContentEvent { get; }

    /// <summary>ssot-generation.md's 40 tripwire, re-axised per content event per spec-item-surfaces.md
    /// (I12's wall-clock axis "imports a wall-clock axis the game does not have"). A watch number.</summary>
    public int InflowWatchPerContentEvent { get; }

    /// <summary>GG-44: surface id → the state key that unlocks it. Never a constant list.</summary>
    public IReadOnlyDictionary<string, string> SurfaceUnlocks { get; }

    public static ItemSurfaceTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ItemSurfaceTuningRejection("item-surfaces tuning is empty");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ItemSurfaceTuningRejection($"item-surfaces tuning is not valid JSON: {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ItemSurfaceTuningRejection("item-surfaces tuning root must be an object");

            var collection = Section(root, "collectionStrategy");
            var renderAll = Int(collection, "renderAllThrough", "collectionStrategy");
            var virtualize = Int(collection, "virtualizeThrough", "collectionStrategy");

            var compendium = Section(root, "compendium");
            var oneAway = Int(compendium, "oneAwayDistance", "compendium");
            var tailCap = Int(compendium, "knownInactiveRowCap", "compendium");

            var filter = Section(root, "lootFilter");
            var hideBelow = Int(filter, "defaultHideBelowRarityOrdinal", "lootFilter");
            var reviewPressure = Int(filter, "reviewPressurePerContentEvent", "lootFilter");
            var inflowWatch = Int(filter, "inflowWatchPerContentEvent", "lootFilter");

            var unlockSection = Section(root, "surfaceUnlocks");
            var unlocks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in unlockSection.EnumerateObject())
            {
                if (prop.Name.EndsWith("Note", StringComparison.Ordinal) || prop.Name == "note") continue;
                if (prop.Value.ValueKind != JsonValueKind.String || prop.Value.GetString() is not { Length: > 0 } key)
                    throw new ItemSurfaceTuningRejection($"surfaceUnlocks.{prop.Name} must be a non-empty state key");
                unlocks[prop.Name] = key;
            }

            // ── Structural invariants, each with its own message ──────────────────────────────
            if (renderAll <= 0)
                throw new ItemSurfaceTuningRejection("collectionStrategy.renderAllThrough must be positive");
            if (virtualize < renderAll)
                throw new ItemSurfaceTuningRejection(
                    $"collectionStrategy.virtualizeThrough ({virtualize}) is below renderAllThrough ({renderAll}) — " +
                    "the three GG-50 bands would not be ordered and 'virtualize' would name an empty range");
            if (oneAway < 1)
                throw new ItemSurfaceTuningRejection(
                    "compendium.oneAwayDistance must be at least 1 — at 0 the one-away state names the active set");
            if (tailCap < 0)
                throw new ItemSurfaceTuningRejection("compendium.knownInactiveRowCap must not be negative");
            if (hideBelow < 0)
                throw new ItemSurfaceTuningRejection("lootFilter.defaultHideBelowRarityOrdinal must not be negative");
            if (reviewPressure <= 0 || inflowWatch <= 0)
                throw new ItemSurfaceTuningRejection(
                    "lootFilter review/inflow watch numbers must be positive — a zero watch fires on every event");

            if (unlocks.Count == 0)
                throw new ItemSurfaceTuningRejection(
                    "surfaceUnlocks names no surface — GG-44 requires every surface to declare what unlocks it");

            foreach (var surface in SurfaceCatalog.Ids)
                if (!unlocks.ContainsKey(surface))
                    throw new ItemSurfaceTuningRejection(
                        $"surfaceUnlocks is missing '{surface}' — GG-17/GG-44: a surface with no declared unlock " +
                        "renders as present-but-dead, which is the state the rule exists to forbid");

            return new ItemSurfaceTuning(
                renderAll, virtualize, oneAway, tailCap, hideBelow, reviewPressure, inflowWatch, unlocks);
        }
    }

    static JsonElement Section(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object
            ? el
            : throw new ItemSurfaceTuningRejection($"item-surfaces tuning is missing the '{name}' object");

    static int Int(JsonElement section, string name, string sectionName) =>
        section.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : throw new ItemSurfaceTuningRejection($"{sectionName}.{name} is missing or not a number");
}
