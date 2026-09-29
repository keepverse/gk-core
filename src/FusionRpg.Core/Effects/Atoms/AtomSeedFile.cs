using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects.Atoms.Power;
using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>Which table a seed file's entries land in. The file says so; the directory is only a habit.</summary>
public enum SeedEntryKind
{
    Atom = 0,
    Container,
    Curve,
    Rarity,
    Element,
    ElementMatrix,
    ChannelPolicy,
    Affix,
    ChannelPool,

    /// <summary>E44 criterion 0 (spec-power-sweep.md §4.1): one <c>power_coefficient</c> row —
    /// the seed kind that gives a fitted (or hand-authored) coefficient somewhere real to land.</summary>
    Coefficient,

    /// <summary>ST4.5c (manager ruling on the ST4.5a diagnosis): one <c>power_trigger_frequency</c>
    /// row — the frequency half of <c>CostFunction.Conditionality</c>. Seeded because the five rows
    /// in <c>PowerTables.Authored()</c> are unreachable once <c>power_coefficient</c> holds any row
    /// (<c>RpgStore.GetPowerTables</c>'s fallback), which silently priced every atom carrying a
    /// trigger at zero.</summary>
    TriggerFrequency,
}

/// <summary>
/// One reason a seed file was refused, with enough location to fix it.
///
/// <para>The source path is carried because the expensive seed mistakes are cross-file — the same
/// <c>atom_id</c> authored twice, in two files, by two people. A reason without both paths sends the
/// author looking in one of them.</para>
/// </summary>
public sealed record SeedError(string SourcePath, string EntryId, AtomRejectionReason Reason, string Detail)
{
    public override string ToString() =>
        string.IsNullOrEmpty(EntryId)
            ? $"{SourcePath}: {Reason} — {Detail}"
            : $"{SourcePath} [{EntryId}]: {Reason} — {Detail}";
}

/// <summary>One authored curve, before it reaches the table.</summary>
public sealed record CurveSeed(string CurveId, CurveInput Input, IReadOnlyList<CurvePoint> Points);

/// <summary>
/// One authored channel policy row (E22). Core-side mirror of <c>FusionRpg.Data.ChannelPolicyRow</c> —
/// Core cannot reference Data, and every other seeded table (elements, curves, rarity) already carries
/// its own Core-side row type for the same reason.
///
/// <para><c>DefaultValue</c>/<c>CapMilli</c>/<c>ComposeKind</c> retired (cap-consolidation, T1,
/// 2026-08-24) — dead columns nothing read; a derived cap's one home is
/// <c>gk-core/data/tuning/derived-stats.v1.json</c> now. <c>Direction</c> is the one column with a live
/// consumer and stays.</para>
/// </summary>
public sealed record ChannelPolicySeedRow(string ChannelId, int Direction);

/// <summary>
/// Everything one import wants to write, collected from every file and already checked for the
/// duplicates that only exist across files.
/// </summary>
public sealed class SeedContent
{
    public List<AtomRow> Atoms { get; } = new();
    public List<ContainerRow> Containers { get; } = new();
    public List<AffixRow> Affixes { get; } = new();
    public List<CurveSeed> Curves { get; } = new();
    public List<RarityRow> Rarities { get; } = new();

    /// <summary>The element roster (E18). Ordinals are explicit and append-only.</summary>
    public List<ElementRow> Elements { get; } = new();

    /// <summary>Matchup cells, tagged by which matrix they belong to — the two never share rows.</summary>
    public List<(string Matrix, ElementMatrixRow Row)> ElementMatrix { get; } = new();

    /// <summary>Channel policy rows (E22) — direction is the one column with a live consumer.</summary>
    public List<ChannelPolicySeedRow> ChannelPolicies { get; } = new();

    /// <summary>E30 (spec-channel-pool.md §3.1): named, weighted channel pools an atom's
    /// <c>params.channel</c> may reference instead of one concrete channel.</summary>
    public List<ChannelPoolRow> ChannelPools { get; } = new();

    /// <summary>E44 criterion 0 (spec-power-sweep.md §4.1): authored <c>power_coefficient</c> rows —
    /// the coefficient data path a fitted (or hand-authored) number now has to travel through.</summary>
    public List<PowerCoefficientRow> Coefficients { get; } = new();

    /// <summary>ST4.5c (manager ruling on the ST4.5a diagnosis): authored
    /// <c>power_trigger_frequency</c> rows — fires per battle-minute per trigger. Seeded because
    /// <c>PowerTables.Authored()</c>'s five rows become unreachable the moment the coefficient table
    /// holds any row, and a trigger with no row here multiplies every atom carrying it by zero.</summary>
    public List<TriggerFrequencyRow> TriggerFrequencies { get; } = new();

    /// <summary>Entry id to the file that authored it — the other half of a duplicate report.</summary>
    public Dictionary<string, string> SourceOf { get; } = new(StringComparer.Ordinal);

    public int Count =>
        Atoms.Count + Containers.Count + Affixes.Count + Curves.Count + Rarities.Count
        + Elements.Count + ElementMatrix.Count + ChannelPolicies.Count + ChannelPools.Count
        + Coefficients.Count + TriggerFrequencies.Count;
}

/// <summary>What a collection pass produced, and everything wrong with it.</summary>
public sealed record SeedCollectResult(SeedContent Content, IReadOnlyList<SeedError> Errors)
{
    public bool IsOk => Errors.Count == 0;
}

/// <summary>
/// The authored seed format (E14a) — JSON files that diff, review and version like code, holding
/// content that lives in rows at runtime.
///
/// <para><b>JSON columns are stored canonically.</b> An authored <c>params</c> object is written to
/// <c>params_json</c> through <see cref="ContentHash.CanonicalJson"/>, not as the author typed it.
/// Storing the raw text would make re-indenting a seed file differ from the stored column, which
/// bumps <c>revision</c> — a hashed column — and so moves the content hash for an edit that changed
/// no content at all.</para>
///
/// <para><b>The kind comes from the file, not the folder.</b> <c>gk-data/packs/fusion/data/seed/atoms/</c> is a convention
/// for humans; a file that says <c>"kind": "atom"</c> is an atom file wherever it sits. Encoding the
/// layout twice means two places to be wrong.</para>
/// </summary>
public static class AtomSeedFile
{
    /// <summary>The only format version this importer reads. A newer file is refused, never guessed at.</summary>
    public const int SchemaVersion = 1;

    static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// Read every file into one content set. Files are read in the order given; the caller sorts, so
    /// a duplicate names the same two paths on every machine.
    /// </summary>
    public static SeedCollectResult Collect(IEnumerable<(string Path, string Json)> files)
    {
        var content = new SeedContent();
        var errors = new List<SeedError>();

        foreach (var (path, json) in files)
            ReadInto(path, json, content, errors);

        return new SeedCollectResult(content, errors);
    }

    static void ReadInto(string path, string json, SeedContent into, List<SeedError> errors)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            errors.Add(new SeedError(path, "", AtomRejectionReason.BadParamValue, "not valid JSON: " + ex.Message));
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new SeedError(path, "", AtomRejectionReason.BadParamValue,
                    $"a seed file is an object with 'kind' and 'entries', got {root.ValueKind}"));
                return;
            }

            // An unknown version is refused rather than read optimistically: a format that grew a
            // field would otherwise be imported with that field silently dropped.
            var version = Int(root, "schemaVersion", 0);
            if (version != SchemaVersion)
            {
                errors.Add(new SeedError(path, "", AtomRejectionReason.BadParamValue,
                    $"schemaVersion {version} — this importer reads {SchemaVersion}"));
                return;
            }

            if (!TryKind(Str(root, "kind"), out var kind))
            {
                errors.Add(new SeedError(path, "", AtomRejectionReason.UnknownKind,
                    $"kind '{Str(root, "kind")}' — one of "
                + "atom | container | affix | curve | rarity | element | element-matrix | power-coefficient"));
                return;
            }

            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new SeedError(path, "", AtomRejectionReason.BadParamValue, "no 'entries' array"));
                return;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new SeedError(path, "", AtomRejectionReason.BadParamValue,
                        $"an entry is an object, got {entry.ValueKind}"));
                    continue;
                }

                switch (kind)
                {
                    case SeedEntryKind.Atom: ReadAtom(path, entry, into, errors); break;
                    case SeedEntryKind.Container: ReadContainer(path, entry, into, errors); break;
                    case SeedEntryKind.Affix: ReadAffix(path, entry, into, errors); break;
                    case SeedEntryKind.Curve: ReadCurve(path, entry, into, errors); break;
                    case SeedEntryKind.Element: ReadElement(path, entry, into, errors); break;
                    case SeedEntryKind.ElementMatrix: ReadMatrixCell(path, entry, into, errors); break;
                    case SeedEntryKind.ChannelPolicy: ReadChannelPolicy(path, entry, into, errors); break;
                    case SeedEntryKind.ChannelPool: ReadChannelPool(path, entry, into, errors); break;
                    case SeedEntryKind.Coefficient: ReadCoefficient(path, entry, into, errors); break;
                    case SeedEntryKind.TriggerFrequency: ReadTriggerFrequency(path, entry, into, errors); break;
                    default: ReadRarity(path, entry, into, errors); break;
                }
            }
        }
    }

    // ---- entries -------------------------------------------------------------------------------

    static void ReadAtom(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var family = Str(e, "family");
        var variant = Str(e, "variant");
        var tier = Int(e, "tier", 1);

        // The id is optional and derived when absent. When present it is kept as authored, not
        // rewritten: an id that disagrees with its own columns is E4's IdMismatch, and silently
        // preferring one side would import content nobody wrote.
        var id = Str(e, "id");
        if (id.Length == 0) id = AtomRow.DeriveId(family, variant, tier);

        if (!Claim(path, id, into, errors)) return;

        into.Atoms.Add(new AtomRow
        {
            AtomId = id,
            KindId = Str(e, "kind"),
            FamilyId = family,
            Variant = variant,
            Tier = tier,
            Name = Str(e, "name"),
            WhenJson = Json(e, "when", "{}"),
            ParamsJson = Json(e, "params", "{}"),
            TagsJson = Json(e, "tags", "{}"),
            PowerJson = JsonOrNull(e, "power"),
            PowerOverrideJson = JsonOrNull(e, "powerOverride"),
            PowerNote = StrOrNull(e, "powerNote"),
            IcdKey = StrOrNull(e, "icdKey"),
            Enabled = Bool(e, "enabled", true),
        });
    }

    static void ReadContainer(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var id = Str(e, "id");
        if (!Claim(path, id, into, errors)) return;

        if (!TryContainerKind(Str(e, "kind"), out var kind))
        {
            errors.Add(new SeedError(path, id, AtomRejectionReason.UnknownKind,
                $"container kind '{Str(e, "kind")}' is not one of the six"));
            return;
        }

        var atoms = new List<ContainerAtomRow>();
        if (e.TryGetProperty("atoms", out var atomEls) && atomEls.ValueKind == JsonValueKind.Array)
        {
            var seq = 0;
            foreach (var a in atomEls.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                // seq defaults to the authored order, which is what writing a list already means.
                atoms.Add(new ContainerAtomRow(Int(a, "seq", seq), Str(a, "atom"), JsonOrNull(a, "overrides")));
                seq++;
            }
        }

        var pool = new List<ContainerPoolRow>();
        if (e.TryGetProperty("pool", out var poolEls) && poolEls.ValueKind == JsonValueKind.Array)
            foreach (var p in poolEls.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;

                // E32 (spec-affix-import-path.md §2, decided 2026-09-03): the pool row key is
                // "affix" — ContainerPoolRow.AffixId references an AffixRow, never a bare atom
                // (definitions.md §4a). The old "atom" key is refused, naming the rename, so the
                // latent defect this module closes cannot silently return once any container gains a
                // real pool.
                if (p.TryGetProperty("atom", out _) && !p.TryGetProperty("affix", out _))
                {
                    errors.Add(new SeedError(path, id, AtomRejectionReason.BadParamValue,
                        "a pool row uses 'atom' — pool rows reference an AFFIX, not a bare atom; " +
                        "rename the key to 'affix' (spec-affix-import-path.md §2)"));
                    return;
                }

                pool.Add(new ContainerPoolRow(Str(p, "affix"), Int(p, "weight", 0), StrOrNull(p, "group")));
            }

        into.Containers.Add(new ContainerRow
        {
            ContainerId = id,
            Kind = kind,
            Slot = StrOrNull(e, "slot"),
            Rarity = StrOrNull(e, "rarity"),
            MinTier = IntOrNull(e, "minTier"),
            MaxTier = IntOrNull(e, "maxTier"),
            LevelReq = IntOrNull(e, "levelReq"),
            PrefixRolls = Int(e, "prefixRolls", 0),
            SuffixRolls = Int(e, "suffixRolls", 0),
            TagsJson = Json(e, "tags", "{}"),
            Enabled = Bool(e, "enabled", true),
            Atoms = atoms,
            Pool = pool,
        });
    }

    /// <summary>
    /// A named, ordered bundle of atom refs (module 1, `affix-schema`). <c>class</c> is authored here
    /// only when the file supplies one — <see cref="AffixValidator.Validate"/> and
    /// <see cref="AffixValidator.ResolveClass"/> are the one place that actually owns derivation.
    ///
    /// <para><b>E32 (spec-affix-import-path.md §3.2, decided 2026-09-03): an authored <c>class</c> is
    /// now OPTIONAL.</b> Absent is legal — the shape a real generator emits, since a model that names
    /// its own class can contradict the bundle it just picked (seedsmith's own `derive.py` reasoning).
    /// Present-but-unparseable is still a refusal (an authored value must be one of the three legal
    /// strings if it is there at all); present-and-checked-against-the-derived-value happens later, in
    /// <c>AffixValidator.Validate</c>, which is the one place with an atom lookup to derive against.
    /// This method only parses; it never validates — same division of labor <see cref="ReadContainer"/>
    /// already has with <c>ContainerValidator</c>.</para>
    /// </summary>
    static void ReadAffix(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var id = Str(e, "id");
        if (!Claim(path, id, into, errors)) return;

        AffixClass? affixClass = null;
        if (e.TryGetProperty("class", out var classEl) && classEl.ValueKind == JsonValueKind.String)
        {
            if (!Enum.TryParse<AffixClass>(classEl.GetString(), ignoreCase: true, out var parsed)
                || !Enum.IsDefined(typeof(AffixClass), parsed))
            {
                errors.Add(new SeedError(path, id, AtomRejectionReason.BadParamValue,
                    $"affix class '{classEl.GetString()}' — one of prefix | suffix | mixed"));
                return;
            }
            affixClass = parsed;
        }
        else if (e.TryGetProperty("class", out var badClassEl) && badClassEl.ValueKind != JsonValueKind.String)
        {
            errors.Add(new SeedError(path, id, AtomRejectionReason.BadParamValue,
                $"affix class must be a string, got {badClassEl.ValueKind} — one of prefix | suffix | mixed"));
            return;
        }
        // else: 'class' key absent entirely — legal, derived later (§3.2).

        var refs = new List<AffixRefRow>();
        if (e.TryGetProperty("refs", out var refEls) && refEls.ValueKind == JsonValueKind.Array)
        {
            var seq = 0;
            foreach (var r in refEls.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var atomId = StrOrNull(r, "atom");
                var slotName = StrOrNull(r, "slotName");
                refs.Add(new AffixRefRow(
                    Int(r, "seq", seq), atomId,
                    slotName, StrOrNull(r, "slotDomain"),
                    Int(r, "slotPick", 0), StrOrNull(r, "slotAtomPattern")));
                seq++;
            }
        }

        into.Affixes.Add(new AffixRow(id, affixClass, refs));
    }

    static void ReadCurve(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var id = Str(e, "id");
        if (!Claim(path, id, into, errors)) return;

        var inputName = Str(e, "input");
        if (!Enum.TryParse<CurveInput>(inputName, ignoreCase: true, out var input)
            || !Enum.IsDefined(typeof(CurveInput), input))
        {
            errors.Add(new SeedError(path, id, AtomRejectionReason.BadCurve,
                $"input '{inputName}' — one of level | rarity | tier"));
            return;
        }

        var points = new List<CurvePoint>();
        if (e.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Array)
            foreach (var p in pts.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                points.Add(new CurvePoint(Int(p, "x", 0), Int(p, "mult", 1000)));
            }

        into.Curves.Add(new CurveSeed(id, input, points));
    }

    static void ReadRarity(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var id = Str(e, "id");
        if (!Claim(path, id, into, errors)) return;

        into.Rarities.Add(new RarityRow(
            id, Int(e, "ordinal", 0), Int(e, "prefixRolls", 0), Int(e, "suffixRolls", 0),
            Int(e, "minTier", 1), Int(e, "maxTier", 1)));
    }

    static void ReadElement(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var id = Str(e, "id");
        if (!Claim(path, id, into, errors)) return;

        // Absent is refused, never defaulted to 0: an ordinal is the element's identity in every
        // generated channel, and a silent 0 would collide with fire.
        if (IntOrNull(e, "ordinal") is not { } ordinal)
        {
            errors.Add(new SeedError(path, id, AtomRejectionReason.MissingParam,
                "an element needs an explicit ordinal — it names every channel generated from it"));
            return;
        }

        into.Elements.Add(new ElementRow(id, Str(e, "displayName"), ordinal, Bool(e, "enabled", true)));
    }

    /// <summary>
    /// One channel's values (E22). <c>channel</c> is required — the store refuses one that is not
    /// already in <c>StatChannels.All</c>, so authoring a new channel here is a refusal, not a feature.
    /// </summary>
    static void ReadChannelPolicy(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var channel = Str(e, "channel");
        if (!Claim(path, channel, into, errors)) return;

        if (IntOrNull(e, "direction") is not { } direction)
        {
            errors.Add(new SeedError(path, channel, AtomRejectionReason.MissingParam,
                "a channel policy needs an explicit direction — 0 higher-is-better, 1 lower-is-better"));
            return;
        }

        into.ChannelPolicies.Add(new ChannelPolicySeedRow(channel, direction));
    }

    /// <summary>E30 (spec-channel-pool.md §3.1): one named, weighted channel pool. Member parsing is
    /// shared with <see cref="ChannelPoolFile.TryParse"/> (the whole-document form) via
    /// <see cref="ChannelPoolFile.TryParseEntry"/>, so the two never validate a pool entry
    /// differently.</summary>
    static void ReadChannelPool(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var read = ChannelPoolFile.TryParseEntry(e, out var row);
        if (!read.IsOk)
        {
            errors.Add(new SeedError(path, Str(e, "id"), read.Reason, read.Detail));
            return;
        }

        if (!Claim(path, row.PoolId, into, errors)) return;

        into.ChannelPools.Add(row);
    }

    /// <summary>
    /// One priced coefficient row (E44 criterion 0, spec-power-sweep.md §4.1) — the closest existing
    /// sibling is <see cref="ReadChannelPolicy"/>, also a flat row type, and this mirrors its shape:
    /// structural checks only (a required field must be explicit), the semantic check (reference scale
    /// must be positive — normalisation divides by it) stays where it already lives,
    /// <c>RpgStore.UpsertPowerTables</c>'s own, reused unchanged by the import path so this needs no
    /// new validation logic either.
    ///
    /// <para><c>channel</c> is optional and defaults to <c>""</c>, mirroring
    /// <see cref="PowerCoefficientRow.Channel"/>'s own "empty means priced the same regardless of
    /// channel" shape. The claim key is <c>kindId/channel</c> (or <c>kindId/*</c> when channel is
    /// absent) — the same compound identity <c>RpgStore.UpsertPowerTables</c>'s own error strings
    /// already format a coefficient by — rather than the bare <c>channel</c> <see cref="ReadChannelPolicy"/>
    /// claims, because a coefficient's real key is the (kind, channel) pair the table's own primary key
    /// names, and a bare channel would collide with an unrelated channel-policy row naming the same
    /// channel string.</para>
    /// </summary>
    static void ReadCoefficient(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var kindId = Str(e, "kindId");
        var channel = Str(e, "channel");
        var coeffId = $"{kindId}/{(channel.Length == 0 ? "*" : channel)}";

        if (kindId.Length == 0)
        {
            errors.Add(new SeedError(path, coeffId, AtomRejectionReason.MissingParam,
                "a coefficient needs an explicit kindId"));
            return;
        }

        if (!Claim(path, coeffId, into, errors)) return;

        if (IntOrNull(e, "coeffMilli") is not { } coeffMilli)
        {
            errors.Add(new SeedError(path, coeffId, AtomRejectionReason.MissingParam,
                "a coefficient needs an explicit coeffMilli — points per reference unit, per-mille"));
            return;
        }

        if (IntOrNull(e, "referenceScale") is not { } referenceScale)
        {
            errors.Add(new SeedError(path, coeffId, AtomRejectionReason.MissingParam,
                "a coefficient needs an explicit referenceScale — what \"one unit\" means for this " +
                "channel; normalisation divides by it, so it cannot be defaulted"));
            return;
        }

        into.Coefficients.Add(new PowerCoefficientRow(kindId, channel, coeffMilli, referenceScale));
    }

    /// <summary>
    /// ST4.5c (manager ruling on the ST4.5a diagnosis): one <c>power_trigger_frequency</c> row. The
    /// sibling of <see cref="ReadCoefficient"/> and structural for the same reason — both fields are
    /// explicit, never defaulted.
    ///
    /// <para><b><c>perMinute</c> must be positive, and that is a domain rule rather than a balance
    /// one.</b> <c>CostFunction.Conditionality</c> multiplies an atom's price by
    /// <c>perMinute/60</c>, so a row of zero prices every atom carrying that trigger at exactly zero —
    /// the silent-0 defect this seed kind exists to close, which would otherwise be authorable here.
    /// A trigger that never fires is not content; leave the trigger unlisted and the pricing path
    /// names it as unpriced instead.</para>
    /// </summary>
    static void ReadTriggerFrequency(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var trigger = Str(e, "trigger");
        if (trigger.Length == 0)
        {
            errors.Add(new SeedError(path, "", AtomRejectionReason.MissingParam,
                "a trigger frequency needs an explicit trigger"));
            return;
        }

        if (!Claim(path, trigger, into, errors)) return;

        if (IntOrNull(e, "perMinute") is not { } perMinute)
        {
            errors.Add(new SeedError(path, trigger, AtomRejectionReason.MissingParam,
                "a trigger frequency needs an explicit perMinute — fires per battle-minute; " +
                "Conditionality multiplies by it, so it cannot be defaulted"));
            return;
        }

        if (perMinute <= 0)
        {
            errors.Add(new SeedError(path, trigger, AtomRejectionReason.BadParamValue,
                $"perMinute {perMinute} — a trigger that never fires prices every atom carrying it at " +
                "zero; leave the trigger unlisted and pricing names it unpriced instead"));
            return;
        }

        into.TriggerFrequencies.Add(new TriggerFrequencyRow(trigger, perMinute));
    }

    /// <summary>
    /// One matchup cell. <c>matrix</c> chooses <c>combat</c> or <c>shield</c> — required, because a
    /// cell that guessed its matrix would silently rebalance the other one.
    /// </summary>
    static void ReadMatrixCell(string path, JsonElement e, SeedContent into, List<SeedError> errors)
    {
        var matrix = Str(e, "matrix").ToLowerInvariant();
        var attacker = Str(e, "attacker");
        var defender = Str(e, "defender");
        var cellId = $"{matrix}:{attacker}>{defender}";

        if (matrix is not ("combat" or "shield"))
        {
            errors.Add(new SeedError(path, cellId, AtomRejectionReason.BadParamValue,
                $"matrix '{matrix}' — one of combat | shield"));
            return;
        }

        if (attacker.Length == 0 || defender.Length == 0)
        {
            errors.Add(new SeedError(path, cellId, AtomRejectionReason.MissingParam,
                "a matchup cell needs both an attacker and a defender"));
            return;
        }

        if (!Claim(path, cellId, into, errors)) return;

        var unit = Int(e, "unit", 0);
        if (unit is < -1 or > 1)
        {
            errors.Add(new SeedError(path, cellId, AtomRejectionReason.BadParamValue,
                $"unit {unit} — one of -1 | 0 | 1; K is applied by the caller, once"));
            return;
        }

        into.ElementMatrix.Add((matrix, new ElementMatrixRow(attacker, defender, unit)));
    }

    /// <summary>
    /// Take the id, or report who already has it.
    ///
    /// <para>Last write wins would make the imported content depend on directory iteration order —
    /// the same files would produce a different content hash on a different filesystem.</para>
    /// </summary>
    static bool Claim(string path, string id, SeedContent into, List<SeedError> errors)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(new SeedError(path, "", AtomRejectionReason.BadParamValue, "entry has no id"));
            return false;
        }

        if (into.SourceOf.TryGetValue(id, out var first))
        {
            errors.Add(new SeedError(path, id, AtomRejectionReason.DuplicateKey,
                $"already authored in {first}"));
            return false;
        }

        into.SourceOf[id] = path;
        return true;
    }

    // ---- json helpers --------------------------------------------------------------------------

    static bool TryKind(string name, out SeedEntryKind kind)
    {
        switch (name.ToLowerInvariant())
        {
            case "atom": kind = SeedEntryKind.Atom; return true;
            case "container": kind = SeedEntryKind.Container; return true;
            case "affix": kind = SeedEntryKind.Affix; return true;
            case "curve": kind = SeedEntryKind.Curve; return true;
            case "rarity": kind = SeedEntryKind.Rarity; return true;
            case "element": kind = SeedEntryKind.Element; return true;
            case "element-matrix": kind = SeedEntryKind.ElementMatrix; return true;
            case "channel-policy": kind = SeedEntryKind.ChannelPolicy; return true;
            case "channel-pool": kind = SeedEntryKind.ChannelPool; return true;
            case "power-coefficient": kind = SeedEntryKind.Coefficient; return true;
            case "power-trigger-frequency": kind = SeedEntryKind.TriggerFrequency; return true;
            default: kind = SeedEntryKind.Atom; return false;
        }
    }

    static bool TryContainerKind(string name, out ContainerKind kind)
    {
        // `species-passive` and `world-buff` are the authored spellings (definitions §1); the enum
        // spells them in Pascal case. Separators are dropped rather than mapped one by one.
        var normalised = name.Replace("-", "").Replace("_", "");
        return Enum.TryParse(normalised, ignoreCase: true, out kind)
               && Enum.IsDefined(typeof(ContainerKind), kind);
    }

    static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    static string? StrOrNull(JsonElement o, string name)
    {
        var s = Str(o, name);
        return s.Length == 0 ? null : s;
    }

    static int Int(JsonElement o, string name, int fallback) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v : fallback;

    static int? IntOrNull(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v : null;

    static bool Bool(JsonElement o, string name, bool fallback)
    {
        if (!o.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    /// <summary>A nested object, canonicalised — see the class remark on why the raw text is not stored.</summary>
    static string Json(JsonElement o, string name, string fallback) =>
        o.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? ContentHash.CanonicalJson(el.GetRawText())
            : fallback;

    static string? JsonOrNull(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? ContentHash.CanonicalJson(el.GetRawText())
            : null;
}
