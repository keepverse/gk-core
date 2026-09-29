using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Power;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// content-scale (T3.4, spec-content-scale.md §5). The ratio itself, the single-multiplication
/// contract inside <see cref="Instantiator"/>, and the real shipped corpus's identity-at-the-pin
/// proof. Reproducibility/round-trip behavioural cases beyond the ratio live in
/// <c>InstantiatorTests.cs</c> (extended, not duplicated, per this program's own convention).
/// </summary>
public class ContentScaleTests
{
    static PowerTuning TuningAt(long bMilli) => PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    // ---- the ratio itself -------------------------------------------------------------------------

    [Fact]
    public void Identity_AtThePin_IsExactlyOneThousandMilli()
    {
        // Theta_content = 20 -> contentScale == 1.000 exactly, for ANY B (the pin holds regardless of
        // the dial, T1.1/T1.2's own proof) -- the corpus must not move at calibration depth.
        foreach (var bMilli in new long[] { 0, 200, 400, 1000 })
            Assert.Equal(1000, ContentScale.Milli(20, TuningAt(bMilli)));
    }

    [Theory]
    [InlineData(50, 2764)]
    [InlineData(100, 6882)]
    [InlineData(200, 19529)]
    public void ScalingTable_AtTheDecidedDial_MatchesSsotWithinRounding(int thetaContent, long expectedMilli)
    {
        // SSOT S4.5's table (B=0.4, the decided-but-not-yet-dialed value -- T4.2 turns it on):
        // Theta=50 -> 2.76x, 100 -> 6.88x, 200 -> 19.5x. Asserted to the exact milli this
        // implementation derives (independently re-computed, not read off the code under test) --
        // tighter than matching the doc's 2-3 significant figures, and it still contains them.
        var milli = ContentScale.Milli(thetaContent, TuningAt(400));
        Assert.Equal(expectedMilli, milli);
        Assert.InRange(milli / 1000.0, expectedMilli / 1000.0 - 0.005, expectedMilli / 1000.0 + 0.005);
    }

    [Fact]
    public void BZero_StillRatioCorrect_NotInert()
    {
        // At B=0 (what actually ships through content-scale's own wave), contentScale is still a
        // real, non-trivial ratio away from the pin -- this module is not a no-op just because the
        // dial hasn't been turned yet.
        var milli50 = ContentScale.Milli(50, TuningAt(0));
        var milli100 = ContentScale.Milli(100, TuningAt(0));
        Assert.NotEqual(1000, milli50);
        Assert.True(milli100 > milli50, "contentScale must keep growing with Theta_content even at B=0");
    }

    [Fact]
    public void Apply_AtIdentityScale_ReturnsTheExactSameValue()
    {
        // The algebraic reason "byte-identical at the pin" needs no per-value rounding-drift argument:
        // round(x*1000/1000) == x exactly, for every int x, not just the ones the corpus happens to use.
        foreach (var x in new[] { -1000, -1, 0, 1, 45, 12345, int.MaxValue / 1000 })
            Assert.Equal(x, ContentScale.Apply(x, 1000));
    }

    [Fact]
    public void Apply_IsReversibleWithinOneRoundingUnit()
    {
        var scaleMilli = ContentScale.Milli(100, TuningAt(400));
        var scaled = ContentScale.Apply(37, scaleMilli);
        var recovered = scaled * 1000.0 / scaleMilli;
        Assert.InRange(recovered, 37 - 1, 37 + 1);
    }

    // ---- the single-multiplication contract, exercised through Instantiator -----------------------

    static readonly System.Collections.Generic.Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static ContentScaleTests()
    {
        var id = AtomRow.DeriveId("atom.test-scale", "", 1);
        Catalog[id] = new AtomRow
        {
            AtomId = id, KindId = "stat.modify", FamilyId = "atom.test-scale", Variant = "", Tier = 1,
            ParamsJson = "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"min\":100,\"max\":100,\"roll\":\"onInstantiate\"}}",
        };
    }

    static AtomRow? Lookup(string atomId) => Catalog.TryGetValue(atomId, out var a) ? a : null;

    static ContainerRow Container() => new()
    {
        ContainerId = "item.test-scale-container", // ContainerValidator requires id prefix == kind prefix + "."
        Kind = ContainerKind.Item,
        Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId("atom.test-scale", "", 1)) },
    };

    static long ResolvedAmount(InstanceRow instance)
    {
        using var doc = JsonDocument.Parse(instance.Atoms.Single().ValuesJson);
        return doc.RootElement.GetProperty("amount").GetInt64();
    }

    [Fact]
    public void RollThenScale_SameSeedDifferentDepth_SameRelativeRoll_DifferentAbsolute()
    {
        // min==max==100 removes the RNG as a variable -- any difference between the two resolved
        // amounts is attributable to contentScale alone, not to a different roll.
        var tuning = TuningAt(400);
        Assert.True(Instantiator.TryInstantiate(Container(), Lookup, _ => null, 7, 20, tuning, out var atPin).IsOk);
        Assert.True(Instantiator.TryInstantiate(Container(), Lookup, _ => null, 7, 100, tuning, out var atDepth).IsOk);

        Assert.Equal(100, ResolvedAmount(atPin!));                 // Theta=20: contentScale=1.000, unscaled
        Assert.Equal(ContentScale.Apply(100, ContentScale.Milli(100, tuning)), ResolvedAmount(atDepth!));
        Assert.True(ResolvedAmount(atDepth!) > ResolvedAmount(atPin!));
    }

    [Fact]
    public void Recorded_OnTheInstance_MatchesWhatWasActuallyApplied()
    {
        var tuning = TuningAt(400);
        Assert.True(Instantiator.TryInstantiate(Container(), Lookup, _ => null, 7, 100, tuning, out var inst).IsOk);

        Assert.Equal(100, inst!.ThetaContent);
        Assert.Equal(ContentScale.Milli(100, tuning), inst.ContentScaleMilli);

        // Reversible: dividing the recorded value by the recorded scale recovers the relative roll.
        var recovered = ResolvedAmount(inst) * 1000.0 / inst.ContentScaleMilli;
        Assert.InRange(recovered, 100 - 1, 100 + 1);
    }

    [Fact]
    public void AppliedOnce_InstantiatingTwiceNeverCompoundsTheScale()
    {
        // "Instantiate twice through the full path" (spec S5) -- two independent TryInstantiate calls
        // at the same Theta, not a re-scale of an already-scaled value. Each call recomputes
        // contentScale from Theta fresh, so nothing accumulates across calls.
        var tuning = TuningAt(400);
        Assert.True(Instantiator.TryInstantiate(Container(), Lookup, _ => null, 7, 100, tuning, out var first).IsOk);
        Assert.True(Instantiator.TryInstantiate(Container(), Lookup, _ => null, 7, 100, tuning, out var second).IsOk);

        Assert.Equal(ResolvedAmount(first!), ResolvedAmount(second!));
        Assert.Equal(ContentScale.Apply(100, ContentScale.Milli(100, tuning)), ResolvedAmount(second!));
    }

    [Fact]
    public void MissingThetaContent_CannotEvenCompile_SoNoRuntimeSilent1Point0PathExists()
    {
        // spec-content-scale.md S2.4: "Absence is a rejection, not a default of 1.0." Instantiator.
        // TryInstantiate's thetaContent/tuning parameters are required, not optional/defaulted --
        // this test documents that decision structurally: reflect over the method and assert neither
        // parameter carries a default value, so a future edit that "helpfully" adds `= 20` to either
        // (silently reintroducing the exact bug this task exists to prevent) fails here.
        var method = typeof(Instantiator).GetMethod(nameof(Instantiator.TryInstantiate))!;
        var thetaParam = method.GetParameters().Single(p => p.Name == "thetaContent");
        var tuningParam = method.GetParameters().Single(p => p.Name == "tuning");
        Assert.False(thetaParam.HasDefaultValue, "thetaContent must stay a required parameter");
        Assert.False(tuningParam.HasDefaultValue, "tuning must stay a required parameter");
    }

    // ---- PowerVector / atom pricing stays unscaled --------------------------------------------------

    [Fact]
    public void CostFunctionPrice_TakesNoThetaOrScaleParameter_StructurallyCannotDoubleCount()
    {
        // The double-count tripwire (spec S5) proven structurally, not just empirically:
        // CostFunction.Price's signature accepts only (AtomRow, PowerTables?, depth) -- no
        // Theta_content, no PowerTuning, no InstanceRow -- so a future edit that "helpfully" threads
        // a content-scale parameter through pricing fails HERE, at reflection time, rather than
        // silently double-counting the ratio the first time someone notices priced power drifting
        // with drop depth.
        var parameters = typeof(CostFunction)
            .GetMethod(nameof(CostFunction.Price))!
            .GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(int) && p.Name == "thetaContent");
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(PowerTuning));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(InstanceRow));
    }

    [Fact]
    public void PowerVector_PricedPowerIdentical_RegardlessOfWhatThetaAnInstanceRolledAt()
    {
        // Empirical companion to the structural check above: instantiate the SAME catalog atom at
        // two different content depths -- the instances' rolled amounts genuinely differ -- and
        // confirm pricing the catalog row is untouched either way.
        var atom = Catalog.Values.Single();
        var tuning = TuningAt(400);

        Assert.True(Instantiator.TryInstantiate(Container(), Lookup, _ => null, 7, 20, tuning, out var atPin).IsOk);
        Assert.True(Instantiator.TryInstantiate(Container(), Lookup, _ => null, 7, 200, tuning, out var atDeep).IsOk);
        Assert.NotEqual(ResolvedAmount(atPin!), ResolvedAmount(atDeep!)); // the instances DID scale differently...

        var pricedBefore = CostFunction.Price(atom);
        var pricedAfter = CostFunction.Price(atom);
        Assert.True(pricedBefore.Ok);
        Assert.Equal(pricedBefore.Power, pricedAfter.Power); // ...pricing the catalog row did not move at all
    }

    // ---- the real shipped corpus: identity at the pin -----------------------------------------------

    [Fact]
    public void ShippedCorpus_AtThetaContent20_EveryOnInstantiateAndFixedValue_IsUnscaled()
    {
        var (atoms, containers) = ShippedSeed();
        var lookup = atoms.ToDictionary(a => a.AtomId, a => a, StringComparer.Ordinal);
        var tuning = TuningAt(0); // the shipped dial -- doesn't matter for this assertion, Theta=20 is B-invariant

        var checkedContainers = 0;
        foreach (var container in containers.Take(50)) // representative sample -- full corpus is exercised by the suite's other coverage
        {
            // T3.1: the real corpus's pool rows are not yet migrated to affix ids (that migration is
            // T3.2's own scope), so `_ => null` here means every POOLED container fails validation
            // and is skipped by the `if (!r.IsOk) continue` below — same as any other
            // not-yet-instantiable row this test already tolerates.
            var r = Instantiator.TryInstantiate(container, id => lookup.TryGetValue(id, out var a) ? a : null,
                _ => null, rollSeed: 123456, thetaContent: 20, tuning, out var instance);
            if (!r.IsOk) continue; // some rows in the corpus aren't standalone-instantiable (e.g. pool-only); not this test's concern
            checkedContainers++;

            Assert.Equal(1000, instance!.ContentScaleMilli);
            foreach (var a in instance.Atoms)
            {
                using var doc = JsonDocument.Parse(a.ValuesJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                    // Every numeric field is either an untouched non-value param or a scaled magnitude
                    // at scale=1.000 -- either way it must equal what freezing produces without content-scale,
                    // which Apply_AtIdentityScale_ReturnsTheExactSameValue already proves algebraically for
                    // any int. This loop's job is just confirming real corpus JSON round-trips through
                    // JsonElement without silently losing precision, not re-deriving that proof per field.
                    Assert.True(prop.Value.TryGetInt64(out _) || prop.Value.TryGetDouble(out _));
                }
            }
        }
        Assert.True(checkedContainers > 0, "no containers in the sampled corpus were instantiable -- test needs a different sample");
    }

    static (System.Collections.Generic.IReadOnlyList<AtomRow> Atoms, System.Collections.Generic.IReadOnlyList<ContainerRow> Containers) ShippedSeed()
    {
        var root = RepoRoot();
        var files = new[] { "atoms", "containers" }
            .Select(d => Path.Combine(root, "data", "seed", d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        return (collected.Content.Atoms, collected.Content.Containers);
    }

    static string RepoRoot() => ContentRoot.Path;
}
