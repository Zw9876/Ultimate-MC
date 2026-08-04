using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Produces a stable offline UUID for this machine, derived from the
    /// computer name via MD5 (matching the original launcher's scheme), and
    /// cached to computer_uuid.dat so it never changes between runs.
    /// </summary>
    public static class ComputerId
    {
        private static string UuidFile => Path.Combine(Paths.BaseDir, "computer_uuid.dat");

        public static string Get()
        {
            if (File.Exists(UuidFile))
            {
                var cached = File.ReadAllText(UuidFile).Trim();
                if (!string.IsNullOrWhiteSpace(cached)) return cached;
            }

            string uuid;
            try
            {
                string name = Environment.MachineName;
                byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(name));
                string hex = Convert.ToHexString(hash).ToLowerInvariant(); // 32 chars
                uuid = $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
            }
            catch
            {
                uuid = Guid.NewGuid().ToString();
            }

            try { File.WriteAllText(UuidFile, uuid, new UTF8Encoding(false)); } catch { }
            return uuid;
        }

        public static void Reset()
        {
            try { if (File.Exists(UuidFile)) File.Delete(UuidFile); } catch { }
        }
    }
}
