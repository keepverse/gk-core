using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Derived;

public interface IActorStatSubsystem
{
    string SubsystemId { get; }
    int Order { get; }
    void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods);
}
