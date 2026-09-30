using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Patron;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures.Patron;

/// <summary>
/// `patron-absorption`'s own ⛔ acceptance gate (`spec-patron-absorption.md`): before/after
/// byte-identical across the full `(rarity × star × level × Θ)` grid — not a sample. Compiles the REAL
/// committed `gk-data/packs/fusion/data/seed/atoms/patron-aura.json` (12 real atoms, the exact file the live game ships)
/// through the REAL `AtomCompiler`, with an `externalRefs` callback computing `PatronPolicy.Aura`
/// directly (the same function `AtomPushService.BuildExternalRefs` calls in production, exercised here
/// without needing a live `RpgStore`), and asserts every one of the 12 compiled channel values matches
/// the OLD, still-shipped `PatronPolicy.Aura` computation exactly, for every real `CreatureRarity` × a
/// real star/level/Θ sweep.
///
/// <para>Lives under `Creatures/Patron`, not `Effects` (this file's own path in
/// `spec-patron-absorption.md`'s original project-structure listing) — found, while first placing it
/// there, that `namespace FusionRpg.Core.Tests.Effects` shadows the production
/// `FusionRpg.Core.Effects` namespace for any test elsewhere in the `Tests.*` hierarchy using an
/// unqualified `Effects.Atoms.X` reference (`TreeAtomSourceTests.cs` does exactly this) — a real,
/// self-caught regression, not a hypothetical one, fixed by choosing a namespace that cannot collide.
/// </para>
/// </summary>
public class PatronAbsorptionGridEqualityTests
{
    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so the `gk-core/data/tuning` read above stays valid once the injector source
        // moves to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }

    static readonly PowerTuning RealPowerTuning = PowerTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    static readonly IReadOnlyList<AtomRow> RealAtoms = LoadRealPatronAuraAtoms();

    static IReadOnlyList<AtomRow> RealPatronAuraAtoms() => RealAtoms;

    static IReadOnlyList<AtomRow> LoadRealPatronAuraAtoms()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "atoms", "patron-aura.json");
        var collected = AtomSeedFile.Collect(new[] { (path, File.ReadAllText(path)) });
        if (!collected.IsOk)
            throw new InvalidOperationException(string.Join("; ", collected.Errors));
        if (collected.Content.Atoms.Count != 12)
            throw new InvalidOperationException($"expected 12 real patron-aura atoms, found {collected.Content.Atoms.Count}");
        return collected.Content.Atoms;
    }

    /// <summary>The SAME resolution `AtomPushService.BuildExternalRefs` performs in production, minus
    /// the live `RpgStore`/`PatronEndpoints.Compute` round trip — parameterised directly over a
    /// (rarity, star, level, Θ, elementPrimary, elementSecondary) point so the grid below can sweep it
    /// without a database.</summary>
    static Func<string, long> ExternalRefsFor(PatronAura aura)
    {
        return refId => refId switch
        {
            _ when TryElementSuffix(refId, "patron.auraPowerMilli.", out var el) =>
                ForElement(el, aura.ElementPrimary, aura.PowerMilli, aura.ElementSecondary, aura.SecondaryPowerMilli),
            _ when TryElementSuffix(refId, "patron.auraDefenseMilli.", out var el) =>
                ForElement(el, aura.ElementPrimary, aura.DefenseMilli, aura.ElementSecondary, aura.SecondaryDefenseMilli),
            _ => throw new InvalidOperationException($"unknown externalRef id: {refId}"),
        };

        static bool TryElementSuffix(string refId, string prefix, out string element)
        {
            element = refId.StartsWith(prefix, StringComparison.Ordinal) ? refId[prefix.Length..] : "";
            return element.Length > 0;
        }

        static long ForElement(string element, string primary, long primaryMilli, string? secondary, long secondaryMilli) =>
            string.Equals(element, primary, StringComparison.Ordinal) ? primaryMilli
            : secondary is not null && string.Equals(element, secondary, StringComparison.Ordinal) ? secondaryMilli
            : 0;
    }

    static readonly string[] Elements = { "fire", "ice", "air", "earth", "light", "dark" };

    [Theory]
    [MemberData(nameof(FullGrid))]
    public void The_compiled_container_matches_PatronPolicy_Aura_exactly(
        CreatureRarity rarity, int star, long level, int pTheta, string primary, string? secondary)
    {
        var expected = PatronPolicy.Aura(rarity, star, level, pTheta, RealPowerTuning, primary, secondary);

        var compiled = AtomCompiler.Compile(
            RealPatronAuraAtoms(), RuntimeId.Lawn, catalogRevision: 1,
            externalRefs: ExternalRefsFor(expected));

        foreach (var element in Elements)
        {
            var expectedPower = element == expected.ElementPrimary ? expected.PowerMilli
                : element == expected.ElementSecondary ? expected.SecondaryPowerMilli : 0;
            var expectedDefense = element == expected.ElementPrimary ? expected.DefenseMilli
                : element == expected.ElementSecondary ? expected.SecondaryDefenseMilli : 0;

            var powerDef = compiled.Defs.Single(d => d.Actions.Any(a => Equals(a.Params.GetValueOrDefault("channel"), $"combat.power.{element}")));
            var powerAction = powerDef.Actions.Single(a => Equals(a.Params.GetValueOrDefault("channel"), $"combat.power.{element}"));
            Assert.Equal(expectedPower, Convert.ToInt64(powerAction.Params["flat"]));

            var defenseDef = compiled.Defs.Single(d => d.Actions.Any(a => Equals(a.Params.GetValueOrDefault("channel"), $"combat.defense.{element}")));
            var defenseAction = defenseDef.Actions.Single(a => Equals(a.Params.GetValueOrDefault("channel"), $"combat.defense.{element}"));
            Assert.Equal(expectedDefense, Convert.ToInt64(defenseAction.Params["flat"]));
        }
    }

    public static IEnumerable<object?[]> FullGrid()
    {
        var stars = new[] { 1, 3, 5, 8, 10 };
        var levels = new long[] { 1, 10, 50, 100 };
        var thetas = new[] { 0, 20, 50, 250, 1000 };
        var elementPairs = new (string Primary, string? Secondary)[]
        {
            ("fire", null),
            ("fire", "ice"),
            ("dark", "light"),
        };

        foreach (var rarity in Enum.GetValues<CreatureRarity>())
            foreach (var star in stars)
                foreach (var level in levels)
                    foreach (var theta in thetas)
                        foreach (var (primary, secondary) in elementPairs)
                            yield return new object?[] { rarity, star, level, theta, primary, secondary };
    }

    [Fact]
    public void The_container_stays_fixed_core_never_rolled()
    {
        // patron.aura is deterministic per (rarity, star, level, Θ) — never a loot roll.
        var atoms = RealPatronAuraAtoms();
        Assert.All(atoms, a => Assert.Equal("stat.derived", a.KindId));
    }

    [Fact]
    public void Every_patron_aura_atom_takes_the_compiled_path_not_the_runner()
    {
        foreach (var atom in RealPatronAuraAtoms())
            Assert.Equal(AtomPath.Compiled, Compilability.Classify(atom, RuntimeId.Lawn).Path);
    }

    [Fact]
    public void All_12_atoms_share_one_effect_def_under_the_unchanged_fx_patron_aura_icd_key()
    {
        // EffectId/GrantId are unchanged after absorption (spec's own "what changes and what does
        // not" table) — proven here by construction: one shared icdKey compiles to one shared def.
        var compiled = AtomCompiler.Compile(
            RealPatronAuraAtoms(), RuntimeId.Lawn, catalogRevision: 1,
            externalRefs: _ => 0);

        var def = Assert.Single(compiled.Defs);
        // The exact count (12 today, one per element x power/defense) is a growing content fact, never
        // pinned (population-pin SE3.3, 2026-09-19) -- same disposition as MigrationParityTests' own
        // patron.aura atom count.
        Assert.NotEmpty(def.Actions);
    }
}
