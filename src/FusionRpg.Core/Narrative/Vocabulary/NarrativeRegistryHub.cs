namespace FusionRpg.Core.Narrative.Vocabulary;

/// <summary>
/// `spec-narrative-vocabulary.md` §1 and §5: the one loader for the shared narrative registries. It reads
/// one file per vocabulary out of one directory — the `DungeonRegistryHub`/`DungeonRegistries` shape, and
/// the reason `narrative-seed`'s seedsmith adapter and this runtime can never drift: both read the same
/// committed files.
///
/// <para><b>Trigger set (DESIGN-GATE §2.16).</b> These are static content caches: their full trigger set is
/// <i>process start</i> — the Server host and this program's test fixtures — and nothing else, because a
/// registry changes only by a new build, which restarts the host. No runtime path mutates a catalog;
/// <c>Configure</c> is the only writer, and a test proves it by reflection.</para>
///
/// <para>A missing file, a missing required key, or a rejected row throws
/// <see cref="NarrativeVocabularyRejection"/> naming the file and the JSON path — never a default (§5's
/// "reject, never default").</para>
/// </summary>
public static class NarrativeRegistryHub
{
    public const string HostKindsFile = "host-kinds.v1.json";
    public const string ChoiceKindsFile = "choice-kinds.v1.json";
    public const string ConsequenceKindsFile = "consequence-kinds.v1.json";
    public const string ConditionsFile = "conditions.v1.json";
    public const string RoleTagsFile = "role-tags.v1.json";
    public const string TeachesFile = "teaches.v1.json";
    public const string RolesFile = "roles.v1.json";
    public const string LineContextsFile = "line-contexts.v1.json";

    /// <summary>The seed side authors this vocabulary as `voices.v1.json` (its Project structure), while
    /// this program's spec §1 table calls it `voice-registers.v1.json`; the file the seed writes wins and
    /// the discrepancy is filed as NR-F2. The JSON key is `voices` either way.</summary>
    public const string VoicesFile = "voices.v1.json";

    public const string DoctrinesFile = "doctrines.v1.json";

    /// <summary>Reads every registry file in §1 — the storylet-side half and the character-side half — and
    /// hands each to its catalog. The character-side files (`roles.v1.json`, `voices.v1.json`,
    /// `line-contexts.v1.json`, `doctrines.v1.json`) landed with `CharacterRegistries`/`DoctrineCatalog`.</summary>
    public static void Configure(string registryDir)
    {
        if (string.IsNullOrWhiteSpace(registryDir))
            throw new NarrativeVocabularyRejection("narrative registry directory is empty.");

        HostKindCatalog.Configure(HostKindCatalog.Parse(Read(registryDir, HostKindsFile)));
        ChoiceKindCatalog.Configure(ChoiceKindCatalog.Parse(Read(registryDir, ChoiceKindsFile)));
        ConsequenceKindCatalog.Configure(ConsequenceKindCatalog.Parse(Read(registryDir, ConsequenceKindsFile)));

        var (conditions, proposedLeaves) = ConditionCatalog.ParseDocument(Read(registryDir, ConditionsFile));
        ConditionCatalog.Configure(conditions, proposedLeaves);

        var (roleKinds, families) = RoleTagCatalog.Parse(Read(registryDir, RoleTagsFile));
        RoleTagCatalog.Configure(roleKinds, families);

        TeachesCatalog.Configure(TeachesCatalog.Parse(Read(registryDir, TeachesFile)));

        var (roles, leads) = NarrativeRoleCatalog.Parse(Read(registryDir, RolesFile));
        NarrativeRoleCatalog.Configure(roles, leads);
        LineContextCatalog.Configure(LineContextCatalog.Parse(Read(registryDir, LineContextsFile)));
        VoiceRegisterCatalog.Configure(VoiceRegisterCatalog.Parse(Read(registryDir, VoicesFile)));
        DoctrineCatalog.Configure(DoctrineCatalog.Parse(Read(registryDir, DoctrinesFile)));
    }

    static string Read(string registryDir, string file)
    {
        var path = Path.Combine(registryDir, file);
        if (!File.Exists(path))
            throw new NarrativeVocabularyRejection($"{file}: registry file not found at '{path}'.");
        return File.ReadAllText(path);
    }
}
