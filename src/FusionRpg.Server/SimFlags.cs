using FusionRpg.Contracts;

namespace FusionRpg.Server;

public static class SimFlags
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(RpgConstants.SimEnvVar), "1", StringComparison.Ordinal);
}
