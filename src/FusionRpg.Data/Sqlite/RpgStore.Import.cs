using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;

namespace FusionRpg.Data;

/// <summary>What one import did, or refused to do.</summary>
/// <param name="Committed">False means nothing was written — see <paramref name="Errors"/>.</param>
/// <param name="RowsChanged">
/// How many writes actually altered a row. Zero on a repeat import of unchanged files, which is what
/// makes the content hash stand still and the catalog revision hold.
/// </param>
/// <param name="Coefficients">E44 criterion 0 (spec-power-sweep.md §4.1): how many
/// <c>power-coefficient</c> rows this import carried — what it was GIVEN, matching every other count
/// on this record, not how many actually changed a row.</param>
public sealed record ImportOutcome(
    bool Committed,
    IReadOnlyList<SeedError> Errors,
    int Atoms,
    int Containers,
    int Curves,
    int Rarities,
    int Elements,
    int ChannelPolicies,
    int RowsChanged,
    long CatalogRevision,
    ContentHashStamp? ContentHash,
    int Affixes = 0,
    int Coefficients = 0)
{
    public bool IsOk => Errors.Count == 0;
}

/// <summary>
/// The seed import (E14a) — validate everything, then write everything, in one transaction.
///
/// <para><b>All or nothing, and validate-first is not the same guarantee.</b> Validating every row
/// before the first write stops a known-bad file from landing half a catalog. The single transaction
/// stops a crash, a locked database or a constraint nobody predicted from doing the same thing. Both
/// are needed: a partial import produces a content hash for a state nobody authored, and the hash is
/// what every downstream replay verdict trusts.</para>
///
/// <para><b>The revision is bumped once, and only when something changed.</b> Once per transaction
/// rather than per row, or a fifty-row file would move it fifty times. Not at all on a no-op import,
/// because <c>catalog_revision</c> is what E6 reproduces against and what E19 negotiates on — a bump
/// for content that did not change makes every connected receiver re-download the full push.</para>
///
/// <para>It lives here rather than in the importer tool for two reasons that are not the same. The
/// write path needs this class's private connection, gate and unlocked writers, so it could not live
/// anywhere else without opening them. And <c>guard-dal.ps1</c> scans only <c>src/</c> — SQL under
/// <c>tools/</c> is unguarded, so the rule that keeps SQL in one project would stop applying exactly
/// where a tool was tempted to write its own. (A tool <i>can</i> be tested: <c>ItemSeedValidator</c>
/// has a test project and so does this one. Untestability was not the reason.)</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <param name="dryRun">
    /// Run the whole thing and roll back. Not the same as validating the files: this resolves every
    /// cross-table reference against the real catalog and lets the database itself refuse a write,
    /// which is what an author wants to know before an import lands.
    /// </param>
    public ImportOutcome ImportContent(SeedContent content, bool dryRun = false)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));

        var errors = new List<SeedError>();

        lock (_gate)
        {
            // ---- read what is already stored, before any write ---------------------------------
            //
            // Every read below opens and closes its own connection. They must all finish before the
            // write transaction begins: reading through a second connection while this one holds the
            // write lock would block rather than answer.

            var atomsById = ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
            var curvesById = new Dictionary<string, CurveTable>(StringComparer.Ordinal);
            var ordinalOwner = ListRarities().ToDictionary(r => r.Ordinal, r => r.RarityId);

            // ---- validate ----------------------------------------------------------------------

            // The reader refuses an id authored in two files. This catches the same mistake in a
            // hand-built batch, where keeping the last one would make the import order-dependent —
            // and it runs first, because a duplicate makes every count below a lie.
            RefuseDuplicates(content, errors);

            ValidateCurves(content, curvesById, errors);

            // Incoming atoms overlay the stored ones so a container may reference an atom authored
            // in the same import — the common case for a new item and its affixes.
            ValidateAtoms(content, atomsById, curvesById, errors);
            ValidateRarities(content, ordinalOwner, errors);

            // E32 (spec-affix-import-path.md §3.1 break 4, §3.3): hand-authored affixes validate
            // against the batch's OWN incoming atoms (the common case: a new atom and the bundle
            // that references it, in one import). §3.3's generated 1:1 affixes are derived here too
            // — never committed as rows, but available to the container-pool lookup below so a pool
            // referencing an atom's own generated wrapper (freshly imported, never explicitly
            // authored) still resolves within the same batch.
            var affixesToWrite = ValidateAffixes(content, atomsById, errors);
            var affixesToWriteById = affixesToWrite.ToDictionary(a => a.AffixId, StringComparer.Ordinal);
            var generatedAffixesById = AffixLibraryGenerator.Generate(atomsById.Values)
                .ToDictionary(a => a.AffixId, StringComparer.Ordinal);
            AffixRow? LookupAffixForImport(string id) =>
                affixesToWriteById.TryGetValue(id, out var authored) ? authored
                : generatedAffixesById.TryGetValue(id, out var generated) ? generated
                : GetAffix(id);

            // Containers whose stored copy is byte-identical are skipped, not rewritten: `revision`
            // is a hashed column and an identical rewrite would move the content hash.
            // item-ideal.md, rarity-bands (module 7): a container may name a rarity already stored OR
            // one newly seeded in this same batch (content.Rarities) -- same "overlay" rule ValidateAtoms
            // already applies for atoms, so a container and its rarity can land in one import.
            var rarityIds = ordinalOwner.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var r in content.Rarities) rarityIds.Add(r.RarityId);

            var containersToWrite = ValidateContainers(content, atomsById, LookupAffixForImport, rarityIds.Contains, errors);
            var elementTable = ValidateElements(content, errors);
            var rosterToWrite = elementTable is not null && !SameRoster(GetElementTable(), elementTable)
                ? elementTable
                : null;
            var policyRows = ValidateChannelPolicyContent(content, errors);

            // E44 criterion 0 (spec-power-sweep.md §4.1): the seed reader was the only missing link
            // in an already-shipped table/writer/reader/fallback/hash chain — validated the same
            // "check everything before the first write" way as every other content kind here.
            var coefficientsToWrite = ValidateCoefficients(content, errors);

            // ST4.5c: the frequency half of the same power tables. Without this the seed reader's rows
            // would land nowhere and `CostFunction.Conditionality` would keep multiplying every
            // triggered atom by zero.
            var frequenciesToWrite = ValidateFrequencies(content, errors);

            if (errors.Count > 0)
                return new ImportOutcome(false, errors, 0, 0, 0, 0, 0, 0, 0, GetCatalogRevision(), null);

            // ---- write --------------------------------------------------------------------------

            var changed = 0;

            using (var db = OpenUnlocked())
            using (var tx = db.BeginTransaction())
            {
                // Curves first: an atom that scales through one is only meaningful once it exists.
                foreach (var c in content.Curves)
                    changed += WriteCurveUnlocked(db, tx, c.CurveId, c.Input, c.Points);

                foreach (var r in content.Rarities)
                {
                    UpsertRarityUnlocked(db, tx, r, out var rows);
                    changed += rows;
                }

                foreach (var a in content.Atoms)
                    changed += UpsertAtomUnlocked(db, a, tx);

                // E32 (spec-affix-import-path.md §3.1 break 4): after atoms (an affix references
                // one), before containers (a container's pool references an affix). Only
                // hand-authored, already-resolved affixes are rows — §3.3's 1:1 generated ones are
                // derived above for validation and never committed.
                foreach (var a in affixesToWrite)
                {
                    WriteAffixUnlocked(db, tx, a);
                    changed++;
                }

                foreach (var c in containersToWrite)
                {
                    WriteContainerUnlocked(db, tx, c);
                    changed++;
                }

                // The roster is replaced whole, and only when the import carries one AND it differs:
                // a file set with no element rows must not wipe the roster the database holds, and an
                // unchanged roster must not be rewritten. The matrices are delete-then-insert, so
                // "did anything change" cannot be read off the row counts — it is decided before the
                // write, against the stored table.
                if (rosterToWrite is not null)
                {
                    WriteElementTableUnlocked(db, tx, rosterToWrite);
                    changed++;
                }

                foreach (var row in policyRows)
                    changed += UpsertChannelPolicyRowUnlocked(db, tx, row);

                // E44 criterion 0: absent means "leave the coefficient table alone", the same rule
                // the roster write just above already follows — the folders are swept independently
                // and a run that touched only atoms must not wipe out every authored coefficient.
                if (coefficientsToWrite.Count > 0)
                    changed += WriteCoefficientsUnlocked(db, tx, coefficientsToWrite);

                // ST4.5c: after coefficients, and independently — both writers re-read the other's table
                // and carry it over, so a run that touches only one of the two leaves the other alone.
                if (frequenciesToWrite.Count > 0)
                    changed += WriteTriggerFrequenciesUnlocked(db, tx, frequenciesToWrite);

                if (changed > 0)
                    ExecIn(db, tx,
                        "UPDATE content_meta SET catalog_revision = catalog_revision + 1 WHERE id = 1;");

                // A dry run rolls back by disposing without committing — the writes still happened,
                // so anything the database itself would have refused has already thrown.
                if (!dryRun) tx.Commit();
            }

            // The counts are what the import was GIVEN, not what it wrote — a container skipped as
            // already-identical is still content this import covered. `RowsChanged` is the one that
            // says whether anything moved, and reporting the two differently would make an idempotent
            // re-import look like content going missing.
            return new ImportOutcome(
                !dryRun, errors,
                content.Atoms.Count, content.Containers.Count, content.Curves.Count, content.Rarities.Count,
                content.Elements.Count, content.ChannelPolicies.Count, changed,
                GetCatalogRevision(), ComputeContentHash(), content.Affixes.Count, content.Coefficients.Count);
        }
    }

    // ---- validation ---------------------------------------------------------------------------

    static void ValidateCurves(
        SeedContent content, Dictionary<string, CurveTable> into, List<SeedError> errors)
    {
        foreach (var c in content.Curves)
        {
            var check = CurveTable.TryCreate(c.CurveId, c.Input, c.Points, out var table);
            if (!check.IsOk)
            {
                errors.Add(Error(content, c.CurveId, check));
                continue;
            }
            into[c.CurveId] = table!;
        }
    }

    void ValidateAtoms(
        SeedContent content,
        Dictionary<string, AtomRow> atomsById,
        Dictionary<string, CurveTable> incomingCurves,
        List<SeedError> errors)
    {
        // A curve authored in this import resolves before a stored one of the same id: the file is
        // what is being imported, so its shape is the one the atom is being validated against.
        CurveInput? CurveInputOfBatch(string id) =>
            incomingCurves.TryGetValue(id, out var c) ? c.Input : CurveInputOf(id);

        foreach (var a in content.Atoms)
        {
            var check = AtomRowValidator.Validate(a, CurveInputOfBatch, ComposeKindOf);
            if (!check.IsOk)
            {
                errors.Add(Error(content, a.AtomId, check));
                continue;
            }
            atomsById[a.AtomId] = a;
        }
    }

    static void ValidateRarities(
        SeedContent content, Dictionary<int, string> ordinalOwner, List<SeedError> errors)
    {
        foreach (var r in content.Rarities)
        {
            if (r.MinTier > r.MaxTier)
            {
                errors.Add(Error(content, r.RarityId, AtomRejection.Fail(
                    AtomRejectionReason.BadParamValue,
                    $"tier window [{r.MinTier}, {r.MaxTier}] is inverted")));
                continue;
            }

            // Append-only ordinals, checked against the batch as well as the table: two new bands
            // claiming one ordinal would otherwise be decided by insertion order.
            if (ordinalOwner.TryGetValue(r.Ordinal, out var owner) &&
                !string.Equals(owner, r.RarityId, StringComparison.Ordinal))
            {
                errors.Add(Error(content, r.RarityId, AtomRejection.Fail(
                    AtomRejectionReason.DuplicateKey,
                    $"ordinal {r.Ordinal} already belongs to '{owner}' — ordinals are append-only")));
                continue;
            }

            ordinalOwner[r.Ordinal] = r.RarityId;
        }
    }

    List<ContainerRow> ValidateContainers(
        SeedContent content, Dictionary<string, AtomRow> atomsById,
        Func<string, AffixRow?> lookupAffix, Func<string, bool> rarityExists, List<SeedError> errors)
    {
        var write = new List<ContainerRow>();

        foreach (var c in content.Containers)
        {
            // E32 (spec-affix-import-path.md §3.1 break 4): a container may now reference an affix
            // hand-authored in the SAME import batch, or one of the atom set's own 1:1-generated
            // wrappers (§3.3) — never only an affix already committed to the store, which is what
            // made the whole affix pipeline unreachable before this fix.
            var check = ContainerValidator.Validate(
                c, id => atomsById.TryGetValue(id, out var a) ? a : null, lookupAffix, rarityExists);
            if (!check.IsOk)
            {
                errors.Add(Error(content, c.ContainerId, check));
                continue;
            }

            if (!SameContent(GetContainer(c.ContainerId), c)) write.Add(c);
        }

        return write;
    }

    /// <summary>
    /// E32 (spec-affix-import-path.md §3.1 break 4, §3.2): validates hand-authored affixes against
    /// the batch's own incoming atoms, resolving an absent <c>class</c> to its derived value before
    /// returning — the tables never carry a null class (§3.2's "absent → legal, derive it" decision).
    /// Skips a row that is byte-identical to what is already stored, the same no-op discipline every
    /// other content kind here already follows.
    /// </summary>
    List<AffixRow> ValidateAffixes(
        SeedContent content, Dictionary<string, AtomRow> atomsById, List<SeedError> errors)
    {
        var write = new List<AffixRow>();
        AtomRow? LookupAtomForAffix(string id) => atomsById.TryGetValue(id, out var a) ? a : null;

        foreach (var a in content.Affixes)
        {
            var check = AffixValidator.Validate(a, LookupAtomForAffix, DomainMembers, FamilyVariantHasAnyTierUnlocked);
            if (!check.IsOk)
            {
                errors.Add(Error(content, a.AffixId, check));
                continue;
            }

            var resolved = a.Class is null ? a with { Class = AffixValidator.ResolveClass(a, LookupAtomForAffix) } : a;
            if (!SameAffixContent(GetAffix(resolved.AffixId), resolved)) write.Add(resolved);
        }

        return write;
    }

    /// <summary>
    /// One id, one row — across all five kinds, in one namespace (E32 joined affixes to the four
    /// that were already here).
    ///
    /// <para>Five namespaces that only overlap by accident is the more expensive rule to hold, and a
    /// container named after an atom is a mistake either way.</para>
    /// </summary>
    static void RefuseDuplicates(SeedContent content, List<SeedError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in content.Atoms.Select(a => a.AtomId)
                     .Concat(content.Containers.Select(c => c.ContainerId))
                     .Concat(content.Curves.Select(c => c.CurveId))
                     .Concat(content.Rarities.Select(r => r.RarityId))
                     .Concat(content.Affixes.Select(a => a.AffixId)))
        {
            if (!seen.Add(id))
                errors.Add(Error(content, id, AtomRejection.Fail(
                    AtomRejectionReason.DuplicateKey, "the same id appears twice in one import")));
        }
    }

    /// <summary>
    /// The roster an import carries, or null when it carries none.
    ///
    /// <para>Absent means "leave the roster alone", never "empty it" — the four content folders are
    /// swept independently and a run that touched only atoms must not retire every element.</para>
    /// </summary>
    ElementTable? ValidateElements(SeedContent content, List<SeedError> errors)
    {
        if (content.Elements.Count == 0 && content.ElementMatrix.Count == 0) return null;

        if (content.Elements.Count == 0)
        {
            errors.Add(Error(content, "(elements)", AtomRejection.Fail(
                AtomRejectionReason.MissingParam,
                "matchup cells were authored with no roster; the cells name elements that would not exist")));
            return null;
        }

        var known = content.Elements.Select(e => e.ElementId).ToHashSet(StringComparer.Ordinal);
        foreach (var (matrix, row) in content.ElementMatrix)
        {
            foreach (var side in new[] { row.Attacker, row.Defender })
                if (!known.Contains(side))
                    errors.Add(Error(content, $"{matrix}:{row.Attacker}>{row.Defender}",
                        AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                            $"'{side}' is not in the roster this import carries")));
        }

        // The append-only ordinal rules live in UpsertElementTable, which reads the stored roster.
        return new ElementTable(
            content.Elements,
            content.ElementMatrix.Where(m => m.Matrix == "combat").Select(m => m.Row).ToList(),
            content.ElementMatrix.Where(m => m.Matrix == "shield").Select(m => m.Row).ToList());
    }

    /// <summary>
    /// The channel policy rows an import carries (E22), converted to the Data-side row and checked
    /// against the same rule <c>UpsertChannelPolicies</c> enforces outside an import.
    /// </summary>
    static List<ChannelPolicyRow> ValidateChannelPolicyContent(SeedContent content, List<SeedError> errors)
    {
        var rows = content.ChannelPolicies
            .Select(r => new ChannelPolicyRow(r.ChannelId, r.Direction))
            .ToList();

        var reason = ValidateChannelPolicyRows(rows);
        if (reason is not null)
            errors.Add(Error(content, "(channel-policy)", AtomRejection.Fail(AtomRejectionReason.BadParamValue, reason)));

        return rows;
    }

    /// <summary>
    /// E44 criterion 0 (spec-power-sweep.md §4.1): the coefficient rows an import carries, checked
    /// against the one semantic rule <c>RpgStore.UpsertPowerTables</c> already enforces outside an
    /// import — a reference scale of zero or less would divide by it during pricing and price every
    /// magnitude alike, the units trap that column exists to close. Reused rather than duplicated so
    /// the two write paths can never silently disagree about what a valid coefficient is.
    /// </summary>
    static List<PowerCoefficientRow> ValidateCoefficients(SeedContent content, List<SeedError> errors)
    {
        var rows = new List<PowerCoefficientRow>();
        foreach (var c in content.Coefficients)
        {
            var key = $"{c.KindId}/{(c.Channel.Length == 0 ? "*" : c.Channel)}";
            if (c.ReferenceScale <= 0)
            {
                errors.Add(Error(content, key, AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"reference scale {c.ReferenceScale} — normalisation divides by it, and a zero " +
                    "scale prices every magnitude alike, which is the units trap this column exists to close")));
                continue;
            }

            rows.Add(c);
        }

        return rows;
    }

    /// <summary>
    /// ST4.5c (manager ruling on the ST4.5a diagnosis): the trigger-frequency rows an import carries —
    /// the sibling of <see cref="ValidateCoefficients"/>, reusing the same "check everything before the
    /// first write" shape. <c>perMinute</c> must be positive: <c>CostFunction.Conditionality</c>
    /// multiplies a triggered atom's price by <c>perMinute/60</c>, so a zero row would price every atom
    /// carrying that trigger at exactly zero — silently, which is the defect this seed kind closes, and
    /// a value a hand-built <see cref="SeedContent"/> can still carry even though the seed reader
    /// refuses it.
    /// </summary>
    static List<TriggerFrequencyRow> ValidateFrequencies(SeedContent content, List<SeedError> errors)
    {
        var rows = new List<TriggerFrequencyRow>();
        foreach (var f in content.TriggerFrequencies)
        {
            if (f.PerMinute <= 0)
            {
                errors.Add(Error(content, f.Trigger, AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"perMinute {f.PerMinute} — a trigger that never fires prices every atom carrying it " +
                    "at zero; leave the trigger unlisted and pricing names it unpriced instead")));
                continue;
            }

            rows.Add(f);
        }

        return rows;
    }

    /// <summary>
    /// Whether a re-imported roster says anything new.
    ///
    /// <para>The matrices are written delete-then-insert, so the row counts always look like a change
    /// and cannot answer this. Importing an unchanged roster twice was bumping
    /// <c>catalog_revision</c> — with the content hash correctly standing still, which is the pair of
    /// symptoms that says a change counter is lying rather than that content moved.</para>
    /// </summary>
    static bool SameRoster(ElementTable stored, ElementTable incoming) =>
        stored.Elements.SequenceEqual(incoming.Elements)
        && Same(stored.CombatRows, incoming.CombatRows)
        && Same(stored.ShieldRows, incoming.ShieldRows);

    static bool Same(IReadOnlyList<ElementMatrixRow> a, IReadOnlyList<ElementMatrixRow> b) =>
        a.Count == b.Count
        && a.OrderBy(r => r.Attacker, StringComparer.Ordinal).ThenBy(r => r.Defender, StringComparer.Ordinal)
            .SequenceEqual(
                b.OrderBy(r => r.Attacker, StringComparer.Ordinal).ThenBy(r => r.Defender, StringComparer.Ordinal));

    /// <summary>A rejection, told where it came from. The file path is the half an author can act on.</summary>
    static SeedError Error(SeedContent content, string id, AtomRejection rejection) =>
        new(content.SourceOf.TryGetValue(id, out var path) ? path : "(caller-supplied)",
            id, rejection.Reason, rejection.Detail);
}
