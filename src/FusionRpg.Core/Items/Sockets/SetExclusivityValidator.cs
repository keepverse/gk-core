namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// ⛔ <b>D21 — a set piece may not carry a Strain or a Splice.</b>
///
/// <para>Two layers, two axes, and they do not overlap: a set is <i>across items</i> (collect the
/// pieces); a Strain or Splice is <i>within one item</i> (fill its sockets). Letting one item be both
/// stacks two identity mechanisms on one row.</para>
///
/// <para><b>Not a rejection code, and that is the design.</b> Nothing is refused, so nothing needs a
/// code — a combination simply is not satisfied. Minting a reason for a bonus that did not fire would
/// be a code no operator can act on. The three behaviours:</para>
///
/// <list type="table">
/// <item><term>set piece, fill matches a Strain</term><description>the combination does not fire; the
/// inserts stay, and every resonance still fires</description></item>
/// <item><term>set piece, player sockets toward a Strain</term><description><b>allowed</b> — refusing
/// the insert would punish a fill that is legal for resonance</description></item>
/// <item><term>not a set piece</term><description>normal evaluation</description></item>
/// </list>
///
/// <para>⚠ D21's "base rarity: high" row is struck: D15 rules the same day that a set has no rarity
/// and is completed from pieces of any rung. The exclusivity rule stands on its own.</para>
///
/// <para>⚠ Still open and <b>not this module's to close</b>: socket-combination budget versus set
/// budget on one item. It is a budget question and <b>module 9 (`item-power-reads`)</b> owns it — it
/// cannot be answered before the power reads run.</para>
/// </summary>
public static class SetExclusivityValidator
{
    /// <summary>Whether a combination of this shape may fire on this host.</summary>
    public static bool MayFire(SocketHost host, ComboShape shape) =>
        !host.IsSetPiece || !ComboShapes.IsStrainOrSplice(shape);

    /// <summary>
    /// Whether socketing this insert into this host is allowed. <b>Always true</b> — stated as a
    /// method rather than left implicit, because "the set rule refuses the bonus, never the insert"
    /// is exactly the distinction a later reader would otherwise collapse.
    /// </summary>
    public static bool MaySocket(SocketHost host, InsertDef insert) => true;

    /// <summary>
    /// The reason string the socket UI shows beside a suppressed combination. Not a rejection code:
    /// it is display copy, and it exists so the player is told <i>why</i> rather than shown nothing.
    /// </summary>
    public static string SuppressionReason(SocketHost host, ComboShape shape) =>
        MayFire(host, shape)
            ? ""
            : $"'{host.ContainerId}' is a set piece, and a set piece carries no {ComboShapes.Id(shape)} (D21) — " +
              "the inserts stay and every resonance still fires";
}
