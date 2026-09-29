using System.Reflection;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// The AI contract (spec-power-reads.md, E10), enforced rather than described.
///
/// <blockquote>AI reads the <b>vector</b> and the <b>matchup-conditioned</b> read. It <b>never</b>
/// reads the display scalar.</blockquote>
///
/// <para>The spec deferred this test on the grounds that no AI layer existed, so an architecture rule
/// over an empty namespace would pass forever and guard nothing. That is no longer true:
/// <c>FusionRpg.Core.World.Ai</c> ships today with a dozen types in it. The rule is enforceable now,
/// against a namespace that is real, which is the difference between a guard and a comment.</para>
///
/// <para><b>Why the scalar is off limits to a decision-maker.</b> It is a geometric mean over five
/// categories with different bases — it sorts like-for-like and nothing more. Two vectors that are
/// nothing alike can share a scalar, so a decision taken on it is a decision taken on a number that
/// deliberately threw away what it needed.</para>
/// </summary>
public class PowerAiContractTests
{
    const string AiNamespace = "FusionRpg.Core.World.Ai";

    static readonly Assembly Core = typeof(PowerVector).Assembly;

    static IReadOnlyList<Type> AiTypes() =>
        Core.GetTypes()
            .Where(t => t.Namespace is { } ns
                        && ns.StartsWith(AiNamespace, StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void The_ai_namespace_is_not_empty_so_this_guard_guards_something()
    {
        // Without this the rules below would pass over nothing at all, which is exactly why the spec
        // deferred them. If the namespace is ever renamed, this fails first and says so.
        Assert.NotEmpty(AiTypes());
    }

    [Fact]
    public void No_ai_type_references_the_display_scalar()
    {
        var offenders = AiTypes()
            .Where(t => ReferencesInIl(t, nameof(PowerScalar)))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "AI must read the vector or the matchup read, never the display scalar: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scalar_cannot_see_a_matchup_swing_that_the_vector_shows_plainly()
    {
        // The concrete cost of deciding on the scalar. A fire attacker into ice is 25% stronger — a
        // swing any AI must act on — and at small magnitudes the scalar reports the SAME NUMBER,
        // because a fifth root over five categories is that coarse near the bottom of its range.
        var small = new PowerVector(100, 10, 0, 0, 0);
        var strong = MatchupRead.AgainstCombat(small,
            new[] { FusionRpg.Core.Stats.Derived.ElementTypeId.Fire },
            new[] { FusionRpg.Core.Stats.Derived.ElementTypeId.Ice });

        Assert.True(strong.Offense > small.Offense, "the vector shows the swing");
        Assert.Equal(PowerScalar.Of(small), PowerScalar.Of(strong)); // the scalar does not
    }

    [Fact]
    public void At_larger_magnitudes_the_two_reads_do_diverge()
    {
        // The other half — the scalar is not simply constant. It is lossy, which is worse: it
        // sometimes tracks the difference and sometimes silently does not.
        var large = new PowerVector(100_000, 50_000, 20_000, 10_000, 5_000);
        var strong = MatchupRead.AgainstCombat(large,
            new[] { FusionRpg.Core.Stats.Derived.ElementTypeId.Fire },
            new[] { FusionRpg.Core.Stats.Derived.ElementTypeId.Ice });

        Assert.True(PowerScalar.Of(strong) > PowerScalar.Of(large));
    }

    [Fact]
    public void Two_unlike_vectors_can_share_a_scalar()
    {
        // The concrete reason a decision-maker must not use it: the scalar is lossy on purpose.
        var glassCannon = new PowerVector(100, 0, 0, 0, 0);
        var fortress = new PowerVector(0, 100, 0, 0, 0);

        Assert.Equal(PowerScalar.Of(glassCannon), PowerScalar.Of(fortress));
        Assert.NotEqual(glassCannon, fortress);
    }

    [Fact]
    public void The_scan_finds_a_call_when_there_is_one()
    {
        // The positive control. Without it, `No_ai_type_references_the_display_scalar` could be
        // passing because the scan never finds anything — a guard that guards by being broken.
        Assert.True(ReferencesInIl(typeof(DeliberateOffender), nameof(PowerScalar)));
        Assert.False(ReferencesInIl(typeof(InnocentBystander), nameof(PowerScalar)));
    }

    /// <summary>A type that does exactly what the AI contract forbids, so the scan has something to find.</summary>
    static class DeliberateOffender
    {
        public static int Read(PowerVector v) => PowerScalar.Of(v);
    }

    /// <summary>The same shape, reading the vector instead — which is what the contract allows.</summary>
    static class InnocentBystander
    {
        public static int Read(PowerVector v) => v.Offense;
    }

    /// <summary>
    /// Whether a type's method bodies mention a name. Crude — it reads the IL as bytes and looks for
    /// the metadata token's target through the type's referenced members — but it catches the thing
    /// that matters: a call site.
    /// </summary>
    static bool ReferencesInIl(Type type, string typeName)
    {
        foreach (var method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var body = method.GetMethodBody();
            if (body is null) continue;

            var il = body.GetILAsByteArray();
            if (il is null) continue;

            // Every call token is 4 bytes following an opcode; resolving each is the reliable read.
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] is not (0x28 or 0x6F or 0x73)) continue; // call, callvirt, newobj
                var token = BitConverter.ToInt32(il, i + 1);
                try
                {
                    var target = type.Module.ResolveMethod(token);
                    if (target?.DeclaringType?.Name == typeName) return true;
                }
                catch (ArgumentException) { /* not a method token at this offset */ }
            }
        }

        return false;
    }
}
