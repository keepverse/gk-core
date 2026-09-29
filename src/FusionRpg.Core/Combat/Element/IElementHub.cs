using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Element;

public interface IElementHub
{
    double ResolveComponentBonus(
        ElementTypeId attackerElement,
        ActorElementTypes defenderTypes,
        double baseOverlayDamage);

    double ResolvePayloadBonus(
        IReadOnlyList<ElementPayloadComponent> components,
        ActorElementTypes defenderTypes,
        double baseOverlayDamage);
}
