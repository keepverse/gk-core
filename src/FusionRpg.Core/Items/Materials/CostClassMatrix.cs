namespace FusionRpg.Core.Items.Materials;

/// <summary>
/// The ten priced operations (`salvage-craft` §"Recipes as data" — I9 §6.1's seven-verb enum was
/// stale; the shipped vocabulary is ten). ⛔ <b>This is not a second `op_kind` namespace.</b>
/// `ssot-enhancement.md` §5.3 owns `op_kind`; this enum is the subset of it that has a <i>price</i>,
/// plus the three mints (`forge`/`upcycle`/`forge-gem`) that mutate nothing and so have no `op_kind`
/// at all. Adding a verb here is <b>code</b>, because a verb needs an executor and an owning module —
/// that is the SC7 line the spec draws.
/// </summary>
public enum CraftOperation
{
    /// <summary>Mint a base from nothing. Owned here (module 14).</summary>
    Forge = 0,

    /// <summary>Convert five of grade g into one of grade g+1, g ≤ 2. Owned here (module 14).</summary>
    Upcycle,

    /// <summary>Mint a gem at a rung and an element. Owned by module 16.</summary>
    ForgeGem,

    /// <summary>Open a socket. Owned by module 16.</summary>
    Bore,

    /// <summary>⭐ D24 — declare a crafted socket's element affinity. Prices on `bore`'s own curve.
    /// ⚠ Its `op_kind` (`socket-imbue`) is <b>module 15's to add</b>, not this module's — named here
    /// so it is not invented twice.</summary>
    Imbue,

    /// <summary>Put an insert you already own into an open socket. Owned by module 16.</summary>
    Socket,

    /// <summary>Promote an item's rarity rung. Owned by module 15.</summary>
    Elevate,

    /// <summary>+n → +n+1. Owned by module 15.</summary>
    Temper,

    /// <summary>Re-randomise one affix. Owned by module 15.</summary>
    RerollOne,

    /// <summary>Re-randomise every affix. Owned by module 15.</summary>
    RerollAll,

    /// <summary>
    /// species-gear-chain T23 — restore a worn item's <c>durability_current</c>. Owned by
    /// `deployment-hierarchy` module 7, pulled forward with its storage (T11). The eleventh priced
    /// verb, and the first whose attempt can destroy the item (D1) — which is exactly why it is a
    /// craft operation with a cost and a roll rather than a free reset.
    /// </summary>
    Repair,

    /// <summary>
    /// species-gear-chain T37 — the upgrade tree's priced verb, the twelfth member: it spends souls
    /// priced on the class rung (<c>operations.upgrade</c>) and consumes the input instance itself,
    /// which is not a MaterialClass and therefore rides the mutation rather than a cost line.
    /// Souls-only by design: an upgrade is not a material sink.
    /// </summary>
    Upgrade,
}

public static class CraftOperations
{
    /// <summary>Content ids, in enum order. Kebab-case, matching what a recipe row authors.</summary>
    public static string Id(CraftOperation op) => op switch
    {
        CraftOperation.Forge => "forge",
        CraftOperation.Upcycle => "upcycle",
        CraftOperation.ForgeGem => "forge-gem",
        CraftOperation.Bore => "bore",
        CraftOperation.Imbue => "imbue",
        CraftOperation.Socket => "socket",
        CraftOperation.Elevate => "elevate",
        CraftOperation.Temper => "temper",
        CraftOperation.RerollOne => "reroll-one",
        CraftOperation.RerollAll => "reroll-all",
        CraftOperation.Repair => "repair",
        CraftOperation.Upgrade => "upgrade",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    public static IReadOnlyList<CraftOperation> All { get; } =
        Enum.GetValues<CraftOperation>().OrderBy(o => (int)o).ToArray();

    public static IReadOnlyList<string> AllIds { get; } = All.Select(Id).ToArray();

    public static bool TryParse(string? value, out CraftOperation op)
    {
        var key = (value ?? "").Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(Id(candidate), key, StringComparison.Ordinal))
            {
                op = candidate;
                return true;
            }
        }

        op = default;
        return false;
    }
}

/// <summary>
/// Which spend classes each operation may name, and which catalyst it rides
/// (`salvage-craft` §"The three catalysts"). This is what makes the five-class vocabulary
/// <b>enforceable</b> rather than advisory: a recipe naming a class its operation cannot spend is
/// refused at import, not reviewed by eye.
/// </summary>
public static class CostClassMatrix
{
    /// <summary>
    /// The catalyst verb an operation rides, or null where the operation spends none.
    ///
    /// <para>`imbue` rides `forge` because imbuing declares what a hole <i>is</i> — the same act of
    /// bringing matter into existence that boring the hole was — and D24 prices it on `bore`'s curve
    /// anyway.</para>
    /// </summary>
    public static string? CatalystFor(CraftOperation op) => op switch
    {
        // make
        CraftOperation.Forge or CraftOperation.ForgeGem or CraftOperation.Bore or CraftOperation.Imbue
            => "catalyst.forge",

        // improve
        CraftOperation.Temper or CraftOperation.Elevate or CraftOperation.Repair => "catalyst.temper",

        // re-randomise
        CraftOperation.RerollOne or CraftOperation.RerollAll => "catalyst.flux",

        // `upcycle` converts substrate into substrate and `socket` moves a gem you already own —
        // neither brings anything into existence, so neither burns a catalyst. I9 §7.4 has an empty
        // catalyst cell for both, and that emptiness is a rule: socketing must never be a material
        // decision, and upcycle is a drain valve rather than a sink.
        CraftOperation.Upcycle or CraftOperation.Socket => null,

        // species-gear-chain T37: an upgrade consumes the input chassis and pays souls; it brings
        // nothing into existence and burns no catalyst, so its cell is empty for the same reason
        // `upcycle`/`socket`'s are — the emptiness is the rule.
        CraftOperation.Upgrade => null,

        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    /// <summary>True when <paramref name="op"/> may name a cost line of <paramref name="cls"/>.</summary>
    public static bool Allows(CraftOperation op, MaterialClass cls) => cls switch
    {
        // Every operation may charge the flat fee; `upcycle`'s souls leg is authored per recipe.
        MaterialClass.Souls => true,

        MaterialClass.Shard => op is CraftOperation.ForgeGem or CraftOperation.Elevate or CraftOperation.RerollAll
            or CraftOperation.Repair,

        MaterialClass.Substrate => op is CraftOperation.Forge or CraftOperation.Upcycle or CraftOperation.Bore
            or CraftOperation.Imbue or CraftOperation.Elevate or CraftOperation.Temper or CraftOperation.Repair,

        MaterialClass.Essence => op is CraftOperation.ForgeGem or CraftOperation.Imbue or CraftOperation.RerollOne,

        MaterialClass.Catalyst => CatalystFor(op) != null,

        // species-gear-chain T33: NARROW, not permissive. Only the verbs that IMPROVE a bound piece
        // may spend a trophy, and only above species-cost-shaping's threshold rung (T34b applies that
        // gate; this matrix only says WHICH verbs are eligible at all). A permissive arm here would
        // reopen the "every recipe is a travel itinerary" failure ssot-materials-crafting.md §3.4
        // already refused for zone-keyed ids.
        MaterialClass.Trophy => op is CraftOperation.Elevate or CraftOperation.Temper,

        // species-gear-chain T40/T43/T44: this arm says which VERBS may spend an Assurance line AT
        // ALL — `temper` (Enhance, T40); the verbs `spec-craft-risk-ladder.md` §2 names T24's own
        // ladder as serving, "promotion, temper, reroll and socket work" (`elevate`/Promote,
        // `reroll-one`+`reroll-all`/Reroll — one shared executor — and `bore`/`socket`/`imbue`, the
        // three socket verbs, which likewise share one executor, T43); and `repair` (T23's own D1/D2
        // destroy-and-restore verb, T44). It does NOT say which of the three assurance IDS a given
        // verb may spend: `assure` only ever makes sense where a success die is rolled (Enhance);
        // `protect` never applies to `repair` (R10 — the destroy chance is the sink, unconditionally,
        // and no assurance effect suppresses it) even though `repair` IS eligible here for its own
        // `repair` id. That finer, per-id gate is `ItemWorkbench.TryCoalesceAssuranceLines`'s own job
        // (the same two-layer shape `Trophy`'s arm already uses above: verb-eligible here,
        // narrower-still at the resolver).
        MaterialClass.Assurance => op is CraftOperation.Temper or CraftOperation.Elevate
            or CraftOperation.RerollOne or CraftOperation.RerollAll
            or CraftOperation.Bore or CraftOperation.Socket or CraftOperation.Imbue
            or CraftOperation.Repair,

        _ => throw new ArgumentOutOfRangeException(nameof(cls), cls, null),
    };

    /// <summary>
    /// The rule id a refusal carries. Namespaced under `material`, raised as the one
    /// <c>ContentRuleViolated</c> code — never a new member of the closed 33-code list.
    /// </summary>
    public const string CostClassForbiddenRule = "material.cost-class-forbidden";

    public const string CatalystMismatchRule = "material.catalyst-mismatch";

    /// <summary>
    /// Null when the line is legal, else the refusal detail. Two distinct rules, because they fail
    /// for different reasons and a fix for one is not a fix for the other: a class the operation may
    /// not spend at all, and the <i>right</i> class carrying the <i>wrong</i> catalyst (a `forge`
    /// recipe spending `catalyst.temper` — the spec's own named example).
    /// </summary>
    public static (string Rule, string Detail)? Check(CraftOperation op, string materialId)
    {
        var cls = MaterialCatalog.ClassOf(materialId);

        if (!Allows(op, cls))
            return (CostClassForbiddenRule,
                $"operation '{CraftOperations.Id(op)}' may not spend a {cls} line ('{materialId}')");

        if (cls == MaterialClass.Catalyst)
        {
            var expected = CatalystFor(op);
            if (!string.Equals(expected, materialId, StringComparison.Ordinal))
                return (CatalystMismatchRule,
                    $"operation '{CraftOperations.Id(op)}' rides '{expected}', not '{materialId}'");
        }

        return null;
    }
}
