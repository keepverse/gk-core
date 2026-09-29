using System.Runtime.CompilerServices;

// T8.4/T8.5 (ds 18, fusion-recipe-runtime): FusionRpg.Data.Tests and FusionRpg.E2E.Tests own
// bootstraps call CreatureRecipeCatalog.BuildDeterministicOnly() (internal since T8.5's own "public
// surface is gone" success criterion), matching CreatureSpeciesCatalog.ConfigureFromCompiledDefault()'s
// own hermetic-test shape rather than reading the real committed seed file (that would need
// CreatureSpeciesCatalog configured from the real ~829-species corpus too, not the small compiled
// default these two assemblies' other tests already depend on — a far larger, unrelated change).
//
// Deliberately a C# attribute here, NOT an <InternalsVisibleTo> item in FusionRpg.Core.csproj:
// MatchDataBanGuardTests.FusionRpg_Core_csproj_has_no_Data_ProjectReference substring-scans that one
// file's raw text for "FusionRpg.Data" (case-insensitive) to keep Core layered above the data
// project, and cannot tell a test-only InternalsVisibleTo grant from a real dependency — found and
// reverted live this session after granting FusionRpg.Data.Tests access there broke that guard.
[assembly: InternalsVisibleTo("FusionRpg.Data.Tests")]
[assembly: InternalsVisibleTo("FusionRpg.E2E.Tests")]
