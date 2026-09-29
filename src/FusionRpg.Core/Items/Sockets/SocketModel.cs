using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// The six combination shapes, <b>declared in evaluation order</b> (spec-sockets.md §8: "Strains/
/// Splices first, then Pure (highest k per element), then Ring, Eclipse, Diversity"). Ordering the
/// enum is what makes the order a property of the vocabulary rather than of one method's statement
/// sequence — a later shape cannot be slipped in ahead of Strain by writing it earlier in a loop.
/// </summary>
public enum ComboShape
{
    /// <summary>12 aptitudes × 3 archetypes = 36. Module 21's output.</summary>
    Strain = 0,

    /// <summary>C(12,2) = 66 unordered aptitude pairs. Module 21's output.</summary>
    Splice,

    /// <summary>k inserts share one concrete element, k ∈ {2,3,4}. Generated: 6 × 3 = 18.</summary>
    Pure,

    /// <summary>≥1 of each of two elements adjacent on the ring. Generated: 4.</summary>
    Ring,

    /// <summary>≥1 light and ≥1 dark — the mutual counter. Generated: 1.</summary>
    Eclipse,

    /// <summary>3 or 4 <i>distinct</i> elements present. Generated: 2.</summary>
    Diversity,
}

public static class ComboShapes
{
    /// <summary>The <c>shape</c> column's stored spelling.</summary>
    public static string Id(ComboShape shape) => shape switch
    {
        ComboShape.Strain => "strain",
        ComboShape.Splice => "splice",
        ComboShape.Pure => "pure",
        ComboShape.Ring => "ring",
        ComboShape.Eclipse => "eclipse",
        ComboShape.Diversity => "diversity",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    public static bool TryParse(string? id, out ComboShape shape)
    {
        foreach (ComboShape s in Enum.GetValues(typeof(ComboShape)))
            if (string.Equals(Id(s), id, StringComparison.Ordinal)) { shape = s; return true; }
        shape = default;
        return false;
    }

    /// <summary>
    /// The two shapes D21 forbids on a set piece, and the two D20 fixes at four ingredients. Asked
    /// once here rather than spelled as <c>shape is Strain or Splice</c> at five call sites.
    /// </summary>
    public static bool IsStrainOrSplice(ComboShape shape) =>
        shape is ComboShape.Strain or ComboShape.Splice;
}

/// <summary>
/// One row of the gem catalog, as the evaluator needs it. <b>An insert is a fixed container</b>
/// (ssot-sockets.md §4.3) — <c>prefix_rolls = 0 AND suffix_rolls = 0</c>, five tiers, no rolled
/// values — which is why it stacks in the bag as a quantity rather than a row per copy.
/// </summary>
/// <param name="Element">The insert's concrete element id, <c>"omni"</c>, or <c>""</c> for an
/// element-free insert (a vitality gem). <b>An element-free insert contributes to no resonance shape
/// at all</b> — not even Diversity, which counts distinct <i>elements</i>. Said here because its
/// absence from every shape would otherwise read as an oversight.</param>
/// <param name="Unique">A unique-tagged insert may appear at most once per item (<c>DuplicateKey</c>).</param>
public readonly record struct InsertDef(
    string ContainerId, string FamilyId, string Element, int Tier, bool Unique = false);

/// <summary>
/// One socket on one item instance — the <c>item_socket</c> row, which <b>is the SSOT</b> (D2 §6
/// refused ssot-sockets.md §5.2's "materialized view of the operation log" by name).
/// </summary>
/// <param name="Affinity">One concrete element id, or <c>""</c> for none. Declared by the base type
/// at drop, or chosen by the crafter via <c>socket-imbue</c> on a socket <c>socket-add</c> opened.</param>
/// <param name="Crafted">True when this socket was opened by <c>socket-add</c> rather than granted at
/// drop. <b>D24 lets only a crafted, empty socket be imbued</b>, so the flag is load-bearing, not
/// provenance trivia.</param>
public readonly record struct SocketSlot(
    int Index, string Affinity, bool Crafted, string? InsertContainerId = null, string? InsertInstanceId = null)
{
    public bool IsEmpty => string.IsNullOrEmpty(InsertContainerId);
}

/// <summary>One socket joined to the insert filling it — the evaluator's unit of input.</summary>
public readonly record struct SocketFill(int SocketIndex, string SocketAffinity, InsertDef Insert)
{
    /// <summary>
    /// An insert is <b>attuned</b> when its socket declares an affinity and the insert's element
    /// matches it. <c>omni</c> is never an affinity (element-hub-ssot.md §4), so an omni insert is
    /// never attuned — which follows from the affinity vocabulary rather than needing its own arm.
    /// </summary>
    public bool IsAttuned =>
        SocketAffinity.Length > 0 && string.Equals(SocketAffinity, Insert.Element, StringComparison.Ordinal);
}

/// <summary>The host item, as the evaluator reads it. Read-only in every direction.</summary>
/// <param name="IsSetPiece">D21: a set piece may not carry a Strain or a Splice.</param>
/// <param name="Capacity">
/// strain-splice-host SSH1.3 (host-gate §2) — the base type's own <c>socketMax</c>, the ceiling
/// <c>socket-add</c> already caps at (<c>SocketOperations.cs</c>). <b>Not the same axis as
/// <see cref="SocketCount"/></b>: <c>SocketCount</c> is how many sockets are OPENED right now (what an
/// actual fill can ever fill), <c>Capacity</c> is how many this chassis could EVER hold. An unbored
/// chassis (<c>SocketCount</c> 0) with real capacity still reads as reachable, at a real distance —
/// never <c>Undiscovered</c> — because the player can still bore up to it. Defaults to
/// <see cref="int.MaxValue"/> so every pre-existing caller that never named a ceiling keeps behaving
/// exactly as before (no cap it never declared).
/// </param>
public readonly record struct SocketHost(
    string ContainerId, ItemRole Role, string Frame, int SocketCount, bool IsSetPiece = false,
    int Capacity = int.MaxValue)
{
    /// <summary>Thrown at construction, never clamped — an opened count above the chassis's own
    /// ceiling is a caller bug (a corrupt row, or two builders disagreeing), and clamping it would
    /// hide exactly that.</summary>
    public int SocketCount { get; init; } = SocketCount >= 0 && SocketCount <= Capacity
        ? SocketCount
        : throw new ArgumentOutOfRangeException(
            nameof(SocketCount), SocketCount, $"must be within [0, Capacity={Capacity}]");
}

/// <summary>
/// One ingredient requirement, as a <b>multiset entry</b> — D41: "unordered — we only need collect
/// enough type of socket and put it to the item". ⛔ There is deliberately <b>no position field</b>:
/// ssot-sockets.md §5.2's <c>position</c> column is superseded, and a matcher that read
/// <c>bind_ordinal</c> would be a bug.
///
/// <para>⛔ <b>No tier field.</b> strain-splice-host SSH7.8 (spec-tier-ladder §4, owner-approved
/// 2026-09-21): the old <c>MinTier</c> member is gone with the <c>min_tier</c> column. A fill must
/// reach the LADDER's rung floors (<see cref="ComboRecipe.BaseFloors"/>), never a per-ingredient
/// floor — one tier source, not two.</para>
/// </summary>
public readonly record struct ComboIngredient(string FamilyId, int Quantity = 1);

/// <summary>
/// One combination recipe. The 25 resonances are generated (<see cref="ResonanceGenerator"/>); the
/// 102 Strains and Splices are module 21's authored output.
/// </summary>
/// <param name="ComboId">D27: the <c>container_id</c>, prefixed <c>combo.</c> — definitions.md §1
/// forces the prefix to match the kind, so the lane's old <c>gem.combo-*</c> spelling is retired.</param>
/// <param name="HostRole">A role id the host must have, or <c>""</c> for any.</param>
/// <param name="HostFrame">A frame the host must have, or <c>""</c> for any.</param>
/// <param name="BaseTier">The tier the combination grants before attunement's <c>+1</c>.</param>
public sealed record ComboRecipe(
    string ComboId,
    ComboShape Shape,
    string Element,
    int Threshold,
    string HostRole,
    string HostFrame,
    int MinSockets,
    int BaseTier,
    IReadOnlyList<ComboIngredient> Ingredients,
    //: strain-splice-host SSH7.5: rung 1's floors, read from the TUNING at import (never from the
    //: seed — no combination row carries a tier number from SSH7.3 onwards). The matcher uses them as
    //: its no-ladder rung 1, which is what keeps behaviour identical while the corpus sheds the field.
    IReadOnlyList<int>? BaseFloors = null);

/// <summary>One combination that fires, and why.</summary>
/// <param name="EffectiveCount">Pure's contributor count after attunement's <c>+1</c>; the shape's
/// own threshold for the others.</param>
/// <param name="GrantedTier">The recipe's <c>BaseTier</c> plus attunement's <c>+1</c> for a Strain or
/// Splice. <b>Unbounded above</b> — the structural socket ceiling caps the recipe's shape, never its
/// magnitude.</param>
/// <param name="Circuit">The circuit (<c>socketIndex / SocketCircuitSize</c>) this combination
/// fired in. Defaults to 0 for the single-circuit case, so every pre-circuit caller is unchanged.</param>
public readonly record struct CombinationResult(
    string ComboId, ComboShape Shape, int EffectiveCount, int GrantedTier, bool AllAttuned,
    int Circuit = 0);

/// <summary>
/// The socket layer's content-rule namespace. ⛔ <b>Three new members of the closed rejection enum
/// were requested by spec-sockets.md §12 and are deliberately NOT minted.</b>
/// <see cref="AtomRejectionReason.ContentRuleViolated"/>'s own declaration says it is the last member
/// by design — "a caller that wants a new rule registers a namespace, it never mints another code" —
/// and item-ideal.md §2b.1 says the same. The spec predates that hardening; the code wins. The three
/// refusals land as <c>ContentRuleViolated{socket.*}</c>, which keeps every operator fix distinct —
/// §12's actual requirement — with no enum move at all. ⚠ §12's arithmetic is also wrong: the shipped
/// list is 33 + <c>None</c> + <c>ContentRuleViolated</c> = <b>35</b>, not 34.
/// </summary>
public static class SocketRules
{
    public const string Namespace = "socket";

    /// <summary>§12's <c>NotSocketable</c>: the wrong kind of container, or a host with no sockets.</summary>
    public const string NotSocketable = "socket.not-socketable";

    /// <summary>§12's <c>NoFreeSocket</c>: the fix is <i>make room</i>.</summary>
    public const string NoFreeSocket = "socket.no-free-socket";

    /// <summary>§12's <c>SocketOccupied</c>: the fix is <i>remove first</i>.</summary>
    public const string Occupied = "socket.occupied";

    /// <summary>A base type declaring more sockets than its role's ceiling allows.</summary>
    public const string EntryExceedsRoleCeiling = "socket.entry-exceeds-role-ceiling";

    /// <summary>D24: <c>socket-imbue</c> on a socket that is filled, or that was declared at drop.</summary>
    public const string NotImbuable = "socket.not-imbuable";

    /// <summary>
    /// strain-splice-host SSH8.3 (spec-socket-pricing §2): the essence the player pays for must name
    /// the element the socket is being attuned to. The corpus authors one `essence.<element>` line per
    /// imbue row (`recipegen/imbue.py` derives them from the bore rows), so a request whose element
    /// disagrees with the recipe's own essence is a caller naming an affinity the recipe does not buy —
    /// refused by name in this namespace, with no new <see cref="AtomRejectionReason"/> member
    /// (<see cref="Namespace"/>'s own rule above).
    /// </summary>
    public const string ImbueElementMismatch = "socket.imbue-element-mismatch";

    static SocketRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Force the static registration — the idiom every other item lane uses.</summary>
    public static void EnsureRegistered() => System.Runtime.CompilerServices.RuntimeHelpers
        .RunClassConstructor(typeof(SocketRules).TypeHandle);

    public static AtomRejection Violated(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}
