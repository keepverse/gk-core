using System.Text.Json;

namespace FusionRpg.Core.Actions.Unlock;

public sealed class UnlockTuningRejection : Exception
{
    public UnlockTuningRejection(string message) : base(message) { }
}

/// <summary>
/// The four ratchet dials (spec-unlock-ladder.md §1) plus T20's discard-tax coefficient.
///
/// <para><b>Owner decision 2026-08-28 reverses spec-unlock-ladder.md §3's "flat tax" call:</b> the
/// spec's own text says a flat tax was chosen because "a rung-scaled tax was considered and
/// retracted — the farm it priced against does not exist." The owner overrode that live: discard's
/// soul cost scales with the ACTOR'S power (`Θ`), read through the same `P(Θ)` SSOT every other
/// level-derived magnitude uses (PS-3) — <see cref="DiscardTaxCoeffMilli"/> is a per-mille
/// coefficient of `P(Θ)`, never a private `f(level)`. The coefficient itself is an explicit
/// placeholder ("pick 0.01, 0.1, or any number, rebalance later") — the rule is decided, the number
/// is not.</para>
///
/// <para><b>A-U1 (spec-rung-semantics.md §3.3, 2026-09-03): <c>Cap</c> split into
/// <see cref="HeldCap"/> and <see cref="RungCap"/>.</b> One field served two unrelated meanings —
/// <c>action-unlock.v1.json</c>'s own `_meta` said so verbatim ("cap 10 is both the max held count and
/// the rung ceiling — one number, two uses"). Splitting them at the SAME starting value is
/// behaviour-neutral by construction (nothing reads differently today) and is what makes raising the
/// rung ceiling alone — without also handing every player more held unlocks — possible later.</para>
/// </summary>
public sealed record UnlockTuning(int P1Milli, int DeltaMilli, int FloorMilli, int HeldCap, int RungCap, int DiscardTaxCoeffMilli);

/// <summary>Pure parser for `data/tuning/action-unlock.v{n}.json` (tunables-ssot.md §7.2 — no file I/O
/// here). `floorMilli = 0` is rejected here specifically: a zero floor lets the ratchet decay the
/// chance to a mathematical zero, which is a hard progression ceiling PS-8 forbids.</summary>
public static class UnlockTuningLoader
{
    public static UnlockTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new UnlockTuningRejection("action unlock tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new UnlockTuningRejection($"action unlock tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var p1 = Int(root, "p1Milli");
            var delta = Int(root, "deltaMilli");
            var floor = Int(root, "floorMilli");
            var heldCap = Int(root, "heldCap");
            var rungCap = Int(root, "rungCap");
            var discardTaxCoeff = Int(root, "discardTaxCoeffMilli");

            if (p1 <= 0 || p1 > 1000)
                throw new UnlockTuningRejection($"action unlock tuning: p1Milli {p1} must be in (0, 1000]");
            if (delta <= 0 || delta >= 1000)
                throw new UnlockTuningRejection($"action unlock tuning: deltaMilli {delta} must be in (0, 1000) — a ratchet that does not decay is not a ratchet");
            if (floor <= 0)
                throw new UnlockTuningRejection(
                    "action unlock tuning: floorMilli must be > 0 (PS-8 — a zero floor lets the chance decay " +
                    "to a mathematical zero, which is a hard progression ceiling wearing a different hat)");
            if (floor > p1)
                throw new UnlockTuningRejection($"action unlock tuning: floorMilli {floor} exceeds p1Milli {p1}");
            if (heldCap < 1)
                throw new UnlockTuningRejection($"action unlock tuning: heldCap {heldCap} must be >= 1");
            if (rungCap < 1)
                throw new UnlockTuningRejection($"action unlock tuning: rungCap {rungCap} must be >= 1");
            if (discardTaxCoeff <= 0)
                throw new UnlockTuningRejection("action unlock tuning: discardTaxCoeffMilli must be > 0 — a free discard makes the ratchet's own retraction (never rewinds) meaningless as a brake");

            return new UnlockTuning(p1, delta, floor, heldCap, rungCap, discardTaxCoeff);
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new UnlockTuningRejection($"action unlock tuning: missing or non-integer '{key}'");
        return v;
    }
}
