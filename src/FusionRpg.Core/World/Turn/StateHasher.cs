using System.Security.Cryptography;
using System.Text;

namespace FusionRpg.Core.World.Turn;

/// <summary>
/// The drift detector. A turn's hash is taken over the world's canonical text form, so two machines
/// that agree on the hash agree on every field of the world — and two that disagree can diff the
/// canonical text to find out where.
///
/// SHA-256 rather than a cheap non-cryptographic hash: this value is compared across machines,
/// runtimes, and months of goldens, and a stable algorithm with no collisions is worth far more
/// here than the microseconds a faster hash would save once per turn.
/// </summary>
public static class StateHasher
{
    public static string Hash(WorldState world) => HashText(WorldCanonical.Write(world));

    public static string HashText(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
