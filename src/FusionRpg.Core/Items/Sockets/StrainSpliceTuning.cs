using System.Text.Json;
using FusionRpg.Core.Effects.Atoms.Generation;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// Pure parser over <c>gk-core/data/tuning/strain-splice.v1.json</c> (item module 21) — no file I/O
/// (tunables-ssot.md §7.2: "Core never reads a file. Hosts load and inject"), matching
/// <see cref="SocketTuning"/>, <see cref="Mutation.EnhancementTuning"/> and
/// <see cref="Materials.MaterialTuning"/>.
///
/// <para>⚠ <b>Two files, one domain, and the split is deliberate.</b> D20's ingredient count, the
/// per-actor backstop, the attuned tier bonus, the structural ceiling and the fifteen per-role
/// ceilings all live in the current `sockets` revision and belong to module 16. This file holds only
/// what
/// module 16 does not own. <see cref="Parse"/> takes the <see cref="SocketTuning"/> alongside the
/// JSON and CROSS-VALIDATES against it — a min-tier plan whose length disagrees with the ingredient
/// count is refused at load, because a generator and a matcher disagreeing about how many
/// ingredients a Strain takes is a defect nothing downstream can see.</para>
///
/// <para><b>No key has a default.</b> A missing section throws at load rather than resolving to a
/// silently-invented tier.</para>
/// </summary>
/// <summary>
/// One rung of the tier ladder (strain-splice-host SSH7.1, spec-tier-ladder §1). The floors are a
/// POSITIONAL list — the fill is consumed in ascending family id order, so "tier 2 somewhere" and
/// "tier 2 in the third slot" are different claims — and <see cref="GrantDelta"/> is what the rung
/// adds to the shape's own base tier.
/// </summary>
public sealed record TierLadderRung(int Rung, IReadOnlyList<int> Floors, int GrantDelta);

public sealed class StrainSpliceTuning
{
    StrainSpliceTuning(
        IReadOnlyList<int> minTierPlan,
        IReadOnlyList<TierLadderRung> tierLadder,
        IReadOnlyDictionary<string, int> baseTier,
        int catalogueSizeBar,
        int exactDuplicateNamesMax,
        int nearDuplicateRateMaxPermille)
    {
        MinTierPlan = minTierPlan;
        TierLadder = tierLadder;
        BaseTier = baseTier;
        CatalogueSizeBar = catalogueSizeBar;
        ExactDuplicateNamesMax = exactDuplicateNamesMax;
        NearDuplicateRateMaxPermille = nearDuplicateRateMaxPermille;
    }

    /// <summary>The insert tier each of D20's ingredients must meet, ascending — rung 1's own floors,
    /// kept as the field every pre-ladder caller already reads.</summary>
    public IReadOnlyList<int> MinTierPlan { get; }

    /// <summary>
    /// The tier ladder (spec-tier-ladder §1), ascending. Until the file publishes one it is the
    /// ONE-rung ladder <see cref="MinTierPlan"/> describes — rung 1, the flat floor, no grant delta —
    /// which is what makes a file with no ladder keep loading with its exact old behaviour.
    /// </summary>
    public IReadOnlyList<TierLadderRung> TierLadder { get; }

    /// <summary>Combination-shape id (<c>strain</c>/<c>splice</c>) → the tier granted BEFORE
    /// <see cref="SocketTuning.AttunedTierBonus"/>. Unbounded above: a granted tier is never
    /// clamped, and the structural socket ceiling caps a recipe's SHAPE, never its magnitude.</summary>
    public IReadOnlyDictionary<string, int> BaseTier { get; }

    /// <summary>ssot-sockets §4.4's ~45 learnable-catalogue bar. <b>Reported, never enforced</b> —
    /// a threshold that refused the 102nd combination would be a hard content ceiling.</summary>
    public int CatalogueSizeBar { get; }

    public int ExactDuplicateNamesMax { get; }
    public int NearDuplicateRateMaxPermille { get; }

    public int BaseTierFor(ComboShape shape)
    {
        var id = ComboShapes.Id(shape);
        return BaseTier.TryGetValue(id, out var tier)
            ? tier
            : throw new InvalidOperationException(
                $"strain-splice tuning has no recipe.baseTier row for '{id}'; the rows are " +
                $"[{string.Join(", ", BaseTier.Keys.OrderBy(k => k, StringComparer.Ordinal))}]");
    }

    /// <summary>
    /// The tier a combination grants. Base plus D22-as-amended's attuned bonus.
    /// <para>⚠ <b>Never a gate.</b> A mismatched fill still produces the combination — it just
    /// produces it at the base tier. §2f.2 reverted the hard requirement by name ("a fee wearing a
    /// gate's name"), so there is deliberately no arm here that returns "no combination".</para>
    /// </summary>
    public int GrantedTier(ComboShape shape, SocketTuning socketTuning, bool allAttuned)
        => GrantedTier(shape, rung: 1, socketTuning, allAttuned);

    /// <summary>
    /// The tier a combination grants at a LADDER RUNG (SSH7.2, spec-tier-ladder §2):
    /// <c>baseTier[shape] + ladder[rung].grantDelta + (allAttuned ? attunedTierBonus : 0)</c>.
    /// Attunement is a bonus ON TOP of the rung and never a gate (D22 as amended) — a fill that
    /// reaches the rung fires whether or not every insert is attuned. Rung 0 (no rung met) grants
    /// nothing and is refused by name rather than resolved to rung 1.
    /// </summary>
    public int GrantedTier(ComboShape shape, int rung, SocketTuning socketTuning, bool allAttuned)
    {
        if (socketTuning is null) throw new ArgumentNullException(nameof(socketTuning));
        return BaseTierFor(shape) + RungGrantDelta(rung) + (allAttuned ? socketTuning.AttunedTierBonus : 0);
    }

    /// <summary>The rung's own grant delta; rung 0 is refused (no rung was reached) and a rung the
    /// ladder does not carry is refused rather than clamped to the top.</summary>
    public int RungGrantDelta(int rung)
    {
        var found = TierLadder.FirstOrDefault(r => r.Rung == rung)
            ?? throw new InvalidOperationException(
                $"the strain-splice ladder carries no rung {rung} (it has " +
                $"{string.Join(", ", TierLadder.Select(r => r.Rung))}) — a rung outside it is a " +
                "defect, never rounded to the nearest one");
        return found.GrantDelta;
    }

    public static StrainSpliceTuning Parse(string json, SocketTuning socketTuning)
    {
        if (socketTuning is null) throw new ArgumentNullException(nameof(socketTuning));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var recipe = Section(root, "recipe");
        var plan = Section(recipe, "minTierPlan").EnumerateArray().Select(e => e.GetInt32()).ToList();

        var baseTier = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in Section(recipe, "baseTier").EnumerateObject())
            baseTier[row.Name] = row.Value.GetInt32();

        var tuning = new StrainSpliceTuning(
            plan,
            ReadTierLadder(recipe, plan),
            baseTier,
            Section(Section(root, "learnability"), "catalogueSizeBar").GetInt32(),
            Section(Section(root, "distinctness"), "exactDuplicateNamesMax").GetInt32(),
            Section(Section(root, "distinctness"), "nearDuplicateRateMaxPermille").GetInt32());

        Validate(tuning, socketTuning);
        return tuning;
    }

    /// <summary>
    /// The optional <c>recipe.tierLadder</c>. Absent — today's state — is NOT a substituted default:
    /// it is the one-rung ladder the SHIPPED <c>minTierPlan</c> describes, so a file that carries no
    /// ladder keeps loading with exactly the behaviour it has always had. A published ladder is
    /// validated by <see cref="ValidateTierLadder"/> like every other load rule.
    /// </summary>
    static IReadOnlyList<TierLadderRung> ReadTierLadder(JsonElement recipe, IReadOnlyList<int> plan)
    {
        if (!recipe.TryGetProperty("tierLadder", out var rows) || rows.ValueKind == JsonValueKind.Null)
            return new[] { new TierLadderRung(Rung: 1, Floors: plan, GrantDelta: 0) };

        if (rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("recipe.tierLadder is not an array");

        var ladder = new List<TierLadderRung>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
            ladder.Add(new TierLadderRung(
                Rung: Section(row, "rung").GetInt32(),
                Floors: Section(row, "floors").EnumerateArray().Select(e => e.GetInt32()).ToList(),
                GrantDelta: Section(row, "grantDelta").GetInt32()));
        return ladder;
    }

    /// <summary>
    /// The ladder's own load rules (spec-tier-ladder §1), every one a THROW and never a clamp: rungs
    /// that do not ascend, floors that fall within or across rungs, grant deltas that do not start at
    /// 0 and rise, or a top rung granting a tier the atom ladder does not carry, are tuning defects
    /// that would silently make a better-attuned fill worth nothing.
    /// </summary>
    static void ValidateTierLadder(StrainSpliceTuning t, SocketTuning sockets)
    {
        if (t.TierLadder.Count == 0)
            throw new InvalidOperationException("recipe.tierLadder is empty — a ladder needs a rung");

        var wanted = sockets.StrainSpliceIngredientCount;
        var first = t.TierLadder[0];
        if (first.Rung != 1 || first.GrantDelta != 0)
            throw new InvalidOperationException(
                $"recipe.tierLadder starts at rung {first.Rung} with grantDelta {first.GrantDelta}; " +
                "rung 1 IS the base tier, so it carries no grant delta and the ladder starts there");

        for (var i = 0; i < t.TierLadder.Count; i++)
        {
            var rung = t.TierLadder[i];
            if (rung.Rung != i + 1)
                throw new InvalidOperationException(
                    $"recipe.tierLadder rung {rung.Rung} sits at index {i} — the rungs are a closed " +
                    "ascending ladder 1..n, never renumbered or skipped");
            if (rung.Floors.Count != wanted)
                throw new InvalidOperationException(
                    $"recipe.tierLadder rung {rung.Rung} carries {rung.Floors.Count} floors but " +
                    $"the tuning fixes the ingredient count at {wanted} — the floors are zipped onto " +
                    "the ingredient multiset, so a length mismatch drops or invents one");

            for (var position = 0; position < rung.Floors.Count; position++)
            {
                var floor = rung.Floors[position];
                if (floor < 1 || floor > sockets.InsertTierCount)
                    throw new InvalidOperationException(
                        $"recipe.tierLadder rung {rung.Rung} position {position} names tier {floor}, " +
                        $"outside the shipped insert ladder [1..{sockets.InsertTierCount}]");
                if (position > 0 && floor < rung.Floors[position - 1])
                    throw new InvalidOperationException(
                        $"recipe.tierLadder rung {rung.Rung} floors are not ascending at position " +
                        $"{position} ({floor} below {rung.Floors[position - 1]})");
                if (i > 0 && floor < t.TierLadder[i - 1].Floors[position])
                    throw new InvalidOperationException(
                        $"recipe.tierLadder rung {rung.Rung} position {position} floor {floor} is " +
                        $"below rung {i} at {t.TierLadder[i - 1].Floors[position]} — a higher rung can " +
                        "never ask for less");
            }

            if (i > 0 && rung.GrantDelta <= t.TierLadder[i - 1].GrantDelta)
                throw new InvalidOperationException(
                    $"recipe.tierLadder rung {rung.Rung} grantDelta {rung.GrantDelta} does not rise " +
                    $"above rung {i} at {t.TierLadder[i - 1].GrantDelta} — a rung that grants no more " +
                    "than the one below it is not a rung");
        }

        // The top rung plus the shape's own base tier plus attunement must still land on an atom the
        // ladder carries: the same F7 bound the base tier is checked against, now read at the top.
        var topBase = t.BaseTier.Values.Max();
        var topGranted = topBase + t.TierLadder[^1].GrantDelta + sockets.AttunedTierBonus;
        if (topGranted > FamilyExpansion.TierCount)
            throw new InvalidOperationException(
                $"recipe.tierLadder's top grantDelta ({t.TierLadder[^1].GrantDelta}) plus the top " +
                $"base tier ({topBase}) plus attunement ({sockets.AttunedTierBonus}) grants tier " +
                $"{topGranted}, above the atom ladder's {FamilyExpansion.TierCount} — a combination " +
                "granted there would bind no atom. Extend the ladder or lower the rung; never clamp.");
    }

    static JsonElement Section(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidOperationException(
                $"strain-splice tuning is missing '{name}' — refusing to substitute a default; an " +
                $"unreviewed number here reaches every generated combination");

    static void Validate(StrainSpliceTuning t, SocketTuning sockets)
    {
        var wanted = sockets.StrainSpliceIngredientCount;
        if (t.MinTierPlan.Count != wanted)
            throw new InvalidOperationException(
                $"recipe.minTierPlan has {t.MinTierPlan.Count} entries but {SocketTuningFiles.Current} fixes " +
                $"the ingredient count at {wanted} (D20) — the plan is zipped onto the ingredients, " +
                $"so a length mismatch silently drops or invents a min tier");

        for (var i = 1; i < t.MinTierPlan.Count; i++)
            if (t.MinTierPlan[i] < t.MinTierPlan[i - 1])
                throw new InvalidOperationException(
                    $"recipe.minTierPlan [{string.Join(", ", t.MinTierPlan)}] is not ascending — it " +
                    $"is zipped onto the ingredient multiset sorted by family id, so the order " +
                    $"decides which duplicate gets the cheaper tier and is load-bearing");

        foreach (var tier in t.MinTierPlan)
            if (tier < 1 || tier > sockets.InsertTierCount)
                throw new InvalidOperationException(
                    $"recipe.minTierPlan names tier {tier}, outside the shipped insert ladder " +
                    $"[1..{sockets.InsertTierCount}] — an ingredient no insert can satisfy makes " +
                    $"the combination unbuildable");

        if (t.BaseTier.Count == 0)
            throw new InvalidOperationException(
                "recipe.baseTier is empty — every combination kind needs a tier");

        foreach (var (kind, tier) in t.BaseTier)
        {
            if (tier < 1)
                throw new InvalidOperationException(
                    $"recipe.baseTier.{kind} is {tier}; a granted tier below 1 grants nothing");
            if (!ComboShapes.TryParse(kind, out var shape) || !ComboShapes.IsStrainOrSplice(shape))
                throw new InvalidOperationException(
                    $"recipe.baseTier names '{kind}', which is not a Strain or a Splice — a " +
                    $"generated resonance's tier is ResonanceGenerator's and is not tunable here");
        }

        // F7 (strain-splice-host SSH4.3, spec-combo-bind §1): a granted tier above the atom ladder
        // binds no atom. The bound is STRUCTURAL (FamilyExpansion.TierCount), so it is enforced where
        // tuning loads — a THROW, never a clamp: clamping would make a better-attuned fill silently
        // stop mattering. Magnitude growth is untouched; it rides the ladder's own contentScale.
        ValidateTierLadder(t, sockets);

        var topBase = t.BaseTier.Values.Max();
        var topGranted = topBase + sockets.AttunedTierBonus;
        if (topGranted > FamilyExpansion.TierCount)
            throw new InvalidOperationException(
                $"recipe.baseTier's top rung ({topBase}) plus resonance.attunedTierBonus " +
                $"({sockets.AttunedTierBonus}) grants tier {topGranted}, above the atom ladder's " +
                $"{FamilyExpansion.TierCount} — a combination granted there would bind no atom. " +
                $"Extend the ladder or lower the tier; never clamp.");

        if (t.CatalogueSizeBar < 1)
            throw new InvalidOperationException(
                "learnability.catalogueSizeBar below 1 is unreachable — the bar is a report " +
                "threshold, not a cap, and a bar nothing can clear reports on every run");

        if (t.ExactDuplicateNamesMax < 0 || t.NearDuplicateRateMaxPermille < 0)
            throw new InvalidOperationException("a distinctness threshold is never negative");
    }
}
