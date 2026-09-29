using System.Reflection;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

/// <summary>
/// E42 `units-correction` (docs/architecture/effect-atom/spec-units-correction.md). `definitions.md` §2
/// claimed every derived-channel magnitude was "resolver points — sigmoid scale" for eleven days after
/// the item program proved `combat.power.*` / `combat.defense.*` / `combat.shield.*` are flat game units
/// (`item/atom-layer-handoff.md` §1, 2026-08-22) — corrected 2026-09-03. The claim survived four
/// adversarial passes because nothing asserted it; this suite is that assertion, on both the doc side
/// (the words) and the code side (the decisive negative evidence — <c>CombatProbabilityPolicy</c>
/// declares no <c>PowerScale</c>/<c>DefenseScale</c>, checked by reflection so a future addition of either
/// property is itself a signal this test must be revisited, not silently invalidated).
/// </summary>
public class UnitsCorrectionDriftTests
{
    static string ReadDoc(string relativeToArchitecture)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "docs", "architecture", relativeToArchitecture);
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"could not find docs/architecture/{relativeToArchitecture}");
    }

    [Fact]
    public void CombatProbabilityPolicy_declaresNoPowerOrDefenseScale()
    {
        // The decisive negative evidence the whole correction rests on. If this ever starts passing
        // FALSE (i.e. one of these properties is added), the units correction's own premise needs
        // re-checking before anyone trusts it again — this is not a test that should be "fixed" by
        // deleting it.
        var members = typeof(CombatProbabilityPolicy)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("PowerScale", members);
        Assert.DoesNotContain("DefenseScale", members);

        // What IS there — the genuinely sigmoid families — so the test also pins the positive claim,
        // not only the absence.
        Assert.Contains("AccuracyScale", members);
        Assert.Contains("CritRateScale", members);
        Assert.Contains("CritDamageScale", members);
    }

    [Fact]
    public void DefinitionsMd_statesFlatGameUnitsForPowerDefenseShield()
    {
        var text = ReadDoc(Path.Combine("effect-atom", "definitions.md"));

        Assert.Contains("combat.power.*", text);
        Assert.Contains("combat.defense.*", text);
        Assert.Contains("combat.shield.*", text);
        Assert.Contains("game units", text);
        Assert.Contains("Not resolver points", text);

        // The reason, not just the verdict (spec-units-correction.md contract rule 1) — a corrected
        // claim with no evidence is exactly as unreviewable as the wrong claim it replaced.
        Assert.Contains("PowerScale", text);
        Assert.Contains("OverlayCombatCalculator", text);
    }

    [Fact]
    public void AtomFamilyLibraryMd_agreesWithDefinitions()
    {
        var text = ReadDoc(Path.Combine("effect-atom", "atom-family-library.md"));

        Assert.Contains("+10 fire power` is ten damage", text);
        Assert.Contains("crit rate", text);
    }

    [Fact]
    public void UnitsClaim_failsOnAPlantedDrift()
    {
        // The exact pre-correction wording. If this ever comes back, the doc has regressed to the claim
        // that cost the item program's tier bands an order of magnitude, and the assertions above would
        // still pass on a partial revert (e.g. restoring the sentence but leaving the corrected row).
        var definitions = ReadDoc(Path.Combine("effect-atom", "definitions.md"));
        Assert.DoesNotContain(
            "Derived-channel magnitudes | **resolver points** — sigmoid scale, `AccuracyScale = CritRateScale = 100.0`",
            definitions);

        var familyLibrary = ReadDoc(Path.Combine("effect-atom", "atom-family-library.md"));
        Assert.DoesNotContain(
            "**`+10 fire power` is ten *resolver points*** — sigmoid scale, where `AccuracyScale` and `CritRateScale` are `100.0`, so ten points is 0.1 sigmoid units.",
            familyLibrary);
    }

    [Fact]
    public void SpecValueSpecAndCurve_agreesWithDefinitions()
    {
        var text = ReadDoc(Path.Combine("effect-atom", "spec-value-spec-and-curve.md"));
        Assert.Contains("+10 fire power` is **ten damage**", text);
        Assert.DoesNotContain("`+10 fire power` is **ten resolver points**", text);
    }
}
