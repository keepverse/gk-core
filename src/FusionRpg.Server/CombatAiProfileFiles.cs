using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Server;

/// <summary>
/// combat-ai `replay-identity` (module 8, CAI2.2, spec-replay-identity.md §2): the Server's
/// <see cref="ICombatAiProfileSource"/> — it loads EVERY published `data/tuning/combat-ai.v{n}.json` at
/// startup and answers from that map, so a match pinned to an older version can be re-resolved under
/// the set it actually used. Core reads no file; the host injects (`tunables-ssot.md` §7.2 — the same
/// Core/SDK split `spec-mode-profile.md` uses for `mode-profiles.v1.json`).
///
/// <para><b>Why every version, not a retention window.</b> `publish.py` leaves `v{n}` on disk untouched
/// and writes `v{n+1}` beside it, so the cost is one small parse per published version. A refused
/// replay of a real expedition collect is a player-visible failure; disk is not the constraint
/// (spec §Open questions 2, recommended default (a)).</para>
///
/// <para><b><see cref="ForVersion"/> returning null is a REFUSAL, never a fallback.</b> The caller turns
/// it into `profile.unavailable:v{n}` and refuses the replay; resolving a pinned match on
/// <see cref="Current"/> is the exact defect this module exists to close — a published rebalance would
/// silently rewrite a battle that already happened.</para>
///
/// <para><b>Load-time refusals, all loud and all before any match runs.</b> A file whose NAME version
/// disagrees with the `version` field it carries, a version published twice, and a tuning directory with
/// no `combat-ai.v*.json` at all each throw <see cref="CombatAiTuningRejection"/> naming the file. The
/// name/version check is the hand-edit ban turned into a detection at the earliest possible point:
/// `publish.py` always writes `combat-ai.v{n}.json` carrying `version: n`, so a disagreement means the
/// file was renamed or edited in place. (The spec's step 3 catches the OTHER shape — a file edited in
/// place under an unchanged version and name — by comparing the loaded set's digest against the stamp a
/// match recorded; that comparison belongs to the pin resolution, not here.)</para>
///
/// <para><b>Order-independent by construction.</b> Versions are keyed by number, the map is built from a
/// sorted list, and duplicates are rejected rather than "last one wins" — so the same directory produces
/// the same map whatever order the filesystem enumerates it in.</para>
///
/// <para><b>The pin resolution that consumes this source is <see cref="CombatAiProfilePin"/> below</b>,
/// in this same file: a source and the one procedure that asks it a question are one design, and
/// splitting them across files would invite a second copy of the procedure (the fork the SOLID rule
/// bans).</para>
/// </summary>
public sealed class CombatAiProfileFiles : ICombatAiProfileSource
{
    /// <summary>The published-file name prefix, shared with `publish.py`'s own `"%s.v%d.json"`.</summary>
    public const string FilePrefix = "combat-ai.v";

    const string FileSuffix = ".json";

    readonly Dictionary<int, CombatAiTuning> _byVersion = new();

    /// <summary>
    /// Loads every published version in <paramref name="tuningDirectory"/>. Throws
    /// <see cref="CombatAiTuningRejection"/> for a malformed document (the loader's own rejection), a
    /// name/version disagreement, a duplicate version, or an empty set.
    /// </summary>
    public CombatAiProfileFiles(string tuningDirectory)
    {
        if (string.IsNullOrWhiteSpace(tuningDirectory))
            throw new ArgumentException("tuning directory is required", nameof(tuningDirectory));
        if (!Directory.Exists(tuningDirectory))
            throw new CombatAiTuningRejection($"combat-ai profiles: no tuning directory '{tuningDirectory}'");

        var found = new List<(int Version, string Path, CombatAiTuning Tuning)>();
        foreach (var path in Directory.GetFiles(tuningDirectory, FilePrefix + "*" + FileSuffix))
        {
            var name = Path.GetFileName(path);
            if (!TryVersionOfFileName(name, out var nameVersion)) continue; // not a published combat-ai file

            var tuning = CombatAiTuningLoader.Parse(File.ReadAllText(path));
            if (tuning.Version != nameVersion)
                throw new CombatAiTuningRejection(
                    $"combat-ai profiles: '{name}' carries version {tuning.Version} — a published file's name " +
                    "and its version field must agree (publish.py writes both from one counter; a " +
                    "disagreement means the file was renamed or hand-edited)");

            foreach (var seen in found)
                if (seen.Version == nameVersion)
                    throw new CombatAiTuningRejection(
                        $"combat-ai profiles: version {nameVersion} is published twice — " +
                        $"'{Path.GetFileName(seen.Path)}' and '{name}'");

            found.Add((nameVersion, path, tuning));
        }

        if (found.Count == 0)
            throw new CombatAiTuningRejection(
                $"combat-ai profiles: no '{FilePrefix}*.json' under '{tuningDirectory}'");

        // Sorted so the map — and therefore Current — is a function of the SET, never of the order the
        // filesystem handed the files over.
        found.Sort((a, b) => a.Version.CompareTo(b.Version));
        foreach (var (version, _, tuning) in found)
            _byVersion[version] = tuning;

        Current = _byVersion[found[^1].Version];
    }

    /// <summary>The newest published set — the one a FRESH match resolves under and whose stamp it
    /// records.</summary>
    public CombatAiTuning Current { get; }

    /// <summary>The set a PINNED match resolves under, or <c>null</c> when this host cannot supply that
    /// version. Null is a refusal: the caller returns `profile.unavailable:v{n}` and never falls back to
    /// <see cref="Current"/>.</summary>
    public CombatAiTuning? ForVersion(int tuningVersion) =>
        _byVersion.TryGetValue(tuningVersion, out var tuning) ? tuning : null;

    /// <summary>The versions this host can supply, ascending. A reading, not a contract — the pin asks
    /// <see cref="ForVersion"/>, never this.</summary>
    public IReadOnlyList<int> Versions
    {
        get
        {
            var versions = new List<int>(_byVersion.Keys);
            versions.Sort();
            return versions;
        }
    }

    /// <summary>`combat-ai.v{n}.json` → <c>n</c>; anything else → false. Strict on purpose: the glob that
    /// feeds it is `combat-ai.v*.json`, which would also match a future `combat-ai.vault.json`.</summary>
    static bool TryVersionOfFileName(string fileName, out int version)
    {
        version = 0;
        if (!fileName.StartsWith(FilePrefix, StringComparison.Ordinal)) return false;
        if (!fileName.EndsWith(FileSuffix, StringComparison.Ordinal)) return false;
        var digits = fileName.Substring(FilePrefix.Length, fileName.Length - FilePrefix.Length - FileSuffix.Length);
        if (digits.Length == 0) return false;
        foreach (var c in digits)
            if (c is < '0' or > '9') return false;
        return int.TryParse(digits, out version);
    }
}

/// <summary>
/// combat-ai `replay-identity` (module 8, CAI2.2, spec-replay-identity.md §2): the ONE five-step
/// resolution a replay performs against a row's stored <c>combat_ai_profile</c> stamp. It lives beside
/// <see cref="CombatAiProfileFiles"/> because the two are one decision — the source answers the
/// question this class asks — and because five call sites in two owners (<see cref="WebMatchService"/>'s
/// two correlation-replay branches and its boot sweep, <see cref="DelveBattleSessionManager"/>'s two
/// resume entries) must reach ONE procedure. Five copies would be the fork the SOLID rule bans, and
/// this row's stop condition names it: never a second policy resolver.
///
/// <para><b>It resolves a profile SET, not a profile row.</b> <see cref="CombatAiProfilePolicy"/> still
/// owns the single place a `place/role` is looked up inside a set; nothing here re-implements that,
/// and nothing here changes what a resolve decides. The only production consumer of that policy today
/// is the siege path (<c>BattleRunState</c>'s <c>AiPlace.Siege</c> row), which persists no match log at
/// all — so resolving under the pinned set is behaviourally identical to resolving under today's
/// default. That is exactly why the report carries no new field and no golden moves. The pin becomes
/// load-bearing when combat-ai profiles reach the web/delve decision path
/// (<c>auto-policy-switch</c>); what this row ships is the refusal, not a substitution.</para>
/// </summary>
public static class CombatAiProfilePin
{
    /// <summary>The version a match pinned cannot be supplied from disk. Carries the version, so the
    /// refusal names WHICH pin failed (spec §2 step 4).</summary>
    public const string UnavailablePrefix = "profile.unavailable:v";

    /// <summary>The pinned version exists but its published file was edited in place under the same
    /// version number — the hand-edit ban turned into a detection (spec §2 step 3).</summary>
    public const string Mismatch = "profile.mismatch";

    /// <summary>A stamp was recorded and cannot be read: unverifiable is not proven (spec §2 step 5).</summary>
    public const string Unreadable = "profile.unreadable";

    /// <summary>
    /// The five steps, in the spec's order, each refusing rather than substituting
    /// <see cref="ICombatAiProfileSource.Current"/>:
    /// <list type="number">
    /// <item>a NULL stamp is the legacy shape (<see cref="CombatAiProfilePinResolution.IsLegacy"/>) —
    /// today's default policy, no refusal. "Not stamped" never means "stamped with the current
    /// profile".</item>
    /// <item>unparseable → <see cref="Unreadable"/>, checked BEFORE the lookup because a version
    /// cannot be read out of a stamp that will not parse.</item>
    /// <item><c>ForVersion</c> null → <c>profile.unavailable:v{n}</c>. A null source answers the same
    /// way: a host with no source can supply no version, and quietly resolving on <c>Current</c> is
    /// the drift this module exists to close.</item>
    /// <item>the supplied set's own stamp differs → <see cref="Mismatch"/>, naming the profile that
    /// moved.</item>
    /// <item>otherwise → the pinned set. This is the pin.</item>
    /// </list>
    /// </summary>
    public static CombatAiProfilePinResolution Resolve(ICombatAiProfileSource? source, string? storedCompact)
    {
        if (string.IsNullOrWhiteSpace(storedCompact)) return CombatAiProfilePinResolution.Legacy;

        if (!ContentHashStamp.TryParse(storedCompact, out var stored))
            return CombatAiProfilePinResolution.Refused(
                Unreadable, $"unreadable combat-ai profile stamp '{Clip(storedCompact)}'");

        var requested = stored.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pinned = source?.ForVersion(stored.SchemaVersion);
        if (pinned is null)
            return CombatAiProfilePinResolution.Refused(
                UnavailablePrefix + requested,
                $"this host supplies combat-ai versions {DescribeSupplied(source)}: not v{requested}");

        // A source that hands back a set whose OWN version is not the one asked for is not addressing
        // pins correctly at all. Caught here deliberately: `ContentHashComparison`'s "the covered set
        // moved is not a refusal" rule is about a content REGISTRY gaining a table, and it does not
        // license a pin that names one exact version to resolve against a different one.
        if (pinned.Version != stored.SchemaVersion)
            return CombatAiProfilePinResolution.Refused(Mismatch,
                $"combat-ai v{requested} was requested but v{pinned.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)} was supplied");

        var check = CombatAiProfileIdentity.Compare(storedCompact, CombatAiProfileIdentity.StampOf(pinned));
        if (check.ShouldRefuse)
            return CombatAiProfilePinResolution.Refused(Mismatch, check.Reason);

        return CombatAiProfilePinResolution.Pinned(pinned);
    }

    /// <summary>
    /// The stamp a FRESH row records: the compact form of the set this host resolves under, written by
    /// the SAME call that writes `ruleset_version` (spec §3's invariant, unreachable by construction
    /// rather than by a test that has to be re-derived on the next version bump).
    ///
    /// <para><b>Null when the host has no source</b> — and null means on a write exactly what it means
    /// on a read: "this match recorded no combat-ai profile", the legacy shape. A hand-built test host
    /// that never wires a source writes a row identical in meaning to a pre-combat-ai one, rather than
    /// a fabricated stamp it could not honour. Production wires the source in <c>Program.cs</c>.</para>
    /// </summary>
    public static string? StampFor(ICombatAiProfileSource? source) =>
        source is null ? null : CombatAiProfileIdentity.StampOf(source.Current).ToCompact();

    /// <summary>Enough of a foreign string to identify it, never enough to fill a log line.</summary>
    static string Clip(string text) => text.Length <= 64 ? text : text[..64] + "…";

    /// <summary>What this host CAN supply, for a refusal an operator has to act on. Never the
    /// "newest" — a refusal that named only the newest would read as "it fell back", which is the
    /// behaviour being banned.</summary>
    static string DescribeSupplied(ICombatAiProfileSource? source) => source switch
    {
        null => "none (no profile source is wired into this host)",
        CombatAiProfileFiles files => string.Join(",", files.Versions.Select(v =>
            "v" + v.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        _ => "an unnamed set",
    };
}

/// <summary>
/// What <see cref="CombatAiProfilePin.Resolve"/> decided. Three states, never two: a row either
/// resolved under a named set, carries no stamp at all (the legacy shape), or is refused with a named
/// reason. There is deliberately no "resolved under something else" state — that is the drift.
/// </summary>
/// <param name="Tuning">The set the replay resolved under; null for the legacy and refused states.</param>
/// <param name="Refusal">The lowercase dotted token a player sees (<c>profile.unavailable:v2</c>,
/// <c>profile.mismatch</c>, <c>profile.unreadable</c>); null when nothing refused.</param>
/// <param name="Detail">The operator-facing attribution — which profile moved, which hashes differ, what
/// this host can supply. Never returned to a player and never folded into the token, which stays
/// stable to match on.</param>
public sealed record CombatAiProfilePinResolution(CombatAiTuning? Tuning, string? Refusal, string? Detail)
{
    /// <summary>No stamp on the row: the pre-combat-ai shape, resolved under today's default policy.</summary>
    public static CombatAiProfilePinResolution Legacy { get; } = new(null, null, null);

    /// <summary>The pin resolved: this is the set the match must be re-resolved under.</summary>
    public static CombatAiProfilePinResolution Pinned(CombatAiTuning tuning) =>
        new(tuning ?? throw new ArgumentNullException(nameof(tuning)), null, null);

    /// <summary>A named refusal. The caller returns <see cref="Refusal"/> to the player and logs
    /// <see cref="Detail"/> — the two are separate on purpose, so the token stays matchable.</summary>
    public static CombatAiProfilePinResolution Refused(string refusal, string detail) =>
        new(null, refusal ?? throw new ArgumentNullException(nameof(refusal)), detail);

    /// <summary>Whether the replay must be refused rather than re-resolved.</summary>
    public bool IsRefused => Refusal != null;

    /// <summary>Whether the row carried no stamp — a reading of history, never an error.</summary>
    public bool IsLegacy => Refusal == null && Tuning == null;
}
