using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// T33 (spec-action-selection.md §4): the read seam fog of war will swap — "`IBattleView` from day
/// one, even while it returns everything." Every board/roster fact <see cref="StubIntentSource"/>
/// needs comes through here, never through a direct read of <c>BattleEngine</c>'s own actor list —
/// under fog, "nearest target" becomes "nearest KNOWN target", which is a change to every read the AI
/// makes, and this interface is what confines that change to one implementation later.
///
/// <para>Cost/cooldown/stance seams (<see cref="CooldownLedger"/>, <see cref="IStanceCheck"/>,
/// <see cref="IAffordabilityCheck"/>) are NOT part of this view — those are the shared services
/// <see cref="UsabilityEvaluator.Evaluate"/> already takes as their own parameters, and fog never
/// touches them; only board/roster visibility does.</para>
/// </summary>
public interface IBattleView
{
    /// <summary>Every currently-live actor's ptr, in the view's own listed order — the order
    /// <see cref="StubIntentSource"/> falls back to when no board exists (spec §6: "with coordinates
    /// absent, nearest is undefined... falls back to `SourceOrder`").</summary>
    IReadOnlyList<string> LiveActorKeys { get; }

    /// <summary>
    /// combat-ai `intent-router` (module 4, CAI1.11, spec-intent-router.md §2): the SAME live set as
    /// <see cref="LiveActorKeys"/>, in the order THIS deciding actor's view presents it. The default is
    /// the identity (<see cref="LiveActorKeys"/>) so every existing implementer and every test fake is
    /// byte-identical; only a view that is itself a TRAIT decorator (see <see cref="TraitAwareBattleView"/>)
    /// overrides it.
    ///
    /// <para><b>Why this member exists at all.</b> `bloodthirsty`'s whole effect is a reordering of
    /// `LiveActorKeys` (`BasicAttack.BloodthirstyView`), and which reordering is correct depends on
    /// WHICH actor is asking — "move the lowest-HP ENEMY to the front" is a different list for a
    /// plant than for a zombie. `LiveActorKeys` is the one `IBattleView` member with no `actorKey`, so a
    /// policy that binds its view once at construction (`SiegeAiIntentSource` owns a persistent
    /// `RetargetLedger`/`BattleTrace` and is not rebuilt per decision) can reach the trait's own
    /// per-actor decoration ONLY through a viewer-relative read. This is that read; it carries no trait
    /// vocabulary, exactly like <see cref="SideOf"/>.</para>
    /// </summary>
    IReadOnlyList<string> LiveActorKeysFor(string actorKey) => LiveActorKeys;

    /// <summary>0 plant / 1 zombie / 2 bullet — the same <see cref="EntityFacts.Side"/> vocabulary,
    /// so "enemy" is <c>SideOf(other) != SideOf(self)</c> with no third comparison invented.</summary>
    int SideOf(string actorKey);

    /// <summary><c>null</c> when no board exists yet — the SAME sentinel
    /// <see cref="UsabilityEvaluator"/>'s own <c>casterPos</c>/<c>targetPos</c> already use, so range
    /// gates and "nearest" share one absence convention.</summary>
    GridPos? PositionOf(string actorKey);

    /// <summary>The narrow fact window gate 5's compiled predicate evaluates against.</summary>
    EntityFacts FactsOf(string actorKey);

    /// <summary>Every action this actor currently holds, already compiled (spec §5's own hot-loop
    /// discipline: "nothing parses JSON during battle" applies to the AI's reads too) — gate 1's
    /// "bound" set.</summary>
    IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey);

    /// <summary>
    /// base-defense `siege-ai` (spec-siege-ai.md, R1/R3): the full derived-stat snapshot behind an
    /// actor's Omni combat channels — <see cref="EntityFacts"/> alone (Side/TypeId/HpMilli/ElementId/
    /// Row/Col/IsMindControlled/IsKiller/StatusMask) has no accuracy/dodge/power/defense reader, so a
    /// live scoring AI (<c>SiegeAiIntentSource</c>) cannot estimate a real hit chance through
    /// `FactsOf` alone. `null` when unknown/hidden — the SAME absence convention <see cref="PositionOf"/>
    /// already uses, so fog of war gates this exactly like every other per-actor read (a real, live
    /// implementor need only forward its own already-held `Derived` snapshot; <c>FoggedBattleView</c>
    /// returns `null` for anything outside the viewer's own visibility, matching its own `FactsOf`).
    /// </summary>
    FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot? DerivedOf(string actorKey);

    /// <summary>
    /// base-defense `siege-ai` 17.9 (spec-siege-ai.md §5.20 rule 5): the one bit `combatant-kind`'s own
    /// `CombatantKind` discriminator does not itself expose through this seam — whether `actorKey` is
    /// CURRENTLY garrisoning a `CombatantKind.Structure` emplacement (`BattleActorSetup.GarrisonedBy`
    /// naming it). Returns the structure's own key, or `null` when not garrisoning anything — the SAME
    /// absence convention <see cref="PositionOf"/> already uses. This is self-knowledge, never gated by
    /// fog the way <see cref="DerivedOf"/> gates OTHER actors: an actor always knows its own posting.
    /// </summary>
    string? GarrisonedStructureKeyOf(string actorKey);

    /// <summary>
    /// base-defense `siege-ai` R3 (spec-siege-ai.md §4): the cell `actorKey` should advance toward when
    /// no target is in reach — the Core's own centre for an attacker, the attacker's own entry gate for
    /// a defender (<see cref="FusionRpg.Core.World.District.DistrictLayout.ObjectivePositionFor"/>).
    /// `null` when no siege context is wired for this battle (every non-siege battle, and every siege
    /// battle before `DistrictAssaultResolver` sets an attacker edge) — the SAME absence convention
    /// <see cref="PositionOf"/> already uses. Self-knowledge, never gated by fog the way
    /// <see cref="DerivedOf"/> gates OTHER actors: which side you attack from is common knowledge to
    /// your own side, the same reasoning <see cref="GarrisonedStructureKeyOf"/> already established.
    /// </summary>
    GridPos? ObjectivePositionOf(string actorKey);

    /// <summary>
    /// base-defense `siege-ai` (spec-siege-ai.md, `IsKillingBlow`, 2026-09-07): the target's own real
    /// `MaxHp`, needed to convert <see cref="FactsOf"/>'s per-mille `HpMilli` back to a raw HP a real
    /// damage estimate can be compared against — <see cref="EntityFacts"/> alone has no HP scale
    /// reference at all. `null` when unknown/hidden, the SAME absence convention <see cref="PositionOf"/>
    /// already uses.
    /// </summary>
    long? MaxHpOf(string actorKey);

    /// <summary>
    /// base-defense `siege-ai` §5.20 rule 4 (spec-siege-ai.md §10): the signed −2..+2 taunt/stealth/decoy
    /// scalar applied INSIDE `AiScoring.EffectiveTier`, never as a score bonus — the exact accessor
    /// signature that section's own snippet names (`AggressionOf(actorKey)`). Unlike every other member
    /// here, this one is never "unknown" — 0 (neutral, no taunt/stealth active) is always a correct,
    /// meaningful answer, the same non-nullable-default shape <c>Stance</c> already has, not the
    /// might-not-exist absence convention <see cref="PositionOf"/> established. A real implementor with
    /// no taunt/stealth content to read returns 0 for every actor, matching `AiCandidate`'s own
    /// previously-hardcoded default byte-for-byte — this is a wiring seam for content that does not
    /// exist yet (matching <see cref="GarrisonedStructureKeyOf"/>'s/`EmplacementFireMode`'s own
    /// "vocabulary before its second value has a real consumer" precedent), not a promise of a
    /// non-default value today.
    /// </summary>
    /// <summary>
    /// combat-ai `delve-automated-wiring` (CAI3.4, spec-delve-automated-wiring.md §5): this actor's own
    /// side's DOWNED members — present, targetable and revivable under a `DownedOnDeplete` profile, and
    /// deliberately ABSENT from <see cref="LiveActorKeys"/>, which lists only `Active` actors
    /// (`BattleRunState.cs`, `a.Active`). Under the delve profile an actor at HP &lt;= 0 with a
    /// `PartyIndex` transitions to `TurnState.Downed` and is not cleaned up, so without this read the
    /// `ally-downed` selector is inert.
    ///
    /// <para><b>Empty for every profile without `DownedOnDeplete` — which is every profile but `delve` —
    /// so this is byte-identical to today everywhere else.</b> Self-side only: who on YOUR side is down is
    /// not fog-gated, the same reasoning <see cref="GarrisonedStructureKeyOf"/> and
    /// <see cref="ObjectivePositionOf"/> already established on this interface.</para>
    ///
    /// <para><b>Default implementation, deliberate.</b> The spec expected every implementor to add the
    /// member; a default returning empty is strictly safer and is exactly the byte-identity answer above —
    /// it keeps twelve implementors (two production wrappers, one nested view and eight test fakes)
    /// compiling without nine `=> Array.Empty&lt;string&gt;()` copies that assert nothing, while the ONE
    /// view that actually has the state, <c>BattleRunState</c>, overrides it and the two wrappers forward
    /// it. A view created before this member existed therefore answers "nobody is down", which is what it
    /// would have had to answer anyway.</para>
    /// </summary>
    IReadOnlyList<string> DownedAllyKeysOf(string actorKey) => Array.Empty<string>();

    int AggressionOf(string actorKey);
}
