namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// The tuning FILENAMES the socket layer reads — a name, never a file read (tunables-ssot.md §7.2:
/// Core reads no file; hosts load and inject).
///
/// <para>⛔ <b>One constant per domain, here, so one reader cannot stay behind.</b> The revision a
/// reader loads used to be a literal filename at every call site; a swing to the next revision then
/// silently left whichever reader nobody remembered — the defect `circuit-topology` §4 exists to
/// close. <see cref="Current"/> is the sockets tuning the server, the validator and every test that
/// means "the shipped tuning" must name; a test that means an OLD revision as history keeps its
/// literal and says so.</para>
/// </summary>
public static class SocketTuningFiles
{
    /// <summary>
    /// The sockets tuning revision every production reader loads. Bumped in the SAME commit as the
    /// publish that makes the new revision current — the filename and the reader move together, so a
    /// published file with no reader and a reader pointed at the wrong file are both impossible.
    ///
    /// <para>v2 -> v3 (strain-splice-host SSH6.8): the published `comboPricing` section
    /// (`maxRatioToRarityRouteMilli` 1000 + `measuredAgainst`), whose provenance records the measurement
    /// the passing `items combo-budget --report` took.</para>
    /// </summary>
    public const string Current = "sockets.v3.json";

    /// <summary>
    /// The strain-splice tuning every reader loads (strain-splice-host SSH7.1). Still v1: the ladder
    /// publish is SSH7.7's, and this constant moves in that commit like every other revision constant.
    /// </summary>
    public const string StrainSplice = "strain-splice.v1.json";

    /// <summary>
    /// The materials tuning every reader loads (strain-splice-host SSH8.4). Moved v5 -> v6 by SSH8.5's
    /// combo-budget publish: the red report's derived `bore` 991 / `imbue` 991 / `forge-gem` 657 souls
    /// coefficients, published with the readers switched in the same commit (H7).
    /// </summary>
    public const string Materials = "materials.v6.json";

    /// <summary>
    /// The FILENAME revision <c>n</c> of a <c>&lt;domain&gt;.v{n}.json</c> tuning file — what
    /// `gk-core/tools/tuning/publish.py`'s own `latest_version` reads, and what
    /// <see cref="ComboPricingMeasuredAgainst"/>'s provenance fields must record.
    ///
    /// <para>⛔ <b>Never a file's internal <c>"version"</c> field.</b> The two disagree for sockets
    /// today: the previous revision carries a HIGHER internal version than the current one, because an
    /// earlier publish bumped it in place, so only the filename is monotonic — a measurement recorded
    /// against the internal field would refuse every revision that is in fact newer.</para>
    /// </summary>
    public static int RevisionOf(string tuningFileName)
    {
        if (string.IsNullOrWhiteSpace(tuningFileName))
            throw new SocketTuningRejection("a tuning filename is empty");
        var name = Path.GetFileName(tuningFileName);
        if (!name.EndsWith(".json", StringComparison.Ordinal))
            throw new SocketTuningRejection(
                $"'{tuningFileName}' is not a tuning filename — the shape is <domain>.v<n>.json");
        var stem = name[..^".json".Length];
        var dot = stem.LastIndexOf('.');
        var middle = dot < 0 ? "" : stem[(dot + 1)..];
        if (middle.Length < 2 || middle[0] != 'v' || !int.TryParse(middle[1..], out var revision) ||
            revision < 1)
            throw new SocketTuningRejection(
                $"'{tuningFileName}' carries no numeric revision — the shape is <domain>.v<n>.json");
        return revision;
    }
}
