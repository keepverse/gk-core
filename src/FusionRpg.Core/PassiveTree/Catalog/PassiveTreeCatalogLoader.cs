using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.PassiveTree.Catalog;

/// <summary>
/// Every refusal found loading one tree, batched into a single report — R5 (spec-tree-catalog.md
/// §4): "a node id the catalog has never had is rejected ONCE, at the import boundary, with every
/// offending id named in one report. Never lazily, per actor load." The historical defect this
/// prevents is shipped and live: `AptitudeAllocation.Single` throws per-row inside a reader loop,
/// which is fine at twelve aptitudes and unloadable at 1,560 node ids per actor.
/// </summary>
public sealed record CatalogImportReport(IReadOnlyList<string> Refusals)
{
    public bool IsOk => Refusals.Count == 0;
}

public sealed record LoadedTree(TreeRecord Tree, IReadOnlyList<NodeRecord> Nodes);

/// <summary>
/// The load path that refuses rather than clamps (spec-tree-catalog.md §2, §3, §4). Pure — no
/// SQL, no file I/O of its own beyond the JSON text handed to it; the importer (a later task) owns
/// turning `LoadedTree` into rows inside one all-or-nothing transaction.
/// </summary>
public static class PassiveTreeCatalogLoader
{
    // skill.<treeId>-<branch>-t<tier>-<nodeKey> — container_id's own grammar (item/seed-contract.md
    // :131-133): no dot in the body, every separator a hyphen.
    static readonly Regex NodeIdPattern = new(
        @"^skill\.(?<tree>[a-z][a-z0-9]*)-(?<branch>off|def)-t(?<tier>[0-9]+)-(?<key>[a-z0-9]+)$",
        RegexOptions.Compiled);

    // R6's "classes.v2.json trap" (spec-tree-catalog.md §4): `gk-data/packs/fusion/data/seed/items/_registry/classes.v2.json`
    // carries `"registryVersion": 4` internally -- its filename and its own internal version number
    // disagree, and nothing catches it. This module refuses to ship the same trap: a committed tree
    // file's name and its `catalogVersion` field must be the same number.
    static readonly Regex FileVersionPattern = new(@"\.v(?<v>[0-9]+)\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Dictionary<string, TreeCategory> CategoryTokenMap = new(StringComparer.Ordinal)
    {
        ["aptitude"] = TreeCategory.Primary,
        ["primary"] = TreeCategory.Primary,
        ["elemental"] = TreeCategory.Elemental,
        ["status"] = TreeCategory.Status,
        ["creatureFamily"] = TreeCategory.Family,
        ["family"] = TreeCategory.Family,
        ["species"] = TreeCategory.Species,
    };

    static readonly HashSet<string> AuthorableTriggers = new(AtomTriggers.All, StringComparer.Ordinal);

    static PassiveTreeCatalogLoader()
    {
        foreach (var lifecycle in AtomTriggers.Lifecycle)
            AuthorableTriggers.Remove(lifecycle);
    }

    /// <summary>Parses and validates one tree's committed JSON into a <see cref="LoadedTree"/>,
    /// collecting every refusal into one <see cref="CatalogImportReport"/> rather than throwing on
    /// the first — R5. Returns <c>null</c> (with a non-empty report) when the document is too
    /// malformed to construct even a partial record.</summary>
    public static (LoadedTree? Tree, CatalogImportReport Report) Load(string json, PassiveTreeTuning tuning)
    {
        var refusals = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            refusals.Add($"tree catalog: not valid JSON — {ex.Message}");
            return (null, new CatalogImportReport(refusals));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!TryGetString(root, "treeId", out var treeId))
            {
                refusals.Add("tree catalog: missing 'treeId'");
                return (null, new CatalogImportReport(refusals));
            }

            var categoryToken = GetString(root, "category");
            if (!CategoryTokenMap.TryGetValue(categoryToken, out var category))
            {
                refusals.Add($"tree '{treeId}': category token '{categoryToken}' is outside the five-value " +
                            $"map ({string.Join(", ", CategoryTokenMap.Keys)}) — R7");
            }

            var gateQuantity = GetString(root, "gateQuantity"); // stored regardless of producer (D37) — never validated here
            var shapeArchetype = GetString(root, "shapeArchetype");
            var tiers = GetInt(root, "tiers");
            var branches = GetInt(root, "branches");
            var nodesPerTier = root.TryGetProperty("nodesPerTier", out var npt)
                ? npt.EnumerateArray().Select(e => e.GetInt32()).ToList()
                : new List<int>();
            var catalogVersion = GetInt(root, "catalogVersion");
            var enabled = root.TryGetProperty("enabled", out var enEl) ? enEl.GetBoolean() : true;

            // seedsmith-content-standard, passive-tree-identity-content (2026-09-08): the tree's
            // own real generated identity content — nullable, TryGetProperty read, never required.
            var treeName = root.TryGetProperty("name", out var treeNameEl) && treeNameEl.ValueKind == JsonValueKind.String
                ? treeNameEl.GetString() : null;
            var treeDescription = root.TryGetProperty("description", out var treeDescEl) && treeDescEl.ValueKind == JsonValueKind.String
                ? treeDescEl.GetString() : null;

            var tree = new TreeRecord(treeId, category, gateQuantity, shapeArchetype, tiers, branches,
                nodesPerTier, catalogVersion, enabled, treeName, treeDescription);

            var nodes = new List<NodeRecord>();
            var knownNodeIds = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("nodes", out var nodesEl))
            {
                foreach (var nodeEl in nodesEl.EnumerateArray())
                {
                    var node = LoadNode(nodeEl, treeId, tuning, refusals);
                    if (node is not null)
                    {
                        if (!knownNodeIds.Add(node.NodeId))
                            refusals.Add($"tree '{treeId}': duplicate node id '{node.NodeId}'");
                        nodes.Add(node);
                    }
                }
            }

            // R5: every prereqNodeId must resolve INSIDE the same tree — batched, all offenders named.
            foreach (var node in nodes)
                foreach (var prereq in node.PrereqNodeIds)
                    if (!knownNodeIds.Contains(prereq))
                        refusals.Add($"tree '{treeId}': node '{node.NodeId}' names unresolvable prereq '{prereq}'");

            if (refusals.Count > 0)
                return (null, new CatalogImportReport(refusals));
            return (new LoadedTree(tree, nodes), new CatalogImportReport(refusals));
        }
    }

    static NodeRecord? LoadNode(JsonElement el, string treeId, PassiveTreeTuning tuning, List<string> refusals)
    {
        var nodeId = GetString(el, "id");
        var match = NodeIdPattern.Match(nodeId);
        if (!match.Success)
        {
            refusals.Add($"node '{nodeId}': id violates the grammar 'skill.<treeId>-<branch>-t<tier>-<nodeKey>' " +
                        "(no dot in the body)");
            return null;
        }

        var branchStr = GetString(el, "branch");
        if (!Enum.TryParse<TreeBranch>(branchStr, ignoreCase: true, out var branch))
        {
            refusals.Add($"node '{nodeId}': unknown branch '{branchStr}'");
            return null;
        }

        var tier = GetInt(el, "tier");

        // IdMismatch: an authored id that disagrees with its own coordinates is kept AS AUTHORED
        // and reported — item/seed-contract.md's existing rule for atom_id, applied here (§3.1).
        //
        // 2026-09-07 real-corpus finding: the id GRAMMAR (`NodeIdPattern` above) forbids `.`/`_` in
        // the tree slug (`[a-z][a-z0-9]*`), so `seedsmith`'s own `ids.tree_slug_for` (J1, same date)
        // already strips both when MINTING a node id for a real dotted/underscored tree id
        // (`nerve.afflicted`, `charm_pulse`, ...) — the five real trees whose own roster id legitimately
        // contains either character. This check compared the id's own (correctly stripped) slug
        // against the RAW `treeId` field verbatim, so it refused every node of all five real trees the
        // moment a real bound catalog first tried to import (`skill.charmpulse-off-t7-n1` vs
        // `treeId: "charm_pulse"`) — found by the first real live-boot proof, not a synthetic
        // fixture, since no test before this one ever exercised a tree id containing either
        // character. `TreeSlugFor` mirrors `tree_slug_for`'s exact rule (strip `.`/`_` by
        // concatenation, lowercase) so the two stay the SAME grammar rather than two, silently
        // drifting definitions of "the same tree."
        var idBranch = match.Groups["branch"].Value;
        var idTier = int.Parse(match.Groups["tier"].Value);
        var idTreeSlug = match.Groups["tree"].Value;
        if (!string.Equals(idTreeSlug, TreeSlugFor(treeId), StringComparison.Ordinal)
            || !string.Equals(idBranch, branch.ToString().ToLowerInvariant(), StringComparison.Ordinal)
            || idTier != tier)
        {
            refusals.Add($"node '{nodeId}': IdMismatch — id encodes tree='{idTreeSlug}'/branch='{idBranch}'/" +
                        $"tier={idTier} but the record says tree='{treeId}'/branch='{branch}'/tier={tier}");
        }

        var nodeKey = GetString(el, "nodeKey");
        var prereqs = el.TryGetProperty("prereqNodeIds", out var prereqEl)
            ? prereqEl.EnumerateArray().Select(e => e.GetString()!).ToList()
            : new List<string>();

        var nodeClassStr = GetString(el, "nodeClass");
        if (!Enum.TryParse<NodeClass>(nodeClassStr, ignoreCase: true, out var nodeClass))
        {
            refusals.Add($"node '{nodeId}': unknown nodeClass '{nodeClassStr}'");
            return null;
        }

        var affixIds = el.TryGetProperty("affixIds", out var affixEl)
            ? affixEl.EnumerateArray().Select(e => e.GetString()!).ToList()
            : new List<string>();
        if (affixIds.Count < 1 || affixIds.Count > 3)
        {
            refusals.Add($"node '{nodeId}': affixIds has {affixIds.Count} entries, must be 1..3 (R6)");
        }

        var budgetShareMilli = GetInt(el, "budgetShareMilli");
        if (budgetShareMilli > tuning.Potency.MaxNodeShareMilli)
        {
            refusals.Add($"node '{nodeId}': budgetShareMilli={budgetShareMilli} exceeds " +
                        $"potency.maxNodeShareMilli={tuning.Potency.MaxNodeShareMilli} " +
                        "(compared as budget shares, never against kMicro — §2.5)");
        }

        var excludeProps = el.TryGetProperty("excludeProps", out var epEl)
            ? epEl.EnumerateArray().Select(e => e.GetString()!).ToList()
            : new List<string>();
        var exclusionFormStr = el.TryGetProperty("exclusionForm", out var efEl) ? efEl.GetString() : "None";
        if (!Enum.TryParse<ExclusionForm>(exclusionFormStr, ignoreCase: true, out var exclusionForm))
        {
            refusals.Add($"node '{nodeId}': unknown exclusionForm '{exclusionFormStr}'");
            return null;
        }
        if (exclusionForm == ExclusionForm.None && excludeProps.Count > 0)
            refusals.Add($"node '{nodeId}': exclusionForm is None but excludeProps is non-empty");
        if (exclusionForm != ExclusionForm.None && excludeProps.Count == 0)
            refusals.Add($"node '{nodeId}': exclusionForm is {exclusionForm} but excludeProps is empty");

        var atoms = new List<NodeAtom>();
        if (el.TryGetProperty("atoms", out var atomsEl))
            foreach (var atomEl in atomsEl.EnumerateArray())
            {
                var atom = LoadAtom(atomEl, nodeId, refusals);
                if (atom is not null) atoms.Add(atom);
            }

        // MC-1: sibling statusAtoms array, optional (absent = none bound — every pre-MC-1 file
        // loads exactly as before). Structural validation only: non-empty status, positive
        // duration, sane level, chance in range. The status vocabulary itself is validated where
        // statuses execute (FA2), never here — same split as LoadAtom's channel check being
        // non-fatal. Malformed entries refuse by name, never silently drop.
        var statusAtoms = new List<NodeStatusAtom>();
        if (el.TryGetProperty("statusAtoms", out var statusEl))
            foreach (var sEl in statusEl.EnumerateArray())
            {
                var statusAtom = LoadStatusAtom(sEl, nodeId, refusals);
                if (statusAtom is not null) statusAtoms.Add(statusAtom);
            }

        var tagsJson = el.TryGetProperty("tagsJson", out var tagsEl) && tagsEl.ValueKind != JsonValueKind.Null
            ? tagsEl.GetRawText() : null;
        var enabled = el.TryGetProperty("enabled", out var enEl) ? enEl.GetBoolean() : true;
        var retiredAt = el.TryGetProperty("retiredAtRevision", out var raEl) && raEl.ValueKind != JsonValueKind.Null
            ? raEl.GetInt32() : (int?)null;

        // seedsmith-content-standard, content-completeness-passive-tree (2026-09-08): the real
        // player-facing content `tree-language` already generates per node. Nullable, TryGetProperty
        // read (never required) — a tree/node this generation stage has not reached yet loads exactly
        // as before, both fields null, never a refusal and never a fabricated placeholder.
        var name = el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() : null;
        var flavor = el.TryGetProperty("flavor", out var flavorEl) && flavorEl.ValueKind == JsonValueKind.String
            ? flavorEl.GetString() : null;

        return new NodeRecord(nodeId, treeId, branch, tier, nodeKey, prereqs, nodeClass, affixIds,
            budgetShareMilli, atoms, excludeProps, exclusionForm, tagsJson, enabled, retiredAt,
            name, flavor, statusAtoms);
    }

    static NodeStatusAtom? LoadStatusAtom(JsonElement el, string nodeId, List<string> refusals)
    {
        var statusId = el.TryGetProperty("statusId", out var sEl) ? sEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(statusId))
        {
            refusals.Add($"node '{nodeId}': status atom names no statusId");
            return null;
        }
        if (!el.TryGetProperty("durationMs", out var dEl) || !dEl.TryGetInt64(out var durationMs) || durationMs <= 0)
        {
            refusals.Add($"node '{nodeId}': status atom '{statusId}' has no positive durationMs");
            return null;
        }
        var level = el.TryGetProperty("level", out var lEl) && lEl.TryGetInt32(out var lv) ? lv : 1;
        if (level < 1)
        {
            refusals.Add($"node '{nodeId}': status atom '{statusId}' level {level} is below 1");
            return null;
        }
        var chance = el.TryGetProperty("chancePermille", out var cEl) && cEl.TryGetInt64(out var ch) ? ch : 1000;
        if (chance < 0 || chance > 1000)
        {
            refusals.Add($"node '{nodeId}': status atom '{statusId}' chance {chance}‰ is outside 0..1000");
            return null;
        }
        var trigger = el.TryGetProperty("trigger", out var tEl) && tEl.ValueKind != JsonValueKind.Null
            ? tEl.GetString() : null;
        return new NodeStatusAtom(statusId, durationMs, level, chance, trigger, null);
    }

    static readonly DerivedStatRegistry DerivedRegistry = DerivedStatRegistry.CreateDefault();
    static readonly HashSet<string> PrimaryChannelSet = new(AtomKindRegistry.PrimaryChannels, StringComparer.Ordinal);

    static NodeAtom? LoadAtom(JsonElement el, string nodeId, List<string> refusals)
    {
        var kindId = GetString(el, "kindId");
        if (AtomKindRegistry.Get(kindId) is null)
        {
            refusals.Add($"node '{nodeId}': unknown atom kind '{kindId}'");
            return null;
        }

        var attachPointStr = GetString(el, "attachPoint");
        if (!Enum.TryParse<AttachPoint>(attachPointStr, ignoreCase: true, out var attachPoint))
        {
            refusals.Add($"node '{nodeId}': unknown attachPoint '{attachPointStr}'");
            return null;
        }

        var channelId = GetString(el, "channelId");
        var isPrimary = PrimaryChannelSet.Contains(channelId);
        var isDerived = !isPrimary && DerivedRegistry.TryResolveChannel(channelId, out _);
        if (!isPrimary && !isDerived)
            refusals.Add($"node '{nodeId}': unregistered channel '{channelId}' — validated against " +
                        "the live vocabulary at load, never silently written");

        var opStr = GetString(el, "op");
        if (!Enum.TryParse<NodeAtomOp>(opStr, ignoreCase: true, out var op))
        {
            refusals.Add($"node '{nodeId}': unknown op '{opStr}'");
            return null;
        }

        // §6 M3 (task P4.2). Before `More` became a NodeAtomOp member, `Enum.TryParse` above was the
        // whole enforcement: "more" simply did not parse, so a derived atom could never carry it. The
        // member now exists because `stat.modify` legitimately supports `more` (AtomKindRegistry.cs:517)
        // and the two kinds share this enum — so the derived-side refusal must be explicit, or a
        // `More`-op stat.derived row would load cleanly and then apply nothing forever
        // (`AtomDerivedSubsystem.TryParseOp` has no "more" arm). Refused here BY NAME, at load, exactly
        // as the old structural failure did.
        if (op == NodeAtomOp.More && string.Equals(kindId, "stat.derived", StringComparison.Ordinal))
            refusals.Add($"node '{nodeId}': channel '{channelId}' op 'more' is not one of " +
                        "Flat|Increased|Replace|Flag (§6 M3 -- there is no More on the derived side)");

        string? trigger = el.TryGetProperty("trigger", out var trigEl) && trigEl.ValueKind != JsonValueKind.Null
            ? trigEl.GetString() : null;
        if (trigger is not null && !AuthorableTriggers.Contains(trigger))
            refusals.Add($"node '{nodeId}': trigger '{trigger}' is not one of the 11 authorable triggers " +
                        "(OnGranted/OnRemoved are runtime lifecycle states, never authorable)");

        var whenJson = el.TryGetProperty("whenJson", out var wjEl) && wjEl.ValueKind != JsonValueKind.Null
            ? wjEl.GetRawText() : null;

        var kMicro = GetLong(el, "kMicro");

        var scaleAxisStr = GetString(el, "scaleAxis");
        if (!Enum.TryParse<ScaleAxis>(scaleAxisStr, ignoreCase: true, out var scaleAxis))
        {
            refusals.Add($"node '{nodeId}': unknown scaleAxis '{scaleAxisStr}'");
            return null;
        }

        var unitClassStr = GetString(el, "unitClass");
        if (!Enum.TryParse<UnitClass>(unitClassStr, ignoreCase: true, out var unitClass))
        {
            refusals.Add($"node '{nodeId}': unknown unitClass '{unitClassStr}'");
            return null;
        }

        // §2.4: six UnitClass members are refused as magnitude targets outright.
        var refusedUnitClasses = new HashSet<UnitClass>
        {
            UnitClass.Milliseconds, UnitClass.Count, UnitClass.Flag,
            UnitClass.LadderIndex, UnitClass.AptitudePoints, UnitClass.LoamUnits,
        };
        if (refusedUnitClasses.Contains(unitClass))
            refusals.Add($"node '{nodeId}': unitClass '{unitClass}' is refused as a magnitude target (§2.4)");

        // §2.4's scaleAxis/unitClass agreement — the silent-failure class this module exists to catch.
        var expectedAxis = unitClass switch
        {
            UnitClass.GameUnits or UnitClass.GameUnitsPerSecond or UnitClass.ReciprocalPoints => ScaleAxis.PTheta,
            UnitClass.SigmoidPoints or UnitClass.SigmoidMultiplierPoints or UnitClass.StatusPotencyPoints => ScaleAxis.Theta,
            UnitClass.PerMilleRatio => ScaleAxis.FlatPermille,
            _ => (ScaleAxis?)null,
        };
        if (expectedAxis is not null && expectedAxis != scaleAxis)
            refusals.Add($"node '{nodeId}': unitClass '{unitClass}' must carry scaleAxis " +
                        $"'{expectedAxis}', got '{scaleAxis}' — a silent-failure pairing (§2.4)");

        return new NodeAtom(kindId, attachPoint, channelId, op, trigger, whenJson, kMicro, scaleAxis,
            unitClass);
    }

    /// <summary>
    /// R6's "classes.v2.json trap" (spec-tree-catalog.md §4): a committed file's own `vN` and the
    /// document's `catalogVersion` field must be the same number, asserted rather than left to drift
    /// the way `classes.v2.json` already drifted from its internal `registryVersion: 4` elsewhere in
    /// this repo. Returns a refusal string naming the mismatch, or <c>null</c> when the two agree OR
    /// when <paramref name="fileName"/> carries no recognizable "vN.json" version token at all -- an
    /// inline test fixture or any other caller with no real file path has nothing to check "where
    /// applicable" against (spec-tree-catalog.md §4 wording), so it is never refused for that alone.
    /// </summary>
    public static string? CheckFilenameVersion(string fileName, int catalogVersion)
    {
        if (fileName is null) throw new ArgumentNullException(nameof(fileName));

        var match = FileVersionPattern.Match(fileName);
        if (!match.Success) return null;

        var fileVersion = int.Parse(match.Groups["v"].Value);
        return fileVersion == catalogVersion
            ? null
            : $"tree catalog file '{fileName}': filename version v{fileVersion} disagrees with its own " +
              $"catalogVersion={catalogVersion} (the classes.v2.json trap — spec-tree-catalog.md §4)";
    }

    /// <summary>The SAME rule `seedsmith`'s own `ids.tree_slug_for` mints a node id's tree segment
    /// with (task J1, 2026-09-07): strip every `.`/`_` by concatenation (never inserting a hyphen,
    /// already a structural separator in the id grammar), then lowercase — real species/status ids
    /// are PascalCase or `snake_case`/`dotted.case`, none of which the id grammar's own
    /// `[a-z][a-z0-9]*` tree-slug class permits verbatim. Exposed (not `static` `private`) so a test
    /// can assert this stays byte-identical to the Python side without importing Python into a C#
    /// test run — both sides are pinned against the same five real trees instead.</summary>
    public static string TreeSlugFor(string treeId) =>
        new string(treeId.Where(c => c != '.' && c != '_').ToArray()).ToLowerInvariant();

    static bool TryGetString(JsonElement el, string prop, out string value)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString()!;
            return true;
        }
        value = "";
        return false;
    }

    static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    static long GetLong(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;
}
