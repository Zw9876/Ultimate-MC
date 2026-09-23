using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// A stable per-machine UUID, cached to computer_uuid.dat. Identity is tied to
    /// the machine rather than the username so that renaming yourself keeps your
    /// local player data intact.
    /// </summary>
    /// <remarks>
    /// This is not an anti-impersonation mechanism. Offline-mode servers derive a
    /// joining player's UUID from the username they send and ignore the client's,
    /// so what is passed here only affects this machine's local identity.
    /// </remarks>
    public static class ComputerId
    {
        private static string UuidFile => Path.Combine(Paths.BaseDir, "computer_uuid.dat");
        private static string? _cached;

        public static string Get()
        {
            if (_cached is not null) return _cached;

            if (File.Exists(UuidFile))
            {
                string existing = File.ReadAllText(UuidFile).Trim();
                if (Uuid.IsValid(existing)) return _cached = existing;
            }

            string id = Derive();
            try { File.WriteAllText(UuidFile, id, new UTF8Encoding(false)); } catch { }
            return _cached = id;
        }

        public static void Reset()
        {
            _cached = null;
            try { if (File.Exists(UuidFile)) File.Delete(UuidFile); } catch { }
        }

        // Seeded from Windows' per-installation MachineGuid, because computer names
        // collide readily on a LAN of default-named machines and a collision would
        // give two people the same player identity.
        private static string Derive()
        {
            string seed = MachineGuid() ?? Guid.NewGuid().ToString();
            return Uuid.NameBased($"MinecraftPortableLauncher:{seed}:{Environment.MachineName}");
        }

        private static string? MachineGuid()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography", writable: false);
                return key?.GetValue("MachineGuid") as string;
            }
            catch { return null; }
        }
    }
}
