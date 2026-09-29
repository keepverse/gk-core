namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The pure arithmetic behind one bound <c>bullet.modify</c> grant — E37
/// (spec-projectile-control.md §2b/§3). Extracted out of the injector's
/// <c>CheatPrefixes.BulletInitCheat</c> postfix specifically so it is Unity-free: the injector is not
/// built by CI (<c>.github/workflows/ci.yml</c> runs no injector project), so the overflow-throw
/// contract (criterion 8) would otherwise be provable only by a live, owner-run lawn session. This way
/// it is a normal xunit fact — "every assertion that can live in Core does" (the spec's own §4 rule).
/// </summary>
public static class BulletModifyMath
{
    // Structural (tunables-ssot.md's per-mille exemption, AGENTS.md's "bounded ratios" carve-out) —
    // fixed by the per-mille convention itself (always exactly half the 1000 divisor for round-half-
    // up), never a number a balance pass would independently retune. Named, not inlined, so this
    // Math.cs file (the balance surface per tunables-ssot.md) carries no bare literal on this line.
    const long RoundingOffsetMilli = 500;

    /// <summary>
    /// <c>set</c>/<c>add</c> are whole damage units; <c>scale</c> is per-mille (amount 1500 = x1.5) —
    /// widened to <c>long</c> before multiplying, divided by 1000 exactly once (rounded, not
    /// truncated), and narrowed back to Unity's <c>Bullet.Damage</c> <c>int</c> field ONLY at this
    /// method's own return — a single <c>checked</c> boundary, never a mid-arithmetic cast.
    ///
    /// <para><b>Throws <see cref="OverflowException"/> on overflow — never wraps, never clamps.</b>
    /// Deliberately NOT <c>ZombieCombatFields.ClampToInt32</c>'s shape: that helper saturates silently
    /// for a magnitude with no hard ceiling (hp/atk written straight to a Unity field); this one kind
    /// needs the opposite, a real, provable throw (spec criterion 8), so an authored <c>scale</c> that
    /// would overflow damage is a bug that fails loudly rather than a bullet that silently caps at
    /// <c>int.MaxValue</c>.</para>
    /// </summary>
    public static int Apply(int currentDamage, string op, long amount) => checked(op switch  // overflow-bounded: mirrors PvZ's int Bullet.damage field; the arithmetic is checked, so overflow throws, never wraps
    {
        "set" => (int)amount,
        "add" => (int)((long)currentDamage + amount),
        "scale" => (int)(((long)currentDamage * amount + RoundingOffsetMilli) / 1000),
        // An unknown op cannot reach here in practice — AtomKindRegistry's own Vocabulary check
        // (BulletModifyOps) already refused it at load — but a no-op fallback beats throwing on
        // content this method did not author the refusal for.
        _ => currentDamage,
    });
}

/// <summary>A bullet's own fields relevant to <c>bullet.modify</c> and the D- cheat family. Immutable
/// so <see cref="BulletFireResolver.Resolve"/> can fold over it without mutating a live Unity object —
/// the injector applies only the final result to <c>__instance</c>.</summary>
public readonly record struct BulletFireState(int Damage, int? BulletType, string? MoveWay);  // overflow-bounded: mirrors PvZ's int Bullet.damage field; the arithmetic is checked, so overflow throws, never wraps

/// <summary>
/// The full <c>Bullet.InitData</c> postfix ordering rule (E37 §2b, criterion 5/6), extracted so it is
/// provable in Core/CI with no live Unity <c>Bullet</c> — the same motive as
/// <see cref="BulletModifyMath"/>'s own extraction. <see cref="CheatPrefixes.BulletInitCheat"/> in
/// <c>FusionRpg.Injector</c> is a thin shell over this: read the bullet's current fields in, call
/// <see cref="Resolve"/>, write the result back out.
///
/// <para><b>The rule, stated once, here:</b> every bound <c>bullet.modify</c> grant folds over the
/// bullet's fields FIRST, in bind order; the D- cheat family (<c>D-DMG-SET</c>, <c>D-DMG-%</c>,
/// <c>D-TYPE-SWAP</c>, <c>D-HOMING</c>) is applied AFTER, so an operator's cheat always wins over
/// bound content — never rewritten, only extended, matching §3's "do not rewrite BulletInitCheat".</para>
/// </summary>
public static class BulletFireResolver
{
    /// <param name="initial">The bullet's fields as <c>InitData</c> first set them, before any grant
    /// or cheat runs.</param>
    /// <param name="grants">Every bound <c>bullet.modify</c> atom for this bullet's firing plant (plus
    /// any match-scoped one), in the order they should fold — <see cref="GrantedBulletModifyAtomReader"/>'s
    /// own return order.</param>
    /// <param name="cheatDamageSet">"D-DMG-SET" — a non-negative absolute override, or null/negative
    /// for "not set" (mirrors <c>CheatState.IVal</c>'s own -1-means-unset convention).</param>
    /// <param name="cheatDamagePercent">"D-DMG-%" — a multiplier; 1.0 (within 0.001) means "not set".
    /// The structural floor of 1 (a zero-damage bullet is inert, not balanced) is applied here exactly
    /// as <c>CheatPrefixes.cs</c>'s own comment on this line explains.</param>
    /// <param name="cheatTypeSwap">"D-TYPE-SWAP" — a non-negative bullet type override, or negative for
    /// "not set".</param>
    /// <param name="cheatHoming">"D-HOMING" — forces <c>Track</c> when on.</param>
    public static BulletFireState Resolve(
        BulletFireState initial,
        IReadOnlyList<BoundBulletModifyAtom> grants,
        int cheatDamageSet,  // overflow-bounded: mirrors PvZ's int Bullet.damage field; the arithmetic is checked, so overflow throws, never wraps
        float cheatDamagePercent,
        int cheatTypeSwap,
        bool cheatHoming)
    {
        var state = initial;

        foreach (var atom in grants)
        {
            state = state with { Damage = BulletModifyMath.Apply(state.Damage, atom.Op, atom.Amount) };
            if (atom.BulletType is { } bt) state = state with { BulletType = bt };
            if (!string.IsNullOrEmpty(atom.MoveWay)) state = state with { MoveWay = atom.MoveWay };
        }

        // From here down: byte-identical to CheatPrefixes.BulletInitCheat's pre-E37 cheat block, in
        // the same order, so cheat state always wins over whatever the loop above just did.
        if (cheatDamageSet >= 0) state = state with { Damage = cheatDamageSet };

        if (Math.Abs(cheatDamagePercent - 1f) > 0.001f)
            state = state with
            {
                // Structural floor, not a progression cap — a zero-damage bullet is inert, not
                // balanced (AGENTS.md's "no hard progression ceilings" rule is about upper bounds).
                Damage = Math.Max(1, (int)Math.Round(state.Damage * cheatDamagePercent)),
            };

        if (cheatTypeSwap >= 0) state = state with { BulletType = cheatTypeSwap };

        if (cheatHoming) state = state with { MoveWay = "Track" };

        return state;
    }
}
