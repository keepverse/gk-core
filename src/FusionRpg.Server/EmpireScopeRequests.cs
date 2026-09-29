using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// `save-identity` SE4.31 — the one place an optional <c>?empire=&lt;empireId&gt;</c> query value becomes
/// an <see cref="EmpireRef"/>, and the one place a route whose store has not widened its key refuses a
/// non-human empire (spec-save-identity.md §Contracts).
///
/// <para><b>Why a seam and not one line per route.</b> Three shapes must agree on every empire-taking
/// route: what "absent" means (the save's human empire — so every current caller is unchanged), what an
/// empire id that is not this save's means (404, never another save's rows), and what a Tier B route
/// must do when asked for a non-human empire (409 <c>empire_scope_not_widened</c>, because the store's
/// key has not widened and answering from the human's row would be the substitution defect this module
/// exists to remove). Declared once, a later batch widens the key in the store and only the tier flag
/// moves.</para>
/// </summary>
public static class EmpireScopeRequests
{
    /// <summary>The default owner of an empire-scoped read: the save's human empire, loud when the save
    /// has none (<see cref="SaveEmpiresNotSeeded"/>), never guessed as a particular faction.</summary>
    public static EmpireRef HumanOwnerOf(RpgStore store, long saveId) =>
        new(new SaveId(saveId), store.HumanEmpireOf(saveId));

    /// <summary>
    /// Resolve <c>?empire=</c> for a <b>Tier A</b> route — one whose store is keyed
    /// <c>(save_id, empire_id)</c> and therefore serves <b>every</b> empire of the save. Absent → the
    /// human empire. Named but not an empire of this save → <paramref name="refusal"/> is a 404
    /// <c>empire_not_found</c>.
    /// </summary>
    public static bool TryResolveAny(
        RpgStore store, long saveId, string? empire, out EmpireRef owner, out IResult? refusal)
    {
        if (string.IsNullOrWhiteSpace(empire))
        {
            owner = HumanOwnerOf(store, saveId);
            refusal = null;
            return true;
        }

        if (!TryMatch(store, saveId, empire, out var match))
        {
            owner = default;
            refusal = EmpireNotFound(empire);
            return false;
        }

        owner = new EmpireRef(new SaveId(saveId), match!.Empire);
        refusal = null;
        return true;
    }

    /// <summary>
    /// The <b>Tier B</b> refusal: a store whose key has not widened owns only the save's human empire.
    /// Absent, or the human empire named explicitly → <c>null</c> (nothing to refuse). A non-human empire
    /// → 409 <c>empire_scope_not_widened</c>. An id that is not this save's empire at all → 404.
    /// <para>The refusal happens <b>before</b> the store call, so a Tier B route never reaches SQL with
    /// an empire it cannot serve.</para>
    /// </summary>
    public static IResult? RefuseNonHumanEmpire(RpgStore store, long saveId, string? empire)
    {
        if (string.IsNullOrWhiteSpace(empire)) return null;   // absent = the human empire

        if (!TryMatch(store, saveId, empire, out _)) return EmpireNotFound(empire);

        var requested = empire.Trim();
        if (string.Equals(requested, store.HumanEmpireOf(saveId).Value, StringComparison.Ordinal)) return null;

        return Results.Conflict(new { error = "empire_scope_not_widened", empire = requested });
    }

    static bool TryMatch(RpgStore store, long saveId, string empire, out SaveEmpire? match)
    {
        var requested = empire.Trim();
        match = store.EmpiresOf(saveId)
            .FirstOrDefault(e => string.Equals(e.Empire.Value, requested, StringComparison.Ordinal));
        return match is not null;
    }

    static IResult EmpireNotFound(string empire) =>
        Results.NotFound(new { error = "empire_not_found", empire = empire.Trim() });
}
