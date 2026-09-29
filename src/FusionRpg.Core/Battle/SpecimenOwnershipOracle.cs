using FusionRpg.Contracts;
using FusionRpg.Core.Saves;

namespace FusionRpg.Core.Battle;

/// <summary>
/// aura-skill T21b: the real, production `IOwnSideOracle` for the SPECIMEN case —
/// `BattlefieldOwnSideReactor`'s own doc comment names this as the harder half `MechanicalOwnSideOracle`
/// (T21a) deliberately left unanswered: when a creature SPECIMEN is on the lawn, ownership is "which
/// player deployed it," not "which mechanical side is it on."
///
/// <para><b>save-identity SE4.27 (D4, "ownership by elimination"):</b> the original shape compared the
/// owner's raw player id to "my own player id" — which answered correctly only because exactly two
/// parties ever existed and the second was found by elimination ("any registered owner that is NOT this
/// player is, by elimination, Zomboss's own", `MatchHost.CheckZombossDeployTrigger`'s own comment). After
/// save-identity, a specimen's `player_id` names a SAVE, which every empire of that save shares — so
/// comparing ids can no longer tell Ally from Enemy at all, and a third empire would need a code edit to
/// add a second elimination branch. The fix asks the only question that actually decides it: <b>is the
/// registered owner's CONTROLLER human?</b> That needs no "my id", and a third empire (any controller
/// that is not <see cref="EmpireController.Human"/>) resolves as an enemy with zero code change — the D4
/// fix made executable, not just described.</para>
///
/// <para><b>The Cold-plane bridge this needs is real now, not invented here.</b>
/// `UniqueActorService.DeployAsync` already sends the specimen's ownership to the Injector in the
/// `pvz.spawn.extra` command payload (Server → Injector) — save-identity SE4.28 widens that payload to
/// carry `empireId`/`controller` beside `playerId`, and `CheatState` maps `ptr → (EmpireId,
/// EmpireController)` for the entity's life. This class is the read side: same injected-resolver pattern
/// <see cref="MechanicalOwnSideOracle"/> already established, so a test never needs a live game or a real
/// ptr cache.</para>
///
/// <para>Decision recorded in <c>docs/architecture/decisions.md</c> ("Specimen ownership bridge,
/// 2026-08-30"); widened by save-identity spec-save-identity.md D4/G5.</para>
/// </summary>
public sealed class SpecimenOwnershipOracle : IOwnSideOracle
{
    readonly Func<string, EmpireController?> _resolveController;

    /// <summary>
    /// <paramref name="resolveController"/> is a ptr → owning-empire-controller lookup
    /// (`CheatState.TryGetSpecimenController`, in production) — injected rather than a hard dependency,
    /// so a test never needs the real Injector cache. No "my id" parameter: the oracle answers an
    /// absolute question ("is the owner human?"), never a relative one.
    /// </summary>
    public SpecimenOwnershipOracle(Func<string, EmpireController?> resolveController) =>
        _resolveController = resolveController ?? throw new ArgumentNullException(nameof(resolveController));

    /// <summary>Null exactly when the resolver genuinely has no owner recorded for this ptr (not a
    /// specimen, or not yet registered) — <see cref="IOwnSideOracle.RelationOf"/>'s own contract. A
    /// registered non-human controller is always Enemy, regardless of which empire it names — the
    /// closure that makes a third empire free.</summary>
    public RelationKind? RelationOf(string ptr)
    {
        var controller = _resolveController(ptr);
        if (controller is null) return null;
        return controller == EmpireController.Human ? RelationKind.Ally : RelationKind.Enemy;
    }
}
