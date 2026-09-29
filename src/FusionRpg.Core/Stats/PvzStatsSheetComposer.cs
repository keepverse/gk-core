using FusionRpg.Core.Combat.Element;

namespace FusionRpg.Core.Stats;

/// <summary>
/// Monitor-only sheet: Compose(Y0=0, bag). Not combat entity finals.
/// </summary>
public static class PvzStatsSheetComposer
{
    static readonly string[] Channels =
    {
        StatChannels.Hp, StatChannels.MaxHp, StatChannels.Atk, StatChannels.Defense,
        StatChannels.Arm1, StatChannels.Arm1Max, StatChannels.Arm2, StatChannels.Arm2Max
    };

    /// <summary>Canonical StatChannels id, or null if unknown.</summary>
    public static string? TryCanonicalizeChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;
        var trimmed = channel.Trim();
        foreach (var c in Channels)
        {
            if (string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return null;
    }

    public static bool IsKnownChannel(string? channel) => TryCanonicalizeOrDerivedChannel(channel) != null;

    /// <summary>Primary StatChannels id, or derived catalog channel, or null if unknown.</summary>
    public static string? TryCanonicalizeOrDerivedChannel(string? channel)
    {
        var primary = TryCanonicalizeChannel(channel);
        if (primary != null) return primary;

        if (string.IsNullOrWhiteSpace(channel)) return null;
        var trimmed = channel.Trim();
        return CachedDerivedRegistry().TryResolveChannel(trimmed, out _) ? trimmed : null;
    }

    /// <summary>
    /// E25's own idiom (<see cref="Derived.DerivedStatChannels.AllCombatChannelIds"/>'s
    /// <c>EnsureCacheUnlocked</c>) applied to the second instance of the same defect
    /// (spec-catalog-extension.md §6.3): this method used to call
    /// <see cref="Derived.DerivedStatRegistry.CreateDefault"/> — a fresh dictionary plus one
    /// <see cref="Derived.DerivedStatDef"/> per channel — on <b>every</b> call, on a path invoked per
    /// modifier row, and signalled the unknown case with a <b>thrown exception</b> caught one line
    /// later (the normal path for any primary channel).
    ///
    /// <para><b>Cached by reference identity against <see cref="ElementTable.Current"/>, not a bare
    /// <c>static readonly</c></b> — a plain static would break <see cref="ElementTable.UseScoped"/>,
    /// which tests rely on to swap rosters beside one another. A registry's content is a pure function
    /// of the active element table (<see cref="Derived.DerivedStatChannels.AllCombatChannelEntries"/>
    /// is itself keyed the same way), so this cache is exactly as fresh as rebuilding every call.</para>
    ///
    /// <para><b>Exception-as-control-flow removed</b>: <see cref="Derived.DerivedStatRegistry.TryResolveChannel"/>
    /// answers validity directly instead of a caught <see cref="Derived.UnknownDerivedChannelException"/>.</para>
    ///
    /// <para><b>AsyncLocal, not a shared static slot</b> (found 2026-08-25 re-running the full suite —
    /// see <see cref="Derived.DerivedStatChannels"/>'s matching fix and its doc comment for the full
    /// race description). <see cref="ElementTable.Current"/> is itself <c>AsyncLocal</c>-scoped, so a
    /// single shared cache keyed only by reference to it can be thrashed by two concurrently-running
    /// tests scoped to different rosters. One slot per scope avoids that by construction.</para>
    /// </summary>
    static readonly AsyncLocal<CacheSlot?> Local = new();

    readonly record struct CacheSlot(ElementTable Source, Derived.DerivedStatRegistry Registry);

    static Derived.DerivedStatRegistry CachedDerivedRegistry()
    {
        var current = ElementTable.Current;
        var slot = Local.Value;
        if (slot is { } s && ReferenceEquals(s.Source, current))
            return s.Registry;

        var registry = Derived.DerivedStatRegistry.CreateDefault();
        Local.Value = new CacheSlot(current, registry);
        return registry;
    }

    public static PvzStatsSheetResult Build(IEnumerable<StatModifier> mods)
    {
        var list = mods.Where(m => m != null).ToList();
        var strategy = new PhasedComposeStrategy();
        var channels = new List<PvzStatsChannelResult>();
        foreach (var ch in Channels)
        {
            var channelMods = list.Where(m => m.Channel == ch).ToList();
            if (channelMods.Count == 0) continue;
            var final = strategy.ComposeChannel(0, channelMods);
            channels.Add(new PvzStatsChannelResult
            {
                Channel = ch,
                Final = final,
                SourceCount = channelMods
                    .Select(m => $"{m.SourceKind}|{m.SourceId}")
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                Contributions = channelMods
            });
        }
        return new PvzStatsSheetResult { Channels = channels, Contributions = list };
    }

    public static ModifierOp ParseOp(string? op)
    {
        if (string.IsNullOrWhiteSpace(op)) return ModifierOp.Flat;
        return Enum.TryParse<ModifierOp>(op, ignoreCase: true, out var parsed) ? parsed : ModifierOp.Flat;
    }

    public static StatModifier ToStatModifier(
        string pluginId, string sourceKind, string sourceId, string channel, string op, double value, int priority) =>
        new()
        {
            PluginId = pluginId,
            SourceKind = sourceKind,
            SourceId = sourceId,
            Channel = channel,
            Op = ParseOp(op),
            Value = value,
            Priority = priority
        };
}

public sealed class PvzStatsSheetResult
{
    public IReadOnlyList<PvzStatsChannelResult> Channels { get; init; } = Array.Empty<PvzStatsChannelResult>();
    public IReadOnlyList<StatModifier> Contributions { get; init; } = Array.Empty<StatModifier>();
}

public sealed class PvzStatsChannelResult
{
    public string Channel { get; init; } = "";
    public double Final { get; init; }
    public int SourceCount { get; init; }
    public IReadOnlyList<StatModifier> Contributions { get; init; } = Array.Empty<StatModifier>();
}
