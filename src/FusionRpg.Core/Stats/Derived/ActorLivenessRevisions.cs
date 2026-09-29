namespace FusionRpg.Core.Stats.Derived;

/// <summary>What an invalidation's kind reaches: the whole player, or one live entity.</summary>
public enum LivenessInvalidationScope
{
    Player = 0,
    Entity = 1,
}

/// <summary>
/// The kind → scope mapping of the closed vocabulary (`spec-actor-liveness-refresh.md` "The shape"
/// table). It lives with the vocabulary so the sender, the receiver and the tests cannot disagree
/// about which cache a kind drops.
/// </summary>
public static class LivenessInvalidationScopes
{
    public static LivenessInvalidationScope ScopeOf(LivenessInvalidationKind kind) => kind switch
    {
        // Θ, the commander build and the tree all move the PLAYER's own inputs. A commander allocation
        // still lands at the next board.start because the match freeze is deliberate (owner ruling
        // 2026-09-16) — the invalidation fires so the OUT-of-match cache is correct, and the entity
        // path reads whatever ResolveAllocation returns, frozen or live.
        LivenessInvalidationKind.Ladder => LivenessInvalidationScope.Player,
        LivenessInvalidationKind.CommanderAllocation => LivenessInvalidationScope.Player,
        LivenessInvalidationKind.Tree => LivenessInvalidationScope.Player,
        // A specimen's own allocation and its gear move that specimen only — and neither is covered by
        // the match freeze, which is why these two must reach a living entity mid-match.
        LivenessInvalidationKind.UniqueAllocation => LivenessInvalidationScope.Entity,
        LivenessInvalidationKind.Equip => LivenessInvalidationScope.Entity,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no scope exists for this kind"),
    };
}

/// <summary>
/// The injector's revision store: one monotonic counter per <c>(playerId, entityKey)</c>, plus one per
/// player for the player-scoped kinds.
///
/// <para><b>Mark dirty, never recompose.</b> A bump here does no work beyond a counter: the consumer
/// (<c>LawnDerivedCache.Get</c>) compares the effective revision it last saw and re-resolves once on
/// its next read. That is rule 3's bound made structural — a 300-zombie wave receiving one
/// player-scoped invalidation costs each actor at most ONE further resolve, on its own next read, and
/// costs nothing at all for an actor nothing reads.</para>
///
/// <para><b>The effective revision is the sum, not the max.</b> An actor's inputs can move because ITS
/// entity changed (an allocation, a piece of gear) or because its PLAYER changed (a level, a commander
/// build, a tree spend). Summing keeps either bump monotone; a max would let a large per-entity counter
/// mask a later player bump, which is exactly the "frozen Θ" defect class this module closes.</para>
///
/// <para>Values are <c>long</c> and every bump is <c>checked</c> (<c>docs/architecture/numeric-types.md</c>: integer overflow is
/// a range constraint) — a wrapped counter would make a stale snapshot compare as fresh.</para>
/// </summary>
public sealed class ActorLivenessRevisions
{
    readonly Dictionary<LivenessActorKey, long> _byActor = new();
    readonly Dictionary<long, long> _byPlayer = new();

    /// <summary>How many actors have their own counter — a bound reading for tests, never a contract.</summary>
    public int ActorCount => _byActor.Count;

    /// <summary>How many players have a player-scoped counter — a bound reading for tests.</summary>
    public int PlayerCount => _byPlayer.Count;

    /// <summary>The effective revision for one actor: its own plus its player's. Reading never bumps
    /// and never composes anything — it is the cheap comparison the consumer keys its memo on.</summary>
    public long Read(LivenessActorKey key) =>
        checked(Value(_byActor, key) + Value(_byPlayer, key.PlayerId));

    /// <summary>Whether the consumer's last-seen revision is behind. A pure read, so asking costs
    /// nothing and can be asked as often as a caller likes.</summary>
    public bool IsStale(LivenessActorKey key, long seenRevision) => Read(key) != seenRevision;

    /// <summary>Bump ONE entity's counter (a specimen allocation, a piece of gear).</summary>
    public long Bump(LivenessActorKey key)
    {
        var next = checked(Value(_byActor, key) + 1);
        _byActor[key] = next;
        return next;
    }

    /// <summary>Bump a player's counter (a level, a commander build, a tree spend). Every actor of that
    /// player reads a moved revision on its next read — lazily, with no work done here.</summary>
    public long BumpPlayer(long playerId)
    {
        var next = checked(Value(_byPlayer, playerId) + 1);
        _byPlayer[playerId] = next;
        return next;
    }

    /// <summary>Drop one actor's own counter — the death / pointer-reuse edge (IL2CPP reuses
    /// addresses, so a new actor must not inherit a stranger's revision).</summary>
    public bool Forget(LivenessActorKey key) => _byActor.Remove(key);

    /// <summary>Drop every actor counter for one entity key, whichever player it belonged to — the
    /// injector's per-ptr death edge, where only the pointer is known.</summary>
    public int ForgetActor(string? entityKey)
    {
        if (string.IsNullOrWhiteSpace(entityKey)) return 0;
        var doomed = _byActor.Keys.Where(k => string.Equals(k.EntityKey, entityKey, StringComparison.Ordinal)).ToList();
        foreach (var key in doomed) _byActor.Remove(key);
        return doomed.Count;
    }

    /// <summary>Drop everything — the board-start / match-end barrier. A new match is a fresh window for
    /// every actor rather than a carried-over one.</summary>
    public void Clear()
    {
        _byActor.Clear();
        _byPlayer.Clear();
    }

    static long Value<TKey>(Dictionary<TKey, long> map, TKey key) where TKey : notnull =>
        map.TryGetValue(key, out var v) ? v : 0L;
}

/// <summary>Why an invalidation was not applied. <c>None</c> means it was.</summary>
public enum LivenessRefusal
{
    None = 0,
    /// <summary>The wire named a kind this build does not know — a newer server. Refused and REPORTED
    /// (rule 5), never skipped: quietly ignoring it is how the frozen-Θ defect survived.</summary>
    UnknownKind = 1,
    /// <summary>No usable player id — nothing to scope the invalidation to.</summary>
    MissingPlayer = 2,
    /// <summary>An entity-scoped kind arrived with no entity key — nothing to scope it to.</summary>
    MissingEntity = 3,
}

/// <summary>
/// The receive-side decision: one wire invalidation in, one bump (or one refusal) out. Pure and
/// Unity-free so the whole routing table — including every refusal — is testable without a game, and
/// so the injector's SignalR handler stays a two-line caller.
/// </summary>
public static class LivenessInvalidationRouter
{
    /// <summary>
    /// Apply one received invalidation. <paramref name="refusal"/> is <see cref="LivenessRefusal.None"/>
    /// exactly when the return value is true; every false return names why, so the caller can log a
    /// refusal rather than swallow it.
    /// </summary>
    public static bool TryApply(
        ActorLivenessRevisions revisions,
        string? wireKind,
        long playerId,
        string? entityKey,
        out LivenessInvalidationKind kind,
        out LivenessRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(revisions);

        kind = default;
        refusal = LivenessRefusal.None;

        if (!LivenessInvalidationWire.TryParse(wireKind, out kind))
        {
            refusal = LivenessRefusal.UnknownKind;
            return false;
        }

        if (playerId <= 0)
        {
            refusal = LivenessRefusal.MissingPlayer;
            return false;
        }

        if (LivenessInvalidationScopes.ScopeOf(kind) == LivenessInvalidationScope.Entity)
        {
            if (string.IsNullOrWhiteSpace(entityKey))
            {
                refusal = LivenessRefusal.MissingEntity;
                return false;
            }

            revisions.Bump(new LivenessActorKey(playerId, entityKey!));
            return true;
        }

        revisions.BumpPlayer(playerId);
        return true;
    }
}
