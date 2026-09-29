using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Uniques;

/// <summary>
/// What the corpus checks need to know about everything they are not — resolved by the caller, because
/// Core does no I/O and holds no registry of its own.
/// </summary>
/// <param name="BaseType">
/// <c>baseTypeId → (roleId, frame)</c>. A unique carries no role of its own; it occupies the role of
/// the base type it is built on, so every role rule resolves through here.
/// </param>
/// <param name="RarityOrdinal"><c>rarityId → ordinal</c>, from the seeded ladder.</param>
/// <param name="RungWindow"><c>rarityId → the tier window + count-band floor</c>, for pricing.</param>
/// <param name="PowerAxes">`core.v1.json powerCategories` — the five, closed.</param>
/// <param name="CounterPressureConditions">`core.v1.json counterPressure.conditions` — the closed list,
/// each mapped to a predicate leaf the atom layer already ships.</param>
/// <param name="SeverityBands">`core.v1.json counterPressure.severityBands`.</param>
/// <param name="IsSetMember">Whether an <c>item_set_member</c> row references this container id.</param>
/// <param name="FamilyFrames">
/// <c>familyId → the frames that family may sit on</c>, from the affix-family corpus. Used for §3.5's
/// <b>physics</b> carve-out, which is not the same rule as its taste carve-out: a unique may bypass the
/// frame filter where the reason is flavour, and may <b>not</b> where the reason is that the Unity
/// field does not exist. Optional — omitting it registers no check, matching every other opt-in
/// delegate in this program.
/// </param>
public sealed record UniqueCorpusView(
    Func<string, (string RoleId, ItemFrame Frame)?> BaseType,
    Func<string, int?> RarityOrdinal,
    Func<string, RarityRungWindow?> RungWindow,
    IReadOnlyCollection<string> PowerAxes,
    IReadOnlyCollection<string> CounterPressureConditions,
    IReadOnlyCollection<string> SeverityBands,
    Func<string, bool>? IsSetMember = null,
    Func<string, IReadOnlyCollection<string>?>? FamilyFrames = null);

/// <summary>One rejection, carrying the seed it came from so a 144-row report is actionable.</summary>
public readonly record struct UniqueCorpusFinding(string SeedId, string Partition, AtomRejection Rejection);

/// <summary>
/// One <c>unique</c> entry in a drop table, as the shipped drop corpus writes it: the table's id and
/// the <b>seed</b> id it references.
/// </summary>
/// <param name="IsGeneralChannel">
/// True when this table is the general drop channel (ssot-uniques.md §4.5's first row) rather than a
/// source-locked or deterministic one. ⚠ <b>Supplied by the caller, because the shipped drop-table
/// schema carries no channel marker</b> — `entry-shapes.md` §9 states the band→channel rule but the
/// row has no field that says which channel a table is. Inventing one here would be this module
/// authoring another lane's schema; naming the gap is the honest half.
/// </param>
public readonly record struct UniqueDropReference(string TableId, string UniqueSeedId, bool IsGeneralChannel);

/// <summary>
/// ssot-uniques.md §6.4: <b>the cross-row checks must be import-phase, never load-phase</b> — they are
/// properties of the catalog, not of a row. A per-row load check cannot see the other 143 and would
/// pass every one of them individually while the grid collided.
///
/// <para>Four cross-row rules (§3.7 device 4): one unique per <c>(role, rung band, power axis)</c>; none
/// on either <c>jewel-minor</c>; at most 8 of 15 roles per frame; never a set member. Plus the per-row
/// rules that are checkable from the seed alone — rung floor, reachability, shape, and whether the
/// counter-pressure DECLARATION is well formed against `core.v1.json`'s own closed vocabulary.</para>
///
/// <para><b>The rung band is the partition's key, not the entry's rung.</b>
/// `naming.v1.json idNamespaces.uniques` allocates <c>(themeId, rungBandLowOrdinal)</c> and each band
/// spans two rungs; splitting the grid by rung instead would double it from 40 slots per band to 80 and
/// quietly retire "exactly saturated at 144", which is the reason the allocation is a Latin square
/// rather than a guideline.</para>
///
/// <para>⛔ <b>The budget and parity devices are NOT here.</b> The seed corpus authors no
/// <c>budget_ae</c> — seed-contract §3 forbids numbers in a seed — so a budget refusal at this layer
/// would be refusing content against a price this module invented rather than one an author declared.
/// The hard budget check lives in <see cref="UniqueValidator"/>, where a declared <c>budget_ae</c>
/// exists; the seed corpus gets <see cref="UniqueCorpusReport"/>, which measures and says so.</para>
/// </summary>
public static class UniqueCorpusValidator
{
    public static IReadOnlyList<UniqueCorpusFinding> Validate(
        IReadOnlyList<UniqueSeed> corpus, UniqueCorpusView view, UniqueTuning tuning)
    {
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (view is null) throw new ArgumentNullException(nameof(view));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        UniqueRules.EnsureRegistered();

        var findings = new List<UniqueCorpusFinding>();
        var axisSlots = new Dictionary<string, UniqueSeed>(StringComparer.Ordinal);
        var rolesByFrame = new Dictionary<ItemFrame, Dictionary<string, UniqueSeed>>();
        var seenSeedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in corpus)
        {
            if (!seenSeedIds.Add(s.SeedId))
                findings.Add(Find(s, UniqueRules.CorpusMalformed,
                    $"seed id '{s.SeedId}' appears twice; the (themeId, rungBandLowOrdinal, seq) template " +
                    "makes a collision structurally impossible, so a duplicate is an operational mistake"));

            // ---- shape, from the seed alone -------------------------------------------------------
            if (s.TotalRolls > UniqueLimits.MaxTotalRolls)
                findings.Add(Find(s, UniqueRules.Shape,
                    $"declares {s.TotalRolls} variance slots; a unique draws at most {UniqueLimits.MaxTotalRolls}"));

            if (s.FixedAtoms.Count > tuning.MaxIdentityAtoms)
                findings.Add(Find(s, UniqueRules.Shape,
                    $"{s.FixedAtoms.Count} identity atoms exceeds the readability cap of {tuning.MaxIdentityAtoms}"));

            // ---- counter-pressure DECLARATION well-formedness --------------------------------------
            CheckCounterPressureDeclaration(s, view, findings);

            // ---- power axis -------------------------------------------------------------------------
            if (!view.PowerAxes.Contains(s.PowerAxis, StringComparer.Ordinal))
            {
                findings.Add(Find(s, UniqueRules.CorpusMalformed,
                    $"powerAxis '{s.PowerAxis}' is not one of the five power categories: " +
                    string.Join(", ", view.PowerAxes)));
            }

            // ---- rung floor and reachability ---------------------------------------------------------
            var ordinal = view.RarityOrdinal(s.RarityId);
            if (ordinal is null)
            {
                findings.Add(Find(s, UniqueRules.CorpusMalformed,
                    $"rarity '{s.RarityId}' is not on the seeded ladder"));
            }
            else
            {
                // D4.23 (spec-unique-pipeline.md §5 point 5): "the 95 are enabled: false ... never
                // re-runged, never deleted" -- a disabled anchor sits inertly below a raised floor
                // rather than failing import. Every OTHER per-row and cross-row check still runs: a
                // disabled anchor is still expected to be a well-formed row, only not currently offered.
                if (s.Enabled && !tuning.IsRungEligible(ordinal.Value))
                    findings.Add(Find(s, UniqueRules.RungIneligible,
                        $"rung '{s.RarityId}' is ordinal {ordinal}, below the floor of {tuning.RungFloorOrdinal}"));

                if (s.Acquisition == UniqueAcquisition.Drop && ordinal.Value >= 90)
                    findings.Add(Find(s, UniqueRules.Unreachable,
                        $"acquisition 'drop' at ordinal {ordinal}; every unique at ordinal ≥ 90 must be " +
                        "source-locked or deterministic"));
            }

            // ---- sets ---------------------------------------------------------------------------------
            if (view.IsSetMember is { } isMember && isMember(s.ContainerId))
                findings.Add(Find(s, UniqueRules.SetMembership,
                    $"container '{s.ContainerId}' is referenced by an item_set_member row"));

            // ---- everything role-shaped resolves through the base type --------------------------------
            var bt = view.BaseType(s.BaseTypeId);
            if (bt is null)
            {
                findings.Add(Find(s, UniqueRules.CorpusMalformed,
                    $"baseType '{s.BaseTypeId}' does not resolve, so this unique's role cannot be read and " +
                    "no role rule can run on it"));
                continue;
            }

            var (roleId, btFrame) = bt.Value;

            if (btFrame != s.Frame)
                findings.Add(Find(s, UniqueRules.CorpusMalformed,
                    $"declares frame {s.Frame} but its base type '{s.BaseTypeId}' is on the {btFrame} ladder"));

            if (tuning.ForbiddenRoles.Contains(roleId, StringComparer.Ordinal))
                findings.Add(Find(s, UniqueRules.RoleForbidden,
                    $"base type '{s.BaseTypeId}' occupies role '{roleId}', which is barred from carrying a " +
                    "unique in v1"));

            // ssot-uniques.md §3.5's PHYSICS carve-out, which is not its taste carve-out. A unique may
            // bypass the frame filter where the filter is flavour (that is most of the point of the
            // class). It may not where the field does not exist: `plating`/`carapace` write arm1/arm2,
            // which are zombie-only Unity fields, so a plant unique carrying either "is not daring, it
            // is dead". The family corpus's own `frames` list is the SSOT for which side a family can
            // land on — this check does not carry a hardcoded list of two family ids.
            if (view.FamilyFrames is { } familyFrames)
            {
                var frameId = s.Frame == ItemFrame.Plant ? "plant" : "humanoid";

                // The fixed core AND the variance slot. The rule is about which side the CHANNEL exists
                // on, so it does not care whether the atom was authored or drawn — a variance pool that
                // can only ever draw a dead line is the same defect one step later.
                var families = s.FixedAtoms.Select(a => a.Family);
                if (s.VarianceSlot is { } v) families = families.Append(v.Family);

                foreach (var family in families)
                {
                    var frames = familyFrames(family);
                    if (frames is null || frames.Count == 0) continue;   // unknown family: not our finding
                    if (!frames.Contains(frameId, StringComparer.Ordinal))
                        findings.Add(Find(s, UniqueRules.Shape,
                            $"is a {frameId} unique carrying family '{family}', which the family corpus " +
                            $"restricts to {string.Join("/", frames)}. Where the frame filter is TASTE a unique " +
                            "may bypass it; where it is physics — a Unity field that does not exist on this " +
                            "side — the executor drops the atom and the item is dead rather than daring"));
                }
            }

            var key = $"{s.RungBand}|{roleId}|{s.PowerAxis}";
            if (axisSlots.TryGetValue(key, out var held))
                findings.Add(Find(s, UniqueRules.AxisCollision,
                    $"role '{roleId}' at rung band {s.RungBand} on axis '{s.PowerAxis}' is already taken by " +
                    $"'{held.SeedId}' ({held.Partition}); one unique per (role, rung band, power axis) is what " +
                    "stops the second unique in a role being a stronger version of the first"));
            else
                axisSlots[key] = s;

            if (!rolesByFrame.TryGetValue(btFrame, out var roles))
                rolesByFrame[btFrame] = roles = new Dictionary<string, UniqueSeed>(StringComparer.Ordinal);
            if (!roles.ContainsKey(roleId)) roles[roleId] = s;
        }

        // ---- the 8-of-15 quota, as a count over the whole corpus --------------------------------------
        foreach (var (frame, roles) in rolesByFrame)
        {
            if (roles.Count <= tuning.MaxRolesPerFrame) continue;

            // Reported on the roles PAST the quota in a stable order, so the report names a bounded set
            // an author can act on rather than every unique on the frame.
            foreach (var (roleId, seed) in roles.OrderBy(r => r.Key, StringComparer.Ordinal)
                                                .Skip(tuning.MaxRolesPerFrame))
                findings.Add(Find(seed, UniqueRules.RoleForbidden,
                    $"frame {frame} carries uniques on {roles.Count} roles, above the quota of " +
                    $"{tuning.MaxRolesPerFrame} of 15; role '{roleId}' is over the line. Uniques must leave " +
                    "room the same way sets do, and the two quotas are read together (6 + 8 > 15)"));
        }

        return findings;
    }

    /// <summary>
    /// `entry-shapes.md` §9's band→channel rule, which module 11 recorded as <b>this module's</b>:
    /// <i>"acquisition = 'drop' at ordinal ≥ 90 is UniqueUnreachable, so band 90 never appears in
    /// d1."</i> Module 11's importer refuses a <c>unique</c> entry by KIND today; this is the rule that
    /// applies once the kind is available, and it is the acquisition column doing the work it exists
    /// for — an entry in the general table naming a source-locked item is an item you can find in the
    /// wrong place, which is the same defect as one you cannot find at all.
    /// </summary>
    public static IReadOnlyList<UniqueCorpusFinding> ValidateDropReferences(
        IEnumerable<UniqueDropReference> references,
        IReadOnlyList<UniqueSeed> corpus,
        Func<string, int?> rarityOrdinal)
    {
        if (references is null) throw new ArgumentNullException(nameof(references));
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (rarityOrdinal is null) throw new ArgumentNullException(nameof(rarityOrdinal));
        UniqueRules.EnsureRegistered();

        var bySeedId = corpus.ToDictionary(s => s.SeedId, StringComparer.Ordinal);
        var findings = new List<UniqueCorpusFinding>();

        foreach (var r in references)
        {
            if (!bySeedId.TryGetValue(r.UniqueSeedId, out var s))
            {
                findings.Add(new UniqueCorpusFinding(r.UniqueSeedId, r.TableId,
                    AtomRejection.ContentRule(UniqueRules.CorpusMalformed,
                        $"{r.TableId}: names unique '{r.UniqueSeedId}', which is not in the corpus")));
                continue;
            }

            if (!r.IsGeneralChannel) continue;

            if (s.Acquisition != UniqueAcquisition.Drop)
                findings.Add(Find(s, UniqueRules.Unreachable,
                    $"is declared '{s.Acquisition}' but appears in the general drop table '{r.TableId}'; the " +
                    "acquisition column IS the channel, so a source-locked item in the general table is the " +
                    "wrong place rather than no place"));

            if (rarityOrdinal(s.RarityId) is { } ordinal && ordinal >= 90)
                findings.Add(Find(s, UniqueRules.Unreachable,
                    $"is ordinal {ordinal} and appears in the general drop table '{r.TableId}'; entry-shapes.md " +
                    "§9's band→channel rule is that band 90 never reaches the general table"));
        }

        return findings;
    }

    static void CheckCounterPressureDeclaration(
        UniqueSeed s, UniqueCorpusView view, List<UniqueCorpusFinding> findings)
    {
        var cp = s.CounterPressure;
        switch (cp.Kind)
        {
            case UniqueCounterPressure.Drawback:
                // core.v1.json: "drawback ... Requires severityBand and a channel or family reference."
                if (string.IsNullOrWhiteSpace(cp.SeverityBand))
                    findings.Add(Find(s, UniqueRules.CounterPressure,
                        "declares 'drawback' with no severityBand; the seed has no numbers, so the band IS " +
                        "the magnitude and without it the drawback is a sentence in a note field"));
                else if (!view.SeverityBands.Contains(cp.SeverityBand!, StringComparer.Ordinal))
                    findings.Add(Find(s, UniqueRules.CounterPressure,
                        $"severityBand '{cp.SeverityBand}' is not one of " + string.Join(", ", view.SeverityBands)));

                if (string.IsNullOrWhiteSpace(cp.Family) && string.IsNullOrWhiteSpace(cp.Channel))
                    findings.Add(Find(s, UniqueRules.CounterPressure,
                        "declares 'drawback' naming neither a family nor a channel, so nothing says what the " +
                        "item is worse at"));
                break;

            case UniqueCounterPressure.Conditional:
                if (string.IsNullOrWhiteSpace(cp.Condition))
                    findings.Add(Find(s, UniqueRules.CounterPressure,
                        "declares 'conditional' with no condition; the capability would fire unconditionally"));
                else if (!view.CounterPressureConditions.Contains(cp.Condition!, StringComparer.Ordinal))
                    findings.Add(Find(s, UniqueRules.CounterPressure,
                        $"condition '{cp.Condition}' is not in core.v1.json's closed condition list — each id " +
                        "there maps to a predicate leaf the atom layer already ships, so an invented one is a " +
                        "promise the runtime cannot keep"));
                break;

            case UniqueCounterPressure.Narrow:
                // "Needs no further field" (core.v1.json). It is satisfied ARITHMETICALLY, against the
                // rung baseline, and that measurement is UniqueCorpusReport's -- see the class remark
                // on why it reports here rather than refusing.
                break;
        }
    }

    static UniqueCorpusFinding Find(UniqueSeed s, string ruleId, string detail) =>
        new(s.SeedId, s.Partition, AtomRejection.ContentRule(ruleId, $"{s.ContainerId}: {detail}"));
}
