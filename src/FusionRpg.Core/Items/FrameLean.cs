using System.Text.Json;

namespace FusionRpg.Core.Items;

/// <summary>Class ladders `base-types` (module 6) authors a lean for. `Standard` is declared but
/// carries no lean (D14 — commander gear out of scope for v1).</summary>
public enum ClassLadder { Armour, Weapon, Offhand, Jewel, Standard }

/// <summary>One (ladder, frame) block's directional stat profile — D11 clause 2.</summary>
public readonly record struct FrameLeanProfile(
    IReadOnlyDictionary<string, int> BaseSplitPermille, string ImplicitAxis);

public sealed class FrameLeanRejection : Exception
{
    public FrameLeanRejection(string message) : base(message) { }
}

/// <summary>
/// D11 clauses 2 and 3 (spec-base-types.md "Where the direction lives"): one lean per
/// (<see cref="ClassLadder"/>, frame) — never per role — so clause 3 (correlation across every
/// hybrid-core role) holds by construction rather than by a check that could be defeated by
/// relocating the field. Pure parser over `frame-lean.v1.json`; no file I/O (tunables-ssot.md §7.2).
/// </summary>
public static class FrameLean
{
    public static FrameLeanTable Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FrameLeanRejection("frame-lean: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new FrameLeanRejection($"frame-lean: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("leans", out var leansEl) || leansEl.ValueKind != JsonValueKind.Object)
                throw new FrameLeanRejection("frame-lean: missing or non-object 'leans'");

            var profiles = new Dictionary<(ClassLadder, ItemFrame), FrameLeanProfile>();
            foreach (var ladder in Enum.GetValues<ClassLadder>())
            {
                var ladderKey = LadderKey(ladder);
                if (!leansEl.TryGetProperty(ladderKey, out var ladderEl) || ladderEl.ValueKind != JsonValueKind.Object)
                    throw new FrameLeanRejection($"frame-lean: missing ladder '{ladderKey}'");

                foreach (var frame in Enum.GetValues<ItemFrame>())
                {
                    var frameKey = FrameKey(frame);
                    if (!ladderEl.TryGetProperty(frameKey, out var frameEl))
                        throw new FrameLeanRejection($"frame-lean: '{ladderKey}.{frameKey}' missing");

                    if (frameEl.ValueKind == JsonValueKind.Null)
                    {
                        if (ladder != ClassLadder.Standard)
                            throw new FrameLeanRejection(
                                $"frame-lean: '{ladderKey}.{frameKey}' is null — only 'standard' may declare an empty lean (D14)");
                        continue;
                    }

                    if (!frameEl.TryGetProperty("baseSplitPermille", out var splitEl) || splitEl.ValueKind != JsonValueKind.Object)
                        throw new FrameLeanRejection($"frame-lean: '{ladderKey}.{frameKey}.baseSplitPermille' missing or non-object");
                    var split = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var ch in splitEl.EnumerateObject())
                    {
                        if (ch.Value.ValueKind != JsonValueKind.Number || !ch.Value.TryGetInt32(out var v))
                            throw new FrameLeanRejection($"frame-lean: '{ladderKey}.{frameKey}.baseSplitPermille.{ch.Name}' is not an integer");
                        split[ch.Name] = v;
                    }

                    if (!frameEl.TryGetProperty("implicitAxis", out var axisEl) || axisEl.ValueKind != JsonValueKind.String)
                        throw new FrameLeanRejection($"frame-lean: '{ladderKey}.{frameKey}.implicitAxis' missing");
                    var axis = axisEl.GetString()!;

                    profiles[(ladder, frame)] = new FrameLeanProfile(split, axis);
                }
            }

            return new FrameLeanTable(profiles);
        }
    }

    internal static string LadderKey(ClassLadder l) => l switch
    {
        ClassLadder.Armour => "armour",
        ClassLadder.Weapon => "weapon",
        ClassLadder.Offhand => "offhand",
        ClassLadder.Jewel => "jewel",
        ClassLadder.Standard => "standard",
        _ => throw new ArgumentOutOfRangeException(nameof(l)),
    };

    internal static string FrameKey(ItemFrame f) => f switch
    {
        ItemFrame.Humanoid => "humanoid",
        ItemFrame.Plant => "plant",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };
}

/// <summary>
/// The parsed table. <see cref="Of"/> returns null only for the declared-empty `standard` pair.
/// <see cref="CorrelationHolds"/> is D11 clause 3, HARD: one axis per frame across every hybrid-core
/// role, checked by asking each role's ladder for its frame's axis and requiring them all to agree.
/// </summary>
public sealed class FrameLeanTable
{
    readonly IReadOnlyDictionary<(ClassLadder, ItemFrame), FrameLeanProfile> _profiles;

    internal FrameLeanTable(IReadOnlyDictionary<(ClassLadder, ItemFrame), FrameLeanProfile> profiles) =>
        _profiles = profiles;

    public FrameLeanProfile? Of(ClassLadder ladder, ItemFrame frame) =>
        _profiles.TryGetValue((ladder, frame), out var p) ? p : null;

    public int AuthoredLeanCount => _profiles.Count;

    /// <summary>
    /// Clause 3, structural by construction (the lean lives per (ladder, frame), never per role) —
    /// this asserts it anyway, as a test rather than only a comment (spec-base-types.md's own demand:
    /// "a per-role lean table is rejected at load").
    /// </summary>
    public bool CorrelationHolds(IEnumerable<ClassLadder> ladders)
    {
        string? humanoidAxis = null, plantAxis = null;
        foreach (var ladder in ladders)
        {
            if (ladder == ClassLadder.Standard) continue;
            var h = Of(ladder, ItemFrame.Humanoid);
            var p = Of(ladder, ItemFrame.Plant);
            if (h is null || p is null) return false;

            humanoidAxis ??= h.Value.ImplicitAxis;
            plantAxis ??= p.Value.ImplicitAxis;
            if (h.Value.ImplicitAxis != humanoidAxis) return false;
            if (p.Value.ImplicitAxis != plantAxis) return false;
        }

        return humanoidAxis is not null && humanoidAxis != plantAxis;
    }

    /// <summary>Neither frame's profile may be a superset (strictly ≥ on every shared channel) of
    /// the other's — dominance wearing difference's clothes.</summary>
    public static bool NeitherIsASuperset(FrameLeanProfile a, FrameLeanProfile b)
    {
        var keys = a.BaseSplitPermille.Keys.Union(b.BaseSplitPermille.Keys, StringComparer.Ordinal);
        bool aWinsAny = false, bWinsAny = false;
        foreach (var k in keys)
        {
            var av = a.BaseSplitPermille.GetValueOrDefault(k);
            var bv = b.BaseSplitPermille.GetValueOrDefault(k);
            if (av > bv) aWinsAny = true;
            if (bv > av) bWinsAny = true;
        }

        return aWinsAny && bWinsAny;
    }
}
