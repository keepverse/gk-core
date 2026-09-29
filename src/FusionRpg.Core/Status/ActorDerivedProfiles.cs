using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Status;

/// <summary>Named derived bags for status prove boards — apply/resist must not depend on ambient PvzStats.</summary>
public static class ActorDerivedProfiles
{
    public const string Neutral = "neutral";
    public const string Caster = "caster";
    public const string Glass = "glass";
    public const string IronCc = "iron-cc";
    public const string IronDot = "iron-dot";
    public const string IronContagion = "iron-contagion";
    public const string ImmunePoison = "immune-poison";
    public const string AttackerLess = "attacker-less";

    public const string CombatNeutral = "combat-neutral";
    public const string CombatFireCaster = "combat-fire-caster";
    public const string CombatIceTank = "combat-ice-tank";
    public const string CombatGlass = "combat-glass";

    // Proof-board fixture values (class doc: "status prove boards"), not balance surface — a
    // balance pass never tunes what a *named test scenario* reports as its input, only what real
    // content specifies. tunables-ssot.md T1's test ("would a balance pass change this?") reads no
    // here, unlike genuine content/policy defaults.
    public const double IronOmniResist = 1_000_000;
    // Proof-board fixture, not balance surface (see above).
    public const double CasterPower = 100;
    // Proof-board fixture, not balance surface (see above).
    public const double CombatFirePower = 50;
    // Proof-board fixture, not balance surface (see above).
    public const double CombatIceDefense = 30;
    // Proof-board fixture, not balance surface (see above).
    public const double CombatHighAccuracy = 500;
    // Proof-board fixture, not balance surface (see above).
    public const double CombatLowAccuracy = -500;
    // Proof-board fixture, not balance surface (see above).
    public const double CombatHighDodge = 500;

    public static ActorDerivedSnapshot Get(string? profile)
    {
        var id = (profile ?? Neutral).Trim();
        if (string.Equals(id, AttackerLess, StringComparison.OrdinalIgnoreCase))
            return ActorDerivedSnapshot.AttackerLess();
        if (string.Equals(id, Caster, StringComparison.OrdinalIgnoreCase))
            return CasterSnapshot();
        if (string.Equals(id, Glass, StringComparison.OrdinalIgnoreCase))
            return ActorDerivedSnapshot.StubNeutral();
        if (string.Equals(id, IronCc, StringComparison.OrdinalIgnoreCase))
            return Iron(DerivedStatChannels.StatusResistCc);
        if (string.Equals(id, IronDot, StringComparison.OrdinalIgnoreCase))
            return Iron(DerivedStatChannels.StatusResistDot);
        if (string.Equals(id, IronContagion, StringComparison.OrdinalIgnoreCase))
            return Iron(DerivedStatChannels.StatusResistContagion);
        if (string.Equals(id, ImmunePoison, StringComparison.OrdinalIgnoreCase))
            return ActorDerivedSnapshot.StubNeutral().Overlay(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.StatusImmune("poison"), 1.0)
            });
        if (string.Equals(id, CombatNeutral, StringComparison.OrdinalIgnoreCase))
            return CombatNeutralSnapshot();
        if (string.Equals(id, CombatFireCaster, StringComparison.OrdinalIgnoreCase))
            return CombatFireCasterSnapshot();
        if (string.Equals(id, CombatIceTank, StringComparison.OrdinalIgnoreCase))
            return CombatIceTankSnapshot();
        if (string.Equals(id, CombatGlass, StringComparison.OrdinalIgnoreCase))
            return CombatGlassSnapshot();
        if (string.Equals(id, Neutral, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id))
            return ActorDerivedSnapshot.StubNeutral();
        throw new ArgumentException("unknown derivedProfile: " + id);
    }

    public static ActorDerivedSnapshot Resolve(
        string? profile,
        IReadOnlyDictionary<string, double>? overlay = null)
    {
        var snap = Get(profile);
        if (overlay == null || overlay.Count == 0)
            return snap;
        return snap.Overlay(overlay);
    }

    static ActorDerivedSnapshot CasterSnapshot() =>
        ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPowerOmni, CasterPower),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPowerDot, CasterPower),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPowerCc, CasterPower),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPowerContagion, CasterPower)
        });

    /// <summary>
    /// Category resist is capped at <see cref="Stats.Derived.DerivedStatPolicy.CategoryResistCap"/>
    /// (0.95) — applied once, at compose (cap-consolidation, T1). Omni is uncapped and carries the
    /// potency floor.
    /// </summary>
    static ActorDerivedSnapshot Iron(string categoryChannel) =>
        ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusResistOmni, IronOmniResist),
            new KeyValuePair<string, double>(categoryChannel, IronOmniResist)
        });

    static ActorDerivedSnapshot CombatNeutralSnapshot() =>
        ActorDerivedSnapshot.StubNeutral();

    static ActorDerivedSnapshot CombatFireCasterSnapshot() =>
        ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerFire, CombatFirePower),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, CombatHighAccuracy),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, -CombatHighAccuracy)
        });

    static ActorDerivedSnapshot CombatIceTankSnapshot() =>
        ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseIce, CombatIceDefense),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDodgeOmni, CombatHighDodge)
        });

    static ActorDerivedSnapshot CombatGlassSnapshot() =>
        ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseOmni, -50)
        });
}

/// <summary>
/// Ptr-keyed derived lookup for offline harness / scenario runner — <see cref="SimEffectHost"/> and
/// <see cref="Effects.FoundationHarness"/> each own one instance and both resolve derived stats
/// through it, so this class IS the "one fold, two hosts" seam spec-mechanism-wiring.md §4.3 asks
/// for: fold the logic once here and both hosts pick it up for free, rather than each host growing
/// its own copy that could drift.
///
/// <para><b>The contribution fold (§4.3 step 1).</b> Before this, <see cref="Resolve"/> returned the
/// pinned snapshot untouched — there was nothing for a bound <c>stat.derived</c> atom to contribute
/// to, which is exactly why <c>AtomKindRegistry</c>'s Sim cell stayed <c>None</c>, then E5 flipped it
/// to <c>Partial</c> once a plain-sum fold existed (decisions.md, "Derived-write lawn executor" owner
/// decision 2). <see cref="AddContribution"/> registers a <see cref="BoundDerivedAtom"/> per ptr, and
/// <see cref="Resolve"/> folds them onto the pinned base per channel via
/// <see cref="DerivedComposer.ComposeChannelWithBaseline"/> — sim-hub-parity (T15, 2026-09-13):
/// this is now the SAME op-aware fold every other runtime's <see cref="DerivedComposer"/> uses (Flat/
/// Increased sum, Replace picks highest-priority outright, Flag takes the max), with the pinned
/// snapshot value standing in for <see cref="DerivedStatDef.DefaultValue"/> — not a second,
/// independently-drifting reimplementation. This is what let the Sim cell move from <c>Partial</c> to
/// <c>Full</c>; see <see cref="AtomKindRegistry"/>'s own comment on the `stat.derived` Sim cell.</para>
///
/// <para><b>Deliberately reachable without a bind (§4.3 Verification).</b>
/// <see cref="AddContribution"/> never asks <see cref="BindGate"/> anything — a caller (a test, or a
/// future producer) hands it an already-resolved <see cref="BoundDerivedAtom"/> directly, the same
/// shape <see cref="Subsystems.AtomDerivedSubsystem"/> reads on the lawn. That is what proves the fold
/// and the registry cell are independent: the fold works today, with the cell still <c>None</c>.
/// <see cref="TryBind"/> is the SEPARATE, gated path (§4.3 step 3) — it constructs a real
/// <see cref="BindContext"/> for <see cref="RuntimeId.Sim"/> and asks <see cref="BindGate"/>, so a
/// bind is genuinely ATTEMPTED rather than a test manufacturing a context no production code path
/// ever builds. It refuses every row today (<c>RuntimeUnsupported</c>, because the cell is
/// <c>None</c>) — that is the correct outcome until E5 exercises the four derived ops and flips it.</para>
/// </summary>
public sealed class ActorDerivedLookup
{
    readonly Dictionary<string, ActorDerivedSnapshot> _byPtr = new(StringComparer.Ordinal);

    /// <summary>Bound `stat.derived` contributions, per ptr — the fold's only state.</summary>
    readonly Dictionary<string, List<BoundDerivedAtom>> _contributions = new(StringComparer.Ordinal);

    /// <summary>sim-hub-parity (T15) — held once per lookup instance, same lifetime pattern
    /// <see cref="ActorHub"/> already uses for its own <see cref="DerivedComposer"/> (constructed once,
    /// reused per resolve, never rebuilt per call).</summary>
    readonly DerivedComposer _composer = new();

    public void Pin(string? ptr, ActorDerivedSnapshot snapshot)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key) || snapshot == null) return;
        _byPtr[key] = snapshot;
    }

    public void Clear()
    {
        _byPtr.Clear();
        _contributions.Clear();
    }

    /// <summary>
    /// Registers one bound `stat.derived` contribution for <paramref name="ptr"/> — the SIM-side
    /// analog of <see cref="Subsystems.AtomDerivedSubsystem.ContributeDerived"/>. Folded onto the
    /// pinned snapshot at the next <see cref="Resolve"/>; never applied twice, because it is stored
    /// once here rather than re-derived per call.
    /// </summary>
    public void AddContribution(string? ptr, BoundDerivedAtom atom)
    {
        if (string.IsNullOrWhiteSpace(atom.Channel)) return;
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key)) return;
        if (!_contributions.TryGetValue(key, out var list))
            _contributions[key] = list = new List<BoundDerivedAtom>();
        list.Add(atom);
    }

    /// <summary>
    /// Attempts a real `stat.derived` bind through <see cref="BindGate"/> with
    /// <c>BindContext(RuntimeId.Sim)</c> — §4.3 step 3. Returns the gate's verdict; a caller that gets
    /// <see cref="AtomRejection.IsOk"/> back is expected to translate the accepted rows into
    /// <see cref="BoundDerivedAtom"/>s and fold them via <see cref="AddContribution"/>, exactly as
    /// <c>GrantedDerivedAtomReader</c> does for the lawn. <see cref="BindGate.Check"/> only refuses at
    /// <see cref="RuntimeState.None"/> (and <see cref="RuntimeState.PlanOnly"/> for a non-planner) —
    /// the Sim cell for `stat.derived` has been <see cref="RuntimeState.Partial"/> since E5 and is
    /// <see cref="RuntimeState.Full"/> as of sim-hub-parity (T15), so this has accepted rows since E5,
    /// not merely since this task.
    /// </summary>
    public AtomRejection TryBind(
        IReadOnlyList<AtomRow> atoms, OwnerScope owner, IReadOnlyCollection<string>? overlayKeys = null) =>
        BindGate.Check(atoms, owner, new BindContext(RuntimeId.Sim), overlayKeys: overlayKeys);

    public ActorDerivedSnapshot Resolve(string? ptr, bool attackerLess)
    {
        if (attackerLess)
            return ActorDerivedSnapshot.AttackerLess();
        var key = CombatPtr.Normalize(ptr);
        var baseSnapshot = !string.IsNullOrEmpty(key) && _byPtr.TryGetValue(key, out var snap)
            ? snap
            : ActorDerivedSnapshot.StubNeutral();
        if (string.IsNullOrEmpty(key) || !_contributions.TryGetValue(key, out var contribs) || contribs.Count == 0)
            return baseSnapshot;

        // sim-hub-parity (T15): per-channel op-aware fold via the SAME DerivedComposer every other
        // runtime uses — the pinned snapshot value stands in for DerivedStatDef.DefaultValue.
        // BoundDerivedAtom carries no Priority field (AtomDerivedSubsystem's own real conversion
        // never sets one either, defaulting to 0), so a multi-Replace tie breaks by SourceId — the
        // same tie-break every OTHER real producer already gets when it, too, leaves Priority default.
        var mods = contribs
            .Select(c => new DerivedModifier(c.Channel, c.Op, c.Amount, SourceId: c.SourceId))
            .ToList();
        var composed = contribs
            .Select(c => c.Channel)
            .Distinct(StringComparer.Ordinal)
            .Select(channel => new KeyValuePair<string, double>(
                channel, _composer.ComposeChannelWithBaseline(channel, baseSnapshot.Get(channel), mods)));
        return baseSnapshot.Overlay(composed);
    }
}
