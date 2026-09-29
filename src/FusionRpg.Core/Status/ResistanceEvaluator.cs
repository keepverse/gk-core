using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Status;

public enum StatusKind
{
    OverTime,
    Counter,
    Buff,
    Debuff,
    CrowdControl,
    Contagion,
    Meter,
    UnityCc
}

public enum StatusStacking
{
    Refresh,
    Replace,
    Coexist
}

public enum StatusPayloadKind
{
    PulseHp,
    UnityCc,
    ModifyStat,
    Spread
}

public sealed record StatusDef(
    string StatusId,
    StatusKind Kind,
    string Family,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Tags,
    StatusStacking Stacking,
    IReadOnlyList<StatusPayloadKind> PayloadKinds,
    /// <summary>
    /// spec-status-potency.md §2.3 (Q1) — the element the combine rule reads for
    /// <c>+ resist.{element}</c>, from the status def's OWN tag, never the attacker's. Null means a
    /// genuine absence, not a default (T5): an untagged status contributes nothing to the element term.
    /// </summary>
    string? Element = null,
    /// <summary>
    /// Leech's heal half (spec-healing-pair.md §3) — true only for <c>leech</c>. Copied onto
    /// <see cref="StatusInstance.PulseHealsAttacker"/> at apply time so the pulse loop never needs a
    /// second catalog lookup.
    /// </summary>
    bool PulseHealsAttacker = false);

public sealed class UnknownStatusIdException : Exception
{
    public string StatusId { get; }

    public UnknownStatusIdException(string statusId)
        : base($"Unknown statusId: {statusId}")
    {
        StatusId = statusId;
    }
}

public sealed class StatusCatalog
{
    readonly Dictionary<string, StatusDef> _defs = new(StringComparer.OrdinalIgnoreCase);

    public StatusCatalog(IEnumerable<StatusDef>? defs = null)
    {
        if (defs != null)
            foreach (var d in defs)
                Register(d);
    }

    public void Register(StatusDef def)
    {
        if (string.IsNullOrWhiteSpace(def.StatusId))
            throw new ArgumentException("StatusId required");
        _defs[def.StatusId] = def;
    }

    public bool TryGet(string statusId, out StatusDef def) =>
        _defs.TryGetValue(statusId, out def!);

    public StatusDef GetRequired(string statusId)
    {
        if (!TryGet(statusId, out var def))
            throw new UnknownStatusIdException(statusId);
        return def;
    }

    public IReadOnlyList<StatusDef> All() =>
        _defs.Values.OrderBy(d => d.StatusId, StringComparer.OrdinalIgnoreCase).ToList();
}

public interface IStatusRng
{
    double NextUnit();
}

public sealed class FixedStatusRng : IStatusRng
{
    readonly double _value;

    public FixedStatusRng(double value) => _value = value;

    public double NextUnit() => _value;
}

public sealed class SeededStatusRng : IStatusRng
{
    readonly ICombatRng _rng;

    public SeededStatusRng(ICombatRng rng) => _rng = rng;

    public double NextUnit() => _rng.Next(1_000_000) / 1_000_000.0;
}

/// <summary>Two-phase L2b apply math — actor-hub-ssot.md §4.</summary>
public sealed class ResistanceEvaluator
{
    public static double Sigmoid(double x, double steepness = 1.0) =>
        1.0 / (1.0 + Math.Exp(-x * steepness));

    /// <summary>
    /// delta → apply chance. Two shapes, both reachable from tuning, defaulting to the shipped one
    /// (<see cref="StatusApplyShape.Sigmoid"/> at <c>ApplyOffsetK = 0</c>, which is byte-identical to
    /// the previous <c>Sigmoid(delta / scale, steepness)</c> — <c>delta - 0.0</c> is exact in IEEE).
    ///
    /// <para><b>Why this became a shape rather than a number.</b> A sigmoid is 0.5 at its own zero for
    /// EVERY scale and EVERY steepness, so no value of any existing tunable could move the neutral
    /// point — an unequipped attacker landed every status on a coin flip, and a <c>cc</c> at parity
    /// was a permanent lock. That is the same defect the evasion chain refused for parry
    /// (<c>OverlayCombatCalculator</c>: <i>"a sigmoid would give 0.5 at delta=0 … a new default nobody
    /// chose"</i>), and this is the same fix, made selectable rather than imposed.</para>
    /// </summary>
    public static double ApplyChance(double delta, double scale, double steepness)
    {
        var shifted = delta - StatusPolicy.ApplyOffsetK;
        return StatusPolicy.ApplyShape switch
        {
            StatusApplyShape.LinearFromZero => scale <= 0 ? 0 : Math.Clamp(shifted / scale, 0.0, 1.0),
            _ => Sigmoid(shifted / scale, steepness)
        };
    }

    /// <summary>
    /// spec-status-potency.md §2.1 — Phase 1 (this method's apply-chance roll) is untouched; only
    /// Phase 2 (potency) splits into independent duration and intensity deltas.
    /// <paramref name="statusElement"/> is Q1's term (§2.3): the status DEF's own element tag, never
    /// the attacker's, read once by the caller (<see cref="StatusRuntime.Apply"/> already resolves the
    /// def) rather than giving this stateless calculator a <see cref="StatusCatalog"/> dependency.
    /// Null — a genuine absence, not a default (T5) — for every one of the 21 shipped statuses, none of
    /// which carries an element tag yet.
    /// </summary>
    public StatusApplyResult Evaluate(
        StatusApplyRequest request,
        ActorDerivedSnapshot? attacker,
        ActorDerivedSnapshot defender,
        IStatusRng rng,
        string? statusElement = null)
    {
        if (rng == null) throw new ArgumentNullException(nameof(rng));

        var category = StatusCategoryRegistry.GetRequiredCategory(request.StatusId);
        var tags = request.ImmunityTags ?? Array.Empty<string>();

        foreach (var tag in tags)
        {
            var immuneKey = DerivedStatChannels.StatusImmune(tag);
            if (defender.Get(immuneKey) >= 1.0)
            {
                return Resisted(request, StatusResistReason.Immunity);
            }
        }

        var attackerLess = request.AttackerLess || attacker == null;
        var attackerSnap = ResolveAttackerSnapshot(request, attacker);

        // Phase 1 — unchanged shape, now reading Q1's + resist.{element} term (part of the shared base
        // formula, not a Phase-2-only addition). netFactor here is Phase 1's OWN value (StatusApplyResult
        // doc: "untouched by the split") -- ComputeNetFactor(delta) on the unsplit delta, same as before
        // the split existed. It is distinct from durationNetFactor/intensityNetFactor below.
        var delta = ComputeDelta(request.StatusId, category, attackerSnap, defender, attackerLess, statusElement);
        var netFactor = ComputeNetFactor(delta);

        // Phase 2 — split (§2.1). Each reuses the same base totalPower/totalResist (element term
        // included) and adds its OWN duration- or intensity-specific term on top.
        var durationDelta = ComputePotencyDelta(
            request.StatusId, category, attackerSnap, defender, attackerLess, statusElement, "duration");
        var intensityDelta = ComputePotencyDelta(
            request.StatusId, category, attackerSnap, defender, attackerLess, statusElement, "intensity");

        var durationNetFactor = ComputeNetFactor(durationDelta);
        var intensityNetFactor = ComputeNetFactor(intensityDelta);

        foreach (var tag in tags)
        {
            var reductionKey = DerivedStatChannels.StatusImmuneReduction(tag);
            var reduction = Math.Clamp(defender.Get(reductionKey), 0, 1);
            // PartialImmunityScalesBoth (§6): (1 - immuneReduction) applies to both potency axes —
            // partial immunity blunts a status overall, not selectively by axis.
            durationNetFactor *= 1.0 - reduction;
            intensityNetFactor *= 1.0 - reduction;
        }

        // §2.2: the potency floor fires on INTENSITY only now. A zero-duration status is
        // instantaneous — a legitimate effect (IsUseless below still catches truly-nothing) — but a
        // zero-intensity one does nothing at all, which is what "resisted" means.
        if (intensityNetFactor <= StatusPolicy.MinNetFactor)
        {
            return Resisted(request, StatusResistReason.PotencyFloor,
                delta: delta, netFactor: netFactor,
                durationNetFactor: durationNetFactor, intensityNetFactor: intensityNetFactor);
        }

        // T3.2 (audit F3): no longer scaled by matchPower. Under linear Theta, a power-scaled divisor
        // makes a FIXED gap DECAY as both sides climb (measured: gap-5 p_apply 0.5010 at Theta=10,
        // 0.5000 at Theta=10,000) -- every apply converges to a coin flip regardless of how far ahead
        // the attacker is. A constant divisor is the one regime where both halves of the evaluator
        // (delta's contest, and the roll built from it) read power the same way.
        var effectiveApplyScale = Math.Max(
            StatusPolicy.ApplyScaleFloor,
            StatusPolicy.ApplyScaleKForCategory(category));
        var steepness = StatusPolicy.ApplySteepnessForCategory(category);
        var pApply = ApplyChance(delta, effectiveApplyScale, steepness);
        var pFinal = request.GrantChance * pApply;

        if (rng.NextUnit() >= pFinal)
        {
            return Resisted(request, StatusResistReason.ApplyRoll,
                delta: delta, netFactor: netFactor, pApply: pApply, pFinal: pFinal,
                durationNetFactor: durationNetFactor, intensityNetFactor: intensityNetFactor,
                effectiveApplyScale: effectiveApplyScale);
        }

        var effectiveMagnitude = request.BaseMagnitude * intensityNetFactor;
        var effectiveDuration = request.BaseDuration * durationNetFactor;

        if (IsUseless(effectiveMagnitude, effectiveDuration))
        {
            return Resisted(request, StatusResistReason.UselessMagnitude,
                delta: delta, netFactor: netFactor, pApply: pApply, pFinal: pFinal,
                durationNetFactor: durationNetFactor, intensityNetFactor: intensityNetFactor,
                effectiveApplyScale: effectiveApplyScale);
        }

        return new StatusApplyResult(
            Applied: true,
            ResistReason: null,
            Delta: delta,
            NetFactor: netFactor,
            PApply: pApply,
            PFinal: pFinal,
            EffectiveApplyScale: effectiveApplyScale,
            EffectiveMagnitude: effectiveMagnitude,
            EffectiveDuration: effectiveDuration,
            DurationNetFactor: durationNetFactor,
            IntensityNetFactor: intensityNetFactor);
    }

    static ActorDerivedSnapshot ResolveAttackerSnapshot(StatusApplyRequest request, ActorDerivedSnapshot? attacker)
    {
        if (request.AttackerLess || attacker == null)
            return ActorDerivedSnapshot.AttackerLess();
        return attacker;
    }

    public static double ComputeDelta(
        string statusId,
        string category,
        ActorDerivedSnapshot attacker,
        ActorDerivedSnapshot defender,
        bool attackerLess = false,
        string? element = null)
    {
        var totalPower = (StatusPolicy.IncludeTierPowerInDelta ? attacker.TierPower : 0)
                         + attacker.Get(DerivedStatChannels.StatusPowerOmni)
                         + attacker.Get($"status.power.{category}")
                         + attacker.Get(DerivedStatChannels.StatusPower(statusId));

        // Not re-clamped here: DerivedComposer already capped this value at compose time, against
        // DerivedStatPolicy.CategoryResistCap (cap-consolidation, T1) — one enforcement point. A second
        // clamp here against the SAME tunable made raising it a silent no-op, since compose ran first.
        var categoryResist = defender.Get($"status.resist.{category}");
        var perIdResist = defender.Get(DerivedStatChannels.StatusResist(statusId));

        // Q1 (spec-status-potency.md §2.3): status.resist.{element} already resolves through the open
        // prefix and nothing read it. {element} is the STATUS DEF's own tag, never the attacker's —
        // null/blank is a genuine absence (T5), not a default, so an untagged status contributes 0.
        var elementResist = string.IsNullOrWhiteSpace(element) ? 0 : defender.Get($"status.resist.{element}");

        // T3.1 fix (power-plan.md, found via BattleStatusTests going Victory -> Stalemate, not
        // assumed safe): the tier-power term is a CONTEST between two sides, so it is symmetric —
        // either both sides' tier power enter it, or neither does. An attacker-less application
        // (scripted setup statuses, trait/attack riders — BattleEngine.cs's "land attacker-less at
        // t0") has no real attacker side to contest with; without this guard, ResistFromPowerRatio=1.0
        // makes attacker.TierPower=0 net negative against ANY normal-power defender, netFactor clamps
        // to MinNetFactor (0.0), and the scripted effect becomes permanently, completely inert.
        // Category/per-id/omni/element resist (actual immunities and resistances) still apply normally.
        var totalResist = (attackerLess ? 0 : defender.TierPower * StatusPolicy.ResistFromPowerRatio)
                          + defender.Get(DerivedStatChannels.StatusResistOmni)
                          + categoryResist
                          + perIdResist
                          + elementResist;

        return totalPower - totalResist;
    }

    /// <summary>
    /// spec-status-potency.md §2.1 — durationDelta/intensityDelta share Phase 1's totalPower/totalResist
    /// base (element term included) and each add ONE <paramref name="family"/>-specific term:
    /// attacker's status.{family}.omni/{category}/{statusId} minus defender's
    /// status.{family}Reduction.omni/{category}/{statusId} — the identical omni+category+perId shape as
    /// status.power/status.resist (DerivedStatChannels.cs H.2: "same axis, same combine rule").
    /// <paramref name="family"/> is "duration" or "intensity", never a balance value — a channel-id
    /// fragment, not a magic number.
    /// </summary>
    public static double ComputePotencyDelta(
        string statusId,
        string category,
        ActorDerivedSnapshot attacker,
        ActorDerivedSnapshot defender,
        bool attackerLess,
        string? element,
        string family)
    {
        var baseDelta = ComputeDelta(statusId, category, attacker, defender, attackerLess, element);

        var attackerTerm = attacker.Get($"status.{family}.omni")
                          + attacker.Get($"status.{family}.{category}")
                          + attacker.Get($"status.{family}.{statusId}");
        var defenderReductionTerm = defender.Get($"status.{family}Reduction.omni")
                                   + defender.Get($"status.{family}Reduction.{category}")
                                   + defender.Get($"status.{family}Reduction.{statusId}");

        return baseDelta + attackerTerm - defenderReductionTerm;
    }

    // T3.2 (audit F4): a raw delta used directly as a multiplier made parity and +1 both give 1.0x
    // but +2 give 2.0x -- a cliff, and one retired world (Wa=25) gave 25x. Normalizing by
    // NetFactorScale removes the cliff and retires the delta==0 special case: the linear formula
    // already gives exactly 1.0 there with no branch (1 + 0/scale == 1), which is also why the
    // clamp's floor (MinNetFactor) is what makes a heavily negative delta fully-resisted, not a
    // hardcoded zero.
    public static double ComputeNetFactor(double delta) =>
        Math.Clamp(1.0 + delta / StatusPolicy.NetFactorScale, StatusPolicy.MinNetFactor, StatusPolicy.MaxNetFactor);

    static bool IsUseless(double magnitude, double duration) =>
        Math.Abs(magnitude) < 1e-9 && duration <= 0;

    static StatusApplyResult Resisted(
        StatusApplyRequest request,
        StatusResistReason reason,
        double delta = 0,
        double netFactor = 0,
        double pApply = 0,
        double pFinal = 0,
        double durationNetFactor = 0,
        double intensityNetFactor = 0,
        double effectiveApplyScale = 0) =>
        new(
            Applied: false,
            ResistReason: reason,
            Delta: delta,
            NetFactor: netFactor,
            PApply: pApply,
            PFinal: pFinal,
            EffectiveApplyScale: effectiveApplyScale,
            EffectiveMagnitude: 0,
            EffectiveDuration: 0,
            DurationNetFactor: durationNetFactor,
            IntensityNetFactor: intensityNetFactor);
}
