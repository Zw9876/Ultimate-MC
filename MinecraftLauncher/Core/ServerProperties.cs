using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Reads and updates a server's <c>server.properties</c> without destroying it.
    /// </summary>
    /// <remarks>
    /// This used to be rewritten from scratch on every start, which silently threw
    /// away anything the user had changed by hand. The worst case was
    /// <c>level-name</c>: dropping it sent the server back to the default "world" and
    /// generated a fresh one, so an existing world looked like it had vanished even
    /// though the folder was still sitting there.
    ///
    /// Now only the keys the launcher actually owns are written on every start.
    /// Everything else — including comments, ordering and keys we have never heard
    /// of — is preserved exactly as found.
    /// </remarks>
    public static class ServerProperties
    {
        /// <summary>
        /// Written on every start, because the Server tab is the authority on them.
        /// </summary>
        private static readonly string[] LauncherOwned =
        {
            "server-port", "gamemode", "difficulty", "hardcore",
            "max-players", "pvp", "online-mode", "sync-chunk-writes",
            "view-distance", "simulation-distance",
            "entity-broadcast-range-percentage", "network-compression-threshold"
        };

        /// <summary>
        /// Written once when the file is first created, then left alone so anyone can
        /// tune them. <c>level-name</c> is deliberately absent: leaving it unset means
        /// the server picks its own default, and a value someone has set is preserved
        /// by the same rule as any other untouched key.
        /// </summary>
        private static readonly (string Key, string Value)[] FirstRunDefaults =
        {
            ("white-list", "false"),
            ("enable-command-block", "true"),
            ("spawn-protection", "0"),
        };

        public static string Write(string serverDir, string version, ServerSettings s)
        {
            string path = Path.Combine(serverDir, "server.properties");
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string>();

            bool isNew = lines.Count == 0;
            if (isNew)
            {
                lines.Add("#Minecraft server properties");
                lines.Add($"motd=Minecraft Portable Server - {version}");
            }

            bool hardcore = s.Gamemode.Equals("hardcore", StringComparison.OrdinalIgnoreCase);

            // Hardcore is its own flag rather than a gamemode; the game mode underneath
            // it is survival.
            Set(lines, "gamemode", hardcore ? "survival" : s.Gamemode.ToLowerInvariant());
            Set(lines, "hardcore", hardcore ? "true" : "false");
            Set(lines, "server-port", s.Port.ToString());
            Set(lines, "difficulty", s.Difficulty.ToLowerInvariant());
            Set(lines, "max-players", s.MaxPlayers.ToString());
            Set(lines, "pvp", s.Pvp ? "true" : "false");

            // Not negotiable: these machines have no internet and no Mojang accounts,
            // so online-mode must stay off or nobody can join at all.
            Set(lines, "online-mode", "false");

            // Also forced. With this on, the server thread waits for every chunk write
            // to physically commit, and on a mechanical drive that is an 8-12 ms seek
            // in the middle of a tick — however fast the drive is at throughput, the
            // latency does not change. It is the difference between "the server is
            // busy" and "the server stopped for a moment".
            //
            // The trade is that a hard power cut can leave a recently written chunk
            // corrupt. Worth it here: worlds are started fresh, nothing is backed up,
            // and the launcher's Stop saves the world before exiting anyway.
            Set(lines, "sync-chunk-writes", "false");

            // Owned rather than seeded, deliberately. As first-run defaults these only
            // reached brand-new server folders, so an existing server kept whatever it
            // was created with and none of the tuning reached the servers that were
            // actually being played on.
            Set(lines, "view-distance", s.ViewDistance.ToString());
            Set(lines, "simulation-distance", s.SimulationDistance.ToString());

            // Entity updates are sent per player, so this is the setting that scales
            // worst with a crowd: at twenty players the server is doing twenty times
            // the tracking work. Trimming the range is barely visible in game.
            Set(lines, "entity-broadcast-range-percentage", s.EntityBroadcastPercent.ToString());

            // Compression off. It exists to save bandwidth over the internet, and this
            // launcher only ever serves a LAN, where there is bandwidth to spare and the
            // CPU spent compressing every packet for every player is the scarce thing.
            Set(lines, "network-compression-threshold", "-1");

            foreach (var (key, value) in FirstRunDefaults)
                if (IndexOf(lines, key) < 0)
                    lines.Add($"{key}={value}");

            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return path;
        }

        /// <summary>Reads one value, or null when the key is absent.</summary>
        public static string? Read(string serverDir, string key)
        {
            string path = Path.Combine(serverDir, "server.properties");
            if (!File.Exists(path)) return null;

            var lines = File.ReadAllLines(path).ToList();
            int at = IndexOf(lines, key);
            return at < 0 ? null : lines[at][(lines[at].IndexOf('=') + 1)..].Trim();
        }

        /// <summary>Every key/value pair in the file, ignoring comments.</summary>
        public static Dictionary<string, string> ReadAll(string serverDir)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(serverDir, "server.properties");
            if (!File.Exists(path)) return result;

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                result[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
            }
            return result;
        }

        /// <summary>Replaces a key in place, keeping its position, or appends it.</summary>
        private static void Set(List<string> lines, string key, string value)
        {
            int at = IndexOf(lines, key);
            if (at >= 0) lines[at] = $"{key}={value}";
            else lines.Add($"{key}={value}");
        }

        private static int IndexOf(List<string> lines, string key)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                if (trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }
    }
}
