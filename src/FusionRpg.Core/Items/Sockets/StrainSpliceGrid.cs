using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// Item module 21 (<c>strain-splice-gen</c>) — the closed grid of D20's 102 combinations, DERIVED.
///
/// <code>
/// combo.strain-{aptitude}-{archetype}     combo.strain-might-offense      36 = 12 x 3
/// combo.splice-{aptitudeA}-{aptitudeB}    combo.splice-might-agility      66 = C(12,2)
/// </code>
///
/// <para>⭐ <b>The grid is what makes 102 affordable.</b> Twelve aptitudes and three archetypes
/// produce 36; twelve alone produce 66. Nobody authors 102 rows, and nobody transcribes twelve
/// aptitude ids either — the aptitude axis is <see cref="AptitudeCatalog.All"/>, whose own count is
/// <c>PostureCount × PerPosture</c>, so a thirteenth aptitude grows this grid by construction.</para>
///
/// <para>⭐ <b>A Splice pair is sorted by <see cref="AptitudeRow.Ordinal"/> at MINT time</b>, which is
/// what makes it unordered by construction. A uniqueness check would only discover
/// <c>(Might, Agility)</c> and <c>(Agility, Might)</c> after both had been generated — 66 rows late,
/// and one of them a wasted model call. The same rule the seedsmith generator applies in
/// <c>combogen/emit.py</c>; a test asserts the two ports mint identical id sets.</para>
///
/// <para>⚠ <b>The archetype axis is INJECTED, never declared here.</b> It lives in
/// <c>gk-data/packs/fusion/data/seed/items/_registry/build-themes.v1.json</c> — module 13's derived (aptitude, archetype)
/// registry — and <c>combo.strain-might-offense</c> is the same grid cell as <c>build.might-offense</c>.
/// Re-declaring the three values in Core would be the place the two silently drift apart, and Core
/// reads no file (tunables-ssot.md §7.2), so the host passes them in.</para>
///
/// <para>⛔ <b>No aptitude → element mapping exists here or anywhere.</b> D22-as-amended keys the
/// attuned bonus on each ingredient gem's own element matching its socket's affinity (RULED
/// 2026-09-04), so a Strain's aptitude never has to become an element. Nothing in this file names
/// an element type, an element id, or the element roster — and a test asserts that by scanning this
/// source rather than by trusting this sentence.</para>
/// </summary>
public static class StrainSpliceGrid
{
    public const string StrainPrefix = "combo.strain-";
    public const string SplicePrefix = "combo.splice-";

    /// <summary>One grid cell: a Strain (one aptitude + one archetype) or a Splice (two aptitudes).</summary>
    public readonly record struct Cell(
        ComboShape Shape, string ComboId, IReadOnlyList<string> Aptitudes, string Archetype);

    /// <summary>The id token an aptitude contributes — its id, lower-cased and kebab-legal.</summary>
    public static string Token(AptitudeRow row) => row.Id.ToLowerInvariant();

    public static string StrainId(AptitudeRow aptitude, string archetype)
    {
        if (string.IsNullOrWhiteSpace(archetype))
            throw new ArgumentException("a Strain names exactly one archetype", nameof(archetype));
        return StrainPrefix + Token(aptitude) + "-" + archetype.ToLowerInvariant();
    }

    /// <summary>
    /// The Splice id, with the pair sorted by ordinal so the two argument orders mint one id.
    /// </summary>
    public static string SpliceId(AptitudeRow a, AptitudeRow b)
    {
        if (a.Ordinal == b.Ordinal)
            throw new ArgumentException(
                $"a Splice joins two different aptitudes; both sides are '{a.Id}'", nameof(b));
        var (lo, hi) = a.Ordinal < b.Ordinal ? (a, b) : (b, a);
        return SplicePrefix + Token(lo) + "-" + Token(hi);
    }

    /// <summary>The 36 Strain cells, in aptitude-ordinal then archetype order.</summary>
    public static IReadOnlyList<Cell> Strains(IReadOnlyList<string> archetypes)
    {
        RequireArchetypes(archetypes);
        var cells = new List<Cell>(AptitudeCatalog.Count * archetypes.Count);
        foreach (var aptitude in AptitudeCatalog.All.OrderBy(a => a.Ordinal))
            foreach (var archetype in archetypes)
                cells.Add(new Cell(ComboShape.Strain, StrainId(aptitude, archetype),
                    new[] { aptitude.Id }, archetype));
        return cells;
    }

    /// <summary>The C(12,2) = 66 Splice cells, each unordered pair exactly once.</summary>
    public static IReadOnlyList<Cell> Splices()
    {
        var ordered = AptitudeCatalog.All.OrderBy(a => a.Ordinal).ToList();
        var cells = new List<Cell>(ordered.Count * (ordered.Count - 1) / 2);
        for (var i = 0; i < ordered.Count; i++)
            for (var j = i + 1; j < ordered.Count; j++)
                cells.Add(new Cell(ComboShape.Splice, SpliceId(ordered[i], ordered[j]),
                    new[] { ordered[i].Id, ordered[j].Id }, ""));
        return cells;
    }

    public static IReadOnlyList<Cell> All(IReadOnlyList<string> archetypes) =>
        Strains(archetypes).Concat(Splices()).ToList();

    /// <summary>
    /// The closed set of legal Strain/Splice combination ids. What an authored corpus is checked
    /// AGAINST — a generated row naming an id outside this set is content that no grid cell asked
    /// for, which is how a hand-edit survives a regeneration.
    /// </summary>
    public static IReadOnlySet<string> AllIds(IReadOnlyList<string> archetypes) =>
        All(archetypes).Select(c => c.ComboId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The expected population size, computed rather than written: <c>12 × |archetypes| + C(12,2)</c>.
    /// </summary>
    public static int ExpectedCount(IReadOnlyList<string> archetypes)
    {
        RequireArchetypes(archetypes);
        var n = AptitudeCatalog.Count;
        return n * archetypes.Count + n * (n - 1) / 2;
    }

    /// <summary>
    /// Every reason an authored Strain/Splice recipe may not be seeded, as one rejection each.
    /// Returns ALL of them: an author fixing a generated draft should see the whole refusal, and a
    /// report naming one of four problems produces four round trips (module 13's own reasoning).
    /// </summary>
    /// <param name="recipe">The authored recipe. Only Strain and Splice shapes are checked — a
    /// generated resonance is <see cref="ResonanceGenerator"/>'s and is not on this grid.</param>
    /// <param name="strainSpliceTuning">Optional. When supplied, the recipe's <c>BaseTier</c> is
    /// checked against the tunable one — an authored tier is a magnitude the model was never
    /// allowed to choose, so a row carrying its own is content that escaped P1.</param>
    public static IReadOnlyList<AtomRejection> ValidateRecipe(
        ComboRecipe recipe, SocketTuning socketTuning, IReadOnlyList<string> archetypes,
        StrainSpliceTuning? strainSpliceTuning = null)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        if (socketTuning is null) throw new ArgumentNullException(nameof(socketTuning));

        var problems = new List<AtomRejection>();
        if (!ComboShapes.IsStrainOrSplice(recipe.Shape))
            return problems;

        var legal = AllIds(archetypes);
        if (!legal.Contains(recipe.ComboId))
            problems.Add(StrainSpliceRules.Violated(StrainSpliceRules.NotOnTheGrid,
                $"'{recipe.ComboId}' is not a cell of the {ExpectedCount(archetypes)}-combination " +
                $"grid; the ids are derived from AptitudeCatalog.All and the archetype registry, " +
                $"so an id outside it is content no cell asked for"));

        var wanted = socketTuning.StrainSpliceIngredientCount;
        var supplied = recipe.Ingredients?.Sum(i => i.Quantity) ?? 0;
        if (supplied != wanted)
            problems.Add(StrainSpliceRules.Violated(StrainSpliceRules.IngredientCount,
                $"'{recipe.ComboId}' takes {supplied} ingredients; D20 as amended fixes a Strain " +
                $"or a Splice at exactly {wanted}"));

        if (recipe.MinSockets != wanted)
            problems.Add(StrainSpliceRules.Violated(StrainSpliceRules.MinSocketsDerived,
                $"'{recipe.ComboId}' declares minSockets {recipe.MinSockets}; a {wanted}-ingredient " +
                $"recipe DERIVES {wanted} and the value is never authored"));

        if (recipe.HostRole.Length > 0)
        {
            var hosts = SocketGeometry.RolesThatCanHostAStrain(socketTuning)
                .Select(ItemRoles.Id).ToHashSet(StringComparer.Ordinal);
            if (!hosts.Contains(recipe.HostRole))
                problems.Add(StrainSpliceRules.Violated(StrainSpliceRules.HostCannotHold,
                    $"'{recipe.ComboId}' is hosted on '{recipe.HostRole}', whose socket ceiling " +
                    $"cannot reach {wanted} inserts; no item of that role could ever fire it. " +
                    $"The roles that can are [{string.Join(", ", hosts.OrderBy(h => h, StringComparer.Ordinal))}]"));
        }

        // ⛔ The base-tier rule that used to live here is DELETED (SSH7.5, spec-tier-ladder §3).
        // `CombinationCorpus` fills `ComboRecipe.BaseTier` from the tuning, so a corpus row can no
        // longer declare one and there is nothing left for a check to compare — keeping it would be a
        // rule that can never fire, which reads as a guarantee while checking nothing.

        return problems;
    }

    static void RequireArchetypes(IReadOnlyList<string> archetypes)
    {
        if (archetypes is null || archetypes.Count == 0)
            throw new ArgumentException(
                "the archetype axis comes from data/seed/items/_registry/build-themes.v1.json and " +
                "must be supplied; Core reads no file and declaring the three values here would be " +
                "a second source of truth for module 13's grid", nameof(archetypes));
        if (archetypes.Distinct(StringComparer.Ordinal).Count() != archetypes.Count)
            throw new ArgumentException(
                $"the archetype axis repeats a value ([{string.Join(", ", archetypes)}]); a repeat " +
                "would mint the same Strain id twice", nameof(archetypes));
    }
}

/// <summary>
/// Module 21's content-rule namespace. ⛔ <b>No new <see cref="AtomRejectionReason"/> is minted</b> —
/// that enum is closed at 35 by its own declaration ("a caller that wants a new rule registers a
/// namespace, it never mints a 35th code"), so every refusal below is
/// <c>ContentRuleViolated{strainsplice.*}</c>. Registered separately from <c>socket</c> because
/// these are rules about AUTHORED grid content, not about a socket operation a player performed.
/// </summary>
public static class StrainSpliceRules
{
    public const string Namespace = "strainsplice";

    /// <summary>An authored combination id that is not a cell of the derived grid.</summary>
    public const string NotOnTheGrid = "strainsplice.not-on-the-grid";

    /// <summary>D20 as amended (§2f.2): exactly four ingredients, counted as a multiset.</summary>
    public const string IngredientCount = "strainsplice.ingredient-count";

    /// <summary><c>min_sockets</c> is derived from the ingredient count, never authored.</summary>
    public const string MinSocketsDerived = "strainsplice.min-sockets-derived";

    /// <summary>A host role whose socket ceiling cannot reach the ingredient count.</summary>
    public const string HostCannotHold = "strainsplice.host-cannot-hold";

    // ⛔ `BaseTierNotTunable` lived here until SSH7.5 and is DELETED, not kept as dead code:
    // `CombinationCorpus` fills `ComboRecipe.BaseTier` from the tuning, so no corpus row can declare a
    // tier and a rule comparing one can never fire.

    /// <summary>A corpus row that cannot become a <c>ComboRecipe</c> at all — a blank id, an
    /// unknown shape, no ingredients array, a duplicate id. Structural, and deliberately distinct
    /// from the five content rules <c>ValidateRecipe</c> applies to a well-formed recipe: this one
    /// fires in <c>CombinationCorpus.ToRecipes</c>, before there is a recipe to validate.</summary>
    public const string MalformedEntry = "strainsplice.malformed-entry";

    static StrainSpliceRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Force the static registration — the idiom every other item lane uses.</summary>
    public static void EnsureRegistered() => System.Runtime.CompilerServices.RuntimeHelpers
        .RunClassConstructor(typeof(StrainSpliceRules).TypeHandle);

    public static AtomRejection Violated(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}
