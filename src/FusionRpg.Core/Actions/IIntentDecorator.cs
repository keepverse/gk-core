namespace FusionRpg.Core.Actions;

/// <summary>
/// combat-ai `intent-router` (module 4, CAI1.10, spec-intent-router.md §2): a trait that changes WHAT
/// A POLICY SEES or WHERE ITS ANSWER LANDS. The implementations stay engine-side
/// (`decisions.md:44` — "bloodthirsty/loyal stay BattleEngine-side wrapping, never reimplemented in
/// the action program"); <see cref="IntentRouter"/> owns only the ORDER and the COVERAGE.
///
/// <para><b>No implementation is wired to a real trait in this commit.</b> `IntentRouter`'s own
/// `decorators` parameter accepts a list of these and defaults to <c>null</c> (no decoration, byte-
/// identical) — `bloodthirsty`/`loyal` becoming real `ITraitDecorator`s that actually reach every
/// policy (not only the stub) is `intent-router`'s own "cause B" (CAI1.11), a real, siege-visible
/// behaviour change kept out of this byte-identical commit per the map's one-cause-per-commit rule.
/// </para>
/// </summary>
public interface ITraitDecorator
{
    /// <summary>Whether this trait is active for <paramref name="actorKey"/> this decision. A false
    /// answer means neither method below is called for this actor this decision.</summary>
    bool AppliesTo(string actorKey);

    /// <summary>Pre-decision: the view a policy sees while deciding — `bloodthirsty`'s own shape
    /// (`BasicAttack.cs`'s `BloodthirstyView`, unchanged, reused).</summary>
    IBattleView Decorate(string actorKey, IBattleView inner);

    /// <summary>Post-decision: where the chosen target actually lands — `loyal`'s own shape (the
    /// bodyguard redirect). Applied exactly once on each side (module 1's scorer input, the engine's
    /// own resolve site); never chains a redirect of a redirect.</summary>
    string EffectiveTargetOf(string actorKey, string targetKey);
}
