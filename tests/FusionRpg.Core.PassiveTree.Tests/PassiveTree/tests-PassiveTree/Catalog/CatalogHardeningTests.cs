using System.Reflection;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.State;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Catalog;

/// <summary>
/// Task C3 — "Catalog record hardening: the axis, the enums, the reflection sweep"
/// (spec-tree-catalog.md §2.1 R7, §2.2 D40, §2.3, §2.4, §3, §Success criteria).
///
/// Read against B2's shipped loader/records before writing anything here (`PassiveTreeCatalogLoader
/// .cs`, `NodeAtom.cs`, `NodeRecord.cs`, `TreeRecord.cs`, `PassiveTreeCatalogLoaderTests.cs`): three
/// of C3's four numbered gaps are ALREADY closed by B2, verified line-by-line rather than assumed —
///
///   1. scaleAxis/UnitClass disagreement, naming both values:
///      `PassiveTreeCatalogLoader.LoadAtom`'s `expectedAxis` switch + refusal message, tested by
///      `PassiveTreeCatalogLoaderTests.A_sigmoid_channel_carrying_PTheta_is_refused_the_silent_failure_class`.
///   2. the five-value `TreeCategory` enum + the plan-token import map, refusing an unknown token by
///      name: `TreeRecord.cs`'s `TreeCategory` enum + `PassiveTreeCatalogLoader.CategoryTokenMap`,
///      tested by `PassiveTreeCatalogLoaderTests.A_category_token_outside_the_five_value_map_is_refused_naming_it`.
///      Cross-checked against `gk-forge/tools/seedsmith/seedsmith/adapters/trees/plan/emit.py:41,154` — the
///      plan emits the canonical five tokens directly (`"primary" | "elemental" | "status" | "family"
///      | "species"`), not the `aptitude`/`creatureFamily` renames the spec's prose describes; the map
///      already carries both spellings defensively, so there is no live gap to close.
///   3a. exclusionForm/excludeProps disagreement in both directions, and IdMismatch kept as authored
///      (never rewritten): both already refuse and both already have named tests in
///      `PassiveTreeCatalogLoaderTests.cs`.
///
/// Restating any of those with a new test would not be a gap — it would be coverage duplication,
/// which C3's own instructions rule out. Two real, previously-untested gaps remained, and this file
/// closes them:
///
///   3b. `soulCurveId` had ZERO format validation before this task — any string, including a bare
///       formula/expression, loaded silently. Closed (2026-09-06) by `PassiveTreeCatalogLoader`'s new
///       `SoulCurveIdPattern` check. **Superseded 2026-09-07 (J12, D58, spec-soul-curve-resolution.md):
///       `NodeAtom.SoulCurveId` had zero consumers that ever acted on its value — retired entirely
///       rather than extended. `SoulCurveIdPattern` and all four tests this gap's own closure added are
///       removed below, not left asserting a field that no longer exists.**
///   4.  no reflection sweep existed proving every stored magnitude is `long`. Closed below, with an
///       automated falsifiability proof —
///       `The_classification_sweep_actually_turns_red_on_an_unclassified_numeric_field` runs the exact
///       same helper the production sweep calls, against a decoy type built to fail it, rather than
///       asserting by code comment that a manual edit was tried and reverted. (A companion sweep that
///       refused any `float`/`double` record field was removed by the 2026-09-15 owner ruling lifting
///       the project-wide floating-point ban; a new numeric field of any type still has to be
///       classified by the sweep that remains.)
/// </summary>
public class CatalogHardeningTests
{
    static PassiveTreeTuning MakeTuning() => new(
        SchemaVersion: 1, Version: 1,
        TierLadder: new TierLadderTuning(5),
        Budget: new BudgetTuning(1000, 500),
        TreeShareMilli: 1000, TreeBudgetMilli: 1000,
        Potency: new PotencyTuning(182, 1, new long[] { 46, 91, 137, 182 }),
        Mechanism: new MechanismTuning(0, 1000),
        Archetype: new ArchetypeTuning(6000),
        Exclusion: new ExclusionTuning(20),
        ArchetypeAssignment: "ordinal-round-robin",
        DesignTarget: new DesignTargetTuning(92),
        Concentration: new ConcentrationTuning(1200, 500),
        SoulTrack: new SoulTrackTuning(1000),
        UnlockCost: new UnlockCostTuning(5, 2),
        Respec: new RespecTuning(50, 500),
        GateCounters: new GateCountersTuning(23, 23, 4, 4, 5000, null));

    // ---- gap 3b (soulCurveId format validation) retired 2026-09-07 (J12, D58) alongside the field
    // itself -- see the class doc comment above. TreeJsonWithSoulCurveId and its four tests
    // (A_well_formed_curve_reference_is_accepted, A_null_soulCurveId_is_accepted_the_field_is_optional,
    // A_formula_or_expression_is_refused_never_accepted_as_a_reference,
    // A_soulCurveId_missing_the_curve_prefix_is_refused) removed rather than left asserting a field
    // and a validation path that no longer exist.

    // ---- gap 4: the reflection sweep -------------------------------------------------------------

    // Every SCALAR numeric property across the three catalog record types, classified once so a new
    // field forces a conscious decision instead of silently picking a type. Collection element types
    // (`NodesPerTier: IReadOnlyList<int>` — per-tier node COUNTS, not magnitudes; `Atoms:
    // IReadOnlyList<NodeAtom>` etc.) are out of scope for this scalar sweep by design — a count is
    // exempt from docs/architecture/numeric-types.md's magnitude rule the same way a structural limit is.
    static readonly HashSet<(Type Record, string Property)> StructuralOrBoundedFields = new()
    {
        // TreeRecord — structural, never a magnitude a balance pass would move via P(Theta).
        (typeof(TreeRecord), nameof(TreeRecord.Tiers)),          // D29: the tree's own depth
        (typeof(TreeRecord), nameof(TreeRecord.Branches)),       // D10: always 2
        (typeof(TreeRecord), nameof(TreeRecord.CatalogVersion)), // a version counter
        // NodeRecord
        (typeof(NodeRecord), nameof(NodeRecord.Tier)),               // which tier gate — an index
        (typeof(NodeRecord), nameof(NodeRecord.BudgetShareMilli)),   // a bounded ‰-of-one-branch
            // SHARE (R4/R5) — exempt as a bounded ratio (PS-8), and it is the PLAN's own number,
            // never a resolved magnitude; the potency ceiling is checked against this, not kMicro
            // (§2.5's dimensional-error correction)
        (typeof(NodeRecord), nameof(NodeRecord.RetiredAtRevision)), // a revision counter, nullable
    };

    static readonly HashSet<(Type Record, string Property)> LongMagnitudeFields = new()
    {
        // NodeAtom.KMicro is the ONLY magnitude-shaped coefficient the catalog stores anywhere
        // (§2.3) — "kMicro is this node's share of P(Θ) in per-million... a coefficient, not a
        // resolved value." docs/architecture/numeric-types.md rule 1: `long` for any magnitude `contentScale` can touch.
        (typeof(NodeAtom), nameof(NodeAtom.KMicro)),
    };

    static readonly Type[] CatalogRecordTypes = { typeof(TreeRecord), typeof(NodeRecord), typeof(NodeAtom) };

    static IEnumerable<PropertyInfo> ScalarNumericProperties(Type recordType) =>
        recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => IsScalarNumeric(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType));

    static bool IsScalarNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
        || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    /// <summary>The classification-sweep logic, extracted so the falsifiability test below runs the
    /// identical code path against a decoy type instead of re-implementing the check.</summary>
    static IEnumerable<string> UnclassifiedOrMisclassifiedNumericFields(Type recordType)
    {
        foreach (var prop in ScalarNumericProperties(recordType))
        {
            var key = (recordType, prop.Name);
            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            if (LongMagnitudeFields.Contains(key))
            {
                if (underlying != typeof(long))
                    yield return $"{recordType.Name}.{prop.Name} is classified as a MAGNITUDE but is " +
                                $"{prop.PropertyType.Name}, not long (docs/architecture/numeric-types.md rule 1).";
            }
            else if (StructuralOrBoundedFields.Contains(key))
            {
                // int/int? is fine here — each one is commented above with why it is exempt
                // (a count, an index, a version, or a bounded ratio never touched by P(Θ)).
            }
            else
            {
                yield return $"{recordType.Name}.{prop.Name} ({prop.PropertyType.Name}) is not classified " +
                            "as either a bounded/structural field or a long magnitude in " +
                            $"{nameof(CatalogHardeningTests)} — classify it before this test can pass. " +
                            "This is what catches an unnoticed RESOLVED magnitude landing on the record.";
            }
        }
    }

    [Fact]
    public void Every_stored_magnitude_field_is_long_and_every_numeric_field_is_classified()
    {
        // The complementary half of the sweep: every scalar numeric property on the three catalog
        // record types is accounted for as EITHER a bounded/structural field (documented above,
        // never touched by a P(Θ) multiply) OR a long magnitude coefficient. An unclassified numeric
        // field — including a `resolved magnitude` someone adds under a new name — fails here rather
        // than silently shipping as an `int`, which is the "no resolved magnitude is stored anywhere
        // on the record" half of C3's acceptance bar: this test cannot pass without a conscious
        // classification decision for every numeric field that exists.
        foreach (var recordType in CatalogRecordTypes)
        {
            var violations = UnclassifiedOrMisclassifiedNumericFields(recordType).ToList();
            Assert.True(violations.Count == 0, string.Join("; ", violations));
        }

        // And the positive assertion, named directly: the one magnitude the catalog stores is long.
        var kMicroProp = typeof(NodeAtom).GetProperty(nameof(NodeAtom.KMicro))!;
        Assert.Equal(typeof(long), kMicroProp.PropertyType);
    }

    /// <summary>A decoy type carrying a numeric field that is neither a registered long-magnitude
    /// coefficient nor a documented structural/bounded field — exactly the shape an unnoticed
    /// RESOLVED magnitude (e.g. a cached `P(Θ)` output someone stores under a plausible new name)
    /// would take. Never referenced by production code.</summary>
    sealed record PlantedUnclassifiedMagnitudeDefect(long Fine, int ResolvedPowerSnapshot);

    [Fact]
    public void The_classification_sweep_actually_turns_red_on_an_unclassified_numeric_field()
    {
        // Same helper the production test above calls, run against a type built to fail it — the
        // automated proof that "no resolved magnitude is stored anywhere on the record" is an
        // enforced property of this sweep, not just a comment's claim.
        var violations = UnclassifiedOrMisclassifiedNumericFields(typeof(PlantedUnclassifiedMagnitudeDefect)).ToList();

        Assert.Contains(violations, v => v.Contains(nameof(PlantedUnclassifiedMagnitudeDefect.ResolvedPowerSnapshot)));
    }
}
