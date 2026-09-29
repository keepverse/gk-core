using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

public sealed class CharmAttunementTuningRejection : Exception
{
    /// <summary>The namespaced content rule this refusal carries, for a caller that wants the code.</summary>
    public AtomRejection Rejection { get; }

    public CharmAttunementTuningRejection(string ruleId, string detail) : base($"{ruleId}: {detail}")
    {
        // `charm`, not `threshold`: these rule ids are this module's, and AtomRejection.ContentRule
        // THROWS on an unregistered prefix rather than accepting an unknown vocabulary — which is how
        // the mismatch surfaced here as a failing test instead of as a mystery at boot.
        CharmCarryRules.EnsureRegistered();
        Rejection = AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// `gk-core/data/tuning/charm-attunement.v1.json`, parsed. Pure — no file I/O (tunables-ssot.md §7.2: "Core
/// never reads a file. Hosts load and inject."), matching <see cref="FrameMixTuning"/> and
/// <see cref="FusionRpg.Core.Items.Consumables.ConsumableTuning"/>.
///
/// <para><b>⛔ There is no maximum capacity here, and the parser refuses one BY NAME.</b> ssot-charms
/// §3.3 says "6 AP at start, 20 AP at cap"; AGENTS.md forbids a hard progression ceiling, so 20 is the
/// last AUTHORED rung of <see cref="CapacityLadder"/> and nothing in code refuses a capacity above it.
/// <see cref="CharmPouchGate"/> takes whatever capacity it is handed and never clamps. A
/// <c>maxCapacityAp</c> key would be exactly the ceiling wearing a balance name that the rule exists to
/// stop, so it is refused at load rather than silently ignored — the device module 18 used for its
/// withdrawn <c>carryLimit</c> key.</para>
///
/// <para><b>Every number a balance pass would touch is here and the parser REFUSES rather than
/// defaults.</b> A missing key raises: a gate silently running on a default capacity is how an
/// unreviewed number reaches every pouch in the game.</para>
/// </summary>
public readonly record struct CharmAttunementTuning(
    IReadOnlyList<int> ApCostDomain,
    IReadOnlyList<long> CapacityLadder,
    int AxisCapPerSnapshot,
    int CopyCapPerContainer,
    int UniqueCarryCopyCap,
    string BindingSource,
    string BindingOwnerKind,
    int BindingPriority)
{
    /// <summary>The first rung. <c>long</c> because an AP total is a magnitude summed against it.</summary>
    public long StartingCapacityAp => CapacityLadder[0];

    /// <summary>
    /// Capacity at a zero-based progression rung. Above the last AUTHORED rung this returns the last
    /// one — <b>content exhaustion, not a ceiling</b>: appending rungs to the file extends the ladder
    /// with no code change, and <see cref="CharmPouchGate"/> accepts a capacity above every rung here.
    /// </summary>
    public long CapacityAtRung(int rung)
    {
        if (rung < 0) throw new ArgumentOutOfRangeException(nameof(rung), $"rung {rung} is negative");
        return rung >= CapacityLadder.Count ? CapacityLadder[^1] : CapacityLadder[rung];
    }

    /// <summary>The copy cap that applies to one charm — the tighter of the two, per §3.3 / §3.4.</summary>
    public int CopyCapFor(bool uniqueCarry) => uniqueCarry ? UniqueCarryCopyCap : CopyCapPerContainer;

    public static CharmAttunementTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CharmAttunementTuningRejection("charm.attunement-tuning-malformed", "empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new CharmAttunementTuningRejection("charm.attunement-tuning-malformed",
                $"not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Refused BY NAME rather than ignored: a key that reads like a ceiling and does nothing is
            // worse than one that does something, because a balance pass believes it set a limit.
            foreach (var withdrawn in new[] { "maxCapacityAp", "capacityCap", "apCeiling" })
                if (root.TryGetProperty(withdrawn, out _))
                    throw new CharmAttunementTuningRejection("charm.capacity-ceiling-not-permitted",
                        $"'{withdrawn}' is not a key of this file. AGENTS.md forbids a hard progression " +
                        "ceiling: capacityLadder's last rung is the last AUTHORED rung, and the gate never " +
                        "clamps to it. Append rungs instead");

            var parsed = new CharmAttunementTuning(
                ApCostDomain: IntList(root, "apCostDomain"),
                CapacityLadder: LongList(root, "capacityLadder"),
                AxisCapPerSnapshot: (int)Long(root, "axisCapPerSnapshot"),
                CopyCapPerContainer: (int)Long(root, "copyCapPerContainer"),
                UniqueCarryCopyCap: (int)Long(root, "uniqueCarryCopyCap"),
                BindingSource: Str(root, "bindingSource"),
                BindingOwnerKind: Str(root, "bindingOwnerKind"),
                BindingPriority: (int)Long(root, "bindingPriority"));

            Validate(parsed);
            return parsed;
        }
    }

    /// <summary>
    /// The structural invariants, each refused by its own rule id so a balance pass reads which one it
    /// broke rather than "invalid tuning".
    /// </summary>
    public static void Validate(CharmAttunementTuning t)
    {
        if (t.ApCostDomain.Count == 0)
            throw new CharmAttunementTuningRejection("charm.ap-domain-empty",
                "apCostDomain is empty; every charm's size is authored against it (§3.3)");

        for (var i = 0; i < t.ApCostDomain.Count; i++)
        {
            if (t.ApCostDomain[i] < 1)
                throw new CharmAttunementTuningRejection("charm.ap-domain-not-positive",
                    $"apCostDomain[{i}] is {t.ApCostDomain[i]}; a free charm is not a budget decision");
            if (i > 0 && t.ApCostDomain[i] <= t.ApCostDomain[i - 1])
                throw new CharmAttunementTuningRejection("charm.ap-domain-unordered",
                    $"apCostDomain[{i}] = {t.ApCostDomain[i]} does not exceed [{i - 1}] = " +
                    $"{t.ApCostDomain[i - 1]}; the domain is a ladder of sizes, and a repeat is a typo");
        }

        if (t.CapacityLadder.Count == 0)
            throw new CharmAttunementTuningRejection("charm.capacity-ladder-empty",
                "capacityLadder is empty; the gate has no starting capacity to check a pouch against");

        for (var i = 1; i < t.CapacityLadder.Count; i++)
            if (t.CapacityLadder[i] <= t.CapacityLadder[i - 1])
                throw new CharmAttunementTuningRejection("charm.capacity-ladder-unordered",
                    $"capacityLadder[{i}] = {t.CapacityLadder[i]} does not exceed [{i - 1}] = " +
                    $"{t.CapacityLadder[i - 1]}; a rung that grants nothing is progression that lies");

        var largest = t.ApCostDomain[^1];
        if (t.CapacityLadder[0] < largest)
            throw new CharmAttunementTuningRejection("charm.starting-capacity-below-largest-charm",
                $"the first capacity rung is {t.CapacityLadder[0]} and the largest charm costs {largest} " +
                "AP — a signet nobody can ever carry is dead content, and §6.1's 'a signet is 5 of 6' is " +
                "the whole reason the biggest class reads as a build rather than a stat stick");

        if (t.AxisCapPerSnapshot < 1)
            throw new CharmAttunementTuningRejection("charm.axis-cap-not-positive",
                $"axisCapPerSnapshot is {t.AxisCapPerSnapshot}; zero would forbid every loadout");

        if (t.CopyCapPerContainer < 1)
            throw new CharmAttunementTuningRejection("charm.copy-cap-not-positive",
                $"copyCapPerContainer is {t.CopyCapPerContainer}; zero would forbid every loadout");

        if (t.UniqueCarryCopyCap < 1)
            throw new CharmAttunementTuningRejection("charm.unique-carry-cap-not-positive",
                $"uniqueCarryCopyCap is {t.UniqueCarryCopyCap}; zero would make every signet unattunable");

        if (t.UniqueCarryCopyCap > t.CopyCapPerContainer)
            throw new CharmAttunementTuningRejection("charm.unique-carry-cap-not-tighter",
                $"uniqueCarryCopyCap {t.UniqueCarryCopyCap} exceeds copyCapPerContainer " +
                $"{t.CopyCapPerContainer} — 'unique' must be the TIGHTER of the two limits (§3.4), and " +
                "inverted it would silently loosen the class it exists to restrain");

        if (string.IsNullOrWhiteSpace(t.BindingSource))
            throw new CharmAttunementTuningRejection("charm.binding-source-empty",
                "bindingSource is empty; withdrawal at run end is BY SOURCE and an empty tag withdraws " +
                "everything or nothing");

        if (string.Equals(t.BindingSource, "draught", StringComparison.Ordinal))
            throw new CharmAttunementTuningRejection("charm.binding-source-collides-with-draught",
                "bindingSource is 'draught' — one snapshot mechanism, TWO sources (ssot-consumables.md " +
                "§9 item 10). Sharing the tag would make one run-end withdrawal take both layers down");

        // D33(a). The refusal is by NAME because the wrong answer here is a correctness bug the suite
        // cannot see: StatApplyScope.Matches returns true unconditionally for a player: owner, and
        // `match` matches both sides, so a player:-scoped +atk charm buffs the zombies.
        if (!string.Equals(t.BindingOwnerKind, "unique-actor", StringComparison.Ordinal))
            throw new CharmAttunementTuningRejection("charm.binding-owner-kind-not-actor",
                $"bindingOwnerKind is '{t.BindingOwnerKind}'. D33(a) binds charms at ACTOR scope: " +
                "unique-actor:{specimenId}, one binding per deployed actor. 'player' is the withdrawn " +
                "option C — the stat layer resolves it match-wide, so a player-scoped charm buffs both " +
                "sides — and 'match' is that bug stated outright");

        if (t.BindingPriority >= 0)
            throw new CharmAttunementTuningRejection("charm.binding-priority-not-below-equipment",
                $"bindingPriority is {t.BindingPriority}; an item binding is 0 and the actor effect list " +
                "sorts priority DESC, so a non-negative charm priority makes the account layer read " +
                "before the actor's own gear (ssot-charms.md §4.1)");
    }

    static IReadOnlyList<int> IntList(JsonElement parent, string key)
    {
        var list = new List<int>();
        foreach (var v in Array(parent, key).EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number)
                throw new CharmAttunementTuningRejection("charm.attunement-tuning-malformed",
                    $"'{key}' holds a non-numeric entry");
            list.Add(v.GetInt32());
        }
        return list;
    }

    static IReadOnlyList<long> LongList(JsonElement parent, string key)
    {
        var list = new List<long>();
        foreach (var v in Array(parent, key).EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number)
                throw new CharmAttunementTuningRejection("charm.attunement-tuning-malformed",
                    $"'{key}' holds a non-numeric entry");
            list.Add(v.GetInt64());
        }
        return list;
    }

    static JsonElement Array(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new CharmAttunementTuningRejection("charm.attunement-tuning-malformed",
                $"missing or non-array '{key}'");
        return el;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new CharmAttunementTuningRejection("charm.attunement-tuning-malformed",
                $"missing or non-numeric '{key}'");
        return el.GetInt64();
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new CharmAttunementTuningRejection("charm.attunement-tuning-malformed",
                $"missing or non-string '{key}'");
        return el.GetString()!;
    }
}
