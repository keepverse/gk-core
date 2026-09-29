using FusionRpg.Core.Items;

namespace FusionRpg.Core.Balance.Guards;

/// <summary>One role's channel-split finding — named, never a bare boolean (spec-base-types.md:
/// "the lint reports the role id, not a boolean").</summary>
public sealed record FrameDominanceFinding(string RoleId, string Reason);

public sealed record FrameDominanceReport(IReadOnlyList<FrameDominanceFinding> Findings)
{
    public bool IsGreen => Findings.Count == 0;
}

/// <summary>
/// D11's dominance lint, `channel-split` MODE (spec-base-types.md "The dominance lint"). This is the
/// WHOLE of `base-types`' (module 6) own obligation — the mechanically stronger `corner-matrix` mode
/// (does a real corner exist where each frame wins?) needs module 9's power vector and is owed there
/// as a separate, failing-by-default fixture.
///
/// <para>Channel-split asks a cheaper, honest question that is still real: do the two frames' base
/// profiles actually differ, does neither dominate the other on every shared channel, and does clause
/// 3's one-lean-per-frame correlation hold across the whole hybrid core? A role that fails any of
/// these is a named content defect, not a matter of taste.</para>
/// </summary>
public static class FrameDominanceGuard
{
    public static FrameDominanceReport RunChannelSplit(FrameLeanTable leans, IReadOnlyList<string> hybridCoreRoleIds)
    {
        var findings = new List<FrameDominanceFinding>();

        // Clause 3 first: a broken correlation repeals D3 for every role at once, so it is checked
        // once, HARD, rather than once per role.
        var ladders = hybridCoreRoleIds
            .Select(r => BaseTypeSlate.TryLadderOf(r, out var l) ? l : (ClassLadder?)null)
            .Where(l => l is not null && l != ClassLadder.Standard)
            .Select(l => l!.Value)
            .Distinct()
            .ToList();
        if (!leans.CorrelationHolds(ladders))
            findings.Add(new FrameDominanceFinding("*", "clause 3: the frame lean is not one fixed axis per frame across every hybrid-core role's ladder"));

        foreach (var roleId in hybridCoreRoleIds)
        {
            if (!BaseTypeSlate.TryLadderOf(roleId, out var ladder) || ladder == ClassLadder.Standard)
                continue; // standard carries no lean, D14 -- not a lint target

            var h = leans.Of(ladder, ItemFrame.Humanoid);
            var p = leans.Of(ladder, ItemFrame.Plant);
            if (h is null || p is null)
            {
                findings.Add(new FrameDominanceFinding(roleId, $"ladder '{ladder}' has no authored lean for one or both frames"));
                continue;
            }

            var hProfile = h.Value;
            var pProfile = p.Value;

            var sameKeys = hProfile.BaseSplitPermille.Count == pProfile.BaseSplitPermille.Count
                && hProfile.BaseSplitPermille.All(kv => pProfile.BaseSplitPermille.TryGetValue(kv.Key, out var v) && v == kv.Value);
            if (sameKeys)
            {
                findings.Add(new FrameDominanceFinding(roleId, "the two frames' baseSplitPermille blocks are identical -- no directional profile"));
                continue;
            }

            if (!FrameLeanTable.NeitherIsASuperset(hProfile, pProfile))
                findings.Add(new FrameDominanceFinding(roleId, "one frame's profile is a superset of the other's -- dominance wearing difference's clothes"));
        }

        return new FrameDominanceReport(findings);
    }
}
