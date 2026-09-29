using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures;

/// <summary>
/// The `(Side, GameTypeId) -> CreatureSpeciesDef` lookup a lawn actor needs to find its element
/// (spec-lawn-element-bind.md §2.4, E27). Battle already knows its element from
/// <c>BattleActorSetup</c>; the lawn only ever has a Unity pointer, a side and a PvZ type id, so this
/// is the missing link back to the species row that carries <c>ElementPrimary</c>/<c>ElementSecondary</c>.
///
/// <para><b>Why `(Side, GameTypeId)` and not `GameTypeId` alone.</b> `GameTypeId` is not unique across
/// sides — `polevaulterzombie` and `wallnut` are both `3` in the shipped roster — so a type-id-only key
/// would silently hand a plant a zombie's element.</para>
///
/// <para><b>The roster is store-backed and can change</b> (`CreatureSpeciesCatalog.All` reads whatever
/// `species-import` last wrote), and <c>Validate</c> enforces unique `SpeciesId`/`CreatureTypeId` but not
/// unique `(Side, GameTypeId)`. So a collision is not impossible — it is handled deterministically
/// (lowest `SpeciesId` by ordinal wins) and reported once per build, rather than picked arbitrarily by
/// dictionary insertion order.</para>
/// </summary>
public sealed class LawnElementIndex
{
    readonly Dictionary<(string Side, int GameTypeId), CreatureSpeciesDef> _byKey;

    public LawnElementIndex(IEnumerable<CreatureSpeciesDef> species)
    {
        if (species is null) throw new ArgumentNullException(nameof(species));

        var collisions = new List<string>();
        var byKey = new Dictionary<(string, int), CreatureSpeciesDef>();

        foreach (var s in species.OrderBy(s => s.SpeciesId, StringComparer.Ordinal))
        {
            var key = (s.Side, s.GameTypeId);
            if (byKey.TryGetValue(key, out var existing))
            {
                // Ordered by SpeciesId ascending above, so `existing` already holds the lowest —
                // the incoming `s` is always the one dropped. Deterministic beats arbitrary.
                collisions.Add(
                    $"({s.Side}, {s.GameTypeId}): kept '{existing.SpeciesId}', dropped '{s.SpeciesId}'");
                continue;
            }

            byKey[key] = s;
        }

        _byKey = byKey;
        Collisions = collisions;
    }

    /// <summary>One line per dropped duplicate, built once at construction — never during a resolve.</summary>
    public IReadOnlyList<string> Collisions { get; }

    public bool TryGet(string side, int gameTypeId, out CreatureSpeciesDef species) =>
        _byKey.TryGetValue((side, gameTypeId), out species!);
}
