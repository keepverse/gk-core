namespace FusionRpg.Core.Actions;

/// <summary>
/// T10 (spec-usability-conditions.md's `holdsStock` mode matrix): where an action is being bound
/// determines whether a `holdsStock` precondition can ever be answered.
///
/// | Mode | Stock source | Wave 1 |
/// |---|---|---|
/// | <see cref="Battle"/> | server-authoritative, resolved at action-set assembly | supported |
/// | <see cref="Lawn"/> | the overlay is a stateless observer, never reads current game state | NOT bindable |
///
/// A closed, two-member enum on purpose — "an unsupported mode named is fine; an unstated one is
/// the `resource.delta` defect again." Adding a third mode is a reviewed change to
/// <see cref="ActionCompiler"/>'s own mode switch, not a silent default.
/// </summary>
public enum ActionBindMode
{
    Battle = 0,
    Lawn,
}
