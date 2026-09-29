using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// The class system's whole balance surface, loaded from `tuning/aptitudes.v{n}.json`.
///
/// <para>Four blocks, and the split between them is the design:</para>
/// <list type="bullet">
///   <item><b>grant</b> — the point ECONOMY (how many points exist). <b>Free build: every point
///     costs one point</b>, so there is no price block — the player has no class to be outside of
///     (class-system-ideal.md §7a.3, withdrawn 2026-08-25).</item>
///   <item><b>read</b> — the two SCALE functions (PS-3). Tunable shape, not a hard-coded read.</item>
///   <item><b>familyRead</b> — the `unitClass` DECISION per family. A property of the CHANNEL
///     (what the formula compares it against), never of the edge that feeds it — which is why it
///     is one reviewable block rather than a flag repeated on 48 edges.</item>
///   <item><b>edges</b> — the DISTRIBUTION: channel ← aptitude, with a per-mille coefficient.</item>
/// </list>
///
/// <para><b>Every key is required.</b> A missing one is a load rejection naming it, never a built-in
/// default (tunables-ssot.md T5): a default is a number nobody chose that behaves like one somebody
/// did.</para>
/// </summary>
public sealed class AptitudeTuning
{
    public int SchemaVersion { get; set; }
    public int Version { get; set; }
    public GrantBlock? Grant { get; set; }
    public ReadBlock? Read { get; set; }
    public RecoveryBlock? Recovery { get; set; }
    public MitigationBlock? Mitigation { get; set; }
    public Dictionary<string, string>? FamilyRead { get; set; }
    public List<TunedEdge>? Edges { get; set; }

    /// <summary>Channels the design has RETIRED, mirrored from
    /// <c>gk-core/src/FusionRpg.Core/Stats/Derived/DerivedStatChannels.cs</c>'s <c>Retired</c> set (owner
    /// ruling 2026-09-18 retired <c>progression.bonus.atk</c>). Published tuning versions are
    /// immutable, so they keep naming the retired channel forever; both resolvers must DROP such an
    /// edge rather than reject the file or resolve it, or the parity test below compares a set Core
    /// no longer emits against a set this tool still does. Closed vocabulary — a second entry is a
    /// reviewed change made in BOTH files, which <c>RetiredParity</c> below asserts.</summary>
    public static readonly IReadOnlySet<string> Retired = new HashSet<string>(StringComparer.Ordinal)
    {
        "progression.bonus.atk",
    };

    public sealed class GrantBlock
    {
        public double? AptitudePointsPerTheta { get; set; }
        public double? SkillPointsPerTheta { get; set; }
    }

    /// <summary>The termination-invariant dial (class-system-ideal.md §5d). One multiplier over every
    /// recovery family, because <c>r = recovery/peerDamage</c> is a GLOBAL ratio — nerfing one regen
    /// edge at a time cannot target it.</summary>
    public sealed class RecoveryBlock
    {
        public long? ScaleMilli { get; set; }
        public long? TargetRecoveryShareMilli { get; set; }
        public List<string>? Families { get; set; }
    }

    /// <summary>Recovery's own sibling dial (class-system-todo.md P8.3, AptitudeMitigation in
    /// gk-core/src/FusionRpg.Core/Stats/Aptitudes/AptitudeTuning.cs) for non-recovery survivability —
    /// combat.defense/dodge/parry/block/absorption/heal. Ported 2026-08-27: this tool's own resolver
    /// was blind to this dial even after aptitudes.v2.json shipped it (P3.4/P8.1/Checkpoint 8's shared
    /// finding) — ScaleFor below now mirrors Core's AptitudeResolver.EffectiveKMilli exactly.</summary>
    public sealed class MitigationBlock
    {
        public long? ScaleMilli { get; set; }
        public List<string>? Families { get; set; }
    }

    public sealed class ReadBlock
    {
        public ContestRead? Contest { get; set; }
        public MagnitudeRead? Magnitude { get; set; }

        /// <summary>species-progression step 6.1 (R21) — optional, like every dial this tool already
        /// treats as "absent means the pre-R21 identity": `aptitudes.v1`-`v8` never carried this block,
        /// and this tool's own "every key is required" contract is about a VERSION's own present
        /// blocks, not about forcing every historical version to retroactively carry a key its era
        /// never had.</summary>
        public LayerWeightBlock? LayerWeightMilliByScope { get; set; }
    }

    public sealed class ContestRead
    {
        public double? SpanPoints { get; set; }
        public long? ShareExponentMilli { get; set; }
    }

    public sealed class MagnitudeRead
    {
        public long? ShareExponentMilli { get; set; }
    }

    /// <summary>species-progression step 6.1 (R21) — this tool's whole domain models ONE aptitude's
    /// own corner (`ResolverMatchesSimulatorTests`'s own seeded allocation is `AllocationScope.Commander`
    /// exclusively), so only <see cref="Commander"/> is ever read here; the other three scopes are
    /// carried for completeness with the shipped file's own shape, unread by this tool.</summary>
    public sealed class LayerWeightBlock
    {
        public long? Commander { get; set; }
        public long? CreatureType { get; set; }
        public long? Aspect { get; set; }
        public long? UniqueCreature { get; set; }
    }

    /// <summary>An edge, or a <c>_group</c> divider. Dividers carry no channel and are dropped at
    /// load: the edge list is long enough that reading it without section headings is genuinely
    /// harder, and JSON has no comment the publisher tool would preserve.</summary>
    public sealed record TunedEdge(string? Channel, string? Source, long KMilli)
    {
        public bool IsDivider => string.IsNullOrWhiteSpace(Channel);
    }

    /// <summary>Real edges only — dividers and any future annotation row filtered out, and any edge
    /// naming a retired channel dropped exactly as <c>AptitudeTuningLoader.Parse</c> drops it.</summary>
    public IEnumerable<TunedEdge> RealEdges =>
        Edges!.Where(e => !e.IsDivider && !Retired.Contains(e.Channel!));

    /// <summary>Keys starting with <c>_</c> are prose for the reader, never data.</summary>
    static bool IsNote(string key) => key.StartsWith('_');

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static AptitudeTuning Load(string nameOrPath)
    {
        var path = AptitudeModel.Resolve(nameOrPath, "tuning");
        var t = JsonSerializer.Deserialize<AptitudeTuning>(File.ReadAllText(path), Options)
                ?? throw new AptitudeTuningRejection($"{path}: empty");
        t.Validate(path);
        return t;
    }

    void Validate(string path)
    {
        void Need(bool ok, string key)
        {
            if (!ok) throw new AptitudeTuningRejection($"{path}: missing required key '{key}'");
        }

        Need(Grant?.AptitudePointsPerTheta is > 0, "grant.aptitudePointsPerTheta");
        Need(Grant?.SkillPointsPerTheta is >= 0, "grant.skillPointsPerTheta");
        Need(Read?.Contest?.SpanPoints is > 0, "read.contest.spanPoints");
        Need(Read?.Contest?.ShareExponentMilli is > 0, "read.contest.shareExponentMilli");
        Need(Read?.Magnitude?.ShareExponentMilli is > 0, "read.magnitude.shareExponentMilli");
        Need(Recovery?.ScaleMilli is >= 0, "recovery.scaleMilli");
        Need(Recovery?.TargetRecoveryShareMilli is > 0, "recovery.targetRecoveryShareMilli");
        Need(Recovery?.Families is { Count: > 0 }, "recovery.families");
        Need(Mitigation?.ScaleMilli is >= 0, "mitigation.scaleMilli");
        Need(Mitigation?.Families is { Count: > 0 }, "mitigation.families");
        Need(FamilyRead is { Count: > 0 }, "familyRead");
        Need(Edges is { Count: > 0 }, "edges");

        // Every edge must have a read mode, and the only place it can come from is familyRead.
        // An unclassified channel is exactly the `unitClass: null` blocker this file exists to close,
        // so it rejects rather than picking one — a guessed scale is invisible and moves every number.
        // FamilyRead still carries the retired key (the file is immutable); drop it here too, so the
        // lookup below agrees with Core's parsed surface rather than the raw text.
        var familyReadLive = FamilyRead!.Where(kv => !Retired.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var unclassified = RealEdges
            .Where(e => FamilyOfLive(familyReadLive, e.Channel!) is null)
            .Select(e => e.Channel!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unclassified.Count > 0)
            throw new AptitudeTuningRejection(
                $"{path}: familyRead has no entry for {string.Join(", ", unclassified)} — " +
                "the read mode is a property of the channel and cannot be inferred.");

        var sourceless = RealEdges.Where(e => string.IsNullOrWhiteSpace(e.Source)).Select(e => e.Channel!).ToList();
        if (sourceless.Count > 0)
            throw new AptitudeTuningRejection($"{path}: edge(s) with no source: {string.Join(", ", sourceless)}");
    }

    /// <summary>
    /// Which <c>familyRead</c> row governs a channel, or null if none does.
    ///
    /// <para><b>Exact match first, then strip one axis suffix.</b> `combat.parry.strength.omni` →
    /// `combat.parry.strength` (the element is the arena, never part of the scale class), and
    /// `resource.max.hp` → `resource.max`. But `move.range` and `progression.xpRate` carry NO axis,
    /// so blind stripping would look up `move` and `progression` and reject a channel that is
    /// perfectly well classified. Try the whole id before assuming it has a suffix.</para>
    /// </summary>
    public string? FamilyOf(string channel) => FamilyOfLive(FamilyRead!, channel);

    /// <summary><see cref="FamilyOf"/> over an explicitly supplied table — the retired keys are
    /// dropped before validation, so a retired channel's own <c>familyRead</c> row is not what makes
    /// it look classified.</summary>
    static string? FamilyOfLive(IReadOnlyDictionary<string, string> familyRead, string channel)
    {
        if (familyRead.ContainsKey(channel)) return channel;
        var dot = channel.LastIndexOf('.');
        if (dot <= 0) return null;
        var stripped = channel[..dot];
        return familyRead.ContainsKey(stripped) ? stripped : null;
    }

    /// <summary>
    /// Materialize into the resolver the rest of the tool already drives. One resolver, so the
    /// analytic predictor and the simulator can never disagree about what a build's channels ARE —
    /// only about what happens to them.
    /// </summary>
    public AptitudeModel ToModel(string name)
    {
        var recoveryScale = Recovery!.ScaleMilli!.Value / 1000.0;
        var mitigationScale = Mitigation!.ScaleMilli!.Value / 1000.0;
        // species-progression step 6.1 (R21): this tool's every corner is a Commander-scope-only
        // build, so the Commander layer weight applies to EVERY edge, independent of the
        // recovery/mitigation dial -- absent (aptitudes.v1-v8) means the pre-R21 identity, 1000.
        // Applied as a plain continuous multiply, matching how recovery/mitigation are already
        // carried here (this tool's own doc comment: "carries share^gamma and the scale factor as
        // full-precision doubles throughout" -- a deliberate, documented divergence from Core's own
        // discrete integer rounding, not something this task's own scope reconciles further; trying a
        // discrete round-then-weight order here measurably did not close the gap
        // ResolverMatchesSimulatorTests already re-measured after this change, since the gap's real
        // source is EffectiveKMilli's OWN pre-existing integer-division truncation on this seed's
        // several small-kMilli recovery edges, which the weight now makes proportionally larger, not a
        // new discretization this tool introduces).
        var commanderWeightScale = (Read?.LayerWeightMilliByScope?.Commander ?? 1000) / 1000.0;
        bool IsRecovery(string channel) =>
            Recovery.Families!.Any(f => channel.StartsWith(f, StringComparison.Ordinal));
        bool IsMitigation(string channel) =>
            Mitigation.Families!.Any(f => channel.StartsWith(f, StringComparison.Ordinal));
        // Mirrors Core's AptitudeResolver.EffectiveKMilli exactly: recovery is checked first and, if it
        // matches, mitigation is never even considered for that edge -- the two dials are mutually
        // exclusive per channel, not stacked. R21's commander weight is a SEPARATE, always-applied
        // factor (Core's own resolver applies EffectiveKMilli's dial and the layer weight as two
        // independent multiplications too -- see AptitudeResolver.Resolve).
        double ScaleFor(string channel) =>
            (IsRecovery(channel) ? recoveryScale : IsMitigation(channel) ? mitigationScale : 1.0) * commanderWeightScale;

        var edges = RealEdges.Select(e => new AptitudeEdge(
            e.Channel!, e.Source!,
            e.KMilli / 1000.0 * ScaleFor(e.Channel!),
            FamilyRead![FamilyOf(e.Channel!)!].Equals("magnitude", StringComparison.OrdinalIgnoreCase)
                ? ReadMode.Magnitude
                : ReadMode.Contest)).ToList();

        return new AptitudeModel
        {
            Name = name,
            Description = $"aptitudes.v{Version} — {edges.Count} edges, "
                          + $"contest span {Read!.Contest!.SpanPoints}, "
                          + $"gamma contest {Read.Contest.ShareExponentMilli / 1000.0:0.###} / "
                          + $"magnitude {Read.Magnitude!.ShareExponentMilli / 1000.0:0.###}, "
                          + $"recovery x{Recovery.ScaleMilli!.Value / 1000.0:0.##}, "
                          + $"mitigation x{Mitigation.ScaleMilli!.Value / 1000.0:0.##}",
            Edges = edges,
            ContestSpan = Read.Contest.SpanPoints!.Value,
            ContestShareExponent = Read.Contest.ShareExponentMilli!.Value / 1000.0,
            MagnitudeShareExponent = Read.Magnitude.ShareExponentMilli!.Value / 1000.0
        };
    }
}

public sealed class AptitudeTuningRejection : Exception
{
    public AptitudeTuningRejection(string message) : base(message) { }
}
