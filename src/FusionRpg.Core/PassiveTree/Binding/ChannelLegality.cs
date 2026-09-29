using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.PassiveTree.Binding;

/// <summary>
/// The thirteen-`UnitClass` verdict table, centralized (task D1, spec-tree-binder.md §4.1, §4.2 item
/// 3, §6 M3). One place that answers "does a node atom targeting this channel, with this scaleAxis
/// and this op, bind" — so the rule is never re-derived a second time the way R4's `tierWeight`
/// duplication happened once already.
///
/// <para><b>Counted, not asserted:</b> of the thirteen <see cref="UnitClass"/> values, three accept a
/// ladder-scaled `+X` (<see cref="Verdict.LadderScaled"/>), a fourth accepts a flat per-mille `+X` but
/// never a ladder-scaled one (<see cref="Verdict.FlatPermilleOnly"/>), three accept a `Θ`-linear point
/// grant and never a `P(Θ)` one (<see cref="Verdict.ThetaLinear"/>), and six refuse outright
/// (<see cref="Verdict.Refused"/>). 3 + 1 + 3 + 6 = 13 — <c>ChannelLegalityTests</c> proves this by
/// enumerating <c>Enum.GetValues&lt;UnitClass&gt;()</c>, never by asserting the bare count, so a
/// fourteenth class added to <c>UnitClass</c> without a verdict here fails loudly
/// (<see cref="VerdictFor"/>'s default arm throws) instead of silently falling through.</para>
///
/// <para><b>Relationship to <see cref="PassiveTreeCatalogLoader"/>'s own inline check.</b> C3 already
/// shipped an axis/`UnitClass` agreement refusal inside `LoadAtom` (the outright-refuse set, and the
/// expected-axis switch) with its own 34 passing tests. This class is the same rule, generalized and
/// independently testable — it does not replace that check and the catalog loader is left untouched
/// by this task, so C3's tests keep passing unmodified.</para>
/// </summary>
public static class ChannelLegality
{
    /// <summary>What a `+X` node atom is allowed to do against a channel of a given <see cref="UnitClass"/>.</summary>
    public enum Verdict
    {
        /// <summary><see cref="UnitClass.GameUnits"/>, <see cref="UnitClass.GameUnitsPerSecond"/>,
        /// <see cref="UnitClass.ReciprocalPoints"/> — binds `kMicro · P(Θ_node) / 1e6`, scaleAxis
        /// <see cref="ScaleAxis.PTheta"/>. The canonical case.</summary>
        LadderScaled,

        /// <summary><see cref="UnitClass.PerMilleRatio"/> — flat per-mille points planned against the
        /// clamp, scaleAxis <see cref="ScaleAxis.FlatPermille"/>. Never ladder- or `Θ`-scaled: it is a
        /// bounded ratio and `P(Θ)`-scaling saturates it in a few tiers, killing the soul track on
        /// that node (§4.1's `PerMilleRatio` row).</summary>
        FlatPermilleOnly,

        /// <summary><see cref="UnitClass.SigmoidPoints"/>, <see cref="UnitClass.SigmoidMultiplierPoints"/>,
        /// <see cref="UnitClass.StatusPotencyPoints"/> — binds `kMicro · Θ_node / 1e6`, scaleAxis
        /// <see cref="ScaleAxis.Theta"/>. A `P(Θ)` (PTheta-axis) amount is a design error (PS-1/PS-3):
        /// `contentScale` never touches a rate input and contests read `Θ`, linear.</summary>
        ThetaLinear,

        /// <summary><see cref="UnitClass.Milliseconds"/>, <see cref="UnitClass.Count"/>,
        /// <see cref="UnitClass.Flag"/>, <see cref="UnitClass.LadderIndex"/>,
        /// <see cref="UnitClass.AptitudePoints"/>, <see cref="UnitClass.LoamUnits"/> — refused
        /// outright as a magnitude target, regardless of scaleAxis (§4.1's six-class refuse row).</summary>
        Refused,
    }

    /// <summary>Five primary channels where lower is better (`ModifierOp.DirectionOf`,
    /// `ModifierOp.cs:94,98`, counted at five per §4.2 item 3, superseding the predecessor research's
    /// count of one). A "+X" node against any of these is a self-nerf the cost function correctly
    /// prices as negative while the node's own copy reads as a buff.</summary>
    public static readonly IReadOnlyList<string> LowerIsBetterPrimaries = new[]
    {
        StatChannels.AttackInterval, StatChannels.ProduceInterval, StatChannels.AttackCountdown,
        StatChannels.ProduceCountdown, StatChannels.TakeDmgMultiplier,
    };

    /// <summary>§6 M3, named for the message this rule's own refusal carries: "there is no `More` on
    /// the derived side" (`AtomKindRegistry.cs:583`, `AtomDerivedSubsystem.TryParseOp`). Derived ops
    /// are `Flat | Increased | Replace | Flag`.
    ///
    /// <para>Enforced at TWO named sites, never left to the enum's shape (task P4.2): the catalog
    /// loader's own M3 arm (<c>PassiveTreeCatalogLoader.LoadAtom</c>), and
    /// <see cref="CheckNoMoreOnDerived"/>, reached from <see cref="CheckBind"/> on the bind path.
    /// Before P4.2 the rule was implicit — <see cref="NodeAtomOp"/> had no `More` member, so
    /// `Enum.TryParse` failed for a derived `"op": "more"` row. Adding the member (needed because
    /// `stat.modify` DOES support `more`, and both kinds share this enum) made the implicit refusal
    /// unavailable, so it is now explicit at both sites; a test in each home proves it stays loud.</para>
    /// </summary>
    public const string NoMoreOnDerivedRuleName = "M3";

    /// <summary>Maps a <see cref="UnitClass"/> to its verdict (§4.1's rule table). Enumerable by
    /// design: every one of the thirteen values must appear in exactly one arm below, and the default
    /// arm throws rather than silently defaulting a fourteenth class to <see cref="Verdict.Refused"/>
    /// — a class added to <c>UnitClass</c> without a verdict here must fail loudly, the same discipline
    /// <c>UnitClassContractParityTests</c> already applies to the TypeScript union.</summary>
    public static Verdict VerdictFor(UnitClass unitClass) => unitClass switch
    {
        UnitClass.GameUnits or UnitClass.GameUnitsPerSecond or UnitClass.ReciprocalPoints => Verdict.LadderScaled,
        UnitClass.PerMilleRatio => Verdict.FlatPermilleOnly,
        UnitClass.SigmoidPoints or UnitClass.SigmoidMultiplierPoints or UnitClass.StatusPotencyPoints => Verdict.ThetaLinear,
        UnitClass.Milliseconds or UnitClass.Count or UnitClass.Flag or UnitClass.LadderIndex
            or UnitClass.AptitudePoints or UnitClass.LoamUnits => Verdict.Refused,
        _ => throw new ArgumentOutOfRangeException(nameof(unitClass), unitClass,
            "ChannelLegality: a UnitClass with no verdict — spec-tree-binder.md §4.1 must name its " +
            "row before this switch can bind it; never fall through to a default"),
    };

    /// <summary>
    /// §6 <b>M3</b> — the derived-side `More` refusal, named and enforced explicitly (task P4.2).
    ///
    /// <para>Before P4.2 this rule was enforced by <see cref="NodeAtomOp"/> simply having no `More`
    /// member, so `Enum.TryParse` failed at catalog load. That is correct for `stat.derived` but wrong
    /// for `stat.modify`, which shares the same enum and legitimately supports `more`
    /// (`AtomKindRegistry.cs:517`) — the shared enum refused 80 real primary nodes. Adding the member
    /// without this check would silently convert a loud load-time refusal into the silent drop
    /// <c>TreeAtomSource.BoundAtomsFor</c> performs when `AtomDerivedSubsystem.TryParseOp` fails: the
    /// node would bind, report as a contribution, and apply nothing. M3 must stay a NAMED refusal at
    /// both the load path and the bind path, so this is that check — called from
    /// <see cref="CheckBind"/>, which both sites already go through.</para>
    /// </summary>
    static void CheckNoMoreOnDerived(NodeAtom atom)
    {
        if (atom.Op != NodeAtomOp.More) return;
        if (!string.Equals(atom.KindId, "stat.derived", StringComparison.Ordinal)) return;

        throw new BindRefusal(
            $"channel '{atom.ChannelId}': op '{atom.Op}' is not one of Flat|Increased|Replace|Flag on " +
            "stat.derived (§6 M3 -- there is no More on the derived side; AtomDerivedSubsystem.TryParseOp " +
            "has no 'more' arm, so this atom would bind and apply nothing)");
    }

    /// <summary>The scaleAxis a bindable (non-<see cref="Verdict.Refused"/>) verdict requires.
    /// <c>null</c> for <see cref="Verdict.Refused"/>, which accepts no axis at all.</summary>
    public static ScaleAxis? ExpectedAxis(UnitClass unitClass) => VerdictFor(unitClass) switch
    {
        Verdict.LadderScaled => ScaleAxis.PTheta,
        Verdict.FlatPermilleOnly => ScaleAxis.FlatPermille,
        Verdict.ThetaLinear => ScaleAxis.Theta,
        _ => null,
    };

    /// <summary>
    /// The full bind-time legality check for one node atom (§4.1, §4.2 item 3). Refuses, in order:
    /// (1) one of the six outright-refuse classes, regardless of axis; (2) an axis that disagrees with
    /// the class's one legal axis — the same silent-failure pairing the catalog loader's own inline
    /// check already catches, generalized here so it is independently testable by enumeration; (3) a
    /// `Flat`/`Increased` "+X" grant against one of the five <see cref="LowerIsBetterPrimaries"/> —
    /// this schema's `kMicro` is always a non-negative share of budget, so there is no way to author
    /// the "-X" such a channel would actually need, and a "+X" against it is a self-nerf by
    /// construction, not merely by an unlucky sign.
    /// </summary>
    public static void CheckBind(NodeAtom atom)
    {
        CheckNoMoreOnDerived(atom); // §6 M3, first: a derived `More` is refused before any axis/class
                                    // reasoning, so the message names the real rule, not a downstream one

        var verdict = VerdictFor(atom.UnitClass);
        if (verdict == Verdict.Refused)
            throw new BindRefusal(
                $"channel '{atom.ChannelId}': unitClass '{atom.UnitClass}' is refused as a magnitude " +
                "target outright (§4.1's six-class refuse set: Milliseconds, Count, Flag, LadderIndex, " +
                "AptitudePoints, LoamUnits)");

        var expected = ExpectedAxis(atom.UnitClass)!.Value;
        if (atom.ScaleAxis != expected)
        {
            var rule = verdict switch
            {
                Verdict.FlatPermilleOnly => "binds flat per-mille only, never ladder- or Θ-scaled",
                Verdict.ThetaLinear => "binds Θ-linear only — a P(Θ) amount is refused",
                _ => "must carry a ladder-scaled (PTheta) amount",
            };
            throw new BindRefusal(
                $"channel '{atom.ChannelId}': unitClass '{atom.UnitClass}' {rule}, got scaleAxis " +
                $"'{atom.ScaleAxis}' (§4.1, class named)");
        }

        if ((atom.Op == NodeAtomOp.Flat || atom.Op == NodeAtomOp.Increased) && StatChannels.IsLowerBetter(atom.ChannelId))
            throw new BindRefusal(
                $"channel '{atom.ChannelId}' is LowerIsBetter — a {atom.Op} '+X' node is a self-nerf the " +
                "cost function prices as a penalty while the node's own copy reads as a buff (§4.2 item " +
                "3); this schema's amount is always a non-negative share and has no way to author the " +
                "'-X' this channel actually needs");
    }
}
