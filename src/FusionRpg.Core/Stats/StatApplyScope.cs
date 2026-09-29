using FusionRpg.Core.Effects;

namespace FusionRpg.Core.Stats;

/// <summary>
/// Whether a session <see cref="StatModifier.ApplyOwnerKey"/> applies to a resolve context.
/// Grammar matches <c>EffectOwnerKeys</c>: <c>match</c>, <c>plant:N</c>, <c>zombie:N</c>,
/// <c>entity:HEX</c>, empty → match — plus the side-wide <c>plant:*</c> / <c>zombie:*</c> pair
/// (<see cref="EffectOwnerKey.PlantSide"/>), which means "every actor on that side, any type id".
/// Durable <c>instance:{guid}</c> is Hot-forbidden until a deploy binder rewrites to <c>entity:{ptr}</c>.
///
/// <para><b>Why <c>plant:*</c> and not <c>side:plant</c>.</b> The existing key family already spells
/// the side as the PREFIX and the type id as the key (<c>plant:12</c>); the side-wide key widens that
/// same key slot, so <c>plant:*</c> stays one prefix to parse, one place a side is read, and it reads
/// as "plant, any type". <c>side:plant</c> would introduce a second axis and a second prefix shape for
/// a question the grammar already answers by prefix.</para>
///
/// <para><b>Boundary — the trigger gate is a different question and does not honour this key.</b>
/// <c>EffectOwnerKey.MatchesEvent</c> decides which EVENT a triggered grant fires on, and it keeps its
/// type-keyed arms only: a side-wide key never matches an event, so it is inert on a triggered grant.
/// That is correct today rather than merely tolerated — the only producer
/// (<c>PatronSecondaryPlugin</c>'s <c>fx.patron_aura</c>) is a triggerless <c>stat.derived</c> grant
/// whose mere presence is the effect, and a triggered side-wide grant would name no single owner
/// entity to attribute the proc to. A triggered side-wide grant is a separate decision, not a gap
/// this key is meant to fill.</para>
/// </summary>
public static class StatApplyScope
{
    /// <summary>
    /// Canonical form for bag Keys and Matches: trim, lower-case; empty → <c>match</c>;
    /// <c>entity:0xHEX</c> → <c>entity:hex</c> (no 0x prefix). The side-wide keys are already
    /// canonical, so this is a no-op on them.
    /// </summary>
    public static string Normalize(string? ownerKey)
    {
        if (string.IsNullOrWhiteSpace(ownerKey)) return "match";
        var key = ownerKey.Trim().ToLowerInvariant();
        if (key.StartsWith("entity:", StringComparison.Ordinal))
        {
            var hex = key[7..];
            if (hex.StartsWith("0x", StringComparison.Ordinal))
                hex = hex[2..];
            return "entity:" + hex;
        }

        return key;
    }

    /// <summary>
    /// Durable specimen key reserved for Server/Data. Must not match in Hot Resolve
    /// until a binder translates to <c>entity:{ptr}</c>.
    /// </summary>
    public static bool IsInstanceOwnerKey(string? applyOwnerKey)
    {
        var key = Normalize(applyOwnerKey);
        return key.StartsWith("instance:", StringComparison.Ordinal);
    }

    /// <summary>True for exactly the two side-wide keys — <c>plant:*</c> / <c>zombie:*</c>.</summary>
    public static bool IsSideWideOwnerKey(string? applyOwnerKey)
    {
        var key = Normalize(applyOwnerKey);
        return string.Equals(key, EffectOwnerKey.PlantSide, StringComparison.Ordinal) ||
               string.Equals(key, EffectOwnerKey.ZombieSide, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when a grant keyed <paramref name="grantOwnerKey"/> answers a store lookup for
    /// <paramref name="queriedOwnerKey"/> (<c>IEffectGrantStore.ForOwner</c>).
    ///
    /// <para>Exact normalized equality for every shipped key, plus the ONE widening the side-wide key
    /// exists for: <c>plant:*</c> answers any <c>plant:{typeId}</c> lookup on its own side — the shape
    /// every grant reader asks for a concrete actor — and never <c>zombie:{typeId}</c>, <c>match</c>,
    /// or an <c>entity:</c> lookup. Without it a side-wide grant would answer no lookup at all and the
    /// feature would be silently inert.</para>
    ///
    /// <para>This is a LOOKUP widening, not the apply gate: <see cref="Matches"/> remains the one place
    /// that decides whether a key applies to a resolve context.</para>
    /// </summary>
    public static bool OwnerKeyCovers(string? grantOwnerKey, string? queriedOwnerKey)
    {
        var grant = Normalize(grantOwnerKey);
        var query = Normalize(queriedOwnerKey);
        if (string.Equals(grant, query, StringComparison.Ordinal)) return true;

        if (string.Equals(grant, EffectOwnerKey.PlantSide, StringComparison.Ordinal))
            return query.StartsWith("plant:", StringComparison.Ordinal);
        if (string.Equals(grant, EffectOwnerKey.ZombieSide, StringComparison.Ordinal))
            return query.StartsWith("zombie:", StringComparison.Ordinal);

        return false;
    }

    public static bool Matches(string? applyOwnerKey, StatContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        return Matches(applyOwnerKey, ctx.Side, ctx.TypeId, ctx.EntityKey);
    }

    public static bool Matches(string? applyOwnerKey, StatSide side, int typeId, string? entityKey)
    {
        // Hot guard: instance: never composes until binder exists (S-INSTANCE-KEY-HOT).
        if (IsInstanceOwnerKey(applyOwnerKey))
            return false;

        var key = Normalize(applyOwnerKey);
        if (string.Equals(key, "match", StringComparison.Ordinal))
            return true;

        if (key.StartsWith("plant:", StringComparison.Ordinal))
        {
            // The side check is shared by both plant spellings, so a side-wide key can never reach a
            // zombie even if one arm is later edited.
            if (side != StatSide.Plant) return false;
            var rest = key.AsSpan(6);
            if (rest.Length == 1 && rest[0] == '*') return true; // plant:* — every plant, any type id
            return int.TryParse(rest, System.Globalization.NumberStyles.Integer,
                       System.Globalization.CultureInfo.InvariantCulture, out var tid) &&
                   tid == typeId;
        }

        if (key.StartsWith("zombie:", StringComparison.Ordinal))
        {
            if (side != StatSide.Zombie) return false;
            var rest = key.AsSpan(7);
            if (rest.Length == 1 && rest[0] == '*') return true; // zombie:* — every zombie, any type id
            return int.TryParse(rest, System.Globalization.NumberStyles.Integer,
                       System.Globalization.CultureInfo.InvariantCulture, out var tid) &&
                   tid == typeId;
        }

        if (key.StartsWith("entity:", StringComparison.Ordinal))
        {
            var ptr = key[7..];
            if (string.IsNullOrEmpty(entityKey) || string.IsNullOrEmpty(ptr))
                return false;
            var live = entityKey.Trim();
            if (live.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                live = live[2..];
            return string.Equals(live, ptr, StringComparison.OrdinalIgnoreCase);
        }

        if (key.StartsWith("player:", StringComparison.Ordinal))
            return true; // stub → match-wide apply

        return false;
    }

    public static bool IsMatchWide(string? applyOwnerKey)
    {
        var key = Normalize(applyOwnerKey);

        // `plant:*` / `zombie:*` are deliberately NOT match-wide, and this is a behaviour decision,
        // not a formality: match-wide means "applies to BOTH sides", so a caller that reads this as
        // "no side check is needed" would let a one-side key reach the other side — exactly the defect
        // the side-wide key was added to close.
        return string.Equals(key, "match", StringComparison.Ordinal) ||
               key.StartsWith("player:", StringComparison.Ordinal);
    }

    /// <summary>True for match/player/plant/zombie/entity grammar, the two side-wide keys included (after Normalize).</summary>
    public static bool IsKnownOwnerKey(string? applyOwnerKey)
    {
        var key = Normalize(applyOwnerKey);
        if (string.Equals(key, "match", StringComparison.Ordinal)) return true;
        if (key.StartsWith("player:", StringComparison.Ordinal)) return true;
        if (key.StartsWith("entity:", StringComparison.Ordinal)) return true;
        if (IsSideWideOwnerKey(key)) return true;
        if (key.StartsWith("plant:", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(6), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return true;
        if (key.StartsWith("zombie:", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(7), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return true;
        return false;
    }
}
