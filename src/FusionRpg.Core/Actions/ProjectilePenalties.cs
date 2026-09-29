namespace FusionRpg.Core.Actions;

/// <summary>
/// base-defense `siege-cover` (spec-siege-cover.md), owner decision 35: which shooting penalties this
/// action pays. Flags, not two values — HoMM3's own exemptions are not uniform (a Grand Elf ignores
/// only the range penalty; a Sharpshooter ignores all three), and two values would force every future
/// exemption into an all-or-nothing choice. Lives in `Actions` (not `Battle.Siege`) matching
/// `RequiresLineOfSight`'s own precedent — an action-compiled-shape flag, even though its motivating
/// use case is a siege.
///
/// <para><b>Default is <see cref="All"/></b> — an ordinary shot pays everything, and an exemption is
/// authored content, never an oversight. This is why every one of the five plumbing sites below
/// defaults it to `All`, not `None`.</para>
/// </summary>
[Flags]
public enum ProjectilePenalties
{
    None = 0,
    Range = 1 << 0,
    Obstruction = 1 << 1,
    MeleeLock = 1 << 2,
    All = Range | Obstruction | MeleeLock
}
