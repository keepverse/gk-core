using System.Text.Json;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// <c>JsonOverlay</c> had no direct test coverage until this — every other suite exercised it only
/// indirectly, through a grant overlay or a wire round-trip. Found while working E28 (2026-09-03): its
/// <c>Unwrap</c> had the same defect as <c>AtomCompiler.Plain</c> (a sibling, independently-written
/// unwrapper) — a number's ternary, <c>TryGetInt64(out var l) ? l : el.GetDouble()</c>, has ALWAYS
/// produced a boxed <c>double</c>, never <c>long</c>, regardless of which branch ran. The <c>?:</c>
/// operator resolves one static type for both branches before boxing to <c>object?</c>, and since
/// <c>long</c> widens implicitly to <c>double</c> but not the reverse, the compiler silently converted
/// every integer to a double. Harmless for anything read back through <c>Convert.ToInt32/ToInt64</c>
/// (which tolerate a boxed double), but a real type defect against docs/architecture/numeric-types.md's own overflow table —
/// <c>double</c> loses exact-integer precision above 2^53 and is documented as non-deterministic
/// across runtimes in a hashed/persisted path, where <c>long</c> was always the intended type.
/// </summary>
public class JsonOverlayTests
{
    static Dictionary<string, object?> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

    [Fact]
    public void FromObject_preserves_a_whole_number_as_long_not_double()
    {
        var raw = Parse("{\"amount\":150}");

        var unwrapped = JsonOverlay.FromObject(raw);

        Assert.IsType<long>(unwrapped["amount"]);
        Assert.Equal(150L, unwrapped["amount"]);
    }

    [Fact]
    public void FromObject_still_produces_a_double_for_a_genuinely_fractional_number()
    {
        var raw = Parse("{\"scale\":1.5}");

        var unwrapped = JsonOverlay.FromObject(raw);

        Assert.IsType<double>(unwrapped["scale"]);
        Assert.Equal(1.5, unwrapped["scale"]);
    }

    [Fact]
    public void FromObject_preserves_whole_numbers_inside_a_nested_array_of_objects()
    {
        var raw = Parse("{\"cells\":[{\"row\":1,\"col\":2}]}");

        var unwrapped = JsonOverlay.FromObject(raw);

        var cells = Assert.IsType<List<object?>>(unwrapped["cells"]);
        var first = Assert.IsType<Dictionary<string, object?>>(cells[0]);
        Assert.IsType<long>(first["row"]);
        Assert.Equal(1L, first["row"]);
    }

    [Fact]
    public void GetInt_still_reads_a_long_value_correctly()
    {
        // The pre-fix shape (a boxed double) never actually broke JsonOverlay.GetInt — Convert.ToInt32
        // tolerates it. This pins that the FIXED shape (a boxed long) reads back identically, so the
        // fix is provably behaviour-preserving for every existing GetInt call site.
        var raw = Parse("{\"amount\":150}");
        var unwrapped = JsonOverlay.FromObject(raw);

        Assert.Equal(150, JsonOverlay.GetInt(unwrapped, "amount"));
    }
}
