using System.Linq;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Battle;

/// <summary>
/// The battle's sourced contribution registry for live derived channels. It does not decide a
/// channel value: production supplies the one ActorHub resolver installed by
/// <see cref="BattleRunState"/>, and <see cref="Recompose"/> copies that Hub output into the stable
/// snapshot object every battle consumer already holds.
/// </summary>
public sealed class BattleDerivedModifierLedger
{
    static readonly DerivedComposer HubComposer = new();

    readonly Dictionary<(string ActorKey, string Channel), List<(string SourceId, double Value)>> _contributions = new();
    Func<string, ActorDerivedSnapshot>? _resolveThroughHub;

    /// <summary>
    /// D6 (solid-remediation T3.5): whether any contribution has ever been recorded.
    ///
    /// <para>The per-round recompose in <c>BattleEngine</c> exists for mechanisms that change a
    /// derived value mid-battle. Measured 2026-09-17: the only production writer is
    /// <c>BattleRunState</c>'s <c>setup.ActiveAuras</c> loop, and <c>ActiveAuras</c> is assigned in exactly
    /// two files, both tests — so in production the recompose ran every round, for every actor,
    /// against an empty ledger. This is what lets the caller skip it without deleting the mechanism.</para>
    ///
    /// <para><b>Updated 2026-09-23 (W11, battle-derived-wire T6):</b> the <c>defense</c> projection
    /// (<see cref="BattleStatModifierLedger.PushDefenseToDerived"/>) is now a second production
    /// writer, so any battle with a <c>defense</c> <c>stat.modify</c> or a <c>defense</c> status StatMod makes this
    /// non-empty and the per-round recompose does real work. The skip is still correct — an untouched
    /// ledger is still a hard no-op.</para>
    /// </summary>
    public bool IsEmpty => _contributions.Count == 0;

    /// <summary>
    /// Installs the production composition seam. The resolver is expected to append
    /// <see cref="BoundAtomsFor"/> to the actor's original Hub inputs and call
    /// <c>BattleHubCompose.Resolve</c>; it must not return a private battle-local fold.
    /// </summary>
    public void UseHubResolver(Func<string, ActorDerivedSnapshot> resolver) =>
        _resolveThroughHub = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public void Add(string actorKey, string channel, string sourceId, double value)
    {
        ValidateSource(sourceId);
        var key = (actorKey, channel);
        if (!_contributions.TryGetValue(key, out var list))
            _contributions[key] = list = new List<(string SourceId, double Value)>();
        list.Add((sourceId, value));
    }

    /// <summary>Replace-or-add ONE source's value on a channel (W11, battle-derived-wire T6).
    /// <see cref="Add"/> appends — a source may legitimately own several tuples — but the <c>defense</c>
    /// projection recomputes a single number per source, so re-pushing it must REPLACE rather than
    /// accumulate: otherwise a second <c>stat.modify</c> on the same grant, or a status re-applied under
    /// the same source id, would double-count under <see cref="Recompose"/>.</summary>
    public void Set(string actorKey, string channel, string sourceId, double value)
    {
        ValidateSource(sourceId);
        var key = (actorKey, channel);
        if (!_contributions.TryGetValue(key, out var list))
            _contributions[key] = list = new List<(string SourceId, double Value)>();
        var i = list.FindIndex(t => string.Equals(t.SourceId, sourceId, StringComparison.Ordinal));
        if (i >= 0) list[i] = (sourceId, value);
        else list.Add((sourceId, value));
    }

    /// <summary>Removes every (channel, value) tuple this source added, across all of an actor's
    /// channels. The emptied channel entry is kept so the next Hub recompose still restores the
    /// actor's original composed value.</summary>
    public void RemoveBySource(string actorKey, string sourceId)
    {
        foreach (var key in _contributions.Keys.Where(k => k.ActorKey == actorKey).ToList())
            _contributions[key].RemoveAll(t => t.SourceId == sourceId);
    }

    /// <summary>
    /// Projects the current live contributions through the existing
    /// <see cref="AtomDerivedSubsystem"/> carrier. The source id travels with every row, so
    /// ActorHub remains the only fold and GG-49 attribution is preserved.
    /// </summary>
    public IReadOnlyList<BoundDerivedAtom> BoundAtomsFor(string actorKey)
    {
        if (!_contributions.Any(k => k.Key.ActorKey == actorKey)) return Array.Empty<BoundDerivedAtom>();

        return _contributions
            .Where(k => k.Key.ActorKey == actorKey)
            .OrderBy(k => k.Key.Channel, StringComparer.Ordinal)
            .SelectMany(k => k.Value
                .OrderBy(t => t.SourceId, StringComparer.Ordinal)
                .Select(t => new BoundDerivedAtom(
                    k.Key.Channel, DerivedModifierOp.Flat, t.Value, t.SourceId)))
            .ToArray();
    }

    /// <summary>
    /// Copies the current ActorHub output into the stable live snapshot. The fallback exists only
    /// for the ledger's pre-Hub isolation tests; it calls the exact
    /// <see cref="DerivedComposer.ComposeChannelWithBaseline"/> fold ActorHub itself uses rather than
    /// reimplementing sum/cap/replace arithmetic here.
    /// </summary>
    public void Recompose(string actorKey, ActorDerivedSnapshot baseDerived, ActorDerivedSnapshot live)
    {
        if (_resolveThroughHub is not null)
        {
            var resolved = _resolveThroughHub(actorKey);
            foreach (var (channel, value) in resolved.Channels)
                live.Set(channel, value);
            return;
        }

        foreach (var key in _contributions.Keys.Where(k => k.ActorKey == actorKey))
        {
            var mods = _contributions[key]
                .Select(t => new DerivedModifier(
                    key.Channel, DerivedModifierOp.Flat, t.Value, SourceId: t.SourceId))
                .ToArray();
            live.Set(key.Channel, HubComposer.ComposeChannelWithBaseline(
                key.Channel, baseDerived.Get(key.Channel), mods));
        }
    }

    static void ValidateSource(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("A live derived contribution must name its source.", nameof(sourceId));
    }
}
