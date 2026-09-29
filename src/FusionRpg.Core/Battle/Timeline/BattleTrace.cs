using FusionRpg.Core.Combat;

namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// Observation of a battle's internals — RNG draws, intra-round phase order, per-round state.
/// Opt-in and inert: <see cref="BattleEngine.Resolve(BattleSetup, ulong, BattleTrace?)"/> takes
/// null in production and every record site is a null-conditional call, so tracing cannot change
/// an outcome.
///
/// It exists for the kernel-adoption parity ladder (spec-kernel-adoption.md): a golden hash diff
/// says only "something moved", which is a poor debugging instrument. A trace localizes drift to
/// a stream, a phase, or a round. Captured from the pre-adoption engine — once the kernel lands
/// there is nothing left to capture, which is why this ships before any engine change.
/// </summary>
public sealed class BattleTrace
{
    readonly List<string> _lines = new();
    readonly Dictionary<string, List<int>> _draws = new(StringComparer.Ordinal);
    readonly List<string> _phases = new();
    readonly List<string> _targets = new();
    readonly List<string> _applies = new();
    readonly List<string> _turns = new();
    readonly List<string> _aiDecisions = new();

    /// <summary>
    /// Intra-round phase markers in the order they actually ran. A snapshot, not a live view:
    /// handing out the backing list lets a caller cast it back and append, so the fixture the
    /// byte-identity gate compares could disagree with <see cref="Digest"/> about the same battle.
    /// </summary>
    public IReadOnlyList<string> Phases => _phases.ToArray();

    /// <summary>
    /// One RNG draw. Records the returned VALUE, never a count — <c>SeededRng.NextUInt</c> uses
    /// rejection sampling, so one logical draw consumes a variable number of generator steps and
    /// a count assertion over the underlying generator would be meaningless.
    /// </summary>
    public void Draw(string stream, int value)
    {
        if (!_draws.TryGetValue(stream, out var list))
        {
            list = new List<int>();
            _draws[stream] = list;
        }

        list.Add(value);
        _lines.Add($"draw {stream} {value}");
    }

    /// <summary>
    /// Draws seen on one stream, in order. Empty for a stream that never drew. A snapshot, for the
    /// same reason as <see cref="Phases"/> — and so a caller holding a result mid-battle does not
    /// watch it grow underneath them.
    /// </summary>
    public IReadOnlyList<int> Draws(string stream) =>
        _draws.TryGetValue(stream, out var list) ? list.ToArray() : Array.Empty<int>();

    public void Phase(int round, string phase)
    {
        _phases.Add($"{round}:{phase}");
        _lines.Add($"phase {round} {phase}");
    }

    /// <summary>
    /// One reaction-lane outcome (B6 — <see cref="ReactionLane.TryEnter"/>): entered, or dropped for
    /// having no lane, an exhausted depth stack, or an exhausted <c>WReact</c> pool. Exists so a
    /// dropped reaction is observable without a debugger — the same reason every other line here
    /// exists — and it is additive: no path that predates the reaction lane ever calls it, so
    /// existing trace fixtures are untouched.
    /// </summary>
    public void Reaction(string actorKey, string outcome) => _lines.Add($"reaction {actorKey} {outcome}");

    public void State(int round, string actorKey, long hp, long shieldAbsorbed) =>
        _lines.Add($"state {round} {actorKey} hp={hp} abs={shieldAbsorbed}");

    /// <summary>
    /// The resolved target ptr of one attack (action-todo.md T11 — the action-adoption parity
    /// ladder). Deliberately NOT folded into <see cref="Digest"/>: the kernel-adoption program's own
    /// pre-adoption fixtures (`PreAdoptionTraceTests`) already compare that string byte-for-byte, and
    /// this module does not own those files — a count-matching, target-differing run is exactly the
    /// failure this parity ladder exists to catch, so it gets its own accessor instead.
    /// </summary>
    public void Target(int round, string attackerKey, string targetKey) =>
        _targets.Add($"{round} {attackerKey}->{targetKey}");

    /// <summary>Target lines in order, for the same reason <see cref="Draws"/> is separate from <see cref="Digest"/>.</summary>
    public IReadOnlyList<string> Targets => _targets.ToArray();

    /// <summary>The signed HP delta one apply actually produced, for the same reason as <see cref="Target"/>.</summary>
    public void Apply(int round, string ownerKey, long signedDelta) =>
        _applies.Add($"{round} {ownerKey} {signedDelta}");

    public IReadOnlyList<string> Applies => _applies.ToArray();

    /// <summary>
    /// B38 — one per-actor turn-state transition.
    ///
    /// <para>Its own list, deliberately kept OUT of <see cref="Digest"/>, for exactly the reason
    /// <see cref="Target"/> gives: the digest is the fixture the parity ladder compares, so adding
    /// lines to it would move every trace golden and make an observability addition indistinguishable
    /// from a behaviour change. The turn cycle is bookkeeping over the same battle — it must be
    /// visible without being load-bearing.</para>
    /// </summary>
    public void Turn(int round, string actorKey, TurnState from, TurnState to) =>
        _turns.Add($"{round} {actorKey} {from}->{to}");

    /// <summary>Turn transitions in order, for the same reason <see cref="Targets"/> is separate.</summary>
    public IReadOnlyList<string> Turns => _turns.ToArray();

    /// <summary>
    /// base-defense `siege-ai` R6 (spec-siege-ai.md §7): one AI decision's own top-3 scored candidates
    /// with their full per-term breakdown, pre-formatted by the caller (`AiScoring.FormatTopThree`) —
    /// this class stays domain-agnostic, the same as every other method here, which takes only
    /// primitives and never a subsystem's own scoring type.
    ///
    /// <para><b>Deliberately not a `DecisionTrace` entry.</b> That class (same namespace) replays HUMAN
    /// input decisions for `(setup, seed, trace)` determinism — its `TracedDecision` record is a fixed
    /// `Player`/`Timeout` shape with no room for a scored candidate list, a genuinely different concern
    /// from an AI's own scoring introspection, not the same trace wearing two hats.</para>
    ///
    /// <para>Its own list, kept OUT of <see cref="Digest"/> for the same reason <see cref="Target"/>/
    /// <see cref="Turn"/> are: an observability addition must not become indistinguishable from a
    /// behavior change in the fixture the parity ladder compares.</para>
    /// </summary>
    public void AiDecision(int round, string actorKey, string topThreeSummary) =>
        _aiDecisions.Add($"{round} {actorKey} {topThreeSummary}");

    /// <summary>AI decision lines in order, for the same reason <see cref="Targets"/> is separate.</summary>
    public IReadOnlyList<string> AiDecisions => _aiDecisions.ToArray();

    /// <summary>
    /// Decorates the combat RNG so crit-stream draws are recorded without touching
    /// <c>OverlayCombatCalculator</c> — that path is shared with the overlay hot path.
    /// </summary>
    public ICombatRng WrapCombat(string stream, ICombatRng inner) =>
        new TracingCombatRng(this, stream, inner);

    /// <summary>The whole trace as stable text — the fixture the parity ladder compares.</summary>
    public string Digest => string.Join("\n", _lines);

    sealed class TracingCombatRng : ICombatRng
    {
        readonly BattleTrace _trace;
        readonly string _stream;
        readonly ICombatRng _inner;

        public TracingCombatRng(BattleTrace trace, string stream, ICombatRng inner)
        {
            _trace = trace;
            _stream = stream;
            _inner = inner;
        }

        public int Next(int exclusiveMax)
        {
            var v = _inner.Next(exclusiveMax);
            _trace.Draw(_stream, v);
            return v;
        }
    }
}
