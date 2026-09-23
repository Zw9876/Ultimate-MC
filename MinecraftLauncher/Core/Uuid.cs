using System;
using System.Security.Cryptography;
using System.Text;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// RFC 4122 version-3 (MD5, name-based) UUIDs — the scheme Minecraft itself
    /// uses for offline players.
    /// </summary>
    public static class Uuid
    {
        public static string NameBased(string name)
        {
            byte[] h = MD5.HashData(Encoding.UTF8.GetBytes(name));
            h[6] = (byte)((h[6] & 0x0F) | 0x30);   // version 3
            h[8] = (byte)((h[8] & 0x3F) | 0x80);   // RFC 4122 variant
            return Format(h);
        }

        /// <summary>
        /// The UUID an offline-mode server derives for a username. The server
        /// computes this itself from the name it is given, so anything that has to
        /// agree with the server (the skin server's lookup table) must use this.
        /// </summary>
        public static string OfflinePlayer(string username) =>
            NameBased($"OfflinePlayer:{username}");

        public static string Format(byte[] hash16)
        {
            string hex = Convert.ToHexString(hash16).ToLowerInvariant();
            return $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-" +
                   $"{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
        }

        public static string Strip(string uuid) => uuid.Replace("-", "");

        public static bool IsValid(string? value) => Guid.TryParse(value, out _);
    }
}
