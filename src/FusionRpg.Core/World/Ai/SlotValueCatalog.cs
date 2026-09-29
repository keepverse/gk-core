namespace FusionRpg.Core.World.Ai;

/// <summary>
/// What a slot is worth before anybody's needs are consulted (spec-ai-commander.md §ValueMap).
///
/// A catalog rather than a switch, for the reason every other catalog here exists: the mechanism
/// audit found this codebase growing parallel `switch (id)` blocks that drift apart. A slot kind
/// added without a value here is a startup error, not a silent zero.
///
/// The numbers are per-mille and deliberately coarse. They are a *ranking*, not an economy — the
/// economy arrives with `sector-development`, and when it does these become its opening estimates
/// rather than something to unpick.
/// </summary>
public static class SlotValueCatalog
{
    static IReadOnlyDictionary<SlotKind, int>? _byKind;

    public static IReadOnlyDictionary<SlotKind, int> ByKind => _byKind ??= Validate(Seed);

    static readonly IReadOnlyDictionary<SlotKind, int> Seed = new Dictionary<SlotKind, int>
    {
        // A Seat is what makes a sector an anchor rather than a holding: supply starts at one, and
        // an empire without one has no chain at all. Nothing else comes close.
        [SlotKind.Seat] = 1000,

        // The loam source (empire-economy-ssot.md §3): "settle from anywhere, rare, the prizes."
        // More foundational than the other producers below — without one nearby, no base survives
        // long enough to matter — but still short of a Seat itself.
        [SlotKind.Rootbed] = 750,

        // The producers. Even until `sector-development` gives them output, they are the reason to
        // take ground rather than merely pass through it.
        [SlotKind.EssenceDeposit] = 700,
        [SlotKind.ShardVein] = 700,
        [SlotKind.MaterialSeam] = 650,
        [SlotKind.Lair] = 600,
        [SlotKind.Tear] = 550,

        // Build sites: worth something because of what they could become.
        [SlotKind.Wildland] = 300,
        [SlotKind.Spire] = 450,

        // Fixtures you cannot build on but would rather hold than not.
        [SlotKind.Vault] = 500,
        [SlotKind.Shrine] = 400,
        [SlotKind.Market] = 450,
        [SlotKind.Anomaly] = 200,

        // Ground that has to be cleared before it is even ground. Zero, not negative: a hazard makes
        // a sector *less* attractive by taking up a slot that could have been something, and that is
        // already expressed by it scoring nothing rather than by it scoring against you.
        [SlotKind.Hazard] = 0
    };

    public static int Of(SlotKind kind) =>
        ByKind.TryGetValue(kind, out var value)
            ? value
            : throw new KeyNotFoundException($"No slot value for '{kind}'.");

    /// <summary>Catalog discipline — a kind with no value is a startup error, never a silent zero.</summary>
    public static IReadOnlyDictionary<SlotKind, int> Validate(IReadOnlyDictionary<SlotKind, int> values)
    {
        foreach (var kind in Enum.GetValues<SlotKind>())
        {
            if (!values.ContainsKey(kind))
                throw new InvalidOperationException($"Slot kind '{kind}' has no value in SlotValueCatalog.");
            if (values[kind] < 0)
                throw new InvalidOperationException($"Slot kind '{kind}' has a negative value; ground is worth nothing at worst.");
        }

        return values;
    }
}
