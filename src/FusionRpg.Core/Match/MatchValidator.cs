using FusionRpg.Contracts;

namespace FusionRpg.Core.Match;

/// <summary>One offline fold step for <see cref="MatchValidator.Replay"/>.</summary>
public sealed class MatchReplayStep
{
    public MatchReplayStep(string kind, IReadOnlyDictionary<string, object>? payload = null)
    {
        Kind = kind;
        Payload = payload;
    }

    /// <summary>When set, calls <see cref="MatchRuntime.NotifyPaused"/> and ignores Kind.</summary>
    public MatchReplayStep(bool setPaused)
    {
        SetPaused = setPaused;
    }

    public string? Kind { get; }
    public IReadOnlyDictionary<string, object>? Payload { get; }
    public bool? SetPaused { get; }
}

/// <summary>
/// Offline Replay facade (W1-D). New MatchRuntime per Replay — isolated from LIVE.
/// Never references FusionRpg.Data / SQLite.
/// </summary>
public static class MatchValidator
{
    public static MatchSnapshot Replay(IEnumerable<MatchReplayStep> steps)
    {
        if (steps == null) throw new ArgumentNullException(nameof(steps));
        var rt = new MatchRuntime();
        foreach (var step in steps)
        {
            if (step == null) continue;
            if (step.SetPaused is { } paused)
            {
                rt.NotifyPaused(paused);
                continue;
            }

            if (string.IsNullOrWhiteSpace(step.Kind)) continue;
            rt.Apply(step.Kind, step.Payload);
        }

        return rt.ToSnapshot();
    }

    /// <summary>Fold capture envelopes (e.g. SimEngine events) into a fresh MatchRuntime.</summary>
    public static MatchSnapshot Replay(IEnumerable<EventEnvelope> events)
    {
        if (events == null) throw new ArgumentNullException(nameof(events));
        return Replay(events.Select(ToStep));
    }

    public static MatchReplayStep ToStep(EventEnvelope env)
    {
        if (env == null) return new MatchReplayStep("");
        return new MatchReplayStep(env.Kind ?? "", PayloadDict(env));
    }

    public static IReadOnlyDictionary<string, object>? PayloadDict(EventEnvelope env)
    {
        if (env == null) return null;
        Dictionary<string, object> dict;
        if (env.Payload is IReadOnlyDictionary<string, object> ro)
            dict = new Dictionary<string, object>(ro, StringComparer.OrdinalIgnoreCase);
        else if (env.Payload is IDictionary<string, object> d)
            dict = new Dictionary<string, object>(d, StringComparer.OrdinalIgnoreCase);
        else
            dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(env.MatchKey) &&
            !dict.ContainsKey("matchKey") &&
            !dict.ContainsKey("MatchKey"))
        {
            dict["matchKey"] = env.MatchKey!;
        }

        return dict.Count == 0 ? null : dict;
    }
}
