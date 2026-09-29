namespace FusionRpg.Core.Stats.Derived;

/// <summary>
/// What can change about an actor's inputs — the <b>closed</b> invalidation vocabulary of
/// <c>lawn-playable</c> module 1 (<c>docs/architecture/lawn-playable/spec-actor-liveness-refresh.md</c>,
/// "The shape").
///
/// <para><b>Why closed.</b> The server names what changed and the injector decides what to re-read
/// (rule 1: this is an invalidation, never a value push). The membership of this enum IS that
/// contract, exactly like <c>ActionCategory</c>, <c>AtomKind</c> or the AI vocabularies: every sender
/// and every handler switches on it, so adding a member is a reviewed change that moves this file and
/// its pinning test together, never a local convenience.</para>
///
/// <para><b><c>Player</c> is deliberately NOT a member here.</b> Hard edge E2
/// (<c>backlog-clean-up-plan.md</c>, recorded by <c>lawn-signal-ownership</c>): the generic
/// server → injector notice on <c>PUT /api/players/current</c> is <c>SP6.6</c>'s
/// (<c>species-progression</c>) and this module <b>extends</b> it (it re-hydrates Θ) rather than
/// declaring a second <c>Player</c>-kind channel — two writers for one idea is the
/// <c>BattleStatComposer</c> mistake this repo already paid for. So a <c>Player</c> invalidation is
/// recognised by <c>SP6.6</c>'s handler, not by these five kinds.</para>
///
/// <para>Ordinals are explicit and dense: the kind travels as a small integer on the wire, so a
/// renumbering would silently re-point every already-sent notice.</para>
/// </summary>
public enum LivenessInvalidationKind
{
    /// <summary>A progression level change — the Θ inputs moved, so every magnitude derived from the
    /// player's power index is stale. Player-scoped.</summary>
    Ladder = 0,

    /// <summary>A commander allocation (<c>POST /api/aptitudes/allocate</c>). Player-scoped. The
    /// invalidation still fires so the out-of-match cache is correct, but a match is a fixed contract:
    /// <c>MatchCommanderSnapshotHolder.ResolveAllocation</c> stays frozen until the next
    /// <c>board.start</c> (owner ruling 2026-09-16), and the entity path reads whatever that returns
    /// rather than special-casing it.</summary>
    CommanderAllocation = 1,

    /// <summary>A unique-specimen allocation (<c>POST /api/aptitudes/unique/allocate</c>).
    /// Specimen-scoped, and NOT covered by the match freeze — this is the scope that must recompose
    /// mid-match with no redeploy.</summary>
    UniqueAllocation = 2,

    /// <summary>Equip or unequip, either scope (a specimen's own gear or a holder's).</summary>
    Equip = 3,

    /// <summary>A passive-tree spend. Player-scoped.</summary>
    Tree = 4,
}

/// <summary>
/// The WIRE form of the closed invalidation vocabulary — the exact strings that travel on the
/// `AptitudesUpdated` payload's additive `kind` field, and the one place both ends agree about them.
///
/// <para>A name per member rather than the enum's own <c>ToString()</c>: the wire spelling is a
/// contract that outlives a C# identifier (a rename must be a deliberate wire change, not a
/// side effect), and the injector must be able to REFUSE an unknown kind rather than silently skip it
/// (`spec-actor-liveness-refresh.md` rule 5), which needs a parser that owns the vocabulary —
/// <see cref="TryParse"/> is that parser, and <c>false</c> is the refusal.</para>
///
/// <para><c>Player</c> has no wire name here on purpose: it is <c>SP6.6</c>'s own notice (hard edge
/// E2), not a member of this module's vocabulary.</para>
/// </summary>
public static class LivenessInvalidationWire
{
    public const string Ladder = "ladder";
    public const string CommanderAllocation = "commanderAllocation";
    public const string UniqueAllocation = "uniqueAllocation";
    public const string Equip = "equip";
    public const string Tree = "tree";

    public static string NameOf(LivenessInvalidationKind kind) => kind switch
    {
        LivenessInvalidationKind.Ladder => Ladder,
        LivenessInvalidationKind.CommanderAllocation => CommanderAllocation,
        LivenessInvalidationKind.UniqueAllocation => UniqueAllocation,
        LivenessInvalidationKind.Equip => Equip,
        LivenessInvalidationKind.Tree => Tree,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no wire name exists for this kind"),
    };

    /// <summary>Parse a wire name. <c>false</c> means "this build does not know that kind" — a refusal
    /// the caller must REPORT (a newer server), never a silent skip.</summary>
    public static bool TryParse(string? wire, out LivenessInvalidationKind kind)
    {
        switch (wire)
        {
            case Ladder: kind = LivenessInvalidationKind.Ladder; return true;
            case CommanderAllocation: kind = LivenessInvalidationKind.CommanderAllocation; return true;
            case UniqueAllocation: kind = LivenessInvalidationKind.UniqueAllocation; return true;
            case Equip: kind = LivenessInvalidationKind.Equip; return true;
            case Tree: kind = LivenessInvalidationKind.Tree; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>
/// The identity a revision counts for: one player and one live entity, matching the
/// <c>(playerId, entityKey)</c> pair the spec names. <see cref="EntityKey"/> is the live
/// owner key the rest of the derived layer already uses (the ptr hex that
/// <c>UniqueOwnerBinder.ToEntityKey</c> produces) — IL2CPP reuses pointers, so a key is only
/// meaningful for as long as its entity lives, which is why this is a value passed in, never a
/// durable id.
/// </summary>
public readonly record struct LivenessActorKey(long PlayerId, string EntityKey)
{
    public override string ToString() => $"{PlayerId}:{EntityKey}";
}

/// <summary>
/// One actor's liveness counter — a <c>long</c> per <see cref="LivenessActorKey"/>, monotonic, bumped
/// by whatever changed that actor's inputs and compared by consumers to decide "is my composed
/// snapshot stale?" (rule 2: a counter, not a hash — cheap to compare, impossible to collide).
///
/// <para>This is a value, not a store: the injector's dirty-marking and the server's per-scope bumping
/// live with their own callers (LW1.5/LW1.6), so nothing here is a second composer or a cache.</para>
/// </summary>
public readonly record struct ActorLivenessRevision(LivenessActorKey Key, long Value)
{
    /// <summary>The revision a key has before anything has changed for it. Zero is the sentinel
    /// meaning "never invalidated", so a consumer that has never seen a notice and one that has seen
    /// the first notice can be told apart.</summary>
    public static ActorLivenessRevision Initial(LivenessActorKey key) => new(key, 0);

    /// <summary>The next revision for this key. Integer overflow <b>throws</b> rather than wrapping
    /// (<c>docs/architecture/numeric-types.md</c>: integer overflow is a range constraint) — a wrapped counter would make a
    /// stale snapshot compare as fresh, which is the exact bug class this type exists to close. A
    /// <c>long</c> bumping once per real input change cannot reach that bound in any run.</summary>
    public ActorLivenessRevision Next() => new(Key, checked(Value + 1));

    /// <summary>Whether this revision is newer than <paramref name="seen"/> — the "recompose on next
    /// read" test. Compares values only; a revision of a DIFFERENT key is not comparable and callers
    /// must key their stores, because two actors' counters are independent by construction.</summary>
    public bool IsNewerThan(ActorLivenessRevision seen) => Value > seen.Value;
}
