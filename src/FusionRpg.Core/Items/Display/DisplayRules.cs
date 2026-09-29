using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Display;

/// <summary>
/// The display lane's content-rule namespace — spec-item-card.md's four new reason codes, resolved as
/// <c>ContentRuleViolated{display.*}</c> rather than as four new members of
/// <see cref="AtomRejectionReason"/>'s closed list.
///
/// <para>⛔ <b>Why namespaced and not minted.</b> <see cref="AtomRejectionReason.ContentRuleViolated"/>
/// is declared the last member by design — "a caller that wants a new rule registers a namespace, it
/// never mints another code" — and item-ideal.md §2b.1 says the same. Every prior item module took
/// the same route (<c>SocketRules</c>, <c>ItemGrantRules</c>); this is the display lane's copy of that
/// pattern, and the spec's own "adding a reason code is a reviewed change to definitions.md §10's
/// closed 33-code list" warning is what it avoids.</para>
///
/// <para>The spec offers to collapse <see cref="MissingDisplayTemplate"/> and
/// <see cref="MissingDisplayKey"/> into one code if the owner wants fewer. They are kept apart here
/// because the FIX differs — author a template row versus author a string — and both are shipped as
/// separate rule ids, which is reversible in one line if the owner disagrees. The spec is explicit
/// that <see cref="MissingUnitClass"/> and <see cref="UnrenderedMagnitude"/> "must not collapse into
/// anything", and they do not.</para>
/// </summary>
public static class DisplayRules
{
    public const string Namespace = "display";

    /// <summary>A channel with a reader and no declared unit. "The failure this lane exists for":
    /// <c>+10 fire power</c> and <c>+10 crit rate</c> become indistinguishable without it.</summary>
    public const string MissingUnitClass = "display.missing-unit-class";

    /// <summary>A present family with no words. Distinct from <c>UnknownAtom</c>, which is about an
    /// ABSENT atom — this one is a real, rollable family that would render as a raw id.</summary>
    public const string MissingDisplayTemplate = "display.missing-display-template";

    /// <summary>The template row exists, the string does not.</summary>
    public const string MissingDisplayKey = "display.missing-display-key";

    /// <summary>A number the engine applies and the player never sees — the <c>status.expose</c>
    /// defect in mirror image.</summary>
    public const string UnrenderedMagnitude = "display.unrendered-magnitude";

    static DisplayRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Force the static registration — the idiom every other item lane uses.</summary>
    public static void EnsureRegistered() => System.Runtime.CompilerServices.RuntimeHelpers
        .RunClassConstructor(typeof(DisplayRules).TypeHandle);

    public static AtomRejection Violated(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// One authored affix family, reduced to exactly what the four display rules read. Deliberately NOT
/// <c>FamilyEntryInput</c>: that record is <c>FamilyExpansion</c>'s input and carries a power band and
/// a source file this check has no business reading, and it drops <c>declaresMagnitude</c>, which is
/// the one fact <see cref="DisplayRules.UnrenderedMagnitude"/> needs.
/// </summary>
/// <param name="Channel">Raw <c>params.channel</c> as authored — may be an element template or a bare
/// family stem, which is why the check reads it through
/// <see cref="ChannelUnits.ForAuthoredChannel"/> and not <see cref="ChannelUnits.For"/>.</param>
/// <param name="DeclaresMagnitude">True when <c>params</c> carries an <c>amount</c> — the number the
/// engine will apply, and therefore the number a template has to show.</param>
public readonly record struct DisplayFamilyFact(
    string FamilyId, string KindId, string Channel, bool DeclaresMagnitude);

/// <summary>One rule that fired, with the family or key it fired on.</summary>
public readonly record struct DisplayContentFinding(string RuleId, string Subject, string Detail)
{
    public AtomRejection AsRejection() => DisplayRules.Violated(RuleId, $"{Subject}: {Detail}");
}

/// <summary>
/// The four rules, as a pure function over already-parsed content — no file I/O, no database, and no
/// second parser. <c>gk-forge/tools/ItemSeedValidator</c>'s <c>DisplayCheck</c> is the first real consumer, and
/// the reason spec-item-card.md's "the mapping is decided, the enforcing check is not built" gap
/// closes here rather than in the tool: a rule implemented inside a build tool cannot be called by the
/// importer later.
/// </summary>
public static class DisplayContentRules
{
    /// <summary>The kinds that carry a channel at all. A <c>status.apply</c>/<c>board.action</c>/
    /// <c>resource.delta</c> family authors no channel and must never be reported as missing a unit
    /// for one — <see cref="DisplayRules.MissingUnitClass"/> is about "a channel with a reader",
    /// not about "an atom".</summary>
    static readonly IReadOnlySet<string> ChannelBearingKinds =
        new HashSet<string>(StringComparer.Ordinal) { "stat.modify", "stat.derived" };

    /// <summary>
    /// Every violation across one corpus, in a stable order (rule id, then subject) so a report can be
    /// diffed between runs.
    /// </summary>
    /// <param name="families">Every authored affix family.</param>
    /// <param name="templates">Every authored display-template row, keyed however the caller loaded
    /// them; only <see cref="DisplayTemplateRow.RuntimeFamily"/> and
    /// <see cref="DisplayTemplateRow.NameKey"/> are read.</param>
    /// <param name="stringKeys">Every key <c>gk-content/content/display/en.json</c> defines, or <c>null</c> when
    /// the caller could not load it — absence degrades to "skip the key rule", never to a false
    /// positive on every row, matching every other optional-registry precedent in this codebase.</param>
    public static IReadOnlyList<DisplayContentFinding> Check(
        IReadOnlyList<DisplayFamilyFact> families,
        IReadOnlyList<DisplayTemplateRow> templates,
        IReadOnlySet<string>? stringKeys = null,
        DerivedStatRegistry? registry = null)
    {
        if (families is null) throw new ArgumentNullException(nameof(families));
        if (templates is null) throw new ArgumentNullException(nameof(templates));

        var resolved = registry ?? DerivedStatRegistry.CreateDefault();
        var byFamily = new Dictionary<string, DisplayTemplateRow>(StringComparer.Ordinal);
        foreach (var t in templates) byFamily[t.RuntimeFamily] = t;

        var findings = new List<DisplayContentFinding>();

        foreach (var family in families)
        {
            // ---- MissingUnitClass -----------------------------------------------------------------
            if (ChannelBearingKinds.Contains(family.KindId) && family.Channel.Length > 0
                && ChannelUnits.ForAuthoredChannel(family.Channel, resolved) is null)
                findings.Add(new DisplayContentFinding(DisplayRules.MissingUnitClass, family.FamilyId,
                    $"kind '{family.KindId}' declares channel '{family.Channel}', which resolves to no "
                    + "unit class — the magnitude would render as a bare number with no way to tell it "
                    + "from a different currency"));

            // ---- MissingDisplayTemplate -----------------------------------------------------------
            if (!byFamily.TryGetValue(family.FamilyId, out var template))
            {
                findings.Add(new DisplayContentFinding(DisplayRules.MissingDisplayTemplate, family.FamilyId,
                    "no display-template row names this family — it would render as a raw id, and "
                    + "spec-item-card.md N1 makes that a rejection, never a blank"));
                continue;
            }

            // ---- UnrenderedMagnitude ---------------------------------------------------------------
            if (!family.DeclaresMagnitude) continue;

            var shows = ShowsAMagnitude(template.Template)
                        || (template.PlantOverrideTemplate is { } plant && ShowsAMagnitude(plant));
            if (!shows)
                findings.Add(new DisplayContentFinding(DisplayRules.UnrenderedMagnitude, family.FamilyId,
                    $"the family declares an amount but its template (\"{template.Template}\") shows no "
                    + "'{value}' — a number the engine applies and the player never sees"));
        }

        // ---- MissingDisplayKey ---------------------------------------------------------------------
        if (stringKeys is not null)
            foreach (var t in templates)
                if (!stringKeys.Contains(t.NameKey))
                    findings.Add(new DisplayContentFinding(DisplayRules.MissingDisplayKey, t.RuntimeFamily,
                        $"template row names string key '{t.NameKey}', which content/display/en.json "
                        + "does not define — the row exists, the string does not"));

        return findings
            .OrderBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.Subject, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The magnitude placeholder, matched through <see cref="DisplayTemplates.PlaceholdersOf"/>
    /// rather than by a second substring search, so the rule and the renderer agree on what a
    /// placeholder is.</summary>
    public const string MagnitudePlaceholder = "value";

    static bool ShowsAMagnitude(string template) =>
        DisplayTemplates.PlaceholdersOf(template).Contains(MagnitudePlaceholder, StringComparer.Ordinal);
}
