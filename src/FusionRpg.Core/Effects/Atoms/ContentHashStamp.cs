using System.Globalization;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// What a run recorded about the content it resolved against: the registry version, the combined
/// hash, and a short digest per covered table.
///
/// <para><b>Why the per-table digests travel.</b> A cross-version comparison must "compare the
/// per-table digests both versions share" — with only a combined hash there is nothing to compare
/// and the rule collapses into a blanket refusal, which would hard-fail the whole Checkpoint D
/// corpus the moment E18 or E9 registers a table. They are truncated to
/// <see cref="TableDigestHexLength"/> because they exist to <i>attribute</i> a change; the full
/// combined hash is what decides whether one happened.</para>
/// </summary>
public sealed record ContentHashStamp(
    int SchemaVersion,
    string Hash,
    IReadOnlyDictionary<string, string> TableDigests)
{
    /// <summary>Short enough to keep a log row small, long enough that a collision is not a concern.</summary>
    public const int TableDigestHexLength = 16;

    /// <summary>What a human reads in a report: <c>content:a3f91c</c>.</summary>
    public string Short => "content:" + (Hash.Length >= 6 ? Hash[..6] : Hash);

    /// <summary><c>v1|&lt;hash&gt;|table=digest,table=digest</c> — the durable form.</summary>
    public string ToCompact()
    {
        var tables = string.Join(",", TableDigests
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + Truncate(kv.Value)));
        return $"v{SchemaVersion.ToString(CultureInfo.InvariantCulture)}|{Hash}|{tables}";
    }

    public static bool TryParse(string? compact, out ContentHashStamp stamp)
    {
        stamp = null!;
        if (string.IsNullOrWhiteSpace(compact)) return false;

        var parts = compact.Split('|');
        if (parts.Length != 3) return false;
        if (parts[0].Length < 2 || parts[0][0] != 'v') return false;
        if (!int.TryParse(parts[0][1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            return false;
        if (parts[1].Length == 0) return false;

        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parts[2].Length > 0)
        {
            foreach (var entry in parts[2].Split(','))
            {
                var eq = entry.IndexOf('=');
                if (eq <= 0 || eq == entry.Length - 1) return false;
                digests[entry[..eq]] = entry[(eq + 1)..];
            }
        }

        stamp = new ContentHashStamp(version, parts[1], digests);
        return true;
    }

    static string Truncate(string hex) =>
        hex.Length <= TableDigestHexLength ? hex : hex[..TableDigestHexLength];
}

/// <summary>How a stored stamp relates to the content loaded right now.</summary>
public enum ContentHashVerdict
{
    /// <summary>Same registry version, same hash — or nothing was stamped at all.</summary>
    Match = 0,

    /// <summary>Same registry version, different hash. Content moved under a run that must not be re-resolved.</summary>
    Mismatch,

    /// <summary>
    /// The covered set itself changed. <b>Not a refusal</b> — a table joining the registry is an
    /// expected, attributable event, and refusing on it would break every stamp made before the bump.
    /// </summary>
    RegistryChanged,

    /// <summary>A stamp was recorded but cannot be read. Treated as a refusal: unverifiable is not proven.</summary>
    Unreadable,
}

/// <summary>The verdict plus the attribution an operator needs to act on it.</summary>
public sealed record ContentHashComparison(
    ContentHashVerdict Verdict,
    string Reason,
    IReadOnlyList<string> ChangedTables,
    IReadOnlyList<string> AddedTables,
    IReadOnlyList<string> RemovedTables)
{
    /// <summary>Whether a replay or sweep must refuse rather than silently re-resolve.</summary>
    public bool ShouldRefuse => Verdict is ContentHashVerdict.Mismatch or ContentHashVerdict.Unreadable;

    static readonly string[] Nothing = Array.Empty<string>();

    /// <summary>
    /// Compare what a run stored against the content loaded now.
    ///
    /// <para>A null or empty stored stamp is a <see cref="ContentHashVerdict.Match"/>: rows written
    /// before this module existed carry no stamp, and refusing them would strand crash-recovery work
    /// that predates the feature. Same treatment the platform stamp already gives its own rows.</para>
    /// </summary>
    public static ContentHashComparison Compare(string? storedCompact, ContentHashStamp current)
    {
        if (string.IsNullOrWhiteSpace(storedCompact))
            return new ContentHashComparison(ContentHashVerdict.Match, "no stored content stamp",
                Nothing, Nothing, Nothing);

        if (!ContentHashStamp.TryParse(storedCompact, out var stored))
            return new ContentHashComparison(ContentHashVerdict.Unreadable,
                $"unreadable content stamp '{Clip(storedCompact)}'", Nothing, Nothing, Nothing);

        var changed = SharedTablesThatDiffer(stored, current);

        if (stored.SchemaVersion == current.SchemaVersion)
        {
            if (string.Equals(stored.Hash, current.Hash, StringComparison.Ordinal))
                return new ContentHashComparison(ContentHashVerdict.Match, "content unchanged",
                    Nothing, Nothing, Nothing);

            var where = changed.Count > 0 ? " in " + string.Join(", ", changed) : "";
            // Both hashes in full: the operator has to be able to tell which content each side ran
            // against, and a six-character prefix is for reading, not for comparing.
            return new ContentHashComparison(ContentHashVerdict.Mismatch,
                $"content hash moved{where}: stored {stored.Hash}, current {current.Hash}",
                changed, Nothing, Nothing);
        }

        // The covered set moved. Report what joined and what left from the stamps themselves, so a
        // build that has never heard of the stored version can still attribute the difference.
        var storedTables = stored.TableDigests.Keys.ToHashSet(StringComparer.Ordinal);
        var currentTables = current.TableDigests.Keys.ToHashSet(StringComparer.Ordinal);
        var added = currentTables.Except(storedTables).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var removed = storedTables.Except(currentTables).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var detail = changed.Count > 0
            ? $"shared tables changed: {string.Join(", ", changed)}"
            : "shared tables unchanged";

        return new ContentHashComparison(ContentHashVerdict.RegistryChanged,
            $"contentHashSchemaVersion {stored.SchemaVersion} -> {current.SchemaVersion} " +
            $"({stored.Short} -> {current.Short}); {detail}" +
            (added.Length > 0 ? $"; added {string.Join(", ", added)}" : "") +
            (removed.Length > 0 ? $"; removed {string.Join(", ", removed)}" : ""),
            changed, added, removed);
    }

    /// <summary>
    /// Table digests are stored truncated, so compare on the shorter of the two prefixes rather than
    /// calling every table changed the moment one side is abbreviated.
    /// </summary>
    static IReadOnlyList<string> SharedTablesThatDiffer(ContentHashStamp a, ContentHashStamp b)
    {
        var changed = new List<string>();
        foreach (var (name, left) in a.TableDigests.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!b.TableDigests.TryGetValue(name, out var right)) continue;
            var n = Math.Min(left.Length, right.Length);
            if (n == 0 || !string.Equals(left[..n], right[..n], StringComparison.Ordinal))
                changed.Add(name);
        }
        return changed;
    }

    static string Clip(string s) => s.Length <= 48 ? s : s[..48] + "…";
}
