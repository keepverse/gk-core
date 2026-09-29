using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Display;

/// <summary>
/// G3 §2.1's projection (spec-item-card.md) — the ONLY line producer. Pure: no Unity value, no
/// browser, testable with the game closed (SC8). Three levels build on this one: Line here, Card and
/// Compare are ordered collections of what this produces — there is no second path to a line.
/// </summary>
public static class ItemDisplayRenderer
{
    // ---- Rule 1 — the shipped percent conversion, adopted, not reinvented -------------------------
    // gk-web/web/fusion-rpg-web/src/features/creatures/patronView.ts:23 (verified): (milli/10).toFixed(1),
    // trailing ".0" stripped. One convention; patronView is asked to call this instead of owning it.
    /// <remarks><c>long</c>, not <c>int</c> (widened 2026-09-06): a per-mille magnitude is something
    /// <c>contentScale</c> can touch, and AGENTS.md's rule is <c>long</c> for every magnitude. It was
    /// <c>int</c>, which forced a <c>checked((int))</c> narrowing at both call sites in
    /// <see cref="FormatValue"/> — a throw is the correct failure, but not needing one is better. The
    /// <c>double</c> below is a FORMATTING step at the display boundary, not arithmetic on a
    /// magnitude: it is the shipped <c>patronView</c> conversion adopted verbatim (Rule 1), it feeds
    /// nothing back, and Rule 3 forbids it feeding back.</remarks>
    public static string FormatPerMille(long milli)
    {
        var tenths = milli / 10.0;
        var text = tenths.ToString("F1");
        return (text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text) + "%";
    }

    /// <summary>Rule 2: never render a non-zero per-mille as 0% — round away from zero, the direction
    /// the engine itself uses (<c>CurveTable.DivRoundHalfAway</c>).</summary>
    public static long RoundAwayFromZero(long numerator, long denominator) =>
        CurveTable.DivRoundHalfAway(numerator, denominator);

    /// <remarks><c>long</c> for the same reason <see cref="FormatPerMille"/> is.</remarks>
    public static string FormatMilliseconds(long ms) =>
        ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:F1} s";

    /// <summary>Rule P for a sigmoid CONTEXT read (not the power scalar, which is module 9's own
    /// `CardPower`) — the sigmoid's shift from its neutral (50%) read, in percentage points, one
    /// decimal, with the "this is approximate" marker. `deltaPoints / scale` is the sigmoid argument
    /// (spec-magnitude-and-units.md §4.1: `p = 1/(1 + e^(−delta/100))`), so `+150 crit rate` reads
    /// `≈ 31.8 pp` — the worked example §14 D.1's card row pins (`≈ +31.8 pp vs neutral`), and the
    /// value `gk-web/web/fusion-rpg-web/src/i18n/magnitude.ts`'s `formatSigmoidContext` already produces.
    /// combat-math-dedup T17: this side was `deltaPoints / scale` (not a sigmoid at all). The
    /// reference label and sign glyph the web surface adds remain that surface's; this helper is the
    /// shared numeric read only.</summary>
    public static string FormatSigmoidContext(int deltaPoints, double scale) =>
        $"≈ {((CombatProbability.Sigmoid(deltaPoints, scale) - CombatProbability.Sigmoid(0, scale)) * 100):F1} pp";

    /// <summary>
    /// The roll-quality bar. Only <see cref="RollPolicy.OnInstantiate"/> gets one — `Fixed` has no
    /// luck to show, `OnApply` shows the band the hit rolled, not the item's own luck.
    /// </summary>
    public static RollBar? BarFor(RollPolicy roll, int qualityPerMille) => roll switch
    {
        RollPolicy.Fixed => null,
        RollPolicy.OnApply => null,
        RollPolicy.OnInstantiate => new RollBar(
            Math.Clamp((qualityPerMille * RollBar.MaxSegments + 999) / 1000, 1, RollBar.MaxSegments)),
        _ => null,
    };

    /// <summary>
    /// Produce one line. <paramref name="frozenValue"/> is the integer already in
    /// <c>effect_instance_atom.values_json</c> — this NEVER re-applies a curve or re-rolls (Rule 3).
    /// Never a tier number, an atom id, a family id, or a name band in <paramref name="args"/> (§2.4).
    ///
    /// <para><b><paramref name="frozenValue"/> and <paramref name="unit"/> are nullable, and the two
    /// nulls are checked, not tolerated.</b> Four of the shipped 98 templates name no
    /// <c>{value}</c> at all (<c>atom.elpw-attune</c>, <c>atom.ward-brace</c>,
    /// <c>atom.prec-truesight</c>, <c>atom.sust-freshgraft</c>) — a line with nothing to quantify has
    /// no unit either, and demanding one would force a caller to invent a currency for a sentence
    /// that carries no number. The converse is refused outright: a template that DOES name
    /// <c>{value}</c> with no value or no unit throws, which is
    /// <see cref="DisplayRules.UnrenderedMagnitude"/> and <see cref="DisplayRules.MissingUnitClass"/>
    /// enforced at the render boundary as well as at content-validation time.</para>
    /// </summary>
    /// <param name="bandMax">
    /// §3.4's <c>OnApply</c> arm: <b>the band, not a value</b> — <c>100–200 fire damage on hit</c>. An
    /// <c>OnApply</c> spec is deliberately left unresolved by <c>Instantiator.Freeze</c> (the hit rolls
    /// it, not the item, and <c>ssot-affixes.md:422</c> makes that the corpus's own default), so there
    /// is no single number to show and showing one would be a lie about what the item does. Supply the
    /// band's upper bound here and <paramref name="frozenValue"/> as its lower; ignored for every other
    /// policy, where a band does not exist.
    /// </param>
    public static DisplayLine Line(
        DisplayTemplateRow template, AtomRow atom, string frame, long? frozenValue,
        SourceKind sourceKind, int groupOrder, UnitClass? unit, string? elementVariant = null,
        RollPolicy roll = RollPolicy.Fixed, int qualityPerMille = 1000, string? contextRead = null,
        long? bandMax = null)
    {
        if (template.Status != "live")
            throw new DisplayTemplateRejection(
                $"'{template.RuntimeFamily}' has no live display template (status='{template.Status}') "
                + "-- rendering it would show a status the content is not ready to show");

        var body = frame == "plant" && template.PlantOverrideTemplate is not null
            ? template.PlantOverrideTemplate
            : template.Template;
        var wantsValue = DisplayTemplates.PlaceholdersOf(body)
            .Contains(DisplayContentRules.MagnitudePlaceholder, StringComparer.Ordinal);

        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wantsValue)
        {
            if (unit is null)
                throw new DisplayTemplateRejection(
                    $"'{template.RuntimeFamily}' renders a magnitude but no unit class resolved "
                    + $"({DisplayRules.MissingUnitClass}) -- a bare number is indistinguishable from a "
                    + "different currency");
            if (frozenValue is null)
                throw new DisplayTemplateRejection(
                    $"'{template.RuntimeFamily}' names '{{value}}' but the instance froze no magnitude "
                    + $"for it ({DisplayRules.UnrenderedMagnitude})");

            args["value"] = roll == RollPolicy.OnApply && bandMax is { } hi && hi != frozenValue.Value
                ? FormatBand(unit.Value, frozenValue.Value, hi)
                : FormatValue(unit.Value, frozenValue.Value);
        }
        if (elementVariant is not null) args["element"] = elementVariant;

        var rendered = DisplayTemplates.Render(template, frame, args);

        return new DisplayLine(
            Key: template.NameKey,
            Args: new Dictionary<string, string>(args) { ["__rendered"] = rendered },
            Unit: unit,
            SourceKind: sourceKind,
            GroupOrder: groupOrder,
            RollBar: BarFor(roll, qualityPerMille),
            ContextRead: contextRead,
            // Only OnInstantiate has a roll quality at all. `Fixed` never rolled; `OnApply` has a
            // BAND, and "where in the band did this land" is a question about a hit that has not
            // happened yet — reporting 1000‰ there (which is what an unreadable band degrades to)
            // would put a full-luck number on a line that has no luck. Corrected 2026-09-06 while
            // wiring the Card level; the bar already followed this rule, the field did not.
            RollQualityPerMille: roll == RollPolicy.OnInstantiate ? qualityPerMille : null);
    }

    /// <summary>
    /// One <c>OnApply</c> band, both bounds under the same unit's own Rule-P precision. The separator
    /// is an EN DASH (U+2013), the typographic range mark — a hyphen reads as a minus sign next to a
    /// signed magnitude, which is the one place this could actually mislead.
    /// </summary>
    public static string FormatBand(UnitClass unit, long min, long max) =>
        min == max ? FormatValue(unit, min) : FormatValue(unit, min) + "–" + FormatValue(unit, max);

    /// <summary>Rule P by unit class — precision never exceeds the source's claimed accuracy.
    /// Frozen integer -> exact (GameUnits/Count/GameUnitsPerSecond); per-mille -> one decimal;
    /// ms -> formatted duration; sigmoid/status/reciprocal -> one decimal, approximate.</summary>
    static string FormatValue(UnitClass unit, long value) => unit switch
    {
        UnitClass.GameUnits or UnitClass.GameUnitsPerSecond or UnitClass.Count or UnitClass.LadderIndex => value.ToString(),
        UnitClass.PerMilleRatio => FormatPerMille(value),
        UnitClass.Milliseconds => FormatMilliseconds(value),
        UnitClass.Flag => value != 0 ? "" : throw new DisplayTemplateRejection("a Flag unit atom rendered a line but is not set"),
        UnitClass.SigmoidPoints or UnitClass.SigmoidMultiplierPoints or UnitClass.StatusPotencyPoints
            or UnitClass.ReciprocalPoints or UnitClass.AptitudePoints => value.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "unhandled unit class"),
    };
}
