using System.Globalization;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// The <c>op_kind</c> namespace (ssot-enhancement.md §5.3). <b>It is this module's, and it is
/// closed</b> — modules 14 and 16 draw from it rather than minting their own, which is why the four
/// socket kinds live here even though module 16 performs them. Adding a member is ask-first.
/// </summary>
public enum MutationOpKind
{
    /// <summary>+n → +n+1. Adds a scalar and, on a stride level, a milestone atom. Redraws nothing.</summary>
    Enhance = 0,

    /// <summary>Temper — redraws the VALUE of one affix inside its own range.</summary>
    RerollValue,

    /// <summary>Reforge / Imprint — redraws identity, tier and value of a chosen subset.</summary>
    RerollAffix,

    /// <summary>Transfer, donor side. Shares one correlation id with <see cref="EnhanceTransferIn"/>.</summary>
    EnhanceTransferOut,

    /// <summary>Transfer, recipient side.</summary>
    EnhanceTransferIn,

    /// <summary>Administrative rollback to a recorded <c>op_seq</c>.</summary>
    Restore,

    /// <summary>Open a socket. Performed by module 16.</summary>
    SocketAdd,

    /// <summary>Put an insert into an open socket. Performed by module 16.</summary>
    SocketInsert,

    /// <summary>Take an insert out. Performed by module 16.</summary>
    SocketRemove,

    /// <summary>
    /// D24 — declare a crafted socket's element affinity. <b>Minted here</b>, because inventing it in
    /// module 16 would fork the namespace (spec-enhance-reroll.md §6; module 14 priced the operation
    /// and deliberately left the op_kind to this module).
    /// </summary>
    SocketImbue,

    /// <summary>
    /// species-gear-chain T23 — workbench repair, the eleventh member: `deployment-hierarchy-map.md:89`
    /// reserved this ordinal and the durability slice pulled its storage forward as T11. Repair RESTORES
    /// `durability_current`; it never raises `max`, and its destroy chance (D1) is the one sink that
    /// removes an item from the economy — which is why wear itself never destroys (T24).
    /// </summary>
    Repair,

    /// <summary>
    /// species-gear-chain T25 — rarity promotion, the twelfth member. Moves an item one rung up
    /// <see cref="RarityLadder"/> under the same priced verb `elevate` already prices, so it adds no
    /// `CraftOperation` and no tuning row. Purely additive: every affix survives identically, and the
    /// item carries its own identity rather than becoming the named successor.
    /// </summary>
    Promotion,
    /// <summary>
    /// species-gear-chain T37 — the upgrade tree, the thirteenth member: consume one instance and
    /// produce a DIFFERENT one (a successor chassis carrying the same affixes). The op ledger's
    /// `instance_id` is the CONSUMED item, so an upgrade reads as that item's own history; the
    /// successor is named by the apply path's outcome ref. No private failure chance — a refusal
    /// consumes nothing (spec-item-upgrade-tree.md § 4, § 5).
    /// </summary>
    Upgrade,
}

/// <summary>
/// What an enhancement attempt decided. <b>There is no destroy outcome — not as an enum value, not
/// as a reason code.</b> A code nothing emits is a lie in a table, and reserving one invites a later
/// session to wire it up (spec §4).
/// </summary>
public enum EnhanceOutcome
{
    /// <summary>Level went up by one.</summary>
    Success = 0,

    /// <summary>Materials spent, level unchanged, pity counter +1.</summary>
    Failure,

    /// <summary>As <see cref="Failure"/>, and one level lost — only at or above
    /// <c>downgradeFromLevel</c>, and only when no <c>ward.enhance</c> is loaded.</summary>
    FailureWithDowngrade,
}

/// <summary>Structural limits. Neither is a balance number and both say why.</summary>
public static class MutationLimits
{
    /// <summary>
    /// <c>mutation_seq</c>'s ceiling. <b>Structural, not a design ceiling</b>: it bounds a retry loop
    /// and a log's length, not how strong an item may become. An op that would exceed it THROWS —
    /// AGENTS.md's rule for an absolute bound; it never clamps, because a clamp turns a runaway loop
    /// into a silent no-op.
    /// </summary>
    public const int MutationSeqCap = 4096;
}

/// <summary>One atom's frozen numbers, as the head holds them.</summary>
public sealed record InstanceAtomHead(
    int Seq, string AtomId, IReadOnlyDictionary<string, long> Values, bool Suppressed = false);

/// <summary>
/// D2 clause 1 — the head is the SSOT. <c>effect_instance_atom.values_json</c> always holds the
/// current numbers; no read path composes anything.
/// </summary>
public sealed record InstanceHead(int EnhanceLevel, IReadOnlyList<InstanceAtomHead> Atoms)
{
    public static readonly InstanceHead Empty = new(0, Array.Empty<InstanceAtomHead>());
}

/// <summary>One materialised value write. Absolute, never a formula (D2 clause 4).</summary>
public readonly record struct AtomValueSet(int Seq, string Key, long Value);

/// <summary>One appended atom row. <c>Seq</c> is allocated, never reused, never renumbered.</summary>
public sealed record AtomAppend(int Seq, string AtomId, IReadOnlyDictionary<string, long> Values);

/// <summary>
/// D2 clause 4 — <b>the recorded result, never the recipe.</b> Everything here is a materialised
/// delta plus the decided outcome; replay applies it verbatim and never re-runs a formula or
/// re-rolls a die. This is what makes a rebalance structurally unable to reach backwards into an
/// item a player already owns.
/// </summary>
public sealed record MutationResult(
    string Outcome,
    int EnhanceLevelDelta,
    IReadOnlyList<AtomValueSet> Values,
    IReadOnlyList<int> Suppressed,
    IReadOnlyList<AtomAppend> Appended)
{
    public static MutationResult Nothing(string outcome) =>
        new(outcome, 0, Array.Empty<AtomValueSet>(), Array.Empty<int>(), Array.Empty<AtomAppend>());
}

/// <summary>
/// One row of <c>effect_instance_op</c> (D2 §9 clause 2). <c>Seq</c> is dense and gapless per
/// instance; <c>CorrelationId</c> is unique per instance and carries clause 8's idempotency.
/// </summary>
/// <param name="CatalogRevision">D2 clause 5 — the op stamps its <b>own</b> catalog revision. It is
/// NOT <c>effect_instance.catalog_revision</c>, which is origin-only and which no operation rewrites.</param>
/// <param name="RulesVersion">D2 clause 5's other half: which rules decided this op. Recorded for
/// provenance only — replay never reads it, because replay never re-runs a formula (clause 4).</param>
/// <param name="CostJson">D2 clause 11 — the spend, in module 14's material vocabulary. "A spent cost
/// with no op is theft; an op with no cost is duplication."</param>
public sealed record MutationOp(
    string InstanceId,
    int Seq,
    MutationOpKind Kind,
    string CorrelationId,
    long OpSeed,
    MutationResult Result,
    string AppliedUtc,
    long CatalogRevision = 0,
    int RulesVersion = 0,
    string CostJson = "{}");

/// <summary>Ids, parsing and the seeded stream name. Kebab-case, matching what a row stores.</summary>
public static class MutationOpKinds
{
    public static string Id(MutationOpKind kind) => kind switch
    {
        MutationOpKind.Enhance => "enhance",
        MutationOpKind.RerollValue => "reroll-value",
        MutationOpKind.RerollAffix => "reroll-affix",
        MutationOpKind.EnhanceTransferOut => "enhance-transfer-out",
        MutationOpKind.EnhanceTransferIn => "enhance-transfer-in",
        MutationOpKind.Restore => "restore",
        MutationOpKind.SocketAdd => "socket-add",
        MutationOpKind.SocketInsert => "socket-insert",
        MutationOpKind.SocketRemove => "socket-remove",
        MutationOpKind.SocketImbue => "socket-imbue",
        MutationOpKind.Repair => "repair",
        MutationOpKind.Promotion => "promotion",
        MutationOpKind.Upgrade => "upgrade",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static IReadOnlyList<MutationOpKind> All { get; } =
        Enum.GetValues<MutationOpKind>().OrderBy(k => (int)k).ToArray();

    public static IReadOnlyList<string> AllIds { get; } = All.Select(Id).ToArray();

    public static bool TryParse(string? value, out MutationOpKind kind)
    {
        var key = (value ?? "").Trim();
        foreach (var candidate in All)
        {
            if (!string.Equals(Id(candidate), key, StringComparison.Ordinal)) continue;
            kind = candidate;
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>
    /// The Boundaries' named stream: <c>SeededRng.DeriveStream(op_seed, "item.{op_kind}")</c> — one
    /// per op kind, recorded even when the operation rolls nothing, so adding a roll to an operation
    /// later never shifts another operation's sequence.
    /// </summary>
    public static string StreamName(MutationOpKind kind) => "item." + Id(kind);
}

/// <summary>
/// Canonical serialisation for <see cref="MutationResult"/> and the head's <c>state_hash</c>
/// (definitions §8): <b>SHA256 over a length-prefixed, sorted, concatenated form. XOR-folding is
/// banned</b> — it is order-insensitive by construction, which is exactly the property a state hash
/// must not have.
/// </summary>
public static class MutationCanonical
{
    public static string WriteResult(MutationResult result)
    {
        var opts = new JsonWriterOptions { Indented = false };
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, opts))
        {
            w.WriteStartObject();
            w.WriteString("outcome", result.Outcome);
            w.WriteNumber("enhanceLevelDelta", result.EnhanceLevelDelta);

            w.WriteStartArray("values");
            foreach (var v in result.Values.OrderBy(v => v.Seq).ThenBy(v => v.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteNumber("seq", v.Seq);
                w.WriteString("key", v.Key);
                w.WriteNumber("value", v.Value);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("suppressed");
            foreach (var s in result.Suppressed.OrderBy(s => s)) w.WriteNumberValue(s);
            w.WriteEndArray();

            w.WriteStartArray("appended");
            foreach (var a in result.Appended.OrderBy(a => a.Seq))
            {
                w.WriteStartObject();
                w.WriteNumber("seq", a.Seq);
                w.WriteString("atomId", a.AtomId);
                w.WriteStartObject("values");
                foreach (var (k, val) in a.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    w.WriteNumber(k, val);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static MutationResult ReadResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var values = new List<AtomValueSet>();
        foreach (var v in root.GetProperty("values").EnumerateArray())
            values.Add(new AtomValueSet(v.GetProperty("seq").GetInt32(), v.GetProperty("key").GetString()!,
                v.GetProperty("value").GetInt64()));

        var suppressed = root.GetProperty("suppressed").EnumerateArray().Select(e => e.GetInt32()).ToList();

        var appended = new List<AtomAppend>();
        foreach (var a in root.GetProperty("appended").EnumerateArray())
        {
            var vals = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var p in a.GetProperty("values").EnumerateObject()) vals[p.Name] = p.Value.GetInt64();
            appended.Add(new AtomAppend(a.GetProperty("seq").GetInt32(), a.GetProperty("atomId").GetString()!, vals));
        }

        return new MutationResult(
            root.GetProperty("outcome").GetString()!,
            root.GetProperty("enhanceLevelDelta").GetInt32(),
            values, suppressed, appended);
    }

    /// <summary>
    /// The head's canonical form: every field length-prefixed, atoms sorted by <c>seq</c> and keys
    /// sorted ordinally, then concatenated and hashed once, <b>including suppressed rows</b> (D2
    /// clause 12). Length prefixes are what stop <c>("ab","c")</c> and <c>("a","bc")</c> hashing the
    /// same, and XOR-folding is banned because it is order-insensitive by construction.
    ///
    /// <para>definitions §8's <c>N:</c> NULL marker is honoured by <see cref="Null"/>; no field on the
    /// head is nullable today, so it is never emitted — it exists so a nullable column added later
    /// cannot be silently encoded as an empty string, which would collide with a real empty one.</para>
    /// </summary>
    public static string StateHash(InstanceHead head)
    {
        var sb = new StringBuilder();
        Append(sb, head.EnhanceLevel.ToString(CultureInfo.InvariantCulture));
        foreach (var atom in head.Atoms.OrderBy(a => a.Seq))
        {
            Append(sb, atom.Seq.ToString(CultureInfo.InvariantCulture));
            Append(sb, atom.AtomId);
            Append(sb, atom.Suppressed ? "1" : "0");
            foreach (var (k, v) in atom.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                Append(sb, k);
                Append(sb, v.ToString(CultureInfo.InvariantCulture));
            }
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>definitions §8's NULL marker — distinct from a zero-length string by construction.</summary>
    internal const string Null = "N:";

    static void Append(StringBuilder sb, string? field) =>
        sb.Append(field is null
            ? Null
            : field.Length.ToString(CultureInfo.InvariantCulture) + ":" + field).Append('|');
}

/// <summary>
/// The two content-rule namespaces this module raises under, registered where every other item lane
/// registers (ItemCategoryTable, DropTableValidator, ThresholdEvaluator): once, at the point the
/// lane starts raising ids. <c>ContentRuleViolated{enhance.*}</c> and
/// <c>ContentRuleViolated{reroll.*}</c> — <b>never a new member of the closed 33-code list.</b>
/// </summary>
public static class MutationRules
{
    public const string EnhanceNamespace = "enhance";
    public const string RerollNamespace = "reroll";
    public const string MutationNamespace = "mutation";

    static MutationRules()
    {
        ContentRuleNamespaces.Register(EnhanceNamespace);
        ContentRuleNamespaces.Register(RerollNamespace);
        ContentRuleNamespaces.Register(MutationNamespace);
    }

    /// <summary>Touch to force the static registration — the same idiom the other lanes use.</summary>
    public static void EnsureRegistered() { }

    public static AtomRejection Violated(string ruleId, string detail) =>
        AtomRejection.ContentRule(ruleId, detail);
}
