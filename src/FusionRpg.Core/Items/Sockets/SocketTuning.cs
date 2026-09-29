using System.Text.Json;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Sockets;

public sealed class SocketTuningRejection : Exception
{
    public SocketTuningRejection(string message) : base(message) { }
}

/// <summary>One rung's inclusive socket-grant window (spec-sockets.md §5).</summary>
public readonly record struct SocketGrantWindow(int Min, int Max);

/// <summary>What removing an insert of a given tier costs the player (ssot-sockets.md §4.7).</summary>
public enum SocketRemovalOutcome
{
    /// <summary>t1–t2. The learning tier: early play should never punish not-knowing.</summary>
    Free = 0,

    /// <summary>t3. The insert survives and a material is spent (module 14's terms).</summary>
    Costed,

    /// <summary>t4+. The insert is destroyed. <b>The item always survives.</b></summary>
    DestroysInsert,
}

/// <summary>
/// Structural limits. Exempt from AGENTS.md's no-hard-ceilings rule, and required to say why.
/// </summary>
public static class SocketLimits
{
    /// <summary>
    /// The most sockets any role may declare. <b>STRUCTURAL, not a progression ceiling</b>: it is a
    /// LEGIBILITY limit on one item's recipe shape — a four-ingredient recipe is memorable, a
    /// six-ingredient one is a wiki lookup. It bounds nothing that grows. Every power axis this layer
    /// touches stays open: <c>insertTiers.count</c> is a soft content axis, a combination's granted
    /// tier is unbounded above, and magnitude growth rides <c>contentScale</c>, which the socket layer
    /// never reads. A tuning row above it <b>throws at load</b>; it is never clamped, because a clamp
    /// turns "this role quietly lost a socket" into a bug with no symptom.
    /// </summary>
    public const int SocketMaxCeiling = 8;

    /// <summary>
    /// The number of sockets one circuit holds. <b>STRUCTURAL, not a progression ceiling</b> and not
    /// tunable: it decides whether a recipe is READABLE, not how the game feels. A Strain consumes
    /// exactly one complete circuit, so this is also the recipe width — raising it would change what
    /// a Strain IS, never how strong it is. The evaluator groups a fill by
    /// <c>socketIndex / SocketCircuitSize</c>, so a host with two circuits can carry two independent
    /// combinations (circuit-topology §2-§3, R13's per-circuit identity rule).
    /// </summary>
    public const int SocketCircuitSize = 4;
}

/// <summary>
/// Pure parser over the current `sockets` revision (<see cref="SocketTuningFiles.Current"/>) — no file
/// I/O (tunables-ssot.md §7.2: "Core
/// never reads a file. Hosts load and inject"), matching <see cref="Mutation.EnhancementTuning"/> and
/// <see cref="Materials.MaterialTuning"/>.
///
/// <para><b>No key has a default.</b> A missing section throws at load rather than resolving to a
/// silently-invented socket count, price or shape.</para>
///
/// <para><b>Structural invariants are checked at parse time</b>, each with its own message: the
/// fifteen ceiling rows against the role registry (and <c>standard</c>'s deliberate absence), every
/// ceiling against <see cref="SocketLimits.SocketMaxCeiling"/>, the rarity windows for
/// well-formedness, monotonicity and OD4 overlap, the resonance ring against the concrete element
/// roster, the removal thresholds against the tier ladder, and D20's ingredient count against the
/// structural ceiling. A table that would author combinations nothing can ever fire fails at boot
/// rather than at the first socketed item.</para>
/// </summary>
/// <summary>
/// The closed item-kind vocabulary <c>socketAllowanceByKind</c> may name
/// (species-gear-chain T4, spec-socket-allowance-by-kind). A fifth kind (void, …) is a reviewed
/// tuning + code change together: the table refuses an unknown kind at load, and
/// <see cref="SocketTuning.ShiftedWindow"/> refuses an unknown kind at call, so neither side can
/// silently grant the wrong allowance. `unique` and `boss` are two rows with identical values
/// today — different acquisition routes that will diverge.
/// </summary>
public static class SocketKinds
{
    public const string Ordinary = "ordinary";
    public const string Set = "set";
    public const string Unique = "unique";
    public const string Boss = "boss";

    public static readonly IReadOnlyList<string> All = new[] { Ordinary, Set, Unique, Boss };
}

public sealed class SocketTuning
{
    SocketTuning(
        IReadOnlyDictionary<ItemRole, int> socketCeiling,
        int structuralCeiling,
        IReadOnlyDictionary<string, SocketGrantWindow> rarityGrant,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, SocketGrantWindow>> allowanceByKind,
        IReadOnlyList<string> structuralBoundNotes,
        int insertTierCount,
        int upcycleInputPerOutput,
        int removalFreeThroughTier,
        int removalCostedThroughTier,
        IReadOnlyList<int> pureThresholds,
        IReadOnlyList<int> diversityThresholds,
        IReadOnlyList<ElementTypeId> ringOrder,
        ElementTypeId eclipseA,
        ElementTypeId eclipseB,
        int attunedEffectiveCountBonus,
        int attunedTierBonus,
        int strainSpliceIngredientCount,
        long? comboPricingMaxRatioToRarityRouteMilli,
        ComboPricingMeasuredAgainst? comboPricingMeasuredAgainst)
    {
        SocketCeiling = socketCeiling;
        StructuralCeiling = structuralCeiling;
        RarityGrant = rarityGrant;
        AllowanceByKind = allowanceByKind;
        StructuralBoundNotes = structuralBoundNotes;
        InsertTierCount = insertTierCount;
        UpcycleInputPerOutput = upcycleInputPerOutput;
        RemovalFreeThroughTier = removalFreeThroughTier;
        RemovalCostedThroughTier = removalCostedThroughTier;
        PureThresholds = pureThresholds;
        DiversityThresholds = diversityThresholds;
        RingOrder = ringOrder;
        EclipseA = eclipseA;
        EclipseB = eclipseB;
        AttunedEffectiveCountBonus = attunedEffectiveCountBonus;
        AttunedTierBonus = attunedTierBonus;
        StrainSpliceIngredientCount = strainSpliceIngredientCount;
        ComboPricingMaxRatioToRarityRouteMilli = comboPricingMaxRatioToRarityRouteMilli;
        ComboPricingMeasuredAgainst = comboPricingMeasuredAgainst;
    }

    /// <summary>The fifteen in-scope roles' ceilings. <c>Standard</c> is absent, never zero (D14).</summary>
    public IReadOnlyDictionary<ItemRole, int> SocketCeiling { get; }

    public int StructuralCeiling { get; }
    public IReadOnlyDictionary<string, SocketGrantWindow> RarityGrant { get; }

    /// <summary>The derived per-kind windows — <c>rarityGrant</c> shifted BEFORE the roll, the only
    /// position where "reduced" and "increased" are symmetric (spec-socket-allowance-by-kind).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SocketGrantWindow>> AllowanceByKind { get; }

    /// <summary>Every kind×rung cell whose derived window exceeds the structural ceiling. REPORTED,
    /// never hidden and never a load refusal: the grant resolves through the downstream clamps
    /// (base <c>socketMax</c>, role ceiling — both ≤ <c>StructuralCeiling</c>), so the inversion is
    /// bounded, visibly.</summary>
    public IReadOnlyList<string> StructuralBoundNotes { get; }

    /// <summary>The window a kind rolls from at a rung. Unknown kinds and unknown rungs throw —
    /// the parser refuses both at load, so reaching here means the caller invented one.</summary>
    public SocketGrantWindow ShiftedWindow(string kind, string rung)
    {
        if (!AllowanceByKind.TryGetValue(kind, out var table))
            throw new SocketTuningRejection(
                $"socket tuning: socketAllowanceByKind names no kind '{kind}' — the kind vocabulary is " +
                $"closed ({string.Join(", ", SocketKinds.All)}), and adding a member is a reviewed change");
        if (!table.TryGetValue(rung, out var window))
            throw new SocketTuningRejection(
                $"socket tuning: socketAllowanceByKind['{kind}'] has no window for rung '{rung}' — every rung " +
                "on the shipped ladder needs one, because a missing row would silently drop to zero sockets");
        return window;
    }
    public int InsertTierCount { get; }
    public int UpcycleInputPerOutput { get; }
    public int RemovalFreeThroughTier { get; }
    public int RemovalCostedThroughTier { get; }
    public IReadOnlyList<int> PureThresholds { get; }
    public IReadOnlyList<int> DiversityThresholds { get; }
    public IReadOnlyList<ElementTypeId> RingOrder { get; }
    public ElementTypeId EclipseA { get; }
    public ElementTypeId EclipseB { get; }

    /// <summary>Applies to <b>Pure only</b> — see the tuning file's own <c>resonance.note</c>.</summary>
    public int AttunedEffectiveCountBonus { get; }

    /// <summary>Applies to a Strain or Splice, which has no count to raise.</summary>
    public int AttunedTierBonus { get; }

    /// <summary>D20, fixed at 4. Not a free tunable — see the file's <c>strainSplice.note</c>.</summary>
    public int StrainSpliceIngredientCount { get; }

    /// <summary>
    /// The optional <c>comboPricing</c> section (SSH6.6, spec-combo-budget §4) — the ONE section this
    /// file may omit. <c>null</c> until a measurement has passed and been published, which is today's
    /// state: a one-rung ladder has nothing to bind against, so nothing is refused yet
    /// (<see cref="ComboPricingProvenance.Check"/>).
    /// </summary>
    public long? ComboPricingMaxRatioToRarityRouteMilli { get; }

    /// <summary>What the published pricing was measured against, or <c>null</c> when it carries no
    /// provenance. Its versions are FILENAME revisions, never this file's internal <c>version</c>
    /// field.</summary>
    public ComboPricingMeasuredAgainst? ComboPricingMeasuredAgainst { get; }

    /// <summary>The role's ceiling, or <c>0</c> for a role with no row (i.e. <c>standard</c>).</summary>
    public int CeilingFor(ItemRole role) => SocketCeiling.TryGetValue(role, out var c) ? c : 0;

    /// <summary>ssot-sockets.md §4.7, as thresholds so extending the tier ladder needs no edit here.</summary>
    public SocketRemovalOutcome RemovalFor(int insertTier) =>
        insertTier <= RemovalFreeThroughTier ? SocketRemovalOutcome.Free
        : insertTier <= RemovalCostedThroughTier ? SocketRemovalOutcome.Costed
        : SocketRemovalOutcome.DestroysInsert;

    public static SocketTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SocketTuningRejection("socket tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new SocketTuningRejection($"socket tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new SocketTuningRejection("socket tuning: root is not an object");

            var structuralCeiling = Positive(root, "structuralCeiling");
            if (structuralCeiling != SocketLimits.SocketMaxCeiling)
                throw new SocketTuningRejection(
                    $"socket tuning: structuralCeiling={structuralCeiling} disagrees with SocketLimits.SocketMaxCeiling=" +
                    $"{SocketLimits.SocketMaxCeiling} — the ceiling is structural and mirrored in code, so the two move " +
                    "together in one reviewed change or the file and the runtime disagree silently");

            var ceilings = ReadCeilings(root, structuralCeiling);
            var grants = ReadGrants(root, structuralCeiling);
            var (allowanceByKind, structuralBoundNotes) = ReadAllowance(root, grants, structuralCeiling);

            var tiers = Section(root, "insertTiers");
            var insertTierCount = Positive(tiers, "count");
            var upcycle = Positive(tiers, "upcycleInputPerOutput");
            if (upcycle < 2)
                throw new SocketTuningRejection(
                    $"socket tuning: insertTiers.upcycleInputPerOutput={upcycle} does not drain — a ratio below 2 " +
                    "manufactures tiers instead of consuming them, which is the inventory failure ssot-sockets.md §8.4 names");

            var removal = Section(root, "removal");
            var free = NonNegative(removal, "freeThroughTier");
            var costed = NonNegative(removal, "costedThroughTier");
            if (costed < free)
                throw new SocketTuningRejection(
                    $"socket tuning: removal.costedThroughTier={costed} is below freeThroughTier={free} — " +
                    "removal must get harsher with tier, never cheaper");
            if (costed >= insertTierCount)
                throw new SocketTuningRejection(
                    $"socket tuning: removal.costedThroughTier={costed} covers the whole {insertTierCount}-tier ladder, " +
                    "so no tier is ever a commitment — ssot-sockets.md §4.7's t4–t5 arm would be unreachable");

            var res = Section(root, "resonance");
            var pure = IntList(res, "pureThresholds");
            var diversity = IntList(res, "diversityThresholds");
            Ascending(pure, "resonance.pureThresholds");
            Ascending(diversity, "resonance.diversityThresholds");
            foreach (var k in pure.Concat(diversity))
                if (k < 2 || k > SocketLimits.SocketCircuitSize)
                    throw new SocketTuningRejection(
                        $"socket tuning: resonance threshold {k} is outside [2, {SocketLimits.SocketCircuitSize}] — a " +
                        "threshold above one circuit's width names a shape no single circuit could ever hold " +
                        "(circuit-topology §2; the limit is the CIRCUIT, not the structural ceiling)");

            var ring = ElementList(res, "ringOrder");
            if (ring.Count < 3)
                throw new SocketTuningRejection(
                    $"socket tuning: resonance.ringOrder has {ring.Count} elements — a ring needs at least three, or " +
                    "'adjacent' collapses to 'the other one' and Ring duplicates Eclipse");
            if (ring.Distinct().Count() != ring.Count)
                throw new SocketTuningRejection("socket tuning: resonance.ringOrder repeats an element");

            var eclipse = ElementList(res, "eclipsePair");
            if (eclipse.Count != 2 || eclipse[0] == eclipse[1])
                throw new SocketTuningRejection(
                    "socket tuning: resonance.eclipsePair must name exactly two distinct concrete elements");

            var strain = Section(root, "strainSplice");
            var ingredientCount = Positive(strain, "ingredientCount");
            // circuit-topology §2: a Strain consumes exactly one complete circuit, so the ingredient
            // count IS the circuit width. A tuning that disagrees would author a recipe that spans
            // two circuits (unmatchable) or leaves a circuit partly used (an identity with no width).
            if (ingredientCount != SocketLimits.SocketCircuitSize)
                throw new SocketTuningRejection(
                    $"socket tuning: strainSplice.ingredientCount={ingredientCount} is not " +
                    $"{SocketLimits.SocketCircuitSize} (SocketLimits.SocketCircuitSize) — a Strain consumes " +
                    "exactly one complete circuit, so the two are the same number by construction");

            var (comboPricingMaxRatio, comboPricingMeasuredAgainst) = ReadComboPricing(root);

            return new SocketTuning(
                ceilings, structuralCeiling, grants,
                allowanceByKind, structuralBoundNotes,
                insertTierCount, upcycle, free, costed,
                pure, diversity, ring, eclipse[0], eclipse[1],
                NonNegative(res, "attunedEffectiveCountBonus"),
                NonNegative(res, "attunedTierBonus"),
                ingredientCount,
                comboPricingMaxRatio, comboPricingMeasuredAgainst);
        }
    }

    static IReadOnlyDictionary<ItemRole, int> ReadCeilings(JsonElement root, int structuralCeiling)
    {
        var el = Section(root, "socketCeiling");
        var result = new Dictionary<ItemRole, int>();

        foreach (var prop in el.EnumerateObject())
        {
            if (!ItemRoles.TryParse(prop.Name, out var role))
                throw new SocketTuningRejection($"socket tuning: socketCeiling names unknown role '{prop.Name}'");
            if (role == ItemRole.Standard)
                throw new SocketTuningRejection(
                    "socket tuning: socketCeiling carries a row for 'standard' — D14 puts the commander slot out of " +
                    "item scope entirely, and a row (even a zero one) reads as 'in scope, allowed no sockets'");
            if (prop.Value.ValueKind != JsonValueKind.Number)
                throw new SocketTuningRejection($"socket tuning: socketCeiling['{prop.Name}'] is not a number");

            var ceiling = prop.Value.GetInt32();
            if (ceiling < 0 || ceiling > structuralCeiling)
                throw new SocketTuningRejection(
                    $"socket tuning: socketCeiling['{prop.Name}']={ceiling} is outside [0, {structuralCeiling}] — the " +
                    "structural ceiling THROWS rather than clamping (AGENTS.md), because a clamped role silently loses " +
                    "a socket nobody can see");
            result[role] = ceiling;
        }

        foreach (ItemRole role in Enum.GetValues(typeof(ItemRole)))
        {
            if (role == ItemRole.Standard) continue;
            if (!result.ContainsKey(role))
                throw new SocketTuningRejection(
                    $"socket tuning: socketCeiling has no row for in-scope role '{ItemRoles.Id(role)}' — an absent row " +
                    "is not a zero, it is an undefined ceiling, and spec-sockets.md §3 re-issued the table precisely " +
                    "because the stale twelve-id version left ward-array, infusion and retinue unassigned");
        }

        return result;
    }

    static IReadOnlyDictionary<string, SocketGrantWindow> ReadGrants(JsonElement root, int structuralCeiling)
    {
        var el = Section(root, "rarityGrant");
        var result = new Dictionary<string, SocketGrantWindow>(StringComparer.Ordinal);

        foreach (var rung in RarityLadder.RungIds)
        {
            if (!el.TryGetProperty(rung, out var row) || row.ValueKind != JsonValueKind.Object)
                throw new SocketTuningRejection(
                    $"socket tuning: rarityGrant has no window for rung '{rung}' — every rung on the shipped ladder " +
                    "needs one, because a missing row would silently drop to zero sockets at that rung");

            var min = NonNegative(row, "socketMin");
            var max = NonNegative(row, "socketMax");
            if (min > max)
                throw new SocketTuningRejection($"socket tuning: rarityGrant['{rung}'] has socketMin {min} > socketMax {max}");
            if (max > structuralCeiling)
                throw new SocketTuningRejection(
                    $"socket tuning: rarityGrant['{rung}'].socketMax={max} exceeds the structural ceiling {structuralCeiling}");
            result[rung] = new SocketGrantWindow(min, max);
        }

        foreach (var name in el.EnumerateObject().Select(p => p.Name))
            if (!RarityLadder.RungIds.Contains(name, StringComparer.Ordinal))
                throw new SocketTuningRejection($"socket tuning: rarityGrant names unknown rung '{name}'");

        // OD4's overlap principle on this axis (ssot-sockets.md §4.1): adjacent windows must overlap,
        // or socket count becomes a strict ladder and §8.1's "the only stat that matters" failure
        // returns. Monotonicity is checked alongside it — a higher rung must never grant fewer.
        for (var i = 1; i < RarityLadder.RungIds.Count; i++)
        {
            var lowId = RarityLadder.RungIds[i - 1];
            var highId = RarityLadder.RungIds[i];
            var low = result[lowId];
            var high = result[highId];

            if (high.Min < low.Min || high.Max < low.Max)
                throw new SocketTuningRejection(
                    $"socket tuning: rarityGrant is not monotonic — '{highId}' [{high.Min}..{high.Max}] grants less " +
                    $"than '{lowId}' [{low.Min}..{low.Max}]");

            if (high.Min > low.Max)
                throw new SocketTuningRejection(
                    $"socket tuning: rarityGrant['{lowId}'] [{low.Min}..{low.Max}] and ['{highId}'] " +
                    $"[{high.Min}..{high.Max}] do not overlap — OD4's overlap principle (ssot-sockets.md §4.1) is what " +
                    "stops socket count being a strict ladder, and a gap here re-opens §8.1 at full strength");
        }

        return result;
    }

    /// <summary>
    /// species-gear-chain T4 (spec-socket-allowance-by-kind): the per-kind shift table. Signed
    /// per-rung deltas (OQ1), shifting BOTH min and max (OQ2) — a scalar per kind cannot load,
    /// because chaff/sprout sit at {0,0} and any −1 there is a Negative rejection, not a quieter
    /// reduction. Every derived per-kind table re-passes the authored table's own four checks —
    /// inverted, negative, monotonic, non-overlapping — each naming the kind and rung, because a
    /// per-kind shift can break OD4 even when every individual window is valid. A derived max above
    /// the structural ceiling is a REPORT (StructuralBoundNotes), never a refusal: the grant still
    /// resolves through the downstream clamps, bounded and visible.
    /// </summary>
    static (IReadOnlyDictionary<string, IReadOnlyDictionary<string, SocketGrantWindow>> Tables,
            IReadOnlyList<string> Notes)
        ReadAllowance(JsonElement root, IReadOnlyDictionary<string, SocketGrantWindow> grants, int structuralCeiling)
    {
        var el = Section(root, "socketAllowanceByKind");
        var tables = new Dictionary<string, IReadOnlyDictionary<string, SocketGrantWindow>>(StringComparer.Ordinal);
        var notes = new List<string>();

        foreach (var kindProp in el.EnumerateObject())
        {
            if (!SocketKinds.All.Contains(kindProp.Name, StringComparer.Ordinal))
                throw new SocketTuningRejection(
                    $"socket tuning: socketAllowanceByKind names unknown kind '{kindProp.Name}' — the kind " +
                    $"vocabulary is closed ({string.Join(", ", SocketKinds.All)})");
            if (kindProp.Value.ValueKind != JsonValueKind.Object)
                throw new SocketTuningRejection(
                    $"socket tuning: socketAllowanceByKind['{kindProp.Name}'] is not an object");
        }

        foreach (var kind in SocketKinds.All)
        {
            if (!el.TryGetProperty(kind, out var rows) || rows.ValueKind != JsonValueKind.Object)
                throw new SocketTuningRejection(
                    $"socket tuning: socketAllowanceByKind has no row for kind '{kind}' — an absent kind is " +
                    "not a zero shift, it is an undefined allowance");

            var table = new Dictionary<string, SocketGrantWindow>(StringComparer.Ordinal);
            foreach (var rung in RarityLadder.RungIds)
            {
                if (!rows.TryGetProperty(rung, out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Number)
                    throw new SocketTuningRejection(
                        $"socket tuning: socketAllowanceByKind['{kind}'] has no delta for rung '{rung}'");
                var delta = deltaEl.GetInt32();
                var shiftedMin = grants[rung].Min + delta;
                var shiftedMax = grants[rung].Max + delta;

                if (shiftedMin < 0 || shiftedMax < 0)
                    throw new SocketTuningRejection(
                        $"socket tuning: socketAllowanceByKind['{kind}'] shifts rung '{rung}' to " +
                        $"[{shiftedMin}..{shiftedMax}], a negative window — a reduction may narrow a rung's " +
                        "window but may never drive it below zero");
                if (shiftedMax < shiftedMin)
                    throw new SocketTuningRejection(
                        $"socket tuning: socketAllowanceByKind['{kind}'] shifts rung '{rung}' to " +
                        $"[{shiftedMin}..{shiftedMax}], an inverted window — a reduction may narrow a rung's " +
                        "window but may never drive its maximum below its minimum");
                if (shiftedMax > structuralCeiling)
                    notes.Add(
                        $"socket tuning: socketAllowanceByKind['{kind}'] shifts rung '{rung}' to " +
                        $"[{shiftedMin}..{shiftedMax}], above the structural ceiling {structuralCeiling} — the " +
                        "grant resolves through the downstream clamps (base socketMax, role ceiling), so this " +
                        "cell is bounded and reported here, never silently swallowed");
                table[rung] = new SocketGrantWindow(shiftedMin, shiftedMax);
            }

            foreach (var name in rows.EnumerateObject().Select(p => p.Name))
                if (!RarityLadder.RungIds.Contains(name, StringComparer.Ordinal))
                    throw new SocketTuningRejection(
                        $"socket tuning: socketAllowanceByKind['{kind}'] names unknown rung '{name}'");

            CheckLadder(table, $"socketAllowanceByKind['{kind}']");
            tables[kind] = table;
        }

        return (tables, notes);
    }

    /// <summary>The authored table's own monotonicity + OD4 overlap checks, shared by the derived
    /// per-kind tables — the same invariant, the same message shape, the table named.</summary>
    static void CheckLadder(IReadOnlyDictionary<string, SocketGrantWindow> table, string label)
    {
        for (var i = 1; i < RarityLadder.RungIds.Count; i++)
        {
            var lowId = RarityLadder.RungIds[i - 1];
            var highId = RarityLadder.RungIds[i];
            var low = table[lowId];
            var high = table[highId];

            if (high.Min < low.Min || high.Max < low.Max)
                throw new SocketTuningRejection(
                    $"socket tuning: {label} is not monotonic — '{highId}' [{high.Min}..{high.Max}] grants less " +
                    $"than '{lowId}' [{low.Min}..{low.Max}]");

            if (high.Min > low.Max)
                throw new SocketTuningRejection(
                    $"socket tuning: {label}['{lowId}'] [{low.Min}..{low.Max}] and ['{highId}'] " +
                    $"[{high.Min}..{high.Max}] do not overlap — OD4's overlap principle (ssot-sockets.md §4.1) is what " +
                    "stops socket count being a strict ladder, and a gap here re-opens §8.1 at full strength");
        }
    }

    /// <summary>
    /// The optional <c>comboPricing</c> section (spec-combo-budget §4). A document without it is the
    /// normal state today: nothing has passed a measurement yet, and a one-rung ladder binds nothing.
    /// When the section IS present, <c>maxRatioToRarityRouteMilli</c> is required (a half-published
    /// section would leave the bound undefined), and <c>measuredAgainst</c> is validated field by field
    /// — its versions are FILENAME revisions and its digest is a SHA-256 hex string, both refused by
    /// name rather than parsed loosely.
    /// </summary>
    static (long? MaxRatioMilli, ComboPricingMeasuredAgainst? MeasuredAgainst) ReadComboPricing(JsonElement root)
    {
        if (!root.TryGetProperty("comboPricing", out var section) ||
            section.ValueKind == JsonValueKind.Null)
            return (null, null);
        if (section.ValueKind != JsonValueKind.Object)
            throw new SocketTuningRejection("socket tuning: comboPricing is not an object");

        var maxRatio = Positive(section, "maxRatioToRarityRouteMilli");

        if (!section.TryGetProperty("measuredAgainst", out var measured) ||
            measured.ValueKind == JsonValueKind.Null)
            return (maxRatio, null);
        if (measured.ValueKind != JsonValueKind.Object)
            throw new SocketTuningRejection("socket tuning: comboPricing.measuredAgainst is not an object");

        var digest = measured.TryGetProperty("combinationCorpusDigest", out var digestEl) &&
                     digestEl.ValueKind == JsonValueKind.String
            ? digestEl.GetString() ?? ""
            : "";
        if (digest.Length != 64 || !digest.All(ch => Uri.IsHexDigit(ch)))
            throw new SocketTuningRejection(
                $"socket tuning: comboPricing.measuredAgainst.combinationCorpusDigest is '{digest}' — a " +
                "SHA-256 hex digest of the accepted combination set is what a corpus change moves, and " +
                "anything else cannot be compared");

        return (maxRatio, new ComboPricingMeasuredAgainst(
            SocketsVersion: Positive(measured, "socketsVersion"),
            StrainSpliceVersion: Positive(measured, "strainSpliceVersion"),
            MaterialsVersion: Positive(measured, "materialsVersion"),
            CircuitSize: Positive(measured, "circuitSize"),
            CombinationCorpusDigest: digest.ToLowerInvariant()));
    }

    static JsonElement Section(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new SocketTuningRejection($"socket tuning: missing or non-object section '{key}'");
        return el;
    }

    static int Positive(JsonElement parent, string key)
    {
        var v = ReadInt(parent, key);
        if (v <= 0) throw new SocketTuningRejection($"socket tuning: '{key}' must be positive, got {v}");
        return v;
    }

    static int NonNegative(JsonElement parent, string key)
    {
        var v = ReadInt(parent, key);
        if (v < 0) throw new SocketTuningRejection($"socket tuning: '{key}' must be >= 0, got {v}");
        return v;
    }

    static int ReadInt(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new SocketTuningRejection($"socket tuning: missing or non-numeric '{key}'");
        return el.GetInt32();
    }

    static IReadOnlyList<int> IntList(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new SocketTuningRejection($"socket tuning: missing or non-array '{key}'");
        var result = el.EnumerateArray().Select(e =>
            e.ValueKind == JsonValueKind.Number
                ? e.GetInt32()
                : throw new SocketTuningRejection($"socket tuning: '{key}' holds a non-numeric entry")).ToList();
        if (result.Count == 0) throw new SocketTuningRejection($"socket tuning: '{key}' is empty");
        return result;
    }

    static IReadOnlyList<ElementTypeId> ElementList(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new SocketTuningRejection($"socket tuning: missing or non-array '{key}'");

        var result = new List<ElementTypeId>();
        foreach (var e in el.EnumerateArray())
        {
            var name = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (name is null || !ElementRoster.TryParse(name, out var id))
                throw new SocketTuningRejection(
                    $"socket tuning: '{key}' names '{name}', which is not a CONCRETE element — 'omni' in particular is " +
                    "not an affinity and never a resonance member (element-hub-ssot.md §4)");
            result.Add(id);
        }

        return result;
    }

    static void Ascending(IReadOnlyList<int> values, string label)
    {
        for (var i = 1; i < values.Count; i++)
            if (values[i] <= values[i - 1])
                throw new SocketTuningRejection($"socket tuning: '{label}' is not strictly ascending at index {i}");
    }
}
