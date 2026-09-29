namespace FusionRpg.Core.Actions.Ai;

/// <summary>Host-supplied: which class an actor belongs to. <c>null</c> (the default everywhere this
/// is optional) means every actor resolves <see cref="AiActorClass.Unique"/> — see
/// <see cref="AiTierResolver"/>'s own doc comment for why that direction, not the other, is the safe
/// default.</summary>
public delegate AiActorClass AiActorClassOf(string actorKey);

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md §1): D6's class
/// split as the ONLY place a tier is decided. A profile that names a <c>TierOverride</c> wins;
/// otherwise the actor's class decides through the profile's own <c>TierByActorClass</c> map
/// (module 2's schema, total over <see cref="AiActorClass"/> by construction — the loader rejects an
/// incomplete map at parse).
///
/// <para><b>Never a difficulty input (D2).</b> The signature is the enforcement: this method takes a
/// profile and a class, nothing else — no danger band, no wave number, no side, no player rank. "In
/// this game we don't make difficulty by AI" is not a comment here; it is what the parameter list
/// cannot express.</para>
/// </summary>
public static class AiTierResolver
{
    public static AiTier For(CombatAiProfile profile, AiActorClass actorClass) =>
        profile.TierOverride ?? profile.TierByActorClass[actorClass];
}
