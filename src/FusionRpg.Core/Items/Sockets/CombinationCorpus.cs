using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// One authored <c>combination</c>-kind corpus row — the C# mirror of the field shape
/// <c>gk-forge/tools/ItemSeedValidator/Registries/KindCatalog.cs</c> defines and
/// <c>gk-forge/tools/seedsmith/seedsmith/adapters/items/combogen/emit.py</c> assembles.
///
/// <para>⚠ <b>Every field is nullable/zero-defaulted on purpose.</b> A corpus row that is missing a
/// field is not an exception here — it is a <see cref="CombinationCorpus.ToRecipes">refusal</see>,
/// because one bad row must not take boot down (the refusal-by-name pattern
/// <c>GemContainerBuild.TryBuildOne</c> already uses). <see cref="CombinationCorpus.Parse"/> reads
/// what the document carries and leaves the rest absent; the mapper decides what is unusable.</para>
/// </summary>
/// <param name="Aptitudes">Read for the entry's own shape, never to rebuild the id: the id already
/// encodes the grid cell, and re-deriving it would be a second source of truth for the grid.</param>
/// <remarks>
/// ⛔ <b>There is deliberately no <c>grantedTier</c> member here.</b> SSH7.5/7.6 (spec-tier-ladder §3)
/// moved the tier a shape grants to TUNING — <see cref="StrainSpliceTuning.BaseTierFor"/> plus the
/// ladder rung's own <see cref="StrainSpliceTuning.RungGrantDelta"/> and attunement's bonus — and the
/// re-emitted corpus carries no tier number at all. A member that modelled one would be a field with
/// no authored data and a default that cannot be told from a real value; the C# validator
/// (<c>gk-forge/tools/ItemSeedValidator/Registries/KindCatalog.cs</c>) is what refuses a row that still carries
/// one, so the mapper does not need to read it to reject it.
/// </remarks>
public sealed record CombinationEntry(
    string? Id,
    string? Shape,
    IReadOnlyList<string>? Aptitudes,
    string? Archetype,
    string? HostRole,
    string? HostFrame,
    int MinSockets,
    IReadOnlyList<ComboIngredient>? Ingredients);

/// <summary>
/// Item module 3 (<c>recipe-import</c>) — the pure mapper from the generated <c>combination</c>
/// corpus to <see cref="ComboRecipe"/>.
///
/// <para>⛔ <b>Core reads no file (tunables-ssot.md §7.2).</b> <see cref="Parse"/> takes the seed
/// document's own TEXT — the same "host reads, Core parses" split
/// <see cref="SocketTuning.Parse(string)"/> and <see cref="StrainSpliceTuning.Parse(string)"/> use —
/// and the host (SSH3.3, <c>Program.cs</c>) owns reading
/// <c>gk-data/packs/fusion/data/seed/items/combinations/*.json</c>.</para>
///
/// <para>⭐ <b>Mapping and validation are two stages, deliberately.</b> This file answers "can this
/// row become a recipe at all" (a blank id, an unknown shape, no ingredients, a duplicate id); the
/// five content rules — on the grid, four ingredients, derived <c>minSockets</c>, a host role that
/// can hold four, a tunable <c>baseTier</c> — stay in
/// <see cref="StrainSpliceGrid.ValidateRecipe"/> and are applied by the host before seeding. Fusing
/// them would make the mapper the second place those rules live.</para>
/// </summary>
public static class CombinationCorpus
{
    /// <summary>The corpus kind id every combination seed document declares.</summary>
    public const string Kind = "combination";

    /// <summary>
    /// The entries of one seed document, or an empty list for a document that is not a
    /// <c>combination</c> seed (a ledger or the still-blocked artefact lives in the same directory
    /// and is not corpus content). A document that DECLARES the kind but carries no <c>entries</c>
    /// array is refused rather than read as an empty corpus — an absent corpus and an unreadable one
    /// must stay distinguishable.
    /// </summary>
    public static IReadOnlyList<CombinationEntry> Parse(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<CombinationEntry>();
        if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            !string.Equals(kind.GetString(), Kind, StringComparison.Ordinal))
            return Array.Empty<CombinationEntry>();
        if (!root.TryGetProperty("entries", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                "a `combination` seed document carries no `entries` array — refusing to read it as " +
                "an empty corpus");

        var entries = new List<CombinationEntry>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray()) entries.Add(ReadEntry(row));
        return entries;
    }

    /// <summary>
    /// Maps every entry that can become a <see cref="ComboRecipe"/>, and refuses the rest by name.
    /// Every input lands in exactly one of the two lists — <c>recipes.Count + refusals.Count ==
    /// entries.Count</c> — so a malformed row is visible rather than silently absent.
    /// </summary>
    public static (IReadOnlyList<ComboRecipe> Recipes, IReadOnlyList<AtomRejection> Refusals)
        ToRecipes(IReadOnlyList<CombinationEntry> entries, StrainSpliceTuning tuning)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var recipes = new List<ComboRecipe>(entries.Count);
        var refusals = new List<AtomRejection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < entries.Count; i++)
        {
            var at = $"entry #{i + 1}";
            var entry = entries[i];
            if (entry is null) { refusals.Add(Malformed($"{at} is null")); continue; }
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                refusals.Add(Malformed($"{at} carries no id"));
                continue;
            }

            var who = $"'{entry.Id}'";
            if (!seen.Add(entry.Id))
            {
                refusals.Add(Malformed(
                    $"{who} repeats an id already read — two rows would mint one recipe, and the " +
                    $"second would silently overwrite the first"));
                continue;
            }

            if (!ComboShapes.TryParse(entry.Shape, out var shape) ||
                !ComboShapes.IsStrainOrSplice(shape))
            {
                refusals.Add(Malformed(
                    $"{who} carries shape '{entry.Shape}', which is neither a Strain nor a Splice; " +
                    $"the shipped shapes are [{ComboShapes.Id(ComboShape.Strain)}, " +
                    $"{ComboShapes.Id(ComboShape.Splice)}]"));
                continue;
            }

            if (!tuning.BaseTier.ContainsKey(ComboShapes.Id(shape)))
            {
                refusals.Add(Malformed(
                    $"{who} is a {ComboShapes.Id(shape)} and the strain-splice tuning " +
                    $"({SocketTuningFiles.StrainSplice}) " +
                    $"carries no recipe.baseTier row for that shape"));
                continue;
            }
            if (tuning.TierLadder.Count == 0)
            {
                refusals.Add(Malformed(
                    $"{who} cannot be imported: the strain-splice tuning carries no tierLadder, so " +
                    "there is no rung 1 for the matcher to read"));
                continue;
            }

            if (entry.Ingredients is null)
            {
                refusals.Add(Malformed($"{who} carries no ingredients array"));
                continue;
            }

            recipes.Add(new ComboRecipe(
                ComboId: entry.Id,
                Shape: shape,
                // A combination grants a FAMILY, resolved to an atom when it binds (module 4) — the
                // element and threshold axes belong to the resonances `ResonanceGenerator` mints.
                Element: "",
                Threshold: 0,
                HostRole: entry.HostRole ?? "",
                HostFrame: entry.HostFrame ?? "",
                MinSockets: entry.MinSockets,
                // SSH7.5 (spec-tier-ladder §3): the tier a shape grants is TUNING's, never the
                // corpus's — the corpus carries no tier number and this mapper models none.
                BaseTier: tuning.BaseTierFor(shape),
                // SSH7.8: an ingredient is (family, quantity) only — the LADDER decides which tier a
                // fill must reach. The real floors ride the RECIPE, below, so a pre-ladder caller's
                // rung 1 is byte-identical to the shipped one.
                Ingredients: entry.Ingredients.ToList(),
                BaseFloors: tuning.TierLadder[0].Floors));
        }

        return (recipes, refusals);
    }

    static AtomRejection Malformed(string detail) =>
        StrainSpliceRules.Violated(StrainSpliceRules.MalformedEntry, detail);

    static CombinationEntry ReadEntry(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return new CombinationEntry(null, null, null, null, null, null, 0, null);

        return new CombinationEntry(
            Id: Text(row, "id"),
            Shape: Text(row, "shape"),
            Aptitudes: Texts(row, "aptitudes"),
            Archetype: Text(row, "archetype"),
            HostRole: Text(row, "hostRole"),
            HostFrame: Text(row, "hostFrame"),
            MinSockets: Number(row, "minSockets"),
            Ingredients: IngredientRows(row));
    }

    static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static int Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : 0;

    /// <summary>
    /// SHA-256 over the ACCEPTED Strain/Splice set, in the four fields a combination's power and price
    /// are made of — ids, ingredient families (with quantity), grants, host pins — sorted, so
    /// the reading is order-independent and moves the moment any of them does.
    ///
    /// <para>⭐ <b>Why the corpus needs its own digest.</b> The corpus moves without any tuning file
    /// moving: an R11 re-run, an R13 per-id ruling, a grant repair. A new word is new power nobody
    /// priced, so <see cref="ComboPricingProvenance.Check"/> compares this digest against the one the
    /// measurement recorded and refuses by name when they differ.</para>
    ///
    /// <para>A recipe whose id the grants map does not carry digests its grants as empty rather than
    /// being skipped: an unreadable row must change the digest, never disappear from it.</para>
    /// </summary>
    public static string Digest(
        IReadOnlyList<ComboRecipe> recipes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> grants)
    {
        if (recipes is null) throw new ArgumentNullException(nameof(recipes));
        if (grants is null) throw new ArgumentNullException(nameof(grants));

        var lines = recipes
            .Select(recipe =>
            {
                var families = recipe.Ingredients
                    .Select(i => $"{i.FamilyId}x{i.Quantity}")
                    .OrderBy(f => f, StringComparer.Ordinal);
                var granted = grants.TryGetValue(recipe.ComboId, out var list)
                    ? list.OrderBy(g => g, StringComparer.Ordinal).ToList()
                    : new List<string>();
                return string.Join("\t", new[]
                {
                    recipe.ComboId,
                    string.Join(",", families),
                    string.Join(",", granted),
                    $"{recipe.HostRole}|{recipe.HostFrame}",
                });
            })
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    static IReadOnlyList<string>? Texts(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return list;
    }

    /// <summary>
    /// Every entry's <c>grants</c>, keyed by id — the field a combination's CONTAINER is built from
    /// and <see cref="ComboRecipe"/> deliberately does not carry (a recipe is a firing rule; its grants
    /// become rows in <c>effect_container</c> at boot, exactly as <c>ComboContainerBuild</c> mints
    /// them). A pure parse over the document's own text, like <see cref="Parse"/>, so the ids and the
    /// families cannot be read through two different schemas.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadGrants(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));

        var byId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return byId;
        if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            !string.Equals(kind.GetString(), Kind, StringComparison.Ordinal))
            return byId;
        if (!root.TryGetProperty("entries", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return byId;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var id = Text(row, "id");
            if (id is null) continue;
            byId[id] = Texts(row, "grants") ?? Array.Empty<string>();
        }
        return byId;
    }

    static IReadOnlyList<ComboIngredient>? IngredientRows(JsonElement row)
    {
        if (!row.TryGetProperty("ingredients", out var value) ||
            value.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<ComboIngredient>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var family = Text(item, "family");
            if (family is null) continue;
            // `quantity` is optional in the emitted shape and defaults to one — a recipe is an
            // unordered MULTISET (D41), so a row that omits it means "one", never "unknown".
            // SSH7.8: a row's `minTier`, if an older document still carries one, is UNREAD — the
            // ladder owns floors and `ComboIngredient` has no member to hold it.
            var quantity = item.TryGetProperty("quantity", out var q) &&
                           q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var n) ? n : 1;
            list.Add(new ComboIngredient(family, quantity));
        }
        return list;
    }
}
