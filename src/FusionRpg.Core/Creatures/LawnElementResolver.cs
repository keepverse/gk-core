using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures;

/// <summary>
/// Turns a lawn actor's `(side, PvZ typeId)` into its species' <see cref="ActorElementTypes"/>, cached
/// per actor per match (spec-lawn-element-bind.md §2.4, E27).
///
/// <para><b>The board lookup is the expensive part, not the element map.</b>
/// <c>InjectorCombatBridge.ResolveElementTypesFromHub</c> and <c>InjectorStatusBridge.ResolveDerived</c>
/// each loop the whole board to find one actor's `(side, typeId)` — <b>on every hit</b>, which the
/// 2026-08 perf audit already blamed. `boardLookup` is a <see cref="Func{TResult}"/> for exactly this
/// reason: it only ever runs on a cache miss, so this resolver is what removes that scan from the hot
/// path rather than adding a second one beside it.</para>
///
/// <para><b>A pointer is match-scoped.</b> A later match can hand the same Unity pointer to a different
/// entity, so the cache clears whenever <c>matchKey</c> changes rather than living for the process.</para>
///
/// <para><b>The full trigger set (DESIGN-GATE §2.16).</b> This cache is edge-refreshed, so every edge
/// that can change what a ptr's <c>(side, elements)</c> resolves to must be enumerated and tested, not
/// just the one that prompted the fix. There are exactly four candidates and only two of them fire:
/// <list type="number">
/// <item><b>Match change</b> — fires. Handled by the wholesale clear in <see cref="Resolve"/>.</item>
/// <item><b>Hypno / charm</b> — <b>does not fire.</b> The <c>side</c> this cache stores is the actor's
/// OBJECT KIND, not its allegiance: <c>InjectorEntityRegistry.CollectSnaps</c> writes
/// <c>Side = "plant"</c> / <c>Side = "zombie"</c> as literals per collection and carries mind control in
/// the separate <c>BoardEntitySnap.MindControlled</c> flag, which <c>MechanicalOwnSideOracle</c> is the
/// SSOT for folding in ("mind control flips which side an entity fights FOR, not which side it visually
/// belongs to"). A hypnotised zombie is still a Zombie with a zombie type id, so its species row is
/// still the zombie one — flipping this cached side on hypno would make the
/// <c>(side, gameTypeId)</c> lookup MISS and degrade a charmed zombie to Neutral, and would break
/// <c>GateCounterHost</c>, which depends on this value being spawn kind (spec-gate-counters.md §2.1's
/// charm/hypno closing rule).</item>
/// <item><b>Entity death + IL2CPP pointer reuse</b> — fires, and is what
/// <see cref="Invalidate"/> exists for. A new entity allocated at a dead one's address inside the same
/// match must not inherit its entry. <c>GameHooks.ForgetEntity</c> is the single leave-board cleanup
/// every death path already funnels through, so the invalidation rides there.</item>
/// <item><b>Catalog revision mid-run</b> — <b>does not fire.</b> The index is built once from
/// <c>CreatureSpeciesCatalog</c>, which is configured exactly once per host at startup
/// (<c>SpeciesSnapshot</c>: "loaded once, immutable for the process lifetime... no live reload").
/// <c>reforge-world</c> re-rolls a PLAYER's rolled species rows in the store and never calls
/// <c>Configure</c>.</item>
/// </list>
/// </para>
///
/// <para><b>A fifth case, added 2026-09-14 (lawn-combat-wire T10/T12 live-inert investigation): the
/// board not knowing this ptr YET.</b> <paramref name="boardLookup"/> now answers an explicit
/// <c>Found</c> flag instead of overloading <c>typeId == 0</c> as a "no entity" sentinel.
/// <b>2026-09-14, corrected same day (live-inert root cause, third defect):</b> the original version of
/// this fix used <c>typeId == 0</c> itself as the "board does not know this ptr" signal, on the documented
/// belief that <c>0</c> is this codebase's universal "no entity" sentinel (mirroring
/// <c>ZombieType.Nothing</c>). That belief is false for THIS game build: live evidence
/// (<c>gk-data/packs/fusion/data/generated/creatures/Peashooter.json</c> and <c>NormalZombie.json</c>, both
/// <c>"gameTypeId": 0</c>; confirmed via `debug.spawn.plant`/`debug.spawn.zombie` events showing
/// <c>type:0, typeName:"Peashooter"</c> / <c>typeName:"NormalZombie"</c> — the two most common default
/// test subjects) proves species index <c>0</c> is a real, ordinary creature on both sides, not a sentinel.
/// The old <c>typeId == 0</c> check therefore treated EVERY Peashooter and EVERY NormalZombie as
/// permanently unresolved — never a one-frame race, an unconditional miss — which is why
/// <c>LawnBasicAttackGrantBinder</c>'s own retry-and-requeue fix (same investigation, earlier the same
/// day) never closed the live symptom: retrying a board-fact lookup that is wrong by construction just
/// repeats the same wrong answer. It also silently degraded <c>InjectorCombatBridge</c>/
/// <c>InjectorStatusBridge</c>'s elemental combat math to Neutral for both species, a wider blast radius
/// than the grant binder alone. The fix is a real found/not-found signal, decoupled from the numeric
/// typeId: a board miss (<c>Found == false</c>) is answered directly and never cached, for the same
/// reason as before — a caller that retries the SAME ptr once the board genuinely knows about it must
/// not read back a stale cached Neutral, because a cache hit never re-invokes
/// <paramref name="boardLookup"/> at all.</para>
/// </summary>
public sealed class LawnElementResolver
{
    readonly LawnElementIndex _index;
    readonly Action<string>? _onDiagnostic;
    readonly Dictionary<string, (string Side, ActorElementTypes Elements)> _cache =
        new(StringComparer.Ordinal);
    readonly HashSet<int> _reportedMisses = new();
    string? _matchKey;

    public LawnElementResolver(LawnElementIndex index, Action<string>? onDiagnostic = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _onDiagnostic = onDiagnostic;
        foreach (var line in index.Collisions)
            _onDiagnostic?.Invoke("lawn-element-bind: duplicate (side, gameTypeId) " + line);
    }

    /// <summary>Resolves since the resolver was built — read-only, for a cache-hit-rate test.</summary>
    public int ResolveCallCount { get; private set; }

    /// <summary>Board lookups actually performed — strictly less than <see cref="ResolveCallCount"/>
    /// once a ptr repeats within a match, which is the whole point of the cache.</summary>
    public int BoardLookupCount { get; private set; }

    /// <summary>Entries currently cached — read-only, so a test can prove a per-ptr invalidation
    /// removed exactly one entry rather than clearing the board's worth of them.</summary>
    public int CachedPtrCount => _cache.Count;

    public (string Side, ActorElementTypes Elements) Resolve(
        string? matchKey, string ptrKey, Func<(string Side, int TypeId, bool Found)> boardLookup)
    {
        if (string.IsNullOrEmpty(ptrKey)) throw new ArgumentException("ptrKey is required.", nameof(ptrKey));
        if (boardLookup is null) throw new ArgumentNullException(nameof(boardLookup));

        ResolveCallCount++;

        if (!string.Equals(matchKey, _matchKey, StringComparison.Ordinal))
        {
            _cache.Clear();
            _reportedMisses.Clear();
            _matchKey = matchKey;
        }

        // Keyed on the CANONICAL ptr, not the caller's spelling. The three call sites do not agree on
        // one: `GateCounterHost` passes `CombatPtr.Normalize(ptr)` while both combat bridges pass the
        // raw key, so "1A2B" and "1a2b" used to occupy two entries for one entity — which both wastes
        // a board scan and, more importantly, makes an invalidation by ptr unreliable (it would clear
        // one spelling and leave the other serving the dead entity). Normalizing here is what makes
        // `Invalidate` actually hit.
        var key = CombatPtr.Normalize(ptrKey);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        BoardLookupCount++;
        var (side, typeId, found) = boardLookup();
        // 2026-09-14 fix (this class's own doc, "a fifth case"): a board miss (Found == false) is never
        // cached -- a caller that resolves too early (before the entity's own board-registration hook has
        // run) must not permanently poison this ptr's entry with Neutral, even once a later call would
        // have found the real species. Found is a real flag now, never inferred from typeId == 0 -- see
        // the class doc's "corrected same day" note for why that inference was wrong.
        if (!found) return (side, ActorElementTypes.Neutral);
        var elements = ElementsFor(side, typeId);

        var result = (side, elements);
        _cache[key] = result;
        return result;
    }

    /// <summary>
    /// Drop ONE actor's cached <c>(side, elements)</c> — the leave-board edge (trigger 3 in the class
    /// doc's trigger set). Deliberately not a whole-cache clear: a clear on every death would re-resolve
    /// every actor on the board through <c>boardLookup</c>, which is precisely the per-hit board-scan
    /// pattern the 2026-08 perf audit blamed for lawn lag and that this cache exists to remove.
    ///
    /// <para>Never throws. An unknown, empty or null ptr is a no-op — the caller is a death hook that
    /// fires for every entity, including ones this resolver was never asked about, so "not cached" is
    /// the normal case rather than an error.</para>
    /// </summary>
    /// <returns><c>true</c> when an entry was actually removed — for tests and diagnostics only; no
    /// caller is expected to branch on it.</returns>
    public bool Invalidate(string? ptrKey)
    {
        var key = CombatPtr.Normalize(ptrKey);
        return key.Length != 0 && _cache.Remove(key);
    }

    ActorElementTypes ElementsFor(string side, int typeId)
    {
        if (!_index.TryGet(side, typeId, out var species))
            return Miss(typeId, $"no species for (side={side}, typeId={typeId})");

        if (!Enum.IsDefined(typeof(ElementTypeId), species.ElementPrimary))
            return Miss(typeId,
                $"species '{species.SpeciesId}' has an undefined ElementPrimary ({(int)species.ElementPrimary})");

        var secondary = species.ElementSecondary;
        if (secondary is { } sec && !Enum.IsDefined(typeof(ElementTypeId), sec))
            return Miss(typeId,
                $"species '{species.SpeciesId}' has an undefined ElementSecondary ({(int)sec})");

        // Mirrors BattleEngine.cs:36-38 verbatim, including the secondary-collapse rule — two
        // constructions of the same concept that differ by a corner case is how the two runtimes
        // drift apart. CreatureSpeciesCatalog.Validate already refuses secondary == primary at import,
        // so this is belt-and-braces, not a case that fires against a validated roster.
        return ActorElementTypes.Create(
            species.ElementPrimary,
            secondary == species.ElementPrimary ? null : secondary);
    }

    ActorElementTypes Miss(int typeId, string reason)
    {
        if (_reportedMisses.Add(typeId))
            _onDiagnostic?.Invoke("lawn-element-bind: " + reason + " — resolving Neutral");
        return ActorElementTypes.Neutral;
    }
}
