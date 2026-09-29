namespace FusionRpg.Core.Stats.Derived;

/// <summary>Resolved derived channel bag for one actor at Apply time.</summary>
public sealed class ActorDerivedSnapshot
{
    readonly Dictionary<string, double> _channels = new(StringComparer.Ordinal);

    public static ActorDerivedSnapshot Empty { get; } = new();

    public static ActorDerivedSnapshot FromValues(IEnumerable<KeyValuePair<string, double>> values)
    {
        var s = new ActorDerivedSnapshot();
        foreach (var (k, v) in values)
            s._channels[k] = v;
        return s;
    }

    public static ActorDerivedSnapshot StubNeutral() =>
        FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0)
        });

    public static ActorDerivedSnapshot AttackerLess() =>
        FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPowerOmni, 0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPowerDot, 0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPowerCc, 0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPowerContagion, 0)
        });

    public IReadOnlyDictionary<string, double> Channels => _channels;

    public double Get(string channelId, double defaultValue = 0) =>
        _channels.TryGetValue(channelId, out var v) ? v : defaultValue;

    public bool TryGet(string channelId, out double value) =>
        _channels.TryGetValue(channelId, out value);

    public double TierPower =>
        Get(DerivedStatChannels.ProgressionPower, 1.0) * Get(DerivedStatChannels.ProgressionRealm, 1.0);

    /// <summary>Replaces each named channel's value outright. Use for a genuine replacement (a
    /// fixture overriding a base profile — see <c>ActorDerivedProfiles</c>), never for a second
    /// producer contributing to a channel another producer already writes: two producers replacing
    /// the same channel means the second one silently erases the first (audit D1).</summary>
    public ActorDerivedSnapshot Overlay(IEnumerable<KeyValuePair<string, double>> extra)
    {
        var next = FromValues(_channels);
        foreach (var (k, v) in extra)
            next._channels[k] = v;
        return next;
    }

    /// <summary>Adds each named channel's value to whatever is already there. Use whenever the
    /// caller is CONTRIBUTING to a channel rather than replacing it — this is what makes two
    /// independent producers (e.g. a patron aura and a commander aura) compose instead of one
    /// silently overwriting the other (audit D1). The caller supplies only its own contribution;
    /// this method reads the existing value, so a caller must never also add the existing value
    /// itself or the contribution doubles.</summary>
    public ActorDerivedSnapshot OverlayAdd(IEnumerable<KeyValuePair<string, double>> extra)
    {
        var next = FromValues(_channels);
        foreach (var (k, v) in extra)
            next._channels[k] = next._channels.TryGetValue(k, out var existing) ? existing + v : v;
        return next;
    }

    internal void Set(string channelId, double value) => _channels[channelId] = value;
}
