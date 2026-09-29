namespace FusionRpg.Core.Stats.Derived;

/// <summary>One source's own contribution to a channel — retained exactly as authored, at whatever
/// <see cref="DerivedModifier.Op"/> it carries.</summary>
public sealed record DerivedContribution(string SourceId, DerivedModifierOp Op, double Value);

/// <summary>
/// aura-skill T11: per-source provenance retained alongside <see cref="DerivedComposer.Compose"/>,
/// so a channel can name where its value came from — GG-49's own question, *"why did my attack
/// drop,"* is unanswerable without this (today `Compose` folds a modifier list into one number per
/// channel and the per-source breakdown is gone the moment it returns).
///
/// <para><b>Alongside compose, never a second copy of it.</b> This groups the SAME
/// <see cref="DerivedModifier"/> list <see cref="DerivedComposer.Compose"/> already receives, by
/// channel, keeping every contribution exactly as authored — it does not re-implement
/// <see cref="DerivedComposer.ComposeChannel"/>'s per-kind op filtering (D6). A contribution that a
/// channel's compose kind does not actually read (the exact D6 shape `AtomRowValidator`, T2, rejects
/// at bind time) still shows up here — this bag answers "what tried to contribute," not "what the
/// fold used"; the fold's own answer is `Compose`'s single resulting number, unchanged.</para>
/// </summary>
public sealed class DerivedContributionBag
{
    readonly Dictionary<string, List<DerivedContribution>> _byChannel = new(StringComparer.Ordinal);

    public static DerivedContributionBag From(IEnumerable<DerivedModifier> modifiers)
    {
        var bag = new DerivedContributionBag();
        foreach (var m in modifiers)
        {
            if (!bag._byChannel.TryGetValue(m.ChannelId, out var list))
                bag._byChannel[m.ChannelId] = list = new List<DerivedContribution>();
            list.Add(new DerivedContribution(m.SourceId, m.Op, m.Value));
        }
        return bag;
    }

    /// <summary>Every contribution recorded for this channel, in the order the source modifier list
    /// carried them — two different sources on the same channel are two separate entries, never
    /// merged. An unknown/untouched channel returns an empty list, never null and never throws.</summary>
    public IReadOnlyList<DerivedContribution> ContributionsFor(string channelId) =>
        _byChannel.TryGetValue(channelId, out var list) ? list : Array.Empty<DerivedContribution>();

    public IReadOnlyCollection<string> Channels => _byChannel.Keys;
}
