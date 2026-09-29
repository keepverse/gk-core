namespace FusionRpg.Core.Items;

/// <summary>Which body a role's name is drawn from — never a faction. <c>Hybrid</c> is not listed
/// here: a hybrid item is drawn from whichever of the two pure ladders the base type's own frame
/// names (item-ideal.md §D3) — it does not mint a third vocabulary.</summary>
public enum ItemFrame { Humanoid, Plant }

/// <summary>
/// One role table, two frame vocabularies (item-ideal.md, `slot-roles` §2.2) — so the affix library
/// is authored once and every frame just names the same slot in its own fiction. Reads names off
/// the already-parsed <see cref="ItemRoleDef"/> list; it owns no data of its own.
/// </summary>
public static class FrameVocabulary
{
    public static string NameOf(ItemRoleDef def, ItemFrame frame) => frame switch
    {
        ItemFrame.Humanoid => def.HumanoidName,
        ItemFrame.Plant => def.PlantName,
        _ => throw new ArgumentOutOfRangeException(nameof(frame)),
    };

    public static ItemRoleDef Find(IReadOnlyList<ItemRoleDef> all, ItemRole role) =>
        all.FirstOrDefault(r => r.Role == role)
            ?? throw new KeyNotFoundException($"role '{ItemRoles.Id(role)}' is not in this registry");
}
