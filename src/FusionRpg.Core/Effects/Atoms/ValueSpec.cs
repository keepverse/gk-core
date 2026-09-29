using System.Linq;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// When a number resolves. Three policies for the four roll moments — moment 3 (bind / equip)
/// deliberately has none, because binding never rolls: if a value would change at equip, it is
/// <see cref="OnApply"/>.
/// </summary>
public enum RollPolicy
{
    /// <summary>Never resolves — the number is the number. <c>Min == Max</c>.</summary>
    Fixed = 0,

    /// <summary>Moment 2 — the item drops and the value freezes forever, off the instance roll seed.</summary>
    OnInstantiate,

    /// <summary>Moment 4 — resolves every time the atom applies. `100-200 fire damage on hit`.</summary>
    OnApply,
}

/// <summary>
/// Every number an atom can carry. Anywhere a magnitude could go in an atom's params, a ValueSpec
/// can go.
///
/// <para><b>Units are not interchangeable.</b> <c>+10 hp</c> is ten hit points; <c>+10 fire power</c>
/// is ten <i>resolver points</i> on a sigmoid scale where CritRateScale is 100.0, so ten points is
/// 0.1 sigmoid units. Tier bands are authored per channel family and never copied across — see
/// definitions.md §2 and E9's normalize(magnitude, referenceScale), which exists precisely so a
/// coefficient table does not price these two alike and land an order of magnitude out.</para>
///
/// <para>Integer throughout: magnitudes are int, curve multipliers are per-mille, and interpolation
/// rounds half away from zero exactly once.</para>
/// </summary>
/// <param name="Min">Inclusive lower bound.</param>
/// <param name="Max">Inclusive upper bound.</param>
/// <param name="Roll">When the value resolves.</param>
/// <param name="CurveId">Optional <c>effect_curve</c> row scaling Min and Max before any roll.</param>
/// <param name="EventField">
/// `P0.2` (spec-value-spec-and-curve.md "Event-linked magnitudes", landed 2026-08-28): GAS's
/// `SetByCaller` shape — read a field the FIRING event already carries (`ev.Damage`) instead of
/// rolling Min/Max. <b>Closed to <c>"damage"</c> today.</b> Mutually exclusive with
/// Min/Max/Roll/CurveId — an authored spec picks one source, never both. Never resolved through
/// <see cref="Resolve"/>: no caller of that method has a firing event in scope (traced, not assumed —
/// see the spec section), so this resolves downstream instead, in
/// `AtomCompiler.ResolvedParams`/`DamagePacketBuilder.FromOverlay`.
/// </param>
/// <param name="MultiplierMilli">
/// Per-mille multiplier applied to the event field's value — the balance number ("500" = 50%
/// lifesteal). <b>Required whenever <paramref name="EventField"/> is set</b> (enforced in
/// <see cref="AtomJson.TryReadValueSpec"/>, never silently defaulted) since it is authored content,
/// not a structural constant; unused and ignored otherwise.
/// </param>
/// <param name="PowerLadder">
/// T6.2 (`patron-absorption`, `tasks/seed-to-concrete-open-decisions.md` §2, owner-approved
/// 2026-09-02): the same "a magnitude needs something outside <see cref="Resolve"/>'s own scope"
/// shape <see cref="EventField"/> already solved for firing-event fields, applied here to an
/// owner's own power index — read <c>PowerLadder.Value(Θ)</c> (`Power/PowerLadder.cs`, the one
/// shared ladder every magnitude in this codebase reads) instead of rolling Min/Max. Mutually
/// exclusive with Min/Max/Roll/CurveId/EventField. Unlike <see cref="EventField"/>, Θ is known at
/// COMPILE time (an owner's own power index, not something a hit produces), so this resolves in
/// <c>AtomCompiler.ResolvedParams</c> directly, never deferred to an apply-time consumer.
/// </param>
/// <param name="PowerLadderKMilli">
/// Per-mille multiplier applied to <c>PowerLadder.Value(Θ)</c> — the balance number, matching
/// <c>PatronPolicy.PThetaKMilli</c>'s own role. <b>Required whenever <paramref name="PowerLadder"/>
/// is set</b> (enforced in <see cref="AtomJson.TryReadValueSpec"/>, never silently defaulted);
/// unused and ignored otherwise.
/// </param>
/// <param name="PowerLadderKMicro">
/// task B3 (spec-tree-binder.md §3.5, §7): a per-million sibling of <paramref name="PowerLadderKMilli"/>,
/// for the one caller a per-mille coefficient is too coarse for — `tree-binder`'s `kMicro`
/// (spec-tree-catalog.md §2.3). At per-mille, `gated-deep` stores `kMilli = 0` for 12 of 40 nodes
/// (silently inert, in the shallow tiers every build buys first); at per-million the same worst
/// case is 0.04% error. Constructed programmatically by `tree-binder`, never authored as JSON
/// (`AtomJson.TryReadValueSpec` is untouched — this field has no grammar branch there), so
/// <see cref="PowerLadderKMilli"/>'s existing authored-content consumers are completely unaffected.
/// Non-zero selects this field over <see cref="PowerLadderKMilli"/> in <c>AtomCompiler</c>.
/// </param>
/// <param name="ClampedLevelScale">
/// T6.2's own second gap, found while resuming it: `AuraMilli`'s flat part is
/// <c>clamp(base + level, 0, cap)</c> — a clamp has no home in the closed FA1 op vocabulary
/// (`AtomRowValidator.StatOps`/`DerivedOps` — no cap/min/max op exists) or in any channel-level
/// policy. Owner-approved as "a new FA1 op" (2026-09-02), then found not to need one: `level` is
/// the only true per-owner runtime input here (rarity/star are fixed per authored container), and
/// it is already available at compile time — the same <c>ownerLevel</c> parameter curve-scaled
/// values already read. Resolves to <c>Math.Clamp(BaseMilli + ownerLevel, 0, CapMilli)</c> in
/// `AtomCompiler.ResolvedParams`, exactly like <see cref="PowerLadder"/> — no Injector-side change,
/// no new runtime opcode. Mutually exclusive with Min/Max/Roll/CurveId/EventField/PowerLadder.
/// </param>
/// <param name="ClampedLevelScaleBaseMilli">
/// The authored constant — <c>RarityBaseMilli(rarity) + PerStarMilli·star</c> for this container's
/// own (rarity, star). Required whenever <see cref="ClampedLevelScale"/> is set.
/// </param>
/// <param name="ClampedLevelScaleCapMilli">
/// The ceiling — matches <c>PatronPolicy.AuraClampMilli</c>'s own role. Required whenever
/// <see cref="ClampedLevelScale"/> is set, never silently defaulted.
/// </param>
/// <param name="ExternalRef">
/// `patron-absorption` (`spec-patron-absorption.md`'s own 2026-09-06 amendment) — the "referenced,
/// not re-expressed" shape that spec decided on: some magnitudes are not safely RE-DERIVABLE as an
/// atom formula at all, because the real value is per-player-varying and already computed by a
/// SHIPPED function elsewhere (`PatronPolicy.AuraMilli`) — reproducing it as composed
/// <see cref="PowerLadder"/>/<see cref="ClampedLevelScale"/> atoms would satisfy the letter of "an
/// atom carries the number" while leaving TWO independently-expressed formulas that are only
/// PROVABLY equal by a sweep, never by construction, and can drift the moment either is tuned alone.
/// A closed, literal, reviewed string id (never a free-form expression or a reflected method name),
/// resolved by <c>AtomCompiler.ResolvedParams</c> via a caller-supplied
/// <c>externalRefs: Func&lt;string, long&gt;?</c> callback — the exact same shape
/// <c>curves: Func&lt;string, CurveTable?&gt;</c> already has, so <c>AtomCompiler</c> itself never
/// imports <c>PatronPolicy</c> or anything domain-specific; it only ever invokes whatever callback it
/// was handed. Mutually exclusive with every other `ValueSpec` shape. Compiling an atom that carries
/// this marker with no callback supplied (or an id the callback does not recognise) throws, naming
/// the ref id — never silently prices at zero, matching every other marker's own rule.
/// </param>
public readonly record struct ValueSpec(
    int Min, int Max, RollPolicy Roll, string? CurveId = null,
    string? EventField = null, int MultiplierMilli = 1000,
    bool PowerLadder = false, int PowerLadderKMilli = 0, long PowerLadderKMicro = 0,
    bool ClampedLevelScale = false, int ClampedLevelScaleBaseMilli = 0, int ClampedLevelScaleCapMilli = 0,
    string? ExternalRef = null)
{
    /// <summary>The closed set of fields an event-linked spec may read. One member today.</summary>
    public static readonly IReadOnlyCollection<string> EventFields = new[] { "damage" };

    /// <summary>A single number that never rolls.</summary>
    public static ValueSpec Of(int value) => new(value, value, RollPolicy.Fixed);

    /// <summary>An inclusive range rolled on every apply.</summary>
    public static ValueSpec Range(int min, int max) => new(min, max, RollPolicy.OnApply);

    public bool IsFixed => Roll == RollPolicy.Fixed;

    /// <summary>
    /// Shape validation only. An unknown <see cref="CurveId"/> is E4's rejection at load, not this
    /// module's — one owner, not two.
    /// </summary>
    public AtomRejection Validate()
    {
        if (EventField is not null)
        {
            if (!EventFields.Contains(EventField))
                return AtomRejection.Fail(AtomRejectionReason.BadValueSpec,
                    $"unknown eventField '{EventField}' — the closed set is: {string.Join(", ", EventFields)}");

            if (Min != 0 || Max != 0 || Roll != RollPolicy.Fixed || CurveId is not null || PowerLadder || ClampedLevelScale || ExternalRef is not null)
                return AtomRejection.Fail(AtomRejectionReason.BadValueSpec,
                    "eventField is exclusive of min/max/roll/curve/powerLadder/clampedLevelScale/externalRef — author only " +
                    "{\"eventField\": ..., \"multiplierMilli\": ...}");

            return AtomRejection.Ok;
        }

        if (PowerLadder)
        {
            if (Min != 0 || Max != 0 || Roll != RollPolicy.Fixed || CurveId is not null || ClampedLevelScale || ExternalRef is not null)
                return AtomRejection.Fail(AtomRejectionReason.BadValueSpec,
                    "powerLadder is exclusive of min/max/roll/curve/eventField/clampedLevelScale/externalRef — author only " +
                    "{\"powerLadder\": true, \"kMilli\": ...}");

            return AtomRejection.Ok;
        }

        if (ClampedLevelScale)
        {
            if (Min != 0 || Max != 0 || Roll != RollPolicy.Fixed || CurveId is not null || ExternalRef is not null)
                return AtomRejection.Fail(AtomRejectionReason.BadValueSpec,
                    "clampedLevelScale is exclusive of min/max/roll/curve/eventField/powerLadder/externalRef — author only " +
                    "{\"clampedLevelScale\": true, \"baseMilli\": ..., \"capMilli\": ...}");
            if (ClampedLevelScaleCapMilli < 0)
                return AtomRejection.Fail(AtomRejectionReason.BadValueSpec,
                    $"clampedLevelScale capMilli must be >= 0, got {ClampedLevelScaleCapMilli}");

            return AtomRejection.Ok;
        }

        if (ExternalRef is not null)
        {
            if (ExternalRef.Length == 0)
                return AtomRejection.Fail(AtomRejectionReason.BadValueSpec, "externalRef must not be empty");
            if (Min != 0 || Max != 0 || Roll != RollPolicy.Fixed || CurveId is not null)
                return AtomRejection.Fail(AtomRejectionReason.BadValueSpec,
                    "externalRef is exclusive of min/max/roll/curve/eventField/powerLadder/clampedLevelScale — author only " +
                    "{\"externalRef\": \"...\"}");

            return AtomRejection.Ok;
        }

        if (Min > Max)
            return AtomRejection.Fail(AtomRejectionReason.BadValueSpec, $"min {Min} > max {Max}");

        // "fixed" means one number. A fixed spec with a spread is an authoring mistake that would
        // otherwise silently resolve to Min forever.
        if (Roll == RollPolicy.Fixed && Min != Max)
            return AtomRejection.Fail(AtomRejectionReason.BadValueSpec,
                $"fixed value spec has a range [{Min}, {Max}] — use OnInstantiate or OnApply");

        if (CurveId is { Length: 0 })
            return AtomRejection.Fail(AtomRejectionReason.BadCurve, "curveId is empty");

        return AtomRejection.Ok;
    }

    /// <summary>
    /// Resolve to a number. <paramref name="rng"/> is required only for rolling policies; a Fixed
    /// spec never touches it, so a caller with no stream can still resolve constants.
    /// </summary>
    public int Resolve(IAtomRandom? rng)
    {
        if (Min == Max) return Min;

        if (Roll == RollPolicy.Fixed)
            return Min; // validation rejects this shape; resolve stays total rather than throwing hot.

        if (rng is null)
            throw new InvalidOperationException(
                $"ValueSpec [{Min}, {Max}] with roll {Roll} needs a stream; none was supplied.");

        return rng.NextInclusive(Min, Max);
    }

    /// <summary>
    /// Apply a curve multiplier to both bounds <b>before</b> any roll, so the inclusive-bounds
    /// guarantee still holds after scaling.
    /// </summary>
    public ValueSpec Scaled(int multiplierMilli) =>
        this with
        {
            Min = CurveTable.ApplyMilli(Min, multiplierMilli),
            Max = CurveTable.ApplyMilli(Max, multiplierMilli),
        };
}
