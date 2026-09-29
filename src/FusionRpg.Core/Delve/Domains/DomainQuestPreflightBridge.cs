using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// D4.13's own row-8 production wiring (party-dungeon-todo.md, 2026-09-07 citation correction) — closes
/// `DomainPreflightInputs.CheckQuests` (`DomainPreflight.cs:30`), the last of the five delegated rows
/// that was still a stub (<c>_ =&gt; Array.Empty&lt;DomainRefusal&gt;()</c>, `DomainRealPipelineTests.cs`).
///
/// <para>Mirrors <see cref="DomainGraphPreflight"/>'s own established "catch the underlying throw,
/// convert to a `DomainRefusal`" bridge shape exactly — <see cref="QuestPreflight.Run"/> throws a
/// <see cref="QuestRefusal"/> (this module's own type, spec §8: "no flag, no fallback"), and this file's
/// entire job is translating that into the plain-record <see cref="DomainRefusal"/> shape
/// <see cref="DomainPreflight"/> composes rows 4-8 through. Calls <c>Run</c> with a single-domain list
/// (<c>new[] { domain }</c>), the same idiom <see cref="DomainEncounterPreflight.Build"/> already uses
/// for its own single-domain <c>EncounterDomain</c> wrap — the corpus is built ONCE by the caller
/// (every other domain's own data still lives in it), only the domain LIST passed to `Run` is narrowed
/// to one, so the sweep never even samples another domain's content while checking this one.</para>
/// </summary>
public static class DomainQuestPreflight
{
    public static Func<DomainRow, IReadOnlyList<DomainRefusal>> Build(
        QuestPreflight.QuestPreflightCorpus corpus, LayoutTemplateCatalog layouts, DungeonTuning tuning)
    {
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (layouts is null) throw new ArgumentNullException(nameof(layouts));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        return domain =>
        {
            try
            {
                QuestPreflight.Run(corpus, new[] { domain }, layouts, tuning);
                return Array.Empty<DomainRefusal>();
            }
            catch (QuestRefusal ex)
            {
                return new[] { new DomainRefusal(domain.DomainId, $"domain.quest:{ex.Rule}", ex.Message) };
            }
        };
    }
}
