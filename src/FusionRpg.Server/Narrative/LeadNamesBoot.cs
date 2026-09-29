namespace FusionRpg.Server.Narrative;

/// <summary>
/// identity-rename T2: the boot read of the authored lead-names registry
/// (<c>gk-data/packs/fusion/data/seed/narrative/_registry/names.en.v1.json</c>). The host owns the path — Core never
/// touches one — and this is the one place that turns the file into the process-wide
/// <see cref="FusionRpg.Core.Narrative.LeadNamesHub"/>.
///
/// <para>A missing file throws a <see cref="FileNotFoundException"/> whose message and
/// <see cref="FileNotFoundException.FileName"/> both name the path. A silent default name is not an
/// option: the same hub feeds player-facing text and a new world's persisted faction label, so a
/// guessed string is an identity no later reader could tell from an authored one.</para>
/// </summary>
public static class LeadNamesBoot
{
    /// <summary>Reads, parses and configures. Throws — naming <paramref name="path"/> — when the file
    /// is not there, and never configures a fallback.</summary>
    /// <remarks>
    /// The read/parse/configure step itself now lives in
    /// <see cref="FusionRpg.Core.Narrative.LeadNamesHub.ConfigureFromFile"/>, because the seed importer
    /// needs the identical step and a second copy here would drift — it did drift into a hole: the
    /// importer never configured the hub at all, so a cold import against an empty data dir reached
    /// <c>RpgStore</c>'s reader and rolled the whole import back. What stays here is what only the
    /// server knows: that the registry is its own published content, beside the binary, by the Content
    /// rule in FusionRpg.Server.csproj.
    /// </remarks>
    public static void Configure(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"lead-names boot: the names registry is not beside the server at '{path}'. A published "
                + "server ships it through the <Content> rule in FusionRpg.Server.csproj "
                + "(data/seed/narrative/_registry/names.en.v1.json); there is no default lead name.",
                path);
        }

        FusionRpg.Core.Narrative.LeadNamesHub.ConfigureFromFile(path);
    }
}
