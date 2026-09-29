namespace FusionRpg.Core.Stats;

/// <summary>
/// The live direction override for channels (E22, completeness-audit.md finding B1).
///
/// <para><c>effect_channel_policy</c> (E16) shipped with a write path (`RpgStore.UpsertChannelPolicies`)
/// and no read path — registered in the content hash at registry v4, refused an unknown channel at
/// write time, and reachable by nothing at runtime. <see cref="StatChannels.DirectionOf"/> stayed the
/// hardcoded switch it always was.</para>
///
/// <para><b>Scope, honestly narrower than the table's column list.</b> The table also carries
/// <c>default_value</c>, <c>cap_milli</c> and <c>compose_kind</c> — none of which any code anywhere
/// reads, for any channel, primary or derived (checked: <see cref="Derived.DerivedComposer"/> applies
/// <see cref="Derived.DerivedStatDef.Cap"/> from the code-registered derived catalog, a different and
/// already-consumed mechanism; <c>effect_channel_policy</c> is scoped to <c>StatChannels.All</c>, the
/// primary channels, and cannot even name a derived resist channel). Direction is the one column with
/// an existing, already-tested consumer (<see cref="StatChannels.IsLowerBetter"/>, read by
/// <c>CostFunction</c>'s direction-aware pricing and <c>StatComposer</c>'s interval floor) — the one
/// honest claim this module can make is "direction is live"; a claim about the other three columns
/// would be aspirational.</para>
///
/// <para>Same shape as <see cref="Combat.Element.ElementTable"/>/<see cref="Effects.Atoms.Power.PowerTables"/>:
/// process-global <see cref="Use"/> for a host, <see cref="UseScoped"/> (<c>AsyncLocal</c>) for a test
/// that must not disturb one running beside it. An empty table — the default, and what every host
/// with nothing imported runs on — changes nothing: <see cref="DirectionOf"/> falls through to the
/// same switch it always used.</para>
/// </summary>
public sealed class ChannelPolicyTable
{
    readonly IReadOnlyDictionary<string, ChannelDirection> _directions;

    public ChannelPolicyTable(IReadOnlyDictionary<string, int> directions)
    {
        if (directions is null) throw new ArgumentNullException(nameof(directions));
        var map = new Dictionary<string, ChannelDirection>(StringComparer.Ordinal);
        foreach (var (channel, dir) in directions)
            map[channel] = dir == (int)ChannelDirection.LowerIsBetter
                ? ChannelDirection.LowerIsBetter
                : ChannelDirection.HigherIsBetter;
        _directions = map;
    }

    public static ChannelPolicyTable Empty { get; } = new(new Dictionary<string, int>(StringComparer.Ordinal));

    public bool TryGetDirection(string channel, out ChannelDirection direction) =>
        _directions.TryGetValue(channel, out direction);

    static ChannelPolicyTable _global = Empty;
    static readonly AsyncLocal<ChannelPolicyTable?> Scoped = new();

    public static ChannelPolicyTable Current => Scoped.Value ?? _global;

    /// <summary>Process-wide. What a host calls once, after loading policy rows from its store.</summary>
    public static void Use(ChannelPolicyTable table) =>
        _global = table ?? throw new ArgumentNullException(nameof(table));

    public static void ResetToEmpty() => _global = Empty;

    /// <summary>Swap for this async context only, so a test cannot disturb one running beside it.</summary>
    public static IDisposable UseScoped(ChannelPolicyTable table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        var previous = Scoped.Value;
        Scoped.Value = table;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly ChannelPolicyTable? _previous;
        public Restore(ChannelPolicyTable? previous) => _previous = previous;
        public void Dispose() => Scoped.Value = _previous;
    }
}
